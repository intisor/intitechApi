namespace IntitechApi.Models;

public record GitHubSummary(
    GitHubProfile Profile,
    List<GitHubRepo> TopRepos,
    List<GitHubRepo> PortfolioRepos,
    GitHubActivity Activity,
    LanguageBreakdown Languages,
    DateTime CachedAt
);

public record GitHubProfile(
    string Username,
    string DisplayName,
    string Bio,
    string AvatarUrl,
    int PublicRepos,
    int Followers,
    int Following,
    string ProfileUrl
);

public record GitHubRepo(
    string Name,
    string? Description,
    string Url,
    string? Language,
    int Stars,
    int Forks,
    bool IsForked,
    DateTime UpdatedAt,
    List<string> Topics
);

public record GitHubActivity(
    int TotalCommitsThisYear,
    int CommitsThisWeek,
    int CommitsThisMonth,
    int CurrentStreak,
    int LongestStreak,
    List<DailyContribution> RecentContributions
);

public record DailyContribution(
    DateOnly Date,
    int Count
);

public record LanguageBreakdown(
    Dictionary<string, double> Percentages,
    Dictionary<string, int> ByteCounts
);
