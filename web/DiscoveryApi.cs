using System.Text;
using System.Text.Json;
using ProxyNodeHub;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("ProxyNodeHub.Tests")]

namespace ProxyNodeHub.Web;

public static class DiscoveryApi
{
    private static readonly SemaphoreSlim MirrorTestGate = new(1, 1);

    public static void MapDiscoveryApi(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
        api.MapPost("/explore", (DiscoveryWorker worker) => QueueResult(worker.RequestExplore()));
        api.MapPost("/recheck", (RepositorySelection request, DiscoveryWorker worker) =>
            QueueResult(worker.RequestRecheck(request.FullNames)));
        api.MapPut("/favorites/batch", (FavoriteSelection request, StateStore store) =>
        {
            store.SetFavorites(request.FullNames, request.Favorite);
            return Results.NoContent();
        });
        api.MapPost("/export", async (ExportSelection request, StateStore store, SubscriptionExporter exporter, HttpContext context) =>
        {
            var state = store.State;
            var repositories = StateStore.SelectRepositories(state, request.FullNames);
            if (request.Format == "json") return Results.File(JsonSerializer.SerializeToUtf8Bytes(repositories,
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { IncludeFields = true }), "application/json", "repositories.json");
            if (request.Format is not ("links" or "plain" or "base64")) throw new ArgumentException("不支持此导出格式。");
            var urls = SubscriptionUrls(repositories);
            if (urls.Length == 0) return Results.Problem(statusCode: 422, title: "选中仓库没有可导出的有效订阅。");
            return request.Format == "links"
                ? Results.File(Encoding.UTF8.GetBytes(string.Join('\n', urls) + "\n"), "text/plain; charset=utf-8", "subscriptions.txt")
                : await exporter.ExportAsync(urls, request.Format, context.RequestAborted, state.Settings.DownloadMirror);
        });
        api.MapGet("/search-history", (StateStore store) => Results.Ok(store.GetSearchHistory()));
        api.MapDelete("/search-history", (StateStore store) => { store.ClearSearchHistory(); return Results.NoContent(); });
        api.MapGet("/logs", (DateTimeOffset? startedAt, DiscoveryWorker worker) => Results.Ok(worker.GetLogs(startedAt)));
        api.MapGet("/logs/download", (DateTimeOffset? startedAt, DiscoveryWorker worker) =>
            Results.File(Encoding.UTF8.GetBytes(string.Join('\n', worker.GetLogs(startedAt)
                .Select(l => $"[{l.At:O}] {l.Message}")) + "\n"), "text/plain; charset=utf-8", "discovery.log.txt"));
        api.MapDelete("/logs", (DiscoveryWorker worker) => { worker.ClearLogs(); return Results.NoContent(); });
        api.MapGet("/mirrors", () => Results.Ok(GitHubService.PublicMirrors.Select(m => new { m.Name, m.Prefix, m.IsDefault })));
        api.MapPost("/mirrors/test", async Task<IResult> (CancellationToken ct) =>
        {
            if (!await MirrorTestGate.WaitAsync(0, ct)) return Results.Problem(statusCode: 409, title: "镜像测试正在运行。");
            try
            {
                var results = await GitHubService.TestPublicMirrorsAsync(ct);
                return Results.Ok(results.Select(m => new { m.Name, m.LatencyMs }));
            }
            finally { MirrorTestGate.Release(); }
        });
    }

    public static string[] SubscriptionUrls(IEnumerable<RepoInfo> repositories) => repositories.SelectMany(r => r.Links)
        .Where(l => l.IsAnalyzed && l.IsValid && l.NodeCount > 0 && GitHubService.IsAllowedSubscription(l.Url))
        .Select(l => l.Url).Distinct(StringComparer.Ordinal).ToArray();

    private static IResult QueueResult(bool accepted) => accepted
        ? Results.Accepted(value: new { message = "发现任务已排队。" })
        : Results.Problem(statusCode: 409, title: "发现任务已在运行或等待运行。");

    private sealed record RepositorySelection(string[] FullNames);
    private sealed record FavoriteSelection(string[] FullNames, bool Favorite);
    private sealed record ExportSelection(string Format, string[] FullNames);
}
