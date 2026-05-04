using IntitechApi.Models;

namespace IntitechApi.Services;

public class PortfolioService
{
    private readonly GitHubService _github;
    private readonly MetadataService _metadata;
    private readonly WakaTimeService _wakatime;

    public PortfolioService(GitHubService github, MetadataService metadata, WakaTimeService wakatime)
    {
        _github = github;
        _metadata = metadata;
        _wakatime = wakatime;
    }

    public async Task<PortfolioSummary?> GetSummaryAsync()
    {
        var githubSummary = await _github.GetSummaryAsync();
        var metadata = await _metadata.GetMetadataAsync();
        var wakaStats = await _wakatime.GetProjectStatsAsync();

        if (metadata is null) return null;

        // Merge Manual Projects with GitHub Projects
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

        return new PortfolioSummary(
            About: metadata.About,
            Skills: metadata.Skills,
            Projects: allProjects,
            System: metadata.System,
            GitHub: githubSummary,
            Timestamp: DateTime.UtcNow
        );
    }
}
