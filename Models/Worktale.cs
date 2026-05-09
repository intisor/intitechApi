namespace IntitechApi.Models;

public record WorktaleIngestRequest(
    string Hash,
    string Message,
    string Repo,
    string? FilesChanged,
    int LinesAdded,
    int LinesRemoved,
    DateTime? Timestamp
);

public record ChangelogMilestoneRequest(
    string Title,
    string Description,
    string? Repo,
    DateTime? Timestamp
);

public record ChangelogEntry(
    string Id,
    string Type,
    string Repo,
    string Title,
    string? Description,
    string? Hash,
    List<string> FilesChanged,
    int LinesAdded,
    int LinesRemoved,
    DateTime Timestamp,
    string Status,
    string? Narrative,
    string? NarrativeError,
    DateTime CreatedAt,
    DateTime UpdatedAt
);

public record WorktaleNarrativeResult(
    string Narrative,
    bool IsFallback,
    string Source,
    string? Error
);