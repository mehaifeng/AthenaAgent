using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
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
        if (TryGetCached(out var cached)) return cached;
        if (!File.Exists(ConfigFilePath)) return GetOrCreateDefault();

        try
        {
            var writeTimeUtc = File.GetLastWriteTimeUtc(ConfigFilePath);
            var config = DeserializeConfig(await File.ReadAllTextAsync(ConfigFilePath).ConfigureAwait(false), out var migrated);
            if (migrated)
            {
                await WriteAtomicallyAsync(config).ConfigureAwait(false);
                writeTimeUtc = File.GetLastWriteTimeUtc(ConfigFilePath);
            }
            StoreCache(config, writeTimeUtc);
            return config;
        }
        catch (UnsupportedConfigSchemaException)
        {
            throw;
        }
        catch
        {
            BackupExistingConfig("damaged");
            return GetOrCreateDefault();
        }
    }

    public AppConfig Load()
    {
        if (TryGetCached(out var cached)) return cached;
        if (!File.Exists(ConfigFilePath)) return GetOrCreateDefault();

        try
        {
            var writeTimeUtc = File.GetLastWriteTimeUtc(ConfigFilePath);
            var config = DeserializeConfig(File.ReadAllText(ConfigFilePath), out var migrated);
            if (migrated)
            {
                WriteAtomicallyAsync(config).GetAwaiter().GetResult();
                writeTimeUtc = File.GetLastWriteTimeUtc(ConfigFilePath);
            }
            StoreCache(config, writeTimeUtc);
            return config;
        }
        catch (UnsupportedConfigSchemaException)
        {
            throw;
        }
        catch
        {
            BackupExistingConfig("damaged");
            return GetOrCreateDefault();
        }
    }

    public async Task SaveAsync(AppConfig config)
    {
        config.ConfigSchemaVersion = 6;
        AppConfigNormalizer.NormalizeContextPolicy(config);
        AppConfigNormalizer.NormalizeProtocol(config);
        await WriteAtomicallyAsync(config).ConfigureAwait(false);
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
                if (!File.Exists(ConfigFilePath))
                {
                    config = _cachedConfig;
                    return true;
                }

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

    private AppConfig GetOrCreateDefault()
    {
        lock (_cacheLock)
        {
            return _cachedConfig ??= new AppConfig();
        }
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

    private AppConfig DeserializeConfig(string json, out bool migrated)
    {
        var root = JsonNode.Parse(json) as JsonObject;
        var version = root?["configSchemaVersion"]?.GetValue<int>() ?? 0;
        if (version > 6)
        {
            BackupExistingConfig($"future-v{version}");
            throw new UnsupportedConfigSchemaException(version);
        }

        if (version < 5)
        {
            BackupExistingConfig($"legacy-v{version}");
            migrated = true;
            return new AppConfig();
        }

        var config = root?.Deserialize<AppConfig>(JsonOptions) ?? new AppConfig();
        if (version == 5)
        {
            var legacyMax = root?["maxContextTokens"]?.GetValue<int>() ?? 128_000;
            var legacyThreshold = root?["compressionThreshold"]?.GetValue<int>() ?? 64_000;
            var legacyAutoCompress = root?["autoCompress"]?.GetValue<bool>() ?? true;
            var legacyKeepRecentRounds = root?["keepRecentRounds"]?.GetValue<int>() ?? 3;
            config.ContextPolicy = new AppContextPolicy
            {
                Mode = legacyMax == 128_000 && legacyThreshold == 64_000
                    ? ContextPolicyMode.Auto
                    : ContextPolicyMode.LegacyCustom,
                CustomCapTokens = legacyMax == 128_000 && legacyThreshold == 64_000 ? null : legacyMax,
                CompressionThresholdMode = legacyMax == 128_000 && legacyThreshold == 64_000
                    ? CompressionThresholdMode.Auto
                    : CompressionThresholdMode.Custom,
                CustomCompressionThresholdTokens = legacyMax == 128_000 && legacyThreshold == 64_000
                    ? null
                    : legacyThreshold,
                AutoCompress = legacyAutoCompress,
                KeepRecentRounds = legacyKeepRecentRounds,
                TargetSummaryTokens = 8192
            };
            config.ConfigSchemaVersion = 6;
            AppConfigNormalizer.NormalizeContextPolicy(config);
            AppConfigNormalizer.NormalizeProtocol(config);
            migrated = true;
            return config;
        }

        config.ConfigSchemaVersion = 6;
        AppConfigNormalizer.NormalizeContextPolicy(config);
        AppConfigNormalizer.NormalizeProtocol(config);
        migrated = false;
        return config;
    }

    private async Task WriteAtomicallyAsync(AppConfig config, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(ConfigFilePath)
                        ?? throw new InvalidOperationException("Config path has no parent directory.");
        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(ConfigFilePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.WriteThrough | FileOptions.Asynchronous);
            await using (stream.ConfigureAwait(false))
            {
                await JsonSerializer.SerializeAsync(stream, config, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(ConfigFilePath))
            {
                File.Copy(ConfigFilePath, ConfigFilePath + ".bak", overwrite: true);
            }
            File.Move(tempPath, ConfigFilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    private void BackupExistingConfig(string reason)
    {
        try
        {
            if (!File.Exists(ConfigFilePath)) return;
            var backupPath = $"{ConfigFilePath}.{reason}.{DateTime.UtcNow:yyyyMMddHHmmssfff}.bak";
            File.Copy(ConfigFilePath, backupPath, overwrite: false);
        }
        catch
        {
            // 加载仍需可降级；备份失败不会把损坏内容覆盖成新配置。
        }
    }
}

public sealed class UnsupportedConfigSchemaException(int version)
    : InvalidOperationException($"Configuration schema v{version} is newer than supported v6.")
{
    public int Version { get; } = version;
}
