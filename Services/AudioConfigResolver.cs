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
        var provider = string.IsNullOrWhiteSpace(config.ChatAudioProvider)
            ? "OpenAI"
            : config.ChatAudioProvider;
        var baseUrl = string.IsNullOrWhiteSpace(config.ChatAudioBaseUrl)
            ? GetDefaultBaseUrl(provider)
            : config.ChatAudioBaseUrl;
        var apiKey = string.IsNullOrWhiteSpace(config.ChatAudioApiKey)
            ? config.ApiKey
            : config.ChatAudioApiKey;
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
