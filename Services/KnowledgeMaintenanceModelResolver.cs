using Athena.UI.Models;

namespace Athena.UI.Services;

/// <summary>知识库整理 Agent 的有效模型配置，由统一模型角色解析。</summary>
internal sealed class EffectiveMaintenanceModel
{
    public string BaseUrl { get; init; } = string.Empty;
    public string ApiKey { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public int MaxOutputTokens { get; init; }
    public double Temperature { get; init; }
}

/// <summary>
/// 解析统一的知识整理模型角色，避免独立凭据与主供应商配置分叉。
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
