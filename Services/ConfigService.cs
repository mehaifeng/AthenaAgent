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
        config.ConfigSchemaVersion = 7;
        AppConfigNormalizer.NormalizeContextPolicy(config);
        AppConfigNormalizer.NormalizeProtocol(config);
        AppConfigNormalizer.NormalizeVirtualPet(config);
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
                catch (IOException)
                {
                    // 文件被删除/占用时读不到写入时间：缓存判定失败即可，
                    // 走下面的完整重载路径，不是错误。
                }
                catch (UnauthorizedAccessException)
                {
                    // 同上：权限变化只影响这次缓存命中判断。
                }
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
        if (version > 8)
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
        var droppedLegacyCompressionKeys = MigrateLegacyCompressionKeys(root, config);
        if (version == 5)
        {
            var legacyMax = root?["maxContextTokens"]?.GetValue<int>() ?? 128_000;
            var legacyThreshold = root?["compressionThreshold"]?.GetValue<int>() ?? 64_000;
            var legacyAutoCompress = root?["autoCompress"]?.GetValue<bool>() ?? true;
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
                SummaryMaxTokens = AppContextPolicy.DefaultSummaryMaxTokens
            };
            config.ConfigSchemaVersion = 8;
            AppConfigNormalizer.MigrateBrowserDefaults(config);
            AppConfigNormalizer.NormalizeContextPolicy(config);
            AppConfigNormalizer.NormalizeProtocol(config);
            AppConfigNormalizer.NormalizeVirtualPet(config);
            migrated = true;
            return config;
        }

        if (version is 6 or 7)
        {
            // v6 → v7：浏览器智能体的旧默认值（12 步上限、不保留登录态）就地抬到新默认。
            // v7 → v8：SoM 标注上限 80 抬到 150——同一个一次性迁移，只改仍是旧默认的项。
            config.ConfigSchemaVersion = 8;
            AppConfigNormalizer.MigrateBrowserDefaults(config);
            AppConfigNormalizer.NormalizeContextPolicy(config);
            AppConfigNormalizer.NormalizeProtocol(config);
            AppConfigNormalizer.NormalizeVirtualPet(config);
            migrated = true;
            return config;
        }

        config.ConfigSchemaVersion = 8;
        AppConfigNormalizer.NormalizeContextPolicy(config);
        AppConfigNormalizer.NormalizeProtocol(config);
        AppConfigNormalizer.NormalizeVirtualPet(config);
        migrated = droppedLegacyCompressionKeys;
        return config;
    }

    /// <summary>
    /// 全量压缩取代了「保留轮数 / 压缩强度 / 摘要目标」：前两项读入后丢弃，旧的摘要目标（上限语义）
    /// 原样成为 <see cref="AppContextPolicy.SummaryMaxTokens"/>（由归一化夹取）。
    /// 新键已存在时以新键为准，所以迁移可重复执行。返回 true 表示读到了旧键，调用方应重写文件让它们消失。
    /// </summary>
    internal static bool MigrateLegacyCompressionKeys(JsonObject? root, AppConfig config)
    {
        if (root == null) return false;
        var found = root.ContainsKey("keepRecentRounds");
        // 工作区知识预算由 token 改为字符：旧值按 ×3 换算（中英文混合下的保守折中）。新键已存在时以新键为准。
        if (root.ContainsKey("workspaceKnowledgeTokenBudget"))
        {
            found = true;
            if (!root.ContainsKey("workspaceKnowledgeCharBudget")
                && root["workspaceKnowledgeTokenBudget"] is JsonValue legacyBudget
                && legacyBudget.TryGetValue<long>(out var legacyTokens)
                && legacyTokens >= 0)
            {
                config.WorkspaceKnowledgeCharBudget = (int)Math.Min(legacyTokens * 3, int.MaxValue);
            }
        }
        if (root["contextPolicy"] is JsonObject policy)
        {
            found |= policy.ContainsKey("keepRecentRounds")
                     | policy.ContainsKey("compressionStrength")
                     | policy.ContainsKey("targetSummaryTokens");
            if (!policy.ContainsKey("summaryMaxTokens")
                && policy["targetSummaryTokens"] is JsonValue legacy
                && legacy.TryGetValue<long>(out var legacyTarget))
            {
                config.ContextPolicy.SummaryMaxTokens = legacyTarget;
            }
        }
        if (found)
            Serilog.Log.Information("ContextPolicyMigrated: dropped keepRecentRounds/compressionStrength, targetSummaryTokens -> summaryMaxTokens, workspaceKnowledgeTokenBudget -> workspaceKnowledgeCharBudget (x3)");
        return found;
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
