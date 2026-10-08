using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using ProxyNodeHub;

namespace ProxyNodeHub.Web;

public sealed record CheckerConnection(string? ApiUrl, string? ApiKey, string? WebUrl)
{
    public bool Configured => !string.IsNullOrWhiteSpace(ApiUrl) && !string.IsNullOrWhiteSpace(ApiKey);
    public override string ToString() => $"CheckerConnection {{ Configured = {Configured} }}";
}

// This store is the only owner of Web credential overrides. Neither the saved
// document nor a resolved credential snapshot is returned by an HTTP endpoint.
public sealed class ConnectionStore
{
    private readonly string path;
    private readonly IDataProtector protector;
    private readonly string? environmentToken;
    private readonly CheckerConnection environmentChecker;
    private readonly object gate = new();
    private SavedConnections saved;

    public ConnectionStore(string directory, IConfiguration config, IDataProtectionProvider protection)
    {
        path = Path.Combine(directory, "connections.protected");
        protector = protection.CreateProtector("ProxyNodeHub.Connections.v1");
        environmentToken = string.IsNullOrWhiteSpace(config["GITHUB_TOKEN"]) ? null : ValidateSecret(config["GITHUB_TOKEN"]);
        environmentChecker = new(ValidateUrl(config["SUBSCHECK_API_URL"]),
            string.IsNullOrWhiteSpace(config["SUBSCHECK_API_KEY"]) ? null : ValidateSecret(config["SUBSCHECK_API_KEY"]),
            ValidateUrl(config["SUBSCHECK_WEB_URL"]));
        saved = File.Exists(path)
            ? JsonSerializer.Deserialize<SavedConnections>(protector.Unprotect(File.ReadAllText(path)))
                ?? throw new InvalidDataException("Invalid saved connection settings.")
            : new();
        ValidateMode(saved.GitHubMode);
        ValidateMode(saved.CheckerMode);
        if (saved.GitHubMode == "custom") _ = ValidateSecret(saved.GitHubToken);
        if (saved.CheckerMode == "custom")
        {
            if (ValidateUrl(saved.CheckerApiUrl) is null) throw new InvalidDataException("Missing checker address.");
            _ = ValidateSecret(saved.CheckerApiKey);
            _ = ValidateUrl(saved.CheckerWebUrl);
        }
    }

    public string? GetGitHubToken()
    {
        lock (gate) return saved.GitHubMode switch
        { "custom" => saved.GitHubToken, "disabled" => null, _ => environmentToken };
    }

    public CheckerConnection GetChecker()
    {
        lock (gate) return saved.CheckerMode switch
        {
            "custom" => new(saved.CheckerApiUrl, saved.CheckerApiKey, saved.CheckerWebUrl),
            "disabled" => new(null, null, null),
            _ => environmentChecker
        };
    }

    public object GetPublicState()
    {
        lock (gate)
        {
            var checker = GetChecker();
            return new
            {
                github = new { source = saved.GitHubMode, configured = GetGitHubToken() is not null },
                checker = new { source = saved.CheckerMode, configured = checker.Configured,
                    apiUrl = checker.ApiUrl, webUrl = checker.WebUrl, keyConfigured = checker.ApiKey is not null }
            };
        }
    }

    public void SetGitHub(string mode, string? token)
    {
        ValidateMode(mode);
        var value = mode == "custom" ? ValidateSecret(token) : null;
        lock (gate) Commit(new SavedConnections
        {
            GitHubMode = mode, GitHubToken = value, CheckerMode = saved.CheckerMode,
            CheckerApiUrl = saved.CheckerApiUrl, CheckerApiKey = saved.CheckerApiKey, CheckerWebUrl = saved.CheckerWebUrl
        });
    }

    public void SetChecker(string mode, string? apiUrl, string? apiKey, string? webUrl)
    {
        ValidateMode(mode);
        lock (gate)
        {
            var url = mode == "custom" ? ValidateUrl(apiUrl) : null;
            if (mode == "custom" && url is null) throw new ArgumentException("请输入检测器 API 地址。");
            var key = mode != "custom" ? null : ValidateSecret(string.IsNullOrWhiteSpace(apiKey)
                && saved.CheckerMode == "custom" ? saved.CheckerApiKey : apiKey);
            // Do not forward a saved key to a newly entered host without an explicit replacement.
            if (mode == "custom" && string.IsNullOrWhiteSpace(apiKey) && saved.CheckerApiUrl != url)
                throw new ArgumentException("更改 API 地址时请重新输入密钥，避免将旧密钥发送给其他服务。");
            Commit(new SavedConnections
            {
                GitHubMode = saved.GitHubMode, GitHubToken = saved.GitHubToken,
                CheckerMode = mode, CheckerApiUrl = url, CheckerApiKey = key,
                CheckerWebUrl = mode == "custom" ? ValidateUrl(webUrl) : null
            });
        }
    }

    private void Commit(SavedConnections next)
    {
        AtomicFile.Write(path, protector.Protect(JsonSerializer.Serialize(next)));
        saved = next;
    }

    private static void ValidateMode(string? mode)
    {
        if (mode is not ("environment" or "custom" or "disabled"))
            throw new ArgumentException("配置来源必须为 environment、custom 或 disabled。");
    }

    private static string ValidateSecret(string? value)
    {
        var secret = value?.Trim();
        if (secret is not { Length: >= 1 and <= 1024 } || secret.Any(c => c <= 32 || c >= 127))
            throw new ArgumentException("密钥需为1–1024个非空白 ASCII 字符。");
        return secret;
    }

    private static string? ValidateUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.Length > 2048 || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new ArgumentException("检测器地址必须为不含凭据、查询和片段的 HTTP(S) URL。");
        return uri.AbsoluteUri.TrimEnd('/');
    }

    private sealed class SavedConnections
    {
        public string GitHubMode { get; init; } = "environment";
        public string? GitHubToken { get; init; }
        public string CheckerMode { get; init; } = "environment";
        public string? CheckerApiUrl { get; init; }
        public string? CheckerApiKey { get; init; }
        public string? CheckerWebUrl { get; init; }
    }
}
