using System.Text.Json;
using ProxyNodeHub;

namespace ProxyNodeHub.Web;

public sealed record AttemptStatus(bool Running, DateTimeOffset? StartedAt, string? Error,
    string Phase, int Completed = 0, int Total = 0);

public sealed class DiscoveryWorker(StateStore store, FeatureLibrary features, ConnectionStore connections,
    ILogger<DiscoveryWorker> logger) : BackgroundService
{
    private readonly object gate = new();
    private readonly SemaphoreSlim wake = new(0, 1);
    private readonly Queue<LogEntry> logs = new();
    private AttemptStatus attempt = new(false, null, null, "等待运行");
    private CancellationTokenSource? active;
    private sealed record RunRequest(bool Explore = false, RepoInfo[]? Repositories = null);
    private RunRequest? requested;
    private DateTimeOffset? lastFinished;
    public AttemptStatus Attempt { get { lock (gate) return attempt; } }
    public LogEntry[] Logs { get { lock (gate) return (logs.Count > 0 ? logs.ToArray() : store.State.History.FirstOrDefault()?.Logs ?? [])
        .Where(l => l.At >= DateTimeOffset.UtcNow.AddDays(-store.State.Settings.LogRetentionDays)).ToArray(); } }

    public DateTimeOffset? NextRunAt
    {
        get
        {
            var state = store.State;
            if (!state.Settings.AutoRefresh) return null;
            var last = state.History.FirstOrDefault()?.FinishedAt ?? state.Current?.GeneratedAt;
            lock (gate)
            {
                if (lastFinished is { } recent && (last is null || recent > last)) last = recent;
                return last is null ? DateTimeOffset.UtcNow : last.Value.AddHours(state.Settings.RefreshHours);
            }
        }
    }

    public bool RequestRun() => Enqueue(new());
    public bool RequestExplore() => Enqueue(new(Explore: true));
    public bool RequestRecheck(string[] fullNames) => Enqueue(new(Repositories: StateStore.SelectRepositories(store.State, fullNames)));

    private bool Enqueue(RunRequest request)
    {
        lock (gate)
        {
            if (attempt.Running || requested is not null) return false;
            requested = request;
            attempt = attempt with { Phase = "准备运行", Error = null };
            Signal();
            return true;
        }
    }

    public void CancelRun()
    {
        lock (gate)
        {
            requested = null;
            active?.Cancel();
            if (!attempt.Running) attempt = attempt with { Phase = "等待运行" };
        }
    }

    public LogEntry[] GetLogs(DateTimeOffset? startedAt)
    {
        lock (gate) return startedAt is null || attempt.Running && attempt.StartedAt == startedAt
            ? Logs : store.GetLogs(startedAt.Value);
    }

    public void ClearLogs()
    {
        // The same gate covers run completion so cleared lines cannot be reintroduced by a stale run snapshot.
        lock (gate) { store.ClearLogs(); logs.Clear(); }
    }

    public void SettingsChanged() { lock (gate) Signal(); }
    private void Signal() { if (wake.CurrentCount == 0) wake.Release(); }

    private void Log(string message)
    {
        var entry = new LogEntry(DateTimeOffset.UtcNow, message.Replace('\r', ' ').Replace('\n', ' '));
        lock (gate)
        {
            logs.Enqueue(entry);
            while (logs.Count > 200) logs.Dequeue();
        }
        logger.LogInformation("{Message}", entry.Message);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var due = NextRunAt;
            bool run;
            RunRequest work;
            lock (gate)
            {
                run = requested is not null || due <= DateTimeOffset.UtcNow;
                work = requested ?? new();
                if (run)
                {
                    requested = null;
                    logs.Clear();
                    attempt = new(true, DateTimeOffset.UtcNow, null, "搜索仓库");
                    active = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    active.CancelAfter(TimeSpan.FromMinutes(15));
                }
            }
            if (!run)
            {
                var delay = due is null ? TimeSpan.FromHours(24) : due.Value - DateTimeOffset.UtcNow;
                try { await wake.WaitAsync(delay > TimeSpan.Zero ? delay : TimeSpan.Zero, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                continue;
            }

            var started = Attempt.StartedAt!.Value;
            DiscoverySnapshot? snapshot = null;
            RepoInfo[]? rechecked = null;
            var searched = new System.Collections.Concurrent.ConcurrentBag<string>();
            string? error = null;
            try
            {
                var source = store.State;
                var settings = source.Settings;
                using var github = new GitHubService(connections.GetGitHubToken(), restrictPublicSources: true);
                await github.ConfigurePublicMirrorAsync(settings.DownloadMirror, active!.Token);
                void Progress(int done, int total) { lock (gate) attempt = attempt with { Phase = "分析订阅", Completed = Math.Max(attempt.Completed, done), Total = total }; }
                if (work.Repositories is { } selected)
                {
                    var analyzed = await AnalyzeRepositoriesAsync(github, selected, settings, features, Log, Progress, active.Token,
                        requireComplete: true);
                    rechecked = analyzed.Repositories;
                    if (rechecked.Length == 0) throw new InvalidDataException("选中仓库均未完成验证，保留已有来源。");
                    Log($"重新检查完成：更新 {rechecked.Length}/{selected.Length} 个仓库；未完成的来源保持不变。");
                }
                else
                {
                    snapshot = await DiscoverAsync(github, settings, features, Log, Progress, active.Token,
                        work.Explore ? StateStore.ExplorationExclusions(source, started) : null, searched.Add);
                    Log($"发现完成：{snapshot.Repositories.Length} 个仓库，{snapshot.Subscriptions.Length} 条有效订阅。");
                    if (snapshot.FailedSearches + snapshot.FailedRepositories + snapshot.FailedDownloads > 0)
                        Log($"部分来源失败：搜索 {snapshot.FailedSearches}，仓库 {snapshot.FailedRepositories}，下载 {snapshot.FailedDownloads}；已发布本轮验证通过的结果。");
                }
            }
            catch (OperationCanceledException)
            {
                error = stoppingToken.IsCancellationRequested ? "服务正在停止，保留上次结果。"
                    : active!.IsCancellationRequested ? "任务已取消或超过15分钟，保留上次结果。" : "网络请求超时，保留上次结果。";
            }
            catch (Exception ex)
            {
                error = ex is InvalidDataException ? ex.Message : $"更新失败：{ex.GetType().Name}，保留上次结果。";
            }
            if (error is not null) Log(error);
            var finished = DateTimeOffset.UtcNow;
            try
            {
                lock (gate)
                {
                    if (work.Repositories is not null)
                        store.FinishRecheck(rechecked ?? [], new(started, finished, rechecked is { Length: > 0 },
                            rechecked?.SelectMany(r => r.Links).Select(l => l.Url).Distinct().Count() ?? 0, error, Logs));
                    else store.FinishRun(snapshot, new(started, finished, snapshot is not null,
                        snapshot?.Subscriptions.Length ?? 0, error, Logs), searched.ToArray(), merge: work.Explore);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = $"结果无法保存：{ex.GetType().Name}，请检查数据卷权限和空间。";
                Log(error);
            }
            lock (gate)
            {
                lastFinished = finished;
                active!.Dispose();
                active = null;
                attempt = attempt with { Running = false, Error = error, Phase = error is null ? "更新完成" : "本轮未完成" };
            }
        }
    }

    internal static async Task<DiscoverySnapshot> DiscoverAsync(GitHubService github, DiscoverySettings settings,
        FeatureLibrary features, Action<string> log, Action<int, int> progress, CancellationToken ct,
        HashSet<string>? exclusions = null, Action<string>? analyzed = null)
    {
        var found = new Dictionary<string, GitHubRepo>(StringComparer.OrdinalIgnoreCase);
        var failedSearches = 0;
        foreach (var query in DiscoveryEngine.Queries(settings.InactiveDays))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                foreach (var repo in await github.SearchReposAsync(query, 30, ct))
                    if (!repo.Fork && !GitHubAnalyzer.IsStale(repo) && exclusions?.Contains(repo.FullName) != true)
                        found.TryAdd(repo.FullName, repo);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException)
            {
                failedSearches++;
                log($"搜索请求失败：{ex.GetType().Name}。");
            }
            // Search has a separate per-minute budget, even with a token.
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
        if (found.Count == 0) throw new InvalidDataException(exclusions is { Count: > 0 }
            ? "排除已分析或已收藏仓库后没有新来源；可重置搜索记忆或执行普通刷新。保留上次结果。"
            : "没有发现仓库，请检查网络或 GitHub API 限额；保留上次结果。");

        var candidates = found.Values.Take(settings.RepoCount).Select(GitHubAnalyzer.ToRepoInfo).ToArray();
        var result = await AnalyzeRepositoriesAsync(github, candidates, settings, features, log, progress, ct, analyzed: analyzed);
        ct.ThrowIfCancellationRequested();
        var repos = result.Repositories;
        var urls = repos.SelectMany(r => r.Links).Select(l => l.Url).Distinct(StringComparer.Ordinal).ToArray();
        if (urls.Length == 0) throw new InvalidDataException("本轮没有验证通过的订阅，保留上次有效结果。");
        return new(DateTimeOffset.UtcNow, repos, urls, failedSearches, result.Failed, github.FailedDownloads);
    }

    internal static async Task<(RepoInfo[] Repositories, int Failed)> AnalyzeRepositoriesAsync(GitHubService github,
        RepoInfo[] candidates, DiscoverySettings settings, FeatureLibrary features, Action<string> log,
        Action<int, int> progress, CancellationToken ct, bool requireComplete = false, Action<string>? analyzed = null)
    {
        settings.Validate();
        using var limit = new SemaphoreSlim(settings.AnalysisConcurrency, settings.AnalysisConcurrency);
        var completed = 0;
        var failed = 0;
        progress(0, candidates.Length);
        var results = await Task.WhenAll(candidates.Select(async original =>
        {
            await limit.WaitAsync(ct);
            try
            {
                // Analysis mutates its model: never hand it a published current/favorite instance.
                var repo = JsonSerializer.Deserialize<RepoInfo>(JsonSerializer.Serialize(original, StateStore.JsonOptions), StateStore.JsonOptions)!;
                var outcome = await DiscoveryEngine.AnalyzeAsync(github, repo, ct, log, features);
                var complete = outcome.CommitsSucceeded && outcome.SubscriptionsSucceeded;
                if (!complete) Interlocked.Increment(ref failed);
                repo.Links = repo.Links.Where(l => l.IsAnalyzed && l.IsValid && l.NodeCount > 0 && GitHubService.IsAllowedSubscription(l.Url)).ToList();
                repo.TotalNodes = GitHubAnalyzer.BestNodeCount(repo);
                analyzed?.Invoke(repo.FullName);
                return repo.Links.Count > 0 && (!requireComplete || complete) ? repo : null;
            }
            finally
            {
                progress(Interlocked.Increment(ref completed), candidates.Length);
                limit.Release();
            }
        }));
        return (results.OfType<RepoInfo>().OrderByDescending(r => r.Score).ToArray(), failed);
    }

    public override void Dispose() { wake.Dispose(); base.Dispose(); }
}
