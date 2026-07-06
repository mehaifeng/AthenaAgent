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

    public static ResolvedAudioConfig Resolve(AppConfig config)
    {
        // 统一继承树的音频例外：InheritMain 仅继承主 AI 的 ApiKey；BaseUrl 不继承主聊天端点
        // （主 BaseUrl 是 /v1 聊天端点而非 /audio/speech，直接继承会弄坏 TTS），
        // 始终按音频 provider 的默认端点解析，Custom 模式下才用用户填写的音频 BaseUrl。
        var inherit = config.ChatAudioCredentialSource == ModelCredentialSource.InheritMain;
        var provider = string.IsNullOrWhiteSpace(config.ChatAudioProvider)
            ? "OpenAI"
            : config.ChatAudioProvider;
        var baseUrl = !inherit && !string.IsNullOrWhiteSpace(config.ChatAudioBaseUrl)
            ? config.ChatAudioBaseUrl
            : GetDefaultBaseUrl(provider);
        var apiKey = inherit
            ? config.ApiKey
            : ModelCredentialResolver.FirstNonEmpty(config.ChatAudioApiKey, config.ApiKey);
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
