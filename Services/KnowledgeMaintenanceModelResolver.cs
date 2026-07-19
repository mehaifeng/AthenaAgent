using Athena.UI.Models;

namespace Athena.UI.Services;

/// <summary>知识库整理 Agent 的有效模型配置（合并"来源开关 + 逐字段回退"后的最终结果）。</summary>
internal sealed class EffectiveMaintenanceModel
{
    public string BaseUrl { get; init; } = string.Empty;
    public string ApiKey { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public int MaxOutputTokens { get; init; }
    public double Temperature { get; init; }
}

/// <summary>
/// 解析整理 Agent 模型：默认跟随次级（后台）模型；次级字段留空时逐字段回退主 AI；
/// Custom 时用整理专属字段（同样逐字段回退）。与 SubAgentModelResolver 同构。
/// </summary>
internal static class KnowledgeMaintenanceModelResolver
{
    public static EffectiveMaintenanceModel Resolve(AppConfig config)
    {
        var effective = OpenAiModelRuntimeFactory.Resolve(config, AiModelRole.KnowledgeMaintenance);
        return new EffectiveMaintenanceModel
        {
            BaseUrl = effective.BaseUrl,
            ApiKey = effective.ApiKey,
            Model = effective.Model,
            MaxOutputTokens = effective.MaxOutputTokens,
            Temperature = effective.Temperature
        };
    }
}
