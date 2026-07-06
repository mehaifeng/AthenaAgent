using Athena.UI.Models;
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
/// </summary>
internal static class BrowserAgentModelResolver
{
    public static EffectiveBrowserAgentConfig Resolve(AppConfig config)
    {
        // 跟随主模型：Provider/BaseUrl/ApiKey/Model 全部复用主 AI；仅保留浏览器循环
        // 专属的采样参数（输出 token 预算、温度），它们是智能体调参而非模型身份。
        if (config.BrowserModelSource == BrowserModelSource.InheritMain)
        {
            return new EffectiveBrowserAgentConfig
            {
                Provider = config.Provider,
                BaseUrl = config.BaseUrl,
                ApiKey = config.ApiKey,
                Model = config.Model,
                MaxTokens = config.BrowserAgentMaxTokens,
                Temperature = config.BrowserAgentTemperature,
                BaseUrlSource = "MainAI(Inherit)",
                ApiKeySource = "MainAI(Inherit)"
            };
        }

        // 自定义：凭据委托统一继承树解析器（逐字段留空回退主 AI，保持向后兼容）；
        // 遗留 provider="Inherit" 字符串在此归一化为空以触发回退。
        var apiKeyFromBrowser = !string.IsNullOrWhiteSpace(config.BrowserAgentApiKey);
        var baseUrlFromBrowser = !string.IsNullOrWhiteSpace(config.BrowserAgentBaseUrl);
        var providerRaw = string.Equals(config.BrowserAgentProvider, "Inherit", StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : config.BrowserAgentProvider;
        var effective = ModelCredentialResolver.Resolve(
            ModelCredentialSource.Custom, config,
            providerRaw, config.BrowserAgentBaseUrl, config.BrowserAgentApiKey,
            config.BrowserAgentModel, config.BrowserAgentModel);

        return new EffectiveBrowserAgentConfig
        {
            Provider = effective.Provider,
            BaseUrl = effective.BaseUrl,
            ApiKey = effective.ApiKey,
            Model = config.BrowserAgentModel,
            MaxTokens = config.BrowserAgentMaxTokens,
            Temperature = config.BrowserAgentTemperature,
            BaseUrlSource = baseUrlFromBrowser ? "BrowserAgent" : "MainAI",
            ApiKeySource = apiKeyFromBrowser ? "BrowserAgent" : "MainAI"
        };
    }
}
