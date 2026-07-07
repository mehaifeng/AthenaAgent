using Athena.UI.Models;
using Athena.UI.Services.Interfaces;

namespace Athena.UI.Services;

/// <summary>
/// 自备 Key（BYOK / Custom）模式下的统一凭据出口实现。
/// <para>
/// 这是订阅计划 Phase 1 的落点：把原先散落在各服务与各角色解析器里的凭据解析
/// 全部收敛到本类的 <see cref="Resolve"/> 一处。逐角色逻辑与重构前逐字节等价
/// （直接复用 <see cref="ModelCredentialResolver"/> / <see cref="AudioConfigResolver"/> 的既有继承树语义），
/// 保证纯重构无行为变化。Phase 3 会新增 Subscription 实现并由组合根按 <c>ModelAccessMode</c> 分发。
/// </para>
/// </summary>
public sealed class CustomModelEndpointResolver : IModelEndpointResolver
{
    /// <summary>无状态共享实例：供无 DI 的调用点（历史/测试）作为默认回退，避免重复构造。</summary>
    public static readonly CustomModelEndpointResolver Instance = new();

    public EffectiveModelConfig Resolve(ModelRole role, AppConfig config) => role switch
    {
        ModelRole.Primary => new EffectiveModelConfig(config.Provider, config.BaseUrl, config.ApiKey, config.Model),

        // 次级：留空字段逐字段回退主 AI（与 ConversationHistoryService 既有语义一致）。
        ModelRole.Secondary => ModelCredentialResolver.Resolve(
            config.SecondaryCredentialSource, config,
            config.SecondaryProvider, config.SecondaryBaseUrl, config.SecondaryApiKey,
            config.SecondaryModel),

        // 嵌入：无独立 provider 语义时 "Inherit" 归一化为空以触发回退（与 OpenAIEmbeddingService 一致）。
        ModelRole.Embedding => ModelCredentialResolver.Resolve(
            config.EmbeddingCredentialSource, config,
            config.EmbeddingProvider == "Inherit" ? string.Empty : config.EmbeddingProvider,
            config.EmbeddingBaseUrl, config.EmbeddingApiKey,
            config.EmbeddingModel),

        // 图像：无独立 provider 字段，传空即回退主配置；默认模型 gpt-image-1（与 OpenAIImageGenerationService 一致）。
        ModelRole.Image => ModelCredentialResolver.Resolve(
            config.ImageGenerationCredentialSource, config,
            string.Empty, config.ImageGenerationBaseUrl, config.ImageGenerationApiKey,
            config.ImageGenerationModel, "gpt-image-1"),

        ModelRole.Audio => ResolveAudio(config),
        ModelRole.SubAgent => ResolveSubAgent(config),
        ModelRole.BrowserAgent => ResolveBrowserAgent(config),
        ModelRole.Maintenance => ResolveMaintenance(config),

        _ => new EffectiveModelConfig(config.Provider, config.BaseUrl, config.ApiKey, config.Model)
    };

    // 音频例外：BaseUrl 是完整 /audio/speech 端点。凭据/端点/模型三元组仍由既有 AudioConfigResolver 决定；
    // Voice / AutoPlay 不是凭据，留给调用方（OpenAIChatService）读取配置组装 ResolvedAudioConfig。
    private static EffectiveModelConfig ResolveAudio(AppConfig config)
    {
        var audio = AudioConfigResolver.Resolve(config);
        return new EffectiveModelConfig(audio.Provider, audio.BaseUrl, audio.ApiKey, audio.Model);
    }

    // 子代理：跟随主模型或用子代理专属字段（逐字段回退主 AI）。credential 部分等价于原 SubAgentModelResolver。
    private static EffectiveModelConfig ResolveSubAgent(AppConfig config)
    {
        var source = config.SubAgentModelSource == SubAgentModelSource.InheritMain
            ? ModelCredentialSource.InheritMain
            : ModelCredentialSource.Custom;
        var model = config.SubAgentModelSource == SubAgentModelSource.InheritMain
            ? config.Model
            : config.SubAgentModel;
        return ModelCredentialResolver.Resolve(
            source, config,
            config.SubAgentProvider, config.SubAgentBaseUrl, config.SubAgentApiKey,
            model);
    }

    // 浏览器智能体：InheritMain 全量复用主 AI；否则逐字段回退，但 Model 恒取 BrowserAgentModel
    // （即使为空，保持与原 BrowserAgentModelResolver 完全一致，不额外回退到主模型名）。
    private static EffectiveModelConfig ResolveBrowserAgent(AppConfig config)
    {
        if (config.BrowserModelSource == BrowserModelSource.InheritMain)
        {
            return new EffectiveModelConfig(config.Provider, config.BaseUrl, config.ApiKey, config.Model);
        }

        var providerRaw = string.Equals(config.BrowserAgentProvider, "Inherit", System.StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : config.BrowserAgentProvider;
        var effective = ModelCredentialResolver.Resolve(
            ModelCredentialSource.Custom, config,
            providerRaw, config.BrowserAgentBaseUrl, config.BrowserAgentApiKey,
            config.BrowserAgentModel, config.BrowserAgentModel);
        return new EffectiveModelConfig(effective.Provider, effective.BaseUrl, effective.ApiKey, config.BrowserAgentModel);
    }

    // 知识库整理：先算次级有效凭据（次级本身也含回退主 AI 语义），再按整理来源开关叠加。
    private static EffectiveModelConfig ResolveMaintenance(AppConfig config)
    {
        var secondary = ModelCredentialResolver.Resolve(
            config.SecondaryCredentialSource, config,
            config.SecondaryProvider, config.SecondaryBaseUrl, config.SecondaryApiKey,
            config.SecondaryModel);

        return config.KnowledgeMaintenanceModelSource switch
        {
            KnowledgeMaintenanceModelSource.InheritMain =>
                new EffectiveModelConfig(config.Provider, config.BaseUrl, config.ApiKey, config.Model),

            KnowledgeMaintenanceModelSource.Custom =>
                new EffectiveModelConfig(
                    secondary.Provider,
                    ModelCredentialResolver.FirstNonEmpty(config.KnowledgeMaintenanceBaseUrl, secondary.BaseUrl),
                    ModelCredentialResolver.FirstNonEmpty(config.KnowledgeMaintenanceApiKey, secondary.ApiKey),
                    ModelCredentialResolver.FirstNonEmpty(config.KnowledgeMaintenanceModel, secondary.Model)),

            // InheritSecondary 及默认：直接用次级有效凭据。
            _ => secondary
        };
    }
}
