using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace Athena.UI.Services;

/// <summary>
/// 配置服务实现
/// </summary>
public class ConfigService : IConfigService
{
    /// <inheritdoc/>
    public event EventHandler<AppConfig>? ConfigChanged;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IPlatformPathService _platformPathService;

    // 配置缓存：config.json 仅由本服务的 SaveAsync 写入，因此用文件的最后写入时间做失效判断即可，
    // 既能在热路径（每次发送都会读取配置）上免去重复的磁盘读 + JSON 解析，又能感知外部手动改动。
    private readonly object _cacheLock = new();
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
        if (!File.Exists(ConfigFilePath))
        {
            return new AppConfig();
        }

        if (TryGetCached(out var cached))
        {
            return cached;
        }

        try
        {
            var writeTimeUtc = File.GetLastWriteTimeUtc(ConfigFilePath);
            var json = await File.ReadAllTextAsync(ConfigFilePath);
            var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
            var resolved = ApplyLegacyBrowserObservationMode(config ?? new AppConfig(), json);
            StoreCache(resolved, writeTimeUtc);
            return resolved;
        }
        catch
        {
            return new AppConfig();
        }
    }

    /// <summary>
    /// 同步加载配置（用于启动时避免死锁）
    /// </summary>
    public AppConfig Load()
    {
        if (!File.Exists(ConfigFilePath))
        {
            return new AppConfig();
        }

        if (TryGetCached(out var cached))
        {
            return cached;
        }

        try
        {
            var writeTimeUtc = File.GetLastWriteTimeUtc(ConfigFilePath);
            var json = File.ReadAllText(ConfigFilePath);
            var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
            var resolved = ApplyLegacyBrowserObservationMode(config ?? new AppConfig(), json);
            StoreCache(resolved, writeTimeUtc);
            return resolved;
        }
        catch
        {
            return new AppConfig();
        }
    }

    public async Task SaveAsync(AppConfig config)
    {
        var json = JsonSerializer.Serialize(config, JsonOptions);
        await File.WriteAllTextAsync(ConfigFilePath, json);
        // 写入后立刻刷新缓存，让后续 Load() 直接命中而无需再读盘。
        try
        {
            StoreCache(config, File.GetLastWriteTimeUtc(ConfigFilePath));
        }
        catch
        {
            InvalidateCache();
        }
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
                catch
                {
                    // 取写入时间失败时，按缓存未命中处理，走完整读取路径。
                }
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
        lock (_cacheLock)
        {
            _cachedConfig = null;
        }
    }

    private static AppConfig ApplyLegacyBrowserObservationMode(AppConfig config, string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.TryGetProperty("browserObservationMode", out _))
            {
                return config;
            }

            var hasLegacyVision = TryGetBool(root, "browserUseVisionMode", out var useVision);
            var hasLegacySom = TryGetBool(root, "browserSomEnabled", out var somEnabled);
            if (!hasLegacyVision && !hasLegacySom)
            {
                return config;
            }

            useVision = hasLegacyVision ? useVision : true;
            somEnabled = hasLegacySom ? somEnabled : true;
            config.BrowserObservationMode = !useVision
                ? BrowserObservationMode.DomOnly
                : BrowserObservationMode.VisionWithSom;
        }
        catch
        {
            // Ignore migration failures; default config values remain valid.
        }

        return config;
    }

    private static bool TryGetBool(JsonElement root, string propertyName, out bool value)
    {
        value = false;
        if (!root.TryGetProperty(propertyName, out var element))
        {
            return false;
        }

        if (element.ValueKind == JsonValueKind.True || element.ValueKind == JsonValueKind.False)
        {
            value = element.GetBoolean();
            return true;
        }

        return element.ValueKind == JsonValueKind.String && bool.TryParse(element.GetString(), out value);
    }
}
