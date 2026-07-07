using Athena.UI.Models;
using Athena.UI.Services.Interfaces;

namespace Athena.UI.Services;

/// <summary>知识库整理 Agent 的有效模型配置（合并"来源开关 + 逐字段回退"后的最终结果）。</summary>
internal sealed class EffectiveMaintenanceModel
{
    public string BaseUrl { get; init; } = string.Empty;
    public string ApiKey { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
}

/// <summary>
/// 解析整理 Agent 模型：凭据/端点/模型委托统一凭据出口 <see cref="IModelEndpointResolver"/>（订阅计划 Phase 1）。
/// 逐来源开关（跟随次级 / 跟随主 / 自定义）的语义在 <see cref="CustomModelEndpointResolver"/> 内实现，行为不变。
/// </summary>
internal static class KnowledgeMaintenanceModelResolver
{
    public static EffectiveMaintenanceModel Resolve(AppConfig config, IModelEndpointResolver endpointResolver)
    {
        var cred = endpointResolver.Resolve(ModelRole.Maintenance, config);
        return new EffectiveMaintenanceModel
        {
            BaseUrl = cred.BaseUrl,
            ApiKey = cred.ApiKey,
            Model = cred.Model
        };
    }
}
