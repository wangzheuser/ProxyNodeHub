using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ProxyNodeHub;
using ProxyNodeHub.Web;

public static class WebChecks
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception(message);
    }

    public static async Task RunAsync()
    {
        var root = Directory.CreateTempSubdirectory("proxynodehub-web-checks-").FullName;
        try
        {
            var directory = Path.Combine(root, "state");
            var defaults = new DiscoverySettings();
            var store = new StateStore(directory, defaults);
            Check(store.State.Settings == defaults && store.State.Current is null &&
                store.State.Favorites.Length == 0 && store.State.History.Length == 0 && !store.IsFresh,
                "First Web state uses defaults without inventing discovery results");
            var settings = new DiscoverySettings(12, 25, 30, false);
            store.SaveSettings(settings);
            foreach (var invalid in new[] { settings with { RefreshHours = 0 }, settings with { RefreshHours = 25 },
                settings with { RepoCount = 0 }, settings with { RepoCount = 101 },
                settings with { InactiveDays = 0 }, settings with { InactiveDays = 61 } })
                Throws<ArgumentException>(() => store.SaveSettings(invalid), "Invalid settings must fail");
            store = new StateStore(directory, defaults);
            Check(store.State.Settings == settings, "Saved settings survive restart and invalid updates");

            var at = DateTimeOffset.UtcNow.AddMinutes(-10);
            const string url = "https://raw.githubusercontent.com/fixture/repo/main/sub.txt";
            var repo = new RepoInfo { FullName = "fixture/repo", Branch = "main", TotalNodes = 2,
                Links = [new() { Name = "sub.txt", Url = url, Type = "URI", NodeCount = 2,
                    IsValid = true, IsAnalyzed = true }] };
            var snapshot = new DiscoverySnapshot(at, [repo], [url]);
            var success = new RunHistory(at.AddMinutes(-1), at, true, 1, null, [new(at, "fixture success")]);
            store.FinishRun(snapshot, success);
            store.SetFavorite(repo.FullName, true);
            store.SetFavorite("FIXTURE/REPO", true);
            Check(store.State.Favorites.Length == 1, "Favorites are idempotent and case insensitive");
            Throws<KeyNotFoundException>(() => store.SetFavorite("fixture/not-discovered", true),
                "Favorites cannot invent undiscovered repositories");
            Throws<ArgumentException>(() => store.SetFavorite("", true), "Empty favorite name fails");
            store = new StateStore(directory, defaults);
            Check(store.IsFresh && store.State.Current!.Subscriptions.SequenceEqual([url]) &&
                store.State.Favorites.Single().Links.Single().NodeCount == 2 &&
                store.State.History.Single().Logs.Single().Message == "fixture success",
                "Snapshot, favorite model fields and logs survive restart");
            store.SetFavorite("FIXTURE/REPO", false);
            Check(store.State.Favorites.Length == 0, "Favorite removal is case insensitive");

            var failed = new RunHistory(at, at.AddSeconds(1), false, 0, "fixture failure", []);
            store.FinishRun(null, failed);
            Check(store.State.Current!.Subscriptions.SequenceEqual([url]) &&
                !store.State.History[0].Success && store.State.History[0].Error == "fixture failure",
                "Failed runs retain the last usable snapshot and expose failure");
            Throws<InvalidDataException>(() => store.FinishRun(null, success), "Success requires a snapshot");
            Throws<InvalidDataException>(() => store.FinishRun(snapshot, failed), "Failure cannot publish a snapshot");
            Throws<InvalidDataException>(() => store.FinishRun(snapshot with { Subscriptions = [] }, success),
                "Empty snapshots cannot replace usable data");
            Throws<InvalidDataException>(() => store.FinishRun(snapshot with { Subscriptions = ["http://127.0.0.1/private"] }, success),
                "Persisted feeds enforce the public-source boundary");
            for (var i = 0; i < 25; i++)
                store.FinishRun(null, failed with { FinishedAt = at.AddSeconds(i + 2), Error = $"failure-{i}" });
            store = new StateStore(directory, defaults);
            Check(store.State.History.Length == 20 && store.State.History[0].Error == "failure-24" &&
                store.State.History[^1].Error == "failure-5" && store.State.Current!.Subscriptions.Single() == url,
                "History is durable, newest first, bounded to 20 and does not erase the last success");

            using (var worker = new DiscoveryWorker(store, new FeatureLibrary(Path.Combine(root, "features")),
                ConnectionChecks.CreateStore(Path.Combine(root, "connections")), NullLogger<DiscoveryWorker>.Instance))
            {
                Check(worker.NextRunAt is null && !worker.Attempt.Running,
                    "Disabled scheduling has no next run");
                Check(worker.RequestRun() && !worker.RequestRun(), "Only one manual run can queue");
                worker.CancelRun();
                Check(!worker.Attempt.Running && worker.RequestRun(), "Cancelling a queued run permits a new request");
                worker.CancelRun();
                store.SaveSettings(settings with { AutoRefresh = true });
                Check(worker.NextRunAt == store.State.History[0].FinishedAt.AddHours(12),
                    "Scheduling continues from the latest completed attempt, including failures");
                store.SaveSettings(settings);
            }

            var file = Path.Combine(directory, "state.json");
            var before = File.ReadAllBytes(file);
            var previous = store.State;
            Directory.CreateDirectory(file + ".tmp"); // A real write failure also works when tests run as root.
            var writeFailed = false;
            try { store.SaveSettings(settings with { RefreshHours = 1 }); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { writeFailed = true; }
            Check(writeFailed && ReferenceEquals(previous, store.State) && File.ReadAllBytes(file).SequenceEqual(before),
                "Failed atomic writes neither publish in memory nor overwrite durable state");
            Directory.Delete(file + ".tmp");

            var legacyDirectory = Directory.CreateDirectory(Path.Combine(root, "legacy")).FullName;
            var legacyFile = Path.Combine(legacyDirectory, "current.json");
            var legacyJson = JsonSerializer.Serialize(snapshot, StateStore.JsonOptions);
            File.WriteAllText(legacyFile, legacyJson);
            var imported = new StateStore(legacyDirectory, settings);
            Check(imported.State.Current!.Subscriptions.SequenceEqual([url]) && File.ReadAllText(legacyFile) == legacyJson,
                "Legacy current.json imports without modifying the rollback source");
            File.WriteAllText(legacyFile, "invalid legacy content after import");
            Check(new StateStore(legacyDirectory, defaults).State.Current!.Subscriptions.Single() == url,
                "state.json is authoritative after the one-time import");
            using (var worker = new DiscoveryWorker(imported, new FeatureLibrary(Path.Combine(root, "legacy-features")),
                ConnectionChecks.CreateStore(Path.Combine(root, "connections")), NullLogger<DiscoveryWorker>.Instance))
            {
                imported.SaveSettings(settings with { AutoRefresh = true });
                Check(worker.NextRunAt == at.AddHours(12), "Imported snapshots anchor scheduling when there is no history");
            }

            var invalidDirectory = Directory.CreateDirectory(Path.Combine(root, "invalid")).FullName;
            var invalidFile = Path.Combine(invalidDirectory, "state.json");
            File.WriteAllText(invalidFile, "{\"Settings\":null,\"Current\":null,\"Favorites\":[],\"History\":[]}");
            Throws<InvalidDataException>(() => new StateStore(invalidDirectory, defaults),
                "Invalid persisted state fails explicitly instead of resetting user data");
            File.WriteAllText(invalidFile, "{");
            Throws<JsonException>(() => new StateStore(invalidDirectory, defaults), "Malformed persisted JSON fails explicitly");
        }
        finally { Directory.Delete(root, recursive: true); }
        Console.WriteLine("Web state and scheduler checks passed.");
        await CheckSubsCheckAsync();
    }

    private static async Task CheckSubsCheckAsync()
    {
        var directory = Directory.CreateTempSubdirectory("proxynodehub-protocol-").FullName;
        const string key = "synthetic-subs-check-key-only";
        var calls = new[]
        {
            (Endpoint: "status", Post: false, Body: "{\"checking\":false,\"progress\":12}"),
            (Endpoint: "results", Post: false, Body: "{\"nodes\":[{\"name\":\"fixture\",\"speed\":123}]}"),
            (Endpoint: "trigger-check", Post: true, Body: "{\"success\":true}")
        };
        var requests = new ConcurrentQueue<(string Method, string Path, string Key)>();
        var responseStatus = 200;
        var redirectHits = 0;
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var server = builder.Build();
        server.Run(async context =>
        {
            requests.Enqueue((context.Request.Method, context.Request.Path.Value ?? "",
                context.Request.Headers["X-API-Key"].ToString()));
            if (context.Request.Path == "/redirect-target")
            {
                Interlocked.Increment(ref redirectHits);
                await context.Response.WriteAsync("{}");
                return;
            }
            context.Response.StatusCode = Volatile.Read(ref responseStatus);
            if (context.Response.StatusCode is >= 300 and < 400)
                context.Response.Headers.Location = server.Urls.Single() + "/redirect-target";
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(calls.FirstOrDefault(c => "/api/" + c.Endpoint == context.Request.Path).Body ?? "{}");
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await server.StartAsync(deadline.Token);
        try
        {
            var address = server.Urls.Single();
            foreach (var config in new[]
            {
                new Dictionary<string, string?>(),
                new Dictionary<string, string?> { ["SUBSCHECK_API_URL"] = address },
                new Dictionary<string, string?> { ["SUBSCHECK_API_KEY"] = key }
            })
            {
                using var unconfigured = new SubsCheckClient(ConnectionChecks.CreateStore(
                    directory,
                    new ConfigurationBuilder().AddInMemoryCollection(config).Build()));
                var result = await unconfigured.CallAsync("status", false, deadline.Token);
                Check(!unconfigured.Configured && result is IStatusCodeHttpResult { StatusCode: 503 },
                    "Incomplete subs-check configuration returns 503 without an HTTP request");
            }
            Check(requests.IsEmpty, "Unconfigured subs-check calls stay local");
            using var client = new SubsCheckClient(ConnectionChecks.CreateStore(
                directory,
                new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SUBSCHECK_API_URL"] = address,
                ["SUBSCHECK_API_KEY"] = key,
                ["SUBSCHECK_WEB_URL"] = address + "/admin"
            }).Build()));
            Check(client.Configured && client.WebUrl == address + "/admin", "Configured subs-check URLs are retained");
            foreach (var call in calls)
            {
                var result = await client.CallAsync(call.Endpoint, call.Post, deadline.Token);
                Check(result is IValueHttpResult<JsonElement> value && value.Value.GetRawText() == call.Body,
                    "subs-check success preserves upstream JSON without fabricating node data");
            }
            Check(requests.ToArray().SequenceEqual(calls.Select(c => (c.Post ? "POST" : "GET", "/api/" + c.Endpoint, key))),
                "subs-check uses the fixed status/results/trigger-check paths and X-API-Key");
            foreach (var code in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.ServiceUnavailable,
                HttpStatusCode.Found, HttpStatusCode.TemporaryRedirect })
            {
                Volatile.Write(ref responseStatus, (int)code);
                try
                {
                    await client.CallAsync(code == HttpStatusCode.TemporaryRedirect ? "trigger-check" : "status",
                        code == HttpStatusCode.TemporaryRedirect, deadline.Token);
                    throw new Exception("subs-check HTTP failure or redirect must not become a successful response");
                }
                catch (HttpRequestException ex)
                {
                    Check(ex.StatusCode == code, "subs-check failures and redirects preserve the upstream HTTP status");
                }
            }
            Check(Volatile.Read(ref redirectHits) == 0 && requests.All(r => r.Path != "/redirect-target"),
                "subs-check redirects are not followed and cannot forward the API key to their target");
        }
        finally
        {
            using var stopDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await server.StopAsync(stopDeadline.Token);
            Directory.Delete(directory, recursive: true);
        }
        Console.WriteLine("subs-check loopback protocol checks passed.");
    }
}
