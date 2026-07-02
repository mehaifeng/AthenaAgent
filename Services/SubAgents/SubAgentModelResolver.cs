using Athena.UI.Models;
using System;

namespace Athena.UI.Services.SubAgents;

/// <summary>子代理模型的有效配置（合并"来源开关 + 逐字段回退"后的最终结果）。</summary>
internal sealed class EffectiveSubAgentConfig
{
    public string BaseUrl { get; init; } = string.Empty;
    public string ApiKey { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public int MaxTokens { get; init; }
    public double Temperature { get; init; }
}

/// <summary>
/// 解析子代理模型配置：默认跟随主模型；Custom 时使用子代理专属字段，逐字段在留空时回退主 AI。
/// 与 BrowserAgentModelResolver 同构，避免逻辑分叉。
/// </summary>
internal static class SubAgentModelResolver
{
    public static EffectiveSubAgentConfig Resolve(AppConfig config)
    {
        if (config.SubAgentModelSource == SubAgentModelSource.InheritMain)
        {
            return new EffectiveSubAgentConfig
            {
                BaseUrl = config.BaseUrl,
                ApiKey = config.ApiKey,
                Model = config.Model,
                MaxTokens = config.SubAgentMaxTokens,
                Temperature = config.SubAgentTemperature
            };
        }

        var apiKeyFromSub = !string.IsNullOrWhiteSpace(config.SubAgentApiKey);
        var baseUrlFromSub = !string.IsNullOrWhiteSpace(config.SubAgentBaseUrl);
        return new EffectiveSubAgentConfig
        {
            BaseUrl = baseUrlFromSub ? config.SubAgentBaseUrl : config.BaseUrl,
            ApiKey = apiKeyFromSub ? config.SubAgentApiKey : config.ApiKey,
            Model = string.IsNullOrWhiteSpace(config.SubAgentModel) ? config.Model : config.SubAgentModel,
            MaxTokens = config.SubAgentMaxTokens,
            Temperature = config.SubAgentTemperature
        };
    }
}
