using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using IntitechApi.Models;

namespace IntitechApi.Services;

public record AiHealthResult(
    bool Success,
    string Provider,
    string Model,
    int Attempts,
    string? Error
);

internal sealed record ProviderConfig(
    string Name,
    string BaseUrl,
    string Model,
    string? ApiKey,
    bool RequiresApiKey
);

public class FreeAiNarrativeService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<FreeAiNarrativeService> _logger;
    private readonly ConcurrentDictionary<string, Queue<DateTime>> _requestWindows = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _budgetLock = new();

    private const int MaxRetriesPerProvider = 2;
    private const int DefaultRateLimitPerMinute = 24;

    public FreeAiNarrativeService(HttpClient httpClient, IConfiguration configuration, ILogger<FreeAiNarrativeService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<WorktaleNarrativeResult> GenerateForCommitAsync(ChangelogEntry entry, CancellationToken cancellationToken)
    {
        var fallbackNarrative = BuildFallbackNarrative(entry);

        var providers = BuildProviderChain();
        if (providers.Count == 0)
        {
            return new WorktaleNarrativeResult(
                Narrative: fallbackNarrative,
                IsFallback: true,
                Source: "fallback:no-provider-config",
                Error: "No AI providers are configured. Set AI_PROVIDER and AI_FALLBACKS.");
        }

        var prompt = BuildPrompt(entry);
        var errors = new List<string>();

        foreach (var provider in providers)
        {
            var providerResult = await TryProviderAsync(provider, prompt, cancellationToken);
            if (providerResult.success && !string.IsNullOrWhiteSpace(providerResult.narrative))
            {
                return new WorktaleNarrativeResult(
                    Narrative: providerResult.narrative.Trim(),
                    IsFallback: false,
                    Source: provider.Name,
                    Error: null);
            }

            if (!string.IsNullOrWhiteSpace(providerResult.error))
            {
                errors.Add($"{provider.Name}: {providerResult.error}");
            }
        }

        return new WorktaleNarrativeResult(
            Narrative: fallbackNarrative,
            IsFallback: true,
            Source: "fallback:all-providers-failed",
            Error: string.Join(" | ", errors));
    }

    public async Task<AiHealthResult> ProbeAsync(bool forcePrimaryFailure, CancellationToken cancellationToken)
    {
        var providers = BuildProviderChain();
        if (providers.Count == 0)
        {
            return new AiHealthResult(false, "none", "none", 0, "No providers configured.");
        }

        var attempts = 0;
        var startIndex = forcePrimaryFailure && providers.Count > 1 ? 1 : 0;

        for (var i = startIndex; i < providers.Count; i++)
        {
            var provider = providers[i];
            var probePrompt = "Return exactly this text and nothing else: health-ok";
            var result = await TryProviderAsync(provider, probePrompt, cancellationToken);
            attempts++;

            if (result.success)
            {
                return new AiHealthResult(true, provider.Name, provider.Model, attempts, null);
            }

            if (i == providers.Count - 1)
            {
                return new AiHealthResult(false, provider.Name, provider.Model, attempts, result.error);
            }
        }

        return new AiHealthResult(false, "none", "none", attempts, "Probe failed unexpectedly.");
    }

    private async Task<(bool success, string? narrative, string? error)> TryProviderAsync(
        ProviderConfig provider,
        string prompt,
        CancellationToken cancellationToken)
    {
        if (provider.RequiresApiKey && string.IsNullOrWhiteSpace(provider.ApiKey))
        {
            return (false, null, "Missing API key.");
        }

        if (!TryAcquireRateBudget(provider.Name, out var budgetError))
        {
            _logger.LogWarning("AI budget blocked provider={Provider} reason={Reason}", provider.Name, budgetError);
            return (false, null, budgetError);
        }

        var endpoint = NormalizeChatEndpoint(provider.BaseUrl);
        for (var retry = 0; retry <= MaxRetriesPerProvider; retry++)
        {
            using var request = BuildRequest(endpoint, provider, prompt);
            var sw = Stopwatch.StartNew();
            try
            {
                using var response = await _httpClient.SendAsync(request, cancellationToken);
                var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
                sw.Stop();

                _logger.LogInformation(
                    "AI provider attempt provider={Provider} model={Model} retry={Retry} status={StatusCode} latencyMs={LatencyMs}",
                    provider.Name,
                    provider.Model,
                    retry,
                    (int)response.StatusCode,
                    sw.ElapsedMilliseconds);

                if (response.IsSuccessStatusCode)
                {
                    var output = ExtractChatContent(responseText);
                    if (!string.IsNullOrWhiteSpace(output))
                    {
                        return (true, output, null);
                    }

                    return (false, null, "Provider returned empty completion.");
                }

                if (IsRetryableStatus(response.StatusCode) && retry < MaxRetriesPerProvider)
                {
                    await DelayForRetryAsync(retry, cancellationToken);
                    continue;
                }

                return (false, null, $"HTTP {(int)response.StatusCode}: {Trim(responseText, 260)}");
            }
            catch (TaskCanceledException ex) when (retry < MaxRetriesPerProvider)
            {
                _logger.LogWarning(ex, "Timeout calling provider {Provider}. retry={Retry}", provider.Name, retry);
                await DelayForRetryAsync(retry, cancellationToken);
            }
            catch (HttpRequestException ex) when (retry < MaxRetriesPerProvider)
            {
                _logger.LogWarning(ex, "Transient HTTP error from provider {Provider}. retry={Retry}", provider.Name, retry);
                await DelayForRetryAsync(retry, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected provider error for {Provider}", provider.Name);
                return (false, null, ex.Message);
            }
        }

        return (false, null, "Exhausted retries for provider.");
    }

    private bool TryAcquireRateBudget(string providerName, out string? error)
    {
        var limit = GetRateLimitPerMinute();
        if (limit <= 0)
        {
            error = null;
            return true;
        }

        var windowSeconds = GetRateLimitWindowSeconds();
        var now = DateTime.UtcNow;
        var cutoff = now.AddSeconds(-windowSeconds);

        lock (_budgetLock)
        {
            var queue = _requestWindows.GetOrAdd(providerName, _ => new Queue<DateTime>());
            while (queue.Count > 0 && queue.Peek() < cutoff)
            {
                queue.Dequeue();
            }

            if (queue.Count >= limit)
            {
                error = $"Local request budget exceeded ({limit}/{windowSeconds}s).";
                return false;
            }

            queue.Enqueue(now);
            error = null;
            return true;
        }
    }

    private int GetRateLimitPerMinute()
    {
        var raw = GetConfig("AI_RATE_LIMIT_PER_MINUTE");
        return int.TryParse(raw, out var value) && value >= 0 ? value : DefaultRateLimitPerMinute;
    }

    private int GetRateLimitWindowSeconds()
    {
        var raw = GetConfig("AI_RATE_LIMIT_WINDOW_SECONDS");
        return int.TryParse(raw, out var value) && value > 0 ? value : 60;
    }

    private HttpRequestMessage BuildRequest(string endpoint, ProviderConfig provider, string prompt)
    {
        var payload = new
        {
            model = provider.Model,
            temperature = 0.2,
            max_tokens = 180,
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content = "You write concise engineering changelog narratives."
                },
                new
                {
                    role = "user",
                    content = prompt
                }
            }
        };

        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };

        if (!string.IsNullOrWhiteSpace(provider.ApiKey))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", provider.ApiKey);
        }

        if (string.Equals(provider.Name, "openrouter", StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.TryAddWithoutValidation("HTTP-Referer", "https://api.intitech.dev");
            request.Headers.TryAddWithoutValidation("X-Title", "IntitechApi Worktale");
        }

        return request;
    }

    private List<ProviderConfig> BuildProviderChain()
    {
        var providers = new List<ProviderConfig>();

        var primaryName = GetConfig("AI_PROVIDER") ?? "openrouter";
        var primary = BuildProviderFromConfig(primaryName, usePrimaryOverrides: true);
        if (primary is not null)
        {
            providers.Add(primary);
        }

        var fallbackList = GetConfig("AI_FALLBACKS");
        if (!string.IsNullOrWhiteSpace(fallbackList))
        {
            var fallbackNames = fallbackList
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(name => !string.Equals(name, primaryName, StringComparison.OrdinalIgnoreCase));

            foreach (var fallbackName in fallbackNames)
            {
                var provider = BuildProviderFromConfig(fallbackName, usePrimaryOverrides: false);
                if (provider is not null)
                {
                    providers.Add(provider);
                }
            }
        }

        return providers;
    }

    private ProviderConfig? BuildProviderFromConfig(string providerName, bool usePrimaryOverrides)
    {
        var preset = GetPreset(providerName);
        if (preset is null)
        {
            _logger.LogWarning("Unknown AI provider preset: {Provider}", providerName);
            return null;
        }

        var baseUrl = usePrimaryOverrides ? GetConfig("AI_BASE_URL") ?? preset.Value.baseUrl : preset.Value.baseUrl;
        var model = usePrimaryOverrides ? GetConfig("AI_MODEL") ?? preset.Value.model : preset.Value.model;

        var apiKey = usePrimaryOverrides
            ? GetConfig("AI_API_KEY") ?? GetConfig(preset.Value.apiKeyKey)
            : GetConfig(preset.Value.apiKeyKey);

        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(model))
        {
            _logger.LogWarning("Provider {Provider} has missing AI_BASE_URL or AI_MODEL.", providerName);
            return null;
        }

        return new ProviderConfig(providerName, baseUrl, model, apiKey, preset.Value.requiresApiKey);
    }

    private string? GetConfig(string key)
    {
        return _configuration[key] ?? _configuration[$"AI:{MapToSectionKey(key)}"];
    }

    private static string MapToSectionKey(string flatKey)
    {
        return flatKey switch
        {
            "AI_PROVIDER" => "Provider",
            "AI_BASE_URL" => "BaseUrl",
            "AI_MODEL" => "Model",
            "AI_API_KEY" => "ApiKey",
            "AI_FALLBACKS" => "Fallbacks",
            "AI_RATE_LIMIT_PER_MINUTE" => "RateLimitPerMinute",
            "AI_RATE_LIMIT_WINDOW_SECONDS" => "RateLimitWindowSeconds",
            _ => flatKey
        };
    }

    private static (string baseUrl, string model, string apiKeyKey, bool requiresApiKey)? GetPreset(string provider)
    {
        if (string.Equals(provider, "openrouter", StringComparison.OrdinalIgnoreCase))
        {
            return ("https://openrouter.ai/api/v1", "meta-llama/llama-3.3-8b-instruct:free", "OPENROUTER_API_KEY", true);
        }

        if (string.Equals(provider, "groq", StringComparison.OrdinalIgnoreCase))
        {
            return ("https://api.groq.com/openai/v1", "llama-3.1-8b-instant", "GROQ_API_KEY", true);
        }

        if (string.Equals(provider, "mistral", StringComparison.OrdinalIgnoreCase))
        {
            return ("https://api.mistral.ai/v1", "mistral-small-latest", "MISTRAL_API_KEY", true);
        }

        if (string.Equals(provider, "cerebras", StringComparison.OrdinalIgnoreCase))
        {
            return ("https://api.cerebras.ai/v1", "llama-3.3-70b", "CEREBRAS_API_KEY", true);
        }

        return null;
    }

    private static bool IsRetryableStatus(HttpStatusCode code)
    {
        return code == HttpStatusCode.TooManyRequests ||
               code == HttpStatusCode.RequestTimeout ||
               ((int)code >= 500 && (int)code <= 599);
    }

    private static async Task DelayForRetryAsync(int retry, CancellationToken cancellationToken)
    {
        var delayMs = (int)Math.Min(2000, 300 * Math.Pow(2, retry));
        await Task.Delay(delayMs, cancellationToken);
    }

    private static string NormalizeChatEndpoint(string baseUrl)
    {
        var trimmed = baseUrl.TrimEnd('/');
        return trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : $"{trimmed}/chat/completions";
    }

    private static string? ExtractChatContent(string responseText)
    {
        using var document = JsonDocument.Parse(responseText);
        if (!document.RootElement.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var firstChoice = choices.EnumerateArray().FirstOrDefault();
        if (firstChoice.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }

        if (!firstChoice.TryGetProperty("message", out var message))
        {
            return null;
        }

        if (!message.TryGetProperty("content", out var contentElement))
        {
            return null;
        }

        if (contentElement.ValueKind == JsonValueKind.String)
        {
            return contentElement.GetString();
        }

        if (contentElement.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var part in contentElement.EnumerateArray())
            {
                if (part.TryGetProperty("type", out var type) &&
                    string.Equals(type.GetString(), "text", StringComparison.OrdinalIgnoreCase) &&
                    part.TryGetProperty("text", out var text) &&
                    !string.IsNullOrWhiteSpace(text.GetString()))
                {
                    parts.Add(text.GetString()!);
                }
            }

            return parts.Count == 0 ? null : string.Join("\n", parts);
        }

        return null;
    }

    private static string BuildPrompt(ChangelogEntry entry)
    {
        var files = entry.FilesChanged.Count == 0
            ? "none provided"
            : string.Join(", ", entry.FilesChanged);

        return $"""
You are writing changelog entries for INTITECH - the portfolio of Abdulawwal Intisor, a backend engineer from Nigeria who builds what is needed.

Given this commit:
- Repo: {entry.Repo}
- Message: {entry.Title}
- Files changed: {files}
- Lines added: {entry.LinesAdded}, removed: {entry.LinesRemoved}

Write a 2-3 sentence narrative in first person, past tense.
Be specific about what was built and why it matters.
Sound like an engineer, not a press release.
No bullet points. Pure prose. Max 60 words.
""";
    }

    private static string BuildFallbackNarrative(ChangelogEntry entry)
    {
        var filePart = entry.FilesChanged.Count switch
        {
            0 => "without a file list from the hook",
            1 => $"touching {entry.FilesChanged[0]}",
            _ => $"touching {entry.FilesChanged.Count} files"
        };

        return $"I shipped \"{entry.Title}\" in {entry.Repo}, {filePart}. The change added {entry.LinesAdded} lines and removed {entry.LinesRemoved}, capturing steady progress even when free AI providers were unavailable.";
    }

    private static string Trim(string text, int maxLen)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length <= maxLen)
        {
            return text;
        }

        return text[..maxLen] + "...";
    }
}
