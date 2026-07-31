using Athena.UI.Models;
using System;
using System.Linq;

namespace Athena.UI.Services;

public static class AudioConfigResolver
{
    public static ResolvedAudioConfig Resolve(AppConfig config)
    {
        var provider = string.IsNullOrWhiteSpace(config.ChatAudioProvider) ? "OpenAI" : config.ChatAudioProvider;
        var settings = config.AudioProviderSettings.FirstOrDefault(
            item => item.ProviderId.Equals(provider, StringComparison.OrdinalIgnoreCase));
        var defaultOption = ExtensionProviderCatalog.AudioProviders.FirstOrDefault(
            item => item.Id.Equals(provider, StringComparison.OrdinalIgnoreCase));
        var model = string.IsNullOrWhiteSpace(settings?.Model)
            ? defaultOption?.DefaultModel ?? string.Empty
            : settings.Model;
        var voice = string.IsNullOrWhiteSpace(settings?.Voice)
            ? defaultOption?.DefaultVoice ?? string.Empty
            : settings.Voice;
        return new ResolvedAudioConfig(
            provider,
            settings?.BaseUrl ?? defaultOption?.DefaultBaseUrl ?? string.Empty,
            settings?.ApiKey ?? string.Empty,
            model,
            voice,
            config.ChatAudioAutoPlay,
            settings?.Language ?? "en-US",
            Math.Clamp(settings?.Speed ?? 1.0, 0.5, 2.0),
            settings?.LocalExecutable ?? string.Empty,
            settings?.LocalModelPath ?? string.Empty);
    }

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
