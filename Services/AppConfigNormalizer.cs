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

    /// <summary>钳制终端输出截断上限到合法区间（1K~1M 字符，防止手滑填 0 或超大值）。</summary>
    public static void NormalizeSecurity(AppConfig config)
    {
        config.MaxTerminalOutputChars = Math.Clamp(config.MaxTerminalOutputChars, 1_000, 1_000_000);
    }

    /// <summary>迁移第一阶段的临时猫头鹰并钳制窗口内宠物尺寸。</summary>
    public static void NormalizeVirtualPet(AppConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.VirtualPetSlug)
            || config.VirtualPetSlug.Equals("athena-owl", StringComparison.OrdinalIgnoreCase))
            config.VirtualPetSlug = "boba";
        config.VirtualPetScale = Math.Clamp(
            double.IsFinite(config.VirtualPetScale) ? config.VirtualPetScale : 0.5,
            0.25,
            1.0);
        if (config.VirtualPetRoamArea is not (VirtualPetRoamArea.LowerHalf
            or VirtualPetRoamArea.LogTerminalBottom
            or VirtualPetRoamArea.SessionListBottom))
            config.VirtualPetRoamArea = VirtualPetRoamArea.LowerHalf;
    }

    /// <summary>协议枚举归一化：config.json 手改出非法数字时钳回 Auto。</summary>
    public static void NormalizeProtocol(AppConfig config)
    {
        if (config.AiModels?.Providers == null) return;
        foreach (var provider in config.AiModels.Providers)
        {
            if (provider.Protocol is not (ProviderProtocol.Auto
                or ProviderProtocol.ChatCompletions
                or ProviderProtocol.Responses))
            {
                provider.Protocol = ProviderProtocol.Auto;
            }
        }
    }

}
