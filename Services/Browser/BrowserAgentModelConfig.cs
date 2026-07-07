using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using System;

namespace Athena.UI.Services.Browser;

/// <summary>
/// 浏览器智能体模型的有效配置：合并"来源开关 + 逐字段回退"后的最终结果。
/// </summary>
internal sealed class EffectiveBrowserAgentConfig
{
    public string Provider { get; init; } = string.Empty;
    public string BaseUrl { get; init; } = string.Empty;
    public string ApiKey { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public int MaxTokens { get; init; }
    public double Temperature { get; init; }
    public string BaseUrlSource { get; init; } = string.Empty;
    public string ApiKeySource { get; init; } = string.Empty;
}

/// <summary>
/// 解析浏览器智能体模型的有效配置。供任务规划器与执行循环共用，避免逻辑分叉
/// （历史上两处各有一份副本，导致"跟随主模型"只在其中一处生效）。
/// 凭据/端点/模型委托统一凭据出口 <see cref="IModelEndpointResolver"/>（订阅计划 Phase 1）；
/// 采样参数（输出 token 预算、温度）与诊断用来源标签仍在本地按配置计算。
/// </summary>
internal static class BrowserAgentModelResolver
{
    public static EffectiveBrowserAgentConfig Resolve(AppConfig config, IModelEndpointResolver endpointResolver)
    {
        var cred = endpointResolver.Resolve(ModelRole.BrowserAgent, config);

        // 诊断用来源标签：Custom 模式下沿用既有语义（是否填了浏览器专属字段）。
        var inherit = config.BrowserModelSource == BrowserModelSource.InheritMain;
        var baseUrlFromBrowser = !inherit && !string.IsNullOrWhiteSpace(config.BrowserAgentBaseUrl);
        var apiKeyFromBrowser = !inherit && !string.IsNullOrWhiteSpace(config.BrowserAgentApiKey);

        return new EffectiveBrowserAgentConfig
        {
            Provider = cred.Provider,
            BaseUrl = cred.BaseUrl,
            ApiKey = cred.ApiKey,
            Model = cred.Model,
            MaxTokens = config.BrowserAgentMaxTokens,
            Temperature = config.BrowserAgentTemperature,
            BaseUrlSource = inherit ? "MainAI(Inherit)" : (baseUrlFromBrowser ? "BrowserAgent" : "MainAI"),
            ApiKeySource = inherit ? "MainAI(Inherit)" : (apiKeyFromBrowser ? "BrowserAgent" : "MainAI")
        };
    }
}
