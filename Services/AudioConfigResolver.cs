using Athena.UI.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Athena.UI.Services;

public static class AudioConfigResolver
{
    private static readonly Dictionary<string, string> AudioProviderUrls = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Edge"] = "",
        ["OpenAI"] = "https://api.openai.com/v1/audio/speech",
        ["xAI"] = "https://api.x.ai/v1/tts",
        ["ElevenLabs"] = "https://api.elevenlabs.io/v1",
        ["Mistral"] = "https://api.mistral.ai/v1/audio/speech",
        ["Gemini"] = "https://generativelanguage.googleapis.com/v1beta",
        ["KittenTTS"] = "",
        ["Piper"] = "",
        ["DeepInfra"] = "https://api.deepinfra.com/v1/openai/audio/speech"
    };

    public static IReadOnlyList<string> ProviderNames { get; } =
        ["Edge", "OpenAI", "xAI", "ElevenLabs", "Mistral", "Gemini", "KittenTTS", "Piper", "DeepInfra"];

    public static ResolvedAudioConfig Resolve(AppConfig config)
    {
        var provider = string.IsNullOrWhiteSpace(config.ChatAudioProvider) ? "OpenAI" : config.ChatAudioProvider;
        var settings = config.AudioProviderSettings.FirstOrDefault(
            item => item.ProviderId.Equals(provider, StringComparison.OrdinalIgnoreCase));
        var legacyProvider = settings == null && !string.IsNullOrWhiteSpace(config.ChatAudioProviderId)
            ? config.AiModels.Providers.FirstOrDefault(item => item.Id == config.ChatAudioProviderId)
            : null;
        var legacyBaseUrl = legacyProvider == null
            ? config.ChatAudioBaseUrl
            : legacyProvider.BaseUrl.TrimEnd('/') + "/audio/speech";
        var defaultOption = ExtensionProviderCatalog.AudioProviders.FirstOrDefault(
            item => item.Id.Equals(provider, StringComparison.OrdinalIgnoreCase));
        var model = settings?.Model;
        if (string.IsNullOrWhiteSpace(model)) model = config.ChatAudioModel;
        if (string.IsNullOrWhiteSpace(model)) model = defaultOption?.DefaultModel ?? string.Empty;
        var voice = settings?.Voice;
        if (string.IsNullOrWhiteSpace(voice)) voice = config.ChatAudioVoice;
        if (string.IsNullOrWhiteSpace(voice)
            || (provider == "Edge" && voice.Equals("alloy", StringComparison.OrdinalIgnoreCase)))
            voice = defaultOption?.DefaultVoice ?? string.Empty;
        return new ResolvedAudioConfig(
            legacyProvider?.DisplayName ?? provider,
            settings?.BaseUrl ?? legacyBaseUrl,
            settings?.ApiKey ?? legacyProvider?.ApiKey ?? config.ChatAudioApiKey,
            model,
            voice,
            config.ChatAudioAutoPlay,
            settings?.Language ?? config.ChatAudioLanguage,
            Math.Clamp(settings?.Speed ?? config.ChatAudioSpeed, 0.5, 2.0),
            settings?.LocalExecutable ?? config.ChatAudioLocalExecutable,
            settings?.LocalModelPath ?? config.ChatAudioLocalModelPath);
    }

    public static bool TryGetDefaultBaseUrl(string? provider, out string url)
        => AudioProviderUrls.TryGetValue(provider ?? string.Empty, out url!);

    public static string GetDefaultBaseUrl(string? provider)
        => AudioProviderUrls.TryGetValue(provider ?? "OpenAI", out var url)
            ? url
            : AudioProviderUrls["OpenAI"];

    public static string GetSdkBaseUrl(string configuredUrl)
    {
        if (string.IsNullOrWhiteSpace(configuredUrl))
            return string.Empty;
        if (!Uri.TryCreate(configuredUrl.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new UriFormatException($"Invalid audio Base URL: {configuredUrl}");

        var builder = new UriBuilder(uri);
        var path = builder.Path.TrimEnd('/');
        const string suffix = "/audio/speech";
        if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            path = path[..^suffix.Length].TrimEnd('/');
        builder.Path = string.IsNullOrEmpty(path) ? "/" : path;
        return builder.Uri.AbsoluteUri.TrimEnd('/');
    }
}

public sealed record ResolvedAudioConfig(
    string Provider,
    string BaseUrl,
    string ApiKey,
    string Model,
    string Voice,
    bool AutoPlay,
    string Language,
    double Speed,
    string LocalExecutable,
    string LocalModelPath);
