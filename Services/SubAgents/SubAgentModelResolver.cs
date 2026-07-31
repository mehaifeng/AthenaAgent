using Athena.UI.Models;

namespace Athena.UI.Services.SubAgents;

/// <summary>子代理模型的有效配置，由统一模型角色解析。</summary>
internal sealed class EffectiveSubAgentConfig
{
    public string BaseUrl { get; init; } = string.Empty;
    public string ApiKey { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public int MaxTokens { get; init; }
    public double Temperature { get; init; }
}

/// <summary>
/// 解析统一的子代理模型角色，避免独立凭据与主供应商配置分叉。
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
