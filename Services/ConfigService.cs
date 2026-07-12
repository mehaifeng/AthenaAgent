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
            resolved = ApplyLegacyBrowserAgentFields(resolved, json);
            resolved = ApplyCredentialSourceMigration(resolved, json);
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
            resolved = ApplyLegacyBrowserAgentFields(resolved, json);
            resolved = ApplyCredentialSourceMigration(resolved, json);
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

    /// <summary>
    /// 字段改名迁移：历史配置使用 browserVision* 键，重命名为 browserAgent* 后，
    /// 反序列化不会再填充这些值。此处在新键缺失时，把旧键的值搬到新字段，避免老用户
    /// 丢失浏览器模型配置。
    /// </summary>
    private static AppConfig ApplyLegacyBrowserAgentFields(AppConfig config, string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            // 已是新格式（写过 browserAgentModel）则无需搬运。
            if (root.TryGetProperty("browserAgentModel", out _))
            {
                return config;
            }

            if (TryGetString(root, "browserVisionProvider", out var provider))
                config.BrowserAgentProvider = provider;
            if (TryGetString(root, "browserVisionBaseUrl", out var baseUrl))
                config.BrowserAgentBaseUrl = baseUrl;
            if (TryGetString(root, "browserVisionApiKey", out var apiKey))
                config.BrowserAgentApiKey = apiKey;
            if (TryGetString(root, "browserVisionModel", out var model))
                config.BrowserAgentModel = model;
            if (TryGetInt(root, "browserVisionMaxTokens", out var maxTokens))
                config.BrowserAgentMaxTokens = maxTokens;
            if (TryGetDouble(root, "browserVisionTemperature", out var temperature))
                config.BrowserAgentTemperature = temperature;
        }
        catch
        {
            // Ignore migration failures; default config values remain valid.
        }

        return config;
    }

    /// <summary>
    /// 统一凭据继承树迁移：本功能引入前，各角色（次级/Embedding）靠"留空回退主配置"
    /// 的隐式语义工作。凡是没有写过对应 *CredentialSource 键的历史配置：角色的 ApiKey 或 BaseUrl
    /// 已填 → 迁移为 Custom（Custom 仍保留逐字段回退，行为不变）；全空 → InheritMain。
    /// 次级模型的遗留 provider="Inherit" 字符串强制归为 InheritMain 并归一化 provider。
    /// （图像/音频/浏览器已改为独立凭据，不再参与继承树。）
    /// </summary>
    private static AppConfig ApplyCredentialSourceMigration(AppConfig config, string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (!root.TryGetProperty("secondaryCredentialSource", out _))
            {
                if (string.Equals(config.SecondaryProvider, "Inherit", StringComparison.OrdinalIgnoreCase))
                {
                    config.SecondaryCredentialSource = ModelCredentialSource.InheritMain;
                    config.SecondaryProvider = "OpenAI";
                }
                else
                {
                    config.SecondaryCredentialSource = HasCustomCredential(config.SecondaryApiKey, config.SecondaryBaseUrl)
                        ? ModelCredentialSource.Custom
                        : ModelCredentialSource.InheritMain;
                }
            }

            if (!root.TryGetProperty("embeddingCredentialSource", out _))
            {
                config.EmbeddingCredentialSource = HasCustomCredential(config.EmbeddingApiKey, config.EmbeddingBaseUrl)
                    ? ModelCredentialSource.Custom
                    : ModelCredentialSource.InheritMain;
            }

        }
        catch
        {
            // Ignore migration failures; default config values remain valid.
        }

        return config;
    }

    private static bool HasCustomCredential(string apiKey, string baseUrl) =>
        !string.IsNullOrWhiteSpace(apiKey) || !string.IsNullOrWhiteSpace(baseUrl);

    private static bool TryGetString(JsonElement root, string propertyName, out string value)
    {
        value = string.Empty;
        if (root.TryGetProperty(propertyName, out var element) && element.ValueKind == JsonValueKind.String)
        {
            value = element.GetString() ?? string.Empty;
            return true;
        }

        return false;
    }

    private static bool TryGetInt(JsonElement root, string propertyName, out int value)
    {
        value = 0;
        if (root.TryGetProperty(propertyName, out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out value))
        {
            return true;
        }

        return false;
    }

    private static bool TryGetDouble(JsonElement root, string propertyName, out double value)
    {
        value = 0;
        if (root.TryGetProperty(propertyName, out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetDouble(out value))
        {
            return true;
        }

        return false;
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
