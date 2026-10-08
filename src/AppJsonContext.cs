using System.Text.Json.Serialization;

namespace ProxyNodeHub;

// Keep the desktop persistence format and kernel camelCase response compatibility.
[JsonSourceGenerationOptions(IncludeFields = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(CacheData), TypeInfoPropertyName = "CacheData")]
[JsonSerializable(typeof(List<RepoInfo>), TypeInfoPropertyName = "RepoList")]
[JsonSerializable(typeof(SubscriptionLink), TypeInfoPropertyName = "SubscriptionLink")]
[JsonSerializable(typeof(GitHubCommit), TypeInfoPropertyName = "GitHubCommit")]
[JsonSerializable(typeof(CommitDetail), TypeInfoPropertyName = "CommitDetail")]
[JsonSerializable(typeof(GitHubUser), TypeInfoPropertyName = "GitHubUser")]
[JsonSerializable(typeof(BatchRepoResult), TypeInfoPropertyName = "BatchRepoResult")]
[JsonSerializable(typeof(GitHubRepo), TypeInfoPropertyName = "GitHubRepo")]
[JsonSerializable(typeof(AppSettings), TypeInfoPropertyName = "AppSettings")]
[JsonSerializable(typeof(GitHubVariables), TypeInfoPropertyName = "GitHubVariables")]
[JsonSerializable(typeof(KnownRepoConfig), TypeInfoPropertyName = "KnownRepoConfig")]
[JsonSerializable(typeof(SpeedTestSnapshot), TypeInfoPropertyName = "SpeedTestSnapshot")]
[JsonSerializable(typeof(SpeedTestStatus), TypeInfoPropertyName = "SpeedTestStatus")]
[JsonSerializable(typeof(List<SpeedStat>), TypeInfoPropertyName = "SpeedStatList")]
internal partial class AppJsonContext : JsonSerializerContext;
