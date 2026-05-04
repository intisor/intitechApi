using IntitechApi.Models;
using Microsoft.Extensions.Caching.Memory;

namespace IntitechApi.Services;

public class PortfolioService
{
    private readonly GitHubService _github;
    private readonly MetadataService _metadata;
    private readonly WakaTimeService _wakatime;
    private readonly IMemoryCache _cache;

    public PortfolioService(GitHubService github, MetadataService metadata, WakaTimeService wakatime, IMemoryCache cache)
    {
        _github = github;
        _metadata = metadata;
        _wakatime = wakatime;
        _cache = cache;
    }

    private const string SummaryCacheKey = "FullPortfolioSummary";

    public async Task<PortfolioSummary?> GetSummaryAsync()
    {
        // 1. Try to get from cache first for instant response
        if (_cache.TryGetValue(SummaryCacheKey, out PortfolioSummary? cached))
        {
            // Optional: You could trigger a background refresh here
            return cached;
        }

        // 2. Fetch all sources IN PARALLEL
        var githubTask = _github.GetSummaryAsync();
        var metadataTask = _metadata.GetMetadataAsync();
        var wakaStatsTask = _wakatime.GetProjectStatsAsync();

        await Task.WhenAll(githubTask, metadataTask, wakaStatsTask);

        var githubSummary = await githubTask;
        var metadata = await metadataTask;
        var wakaStats = await wakaStatsTask;

        if (metadata is null) return null;

        // 3. Merge data
        var allProjects = new List<ProjectInfo>();
        
        foreach (var mp in metadata.ManualProjects)
        {
            wakaStats.TryGetValue(mp.Name, out var time);
            allProjects.Add(mp with { CodingTime = time });
        }
        
        foreach (var repo in githubSummary.PortfolioRepos)
        {
            wakaStats.TryGetValue(repo.Name, out var time);
            allProjects.Add(new ProjectInfo(
                Id: repo.Name.ToLower().Replace(" ", "-"),
                Name: repo.Name,
                Tagline: $"GITHUB · {repo.Language?.ToUpper() ?? "SOURCE"}",
                Description: repo.Description ?? "No description provided.",
                Stack: repo.Topics.Where(t => !t.Equals("portfolio", StringComparison.OrdinalIgnoreCase)).ToList(),
                Metrics: $"↗ {repo.Stars} stars · {repo.Forks} forks",
                Link: repo.Url,
                CodingTime: time
            ));
        }

        var summary = new PortfolioSummary(
            About: metadata.About,
            Skills: metadata.Skills,
            Projects: allProjects,
            System: metadata.System with { 
                ResponseTime = "--", 
                RequestsToday = 0 
            },
            GitHub: githubSummary,
            Timestamp: DateTime.UtcNow
        );

        // 4. Cache the result for 30 minutes
        _cache.Set(SummaryCacheKey, summary, TimeSpan.FromMinutes(30));

        return summary;
    }
}
