using System;
using System.Collections.Generic;
using Athena.UI.Models;

namespace Athena.UI.Services;

public static class AudioConfigResolver
{
    private static readonly Dictionary<string, string> AudioProviderUrls = new(StringComparer.OrdinalIgnoreCase)
    {
        ["OpenAI"] = "https://api.openai.com/v1/audio/speech",
        ["OpenRouter"] = "https://openrouter.ai/api/v1/audio/speech",
        ["System"] = string.Empty,
        ["Custom"] = string.Empty
    };

    /// <summary>音频供应商候选（配置 UI 下拉框数据源，顺序即展示顺序）。</summary>
    public static IReadOnlyList<string> ProviderNames { get; } =
        new[] { "OpenAI", "OpenRouter", "System", "Custom" };

    public static ResolvedAudioConfig Resolve(AppConfig config)
    {
        // 语音播报使用独立凭据，不继承主对话模型；BaseUrl 留空时按音频 provider 的默认端点解析。
        var provider = string.IsNullOrWhiteSpace(config.ChatAudioProvider)
            ? "OpenAI"
            : config.ChatAudioProvider;
        var baseUrl = !string.IsNullOrWhiteSpace(config.ChatAudioBaseUrl)
            ? config.ChatAudioBaseUrl
            : GetDefaultBaseUrl(provider);
        var apiKey = config.ChatAudioApiKey;
        var model = string.IsNullOrWhiteSpace(config.ChatAudioModel)
            ? "gpt-4o-mini-tts"
            : config.ChatAudioModel;
        var voice = string.IsNullOrWhiteSpace(config.ChatAudioVoice)
            ? "alloy"
            : config.ChatAudioVoice;

        return new ResolvedAudioConfig(
            provider,
            baseUrl,
            apiKey,
            model,
            voice,
            config.ChatAudioAutoPlay);
    }

    /// <summary>按供应商取默认端点（仅精确匹配，供应商切换回填用；未知供应商返回 false 不动原值）。</summary>
    public static bool TryGetDefaultBaseUrl(string? provider, out string url)
        => AudioProviderUrls.TryGetValue(provider ?? string.Empty, out url!);

    public static string GetDefaultBaseUrl(string? provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            return AudioProviderUrls["OpenAI"];
        }

        return AudioProviderUrls.TryGetValue(provider, out var url)
            ? url
            : AudioProviderUrls["OpenAI"];
    }
}

public sealed record ResolvedAudioConfig(
    string Provider,
    string BaseUrl,
    string ApiKey,
    string Model,
    string Voice,
    bool AutoPlay);
