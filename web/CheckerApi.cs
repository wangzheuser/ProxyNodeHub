using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ProxyNodeHub;

namespace ProxyNodeHub.Web;

public static class CheckerApi
{
    public static void MapCheckerApi(this WebApplication app)
    {
        var api = app.MapGroup("/api/checker").RequireAuthorization();
        api.MapPost("/stop", async (SubsCheckClient checker, CancellationToken ct) =>
        {
            var result = await checker.CallAsync("force-close", true, ct);
            return result is Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult ? result
                : Results.Accepted(value: new { message = "已请求停止当前检测，请等待状态更新；检测器服务继续运行。" });
        });
        api.MapGet("/version", (SubsCheckClient checker, CancellationToken ct) => checker.CallAsync("version", false, ct));
        api.MapGet("/logs", (SubsCheckClient checker, CancellationToken ct) => checker.CallAsync("logs", false, ct));
        api.MapGet("/config", (SubsCheckClient checker, CancellationToken ct) => checker.GetConfigAsync(ct));
        api.MapPut("/config", (CheckerSettingsRequest request, SubsCheckClient checker, CancellationToken ct) =>
            checker.SaveSettingsAsync(request, ct));
        api.MapPost("/sources", (CheckerSourcesRequest request, SubsCheckClient checker, StateStore store, CancellationToken ct) =>
        {
            var state = store.State;
            var repos = (request.Current ? state.Current?.Repositories ?? [] : [])
                .Concat(request.Favorites ? state.Favorites : []);
            var urls = repos.SelectMany(repo => repo.Links.Where(link => link.IsValid && link.IsAnalyzed && link.NodeCount > 0)
                .Select(link => new UriBuilder(link.Url) { Fragment = repo.FullName }.Uri.AbsoluteUri)).ToArray();
            return checker.AddSourcesAsync(request, urls, ct);
        });
        api.MapGet("/download", (string format, SubsCheckClient checker, CancellationToken ct) => checker.DownloadAsync(format, ct));
    }
}

public sealed record CheckerSettingsRequest(string Revision, Dictionary<string, JsonElement> Values);
public sealed record CheckerSourcesRequest(string Revision, bool Current, bool Favorites, string[] ManualUrls)
{
    public override string ToString() => "CheckerSourcesRequest { ManualUrls = [redacted] }";
}

public sealed class SubsCheckClient(ConnectionStore connections) : IDisposable
{
    private readonly HttpClient http = new(new SocketsHttpHandler { AllowAutoRedirect = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        { Timeout = TimeSpan.FromSeconds(30), MaxResponseContentBufferSize = 32 * 1024 * 1024 };
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly byte[] revisionKey = RandomNumberGenerator.GetBytes(32);
    private static readonly Regex SensitiveLog = new(@"(?i)\b(api[-_ ]?key|token|password|passwd|secret|authorization|credential|uuid|private[-_ ]?key)\b",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex LogUrl = new("""(?i)\b[a-z][a-z0-9+.-]*://[^\s"<>]+""",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public bool Configured => connections.GetChecker().Configured;
    public string? WebUrl => connections.GetChecker().WebUrl;

    public async Task<IResult> CallAsync(string endpoint, bool post, CancellationToken ct)
    {
        if (post ? endpoint is not ("trigger-check" or "force-close") : endpoint is not ("status" or "results" or "version" or "logs"))
            throw new ArgumentException("不支持的检测器操作。");
        var connection = connections.GetChecker();
        if (!connection.Configured) return Unconfigured();
        var result = await JsonAsync(connection, "api/" + endpoint, post, null, ct);
        if (endpoint == "logs")
        {
            if (!result.TryGetProperty("logs", out var logs) || logs.ValueKind != JsonValueKind.Array ||
                logs.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
                throw new InvalidDataException("检测器日志格式无法识别。");
            return Results.Ok(new { logs = logs.EnumerateArray().TakeLast(100)
                .Select(item => RedactLog(item.GetString()!, connection.ApiKey!)).ToArray() });
        }
        return Results.Json(result);
    }

    public async Task<IResult> GetConfigAsync(CancellationToken ct)
    {
        var connection = connections.GetChecker();
        if (!connection.Configured) return Unconfigured();
        var yaml = await ReadConfigAsync(connection, ct);
        return Results.Ok(new CheckerConfig(yaml).Project(Revision(connection, yaml)));
    }

    public Task<IResult> SaveSettingsAsync(CheckerSettingsRequest request, CancellationToken ct)
    {
        if (request.Values is null || request.Values.Count > CheckerConfig.Fields.Count)
            throw new ArgumentException("参数列表无效。");
        return UpdateAsync(request.Revision, config => config.Apply(request.Values), ct);
    }

    public Task<IResult> AddSourcesAsync(CheckerSourcesRequest request, string[] knownUrls, CancellationToken ct)
    {
        if (request.ManualUrls is null || request.ManualUrls.Length > 1000)
            throw new ArgumentException("手动订阅最多1000条。");
        var urls = knownUrls.Concat(request.ManualUrls).Select(value =>
        {
            if (value is null || value.Length > 4096 || value.Any(char.IsControl) ||
                !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0)
                throw new ArgumentException("订阅需为不含用户名和密码的 HTTP(S) URL，每条不超过4096字符。");
            return uri.AbsoluteUri;
        }).Distinct(StringComparer.Ordinal).ToArray();
        if (urls.Length == 0) throw new ArgumentException("所选范围没有有效订阅，请选择来源或填写订阅链接。");
        return UpdateAsync(request.Revision, config => config.AddSources(urls), ct);
    }

    private async Task<IResult> UpdateAsync(string revision, Func<CheckerConfig, string> change, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(revision)) throw new ArgumentException("请先读取检测器参数再保存。");
        if (!await writeGate.WaitAsync(0, ct)) return Conflict("另一项检测器配置操作正在进行，请稍后重新读取。");
        try
        {
            var connection = connections.GetChecker();
            if (!connection.Configured) return Unconfigured();
            var yaml = await ReadConfigAsync(connection, ct);
            if (Revision(connection, yaml) != revision) return Conflict("检测器连接或配置已变化，请重新读取后再修改。");
            if (await IsCheckingAsync(connection, ct)) return Conflict("检测正在运行，请结束后再修改参数或来源。");
            var next = change(new CheckerConfig(yaml));
            // The upstream API has no CAS. Recheck immediately before writing;
            // concurrent edits in another admin remain an upstream limitation.
            var current = await ReadConfigAsync(connection, ct);
            if (connections.GetChecker() != connection || current != yaml)
                return Conflict("检测器连接或配置已变化，未写入。请重新读取。");
            if (await IsCheckingAsync(connection, ct)) return Conflict("检测器已开始运行，未写入参数。");
            _ = await JsonAsync(connection, "api/config", true, new { content = next }, ct);
            // A successful POST only confirms persistence, not hot-reload success.
            var saved = await ReadConfigAsync(connection, ct);
            if (saved != next) return Conflict("保存后配置与请求不一致，请重新读取并检查原管理台；未自动重试覆盖。");
            return Results.NoContent();
        }
        finally { writeGate.Release(); }
    }

    private async Task<bool> IsCheckingAsync(CheckerConnection connection, CancellationToken ct)
    {
        var status = await JsonAsync(connection, "api/status", false, null, ct);
        if (!status.TryGetProperty("checking", out var checking) || checking.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException("无法确认检测器是否空闲，未修改配置。");
        return checking.GetBoolean();
    }

    private async Task<string> ReadConfigAsync(CheckerConnection connection, CancellationToken ct)
    {
        var result = await JsonAsync(connection, "api/config", false, null, ct);
        if (!result.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("检测器配置响应格式无法识别。");
        return content.GetString()!;
    }

    private string Revision(CheckerConnection connection, string yaml) => Convert.ToHexString(HMACSHA256.HashData(revisionKey,
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { connection.ApiUrl, connection.ApiKey, yaml }))));

    public async Task<IResult> DownloadAsync(string format, CancellationToken ct)
    {
        var file = format switch { "clash" => "all.yaml", "mihomo" => "mihomo.yaml", "base64" => "base64.txt",
            _ => throw new ArgumentException("仅支持 clash、mihomo 或 base64 检测产物。") };
        var connection = connections.GetChecker();
        if (!connection.Configured) return Unconfigured();
        // The authenticated Web route gates this download; public file routes on
        // the detector do not need, and must not receive, the admin API key.
        using var response = await SendAsync(connection, "sub/" + file, false, null, false, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return Results.Problem(statusCode: 404, title: "检测器尚未生成此产物；请先完成检测并检查对应格式配置。");
        EnsureSuccess(response);
        return Results.File(await response.Content.ReadAsByteArrayAsync(ct),
            "text/plain; charset=utf-8", file);
    }

    private async Task<JsonElement> JsonAsync(CheckerConnection connection, string path, bool post, object? body, CancellationToken ct)
    {
        using var response = await SendAsync(connection, path, post, body, true, ct);
        EnsureSuccess(response);
        try
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return json.RootElement.Clone();
        }
        catch (JsonException) { throw new InvalidDataException("检测器返回了无法识别的 JSON。"); }
    }

    private async Task<HttpResponseMessage> SendAsync(CheckerConnection connection, string path, bool post, object? body,
        bool authenticated, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(post ? HttpMethod.Post : HttpMethod.Get,
            new Uri(new Uri(connection.ApiUrl!.TrimEnd('/') + "/"), path));
        if (authenticated) request.Headers.Add("X-API-Key", connection.ApiKey);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await http.SendAsync(request, ct);
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"检测器请求失败（HTTP {(int)response.StatusCode}）。", null, response.StatusCode);
    }

    public static string RedactLog(string line, string key)
    {
        if (line.Length > 8192) return "[过长日志已隐藏]";
        if (SensitiveLog.IsMatch(line)) return "[含敏感字段的日志已隐藏]";
        var safe = string.IsNullOrEmpty(key) ? line : line.Replace(key, "[密钥已隐藏]", StringComparison.Ordinal);
        return LogUrl.Replace(safe, "[地址已隐藏]");
    }

    private static IResult Conflict(string message) => Results.Problem(statusCode: 409, title: message);
    private static IResult Unconfigured() => Results.Problem(statusCode: 503, title: "尚未配置检测器 API 地址和密钥，请在服务设置中配置。");
    public void Dispose() { http.Dispose(); writeGate.Dispose(); }
}
