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
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IPlatformPathService _platformPathService;

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

        try
        {
            var json = await File.ReadAllTextAsync(ConfigFilePath);
            var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
            return config ?? new AppConfig();
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

        try
        {
            var json = File.ReadAllText(ConfigFilePath);
            var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
            return config ?? new AppConfig();
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
    }
}
