using System.Text.Json;
using IntitechApi.Models;
using Microsoft.Extensions.Caching.Memory;

namespace IntitechApi.Services;

public class MetadataService
{
    private readonly string _dataPath;
    private readonly IMemoryCache _cache;
    private readonly ILogger<MetadataService> _logger;
    private const string CacheKey = "PortfolioMetadata";

    public MetadataService(IWebHostEnvironment env, IMemoryCache cache, ILogger<MetadataService> logger)
    {
        _dataPath = Path.Combine(env.ContentRootPath, "Data", "portfolio-data.json");
        _cache = cache;
        _logger = logger;
    }

    public async Task<PortfolioMetadata?> GetMetadataAsync()
    {
        if (_cache.TryGetValue(CacheKey, out PortfolioMetadata? cached) && cached is not null)
            return cached;

        try
        {
            if (!File.Exists(_dataPath))
            {
                _logger.LogWarning("Metadata file not found at {Path}", _dataPath);
                return null;
            }

            var json = await File.ReadAllTextAsync(_dataPath);
            var metadata = JsonSerializer.Deserialize<PortfolioMetadata>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (metadata is not null)
            {
                _cache.Set(CacheKey, metadata, TimeSpan.FromHours(1));
            }

            return metadata;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading portfolio metadata");
            return null;
        }
    }
}

public record PortfolioMetadata(
    AboutInfo About,
    List<SkillInfo> Skills,
    List<ProjectInfo> ManualProjects,
    SystemInfo System
);
