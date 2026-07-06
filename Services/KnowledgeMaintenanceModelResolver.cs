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
        // 先算出次级模型的有效凭据（次级自身也可能继承主 AI）——统一继承树的中间层。
        var secondary = ModelCredentialResolver.Resolve(
            config.SecondaryCredentialSource, config,
            config.SecondaryProvider, config.SecondaryBaseUrl, config.SecondaryApiKey,
            config.SecondaryModel);

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
                    BaseUrl = ModelCredentialResolver.FirstNonEmpty(config.KnowledgeMaintenanceBaseUrl, secondary.BaseUrl),
                    ApiKey = ModelCredentialResolver.FirstNonEmpty(config.KnowledgeMaintenanceApiKey, secondary.ApiKey),
                    Model = ModelCredentialResolver.FirstNonEmpty(config.KnowledgeMaintenanceModel, secondary.Model)
                };

            case KnowledgeMaintenanceModelSource.InheritSecondary:
            default:
                // 次级模型即"后台任务模型"：其有效凭据已含回退主 AI 的语义。
                return new EffectiveMaintenanceModel
                {
                    BaseUrl = secondary.BaseUrl,
                    ApiKey = secondary.ApiKey,
                    Model = secondary.Model
                };
        }
    }
}
