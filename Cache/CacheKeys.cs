namespace IntitechApi.Cache;

public static class CacheKeys
{
    public const string GitHubSummary = "github:summary";
    public const string GitHubRepos = "github:repos";
    public const string GitHubProfile = "github:profile";
    public const string GitHubActivity = "github:activity";
}

public static class CacheTTL
{
    public static readonly TimeSpan GitHub = TimeSpan.FromMinutes(10);
}
