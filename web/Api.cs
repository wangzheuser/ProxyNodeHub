using System.Text;
using System.Text.Json;
using ProxyNodeHub;

namespace ProxyNodeHub.Web;

public static class Api
{
    public static void MapProxyNodeApi(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
        api.MapGet("/state", (StateStore store, DiscoveryWorker worker, SubsCheckClient checker, ConnectionStore connections) =>
        {
            var state = store.State;
            return Results.Ok(new
            {
                settings = state.Settings, tokenConfigured = connections.GetGitHubToken() is not null,
                generatedAt = state.Current?.GeneratedAt, nextRunAt = worker.NextRunAt, attempt = worker.Attempt,
                repositories = state.Current?.Repositories ?? [], favorites = state.Favorites,
                history = state.History.Select(h => new { h.StartedAt, h.FinishedAt, h.Success, h.Subscriptions, h.Error }),
                logs = worker.Logs, feedPath = "/subscriptions.txt",
                failures = new { searches = state.Current?.FailedSearches ?? 0,
                    repositories = state.Current?.FailedRepositories ?? 0, downloads = state.Current?.FailedDownloads ?? 0 },
                checker = new { configured = checker.Configured, webUrl = checker.WebUrl }
            });
        });
        api.MapGet("/connections", (ConnectionStore connections) => Results.Ok(connections.GetPublicState()));
        api.MapPut("/connections/github", (GitHubConnectionRequest request, ConnectionStore connections) =>
        { connections.SetGitHub(request.Mode, request.Token); return Results.NoContent(); });
        api.MapPut("/connections/checker", (CheckerConnectionRequest request, ConnectionStore connections) =>
        { connections.SetChecker(request.Mode, request.ApiUrl, request.ApiKey, request.WebUrl); return Results.NoContent(); });
        api.MapPost("/refresh", (DiscoveryWorker worker) => worker.RequestRun()
            ? Results.Accepted(value: new { message = "发现任务已排队。" }) : Results.Problem(statusCode: 409, title: "发现任务已在运行或等待运行。"));
        api.MapPost("/cancel", (DiscoveryWorker worker) => { worker.CancelRun(); return Results.Accepted(value: new { message = "已请求取消。" }); });
        api.MapPut("/settings", (DiscoverySettings settings, StateStore store, DiscoveryWorker worker) =>
        { store.SaveSettings(settings); worker.SettingsChanged(); return Results.NoContent(); });
        api.MapPut("/favorites", (FavoriteRequest request, StateStore store) =>
        { store.SetFavorite(request.FullName, request.Favorite); return Results.NoContent(); });
        api.MapGet("/features", (FeatureLibrary features) => Results.Ok(features.GetReliableRepos()));
        api.MapGet("/export", async (string? format, bool? favorites, StateStore store,
            SubscriptionExporter exporter, HttpContext context) =>
        {
            var state = store.State;
            var repos = favorites == true ? state.Favorites : state.Current?.Repositories ?? [];
            var urls = repos.SelectMany(r => r.Links).Where(l => l.IsValid && l.IsAnalyzed && l.NodeCount > 0)
                .Select(l => l.Url).Distinct(StringComparer.Ordinal).ToArray();
            if (urls.Length == 0) return Results.Problem(statusCode: 422, title: "当前范围没有可导出的订阅。");
            if (format == "json") return Results.File(JsonSerializer.SerializeToUtf8Bytes(repos,
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { IncludeFields = true }), "application/json", "repositories.json");
            if (format is null or "links") return Results.File(Encoding.UTF8.GetBytes(string.Join('\n', urls) + "\n"),
                "text/plain; charset=utf-8", "subscriptions.txt");
            if (format is not ("plain" or "base64")) throw new ArgumentException("不支持此导出格式。");
            return await exporter.ExportAsync(urls, format, context.RequestAborted, state.Settings.DownloadMirror);
        });
        api.MapGet("/checker/status", (SubsCheckClient checker, CancellationToken ct) => checker.CallAsync("status", false, ct));
        api.MapGet("/checker/results", (SubsCheckClient checker, CancellationToken ct) => checker.CallAsync("results", false, ct));
        api.MapPost("/checker/run", async (SubsCheckClient checker, CancellationToken ct) =>
        {
            var result = await checker.CallAsync("trigger-check", true, ct);
            return result is IStatusCodeHttpResult { StatusCode: >= 400 } ? result
                : Results.Accepted(value: new { message = "已提交检测请求，请读取状态确认实际进展。" });
        });
    }
    private sealed record FavoriteRequest(string FullName, bool Favorite);
    private sealed class GitHubConnectionRequest
    {
        public string Mode { get; init; } = "";
        public string? Token { get; init; }
    }
    private sealed class CheckerConnectionRequest
    {
        public string Mode { get; init; } = "";
        public string? ApiUrl { get; init; }
        public string? ApiKey { get; init; }
        public string? WebUrl { get; init; }
    }
}

public sealed class SubscriptionExporter : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public async Task<IResult> ExportAsync(string[] urls, string format, CancellationToken ct, string mirror = "")
    {
        if (!await gate.WaitAsync(0, ct)) return Results.Problem(statusCode: 409, title: "另一个合并导出正在运行，请稍后重试。");
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromMinutes(2));
            using var github = new GitHubService(restrictPublicSources: true);
            await github.ConfigurePublicMirrorAsync(mirror, deadline.Token);
            var contents = new List<string>();
            var bytes = 0;
            foreach (var url in urls)
            {
                var content = await github.GetUrlAsync(url, deadline.Token)
                    ?? throw new HttpRequestException("Subscription download failed.");
                bytes += Encoding.UTF8.GetByteCount(content);
                if (bytes > 16 * 1024 * 1024) throw new InvalidDataException("合并内容超过16 MiB，请缩小收藏范围或使用链接导出。");
                if (NodeParser.SplitNodeLines(content).Count == 0)
                    throw new InvalidDataException("包含非 URI/Base64 订阅；请导出链接交给 subs-check，未输出不完整的合并结果。");
                contents.Add(content);
            }
            var merged = NodeParser.MergeNodes(contents);
            return Results.File(Encoding.UTF8.GetBytes(format == "base64" ? merged.base64 : merged.plain),
                "text/plain; charset=utf-8", format == "base64" ? "nodes.base64.txt" : "nodes.txt");
        }
        finally { gate.Release(); }
    }
    public void Dispose() => gate.Dispose();
}
