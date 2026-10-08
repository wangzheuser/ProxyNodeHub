using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ProxyNodeHub;

// ── 加速代理信息 ──
public class ProxyMirror
{
    public string Name = "";
    public string Prefix = "";       // 代理前缀 (raw.githubusercontent.com/owner/repo/... → {prefix}https://raw.githubusercontent.com/owner/repo/...)
    public string Type = "";         // "直连" / "代理" / "CDN"
    public bool IsJsDelivr;          // jsDelivr 需要路径转换
    public long LatencyMs = -1;      // -1 = 未测速, -2 = 连接超时/失败
    public bool IsDefault;           // 是否为默认(直连)
}

public class GitHubService : IDisposable
{
    private readonly HttpClient _httpApi;
    private readonly HttpClient _httpRaw;
    private readonly string? _token;
    private readonly bool restrictPublicSources;
    private int failedDownloads;
    public int FailedDownloads => Volatile.Read(ref failedDownloads);

    private const string ApiBase = "https://api.github.com";
    private const string RawBaseLen = "https://raw.githubusercontent.com/";

    /// <summary>
    /// 统一 UA。历史上有两处硬编码 "4.9"/"4.5" 各写各的，导致不一致；
    /// 收口到这里，与 csproj 的版本号一致。
    /// </summary>
    private const string UserAgent = "ProxyNodeHub/0.0.1";

    public bool HasToken => !string.IsNullOrEmpty(_token);

    // 当前选中的代理 (null = 直连)
    public ProxyMirror? CurrentProxy { get; set; }

    // 所有可用代理列表
    public static readonly List<ProxyMirror> AllProxies = new()
    {
        new ProxyMirror { Name = "直连 (无代理)", Prefix = "", Type = "直连", IsDefault = true },
        new ProxyMirror { Name = "ghfast.top", Prefix = "https://ghfast.top/", Type = "代理" },
        new ProxyMirror { Name = "gh-proxy.com", Prefix = "https://gh-proxy.com/", Type = "代理" },
        new ProxyMirror { Name = "ghproxy.net", Prefix = "https://ghproxy.net/", Type = "代理" },
        new ProxyMirror { Name = "git.yylx.win", Prefix = "https://git.yylx.win/", Type = "代理" },
        new ProxyMirror { Name = "cdn.akaere.online", Prefix = "https://cdn.akaere.online/", Type = "CDN" },
        new ProxyMirror { Name = "gh.jasonzeng.dev", Prefix = "https://gh.jasonzeng.dev/", Type = "代理" },
        new ProxyMirror { Name = "down.mxw.xx.kg", Prefix = "https://down.mxw.xx.kg/", Type = "代理" },
        new ProxyMirror { Name = "github.tbap.top", Prefix = "https://github.tbap.top/", Type = "代理" },
        new ProxyMirror { Name = "ghm.078465.xyz", Prefix = "https://ghm.078465.xyz/", Type = "代理" },
        new ProxyMirror { Name = "gh.monlor.com", Prefix = "https://gh.monlor.com/", Type = "代理" },
        new ProxyMirror { Name = "jsDelivr (cdn)", Prefix = "https://cdn.jsdelivr.net/", Type = "CDN", IsJsDelivr = true },
        new ProxyMirror { Name = "jsDelivr (fastly)", Prefix = "https://fastly.jsdelivr.net/", Type = "CDN", IsJsDelivr = true },
        new ProxyMirror { Name = "jsDelivr (testing)", Prefix = "https://testingcf.jsdelivr.net/", Type = "CDN", IsJsDelivr = true },
    };

    // Freeze the permitted destinations before desktop latency tests mutate their records.
    private static readonly (string Name, string Prefix, string Type, bool IsDefault, bool IsJsDelivr)[] PublicMirrorDefinitions =
        AllProxies.Select(p => (p.Name, p.Prefix, p.Type, p.IsDefault, p.IsJsDelivr)).ToArray();
    public static IReadOnlyList<ProxyMirror> PublicMirrors => PublicMirrorDefinitions.Select(p => new ProxyMirror
    { Name = p.Name, Prefix = p.Prefix, Type = p.Type, IsDefault = p.IsDefault, IsJsDelivr = p.IsJsDelivr }).ToArray();

    public async Task ConfigurePublicMirrorAsync(string name, CancellationToken ct = default)
    {
        if (name == "auto")
        {
            var results = await TestPublicMirrorsAsync(ct);
            CurrentProxy = results.FirstOrDefault(p => p.LatencyMs >= 0)
                ?? throw new HttpRequestException("直连和固定镜像均未通过测试。");
        }
        else CurrentProxy = name.Length == 0 ? null : PublicMirrors.FirstOrDefault(p => p.Name == name)
            ?? throw new ArgumentException("不支持该下载镜像。");
    }

    public static async Task<List<ProxyMirror>> TestPublicMirrorsAsync(CancellationToken ct = default,
        HttpMessageHandler? handler = null)
    {
        using var http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = 1024 * 1024 };
        http.DefaultRequestHeaders.Add("User-Agent", UserAgent);
        using var limit = new SemaphoreSlim(4, 4);
        const string sample = "https://raw.githubusercontent.com/Pawdroid/Free-servers/main/sub";
        var results = await Task.WhenAll(PublicMirrors.Select(async mirror =>
        {
            await limit.WaitAsync(ct);
            try
            {
                var timer = Stopwatch.StartNew();
                using var response = await http.GetAsync(mirror.IsDefault ? sample : BuildProxiedUrl(sample, mirror)!, ct);
                var content = response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(ct) : "";
                mirror.LatencyMs = NodeParser.CountNodes(content) > 0 ? timer.ElapsedMilliseconds : -2;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) { mirror.LatencyMs = -2; }
            finally { limit.Release(); }
            return mirror;
        }));
        return results.OrderBy(p => p.LatencyMs < 0 ? long.MaxValue : p.LatencyMs).ThenBy(p => !p.IsDefault).ToList();
    }

    public GitHubService(string? token = null, bool restrictPublicSources = false,
        HttpMessageHandler? apiHandler = null, HttpMessageHandler? rawHandler = null)
    {
        _token = token;
        this.restrictPublicSources = restrictPublicSources;
        _httpApi = CreateClient(apiHandler, false, 8);
        _httpRaw = CreateClient(rawHandler, !restrictPublicSources, 5);
        foreach (var h in new[] { _httpApi, _httpRaw })
        {
            h.DefaultRequestHeaders.Add("User-Agent", UserAgent);
            h.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");
        }
        if (!string.IsNullOrEmpty(token))
            _httpApi.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");
    }

    private static HttpClient CreateClient(HttpMessageHandler? handler, bool redirects, int timeout) =>
        new(handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = redirects,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        }) { Timeout = TimeSpan.FromSeconds(timeout), MaxResponseContentBufferSize = 8 * 1024 * 1024 };

    public void Dispose() { _httpApi.Dispose(); _httpRaw.Dispose(); }

    // ── REST: 搜索 ──
    public async Task<List<GitHubRepo>> SearchReposAsync(string query, int perPage, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
            var url = $"{ApiBase}/search/repositories?q={Uri.EscapeDataString(query)}&sort=updated&order=desc&per_page={perPage}";
            using var resp = await _httpApi.GetAsync(url, ct);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var items = doc.RootElement.GetProperty("items");
            return items.EnumerateArray()
                .Select(i => JsonSerializer.Deserialize(i.GetRawText(), CoreJsonContext.Default.GitHubRepo)
                    ?? throw new JsonException("Missing repository object."))
                .ToList();
    }

    // ── REST: 最近提交 ──
    public async Task<List<GitHubCommit>> GetRecentCommitsAsync(string fullName, int days = 7, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
            var since = DateTime.UtcNow.AddDays(-days).ToString("yyyy-MM-ddTHH:mm:ssZ");
            using var resp = await _httpApi.GetAsync($"{ApiBase}/repos/{fullName}/commits?since={since}&per_page=100", ct);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<List<GitHubCommit>>(json) ?? throw new JsonException("Missing commit array.");
    }

    // ── REST: 获取仓库文件树 ──
    public async Task<List<string>> GetFileTreeAsync(string fullName, CancellationToken ct = default, string branch = "main")
    {
        ct.ThrowIfCancellationRequested();
            using var resp = await _httpApi.GetAsync($"{ApiBase}/repos/{fullName}/git/trees/{Uri.EscapeDataString(branch)}?recursive=1", ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return new();
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var tree = doc.RootElement.GetProperty("tree");
            var files = new List<string>();
            foreach (var item in tree.EnumerateArray())
            {
                if (item.GetProperty("type").GetString() == "blob")
                {
                    var path = item.GetProperty("path").GetString();
                    if (!string.IsNullOrEmpty(path) && path.Length < 200)
                        files.Add(path);
                }
            }
            return files;
    }

    // ── REST: 获取 README.md 内容 ──
    public async Task<string?> GetReadmeAsync(string fullName, string branch = "main", CancellationToken ct = default)
    {
        return await GetRawFileAsync(fullName, "README.md", branch, ct);
    }

    // ── 代理测速 ──
    public static async Task<ProxyMirror> TestProxyLatencyAsync(ProxyMirror proxy, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.Add("User-Agent", UserAgent);

            string testUrl;
            if (proxy.IsDefault)
                testUrl = "https://raw.githubusercontent.com/Pawdroid/Free-servers/main/sub";
            else if (proxy.IsJsDelivr)
                testUrl = proxy.Prefix + "gh/Pawdroid/Free-servers@main/sub";
            else
                testUrl = proxy.Prefix + "https://raw.githubusercontent.com/Pawdroid/Free-servers/main/sub";

            var resp = await http.GetAsync(testUrl, ct);
            if (resp.IsSuccessStatusCode)
            {
                await resp.Content.ReadAsStringAsync(ct);
                proxy.LatencyMs = sw.ElapsedMilliseconds;
            }
            else
            {
                proxy.LatencyMs = -2;  // HTTP 错误
            }
        }
        catch (OperationCanceledException)
        {
            proxy.LatencyMs = -1;  // 取消
        }
        catch
        {
            proxy.LatencyMs = -2;  // 超时/连接失败
        }
        sw.Stop();
        return proxy;
    }

    // ── 批量测速所有代理 ──
    public static async Task<List<ProxyMirror>> TestAllProxiesAsync(CancellationToken ct = default)
    {
        var tasks = AllProxies.Select(p => TestProxyLatencyAsync(p, ct));
        await Task.WhenAll(tasks);
        // 按延迟排序: 可用的在前 (延迟升序), 超时的在后
        return AllProxies
            .OrderBy(p => p.LatencyMs < 0 ? int.MaxValue : p.LatencyMs)
            .ThenBy(p => p.IsDefault ? 0 : 1)
            .ToList();
    }

    // ── 下载 raw 文件 (使用当前选中的代理) ──
    public async Task<string?> GetUrlAsync(string url, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        // Desktop keeps its mirror selection; an unattended Web host only fetches
        // supported public sources and cannot follow redirects into the LAN.
        if (restrictPublicSources)
        {
            if (!IsAllowedSubscription(url))
                throw new InvalidDataException("Subscription URL is outside supported public sources.");
            if (CurrentProxy is { IsDefault: false })
            {
                // Resolve by fixed name, never trust a caller-supplied prefix or follow mirror redirects.
                var mirror = PublicMirrors.FirstOrDefault(p => p.Name == CurrentProxy.Name)
                    ?? throw new InvalidDataException("Unsupported public download mirror.");
                var proxied = BuildProxiedUrl(url, mirror);
                if (proxied is not null) return await TryGetAsync(proxied, ct);
            }
            return await TryGetAsync(url, ct);
        }
        // 1. 如果选了代理, 优先用代理
        if (CurrentProxy != null && !CurrentProxy.IsDefault)
        {
            var proxiedUrl = BuildProxiedUrl(url, CurrentProxy);
            if (proxiedUrl != null)
            {
                var content = await TryGetAsync(proxiedUrl, ct);
                if (content != null) return content;
            }
        }

        // 2. 直连
        var direct = await TryGetAsync(url, ct);
        if (direct != null) return direct;

        // 3. 直连失败 → 自动尝试所有代理 (按延迟排序)
        if (url.StartsWith(RawBaseLen, StringComparison.OrdinalIgnoreCase))
        {
            var sortedProxies = AllProxies
                .Where(p => !p.IsDefault && p.LatencyMs > 0)
                .OrderBy(p => p.LatencyMs);

            foreach (var proxy in sortedProxies)
            {
                var proxiedUrl = BuildProxiedUrl(url, proxy);
                if (proxiedUrl == null) continue;
                var content = await TryGetAsync(proxiedUrl, ct);
                if (content != null) return content;
            }
        }

        return null;
    }

    private static string? BuildProxiedUrl(string rawUrl, ProxyMirror proxy)
    {
        if (!rawUrl.StartsWith(RawBaseLen, StringComparison.OrdinalIgnoreCase))
            return null;

        if (proxy.IsJsDelivr)
        {
            // raw.githubusercontent.com/{owner}/{repo}/{branch}/{path} → {prefix}gh/{owner}/{repo}@{branch}/{path}
            var rest = rawUrl[RawBaseLen.Length..];
            var parts = rest.Split(new[] { '/' }, 4);
            if (parts.Length == 4)
                return $"{proxy.Prefix}gh/{parts[0]}/{parts[1]}@{parts[2]}/{parts[3]}";
            return null;
        }

        // 代理型: {prefix}{原始URL}
        return proxy.Prefix + rawUrl;
    }

    private async Task<string?> TryGetAsync(string url, CancellationToken ct)
    {
        try
        {
            using var resp = await _httpRaw.GetAsync(url, ct);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsStringAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;  // 真正的用户取消
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            Interlocked.Increment(ref failedDownloads);
            Trace.TraceWarning("Subscription download failed: {0}", ex.GetType().Name);
            return null;
        }
    }

    public static bool IsAllowedSubscription(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
        uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Query.Length == 0 &&
        (uri.Host == "raw.githubusercontent.com" || uri.Host == "nodes.udptoos.com");

    public static string RawUrl(string fullName, string path, string branch) =>
        $"https://raw.githubusercontent.com/{fullName}/{Uri.EscapeDataString(branch)}/{string.Join('/', path.Split('/').Select(Uri.EscapeDataString))}";

    public Task<string?> GetRawFileAsync(string fullName, string path, string branch = "main", CancellationToken ct = default) =>
        GetUrlAsync(RawUrl(fullName, path, branch), ct);
}
