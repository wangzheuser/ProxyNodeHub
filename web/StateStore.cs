using System.Text.Json;
using ProxyNodeHub;

namespace ProxyNodeHub.Web;

public sealed record DiscoverySettings(int RefreshHours = 6, int RepoCount = 10,
    int InactiveDays = 7, bool AutoRefresh = true, int AnalysisConcurrency = 1,
    bool SkipSearched = false, bool SkipFavorites = false, int SearchHistoryDays = 1,
    int LogRetentionDays = 7, string DownloadMirror = "")
{
    public void Validate()
    {
        if (RefreshHours is < 1 or > 24 || RepoCount is < 1 or > 100 || InactiveDays is < 1 or > 60 ||
            AnalysisConcurrency is < 1 or > 10 || SearchHistoryDays is < 1 or > 30 || LogRetentionDays is < 1 or > 30)
            throw new ArgumentException("更新间隔需为1–24小时，仓库数1–100，活跃范围1–60天，并发1–10，记忆/日志保留1–30天。");
        if (DownloadMirror is null || (DownloadMirror != "auto" && DownloadMirror.Length > 0 &&
            !GitHubService.PublicMirrors.Any(m => m.Name == DownloadMirror)))
            throw new ArgumentException("请选择直连、自动或列表中的固定镜像。");
    }
}

public sealed record DiscoverySnapshot(DateTimeOffset GeneratedAt, RepoInfo[] Repositories,
    string[] Subscriptions, int FailedSearches = 0, int FailedRepositories = 0, int FailedDownloads = 0)
{
    public void Validate()
    {
        if (GeneratedAt == default || Repositories is null || Subscriptions is not { Length: > 0 } ||
            Subscriptions.Any(u => !GitHubService.IsAllowedSubscription(u)) ||
            Repositories.Any(r => r is null || string.IsNullOrWhiteSpace(r.FullName) || r.Links is null))
            throw new InvalidDataException("有效订阅快照为空或格式错误，拒绝覆盖。");
    }
}

public sealed record LogEntry(DateTimeOffset At, string Message);
public sealed record RunHistory(DateTimeOffset StartedAt, DateTimeOffset FinishedAt, bool Success,
    int Subscriptions, string? Error, LogEntry[] Logs);
public sealed record SearchMemory(string FullName, DateTimeOffset SearchedAt);
public sealed record WebState(DiscoverySettings Settings, DiscoverySnapshot? Current,
    RepoInfo[] Favorites, RunHistory[] History)
{
    public SearchMemory[] SearchHistory { get; init; } = [];
}

// One owner commits settings, favorites, run history and the published snapshot.
// Arrays and repository models are treated as immutable after publication.
public sealed class StateStore
{
    private readonly string path;
    private readonly object gate = new();
    private WebState state;
    public static readonly JsonSerializerOptions JsonOptions = new()
    { IncludeFields = true, PropertyNameCaseInsensitive = true };
    public WebState State => Volatile.Read(ref state);
    public bool IsFresh => State.Current is { } s &&
        DateTimeOffset.UtcNow - s.GeneratedAt < TimeSpan.FromHours(25);

    public StateStore(string directory, DiscoverySettings defaults)
    {
        defaults.Validate();
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "state.json");
        if (File.Exists(path))
        {
            state = JsonSerializer.Deserialize<WebState>(File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidDataException("Invalid Web state.");
            if (state.Settings is null || state.Favorites is null || state.History is null || state.SearchHistory is null ||
                state.Favorites.Any(r => r is null || r.Links is null) ||
                state.History.Any(h => h is null || h.Logs is null) ||
                state.SearchHistory.Any(h => h is null || string.IsNullOrWhiteSpace(h.FullName) || h.SearchedAt == default))
                throw new InvalidDataException("Invalid Web state collections.");
            state.Settings.Validate();
            state.Current?.Validate();
        }
        else
        {
            // Explicit one-time import from the previous headless service.
            // Keep current.json untouched for rollback; state.json is authoritative thereafter.
            var previous = Path.Combine(directory, "current.json");
            var snapshot = File.Exists(previous)
                ? JsonSerializer.Deserialize<DiscoverySnapshot>(File.ReadAllText(previous), JsonOptions)
                    ?? throw new InvalidDataException("Invalid legacy snapshot.")
                : null;
            snapshot?.Validate();
            state = new(defaults, snapshot, [], []);
            AtomicFile.Write(path, JsonSerializer.Serialize(state, JsonOptions));
        }
    }

    private void Commit(WebState next)
    {
        AtomicFile.Write(path, JsonSerializer.Serialize(next, JsonOptions));
        Volatile.Write(ref state, next);
    }

    public void SaveSettings(DiscoverySettings settings)
    {
        settings.Validate();
        lock (gate) Commit(state with { Settings = settings, History = RetainLogs(state.History, settings) });
    }

    public void SetFavorite(string fullName, bool favorite) => SetFavorites([fullName], favorite);

    private static string[] ValidateNames(string[] fullNames)
    {
        if (fullNames is null || fullNames.Length == 0 ||
            fullNames.Any(n => string.IsNullOrWhiteSpace(n) || n.Length > 200))
            throw new ArgumentException("请选择已有仓库，名称不得为空或超过200字符。");
        return fullNames.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static RepoInfo[] SelectRepositories(WebState source, string[] fullNames)
    {
        var names = ValidateNames(fullNames);
        var known = (source.Current?.Repositories ?? []).Concat(source.Favorites)
            .DistinctBy(r => r.FullName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(r => r.FullName, StringComparer.OrdinalIgnoreCase);
        return names.Select(n => known.TryGetValue(n, out var repo) ? repo :
            throw new KeyNotFoundException("只能选择已有发现结果或收藏中的仓库。")).ToArray();
    }

    public void SetFavorites(string[] fullNames, bool favorite)
    {
        var names = ValidateNames(fullNames).ToHashSet(StringComparer.OrdinalIgnoreCase);
        lock (gate)
        {
            var selected = favorite ? SelectRepositories(state, fullNames) : [];
            var list = state.Favorites.Where(r => !names.Contains(r.FullName)).Concat(selected).ToArray();
            if (list.Length > 200) throw new ArgumentException("收藏最多200个仓库，请先移除不需要的项目。");
            Commit(state with { Favorites = list });
        }
    }

    public void FinishRun(DiscoverySnapshot? snapshot, RunHistory run, string[]? searched = null, bool merge = false)
    {
        snapshot?.Validate();
        if (run.Success != (snapshot is not null))
            throw new InvalidDataException("Run outcome does not match its snapshot.");
        lock (gate)
        {
            var next = snapshot is not null && merge ? MergeSnapshot(state, snapshot) : state with { Current = snapshot ?? state.Current };
            Commit(WithRun(next, run, searched ?? []));
        }
    }

    public void FinishRecheck(RepoInfo[] updated, RunHistory run)
    {
        if (run.Success != (updated.Length > 0)) throw new InvalidDataException("重检状态与结果不一致。");
        lock (gate)
        {
            var next = state;
            if (updated.Length > 0)
            {
                var urls = updated.SelectMany(r => r.Links).Where(l => l.IsValid && l.IsAnalyzed && l.NodeCount > 0)
                    .Select(l => l.Url).Distinct(StringComparer.Ordinal).ToArray();
                var snapshot = new DiscoverySnapshot(run.FinishedAt, updated, urls);
                snapshot.Validate();
                next = MergeSnapshot(state, snapshot);
            }
            Commit(WithRun(next, run, updated.Select(r => r.FullName).ToArray()));
        }
    }

    private static WebState MergeSnapshot(WebState source, DiscoverySnapshot snapshot)
    {
        var replacements = snapshot.Repositories.ToDictionary(r => r.FullName, StringComparer.OrdinalIgnoreCase);
        var previous = source.Current?.Repositories ?? [];
        var untouched = previous.Where(r => !replacements.ContainsKey(r.FullName)).ToArray();
        var replacedUrls = previous.Where(r => replacements.ContainsKey(r.FullName)).SelectMany(r => r.Links).Select(l => l.Url).ToHashSet(StringComparer.Ordinal);
        var untouchedUrls = untouched.SelectMany(r => r.Links).Select(l => l.Url).ToHashSet(StringComparer.Ordinal);
        var repos = untouched.Concat(snapshot.Repositories).OrderByDescending(r => r.Score).ToArray();
        return source with
        {
            Current = snapshot with { Repositories = repos,
                Subscriptions = (source.Current?.Subscriptions ?? []).Where(u => !replacedUrls.Contains(u) || untouchedUrls.Contains(u))
                    .Concat(snapshot.Subscriptions).Distinct(StringComparer.Ordinal).ToArray() },
            Favorites = source.Favorites.Select(r => replacements.GetValueOrDefault(r.FullName) ?? r).ToArray()
        };
    }

    private static RunHistory[] RetainLogs(IEnumerable<RunHistory> history, DiscoverySettings settings) =>
        history.Select(h => h with { Logs = h.Logs.Where(l => l.At >= DateTimeOffset.UtcNow.AddDays(-settings.LogRetentionDays)).ToArray() }).ToArray();

    private static WebState WithRun(WebState source, RunHistory run, string[] searched)
    {
        var memory = searched.Select(n => new SearchMemory(n, run.FinishedAt)).Concat(source.SearchHistory)
            .Where(h => h.SearchedAt >= run.FinishedAt.AddDays(-30))
            .DistinctBy(h => h.FullName, StringComparer.OrdinalIgnoreCase).ToArray();
        return source with { History = RetainLogs(new[] { run }.Concat(source.History.Take(19)), source.Settings), SearchHistory = memory };
    }

    public SearchMemory[] GetSearchHistory()
    {
        var source = State;
        return source.SearchHistory.Where(h => h.SearchedAt >= DateTimeOffset.UtcNow.AddDays(-source.Settings.SearchHistoryDays)).ToArray();
    }

    public static HashSet<string> ExplorationExclusions(WebState source, DateTimeOffset now)
    {
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (source.Settings.SkipSearched)
            excluded.UnionWith(source.SearchHistory.Where(h => h.SearchedAt > now.AddDays(-source.Settings.SearchHistoryDays)).Select(h => h.FullName));
        if (source.Settings.SkipFavorites) excluded.UnionWith(source.Favorites.Select(r => r.FullName));
        return excluded;
    }

    public void ClearSearchHistory() { lock (gate) Commit(state with { SearchHistory = [] }); }

    public LogEntry[] GetLogs(DateTimeOffset startedAt)
    {
        var source = State;
        var run = source.History.FirstOrDefault(h => h.StartedAt == startedAt)
            ?? throw new KeyNotFoundException("找不到该轮运行记录。");
        return run.Logs.Where(l => l.At >= DateTimeOffset.UtcNow.AddDays(-source.Settings.LogRetentionDays)).ToArray();
    }

    public void ClearLogs()
    {
        lock (gate) Commit(state with { History = state.History.Select(h => h with { Logs = [] }).ToArray() });
    }
}
