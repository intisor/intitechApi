namespace IntitechApi.Models;

public record PortfolioSummary(
    AboutInfo About,
    List<SkillInfo> Skills,
    List<ProjectInfo> Projects,
    SystemInfo System,
    GitHubSummary GitHub,
    DateTime Timestamp
);

public record AboutInfo(
    string Opening,
    string Bio,
    string CareerGoal,
    string Affiliations,
    string Location,
    string Status
);

public record SkillInfo(
    string Name,
    int Tier
);

public record ProjectInfo(
    string Id,
    string Name,
    string Tagline,
    string Description,
    List<string> Stack,
    string Metrics,
    string Link,
    string? CodingTime = null,
    bool IsLive = true
);

public record SystemInfo(
    string Uptime,
    string Version,
    string Build
);
