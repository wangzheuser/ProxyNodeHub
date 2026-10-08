using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ProxyNodeHub;

/// <summary>已知仓库配置模型</summary>
public class KnownRepoConfig
{
    public List<KnownRepoEntry> known_repos { get; set; } = new();
}

public class KnownRepoEntry
{
    public string full_name { get; set; } = "";
    public string path { get; set; } = "";
    public string type { get; set; } = "Base64";
    public bool is_absolute_url { get; set; }
}

/// <summary>已知仓库配置加载器 (从 JSON 文件动态加载)</summary>
public static class KnownRepoLoader
{
    private static readonly string ConfigPath = Path.Combine(
        AppContext.BaseDirectory, "known_repos.json");

    private static Dictionary<string, List<KnownRepoEntry>>? _cache;
    private static readonly object Gate = new();

    /// <summary>加载已知仓库映射 (文件名 → 条目列表)</summary>
    private static Dictionary<string, List<KnownRepoEntry>> LoadConfig()
    {
        lock (Gate)
        {
            if (_cache != null) return _cache;
            var next = new Dictionary<string, List<KnownRepoEntry>>(StringComparer.OrdinalIgnoreCase);
            var config = JsonSerializer.Deserialize(File.ReadAllText(ConfigPath), CoreJsonContext.Default.KnownRepoConfig)
                ?? throw new InvalidDataException("Invalid known repository configuration.");
            foreach (var entry in config.known_repos)
            {
                if (string.IsNullOrEmpty(entry.full_name)) continue;
                if (!next.TryGetValue(entry.full_name, out var list))
                {
                    list = new List<KnownRepoEntry>();
                    next[entry.full_name] = list;
                }
                list.Add(entry);
            }
            return _cache = next;
        }
    }

    /// <summary>获取指定仓库的已知订阅链接</summary>
    public static List<SubscriptionLink> GetKnownLinks(string fullName, string branch = "main")
    {
        var config = LoadConfig();
        if (!config.TryGetValue(fullName, out var entries))
            return new List<SubscriptionLink>();

        return entries.Select(e => new SubscriptionLink
        {
            Name = e.path,
            Url = e.is_absolute_url ? e.path : GitHubService.RawUrl(fullName, e.path, branch),
            Type = e.type,
            NodeCount = -1,
            IsValid = true,
            IsAnalyzed = false
        }).ToList();
    }

    /// <summary>强制重新加载配置 (编辑后调用)</summary>
    public static void Reload() { lock (Gate) _cache = null; }
}
