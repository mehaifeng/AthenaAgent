namespace Athena.UI.Services.Interfaces;

/// <summary>
/// 隐藏功能开关（订阅计划 Phase 0）。用于在分支开发期间把尚未发布的入口默认隐藏，
/// 使分支可随时安全合回 main 而不提前曝光。
/// </summary>
public interface IFeatureFlagService
{
    /// <summary>
    /// 订阅托管模式预览开关。默认 <c>false</c>。
    /// 关闭时：不注册账户服务 UI 入口、不显示模式切换，行为与主干完全一致。
    /// 来源：环境变量 <c>ATHENA_SUBSCRIPTION_PREVIEW=1</c> 或 <c>AthenaData/feature-flags.json</c> 的 <c>subscriptionPreview</c> 字段。
    /// </summary>
    bool SubscriptionFeatureEnabled { get; }
}
