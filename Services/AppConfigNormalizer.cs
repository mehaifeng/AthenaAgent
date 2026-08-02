using System;
using Athena.UI.Models;

namespace Athena.UI.Services;

/// <summary>
/// 配置字段归一化：把越界/缺省值收敛到合法区间与默认值。
/// 保存前（防抖自动保存、扩展页浏览器诊断）与外部变更进入 UI 前共用同一套规则。
/// </summary>
public static class AppConfigNormalizer
{
    public static void NormalizeContextPolicy(AppConfig config)
    {
        config.ContextPolicy ??= new AppContextPolicy();
        var policy = config.ContextPolicy;
        if (policy.CustomCapTokens is < 1024) policy.CustomCapTokens = 1024;
        if (policy.CustomCompressionThresholdTokens is <= 0) policy.CustomCompressionThresholdTokens = 1;
        if (policy.CustomCapTokens.HasValue
            && policy.CustomCompressionThresholdTokens > policy.CustomCapTokens)
        {
            policy.CustomCompressionThresholdTokens = policy.CustomCapTokens;
        }
        policy.KeepRecentRounds = Math.Clamp(policy.KeepRecentRounds, 1, 50);
        policy.TargetSummaryTokens = Math.Clamp(policy.TargetSummaryTokens, 128, 65_536);

        // v6 过渡期兼容旧调用点；Phase 2 的 Policy Resolver 接入后删除这些镜像语义。
        config.AutoCompress = policy.AutoCompress;
        config.KeepRecentRounds = policy.KeepRecentRounds;
        config.MaxContextTokens = checked((int)Math.Min(
            policy.CustomCapTokens ?? 1_000_000,
            int.MaxValue));
        config.CompressionThreshold = checked((int)Math.Min(
            policy.CustomCompressionThresholdTokens ?? 262_144,
            config.MaxContextTokens));
    }

    /// <summary>钳制浏览器相关配置到合法区间。</summary>
    public static void NormalizeBrowser(AppConfig config)
    {
        config.BrowserViewportWidth = Math.Clamp(config.BrowserViewportWidth, 320, 3840);
        config.BrowserViewportHeight = Math.Clamp(config.BrowserViewportHeight, 240, 2160);
        config.BrowserMaxSteps = Math.Clamp(config.BrowserMaxSteps, 1, 50);
        config.BrowserOperationTimeoutSeconds = Math.Clamp(config.BrowserOperationTimeoutSeconds, 5, 300);
        config.BrowserSessionTtlMinutes = Math.Clamp(config.BrowserSessionTtlMinutes, 1, 120);
        config.BrowserScreenshotScale = Math.Clamp(config.BrowserScreenshotScale, 0.25, 2.0);
        config.BrowserImageQuality = Math.Clamp(config.BrowserImageQuality, 30, 100);
        config.BrowserSomMaxElements = Math.Clamp(config.BrowserSomMaxElements, 10, 200);
    }

}
