using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using ProxyNodeHub.Web;

public static class ConnectionChecks
{
    public static ConnectionStore CreateStore(string directory, IConfiguration? config = null) => new(directory,
        config ?? new ConfigurationBuilder().Build(), DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(directory, "keys"))));

    public static async Task RunAsync()
    {
        static void Check(bool value, string label) { if (!value) throw new Exception(label); }
        static void Invalid(Action action)
        {
            try { action(); }
            catch (ArgumentException) { return; }
            throw new Exception("Invalid connection input was accepted.");
        }
        var root = Directory.CreateTempSubdirectory("proxynodehub-connections-").FullName;
        try
        {
            const string envToken = "synthetic-env-token";
            const string webToken = "synthetic-web-token";
            const string envKey = "synthetic-env-key";
            const string webKey = "synthetic-web-key";
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GITHUB_TOKEN"] = envToken, ["SUBSCHECK_API_URL"] = "http://127.0.0.1:9000",
                ["SUBSCHECK_API_KEY"] = envKey, ["SUBSCHECK_WEB_URL"] = "http://127.0.0.1:9000/admin"
            }).Build();
            var store = CreateStore(root, config);
            Check(store.GetGitHubToken() == envToken && store.GetChecker().ApiKey == envKey, "Environment compatibility");
            store.SetGitHub("custom", webToken);
            store.SetChecker("custom", "http://127.0.0.1:9001", webKey, null);
            store = CreateStore(root, config);
            Check(store.GetGitHubToken() == webToken && store.GetChecker().ApiKey == webKey, "Encrypted settings survive restart");
            var publicJson = JsonSerializer.Serialize(store.GetPublicState());
            foreach (var secret in new[] { envToken, webToken, envKey, webKey })
            {
                Check(!publicJson.Contains(secret), "Public settings must not expose credentials");
                Check(!store.GetChecker().ToString().Contains(secret), "Credential snapshot debug output is redacted");
                foreach (var path in new[] { "connections.protected", "connections.protected.bak" })
                    Check(!File.ReadAllText(Path.Combine(root, path)).Contains(secret), "Persistent document and backup are ciphertext");
            }
            var captured = store.GetChecker();
            store.SetChecker("custom", captured.ApiUrl, "synthetic-next-key", captured.WebUrl);
            Check(captured.ApiKey == webKey && store.GetChecker().ApiKey == "synthetic-next-key", "In-flight snapshots do not change");
            store.SetChecker("custom", captured.ApiUrl, "", "https://example.test/admin");
            Check(store.GetChecker().ApiKey == "synthetic-next-key", "Empty key preserves same custom endpoint credential");
            Invalid(() => store.SetChecker("custom", "https://another.test", "", null));
            foreach (var bad in new[] { "http://user:secret@example.test", "http://example.test/?key=x", "file:///tmp/config", "http://example.test/#secret" })
                Invalid(() => store.SetChecker("custom", bad, webKey, null));
            foreach (var bad in new[] { "", "white space", "x\r\nAuthorization:y", new string('a', 1025), "中文" })
                Invalid(() => store.SetGitHub("custom", bad));
            Invalid(() => store.SetGitHub("invalid", null));
            Invalid(() => store.SetChecker("invalid", null, null, null));

            var saved = File.ReadAllText(Path.Combine(root, "connections.protected"));
            Directory.CreateDirectory(Path.Combine(root, "connections.protected.tmp"));
            var failed = false;
            try { store.SetGitHub("custom", "synthetic-failed-token"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed = true; }
            Check(failed && store.GetGitHubToken() == webToken && File.ReadAllText(Path.Combine(root, "connections.protected")) == saved,
                "Write failure preserves both disk and active settings");
            Directory.Delete(Path.Combine(root, "connections.protected.tmp"));
            await Task.WhenAll(Task.Run(() => store.SetGitHub("custom", "synthetic-concurrent-token")),
                Task.Run(() => store.SetChecker("custom", captured.ApiUrl, "synthetic-concurrent-key", null)));
            store = CreateStore(root, config);
            Check(store.GetGitHubToken() == "synthetic-concurrent-token" && store.GetChecker().ApiKey == "synthetic-concurrent-key",
                "Independent concurrent writes retain both updates");
            store.SetGitHub("disabled", null);
            store.SetChecker("disabled", null, null, null);
            store = CreateStore(root, config);
            Check(store.GetGitHubToken() is null && !store.GetChecker().Configured, "Disabled does not silently restore environment credentials");
            store.SetGitHub("environment", null);
            store.SetChecker("environment", null, null, null);
            Check(store.GetGitHubToken() == envToken && store.GetChecker().ApiKey == envKey, "Explicit environment restore");
            Invalid(() => store.SetChecker("custom", captured.ApiUrl, "", null));
            File.WriteAllText(Path.Combine(root, "connections.protected"), "corrupted ciphertext");
            var corruptRejected = false;
            try { _ = CreateStore(root, config); }
            catch (CryptographicException) { corruptRejected = true; }
            Check(corruptRejected, "Corrupt saved credentials fail explicitly rather than falling back");
        }
        finally { Directory.Delete(root, recursive: true); }
        Console.WriteLine("Connection encryption, restart, precedence, validation, failure and concurrency checks passed.");
    }
}
