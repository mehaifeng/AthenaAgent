using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Athena.UI.Models;

public sealed record ExtensionProviderOption(
    string Id,
    string DisplayName,
    string DefaultBaseUrl,
    string DefaultModel = "",
    string DefaultVoice = "",
    IReadOnlyDictionary<string, string>? DeprecatedModels = null);

public partial class ExtensionProviderSettings : ObservableObject
{
    [ObservableProperty] private string _providerId = string.Empty;
    [ObservableProperty] private string _baseUrl = string.Empty;
    [ObservableProperty] private string _apiKey = string.Empty;
    [ObservableProperty] private string _model = string.Empty;
    [ObservableProperty] private string _voice = string.Empty;
    [ObservableProperty] private string _language = "en-US";
    [ObservableProperty] private double _speed = 1.0;
    [ObservableProperty] private string _localExecutable = string.Empty;
    [ObservableProperty] private string _localModelPath = string.Empty;
    [ObservableProperty] private string _aspectRatio = "1:1";
    [ObservableProperty] private string _appId = string.Empty;
    [ObservableProperty] private string _mode = "fast";
}

/// <summary>
/// Providers exposed by the desktop extension settings. These are deliberately
/// separate from chat-model providers because their credentials and protocols
/// are not interchangeable.
/// </summary>
public static class ExtensionProviderCatalog
{
    private static IReadOnlyDictionary<string, string> Replacements(params (string Old, string New)[] entries)
    {
        var result = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var (oldModel, newModel) in entries) result[oldModel] = newModel;
        return result;
    }

    public static IReadOnlyList<ExtensionProviderOption> WebSearchProviders { get; } =
    [
        new("Brave", "Brave Search", "https://api.search.brave.com/res/v1/web/search"),
        new("DuckDuckGo", "DuckDuckGo", "https://html.duckduckgo.com/html/"),
        new("Exa", "Exa", "https://api.exa.ai"),
        new("Firecrawl", "Firecrawl Cloud", "https://api.firecrawl.dev/v1"),
        new("FirecrawlSelfHosted", "Firecrawl Self-Hosted", ""),
        new("Parallel", "Parallel", "https://api.parallel.ai"),
        new("SearXNG", "SearXNG", ""),
        new("Tavily", "Tavily", "https://api.tavily.com"),
        new(
            "xAI",
            "xAI Web Search",
            "https://api.x.ai/v1",
            "grok-4.5",
            DeprecatedModels: Replacements(
                ("grok-4-fast", "grok-4.5"),
                ("grok-4-fast-reasoning", "grok-4.5"),
                ("grok-4-fast-non-reasoning", "grok-4.5"))),
        // Keep Athena's pre-existing adapters selectable for existing users.
        new("WebSearchAPI", "WebSearchAPI", "https://api.websearchapi.ai"),
        new("Zhipu", "智谱 Web Search", "https://open.bigmodel.cn/api/paas/v4"),
        new("Baidu", "百度智能 Web Search", "https://qianfan.baidubce.com/v2/ai_search/web_search")
    ];

    public static IReadOnlyList<ExtensionProviderOption> ImageProviders { get; } =
    [
        new("DeepInfra", "DeepInfra", "https://api.deepinfra.com/v1/openai", "black-forest-labs/FLUX-1.1-pro"),
        new("Fal", "FAL.ai", "https://fal.run", "fal-ai/flux-pro/v1.1"),
        new("Krea", "Krea", "https://api.krea.ai", "krea-2"),
        new(
            "OpenAI",
            "OpenAI",
            "https://api.openai.com/v1",
            "gpt-image-2",
            DeprecatedModels: Replacements(("gpt-image-1", "gpt-image-2"))),
        new(
            "OpenAICodex",
            "OpenAI Codex Auth",
            "https://chatgpt.com/backend-api",
            "gpt-5.6",
            DeprecatedModels: Replacements(("gpt-5", "gpt-5.6"))),
        new(
            "OpenRouter",
            "OpenRouter",
            "https://openrouter.ai/api/v1",
            "google/gemini-3.1-flash-image",
            DeprecatedModels: Replacements(
                ("google/gemini-2.5-flash-image-preview", "google/gemini-3.1-flash-image"))),
        new(
            "xAI",
            "xAI",
            "https://api.x.ai/v1",
            "grok-imagine-image-quality",
            DeprecatedModels: Replacements(("grok-2-image-1212", "grok-imagine-image-quality")))
    ];

    public static IReadOnlyList<ExtensionProviderOption> AudioProviders { get; } =
    [
        new("Edge", "Microsoft Edge TTS", "", "", "en-US-AriaNeural"),
        new(
            "OpenAI",
            "OpenAI",
            "https://api.openai.com/v1/audio/speech",
            "tts-1",
            "alloy",
            Replacements(("gpt-4o-mini-tts", "tts-1"))),
        new("xAI", "xAI", "https://api.x.ai/v1/tts", "", "eve"),
        new("ElevenLabs", "ElevenLabs", "https://api.elevenlabs.io/v1", "eleven_multilingual_v2"),
        new("Mistral", "Mistral Voxtral", "https://api.mistral.ai/v1/audio/speech", "voxtral-mini-tts-2603"),
        new(
            "Gemini",
            "Google Gemini TTS",
            "https://generativelanguage.googleapis.com/v1beta",
            "gemini-3.1-flash-tts-preview",
            "Kore",
            Replacements(("gemini-2.5-flash-preview-tts", "gemini-3.1-flash-tts-preview"))),
        new("KittenTTS", "KittenTTS（本地）", "", "KittenML/kitten-tts-nano-0.8-int8", "Jasper"),
        new("Piper", "Piper（本地）", "", "en_US-lessac-medium"),
        new("DeepInfra", "DeepInfra", "https://api.deepinfra.com/v1/openai/audio/speech", "hexgrad/Kokoro-82M", "af_bella")
    ];
}
