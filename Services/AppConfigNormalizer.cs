using System;
using Athena.UI.Models;

namespace Athena.UI.Services;

/// <summary>
/// 配置字段归一化：把越界/缺省值收敛到合法区间与默认值。
/// 保存前（防抖自动保存、扩展页浏览器诊断）与外部变更进入 UI 前共用同一套规则。
/// </summary>
public static class AppConfigNormalizer
{
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

    /// <summary>补齐语音播报的缺省字段（与 <see cref="AudioConfigResolver"/> 的运行期回退保持一致）。</summary>
    public static void NormalizeAudio(AppConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.ChatAudioProvider))
        {
            config.ChatAudioProvider = "OpenAI";
        }

        if (string.IsNullOrWhiteSpace(config.ChatAudioBaseUrl))
        {
            config.ChatAudioBaseUrl = AudioConfigResolver.GetDefaultBaseUrl(config.ChatAudioProvider);
        }

        if (string.IsNullOrWhiteSpace(config.ChatAudioModel))
        {
            config.ChatAudioModel = "gpt-4o-mini-tts";
        }
    }
}
