using Athena.UI.Models;
using Athena.UI.Services;
using Athena.UI.Services.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ursa.Controls;

namespace Athena.UI.ViewModels;

public partial class ConfigTabViewModel : ViewModelBase
{
    private readonly IConfigService? _configService;
    private readonly IChatService? _chatService;
    private readonly IEmbeddingService? _embeddingService;
    private readonly IConversationHistoryService? _historyService;
    private readonly ILocalizationService? _localizationService;
    private readonly IWebSearchService? _webSearchService;
    private readonly IHeadlessBrowserService? _browserService;
    private readonly IBrowserVisionService? _browserVisionService;
    private readonly IAudioPlaybackService? _audioPlaybackService;
    private readonly ISystemAudioService? _systemAudioService;
    private readonly ILogger _logger = Log.ForContext<ConfigTabViewModel>();

    [ObservableProperty]
    private AppConfig _config = new();

    [ObservableProperty]
    private string _connectionStatus = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestConnectionCommand))]
    private bool _isTestingConnection;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestSecondaryCommand))]
    private bool _isTestingSecondary;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestEmbeddingCommand))]
    private bool _isTestingEmbedding;

    [ObservableProperty]
    private bool _isCompressing;

    [ObservableProperty]
    private ChatTabViewModel? _chatTabViewModel;

    [ObservableProperty]
    private ITokenService? _tokenService;

    [ObservableProperty]
    private int _contextTokensThreshold = 64000;

    public bool CanTestConnection => !IsTestingConnection;
    public bool CanTestSecondary => !IsTestingSecondary;
    public bool CanTestEmbedding => !IsTestingEmbedding;
    public bool CanTestWebSearch => !IsTestingWebSearch;
    public bool CanTestBrowserRuntime => !IsTestingBrowserRuntime && !IsInstallingBrowserRuntime;
    public bool CanInstallBrowserRuntime => !IsInstallingBrowserRuntime && !IsTestingBrowserRuntime;
    public bool CanTestBrowserVision => !IsTestingBrowserVision;
    public bool CanTestAudioOutput => !IsTestingAudioOutput;
    public bool IsRemoteAudioProvider => Config.ChatAudioProvider != "System";
    public bool IsSystemAudioProvider => Config.ChatAudioProvider == "System";
    public bool CanEditRemoteAudioFields => Config.ChatAudioEnabled && IsRemoteAudioProvider;
    public bool HasAudioTestAttachment => AudioTestAttachment != null;
    public bool HasSystemAudioVoices => SystemAudioVoices.Count > 0;

    [ObservableProperty]
    private string _secondaryTestStatus = string.Empty;

    [ObservableProperty]
    private string _embeddingTestStatus = string.Empty;

    [ObservableProperty]
    private string _audioOutputTestStatus = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestAudioOutputCommand))]
    private bool _isTestingAudioOutput;

    [ObservableProperty]
    private ChatAttachment? _audioTestAttachment;

    private CancellationTokenSource? _autoSaveCts;
    private bool _isInternalSaving;

    private static readonly Dictionary<string, string> ProviderUrls = new()
    {
        { "OpenAI", "https://api.openai.com/v1" },
        { "Anthropic", "https://api.anthropic.com/v1/" },
        { "Google", "https://generativelanguage.googleapis.com/v1beta/openai/" },
        { "Zhipu", "https://open.bigmodel.cn/api/paas/v4" },
        { "Mimimaxi", "https://api.minimaxi.com/v1" },
        { "Alibaba", "https://dashscope.aliyuncs.com/compatible-mode/v1" },
        { "Deepseek", "https://api.deepseek.com/v1" },
        { "OpenRouter", "https://openrouter.ai/api/v1" },
        { "Custom", "" }
    };

    public ObservableCollection<string> Providers { get; } = new(ProviderUrls.Keys);
    public ObservableCollection<string> Themes { get; } = new() { "Dark", "Light" };
    public ObservableCollection<BrowserObservationMode> BrowserObservationModes { get; } = new()
    {
        BrowserObservationMode.VisionWithSom,
        BrowserObservationMode.DomOnly
    };

    public bool IsBrowserVisionEnabled => Config.BrowserObservationMode != BrowserObservationMode.DomOnly;

    public ObservableCollection<string> Languages { get; }

    public ObservableCollection<string> WebSearchProviders { get; } = new() { "Tavily", "Zhipu", "Baidu" };

    public bool IsBaiduProvider => Config.WebSearchProvider == "Baidu";

    private static readonly Dictionary<string, string> WebSearchUrls = new()
    {
        { "Tavily", "https://api.tavily.com" },
        { "Zhipu", "https://open.bigmodel.cn/api/paas/v4" },
        { "Baidu", "https://qianfan.baidubce.com/v2/ai_search/web_search" }
    };

    private static readonly Dictionary<string, string> AudioProviderUrls = new()
    {
        { "OpenAI", "https://api.openai.com/v1/audio/speech" },
        { "OpenRouter", "https://openrouter.ai/api/v1/audio/speech" },
        { "System", string.Empty },
        { "Custom", string.Empty }
    };

    private static readonly HashSet<string> RemoteAudioVoices = new(StringComparer.OrdinalIgnoreCase)
    {
        "alloy",
        "ash",
        "ballad",
        "coral",
        "echo",
        "fable",
        "nova",
        "onyx",
        "sage",
        "shimmer",
        "verse"
    };

    public ObservableCollection<string> AudioProviders { get; } = new(AudioProviderUrls.Keys);
    public ObservableCollection<string> SystemAudioVoices { get; } = new();

    [ObservableProperty]
    private int _selectedLanguageIndex;

    [ObservableProperty]
    private bool _isLoadingSystemAudioVoices;

    partial void OnSelectedLanguageIndexChanged(int value)
    {
        if (_localizationService == null || value < 0 || value >= _localizationService.AvailableLanguages.Count)
            return;

        var selectedLanguage = _localizationService.AvailableLanguages[value];
        if (Config.Language != selectedLanguage)
        {
            Config.Language = selectedLanguage;
            _localizationService.SwitchLanguage(selectedLanguage);
            _logger.Information("语言已切换为: {Language}", selectedLanguage);
        }
    }

    public ConfigTabViewModel() : this(null, null, null, null, null, null, null, null, null, null) { }

    public ConfigTabViewModel(IConfigService? configService, IChatService? chatService, IEmbeddingService? embeddingService, IConversationHistoryService? historyService, ILocalizationService? localizationService, IWebSearchService? webSearchService, IHeadlessBrowserService? browserService = null, IBrowserVisionService? browserVisionService = null, IAudioPlaybackService? audioPlaybackService = null, ISystemAudioService? systemAudioService = null)
    {
        _configService = configService;
        _chatService = chatService;
        _embeddingService = embeddingService;
        _historyService = historyService;
        _localizationService = localizationService;
        _webSearchService = webSearchService;
        _browserService = browserService;
        _browserVisionService = browserVisionService;
        _audioPlaybackService = audioPlaybackService;
        _systemAudioService = systemAudioService;

        if (_localizationService != null)
        {
            Languages = new ObservableCollection<string>(_localizationService.AvailableLanguageNames);
        }
        else
        {
            Languages = new ObservableCollection<string> { "English", "中文" };
        }

        SetupConfigListener(Config);
        LoadConfigAsync().ConfigureAwait(false);

        // 订阅配置变更事件（LLM 工具调用修改配置后及时刷新 UI）
        if (_configService != null)
        {
            _configService.ConfigChanged += OnExternalConfigChanged;
        }

        if (_audioPlaybackService != null)
        {
            _audioPlaybackService.PlaybackStateChanged += OnAudioPlaybackStateChanged;
        }
    }

    public void Initialize(ChatTabViewModel chatTabViewModel, ITokenService? tokenService)
    {
        ChatTabViewModel = chatTabViewModel;
        TokenService = tokenService;
        if (TokenService != null)
        {
            TokenService.MaxTokens = Config.MaxContextTokens;
        }
    }

    partial void OnConfigChanged(AppConfig value)
    {
        if (value != null)
        {
            SetupConfigListener(value);
            // 订阅 Config 属性变化，触发 UI 更新
            value.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(AppConfig.WebSearchProvider))
                {
                    OnPropertyChanged(nameof(IsBaiduProvider));
                }
                else if (e.PropertyName == nameof(AppConfig.ChatAudioProvider))
                {
                    OnPropertyChanged(nameof(IsSystemAudioProvider));
                    OnPropertyChanged(nameof(IsRemoteAudioProvider));
                    OnPropertyChanged(nameof(CanEditRemoteAudioFields));
                    _ = RefreshSystemAudioVoicesAsync();
                }
                else if (e.PropertyName == nameof(AppConfig.ChatAudioEnabled))
                {
                    OnPropertyChanged(nameof(CanEditRemoteAudioFields));
                }
                else if (e.PropertyName == nameof(AppConfig.BrowserObservationMode))
                {
                    OnPropertyChanged(nameof(IsBrowserVisionEnabled));
                }
            };
        }
    }

    /// <summary>
    /// 外部（如 LLM 工具调用）修改配置后，在 UI 线程上刷新 ConfigTab 显示
    /// </summary>
    private void OnExternalConfigChanged(object? sender, AppConfig newConfig)
    {
        if (_isInternalSaving) return; // Ignore events triggered by our own auto-save

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            NormalizeExternalAudioConfig(newConfig);
            Config = newConfig;
            if (TokenService != null) TokenService.MaxTokens = newConfig.MaxContextTokens;
            _logger.Information("ConfigTab: 检测到外部配置变更，已刷新 UI");
            _ = RefreshSystemAudioVoicesAsync();
        });
    }

    private void NormalizeExternalAudioConfig(AppConfig config)
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

    private void SetupConfigListener(AppConfig? config)
    {
        if (config == null) return;
        config.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(AppConfig.MaxContextTokens))
            {
                if (TokenService != null) TokenService.MaxTokens = config.MaxContextTokens;
            }
            else if (e.PropertyName == nameof(AppConfig.Provider))
            {
                if (ProviderUrls.TryGetValue(config.Provider, out var url))
                {
                    config.BaseUrl = url;
                }
            }
            else if (e.PropertyName == nameof(AppConfig.ChatAudioProvider))
            {
                if (AudioProviderUrls.TryGetValue(config.ChatAudioProvider, out var url))
                {
                    config.ChatAudioBaseUrl = url;
                }

                NormalizeChatAudioVoiceForProvider(config);
            }
            else if (e.PropertyName == nameof(AppConfig.SecondaryProvider))
            {
                if (ProviderUrls.TryGetValue(config.SecondaryProvider, out var url))
                {
                    config.SecondaryBaseUrl = url;
                }
            }
            else if (e.PropertyName == nameof(AppConfig.EmbeddingProvider))
            {
                if (ProviderUrls.TryGetValue(config.EmbeddingProvider, out var url))
                {
                    config.EmbeddingBaseUrl = url;
                }
            }
            else if (e.PropertyName == nameof(AppConfig.BrowserVisionProvider))
            {
                if (ProviderUrls.TryGetValue(config.BrowserVisionProvider, out var url))
                {
                    config.BrowserVisionBaseUrl = url;
                }
            }
            else if (e.PropertyName == nameof(AppConfig.BrowserObservationMode))
            {
                OnPropertyChanged(nameof(IsBrowserVisionEnabled));
            }
            
            // Trigger auto-save on any property change
            RequestAutoSave();
        };
    }

    private void RequestAutoSave()
    {
        _autoSaveCts?.Cancel();
        _autoSaveCts = new CancellationTokenSource();
        var token = _autoSaveCts.Token;

        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500, token); // 500ms debounce
                if (!token.IsCancellationRequested)
                {
                    await ExecuteAutoSaveAsync();
                }
            }
            catch (TaskCanceledException) { /* Ignored */ }
        });
    }

    private async Task ExecuteAutoSaveAsync()
    {
        _isInternalSaving = true;
        try
        {
            if (_configService != null)
            {
                if (Config.CompressionThreshold > Config.MaxContextTokens)
                {
                    Config.CompressionThreshold = Config.MaxContextTokens;
                    ContextTokensThreshold = Config.CompressionThreshold;
                    _logger.Information("压缩阈值已自动调整为最大上下文限制: {Value}", Config.MaxContextTokens);
                }

                NormalizeBrowserConfig();
                NormalizeAudioConfig();
                await _configService.SaveAsync(Config);

                // Dispatch UI-related updates back to the UI thread
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    App.SetTheme(Config.Theme);
                    _chatService?.UpdateConfig(Config);
                    if (_embeddingService is OpenAIEmbeddingService openAIEmbedding) openAIEmbedding.UpdateConfig(Config);
                    if (_historyService is ConversationHistoryService historyService) historyService.UpdateSecondaryConfig(Config);
                    
                    if (ChatTabViewModel != null)
                    {
                        await ChatTabViewModel.RefreshSettingsAsync();
                    }
                    
                    if (TokenService != null) TokenService.MaxTokens = Config.MaxContextTokens;
                });
                
                _logger.Information("配置已自动静默保存");
            }
        }
        finally
        {
            _isInternalSaving = false;
        }
    }

    private void NormalizeBrowserConfig()
    {
        Config.BrowserViewportWidth = Math.Clamp(Config.BrowserViewportWidth, 320, 3840);
        Config.BrowserViewportHeight = Math.Clamp(Config.BrowserViewportHeight, 240, 2160);
        Config.BrowserMaxSteps = Math.Clamp(Config.BrowserMaxSteps, 1, 50);
        Config.BrowserOperationTimeoutSeconds = Math.Clamp(Config.BrowserOperationTimeoutSeconds, 5, 300);
        Config.BrowserSessionTtlMinutes = Math.Clamp(Config.BrowserSessionTtlMinutes, 1, 120);
        Config.BrowserScreenshotScale = Math.Clamp(Config.BrowserScreenshotScale, 0.25, 2.0);
        Config.BrowserImageQuality = Math.Clamp(Config.BrowserImageQuality, 30, 100);
        Config.BrowserSomMaxElements = Math.Clamp(Config.BrowserSomMaxElements, 10, 200);
        Config.BrowserVisionMaxTokens = Math.Clamp(Config.BrowserVisionMaxTokens, 100, 8000);
        Config.BrowserVisionTemperature = Math.Clamp(Config.BrowserVisionTemperature, 0, 2);
    }

    private void NormalizeAudioConfig()
    {
        if (string.IsNullOrWhiteSpace(Config.ChatAudioProvider))
        {
            Config.ChatAudioProvider = "OpenAI";
        }

        if (string.IsNullOrWhiteSpace(Config.ChatAudioBaseUrl))
        {
            Config.ChatAudioBaseUrl = AudioConfigResolver.GetDefaultBaseUrl(Config.ChatAudioProvider);
        }

        if (string.IsNullOrWhiteSpace(Config.ChatAudioModel))
        {
            Config.ChatAudioModel = "gpt-4o-mini-tts";
        }

        if (string.IsNullOrWhiteSpace(Config.ChatAudioVoice))
        {
            Config.ChatAudioVoice = Config.ChatAudioProvider == "System"
                ? string.Empty
                : "alloy";
        }

        NormalizeChatAudioVoiceForProvider(Config);
    }

    private static void NormalizeChatAudioVoiceForProvider(AppConfig config)
    {
        if (config.ChatAudioProvider == "System")
        {
            if (string.Equals(config.ChatAudioVoice, "alloy", StringComparison.OrdinalIgnoreCase))
            {
                config.ChatAudioVoice = string.Empty;
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(config.ChatAudioVoice) || !RemoteAudioVoices.Contains(config.ChatAudioVoice))
        {
            config.ChatAudioVoice = "alloy";
        }
    }

    private async Task LoadConfigAsync()
    {
        if (_configService != null)
        {
            Config = await _configService.LoadAsync();
            NormalizeAudioConfig();
            ContextTokensThreshold = Config.CompressionThreshold;
            if (TokenService != null) TokenService.MaxTokens = Config.MaxContextTokens;

            if (_localizationService != null)
            {
                var index = _localizationService.GetLanguageIndex(Config.Language);
                if (index >= 0)
                {
                    SelectedLanguageIndex = index;
                    _localizationService.SwitchLanguage(Config.Language);
                }
            }

            await RefreshSystemAudioVoicesAsync();
        }
    }

    partial void OnAudioTestAttachmentChanged(ChatAttachment? value)
    {
        OnPropertyChanged(nameof(HasAudioTestAttachment));
    }

    private async Task RefreshSystemAudioVoicesAsync()
    {
        if (_systemAudioService == null || !_systemAudioService.IsSupported || !IsSystemAudioProvider)
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                SystemAudioVoices.Clear();
                OnPropertyChanged(nameof(HasSystemAudioVoices));
            });
            return;
        }

        IsLoadingSystemAudioVoices = true;
        try
        {
            var voices = await _systemAudioService.GetAvailableVoicesAsync();
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                SystemAudioVoices.Clear();
                foreach (var voice in voices)
                {
                    SystemAudioVoices.Add(voice);
                }

                if (SystemAudioVoices.Count > 0)
                {
                    if (!SystemAudioVoices.Contains(Config.ChatAudioVoice))
                    {
                        Config.ChatAudioVoice = PickPreferredSystemVoice(SystemAudioVoices) ?? SystemAudioVoices[0];
                    }
                }
                else if (Config.ChatAudioProvider == "System" && Config.ChatAudioVoice == "alloy")
                {
                    Config.ChatAudioVoice = string.Empty;
                }

                OnPropertyChanged(nameof(HasSystemAudioVoices));
            });
        }
        finally
        {
            IsLoadingSystemAudioVoices = false;
        }
    }

    private static string? PickPreferredSystemVoice(IEnumerable<string> voices)
    {
        var voiceList = voices.ToList();
        if (OperatingSystem.IsWindows())
        {
            return voiceList.FirstOrDefault(v => string.Equals(v, "Microsoft Zira Desktop", StringComparison.OrdinalIgnoreCase))
                ?? voiceList.FirstOrDefault(v => v.Contains("Zira", StringComparison.OrdinalIgnoreCase))
                ?? voiceList.FirstOrDefault(v => v.Contains("Microsoft", StringComparison.OrdinalIgnoreCase));
        }

        return voiceList.FirstOrDefault();
    }

    [RelayCommand(CanExecute = nameof(CanTestConnection))]
    private async Task TestConnectionAsync()
    {
        if (_chatService == null) { ConnectionStatus = _localizationService?.GetString("Status.ServiceNotInitialized") ?? "Service not initialized"; return; }
        if (string.IsNullOrWhiteSpace(Config.ApiKey)) { ConnectionStatus = _localizationService?.GetString("Status.EnterApiKeyFirst") ?? "Please enter API Key first"; return; }
        IsTestingConnection = true;
        ConnectionStatus = _localizationService?.GetString("Status.TestingConnection") ?? "Testing...";
        try
        {
            _chatService.UpdateConfig(Config);
            var (success, message) = await _chatService.TestConnectionAsync();
            ConnectionStatus = message?.TrimEnd().Replace("\n", _localizationService?.GetString("Config.TestConnectNoRespond"))?? string.Empty;
        }
        finally { IsTestingConnection = false; }
    }

    [ObservableProperty]
    private string _webSearchTestStatus = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestWebSearchCommand))]
    private bool _isTestingWebSearch;

    [ObservableProperty]
    private string _browserRuntimeStatus = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestBrowserRuntimeCommand))]
    [NotifyCanExecuteChangedFor(nameof(InstallBrowserRuntimeCommand))]
    private bool _isTestingBrowserRuntime;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallBrowserRuntimeCommand))]
    [NotifyCanExecuteChangedFor(nameof(TestBrowserRuntimeCommand))]
    private bool _isInstallingBrowserRuntime;

    [ObservableProperty]
    private string _browserVisionTestStatus = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestBrowserVisionCommand))]
    private bool _isTestingBrowserVision;

    [RelayCommand(CanExecute = nameof(CanTestWebSearch))]
    private async Task TestWebSearchAsync()
    {
        if (_webSearchService == null) { WebSearchTestStatus = _localizationService?.GetString("Status.ServiceNotInitialized") ?? "Service not initialized"; return; }
        if (!Config.WebSearchEnabled) { WebSearchTestStatus = _localizationService?.GetString("Status.EnableWebSearchFirst") ?? "Please enable Web Search first"; return; }
        if (string.IsNullOrWhiteSpace(Config.WebSearchApiKey)) { WebSearchTestStatus = _localizationService?.GetString("Status.EnterApiKeyFirst") ?? "Please enter API Key first"; return; }

        IsTestingWebSearch = true;
        WebSearchTestStatus = _localizationService?.GetString("Status.TestingConnection") ?? "Testing...";
        try
        {
            // 刷新配置
            if (_webSearchService is WebSearchService ws) ws.RefreshConfig();
            var (success, message) = await _webSearchService.TestConnectionAsync();
            WebSearchTestStatus = message;
        }
        finally { IsTestingWebSearch = false; }
    }

    [RelayCommand(CanExecute = nameof(CanTestBrowserRuntime))]
    private async Task TestBrowserRuntimeAsync()
    {
        if (_browserService == null) { BrowserRuntimeStatus = _localizationService?.GetString("Status.ServiceNotInitialized") ?? "Service not initialized"; return; }
        if (!Config.BrowserEnabled) { BrowserRuntimeStatus = _localizationService?.GetString("Status.EnableBrowserFirst") ?? "Please enable Browser first"; return; }

        IsTestingBrowserRuntime = true;
        BrowserRuntimeStatus = _localizationService?.GetString("Status.TestingConnection") ?? "Testing...";
        try
        {
            var status = await _browserService.GetRuntimeStatusAsync();
            BrowserRuntimeStatus = status.Details == null ? status.Message : $"{status.Message}\n{status.Details}";
        }
        finally { IsTestingBrowserRuntime = false; }
    }

    [RelayCommand(CanExecute = nameof(CanInstallBrowserRuntime))]
    private async Task InstallBrowserRuntimeAsync()
    {
        if (_browserService == null) { BrowserRuntimeStatus = _localizationService?.GetString("Status.ServiceNotInitialized") ?? "Service not initialized"; return; }
        if (!Config.BrowserEnabled) { BrowserRuntimeStatus = _localizationService?.GetString("Status.EnableBrowserFirst") ?? "Please enable Browser first"; return; }

        IsInstallingBrowserRuntime = true;
        BrowserRuntimeStatus = _localizationService?.GetString("Status.InstallingBrowserRuntime") ?? "Installing browser runtime...";
        try
        {
            var result = await _browserService.InstallRuntimeAsync();
            BrowserRuntimeStatus = result.Message;
        }
        finally { IsInstallingBrowserRuntime = false; }
    }

    [RelayCommand(CanExecute = nameof(CanTestBrowserVision))]
    private async Task TestBrowserVisionAsync()
    {
        if (_browserVisionService == null) { BrowserVisionTestStatus = _localizationService?.GetString("Status.ServiceNotInitialized") ?? "Service not initialized"; return; }
        if (!Config.BrowserEnabled) { BrowserVisionTestStatus = _localizationService?.GetString("Status.EnableBrowserFirst") ?? "Please enable Browser first"; return; }
        if (string.IsNullOrWhiteSpace(Config.BrowserVisionApiKey) && string.IsNullOrWhiteSpace(Config.ApiKey)) { BrowserVisionTestStatus = _localizationService?.GetString("Status.EnterApiKeyFirst") ?? "Please enter API Key first"; return; }

        IsTestingBrowserVision = true;
        BrowserVisionTestStatus = _localizationService?.GetString("Status.TestingConnection") ?? "Testing...";
        try
        {
            NormalizeBrowserConfig();
            if (_configService != null)
            {
                await _configService.SaveAsync(Config);
            }

            var (success, message) = await _browserVisionService.TestConnectionAsync();
            BrowserVisionTestStatus = message;
        }
        finally { IsTestingBrowserVision = false; }
    }

    [RelayCommand(CanExecute = nameof(CanTestSecondary))]
    private async Task TestSecondaryAsync()
    {
        if (_historyService == null) { SecondaryTestStatus = _localizationService?.GetString("Status.ServiceNotInitialized") ?? "Service not initialized"; return; }

        IsTestingSecondary = true;
        SecondaryTestStatus = _localizationService?.GetString("Status.TestingConnection") ?? "Testing...";
        try
        {
            _historyService.UpdateSecondaryConfig(Config);
            var (success, message) = await _historyService.TestSecondaryConnectionAsync();
            SecondaryTestStatus = message;
        }
        finally { IsTestingSecondary = false; }
    }

    [RelayCommand(CanExecute = nameof(CanTestEmbedding))]
    private async Task TestEmbeddingAsync()
    {
        if (_embeddingService == null) { EmbeddingTestStatus = _localizationService?.GetString("Status.ServiceNotInitialized") ?? "Service not initialized"; return; }

        IsTestingEmbedding = true;
        EmbeddingTestStatus = _localizationService?.GetString("Status.TestingConnection") ?? "Testing...";
        try
        {
            if (_embeddingService is OpenAIEmbeddingService oes)
            {
                oes.UpdateConfig(Config);
            }
            var (success, message) = await _embeddingService.TestConnectionAsync();
            EmbeddingTestStatus = message;
        }
        finally { IsTestingEmbedding = false; }
    }

    [RelayCommand(CanExecute = nameof(CanTestAudioOutput))]
    private async Task TestAudioOutputAsync()
    {
        if (_chatService == null)
        {
            AudioOutputTestStatus = GetString("Status.ServiceNotInitialized", "Service not initialized");
            return;
        }

        if (!Config.ChatAudioEnabled)
        {
            AudioOutputTestStatus = GetString("Status.EnableAudioOutputFirst", "Please enable chat audio output first");
            return;
        }

        IsTestingAudioOutput = true;
        AudioOutputTestStatus = GetString("Status.TestingConnection", "Testing...");
        AudioTestAttachment = null;
        try
        {
            var result = await _chatService.TestAudioOutputAsync();
            AudioOutputTestStatus = result.Message;
            if (result.Attachment != null)
            {
                AudioTestAttachment = result.Attachment;
            }
        }
        finally
        {
            IsTestingAudioOutput = false;
        }
    }

    [RelayCommand]
    private async Task PlayAudioTestAttachment()
    {
        if (AudioTestAttachment == null)
        {
            return;
        }

        if (AudioTestAttachment.UsesSystemAudioPlayback && _systemAudioService?.IsSupported == true)
        {
            AudioTestAttachment.IsPlaying = true;
            var result = await _systemAudioService.PlayFileAsync(AudioTestAttachment.StoredPath);
            AudioTestAttachment.IsPlaying = false;
            if (!result.Success)
            {
                AudioOutputTestStatus = string.Format(
                    GetString("Chat.Audio.PlaybackFailedDetail", "Failed to play system audio: {0}"),
                    result.Message);
            }
            return;
        }

        if (_audioPlaybackService == null || !_audioPlaybackService.Play(AudioTestAttachment))
        {
            AudioOutputTestStatus = GetString("Chat.Audio.PlaybackUnavailable", "Audio playback is unavailable on this device.");
        }
    }

    [RelayCommand]
    private void PauseAudioTestAttachment()
    {
        if (AudioTestAttachment == null || _audioPlaybackService == null)
        {
            return;
        }

        _audioPlaybackService.Pause();
    }

    /// <summary>
    /// 更新 Web Search Base URL（供应商切换时调用）
    /// </summary>
    public void UpdateWebSearchBaseUrl(string? provider)
    {
        if (string.IsNullOrEmpty(provider)) return;
        if (WebSearchUrls.TryGetValue(provider, out var url))
        {
            Config.WebSearchBaseUrl = url;
        }
    }

    [RelayCommand]
    private async Task CompressContextAsync()
    {
        if (IsCompressing || ChatTabViewModel == null) return;
        IsCompressing = true;
        try
        {
            await ChatTabViewModel.InternalCompressContextAsync();
            await Task.Delay(500); 
        }
        finally
        {
            IsCompressing = false;
        }
    }

    [RelayCommand]
    private async Task UndoCompressionAsync()
    {
        var result = await MessageBox.ShowAsync(
            message: _localizationService?.GetString("Dialog.ConfirmClearSummary") ?? "Clear the compression summary? This will remove the archived context digest. Compressed messages will remain archived.",
            title: _localizationService?.GetString("Dialog.Title.Warning") ?? "Warning",
            button: MessageBoxButton.OKCancel,
            icon: MessageBoxIcon.Warning);

        if (result == MessageBoxResult.OK)
        {
            if (TokenService != null) TokenService.CompressionPreview = string.Empty;
            if (ChatTabViewModel != null) ChatTabViewModel.InternalUndoCompression();
            _logger.Information("压缩摘要已清空");
        }
    }

    private void OnAudioPlaybackStateChanged(object? sender, AudioPlaybackStateChangedEventArgs e)
    {
        if (AudioTestAttachment == null || AudioTestAttachment.Id != e.AttachmentId)
        {
            return;
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            AudioTestAttachment.IsPlaying = e.IsPlaying;
            if (e.Duration > TimeSpan.Zero)
            {
                AudioTestAttachment.Duration = e.Duration;
            }

            AudioTestAttachment.Position = e.Position;
        });
    }

    private string GetString(string key, string defaultValue)
    {
        return _localizationService?.GetString(key, defaultValue) ?? defaultValue;
    }
}
