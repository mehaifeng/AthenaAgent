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
        // 浏览器智能体使用独立凭据，不继承主对话模型；遗留 provider="Inherit" 字符串归一化为 OpenAI。
        var provider = string.IsNullOrWhiteSpace(config.BrowserAgentProvider)
            || string.Equals(config.BrowserAgentProvider, "Inherit", StringComparison.OrdinalIgnoreCase)
            ? "OpenAI"
            : config.BrowserAgentProvider;

        return new EffectiveBrowserAgentConfig
        {
            Provider = provider,
            BaseUrl = ModelCredentialResolver.FirstNonEmpty(config.BrowserAgentBaseUrl, "https://api.openai.com/v1"),
            ApiKey = config.BrowserAgentApiKey,
            Model = ModelCredentialResolver.FirstNonEmpty(config.BrowserAgentModel, "gpt-4o-mini"),
            MaxTokens = config.BrowserAgentMaxTokens,
            Temperature = config.BrowserAgentTemperature,
            BaseUrlSource = "BrowserAgent",
            ApiKeySource = "BrowserAgent"
        };
    }
}
