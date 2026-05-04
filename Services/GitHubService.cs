using IntitechApi.Cache;
using IntitechApi.Models;
using Microsoft.Extensions.Caching.Memory;
using Octokit;

namespace IntitechApi.Services;

public class GitHubService
{
    private readonly GitHubClient _client;
    private readonly IMemoryCache _cache;
    private readonly ILogger<GitHubService> _logger;
    private readonly string _username;

    public GitHubService(IMemoryCache cache, IConfiguration config, ILogger<GitHubService> logger)
    {
        _cache = cache;
        _logger = logger;
        _username = config["GitHub:Username"] ?? "intisor";
        _client = new GitHubClient(new ProductHeaderValue("IntitechApi", "1.0"));

        var token = config["GitHub:Token"];
        if (!string.IsNullOrWhiteSpace(token))
        {
            _client.Credentials = new Credentials(token);
        }
    }

    public async Task<GitHubSummary> GetSummaryAsync()
    {
        if (_cache.TryGetValue(CacheKeys.GitHubSummary, out GitHubSummary? cached) && cached is not null)
            return cached;

        var profile = await FetchProfileAsync();
        var allRepos = await FetchReposAsync();
        var activity = await ComputeActivityAsync();
        var languages = await FetchLanguagesAsync(allRepos);
        var latestHash = await FetchLatestHashAsync(allRepos);

        var topRepos = allRepos
            .Where(r => !r.IsForked)
            .OrderByDescending(r => r.Stars)
            .ThenByDescending(r => r.UpdatedAt)
            .Take(6)
            .ToList();

        var portfolioRepos = allRepos
            .Where(r => r.Topics.Contains("portfolio", StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(r => r.UpdatedAt)
            .ToList();

        var summary = new GitHubSummary(
            profile with { PublicRepos = allRepos.Count }, // Update count to include private
            topRepos, 
            portfolioRepos, 
            activity, 
            languages, 
            latestHash, 
            DateTime.UtcNow
        );

        _cache.Set(CacheKeys.GitHubSummary, summary, CacheTTL.GitHub);
        return summary;
    }

    public Task<GitHubProfile> GetProfileAsync() => FetchProfileAsync();

    public Task<List<GitHubRepo>> GetReposAsync() => FetchReposAsync();

    public async Task<List<GitHubRepo>> GetTopReposAsync(int take = 6)
    {
        var repos = await FetchReposAsync();
        return repos
            .Where(r => !r.IsForked)
            .OrderByDescending(r => r.Stars)
            .ThenByDescending(r => r.UpdatedAt)
            .Take(take)
            .ToList();
    }

    public async Task<GitHubRepo?> GetRepoAsync(string repoName)
    {
        try
        {
            var repo = await _client.Repository.Get(_username, repoName);
            return await CreateRepoModelAsync(repo);
        }
        catch (NotFoundException)
        {
            return null;
        }
    }

    public Task<GitHubActivity> GetActivityAsync() => ComputeActivityAsync();

    public async Task<LanguageBreakdown> GetLanguageBreakdownAsync() => await FetchLanguagesAsync(await FetchReposAsync());

    private async Task<GitHubProfile> FetchProfileAsync()
    {
        var user = await _client.User.Get(_username);

        return new GitHubProfile(
            Username: user.Login,
            DisplayName: user.Name ?? _username,
            Bio: user.Bio ?? string.Empty,
            AvatarUrl: user.AvatarUrl,
            PublicRepos: user.PublicRepos,
            Followers: user.Followers,
            Following: user.Following,
            ProfileUrl: user.HtmlUrl
        );
    }

    private async Task<List<GitHubRepo>> FetchReposAsync()
    {
        if (_cache.TryGetValue(CacheKeys.GitHubRepos, out List<GitHubRepo>? cachedRepos) && cachedRepos is not null)
            return cachedRepos;

        var octokitRepos = await _client.Repository.GetAllForCurrent(new RepositoryRequest
        {
            Type = RepositoryType.Owner,
            Sort = RepositorySort.Updated,
            Direction = SortDirection.Descending
        });

        var repos = new List<GitHubRepo>(octokitRepos.Count);

        foreach (var repo in octokitRepos)
        {
            repos.Add(await CreateRepoModelAsync(repo));
        }

        _cache.Set(CacheKeys.GitHubRepos, repos, CacheTTL.GitHub);
        return repos;
    }

    private async Task<GitHubRepo> CreateRepoModelAsync(Repository repo)
    {
        var topics = await GetTopicsAsync(repo);
        return new GitHubRepo(
            Name: repo.Name,
            Description: repo.Description,
            Url: repo.HtmlUrl,
            Language: repo.Language,
            Stars: repo.StargazersCount,
            Forks: repo.ForksCount,
            IsForked: repo.Fork,
            UpdatedAt: repo.UpdatedAt.UtcDateTime,
            Topics: topics
        );
    }

    private async Task<List<string>> GetTopicsAsync(Repository repo)
    {
        if (repo.Topics is not null && repo.Topics.Any())
            return repo.Topics.ToList();

        try
        {
            var topics = await _client.Repository.GetAllTopics(_username, repo.Name);
            return topics.Names.ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to load topics for repo {RepoName}", repo.Name);
            return new List<string>();
        }
    }

    private async Task<GitHubActivity> ComputeActivityAsync()
    {
        var events = await _client.Activity.Events.GetAllUserPerformedPublic(_username, new ApiOptions
        {
            PageSize = 100,
            PageCount = 1,
            StartPage = 1
        });

        var now = DateTime.UtcNow;
        var weekAgo = now.AddDays(-7);
        var monthAgo = now.AddMonths(-1);
        var yearStart = new DateTime(now.Year, 1, 1);

        var eventData = events
            .Where(e => string.Equals(e.Type, "PushEvent", StringComparison.OrdinalIgnoreCase))
            .Select(e =>
            {
                var payload = e.Payload as PushEventPayload;
                return new
                {
                    Date = e.CreatedAt.UtcDateTime,
                    Commits = payload?.Commits?.Count ?? 0
                };
            })
            .ToList();

        var commitsThisWeek = eventData.Where(e => e.Date >= weekAgo).Sum(e => e.Commits);
        var commitsThisMonth = eventData.Where(e => e.Date >= monthAgo).Sum(e => e.Commits);
        var totalThisYear = eventData.Where(e => e.Date >= yearStart).Sum(e => e.Commits);

        var contributionsByDate = eventData
            .GroupBy(e => DateOnly.FromDateTime(e.Date))
            .ToDictionary(g => g.Key, g => g.Sum(e => e.Commits));

        var recentContributions = Enumerable.Range(0, 30)
            .Select(i => DateOnly.FromDateTime(now.AddDays(-i)))
            .Select(date => new DailyContribution(
                date,
                contributionsByDate.TryGetValue(date, out var count) ? count : 0))
            .OrderBy(d => d.Date)
            .ToList();

        var longestStreak = 0;
        var currentStreak = 0;

        foreach (var day in recentContributions.OrderByDescending(d => d.Date))
        {
            if (day.Count > 0)
            {
                currentStreak++;
                longestStreak = Math.Max(longestStreak, currentStreak);
            }
            else
            {
                break;
            }
        }

        return new GitHubActivity(
            TotalCommitsThisYear: totalThisYear,
            CommitsThisWeek: commitsThisWeek,
            CommitsThisMonth: commitsThisMonth,
            CurrentStreak: currentStreak,
            LongestStreak: longestStreak,
            RecentContributions: recentContributions
        );
    }

    private async Task<LanguageBreakdown> FetchLanguagesAsync(List<GitHubRepo> repos)
    {
        var totalBytes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var targetRepos = repos.Where(r => !r.IsForked).Take(10).ToList();

        foreach (var repo in targetRepos)
        {
            try
            {
                var languages = await _client.Repository.GetAllLanguages(_username, repo.Name);
                foreach (var language in languages)
                {
                    totalBytes.TryAdd(language.Name, 0);
                    totalBytes[language.Name] += (int)language.NumberOfBytes;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch languages for {RepoName}", repo.Name);
            }
        }

        var grandTotal = totalBytes.Values.Sum();
        var percentages = totalBytes
            .OrderByDescending(kv => kv.Value)
            .Take(8)
            .ToDictionary(
                kv => kv.Key,
                kv => grandTotal > 0 ? Math.Round((double)kv.Value / grandTotal * 100, 1) : 0);

        return new LanguageBreakdown(percentages, totalBytes);
    }

    private async Task<string> FetchLatestHashAsync(List<GitHubRepo> repos)
    {
        try
        {
            var mostRecentRepo = repos.OrderByDescending(r => r.UpdatedAt).FirstOrDefault();
            if (mostRecentRepo == null) return "STABLE";

            var commits = await _client.Repository.Commit.GetAll(_username, mostRecentRepo.Name, new ApiOptions { PageSize = 1, PageCount = 1 });
            var lastCommit = commits.FirstOrDefault();
            
            return lastCommit?.Sha.Substring(0, 7).ToUpper() ?? "STABLE";
        }
        catch
        {
            return "STABLE";
        }
    }
}
