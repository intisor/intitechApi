using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using IntitechApi.Models;

namespace IntitechApi.Services;

public record AiHealthResult(
    bool Success,
    string Provider,
    string Model,
    int Attempts,
    string? Error
);

/// <summary>
/// Tracks per-provider cooldown state (when they come back online after rate-limit).
/// </summary>
public class ProviderCooldownStore
{
    private readonly ConcurrentDictionary<string, long> _cooldowns = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<Dictionary<string, long>> GetCooldownsAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return new Dictionary<string, long>(_cooldowns, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetCooldownAsync(string providerId, long cooledUntilUnixMs, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _cooldowns[providerId] = cooledUntilUnixMs;
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>
/// Candidate provider wrapper with concurrent task.
/// </summary>
internal sealed record SwarmCandidate(
    ProviderConfig Provider,
    Task<(bool success, string? narrative, string? error)> Task
);

internal sealed record ProviderConfig(
    string Name,
    string BaseUrl,
    string Model,
    string? ApiKey,
    bool RequiresApiKey
);

public class WorktaleStore
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
            .Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

public class WorktaleQueue
{
    private readonly Channel<string> _channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });

    public ValueTask EnqueueAsync(string entryId, CancellationToken cancellationToken) =>
        _channel.Writer.WriteAsync(entryId, cancellationToken);

    public ValueTask<string> DequeueAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAsync(cancellationToken);
}

public class FreeAiNarrativeService
{
    private static readonly Dictionary<string, (string baseUrl, string model, string keyPath)> Presets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["openrouter"] = ("https://openrouter.ai/api/v1", "meta-llama/llama-3.3-8b-instruct:free", "AI:OpenRouterApiKey"),
        ["groq"] = ("https://api.groq.com/openai/v1", "llama-3.1-8b-instant", "AI:GroqApiKey"),
        ["mistral"] = ("https://api.mistral.ai/v1", "mistral-small-latest", "AI:MistralApiKey"),
        ["cerebras"] = ("https://api.cerebras.ai/v1", "llama-3.3-70b", "AI:CerebrasApiKey"),
    };

    private readonly HttpClient _http;
    private readonly IConfiguration _cfg;
    private readonly ILogger<FreeAiNarrativeService> _log;
    private readonly ProviderCooldownStore _cooldowns;

    public FreeAiNarrativeService(HttpClient http, IConfiguration cfg, ILogger<FreeAiNarrativeService> log, ProviderCooldownStore cooldowns)
    {
        _http = http;
        _cfg = cfg;
        _log = log;
        _cooldowns = cooldowns;
    }

    public async Task<WorktaleNarrativeResult> GenerateForCommitAsync(ChangelogEntry entry, CancellationToken ct)
    {
        var providers = BuildProviderChain();
        if (providers.Count == 0)
            return Fallback("No providers");

        var cooldowns = await _cooldowns.GetCooldownsAsync(ct);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var available = providers.Where(p => !cooldowns.ContainsKey(p.Name) || cooldowns[p.Name] <= now).ToList();

        if (available.Count == 0)
            return Fallback("All cooling down");

        var prompt = $"""
You write concise engineering narratives for INTITECH.

Commit: {entry.Repo}/{entry.Title}
Files: {(entry.FilesChanged.Count == 0 ? "none" : string.Join(", ", entry.FilesChanged))}
Delta: +{entry.LinesAdded} -{entry.LinesRemoved}

Write 2-3 sentences, first person, past tense. Max 60 words. Be specific about what was built.
""";

        var swarm = available.Select(p => new SwarmCandidate(p, TryProviderAsync(p, prompt, ct))).ToList();
        var errors = new List<string>();

        _log.LogInformation("Swarm {C} providers", swarm.Count);

        while (swarm.Count > 0)
        {
            var task = await Task.WhenAny(swarm.Select(c => c.Task));
            var candidate = swarm.First(c => c.Task == task);
            swarm.Remove(candidate);

            var (success, narrative, error) = await candidate.Task;
            if (success && !string.IsNullOrWhiteSpace(narrative))
            {
                _log.LogInformation("Swarm won {P}", candidate.Provider.Name);
                _ = Task.Run(() => Task.WhenAll(swarm.Select(c => c.Task)).ContinueWith(_ => { }, TaskScheduler.Default));
                return new(narrative.Trim(), false, candidate.Provider.Name, null);
            }

            if (!string.IsNullOrWhiteSpace(error))
                errors.Add($"{candidate.Provider.Name}: {error}");
        }

        return Fallback($"Swarm failed: {string.Join(" | ", errors)}");
    }

    public async Task<AiHealthResult> ProbeAsync(bool skipPrimary, CancellationToken ct)
    {
        var providers = BuildProviderChain();
        var start = skipPrimary && providers.Count > 1 ? 1 : 0;
        int attempts = 0;

        for (var i = start; i < providers.Count; i++)
        {
            var (success, _, _) = await TryProviderAsync(providers[i], "Return: health-ok", ct);
            attempts++;
            if (success)
                return new(true, providers[i].Name, providers[i].Model, attempts, null);
        }

        return new(false, "none", "none", attempts, "Exhausted");
    }

    private async Task<(bool success, string? narrative, string? error)> TryProviderAsync(ProviderConfig p, string prompt, CancellationToken ct)
    {
        if (p.RequiresApiKey && string.IsNullOrWhiteSpace(p.ApiKey))
            return (false, null, "Missing API key");

        var endpoint = p.BaseUrl.TrimEnd('/');
        if (!endpoint.EndsWith("/chat/completions"))
            endpoint += "/chat/completions";

        for (int r = 0; r <= 2; r++)
        {
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        model = p.Model,
                        temperature = 0.2,
                        max_tokens = 180,
                        messages = new[] {
                            new { role = "system", content = "Write concise engineering narratives." },
                            new { role = "user", content = prompt }
                        }
                    }), Encoding.UTF8, "application/json")
                };

                if (!string.IsNullOrWhiteSpace(p.ApiKey))
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", p.ApiKey);

                if (p.Name == "openrouter")
                {
                    req.Headers.TryAddWithoutValidation("HTTP-Referer", "https://api.intitech.dev");
                    req.Headers.TryAddWithoutValidation("X-Title", "IntitechApi");
                }

                var sw = Stopwatch.StartNew();
                using var res = await _http.SendAsync(req, ct);
                sw.Stop();

                _log.LogInformation("Provider {P} retry={R} status={S} ms={Ms}", p.Name, r, (int)res.StatusCode, sw.ElapsedMilliseconds);

                if (res.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var cooldownMs = 60_000L;
                    if (res.Headers.TryGetValues("Retry-After", out var values) && long.TryParse(values.FirstOrDefault(), out var secs))
                        cooldownMs = secs * 1000;
                    await _cooldowns.SetCooldownAsync(p.Name, DateTimeOffset.UtcNow.AddMilliseconds(cooldownMs).ToUnixTimeMilliseconds(), ct);
                    return (false, null, $"429");
                }

                if (res.IsSuccessStatusCode)
                {
                    var text = await res.Content.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(text);
                    var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
                    return string.IsNullOrWhiteSpace(content) ? (false, null, "Empty response") : (true, content, null);
                }

                if ((int)res.StatusCode >= 500 || res.StatusCode == HttpStatusCode.RequestTimeout)
                {
                    if (r < 2) { await Task.Delay(300 * (int)Math.Pow(2, r), ct); continue; }
                }

                return (false, null, $"HTTP {(int)res.StatusCode}");
            }
            catch (TaskCanceledException) when (r < 2) { await Task.Delay(300 * (int)Math.Pow(2, r), ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _log.LogError(ex, "Provider {P}", p.Name); return (false, null, ex.Message); }
        }

        return (false, null, "Max retries");
    }

    private List<ProviderConfig> BuildProviderChain()
    {
        var result = new List<ProviderConfig>();
        var primary = _cfg["AI:Provider"] ?? "openrouter";
        var fallbacks = _cfg["AI:Fallbacks"];

        if (LoadProvider(primary, true) is { } p)
            result.Add(p);

        if (!string.IsNullOrWhiteSpace(fallbacks))
        {
            foreach (var name in fallbacks.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (name != primary && LoadProvider(name, false) is { } p2)
                    result.Add(p2);
            }
        }

        return result;
    }

    private ProviderConfig? LoadProvider(string name, bool usePrimaryOverrides)
    {
        if (!Presets.TryGetValue(name, out var preset))
        {
            _log.LogWarning("Unknown provider: {N}", name);
            return null;
        }

        var (baseUrl, model, keyPath) = preset;

        if (usePrimaryOverrides)
        {
            baseUrl = _cfg["AI:BaseUrl"] ?? baseUrl;
            model = _cfg["AI:Model"] ?? model;
        }

        var key = _cfg["AI:ApiKey"] ?? _cfg[keyPath];
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(model))
            return null;

        return new(name, baseUrl, model, key, !string.IsNullOrWhiteSpace(key));
    }

    private static WorktaleNarrativeResult Fallback(string reason) =>
        new($"Progressed on {DateTime.UtcNow:MMM dd}. {reason}.", true, "fallback", reason);
}

public class WorktaleNarrativeWorker : BackgroundService
{
    private readonly WorktaleQueue _queue;
    private readonly WorktaleStore _store;
    private readonly FreeAiNarrativeService _narrativeService;
    private readonly ILogger<WorktaleNarrativeWorker> _logger;

    public WorktaleNarrativeWorker(
        WorktaleQueue queue,
        WorktaleStore store,
        FreeAiNarrativeService narrativeService,
        ILogger<WorktaleNarrativeWorker> logger)
    {
        _queue = queue;
        _store = store;
        _narrativeService = narrativeService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            string entryId;
            try
            {
                entryId = await _queue.DequeueAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                var entry = await _store.GetByIdAsync(entryId, stoppingToken);
                if (entry is null)
                {
                    _logger.LogWarning("Queued entry {EntryId} was not found in store", entryId);
                    continue;
                }

                if (!string.Equals(entry.Type, "commit", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var result = await _narrativeService.GenerateForCommitAsync(entry, stoppingToken);
                await _store.UpdateNarrativeAsync(entryId, result.Narrative, result.IsFallback, result.Error, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected worker failure while processing queued entry {EntryId}", entryId);
            }
        }
    }
}
