using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace IntitechApi.Services;

public class WakaTimeService
{
    private readonly HttpClient _httpClient;
    private readonly IMemoryCache _cache;
    private readonly ILogger<WakaTimeService> _logger;
    private readonly string? _apiKey;
    private const string CacheKey = "WakaTimeStats";

    public WakaTimeService(HttpClient httpClient, IMemoryCache cache, IConfiguration config, ILogger<WakaTimeService> logger)
    {
        _httpClient = httpClient;
        _cache = cache;
        _logger = logger;
        _apiKey = config["WakaTime:ApiKey"] ?? config["wakatime:ApiKey"];

        if (!string.IsNullOrEmpty(_apiKey))
        {
            var authValue = Convert.ToBase64String(Encoding.UTF8.GetBytes(_apiKey));
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authValue);
        }
        
        _httpClient.BaseAddress = new Uri("https://wakatime.com/api/v1/");
    }

    public async Task<Dictionary<string, string>> GetProjectStatsAsync()
    {
        if (string.IsNullOrEmpty(_apiKey))
        {
            _logger.LogWarning("WakaTime API Key is missing. Skipping stats fetch.");
            return new Dictionary<string, string>();
        }

        if (_cache.TryGetValue(CacheKey, out Dictionary<string, string>? cached) && cached is not null)
            return cached;

        try
        {
            // Fetch stats for 'all_time' to get project-specific duration
            var response = await _httpClient.GetAsync("users/current/stats/all_time");
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var projectMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            
            if (root.TryGetProperty("data", out var data) && data.TryGetProperty("projects", out var projects))
            {
                foreach (var project in projects.EnumerateArray())
                {
                    var name = project.GetProperty("name").GetString();
                    var text = project.GetProperty("text").GetString(); // e.g., "15h 30m"
                    
                    if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(text))
                    {
                        projectMap[name] = text;
                    }
                }
            }

            _cache.Set(CacheKey, projectMap, TimeSpan.FromHours(6)); // WakaTime stats don't change very fast
            return projectMap;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch WakaTime stats");
            return new Dictionary<string, string>();
        }
    }
}
