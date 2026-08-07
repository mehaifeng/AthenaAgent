using Athena.UI.Services.ConfigSurface;
using Athena.UI.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using System;
using System.Threading.Tasks;

namespace Athena.UI.Services.Functions;

/// <summary>
/// 配置管理相关的 Function Calling 实现。
/// 可改的配置面由 <see cref="ConfigFieldCatalog"/> 声明式目录驱动：
/// 视图 = 目录投影 + 摘要（不暴露 config.json 原文与派生数据），修改 = 目录驱动的类型安全赋值。
/// </summary>
public class ConfigurationFunctions
{
    private readonly IConfigService _configService;
    private readonly IConfigSurfaceService _configSurface;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger _logger;

    // 延迟获取 ChatService 以避免循环依赖
    private IChatService? _chatService;

    public ConfigurationFunctions(
        IConfigService configService,
        IConfigSurfaceService configSurface,
        IServiceProvider serviceProvider,
        ILogger logger)
    {
        _configService = configService;
        _configSurface = configSurface;
        _serviceProvider = serviceProvider;
        _logger = logger.ForContext<ConfigurationFunctions>();
    }

    /// <summary>供 FunctionRegistry 生成 schema 时枚举合法键。</summary>
    public string ModifiableKeysText => string.Join(", ", _configSurface.ModifiableKeys);

    /// <summary>供 FunctionRegistry 生成 schema 时枚举合法分区。</summary>
    public string SectionsText => string.Join(", ", _configSurface.Sections);

    private IChatService ChatService => _chatService ??= _serviceProvider.GetRequiredService<IChatService>();

    /// <summary>
    /// 修改应用配置。key 必须在 <see cref="ConfigFieldCatalog"/> 中登记且可修改；
    /// value 按字段类型解析（bool/整数/浮点/枚举名/字符串/JSON 数组或逗号分隔列表），并做取值与范围校验。
    /// </summary>
    public async Task<FunctionResult> ModifyAppConfig(string key, string value)
    {
        try
        {
            var config = await _configService.LoadAsync();
            var result = _configSurface.Apply(config, key, value);
            if (!result.Success)
            {
                return FunctionResult.FailureResult(result.Message, result.Data);
            }

            await _configService.SaveAsync(config);

            // 同步更新 ChatService
            ChatService.UpdateConfig(config);

            _logger.Information("Function: updated configuration {Key} = {Value}", key, value);

            return FunctionResult.SuccessResult(result.Message, result.Data);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to update configuration");
            return FunctionResult.FailureResult($"修改失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 获取当前配置投影（分区 = AI / Context / Appearance / ...，或 All）。
    /// 投影由目录生成，绝不包含 API Key / Token / 模型目录全文；派生数据只以摘要形式出现。
    /// </summary>
    public async Task<FunctionResult> GetAppConfig(string? section = null)
    {
        try
        {
            var config = await _configService.LoadAsync();
            if (!ConfigFieldCatalog.TryResolveSection(section, out _))
            {
                return FunctionResult.FailureResult(
                    $"未知的配置部分: {section}。可选: All, {SectionsText}");
            }

            return FunctionResult.SuccessResult("获取配置成功", _configSurface.BuildView(config, section));
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to retrieve configuration");
            return FunctionResult.FailureResult($"获取失败: {ex.Message}");
        }
    }
}
