using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
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
/// 解析子代理模型配置：凭据/端点/模型委托统一凭据出口 <see cref="IModelEndpointResolver"/>
/// （订阅计划 Phase 1，使订阅模式下走网关而非 BYOK）；采样参数（token 预算、温度）保留子代理专属。
/// </summary>
internal static class SubAgentModelResolver
{
    public static EffectiveSubAgentConfig Resolve(AppConfig config, IModelEndpointResolver endpointResolver)
    {
        var cred = endpointResolver.Resolve(ModelRole.SubAgent, config);
        return new EffectiveSubAgentConfig
        {
            BaseUrl = cred.BaseUrl,
            ApiKey = cred.ApiKey,
            Model = cred.Model,
            MaxTokens = config.SubAgentMaxTokens,
            Temperature = config.SubAgentTemperature
        };
    }
}
