using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ProxyNodeHub;
using ProxyNodeHub.Web;

public static class DiscoveryChecks
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }

    private static RepoInfo Repo(string name, int nodes = 1) => new()
    {
        FullName = name, Branch = "main", TotalNodes = nodes,
        Links = [new() { Name = "sub", Url = GitHubService.RawUrl(name, "sub", "main"),
            NodeCount = nodes, IsValid = true, IsAnalyzed = true }]
    };

    public static async Task RunAsync()
    {
        var root = Directory.CreateTempSubdirectory("proxynodehub-discovery-checks-").FullName;
        try
        {
            var directory = Path.Combine(root, "state");
            var settings = new DiscoverySettings(AutoRefresh: false);
            var store = new StateStore(directory, settings);
            var at = DateTimeOffset.UtcNow.AddMinutes(-10);
            var repos = new[] { Repo("fixture/a"), Repo("fixture/b"), Repo("fixture/c") };
            var snapshot = new DiscoverySnapshot(at, repos, DiscoveryApi.SubscriptionUrls(repos));
            var run = new RunHistory(at.AddSeconds(-1), at, true, 3, null, [new(at, "synthetic log")]);
            store.FinishRun(snapshot, run, ["fixture/a"]);
            store.SetFavorites(["FIXTURE/A", "fixture/b", "fixture/a"], true);
            Check(store.State.Favorites.Length == 2, "Batch favorites are case-insensitive and idempotent.");
            var before = store.State;
            Throws<KeyNotFoundException>(() => store.SetFavorites(["fixture/c", "fixture/unknown"], true));
            Check(ReferenceEquals(before, store.State), "Invalid batches must not partially publish.");
            Throws<ArgumentException>(() => StateStore.SelectRepositories(store.State, []));
            Throws<ArgumentException>(() => StateStore.SelectRepositories(store.State, null!));
            Check(StateStore.SelectRepositories(store.State, ["fixture/c", "FIXTURE/A"]).Length == 2,
                "Selection resolves only known repositories and preserves the requested scope.");
            Check(DiscoveryApi.SubscriptionUrls(StateStore.SelectRepositories(store.State, ["fixture/c"]))
                .SequenceEqual([repos[2].Links[0].Url]), "Selected export must not include other repositories.");

            var invalidUrl = Repo("fixture/private");
            invalidUrl.Links[0].Url = "http://127.0.0.1/private";
            Check(DiscoveryApi.SubscriptionUrls([invalidUrl]).Length == 0, "Exports never include unsupported URLs.");

            // A favorite removed while a queued recheck runs must not reappear at commit.
            store.SetFavorite("fixture/a", false);
            var updated = Repo("fixture/a", 8);
            store.FinishRecheck([updated, Repo("fixture/b", 9)], run with { StartedAt = at.AddMinutes(1), FinishedAt = at.AddMinutes(2) });
            Check(store.State.Current!.Repositories.Length == 3 &&
                store.State.Current.Repositories.Single(r => r.FullName == "fixture/c").TotalNodes == 1,
                "Partial rechecks retain unselected and failed old sources.");
            Check(store.State.Favorites.Length == 1 && store.State.Favorites[0].FullName == "fixture/b" &&
                store.State.Favorites[0].TotalNodes == 9, "Rechecks update current favorites without reviving deleted ones.");
            var successful = store.State.Current;
            store.FinishRecheck([], run with { StartedAt = at.AddMinutes(3), FinishedAt = at.AddMinutes(4), Success = false, Error = "synthetic failure" });
            Check(ReferenceEquals(successful, store.State.Current), "Failed recheck leaves the published feed unchanged.");

            foreach (var invalid in new[] { settings with { AnalysisConcurrency = 0 }, settings with { AnalysisConcurrency = 11 },
                settings with { SearchHistoryDays = 0 }, settings with { SearchHistoryDays = 31 },
                settings with { LogRetentionDays = 0 }, settings with { LogRetentionDays = 31 },
                settings with { DownloadMirror = "http://127.0.0.1/" }, settings with { DownloadMirror = null! },
                settings with { RepoCount = 101 } })
                Throws<ArgumentException>(() => store.SaveSettings(invalid));
            store.SaveSettings(settings with { RepoCount = 100, AnalysisConcurrency = 10, DownloadMirror = "auto" });
            store.SaveSettings(settings with { SkipSearched = true, SkipFavorites = true });
            var exclusions = StateStore.ExplorationExclusions(store.State, DateTimeOffset.UtcNow);
            Check(exclusions.Contains("FIXTURE/A") && exclusions.Contains("fixture/b") && !exclusions.Contains("fixture/c"),
                "Explicit exploration combines search memory and favorites case-insensitively.");
            Check(StateStore.ExplorationExclusions(store.State with { Settings = settings }, DateTimeOffset.UtcNow).Count == 0,
                "Default exploration options do not silently exclude sources.");
            Check(StateStore.ExplorationExclusions(store.State with { Settings = settings with { SkipSearched = true } },
                DateTimeOffset.UtcNow.AddDays(2)).Count == 0, "Search memory expires at its configured lifetime.");

            using (var worker = new DiscoveryWorker(store, new FeatureLibrary(Path.Combine(root, "features")),
                ConnectionChecks.CreateStore(Path.Combine(root, "connections")), NullLogger<DiscoveryWorker>.Instance))
            {
                Check(worker.RequestRecheck(["fixture/a"]) && !worker.RequestExplore() && !worker.RequestRun(),
                    "Recheck, explore and refresh share one queue.");
                worker.CancelRun();
                Check(worker.RequestExplore(), "A queued run can be cancelled.");
                worker.CancelRun();
                Throws<KeyNotFoundException>(() => worker.RequestRecheck(["fixture/unknown"]));
                store.SaveSettings(store.State.Settings with { AutoRefresh = true });
                var due = worker.NextRunAt;
                var completed = store.State.History.Select(h => h.FinishedAt).ToArray();
                Check(worker.GetLogs(run.StartedAt).Length == 1, "An older retained run has readable logs.");
                worker.ClearLogs();
                store.ClearSearchHistory();
                Check(worker.NextRunAt == due && store.State.History.Select(h => h.FinishedAt).SequenceEqual(completed) &&
                    worker.GetLogs(run.StartedAt).Length == 0 && store.GetSearchHistory().Length == 0,
                    "Clearing logs or search memory preserves run history and the scheduling anchor.");
            }

            var oldRun = run with { StartedAt = at.AddDays(-8), FinishedAt = at.AddDays(-8), Logs = [new(at.AddDays(-8), "expired")] };
            store.FinishRun(snapshot, oldRun);
            Check(store.State.History[0].Logs.Length == 0 && store.State.History[0].FinishedAt == oldRun.FinishedAt,
                "Log retention trims entries without discarding run timestamps.");

            var file = Path.Combine(directory, "state.json");
            var saved = File.ReadAllBytes(file);
            before = store.State;
            Directory.CreateDirectory(file + ".tmp");
            var failed = false;
            try { store.SetFavorites(["fixture/a", "fixture/c"], true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed = true; }
            Check(failed && ReferenceEquals(before, store.State) && saved.SequenceEqual(File.ReadAllBytes(file)),
                "Failed batch persistence does not change either state owner.");
            Directory.Delete(file + ".tmp");
            store = new StateStore(directory, settings);
            Check(store.State.Favorites.Single().TotalNodes == 9, "Updated favorite fields survive reload.");

            var legacy = Path.Combine(root, "legacy-state");
            Directory.CreateDirectory(legacy);
            File.WriteAllText(Path.Combine(legacy, "state.json"), """
                {"Settings":{"RefreshHours":6,"RepoCount":10,"InactiveDays":7,"AutoRefresh":false},"Current":null,"Favorites":[],"History":[]}
                """);
            var migrated = new StateStore(legacy, settings);
            Check(migrated.State.Settings.AnalysisConcurrency == 1 && migrated.State.Settings.LogRetentionDays == 7 &&
                migrated.State.Settings.DownloadMirror == "" && migrated.State.SearchHistory.Length == 0,
                "Old state files acquire safe new defaults without a migration rewrite.");

            var added = Repo("fixture/new-source");
            var exploration = new DiscoverySnapshot(at, [added], DiscoveryApi.SubscriptionUrls([added]));
            store.FinishRun(exploration, run, [added.FullName], merge: true);
            Check(store.State.Current!.Repositories.Length == 4 && store.State.Current.Subscriptions.Length == 4,
                "Exploring new repositories adds sources without replacing existing ones.");
            store.FinishRun(exploration, run);
            Check(store.State.Current!.Repositories.Single().FullName == added.FullName,
                "Ordinary refresh keeps its complete-snapshot replacement semantics.");
            var legacyFeed = new StateStore(Path.Combine(root, "legacy-feed"), settings);
            var orphanUrl = GitHubService.RawUrl("fixture/legacy", "sub", "main");
            legacyFeed.FinishRun(snapshot with { Subscriptions = [..snapshot.Subscriptions, orphanUrl] }, run);
            legacyFeed.FinishRun(exploration, run, merge: true);
            Check(legacyFeed.State.Current!.Subscriptions.Contains(orphanUrl),
                "Merging preserves legacy feed URLs even when no repository metadata owns them.");

            var accumulated = Enumerable.Range(0, 201).Select(i => Repo($"fixture/accumulated{i}")).ToArray();
            var accumulatedNames = accumulated.Select(r => r.FullName).ToArray();
            legacyFeed.FinishRun(new(at, accumulated, DiscoveryApi.SubscriptionUrls(accumulated)), run);
            Check(StateStore.SelectRepositories(legacyFeed.State, accumulatedNames).Length == 201,
                "Export and recheck selections are not capped by the favorite storage limit.");
            var beforeLimit = legacyFeed.State;
            Throws<ArgumentException>(() => legacyFeed.SetFavorites(accumulatedNames, true));
            Check(ReferenceEquals(beforeLimit, legacyFeed.State), "The 200-favorite limit still rejects oversized batches atomically.");

            await CheckAnalysisAsync(Path.Combine(root, "analysis"));
            await CheckMirrorsAsync();
        }
        finally { Directory.Delete(root, recursive: true); }
        Console.WriteLine("Discovery batch, recheck, selection, history, scheduling, concurrency and mirror checks passed.");
    }

    private static async Task CheckAnalysisAsync(string directory)
    {
        const string nodes = "vless://11111111-1111-1111-1111-111111111111@example.test:443?security=tls#synthetic";
        var active = 0;
        var peak = 0;
        var failCommits = false;
        var api = new Handler(async (request, ct) =>
        {
            Check(request.RequestUri!.Host == "api.github.com", "Credentials stay on the API host.");
            var concurrent = Interlocked.Increment(ref active);
            int observed;
            do { observed = Volatile.Read(ref peak); } while (concurrent > observed && Interlocked.CompareExchange(ref peak, concurrent, observed) != observed);
            try
            {
                await Task.Delay(20, ct);
                return new(failCommits ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK) { Content = new StringContent("[]") };
            }
            finally { Interlocked.Decrement(ref active); }
        });
        var raw = new Handler((request, _) =>
        {
            Check(request.Headers.Authorization is null, "Subscription requests have no API token.");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(nodes) });
        });
        using var github = new GitHubService("synthetic-token", true, api, raw);
        var features = new FeatureLibrary(directory);
        var repos = Enumerable.Range(0, 4).Select(i => Repo($"fixture/recheck{i}")).ToArray();
        foreach (var repo in repos) features.Save(new RepoFeature { FullName = repo.FullName, KnownSubPaths = ["sub"] });
        var result = await DiscoveryWorker.AnalyzeRepositoriesAsync(github, repos,
            new(AnalysisConcurrency: 2), features, _ => { }, (_, _) => { }, CancellationToken.None, requireComplete: true);
        Check(result.Repositories.Length == 4 && peak == 2, "Analysis obeys the configured parallelism.");
        Check(repos.All(r => !r.CommitsAnalyzed) && result.Repositories.All(r => r.CommitsAnalyzed),
            "Analysis does not mutate the published repository instances.");
        failCommits = true;
        var incomplete = await DiscoveryWorker.AnalyzeRepositoriesAsync(github, [repos[0]], new(), features,
            _ => { }, (_, _) => { }, CancellationToken.None, requireComplete: true);
        Check(incomplete.Repositories.Length == 0 && incomplete.Failed == 1 && !repos[0].CommitsAnalyzed,
            "An upstream failure cannot produce a replacement for an existing repository.");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = false;
        try { await DiscoveryWorker.AnalyzeRepositoriesAsync(github, repos, new(), features, _ => { }, (_, _) => { }, cancellation.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled, "Queued repository analysis propagates cancellation.");
    }

    private static async Task CheckMirrorsAsync()
    {
        const string nodes = "vless://11111111-1111-1111-1111-111111111111@example.test:443?security=tls#synthetic";
        var requests = new ConcurrentQueue<Uri>();
        var raw = new Handler((request, _) =>
        {
            Check(request.Headers.Authorization is null, "Mirrors never receive GitHub tokens.");
            requests.Enqueue(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(nodes) });
        });
        using var github = new GitHubService("synthetic-token", true,
            new Handler((_, _) => throw new Exception("No API call expected.")), raw);
        var mirror = GitHubService.PublicMirrors.First(m => !m.IsDefault && !m.IsJsDelivr);
        await github.ConfigurePublicMirrorAsync(mirror.Name);
        // Even a trusted caller accidentally changing the mutable record cannot change the allowed destination.
        github.CurrentProxy!.Prefix = "http://127.0.0.1/private/";
        var url = GitHubService.RawUrl("fixture/repo", "sub", "main");
        Check(await github.GetUrlAsync(url) == nodes && requests.Single().ToString().StartsWith(mirror.Prefix, StringComparison.Ordinal),
            "Restricted downloads resolve a fixed mirror definition, not mutable prefixes.");
        var refused = false;
        try { await github.GetUrlAsync("http://127.0.0.1/private"); }
        catch (InvalidDataException) { refused = true; }
        Check(refused && requests.Count == 1, "Original source validation happens before mirror conversion.");
        await github.ConfigurePublicMirrorAsync("");
        await github.GetUrlAsync(url);
        Check(requests.Last().Host == "raw.githubusercontent.com", "Explicit direct mode remains direct.");

        var redirectCalls = 0;
        using var redirecting = new GitHubService("synthetic-token", true,
            new Handler((_, _) => throw new Exception("No API call expected.")), new Handler((request, _) =>
            {
                Check(request.Headers.Authorization is null, "Redirecting sources receive no token.");
                Interlocked.Increment(ref redirectCalls);
                var response = new HttpResponseMessage(HttpStatusCode.Redirect);
                response.Headers.Location = new Uri("http://127.0.0.1/private");
                return Task.FromResult(response);
            }));
        Check(await redirecting.GetUrlAsync(url) is null && redirectCalls == 1, "Redirect responses fail instead of triggering an application retry to the LAN.");

        var fixedHosts = GitHubService.PublicMirrors.Where(m => !m.IsDefault).Select(m => new Uri(m.Prefix).Host)
            .Append("raw.githubusercontent.com").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tested = await GitHubService.TestPublicMirrorsAsync(handler: new Handler((request, _) =>
        {
            Check(request.Headers.Authorization is null && fixedHosts.Contains(request.RequestUri!.Host),
                "Mirror tests only use fixed public destinations without credentials.");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(nodes) });
        }));
        Check(tested.Count == GitHubService.PublicMirrors.Count && tested.All(m => m.LatencyMs >= 0),
            "All fixed mirrors produce isolated latency results.");
        Check(GitHubService.PublicMirrors.All(m => m.LatencyMs == -1), "Testing does not mutate shared mirror configuration.");
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
