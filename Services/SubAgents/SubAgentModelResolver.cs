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
        var effective = OpenAiModelRuntimeFactory.Resolve(config, AiModelRole.SubAgent);

        return new EffectiveSubAgentConfig
        {
            BaseUrl = effective.BaseUrl,
            ApiKey = effective.ApiKey,
            Model = effective.Model,
            MaxTokens = effective.MaxOutputTokens,
            Temperature = effective.Temperature
        };
    }
}
