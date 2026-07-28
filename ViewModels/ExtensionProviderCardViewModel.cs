using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;

namespace Athena.UI.ViewModels;

public enum ExtensionProviderKind
{
    Audio,
    Image,
    WebSearch
}

public partial class ExtensionProviderCardViewModel : ObservableObject
{
    private readonly Action<ExtensionProviderCardViewModel> _onSelected;
    private readonly ILocalizationService? _localizationService;

    public ExtensionProviderCardViewModel(
        ExtensionProviderKind kind,
        ExtensionProviderOption option,
        ExtensionProviderSettings settings,
        bool isSelected,
        Action<ExtensionProviderCardViewModel> onSelected,
        ILocalizationService? localizationService = null)
    {
        Kind = kind;
        Option = option;
        Settings = settings;
        _isSelected = isSelected;
        _onSelected = onSelected;
        _localizationService = localizationService;
    }

    public ExtensionProviderKind Kind { get; }
    public ExtensionProviderOption Option { get; }
    public ExtensionProviderSettings Settings { get; }
    public string Id => Option.Id;
    public string DisplayName => Option.DisplayName;
    public IReadOnlyList<string> AspectRatioOptions { get; } = ["1:1", "16:9", "9:16", "4:3", "3:4"];
    public IReadOnlyList<string> SearchModeOptions { get; } = ["fast", "one-shot", "agentic"];

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isExpanded;

    partial void OnIsSelectedChanged(bool value)
    {
        if (value) _onSelected(this);
    }

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    public bool UsesApiKey => Kind switch
    {
        ExtensionProviderKind.Audio => Id is not ("Edge" or "KittenTTS" or "Piper"),
        ExtensionProviderKind.WebSearch => Id is not ("DuckDuckGo" or "SearXNG" or "FirecrawlSelfHosted"),
        _ => true
    };

    public bool UsesBaseUrl => Kind switch
    {
        ExtensionProviderKind.Audio => Id is not ("Edge" or "KittenTTS" or "Piper"),
        ExtensionProviderKind.WebSearch => Id is "FirecrawlSelfHosted" or "SearXNG" or "Tavily" or "xAI"
            or "WebSearchAPI" or "Zhipu" or "Baidu",
        _ => true
    };

    public bool UsesModel => Kind switch
    {
        ExtensionProviderKind.Audio => Id != "Edge",
        ExtensionProviderKind.WebSearch => Id == "xAI",
        _ => true
    };

    public bool UsesVoice => Kind == ExtensionProviderKind.Audio;
    public bool UsesLanguage => Kind == ExtensionProviderKind.Audio && Id is "Edge" or "xAI";
    public bool UsesSpeed => Kind == ExtensionProviderKind.Audio && Id is "Edge" or "xAI" or "KittenTTS";
    public bool UsesLocalRuntime => Kind == ExtensionProviderKind.Audio && Id is "Edge" or "KittenTTS" or "Piper";
    public bool UsesLocalModelPath => Kind == ExtensionProviderKind.Audio && Id == "Piper";
    public bool UsesAspectRatio => Kind == ExtensionProviderKind.Image;
    public bool UsesMode => Kind == ExtensionProviderKind.WebSearch && Id == "Parallel";
    public bool UsesAppId => Kind == ExtensionProviderKind.WebSearch && Id == "Baidu";

    public string ApiKeyLabel => Id == "OpenAICodex"
        ? GetString("Connectors.Provider.CodexToken", "ChatGPT / Codex Access Token")
        : GetString("Connectors.Provider.ApiKey", "API Key");

    public string VoiceLabel => Id == "ElevenLabs"
        ? GetString("Connectors.Provider.VoiceId", "Voice ID")
        : GetString("Connectors.Provider.Voice", "Voice");

    public string BaseUrlLabel => Id switch
    {
        "SearXNG" => GetString("Connectors.Provider.SearXngUrl", "SearXNG instance URL"),
        "FirecrawlSelfHosted" => GetString("Connectors.Provider.SelfHostedUrl", "Self-hosted API URL"),
        _ => GetString("Connectors.Provider.BaseUrl", "Base URL")
    };

    public string Description => GetString(
        $"Connectors.Provider.Description.{Kind}.{Id}",
        string.Empty);

    private string GetString(string key, string fallback) =>
        _localizationService?.GetString(key, fallback) ?? fallback;
}
