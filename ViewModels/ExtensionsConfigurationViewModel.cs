using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System;

namespace Athena.UI.ViewModels;

public partial class ExtensionsConfigurationViewModel : ViewModelBase
{
    private readonly IConfigService _configService;

    public ExtensionsConfigurationViewModel(IConfigService configService)
    {
        _configService = configService;
        Config = configService.Load();
        Config.PropertyChanged += OnConfigChanged;
        if (AudioProviders.All(provider => provider.Id != Config.ChatAudioProvider))
        {
            Config.ChatAudioProvider = "Edge";
            Config.ChatAudioBaseUrl = string.Empty;
            Config.ChatAudioApiKey = string.Empty;
            Config.ChatAudioModel = string.Empty;
            Config.ChatAudioVoice = "en-US-AriaNeural";
        }
        EnsureProviderSettings();
        _selectedAudioProvider = AudioProviders.FirstOrDefault(provider => provider.Id == Config.ChatAudioProvider)
            ?? AudioProviders.First();
        _selectedImageProvider = ImageProviders.FirstOrDefault(provider => provider.Id == Config.ImageGenerationProvider)
            ?? ImageProviders.First();
        _selectedWebSearchProvider = WebSearchProviders.FirstOrDefault(provider => provider.Id == Config.WebSearchProvider)
            ?? WebSearchProviders.First();
        _selectedAudioSettings = Config.AudioProviderSettings.First(item => item.ProviderId == _selectedAudioProvider.Id);
        _selectedImageSettings = Config.ImageProviderSettings.First(item => item.ProviderId == _selectedImageProvider.Id);
        _selectedWebSearchSettings = Config.WebSearchProviderSettings.First(item => item.ProviderId == _selectedWebSearchProvider.Id);
        AudioProviderCards = AudioProviders
            .Select(option => new ExtensionProviderCardViewModel(
                ExtensionProviderKind.Audio,
                option,
                Config.AudioProviderSettings.First(item => item.ProviderId == option.Id),
                option.Id == _selectedAudioProvider.Id,
                card => SelectedAudioProvider = card.Option))
            .ToList();
        ImageProviderCards = ImageProviders
            .Select(option => new ExtensionProviderCardViewModel(
                ExtensionProviderKind.Image,
                option,
                Config.ImageProviderSettings.First(item => item.ProviderId == option.Id),
                option.Id == _selectedImageProvider.Id,
                card => SelectedImageProvider = card.Option))
            .ToList();
        WebSearchProviderCards = WebSearchProviders
            .Select(option => new ExtensionProviderCardViewModel(
                ExtensionProviderKind.WebSearch,
                option,
                Config.WebSearchProviderSettings.First(item => item.ProviderId == option.Id),
                option.Id == _selectedWebSearchProvider.Id,
                card => SelectedWebSearchProvider = card.Option))
            .ToList();
    }

    public AppConfig Config { get; }
    public IReadOnlyList<ExtensionProviderOption> AudioProviders => ExtensionProviderCatalog.AudioProviders;
    public IReadOnlyList<ExtensionProviderOption> ImageProviders => ExtensionProviderCatalog.ImageProviders;
    public IReadOnlyList<ExtensionProviderOption> WebSearchProviders => ExtensionProviderCatalog.WebSearchProviders;
    public IReadOnlyList<string> ImageAspectRatios { get; } = ["1:1", "16:9", "9:16", "4:3", "3:4"];
    public IReadOnlyList<string> SearchModes { get; } = ["fast", "one-shot", "agentic"];
    public IReadOnlyList<ExtensionProviderCardViewModel> AudioProviderCards { get; }
    public IReadOnlyList<ExtensionProviderCardViewModel> ImageProviderCards { get; }
    public IReadOnlyList<ExtensionProviderCardViewModel> WebSearchProviderCards { get; }

    [ObservableProperty] private ExtensionProviderOption? _selectedAudioProvider;
    [ObservableProperty] private ExtensionProviderOption? _selectedImageProvider;
    [ObservableProperty] private ExtensionProviderOption? _selectedWebSearchProvider;
    [ObservableProperty] private ExtensionProviderSettings? _selectedAudioSettings;
    [ObservableProperty] private ExtensionProviderSettings? _selectedImageSettings;
    [ObservableProperty] private ExtensionProviderSettings? _selectedWebSearchSettings;

    public bool AudioUsesApiKey => SelectedAudioProvider?.Id is not ("Edge" or "KittenTTS" or "Piper");
    public bool AudioUsesLocalRuntime => SelectedAudioProvider?.Id is "Edge" or "KittenTTS" or "Piper";
    public bool AudioUsesModel => SelectedAudioProvider?.Id is not "Edge";
    public bool AudioUsesLanguage => SelectedAudioProvider?.Id is "Edge" or "xAI";
    public bool AudioUsesSpeed => SelectedAudioProvider?.Id is "Edge" or "xAI";
    public bool AudioUsesVoiceId => SelectedAudioProvider?.Id is "ElevenLabs";
    public bool AudioUsesLocalModelPath => SelectedAudioProvider?.Id is "Piper";
    public bool WebUsesApiKey => SelectedWebSearchProvider?.Id is not ("DuckDuckGo" or "SearXNG" or "FirecrawlSelfHosted");
    public bool WebUsesBaseUrl => SelectedWebSearchProvider?.Id is "FirecrawlSelfHosted" or "SearXNG" or "Tavily" or "xAI"
        or "WebSearchAPI" or "Zhipu" or "Baidu";
    public bool WebUsesModel => SelectedWebSearchProvider?.Id is "xAI";
    public bool WebUsesMode => SelectedWebSearchProvider?.Id is "Parallel";
    public bool WebUsesAppId => SelectedWebSearchProvider?.Id is "Baidu";
    public bool ImageUsesCodexToken => SelectedImageProvider?.Id is "OpenAICodex";

    partial void OnSelectedAudioProviderChanged(ExtensionProviderOption? value)
    {
        if (value == null) return;
        Config.ChatAudioProvider = value.Id;
        SelectedAudioSettings = Config.AudioProviderSettings.First(item => item.ProviderId == value.Id);
        OnPropertyChanged(nameof(AudioUsesApiKey));
        OnPropertyChanged(nameof(AudioUsesLocalRuntime));
        OnPropertyChanged(nameof(AudioUsesModel));
        OnPropertyChanged(nameof(AudioUsesLanguage));
        OnPropertyChanged(nameof(AudioUsesSpeed));
        OnPropertyChanged(nameof(AudioUsesVoiceId));
        OnPropertyChanged(nameof(AudioUsesLocalModelPath));
        _ = _configService.SaveAsync(Config);
    }

    partial void OnSelectedImageProviderChanged(ExtensionProviderOption? value)
    {
        if (value == null) return;
        Config.ImageGenerationProvider = value.Id;
        SelectedImageSettings = Config.ImageProviderSettings.First(item => item.ProviderId == value.Id);
        OnPropertyChanged(nameof(ImageUsesCodexToken));
        _ = _configService.SaveAsync(Config);
    }

    partial void OnSelectedWebSearchProviderChanged(ExtensionProviderOption? value)
    {
        if (value == null) return;
        Config.WebSearchProvider = value.Id;
        SelectedWebSearchSettings = Config.WebSearchProviderSettings.First(item => item.ProviderId == value.Id);
        OnPropertyChanged(nameof(WebUsesApiKey));
        OnPropertyChanged(nameof(WebUsesBaseUrl));
        OnPropertyChanged(nameof(WebUsesModel));
        OnPropertyChanged(nameof(WebUsesMode));
        OnPropertyChanged(nameof(WebUsesAppId));
        _ = _configService.SaveAsync(Config);
    }

    private void OnConfigChanged(object? sender, PropertyChangedEventArgs e) => _ = _configService.SaveAsync(Config);

    private void EnsureProviderSettings()
    {
        EnsureSettings(
            Config.AudioProviderSettings,
            AudioProviders,
            Config.ChatAudioProvider,
            selected =>
            {
                if (Config.ChatAudioProvider == "OpenAI")
                {
                    selected.BaseUrl = Config.ChatAudioBaseUrl;
                    selected.ApiKey = Config.ChatAudioApiKey;
                    selected.Model = Config.ChatAudioModel;
                    selected.Voice = Config.ChatAudioVoice;
                }
                selected.Language = string.IsNullOrWhiteSpace(Config.ChatAudioLanguage) ? "en-US" : Config.ChatAudioLanguage;
                selected.Speed = Config.ChatAudioSpeed <= 0 ? 1.0 : Config.ChatAudioSpeed;
                selected.LocalExecutable = Config.ChatAudioLocalExecutable;
                selected.LocalModelPath = Config.ChatAudioLocalModelPath;
            });
        EnsureSettings(
            Config.ImageProviderSettings,
            ImageProviders,
            Config.ImageGenerationProvider,
            selected =>
            {
                selected.BaseUrl = Config.ImageGenerationBaseUrl;
                selected.ApiKey = Config.ImageGenerationApiKey;
                selected.Model = Config.ImageGenerationModel;
                selected.AspectRatio = Config.ImageGenerationAspectRatio;
            });
        EnsureSettings(
            Config.WebSearchProviderSettings,
            WebSearchProviders,
            Config.WebSearchProvider,
            selected =>
            {
                selected.BaseUrl = Config.WebSearchBaseUrl;
                selected.ApiKey = Config.WebSearchApiKey;
                selected.Model = Config.WebSearchModel;
                selected.AppId = Config.WebSearchAppId;
                selected.Mode = Config.WebSearchMode;
            });
    }

    private void EnsureSettings(
        System.Collections.ObjectModel.ObservableCollection<ExtensionProviderSettings> settings,
        IReadOnlyList<ExtensionProviderOption> providers,
        string selectedProviderId,
        System.Action<ExtensionProviderSettings> migrateSelected)
    {
        var wasEmpty = settings.Count == 0;
        foreach (var provider in providers)
        {
            if (settings.Any(item => item.ProviderId == provider.Id)) continue;
            settings.Add(new ExtensionProviderSettings
            {
                ProviderId = provider.Id,
                BaseUrl = provider.DefaultBaseUrl,
                Model = provider.DefaultModel,
                Voice = provider.DefaultVoice
            });
        }
        if (wasEmpty)
        {
            var selected = settings.FirstOrDefault(item => item.ProviderId == selectedProviderId);
            if (selected != null) migrateSelected(selected);
        }
        foreach (var setting in settings)
        {
            setting.PropertyChanged -= OnConfigChanged;
            setting.PropertyChanged += OnConfigChanged;
        }
    }
}

public enum ExtensionProviderKind
{
    Audio,
    Image,
    WebSearch
}

public partial class ExtensionProviderCardViewModel : ObservableObject
{
    private readonly Action<ExtensionProviderCardViewModel> _onSelected;

    public ExtensionProviderCardViewModel(
        ExtensionProviderKind kind,
        ExtensionProviderOption option,
        ExtensionProviderSettings settings,
        bool isSelected,
        Action<ExtensionProviderCardViewModel> onSelected)
    {
        Kind = kind;
        Option = option;
        Settings = settings;
        _isSelected = isSelected;
        _onSelected = onSelected;
    }

    public ExtensionProviderKind Kind { get; }
    public ExtensionProviderOption Option { get; }
    public ExtensionProviderSettings Settings { get; }
    public string Id => Option.Id;
    public string DisplayName => Option.DisplayName;

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isExpanded;

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
    public string ApiKeyLabel => Id == "OpenAICodex" ? "ChatGPT / Codex Access Token" : "API Key";
    public string VoiceLabel => Id == "ElevenLabs" ? "Voice ID" : "Voice";
    public string BaseUrlLabel => Id switch
    {
        "SearXNG" => "SearXNG 实例地址",
        "FirecrawlSelfHosted" => "自托管 API 地址",
        _ => "Base URL"
    };

    public string Description => (Kind, Id) switch
    {
        (ExtensionProviderKind.Audio, "Edge") => "免费方案。通过本机安装的 edge-tts 调用 Microsoft Edge 在线语音；配置 Voice、语言和语速，不需要 API Key。",
        (ExtensionProviderKind.Audio, "OpenAI") => "使用 OpenAI Speech API。模型示例：gpt-4o-mini-tts；Voice 示例：alloy、coral、nova。",
        (ExtensionProviderKind.Audio, "xAI") => "使用 xAI 专用 /v1/tts 接口；需要 Voice、语言和 0.7–1.5 范围内的语速。",
        (ExtensionProviderKind.Audio, "ElevenLabs") => "使用 ElevenLabs Text-to-Speech；Voice 必须填写 Voice ID，而不是显示名称。",
        (ExtensionProviderKind.Audio, "Mistral") => "使用 Mistral Voxtral TTS；配置 Voxtral 模型和 Voice ID。",
        (ExtensionProviderKind.Audio, "Gemini") => "使用 Gemini generateContent 音频模态；配置 Gemini TTS 模型和预置 Voice，返回的 PCM 会自动封装为 WAV。",
        (ExtensionProviderKind.Audio, "KittenTTS") => "本地 CPU 语音。需要 Python、kitten-tts 与 soundfile；模型首次使用时可能需要下载。",
        (ExtensionProviderKind.Audio, "Piper") => "完全本地的 Piper 语音。需要 piper 可执行文件以及对应的 .onnx 模型文件。",
        (ExtensionProviderKind.Audio, "DeepInfra") => "使用 DeepInfra 的 OpenAI 兼容 Speech 接口；模型 ID 与 Voice 按 DeepInfra TTS 模型填写。",
        (ExtensionProviderKind.Image, "DeepInfra") => "使用 DeepInfra OpenAI 兼容图像端点；模型目录会变化，请填写当前 image-gen 模型 ID。",
        (ExtensionProviderKind.Image, "Fal") => "FAL_KEY 鉴权，模型 ID 直接组成 fal.run 路由；例如 fal-ai/flux-2/klein/9b。",
        (ExtensionProviderKind.Image, "Krea") => "Krea 使用异步任务：提交后轮询 jobs 接口。可填写 krea-2-medium、krea-2-large 或 krea-2-medium-turbo。",
        (ExtensionProviderKind.Image, "OpenAI") => "使用 OpenAI Images API；支持文本生成和参考图连续性。",
        (ExtensionProviderKind.Image, "OpenAICodex") => "使用 ChatGPT/Codex Access Token 调用 Codex Responses 图像工具；这里不是 OpenAI API Key。",
        (ExtensionProviderKind.Image, "OpenRouter") => "使用 OpenRouter chat/completions 的 image + text 模态；模型必须支持图像输出。",
        (ExtensionProviderKind.Image, "xAI") => "使用 xAI Images API；模型可选 grok-imagine-image 或 grok-imagine-image-quality。",
        (ExtensionProviderKind.WebSearch, "Brave") => "使用 Brave Search API，仅需要订阅 API Key。",
        (ExtensionProviderKind.WebSearch, "DuckDuckGo") => "无需 API Key；通过 DuckDuckGo 搜索页面获取结果。",
        (ExtensionProviderKind.WebSearch, "Exa") => "使用 Exa 神经搜索，并请求结果摘要；需要 EXA API Key。",
        (ExtensionProviderKind.WebSearch, "Firecrawl") => "使用 Firecrawl Cloud 搜索接口；需要 Firecrawl API Key。",
        (ExtensionProviderKind.WebSearch, "FirecrawlSelfHosted") => "连接自托管 Firecrawl，只需填写实例 API 地址，不显示云服务的 API Key 配置。",
        (ExtensionProviderKind.WebSearch, "Parallel") => "使用 Parallel Search；可选择 fast、one-shot 或 agentic 搜索模式。",
        (ExtensionProviderKind.WebSearch, "SearXNG") => "连接自托管 SearXNG 实例，无需 API Key；实例必须开放 JSON format 搜索。",
        (ExtensionProviderKind.WebSearch, "Tavily") => "使用 Tavily Search API；可覆盖服务地址。",
        (ExtensionProviderKind.WebSearch, "xAI") => "通过 xAI Responses API 的 web_search 工具搜索；需要选择 Grok 模型。",
        (ExtensionProviderKind.WebSearch, "WebSearchAPI") => "使用 WebSearchAPI 的 AI Search 接口，需要 API Key。",
        (ExtensionProviderKind.WebSearch, "Zhipu") => "使用智谱 Web Search 接口，需要 API Key，可覆盖服务地址。",
        (ExtensionProviderKind.WebSearch, "Baidu") => "使用百度智能 Web Search；需要 API Key 和 App ID。",
        _ => string.Empty
    };
}
