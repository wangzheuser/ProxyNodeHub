using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using ProxyNodeHub;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static async Task<T> ThrowsAsync<T>(Func<Task> action, string message) where T : Exception
{
    try { await action(); }
    catch (T ex) { return ex; }
    throw new Exception(message);
}

static Task<HttpResponseMessage> Reply(string body, HttpStatusCode status = HttpStatusCode.OK) =>
    Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });

const string node = "vless://fixture@example.com:443?security=tls#fixture";
Check(NodeParser.CountNodes(node) == 1, "URI node count");
Check(NodeParser.CountNodes(Convert.ToBase64String(Encoding.UTF8.GetBytes(node))) == 1, "Base64 node count");
Check(NodeParser.CountNodes("proxies:\nproxy-groups:\n  - name: Auto\n    type: select\n    proxies: [DIRECT]") == 0,
    "Empty Clash template must not count proxy groups as nodes");
Check(NodeParser.CountNodes("proxies:\n  - name: fixture\n    type: ss\n    server: example.com\n    port: 443") == 1, "Clash count");
foreach (var invalidYaml in new[]
{
    "# proxies: are supplied by another file\nproxy-groups:\n  - name: Auto",
    "proxies: []\nproxy-groups:\n  - name: Auto",
    "proxies:\n  - name: template-only",
    "proxies:\n  - name: fixture\n    type: ss\n    server: example.com\n    port: 0",
    "proxies:\n  - name: fixture\n    type: ss\n    server: example.com\n    port: 65536",
    "proxies:\n  - name: fixture\n    type: ss\n    server: example.com\n    port: 443\ninvalid: [",
    "proxies: []\nproxies:\n  - name: fixture",
    "nested:\n  proxies:\n  - name: fixture\n    type: ss\n    server: example.com\n    port: 443"
}) Check(NodeParser.CountNodes(invalidYaml) == 0, "Invalid or non-node YAML must not be published");
Check(NodeParser.CountNodes("proxies: [{type: ss, server: example.com, port: 443, name: fixture}]\nproxy-groups: [{name: Auto}]") == 1,
    "Flow-style nodes count independently of key order and proxy groups");
Check(NodeParser.CountNodes("proxies:\n- {name: first, type: ss, server: example.com, port: 443}\n- {name: second, type: trojan, server: example.org, port: 8443}") == 2,
    "Unindented YAML sequences remain supported");
Check(NodeParser.CountNodes("defaults: &node {name: fixture, type: ss, server: example.com, port: 443}\nproxies: [*node]") == 1,
    "YAML node aliases remain supported");
Check(NodeParser.CountNodes("{\"proxies\":[{\"name\":\"fixture\",\"type\":\"ss\",\"server\":\"example.com\",\"port\":443}]}") == 1,
    "Clash JSON uses the same structured node validation");
foreach (var control in new[] { '\0', '\u0001', '\u007f', '\u009f' })
    Check(NodeParser.CountNodes($"proxies: [{{name: \"fixture{control}\", type: ss, server: example.com, port: 443}}]") == 0,
        "Raw control characters rejected by subs-check must not pass discovery");
Check(NodeParser.CountNodes("proxies: [{name: \"节点 🧪\", type: ss, server: example.com, port: 443}]\r\n") == 1,
    "Valid Unicode names and line endings remain supported");
Check(NodeParser.CountNodes("invalid") == 0, "Invalid subscription");
var merged = NodeParser.MergeNodes([node, node]);
Check(merged.total == 2 && merged.unique == 1, "Existing merge semantics");
var repo = new RepoInfo { FullName = "fixture/repo", Stars = 42,
    Links = [new() { NodeCount = 10, IsAnalyzed = true }] };
Check(GitHubAnalyzer.BestNodeCount(repo) == 10, "Best validated count");
Check(GitHubAnalyzer.CalculateScore(repo) is >= 0 and <= 100, "Score range");
Check(!SubscriptionFinder.IsCandidateNodeFile("README.md"), "Ignore documentation");
Check(SubscriptionFinder.IsCandidateNodeFile("output/clash.yaml"), "Candidate file");
var json = JsonSerializer.Serialize(repo, new JsonSerializerOptions { IncludeFields = true });
Check(json.Contains("fixture/repo"), "Shared model fields");

var root = Directory.CreateTempSubdirectory("proxynodehub-tests-").FullName;
try
{
    var directory = Path.Combine(root, "features");
    var library = new FeatureLibrary(directory);
    var stamp = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    var feature = new RepoFeature { FullName = "fixture/learned", Category = RepoCategory.LinkAggregator,
        KnownSubPaths = ["output/custom.txt"], TotalNodes = 2, LastAnalyzed = stamp,
        AnalysisCount = 4, IsReliable = true };
    library.Save(feature);
    feature.KnownSubPaths.Clear();
    var copy = library.Get(feature.FullName)!;
    copy.KnownSubPaths.Clear();
    Check(library.Get(feature.FullName)!.KnownSubPaths.Count == 1, "Feature ownership on save/get");
    await Task.WhenAll(Enumerable.Range(0, 24).Select(i => Task.Run(() => library.Save(new RepoFeature
    {
        FullName = $"fixture/parallel-{i}", KnownSubPaths = ["sub"], TotalNodes = i + 1, IsReliable = true
    }))));
    var reloaded = new FeatureLibrary(directory);
    Check(reloaded.GetReliableRepos().Count == 25, "Concurrent saves survive reload");
    for (var i = 0; i < 24; i++)
        Check(reloaded.Get($"fixture/parallel-{i}")!.TotalNodes == i + 1, "No lost concurrent writes");
    var stored = reloaded.Get(feature.FullName)!;
    Check(stored.FullName == feature.FullName && stored.Category == RepoCategory.LinkAggregator &&
        stored.KnownSubPaths.SequenceEqual(["output/custom.txt"]) && stored.TotalNodes == 2 &&
        stored.LastAnalyzed == stamp && stored.AnalysisCount == 4 && stored.IsReliable, "All feature fields persist");
    var separate = new FeatureLibrary(Path.Combine(root, "separate"));
    Check(separate.Get(feature.FullName) == null, "Feature directories are isolated");
    separate.Save(new RepoFeature { FullName = feature.FullName, TotalNodes = 99 });
    Check(library.Get(feature.FullName)!.TotalNodes == 2, "Separate stores do not share mutable state");
    var file = Path.Combine(directory, "features.json");
    var before = File.ReadAllBytes(file);
    Directory.CreateDirectory(file + ".tmp"); // Force a real write failure, including when tests run as root.
    foreach (var name in new[] { feature.FullName, "fixture/not-published" })
    {
        var failed = false;
        try { library.Save(new RepoFeature { FullName = name, TotalNodes = 999 }); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed = true; }
        Check(failed, "Storage failure must be observable");
    }
    Check(library.Get(feature.FullName)!.TotalNodes == 2 && library.Get("fixture/not-published") == null,
        "Failed writes must not publish in memory");
    Check(File.ReadAllBytes(file).SequenceEqual(before), "Failed writes preserve durable data");
    Directory.Delete(file + ".tmp");

    const string fixtureToken = "fixture-only-not-a-real-token";
    var rawRequests = new ConcurrentQueue<string>();
    using var github = new GitHubService(fixtureToken, true,
        new StubHandler((request, _) =>
        {
            Check(request.RequestUri!.Host == "api.github.com", "API destination");
            Check(request.Headers.Authorization?.ToString() == $"Bearer {fixtureToken}", "API receives token");
            var url = request.RequestUri!.AbsoluteUri;
            if (url.Contains("/search/repositories?"))
                return Reply("{\"items\":[{\"full_name\":\"fixture/repo\",\"default_branch\":\"release/2026\"}]}");
            Check(url.Contains("/git/trees/release%2F2026?"), "Tree uses exact escaped default branch");
            return Reply("{\"tree\":[{\"type\":\"blob\",\"path\":\"output/node list.txt\"}]}");
        }),
        new StubHandler((request, _) =>
        {
            Check(request.Headers.Authorization == null, "Raw requests never receive API token");
            rawRequests.Enqueue(request.RequestUri!.AbsoluteUri);
            return request.RequestUri!.AbsolutePath.EndsWith("/missing") ? Reply("", HttpStatusCode.NotFound) : Reply(node);
        }));
    var found = GitHubAnalyzer.ToRepoInfo((await github.SearchReposAsync("fixture", 1)).Single());
    Check(found.Branch == "release/2026", "Repository metadata preserves default branch");
    Check((await github.GetFileTreeAsync(found.FullName, branch: found.Branch)).Single() == "output/node list.txt", "Tree data");
    Check(await github.GetRawFileAsync(found.FullName, "output/node list.txt", found.Branch) == node, "Raw download");
    Check(rawRequests.Single() == "https://raw.githubusercontent.com/fixture/repo/release%2F2026/output/node%20list.txt", "Published/raw branch agreement");
    Check(await github.GetRawFileAsync(found.FullName, "missing", "master") == null && rawRequests.Count == 2 &&
        github.FailedDownloads == 0, "Raw 404 is normal and must not try another branch");
    Check(KnownRepoLoader.GetKnownLinks("Pawdroid/Free-servers", "master").All(l => l.Url.Contains("/master/")) &&
        KnownRepoLoader.GetKnownLinks("Pawdroid/Free-servers", "master").Count > 0, "Known mappings use actual branch");
    foreach (var url in new[] { "http://raw.githubusercontent.com/a/b/main/sub", "https://192.168.31.122/sub",
        "https://127.0.0.1/sub", "https://[::1]/sub", "https://raw.githubusercontent.com.evil.invalid/sub",
        "https://raw.githubusercontent.com@127.0.0.1/sub", "https://user@raw.githubusercontent.com/a/b/main/sub",
        "https://raw.githubusercontent.com:444/a/b/main/sub", "https://raw.githubusercontent.com/a/b/main/sub?secret=x" })
    {
        Check(!GitHubService.IsAllowedSubscription(url), "Reject non-public source boundary");
        await ThrowsAsync<InvalidDataException>(() => github.GetUrlAsync(url), "Rejected source must not be fetched");
    }
    Check(rawRequests.Count == 2 && GitHubService.IsAllowedSubscription("https://nodes.udptoos.com/subscriptions/base64.txt"),
        "Blocked URLs never reach HTTP and supported external source remains allowed");
    foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden })
    {
        using var denied = new GitHubService(apiHandler: new StubHandler((_, _) => Reply("{}", status)),
            rawHandler: new StubHandler((_, _) => throw new Exception("Unexpected raw request")));
        foreach (var call in new Func<Task>[] { () => denied.SearchReposAsync("fixture", 1),
            () => denied.GetRecentCommitsAsync("fixture/repo"), () => denied.GetFileTreeAsync("fixture/repo") })
            Check((await ThrowsAsync<HttpRequestException>(call, "API denial must not become empty results")).StatusCode == status,
                "API error retains HTTP status");
    }

    var nodes = node + "\n" + "trojan://fixture@example.org:443#fixture";
    using var learned = new GitHubService(restrictPublicSources: true,
        apiHandler: new StubHandler((_, _) => throw new Exception("L1 must avoid API requests")),
        rawHandler: new StubHandler((request, _) =>
        {
            Check(request.RequestUri!.AbsoluteUri == "https://raw.githubusercontent.com/fixture/learned/stable/output/custom.txt", "L1 path and branch");
            return Reply(nodes);
        }));
    var links = await SubscriptionFinder.FindLinksAsync(learned, feature.FullName, branch: "stable", library: library);
    Check(links.Count == 1 && links[0].Name == "output/custom.txt" && links[0].Url.Contains("/stable/") &&
        links[0].IsAnalyzed && links[0].IsValid && links[0].NodeCount == 2 &&
        new FeatureLibrary(directory).Get(feature.FullName)!.AnalysisCount == 5, "L1 discovery and persisted learning");

    const string readmeUrl = "https://raw.githubusercontent.com/elsewhere/nodepool/stable/deep/clash.yaml";
    using var readme = new GitHubService(restrictPublicSources: true,
        apiHandler: new StubHandler((request, _) => Reply(request.RequestUri!.AbsolutePath.EndsWith("/commits")
            ? "[]" : "{\"tree\":[]}")),
        rawHandler: new StubHandler((request, _) =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url.EndsWith("/README.md")) return Reply($"Subscription documentation: [download]({readmeUrl})");
            return url == readmeUrl ? Reply(nodes) : Reply("", HttpStatusCode.NotFound);
        }));
    var readmeStore = new FeatureLibrary(Path.Combine(root, "readme"));
    links = await SubscriptionFinder.FindLinksAsync(readme, "fixture/readme", branch: "stable", library: readmeStore);
    Check(links.Count == 1 && links[0].Name == "deep/clash.yaml" && links[0].Type == "Clash" &&
        links[0].Url == readmeUrl && links[0].NodeCount == 2 && links[0].IsValid && links[0].IsAnalyzed,
        "L5 README uses third capture group for path, type, and name");

    var analyzed = new RepoInfo { FullName = "fixture/readme", Branch = "stable" };
    var analysis = await DiscoveryEngine.AnalyzeAsync(readme, analyzed, default, library: readmeStore);
    Check(analysis.CommitsSucceeded && analysis.SubscriptionsSucceeded && analyzed.CommitsAnalyzed &&
        analyzed.TotalNodes == 2 && analyzed.Score == GitHubAnalyzer.CalculateScore(analyzed),
        "Both hosts share commit, subscription, node-count and score orchestration");
    Check(DiscoveryEngine.Queries().Length == 8, "Shared search queries");

    using var cancelled = new CancellationTokenSource();
    using var cancelling = new GitHubService(restrictPublicSources: true,
        apiHandler: new StubHandler((_, _) => Reply("{\"tree\":[{\"type\":\"blob\",\"path\":\"output/sub.txt\"}]}")),
        rawHandler: new StubHandler((request, ct) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/README.md")) return Reply("");
            cancelled.Cancel();
            return Task.FromCanceled<HttpResponseMessage>(ct);
        }));
    var cancelledStore = new FeatureLibrary(Path.Combine(root, "cancelled"));
    await ThrowsAsync<OperationCanceledException>(() => SubscriptionFinder.FindLinksAsync(cancelling,
        "fixture/cancelled", cancelled.Token, library: cancelledStore), "L3 must propagate cancellation");
    Check(cancelledStore.Get("fixture/cancelled") == null && cancelling.FailedDownloads == 0,
        "Cancellation neither publishes a feature nor counts as network failure");
    await ThrowsAsync<OperationCanceledException>(() => github.GetUrlAsync(readmeUrl, cancelled.Token), "Raw cancellation");
    await ThrowsAsync<OperationCanceledException>(() => github.SearchReposAsync("fixture", 1, cancelled.Token), "API cancellation");
}
finally { Directory.Delete(root, recursive: true); }
Console.WriteLine("Core regression checks passed.");
await WebChecks.RunAsync();
await ConnectionChecks.RunAsync();
await DiscoveryChecks.RunAsync();
await CheckerChecks.RunAsync();

sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        respond(request, cancellationToken);
}
