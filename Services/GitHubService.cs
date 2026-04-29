using System.Net.Http.Headers;
using System.Text.Json;
using IntitechApi.Cache;
using IntitechApi.Models;
using Microsoft.Extensions.Caching.Memory;

namespace IntitechApi.Services;

public class GitHubService(
    HttpClient httpClient,
    IMemoryCache cache,
    IConfiguration config,
    ILogger<GitHubService> logger)
{
    private readonly string _username = config["GitHub:Username"] ?? "intitech";
    private readonly string? _token = config["GitHub:Token"];

    private void SetAuthHeaders()
    {
        httpClient.DefaultRequestHeaders.UserAgent.Clear();
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("IntitechApi/1.0");
        httpClient.DefaultRequestHeaders.Accept.Clear();
        httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        if (!string.IsNullOrEmpty(_token))
        {
            httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _token);
        }
    }

    public async Task<GitHubSummary> GetSummaryAsync()
    {
        if (cache.TryGetValue(CacheKeys.GitHubSummary, out GitHubSummary? cached) && cached is not null)
            return cached;

        SetAuthHeaders();

        var profile = await FetchProfileAsync();
        var repos = await FetchReposAsync();
        var activity = await ComputeActivityAsync(repos);
        var languages = await FetchLanguagesAsync(repos);

        var topRepos = repos
            .Where(r => !r.IsForked)
            .OrderByDescending(r => r.Stars)
            .ThenByDescending(r => r.UpdatedAt)
            .Take(6)
            .ToList();

        var summary = new GitHubSummary(profile, topRepos, activity, languages, DateTime.UtcNow);

        cache.Set(CacheKeys.GitHubSummary, summary, CacheTTL.GitHub);

        return summary;
    }

    private async Task<GitHubProfile> FetchProfileAsync()
    {
        var response = await httpClient.GetAsync($"https://api.github.com/users/{_username}");
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        return new GitHubProfile(
            Username: root.GetProperty("login").GetString()!,
            DisplayName: root.TryGetProperty("name", out var name) ? name.GetString() ?? _username : _username,
            Bio: root.TryGetProperty("bio", out var bio) ? bio.GetString() ?? "" : "",
            AvatarUrl: root.GetProperty("avatar_url").GetString()!,
            PublicRepos: root.GetProperty("public_repos").GetInt32(),
            Followers: root.GetProperty("followers").GetInt32(),
            Following: root.GetProperty("following").GetInt32(),
            ProfileUrl: root.GetProperty("html_url").GetString()!
        );
    }

    private async Task<List<GitHubRepo>> FetchReposAsync()
    {
        if (cache.TryGetValue(CacheKeys.GitHubRepos, out List<GitHubRepo>? cachedRepos) && cachedRepos is not null)
            return cachedRepos;

        var repos = new List<GitHubRepo>();
        int page = 1;

        while (true)
        {
            var response = await httpClient.GetAsync(
                $"https://api.github.com/users/{_username}/repos?per_page=100&page={page}&sort=updated");

            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var arr = doc.RootElement;

            if (arr.GetArrayLength() == 0) break;

            foreach (var repo in arr.EnumerateArray())
            {
                var topics = new List<string>();
                if (repo.TryGetProperty("topics", out var topicsEl))
                    foreach (var t in topicsEl.EnumerateArray())
                        topics.Add(t.GetString()!);

                repos.Add(new GitHubRepo(
                    Name: repo.GetProperty("name").GetString()!,
                    Description: repo.TryGetProperty("description", out var desc) ? desc.GetString() : null,
                    Url: repo.GetProperty("html_url").GetString()!,
                    Language: repo.TryGetProperty("language", out var lang) ? lang.GetString() : null,
                    Stars: repo.GetProperty("stargazers_count").GetInt32(),
                    Forks: repo.GetProperty("forks_count").GetInt32(),
                    IsForked: repo.GetProperty("fork").GetBoolean(),
                    UpdatedAt: repo.GetProperty("updated_at").GetDateTime(),
                    Topics: topics
                ));
            }

            if (arr.GetArrayLength() < 100) break;
            page++;
        }

        cache.Set(CacheKeys.GitHubRepos, repos, CacheTTL.GitHub);
        return repos;
    }

    private async Task<GitHubActivity> ComputeActivityAsync(List<GitHubRepo> repos)
    {
        // Use events API for recent commit activity
        var response = await httpClient.GetAsync(
            $"https://api.github.com/users/{_username}/events?per_page=100");

        var events = new List<(DateTime Date, int Count)>();
        int commitsThisWeek = 0;
        int commitsThisMonth = 0;
        int totalThisYear = 0;

        var now = DateTime.UtcNow;
        var weekAgo = now.AddDays(-7);
        var monthAgo = now.AddMonths(-1);
        var yearStart = new DateTime(now.Year, 1, 1);

        if (response.IsSuccessStatusCode)
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

            foreach (var ev in doc.RootElement.EnumerateArray())
            {
                if (ev.GetProperty("type").GetString() != "PushEvent") continue;

                var createdAt = ev.GetProperty("created_at").GetDateTime();
                var commitCount = 0;

                if (ev.TryGetProperty("payload", out var payload) &&
                    payload.TryGetProperty("commits", out var commits))
                    commitCount = commits.GetArrayLength();

                if (createdAt >= yearStart) totalThisYear += commitCount;
                if (createdAt >= monthAgo) commitsThisMonth += commitCount;
                if (createdAt >= weekAgo) commitsThisWeek += commitCount;

                events.Add((createdAt, commitCount));
            }
        }

        // Build recent 30-day contributions
        var recentContributions = Enumerable.Range(0, 30)
            .Select(i => DateOnly.FromDateTime(now.AddDays(-i)))
            .Select(date => new DailyContribution(
                date,
                events.Where(e => DateOnly.FromDateTime(e.Date) == date).Sum(e => e.Count)))
            .OrderBy(d => d.Date)
            .ToList();

        // Simple streak calc from recentContributions (reversed = most recent first)
        var streak = 0;
        foreach (var day in recentContributions.OrderByDescending(d => d.Date))
        {
            if (day.Count > 0) streak++;
            else break;
        }

        return new GitHubActivity(
            TotalCommitsThisYear: totalThisYear,
            CommitsThisWeek: commitsThisWeek,
            CommitsThisMonth: commitsThisMonth,
            CurrentStreak: streak,
            LongestStreak: streak, // simplified — events API only goes back 90 days
            RecentContributions: recentContributions
        );
    }

    private async Task<LanguageBreakdown> FetchLanguagesAsync(List<GitHubRepo> repos)
    {
        var totalBytes = new Dictionary<string, int>();

        // Fetch languages for top 10 non-forked repos to avoid rate limiting
        var targetRepos = repos.Where(r => !r.IsForked).Take(10).ToList();

        var tasks = targetRepos.Select(async repo =>
        {
            try
            {
                var res = await httpClient.GetAsync(
                    $"https://api.github.com/repos/{_username}/{repo.Name}/languages");
                if (!res.IsSuccessStatusCode) return;

                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
                foreach (var lang in doc.RootElement.EnumerateObject())
                {
                    var bytes = lang.Value.GetInt32();
                    lock (totalBytes)
                    {
                        totalBytes.TryAdd(lang.Name, 0);
                        totalBytes[lang.Name] += bytes;
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning("Failed to fetch languages for {Repo}: {Ex}", repo.Name, ex.Message);
            }
        });

        await Task.WhenAll(tasks);

        var grandTotal = totalBytes.Values.Sum();
        var percentages = totalBytes
            .OrderByDescending(kv => kv.Value)
            .Take(8)
            .ToDictionary(
                kv => kv.Key,
                kv => grandTotal > 0 ? Math.Round((double)kv.Value / grandTotal * 100, 1) : 0);

        return new LanguageBreakdown(percentages, totalBytes);
    }
}
