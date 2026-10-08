namespace ProxyNodeHub;

public sealed record AnalysisResult(bool CommitsSucceeded, bool SubscriptionsSucceeded);

// Both hosts use this analysis pipeline; each host owns scheduling and UI state.
public static class DiscoveryEngine
{
    public static string[] Queries(int days = 7)
    {
        var since = DateTime.UtcNow.AddDays(-days).ToString("yyyy-MM-dd");
        return new[] { "v2ray nodes", "free proxy subscription", "clash nodes", "免费节点",
            "v2ray 订阅", "free v2ray", "trojan nodes", "sing-box subscription" }
            .Select(q => $"{q} pushed:>{since}").ToArray();
    }

    public static async Task<AnalysisResult> AnalyzeAsync(GitHubService github, RepoInfo repo, CancellationToken ct,
        Action<string>? log = null, FeatureLibrary? library = null)
    {
        void Log(string message) => log?.Invoke(message);
        const int maxRetries = 3;
        var commitsSucceeded = false;
        var subscriptionsSucceeded = false;

        Log($"┌─ {repo.FullName}");

        // --- 分析提交历史 ---
        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                if (attempt > 0)
                {
                    Log($"│  [重试 {attempt}] 获取提交历史...");
                    await Task.Delay(800 * attempt, ct);
                }
                Log($"│  获取提交历史...");
                var commits = await github.GetRecentCommitsAsync(repo.FullName, 7, ct);
                repo.CommitsLast7Days = commits.Count;
                repo.DistinctActiveDays = GitHubAnalyzer.CountDistinctDays(commits);
                repo.ProcessingType = GitHubAnalyzer.DetectProcessingType(commits);
                repo.CommitsAnalyzed = true;
                commitsSucceeded = true;
                Log($"│  ✓ 提交: {commits.Count} 次 (活跃 {repo.DistinctActiveDays} 天) [{repo.ProcessingType}]");
                break;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or OperationCanceledException)
            {
                if (attempt < maxRetries)
                {
                    Log($"│  ⚠ 提交获取失败 ({ex.Message}), 重试 {attempt + 1}/{maxRetries}...");
                }
                else
                {
                    Log($"│  ✗ 提交分析失败: {ex.Message}");
                    repo.CommitsLast7Days = 0;
                    repo.ProcessingType = "分析失败";
                    repo.CommitsAnalyzed = true;
                }
            }
        }

        // --- 分析订阅链接 ---
        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                if (attempt > 0)
                {
                    Log($"│  [重试 {attempt}] 获取订阅链接...");
                    await Task.Delay(800 * attempt, ct);
                }
                Log($"│  探测订阅链接...");
                repo.Links = await SubscriptionFinder.FindLinksAsync(github, repo.FullName, ct, msg => Log(msg), repo.Branch, library);
                repo.TotalNodes = GitHubAnalyzer.BestNodeCount(repo);
                subscriptionsSucceeded = true;

                if (repo.Links.Count > 0)
                {
                    var validLinks = repo.Links.Where(l => l.NodeCount > 0).ToList();
                    if (validLinks.Count > 0)
                    {
                        Log($"│  ✓ 链接: {repo.Links.Count} 条 (有效 {validLinks.Count}), 节点: {repo.TotalNodes}");
                        foreach (var l in validLinks)
                            Log($"│    ● {l.Name} ({l.Type}) = {l.NodeCount} 节点");
                    }
                    else
                    {
                        Log($"│  ○ 链接: {repo.Links.Count} 条 (均未验证到节点)");
                    }
                }
                else
                {
                    Log($"│  ✗ 未找到订阅链接");
                }
                break;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or OperationCanceledException)
            {
                if (attempt < maxRetries)
                {
                    Log($"│  ⚠ 链接获取失败 ({ex.Message}), 重试 {attempt + 1}/{maxRetries}...");
                }
                else
                {
                    Log($"│  ✗ 链接分析失败: {ex.Message}");
                    repo.Links = new List<SubscriptionLink>();
                    repo.TotalNodes = 0;
                }
            }
        }

        repo.Score = GitHubAnalyzer.CalculateScore(repo);
        Log($"└─ 完成 [评分: {repo.Score}]");
        return new(commitsSucceeded, subscriptionsSucceeded);
    }
}
