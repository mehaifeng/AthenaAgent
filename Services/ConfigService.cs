using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Athena.UI.Services;

/// <summary>应用配置的 JSON 持久化服务。</summary>
public class ConfigService : IConfigService
{
    public event EventHandler<AppConfig>? ConfigChanged;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly object _cacheLock = new();
    private readonly IPlatformPathService _platformPathService;
    private AppConfig? _cachedConfig;
    private DateTime _cachedWriteTimeUtc;

    public string ConfigFilePath { get; }

    public ConfigService(IPlatformPathService platformPathService)
    {
        _platformPathService = platformPathService;
        ConfigFilePath = _platformPathService.GetConfigFilePath();
    }

    public async Task<AppConfig> LoadAsync()
    {
        if (!File.Exists(ConfigFilePath)) return new AppConfig();
        if (TryGetCached(out var cached)) return cached;

        try
        {
            var writeTimeUtc = File.GetLastWriteTimeUtc(ConfigFilePath);
            var config = DeserializeConfig(await File.ReadAllTextAsync(ConfigFilePath));
            StoreCache(config, writeTimeUtc);
            return config;
        }
        catch
        {
            return new AppConfig();
        }
    }

    public AppConfig Load()
    {
        if (!File.Exists(ConfigFilePath)) return new AppConfig();
        if (TryGetCached(out var cached)) return cached;

        try
        {
            var writeTimeUtc = File.GetLastWriteTimeUtc(ConfigFilePath);
            var config = DeserializeConfig(File.ReadAllText(ConfigFilePath));
            StoreCache(config, writeTimeUtc);
            return config;
        }
        catch
        {
            return new AppConfig();
        }
    }

    public async Task SaveAsync(AppConfig config)
    {
        await File.WriteAllTextAsync(ConfigFilePath, JsonSerializer.Serialize(config, JsonOptions));
        try { StoreCache(config, File.GetLastWriteTimeUtc(ConfigFilePath)); }
        catch { InvalidateCache(); }
        ConfigChanged?.Invoke(this, config);
    }

    private bool TryGetCached(out AppConfig config)
    {
        lock (_cacheLock)
        {
            if (_cachedConfig != null)
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(ConfigFilePath) == _cachedWriteTimeUtc)
                    {
                        config = _cachedConfig;
                        return true;
                    }
                }
                catch { }
            }
        }

        config = null!;
        return false;
    }

    private void StoreCache(AppConfig config, DateTime writeTimeUtc)
    {
        lock (_cacheLock)
        {
            _cachedConfig = config;
            _cachedWriteTimeUtc = writeTimeUtc;
        }
    }

    private void InvalidateCache()
    {
        lock (_cacheLock) _cachedConfig = null;
    }

    private static AppConfig DeserializeConfig(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject;
        var aiModels = root?["aiModels"] as JsonObject;
        if (aiModels != null && aiModels["provider"] == null
            && aiModels["providers"] is JsonArray { Count: > 0 } providers
            && providers[0] is JsonObject firstProvider)
        {
            var provider = firstProvider.DeepClone().AsObject();
            provider.Remove("id");
            aiModels["provider"] = provider;
            aiModels.Remove("providers");
            root!["configSchemaVersion"] = 3;
        }

        return root?.Deserialize<AppConfig>(JsonOptions) ?? new AppConfig();
    }
}
