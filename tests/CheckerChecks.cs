using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ProxyNodeHub.Web;

public static class CheckerChecks
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static JsonElement Value(IResult result) => result is IValueHttpResult value
        ? JsonSerializer.SerializeToElement(value.Value, Json) : throw new Exception("Expected JSON result.");
    private static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T error) { return error; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }
    private static Dictionary<string, JsonElement> Change(string key, object value) => new() { [key] = JsonSerializer.SerializeToElement(value) };

    public static async Task RunAsync()
    {
        // Keep this LF fixture independent of checkout endings; explicit CRLF coverage is below.
        var original = """
            # retained heading
            concurrent: 20 # retained comment
            api-key: "synthetic-config-secret"
            webdav-password: "synthetic-storage-secret"
            speed-test-url: "https://example.test/file?token=synthetic-url-secret"
            platforms:
              - netflix
              - openai
            # retained footer
            unknown-future-key:
              nested: {one: "two", values: [1, 2]}
            sub-urls:
              - "https://example.test/private?token=synthetic-source-secret"
            sub-urls-remote: ["https://example.test/list"]
            """.Replace("\r\n", "\n", StringComparison.Ordinal);
        var projected = JsonSerializer.Serialize(new CheckerConfig(original).Project("test"), Json);
        foreach (var secret in new[] { "synthetic-config-secret", "synthetic-storage-secret", "synthetic-url-secret", "synthetic-source-secret" })
            Check(!projected.Contains(secret), "Configuration projections never expose private values");
        var edited = new CheckerConfig(original).Apply(new Dictionary<string, JsonElement>()
        {
            ["concurrent"] = JsonSerializer.SerializeToElement(8),
            ["platforms"] = JsonSerializer.SerializeToElement(new[] { "netflix", "youtube" }),
            ["node-prefix"] = JsonSerializer.SerializeToElement("中文 # node \"quoted\""),
        });
        Check(edited.Contains("concurrent: 8 # retained comment") && edited.Contains("# retained footer") &&
            edited.Contains("unknown-future-key:\n  nested: {one: \"two\", values: [1, 2]}") &&
            edited.Contains("webdav-password: \"synthetic-storage-secret\""), "Editing preserves unknown nodes, comments and secrets byte for byte");
        Check(new CheckerConfig(edited).ReadSources("platforms").SequenceEqual(["netflix", "youtube"]), "Block sequences become valid flow sequences");
        Check(new CheckerConfig("platforms:\nunknown: yes\n").Apply(Change("platforms", Array.Empty<string>())).Contains("platforms: []\nunknown: yes"),
            "Empty scalar values can become lists without duplicate colons");
        var flow = new CheckerConfig("platforms: [netflix, openai] # keep\nunknown: value\n").Apply(Change("platforms", new[] { "youtube" }));
        Check(flow.Contains("platforms: [\"youtube\"] # keep\nunknown: value"), "Flow sequence spans include their closing token");
        var emptyFlow = new CheckerConfig("platforms: [] # keep\nunknown: {}\n").Apply(Change("platforms", new[] { "openai" }));
        Check(emptyFlow.Contains("platforms: [\"openai\"] # keep\nunknown: {}"), "Empty flow sequences and untouched empty maps retain exact delimiters");
        const string nested = "unknown: {nested: [{one: [1, 2]}, {}], empty: []} # untouched\nplatforms: [netflix]\n";
        Check(new CheckerConfig(nested).Apply(Change("platforms", Array.Empty<string>())).StartsWith(nested[..nested.IndexOf("platforms", StringComparison.Ordinal)], StringComparison.Ordinal),
            "Nested flow mappings and sequences outside changed fields stay verbatim");
        const string unicode = "# 中文 🧪\r\nnode-prefix: \"旧前缀🧪\" # 保留\r\nplatforms: [netflix]\r\nunknown: 中文\r\n";
        var unicodeChanged = new CheckerConfig(unicode).Apply(Change("platforms", Array.Empty<string>()));
        Check(unicodeChanged == unicode.Replace("[netflix]", "[]", StringComparison.Ordinal), "CRLF, Unicode surrogate pairs and comments keep accurate source offsets");
        var literal = new CheckerConfig("node-prefix: |\n  old prefix\nunknown: untouched\n").Apply(Change("node-prefix", "new prefix"));
        Check(literal.Contains("node-prefix: \"new prefix\"\nunknown: untouched"), "Block scalar rewrites retain the following line boundary");
        var marked = new CheckerConfig("---\nconcurrent: 20\n...\n").Apply(Change("ipv6", true));
        Check(marked.IndexOf("ipv6", StringComparison.Ordinal) < marked.IndexOf("...", StringComparison.Ordinal), "Missing fields insert inside explicit document boundaries");
        var alias = new CheckerConfig("api-key: &credential synthetic-alias-secret\nnode-prefix: *credential\n");
        Check(!JsonSerializer.Serialize(alias.Project("a"), Json).Contains("synthetic-alias-secret"), "Aliases cannot surface secret scalar values");
        var invalidAlias = await ThrowsAsync<InvalidDataException>(() => Task.Run(() => alias.Apply(Change("node-prefix", "new"))));
        Check(!invalidAlias.Message.Contains("synthetic-alias-secret"), "Alias errors do not disclose the source");
        foreach (var yaml in new[] { "concurrent: [synthetic-parser-secret", "concurrent: 1\nconcurrent: 2", "{concurrent: 1}", "a: 1\n---\na: 2" })
        {
            var error = await ThrowsAsync<InvalidDataException>(() => Task.Run(() => new CheckerConfig(yaml)));
            Check(!error.ToString().Contains("synthetic-parser-secret"), "Parser diagnostics do not retain raw YAML or inner exceptions");
        }
        foreach (var change in new[] { Change("api-key", "no"), Change("concurrent", 0), Change("ipv6", "true"),
            Change("platforms", new[] { "line\nbreak" }), Change("alive-test-url", "https://user:pass@example.test/"),
            Change("success-rate", 100), Change("sub-store-port", ":70000"), Change("sub-urls-timeout", 0), Change("sub-urls-timeout", 121) })
            await ThrowsAsync<ArgumentException>(() => Task.Run(() => new CheckerConfig(original).Apply(change)));
        var fetchTimeout = new CheckerConfig(original).Apply(Change("sub-urls-timeout", 30));
        Check(fetchTimeout.Contains("sub-urls-timeout: 30") && fetchTimeout.Contains("concurrent: 20 # retained comment") &&
            fetchTimeout.Contains("api-key: \"synthetic-config-secret\""), "Fetch timeout can be adjusted without changing probe concurrency or credentials");
        var all = CheckerConfig.Fields.ToDictionary(field => field.Key, field => JsonSerializer.SerializeToElement<object>(field.Kind switch
        {
            "integer" => (int)field.Min!.Value, "number" => 0.5, "boolean" => false, "list" => Array.Empty<string>(),
            "choice" => field.Choices![0], _ => ""
        }));
        _ = new CheckerConfig(new CheckerConfig(original).Apply(all));

        var root = Directory.CreateTempSubdirectory("proxynodehub-checker-").FullName;
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var server = builder.Build();
        const string key = "synthetic-checker-key";
        var requests = new ConcurrentQueue<(string Method, string Path, string Key)>();
        var yamlContent = original;
        var configReads = 0;
        var writes = 0;
        var mutateAtRead = -1;
        var checking = false;
        var responseCode = 200;
        var invalidJson = false;
        server.Run(async context =>
        {
            var path = context.Request.Path.Value!;
            requests.Enqueue((context.Request.Method, path, context.Request.Headers["X-API-Key"].ToString()));
            context.Response.StatusCode = responseCode;
            if (responseCode is >= 300 and < 400) context.Response.Headers.Location = server.Urls.Single() + "/redirect-target";
            if (responseCode != 200) { await context.Response.WriteAsync("synthetic-upstream-error-secret"); return; }
            if (invalidJson) { await context.Response.WriteAsync("{synthetic-invalid-json-secret"); return; }
            if (path.StartsWith("/sub/", StringComparison.Ordinal)) { await context.Response.WriteAsync("synthetic-complete-subscription"); return; }
            object result;
            if (path == "/api/config")
            {
                if (context.Request.Method == "POST")
                {
                    using var body = await JsonDocument.ParseAsync(context.Request.Body);
                    yamlContent = body.RootElement.GetProperty("content").GetString()!;
                    writes++;
                    result = new { message = "配置已更新" };
                }
                else
                {
                    if (++configReads == mutateAtRead) yamlContent += "\nexternal-change: true\n";
                    result = new { content = yamlContent };
                }
            }
            else result = path switch
            {
                "/api/status" => new { checking },
                "/api/version" => new { version = "fixture-v1" },
                "/api/logs" => new { logs = new[] { "startup api-key=" + key, "download https://example.test/private?secret=synthetic-log-secret", "connected " + key, "pipeline completed" } },
                "/api/results" => new { nodes = new[] { new { name = "fixture", speed = 512, subTag = "fixture/repo" } } },
                _ => new { message = "accepted" }
            };
            await context.Response.WriteAsJsonAsync(result);
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await server.StartAsync(deadline.Token);
        try
        {
            var address = server.Urls.Single();
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["SUBSCHECK_API_URL"] = address, ["SUBSCHECK_API_KEY"] = key, ["SUBSCHECK_WEB_URL"] = address + "/admin" }).Build();
            var connections = new ConnectionStore(root, config, new EphemeralDataProtectionProvider());
            using var checker = new SubsCheckClient(connections);
            async Task<string> Revision() => Value(await checker.GetConfigAsync(deadline.Token)).GetProperty("revision").GetString()!;
            var revision = await Revision();
            Check((await checker.SaveSettingsAsync(new(revision, Change("concurrent", 9)), deadline.Token)) is IStatusCodeHttpResult { StatusCode: 204 }, "Successful parameter update");
            Check(writes == 1 && yamlContent.Contains("concurrent: 9") && yamlContent.Contains("synthetic-storage-secret"), "The upstream receives the preserved original with selected changes");
            Check((await checker.SaveSettingsAsync(new(revision, Change("concurrent", 10)), deadline.Token)) is IStatusCodeHttpResult { StatusCode: 409 } && writes == 1,
                "Stale revision refuses writes");
            revision = await Revision();
            checking = true;
            Check((await checker.SaveSettingsAsync(new(revision, Change("concurrent", 10)), deadline.Token)) is IStatusCodeHttpResult { StatusCode: 409 } && writes == 1,
                "Running checker refuses configuration changes");
            checking = false;
            mutateAtRead = configReads + 2;
            Check((await checker.SaveSettingsAsync(new(revision, Change("concurrent", 10)), deadline.Token)) is IStatusCodeHttpResult { StatusCode: 409 } && writes == 1,
                "A concurrent upstream edit is detected before POST");
            revision = await Revision();
            var sources = new CheckerSourcesRequest(revision, true, false, ["https://example.test/manual?token=synthetic-manual-secret"]);
            Check((await checker.AddSourcesAsync(sources, ["https://example.test/known#fixture/repo"], deadline.Token)) is IStatusCodeHttpResult { StatusCode: 204 }, "Explicit source append");
            var parsed = new CheckerConfig(yamlContent);
            Check(parsed.ReadSources("sub-urls").Length == 3 && parsed.ReadSources("sub-urls-remote").Single() == "https://example.test/list",
                "Source append retains existing private and remote sources");
            var publicConfig = Value(await checker.GetConfigAsync(deadline.Token)).GetRawText();
            Check(!publicConfig.Contains("synthetic-manual-secret") && !sources.ToString().Contains("synthetic-manual-secret"), "Manual URLs stay write-only");
            var logs = Value(await checker.CallAsync("logs", false, deadline.Token)).GetRawText();
            Check(!logs.Contains(key) && !logs.Contains("synthetic-log-secret") && logs.Contains("pipeline completed"), "Logs redact keys and private URL paths/queries");
            Check(Value(await checker.CallAsync("version", false, deadline.Token)).GetProperty("version").GetString() == "fixture-v1", "Version protocol");
            _ = await checker.CallAsync("force-close", true, deadline.Token);
            Check(requests.Any(r => r == ("POST", "/api/force-close", key)), "Stop uses force-close with API header");
            foreach (var format in new[] { "clash", "mihomo", "base64" }) _ = await checker.DownloadAsync(format, deadline.Token);
            Check(requests.Where(r => r.Path.StartsWith("/sub/", StringComparison.Ordinal)).All(r => r.Key == "") &&
                requests.Any(r => r.Path == "/sub/all.yaml") && requests.Any(r => r.Path == "/sub/base64.txt"), "Only fixed public artifact routes are downloaded without API key");
            await ThrowsAsync<ArgumentException>(() => checker.DownloadAsync("../../config", deadline.Token));
            await ThrowsAsync<ArgumentException>(() => checker.CallAsync("config", false, deadline.Token));
            foreach (var status in new[] { 401, 503, 302, 307 })
            {
                responseCode = status;
                var error = await ThrowsAsync<HttpRequestException>(() => checker.CallAsync("version", false, deadline.Token));
                Check(error.StatusCode == (HttpStatusCode)status && !error.ToString().Contains("synthetic-upstream-error-secret"), "Upstream HTTP status is preserved without raw body");
            }
            Check(requests.All(r => r.Path != "/redirect-target"), "Redirects never receive requests or credentials");
            responseCode = 200;
            invalidJson = true;
            var jsonError = await ThrowsAsync<InvalidDataException>(() => checker.GetConfigAsync(deadline.Token));
            Check(!jsonError.ToString().Contains("synthetic-invalid-json-secret"), "JSON errors do not retain response snippets");
            invalidJson = false;
            revision = await Revision();
            connections.SetChecker("custom", address, "synthetic-rotated-key", address + "/admin");
            Check((await checker.SaveSettingsAsync(new(revision, Change("concurrent", 11)), deadline.Token)) is IStatusCodeHttpResult { StatusCode: 409 }, "Connection/key changes invalidate old config revisions");
            _ = await checker.CallAsync("version", false, deadline.Token);
            Check(requests.Last().Key == "synthetic-rotated-key", "Each new operation captures current connection credentials");
            connections.SetChecker("disabled", null, null, null);
            var count = requests.Count;
            foreach (var result in new[] { await checker.GetConfigAsync(deadline.Token), await checker.CallAsync("logs", false, deadline.Token),
                await checker.CallAsync("version", false, deadline.Token), await checker.CallAsync("force-close", true, deadline.Token), await checker.DownloadAsync("clash", deadline.Token) })
                Check(result is IStatusCodeHttpResult { StatusCode: 503 }, "Disabled checker operations fail locally");
            Check(requests.Count == count, "Disabled operations perform no network requests");
        }
        finally
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await server.StopAsync(stop.Token);
            Directory.Delete(root, recursive: true);
        }
        Console.WriteLine("Checker YAML preservation, privacy, protocol, conflict and loopback checks passed.");
    }
}
