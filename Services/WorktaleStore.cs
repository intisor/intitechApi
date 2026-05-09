using System.Text.Json;
using IntitechApi.Models;

namespace IntitechApi.Services;

public interface IWorktaleStore
{
    Task<ChangelogEntry> AddCommitAsync(WorktaleIngestRequest request, CancellationToken cancellationToken);
    Task<ChangelogEntry> AddMilestoneAsync(ChangelogMilestoneRequest request, CancellationToken cancellationToken);
    Task<List<ChangelogEntry>> GetAllAsync(CancellationToken cancellationToken);
    Task<ChangelogEntry?> GetByIdAsync(string id, CancellationToken cancellationToken);
    Task<bool> UpdateNarrativeAsync(string id, string narrative, bool isFallback, string? error, CancellationToken cancellationToken);
}

public class WorktaleStore : IWorktaleStore
{
    private readonly string _storePath;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public WorktaleStore(IWebHostEnvironment env)
    {
        _storePath = Path.Combine(env.ContentRootPath, "Data", "worktale-changelog.json");
    }

    public async Task<ChangelogEntry> AddCommitAsync(WorktaleIngestRequest request, CancellationToken cancellationToken)
    {
        var timestamp = request.Timestamp?.ToUniversalTime() ?? DateTime.UtcNow;
        var now = DateTime.UtcNow;
        var hashPrefix = string.IsNullOrWhiteSpace(request.Hash)
            ? "unknown"
            : request.Hash[..Math.Min(request.Hash.Length, 12)].ToLowerInvariant();

        var entry = new ChangelogEntry(
            Id: $"{hashPrefix}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
            Type: "commit",
            Repo: request.Repo.Trim(),
            Title: request.Message.Trim(),
            Description: null,
            Hash: request.Hash.Trim(),
            FilesChanged: ParseFilesChanged(request.FilesChanged),
            LinesAdded: Math.Max(0, request.LinesAdded),
            LinesRemoved: Math.Max(0, request.LinesRemoved),
            Timestamp: timestamp,
            Status: "pending",
            Narrative: null,
            NarrativeError: null,
            CreatedAt: now,
            UpdatedAt: now
        );

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var entries = await ReadAllUnsafeAsync(cancellationToken);
            entries.Add(entry);
            await WriteAllUnsafeAsync(entries, cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }

        return entry;
    }

    public async Task<ChangelogEntry> AddMilestoneAsync(ChangelogMilestoneRequest request, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var timestamp = request.Timestamp?.ToUniversalTime() ?? now;

        var entry = new ChangelogEntry(
            Id: $"milestone-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
            Type: "milestone",
            Repo: string.IsNullOrWhiteSpace(request.Repo) ? "general" : request.Repo.Trim(),
            Title: request.Title.Trim(),
            Description: request.Description.Trim(),
            Hash: null,
            FilesChanged: new List<string>(),
            LinesAdded: 0,
            LinesRemoved: 0,
            Timestamp: timestamp,
            Status: "complete",
            Narrative: request.Description.Trim(),
            NarrativeError: null,
            CreatedAt: now,
            UpdatedAt: now
        );

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var entries = await ReadAllUnsafeAsync(cancellationToken);
            entries.Add(entry);
            await WriteAllUnsafeAsync(entries, cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }

        return entry;
    }

    public async Task<List<ChangelogEntry>> GetAllAsync(CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var entries = await ReadAllUnsafeAsync(cancellationToken);
            return entries
                .OrderByDescending(e => e.Timestamp)
                .ThenByDescending(e => e.CreatedAt)
                .ToList();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<ChangelogEntry?> GetByIdAsync(string id, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var entries = await ReadAllUnsafeAsync(cancellationToken);
            return entries.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<bool> UpdateNarrativeAsync(string id, string narrative, bool isFallback, string? error, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var entries = await ReadAllUnsafeAsync(cancellationToken);
            var index = entries.FindIndex(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                return false;
            }

            var existing = entries[index];
            entries[index] = existing with
            {
                Narrative = narrative,
                Status = isFallback ? "fallback" : "complete",
                NarrativeError = error,
                UpdatedAt = DateTime.UtcNow
            };

            await WriteAllUnsafeAsync(entries, cancellationToken);
            return true;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task<List<ChangelogEntry>> ReadAllUnsafeAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_storePath))
        {
            return new List<ChangelogEntry>();
        }

        await using var stream = File.OpenRead(_storePath);
        var entries = await JsonSerializer.DeserializeAsync<List<ChangelogEntry>>(stream, JsonOptions, cancellationToken);
        return entries ?? new List<ChangelogEntry>();
    }

    private async Task WriteAllUnsafeAsync(List<ChangelogEntry> entries, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);

        await using var stream = File.Create(_storePath);
        await JsonSerializer.SerializeAsync(stream, entries, JsonOptions, cancellationToken);
    }

    private static List<string> ParseFilesChanged(string? filesChanged)
    {
        if (string.IsNullOrWhiteSpace(filesChanged))
        {
            return new List<string>();
        }

        return filesChanged
            .Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}