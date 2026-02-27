using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using System;
using System.Reflection;
using System.Threading.Tasks;

namespace Athena.UI.Services.Functions;

/// <summary>
/// 配置管理相关的 Function Calling 实现
/// </summary>
public class ConfigurationFunctions
{
    private readonly IConfigService _configService;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger _logger;

    // 延迟获取 ChatService 以避免循环依赖
    private IChatService? _chatService;

    // 可修改的配置项白名单
    private static readonly string[] AllowedConfigKeys =
    {
        "Temperature", "MaxTokens", "TopP",
        "Theme", "FontSize", "ShowHeartbeatButton", "Language",
        "MaxContextTokens", "CompressionThreshold", "AutoCompress"
    };

    public ConfigurationFunctions(IConfigService configService, IServiceProvider serviceProvider, ILogger logger)
    {
        _configService = configService;
        _serviceProvider = serviceProvider;
        _logger = logger.ForContext<ConfigurationFunctions>();
    }

    /// <summary>
    /// 获取 ChatService（延迟加载）
    /// </summary>
    private IChatService ChatService => _chatService ??= _serviceProvider.GetRequiredService<IChatService>();

    /// <summary>
    /// 修改应用配置
    /// </summary>
    /// <param name="key">配置项名称</param>
    /// <param name="value">新值</param>
    /// <returns>操作结果</returns>
    public async Task<FunctionResult> ModifyAppConfig(string key, string value)
    {
        try
        {
            // 安全检查：只允许修改白名单中的配置项
            if (!Array.Exists(AllowedConfigKeys, k => k.Equals(key, StringComparison.OrdinalIgnoreCase)))
            {
                return FunctionResult.FailureResult(
                    $"不允许修改配置项: {key}。允许的配置项: {string.Join(", ", AllowedConfigKeys)}");
            }

            var config = await _configService.LoadAsync();
            var property = typeof(AppConfig).GetProperty(key, BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);

            if (property == null)
            {
                return FunctionResult.FailureResult($"未找到配置项: {key}");
            }

            // 转换值类型
            var convertedValue = ConvertValue(value, property.PropertyType);
            property.SetValue(config, convertedValue);

            // 保存配置
            await _configService.SaveAsync(config);

            // 同步更新 ChatService
            ChatService.UpdateConfig(config);

            _logger.Information("Function: 修改配置 {Key} = {Value}", key, value);

            return FunctionResult.SuccessResult(
                $"已更新 {key} = {value}",
                new { key, value, updated = true });
        }
        catch (FormatException)
        {
            return FunctionResult.FailureResult($"值格式错误: {value} 无法转换为 {key} 的类型");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "修改配置失败");
            return FunctionResult.FailureResult($"修改失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 获取当前配置
    /// </summary>
    /// <param name="section">配置部分（可选，如 "AI", "Appearance", "Memory"）</param>
    /// <returns>配置信息</returns>
    public async Task<FunctionResult> GetAppConfig(string? section = null)
    {
        try
        {
            var config = await _configService.LoadAsync();

            object? result;

            if (string.IsNullOrEmpty(section) || section.ToLower() == "all")
            {
                // 返回所有配置（但隐藏敏感信息）
                result = new
                {
                    // AI 配置
                    AI = new
                    {
                        Provider = config.Provider,
                        Model = config.Model,
                        Temperature = config.Temperature,
                        MaxTokens = config.MaxTokens,
                        TopP = config.TopP,
                        Timeout = config.Timeout,
                        EnableFunctionCalling = config.EnableFunctionCalling
                        // 注意：不返回 ApiKey 和 BaseUrl
                    },
                    // 外观配置
                    Appearance = new
                    {
                        Theme = config.Theme,
                        FontSize = config.FontSize,
                        ShowHeartbeatButton = config.ShowHeartbeatButton,
                        Language = config.Language
                    },
                    // 记忆配置
                    Memory = new
                    {
                        MaxContextTokens = config.MaxContextTokens,
                        CompressionThreshold = config.CompressionThreshold,
                        AutoCompress = config.AutoCompress
                    }
                };
            }
            else
            {
                result = section.ToLower() switch
                {
                    "ai" => new
                    {
                        Provider = config.Provider,
                        Model = config.Model,
                        Temperature = config.Temperature,
                        MaxTokens = config.MaxTokens,
                        TopP = config.TopP,
                        Timeout = config.Timeout,
                        EnableFunctionCalling = config.EnableFunctionCalling
                    },
                    "appearance" => new
                    {
                        Theme = config.Theme,
                        FontSize = config.FontSize,
                        ShowHeartbeatButton = config.ShowHeartbeatButton,
                        Language = config.Language
                    },
                    "memory" => new
                    {
                        MaxContextTokens = config.MaxContextTokens,
                        CompressionThreshold = config.CompressionThreshold,
                        AutoCompress = config.AutoCompress
                    },
                    _ => (object?)null
                };

                if (result == null)
                {
                    return FunctionResult.FailureResult($"未知的配置部分: {section}。可选: AI, Appearance, Memory, All");
                }
            }

            _logger.Information("Function: 获取配置 section={Section}", section ?? "All");

            return FunctionResult.SuccessResult("获取配置成功", result);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "获取配置失败");
            return FunctionResult.FailureResult($"获取失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 转换值类型
    /// </summary>
    private object ConvertValue(string value, Type targetType)
    {
        if (targetType == typeof(string))
            return value;

        if (targetType == typeof(int))
            return int.Parse(value);

        if (targetType == typeof(double))
            return double.Parse(value);

        if (targetType == typeof(bool))
            return bool.Parse(value);

        // 尝试其他类型
        return System.Convert.ChangeType(value, targetType);
    }
}
