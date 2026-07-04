using Athena.UI.Models;

namespace Athena.UI.Services;

/// <summary>知识库整理 Agent 的有效模型配置（合并"来源开关 + 逐字段回退"后的最终结果）。</summary>
internal sealed class EffectiveMaintenanceModel
{
    public string BaseUrl { get; init; } = string.Empty;
    public string ApiKey { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
}

/// <summary>
/// 解析整理 Agent 模型：默认跟随次级（后台）模型；次级字段留空时逐字段回退主 AI；
/// Custom 时用整理专属字段（同样逐字段回退）。与 SubAgentModelResolver 同构。
/// </summary>
internal static class KnowledgeMaintenanceModelResolver
{
    public static EffectiveMaintenanceModel Resolve(AppConfig config)
    {
        switch (config.KnowledgeMaintenanceModelSource)
        {
            case KnowledgeMaintenanceModelSource.InheritMain:
                return new EffectiveMaintenanceModel
                {
                    BaseUrl = config.BaseUrl,
                    ApiKey = config.ApiKey,
                    Model = config.Model
                };

            case KnowledgeMaintenanceModelSource.Custom:
                return new EffectiveMaintenanceModel
                {
                    BaseUrl = FirstNonEmpty(config.KnowledgeMaintenanceBaseUrl, config.SecondaryBaseUrl, config.BaseUrl),
                    ApiKey = FirstNonEmpty(config.KnowledgeMaintenanceApiKey, config.SecondaryApiKey, config.ApiKey),
                    Model = FirstNonEmpty(config.KnowledgeMaintenanceModel, config.SecondaryModel, config.Model)
                };

            case KnowledgeMaintenanceModelSource.InheritSecondary:
            default:
                // 次级模型即"后台任务模型"：留空字段回退主 AI（与 ConversationHistoryService 的次级客户端一致）。
                return new EffectiveMaintenanceModel
                {
                    BaseUrl = FirstNonEmpty(config.SecondaryBaseUrl, config.BaseUrl),
                    ApiKey = FirstNonEmpty(config.SecondaryApiKey, config.ApiKey),
                    Model = FirstNonEmpty(config.SecondaryModel, config.Model)
                };
        }
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrWhiteSpace(v)) return v;
        }
        return string.Empty;
    }
}
