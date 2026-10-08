using System.Text.Json.Serialization;

namespace ProxyNodeHub;

[JsonSourceGenerationOptions(IncludeFields = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(GitHubRepo))]
[JsonSerializable(typeof(KnownRepoConfig))]
internal partial class CoreJsonContext : JsonSerializerContext;
