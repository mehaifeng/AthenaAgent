using System;
using System.Collections.Generic;
using System.Linq;
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
        // 系统 TTS 无凭据；远程语音只引用“供应商模型”中已配置的连接。
        var providerConfig = config.AiModels.Providers.FirstOrDefault(candidate => candidate.Id == config.ChatAudioProviderId);
        var useSystem = string.Equals(config.ChatAudioProvider, "System", StringComparison.OrdinalIgnoreCase);
        var provider = useSystem ? "System" : providerConfig?.DisplayName ?? string.Empty;
        var endpointPath = string.IsNullOrWhiteSpace(config.ChatAudioEndpointPath) ? "/audio/speech" : config.ChatAudioEndpointPath;
        var baseUrl = useSystem || providerConfig == null
            ? string.Empty
            : providerConfig.BaseUrl.TrimEnd('/') + "/" + endpointPath.TrimStart('/');
        var apiKey = useSystem ? string.Empty : providerConfig?.ApiKey ?? string.Empty;
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

    /// <summary>
    /// Converts the configured speech endpoint to the API root expected by OpenAIClientOptions.
    /// Existing Athena configurations store the full /audio/speech URL, while AudioClient appends that route itself.
    /// A value that is already an API root is returned unchanged.
    /// </summary>
    public static string GetSdkBaseUrl(string configuredUrl)
    {
        if (string.IsNullOrWhiteSpace(configuredUrl))
        {
            return string.Empty;
        }

        if (!Uri.TryCreate(configuredUrl.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new UriFormatException($"Invalid audio Base URL: {configuredUrl}");
        }

        var builder = new UriBuilder(uri);
        var path = builder.Path.TrimEnd('/');
        const string speechSuffix = "/audio/speech";
        if (path.EndsWith(speechSuffix, StringComparison.OrdinalIgnoreCase))
        {
            path = path[..^speechSuffix.Length].TrimEnd('/');
        }

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
    bool AutoPlay);
