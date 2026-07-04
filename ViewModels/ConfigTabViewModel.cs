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
    private readonly IModelCatalogService? _modelCatalogService;
    private readonly IKnowledgeBaseMaintenanceService? _maintenanceService;
    // Cancels in-flight system playback so Stop can kill the external process.
    private CancellationTokenSource? _audioTestCts;
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
    public bool CanTestBrowserAgent => !IsTestingBrowserAgent;
    public bool CanTestAudioOutput => !IsTestingAudioOutput;
    public bool IsRemoteAudioProvider => Config.ChatAudioProvider != "System";
    public bool CanEditRemoteAudioFields => Config.ChatAudioEnabled && IsRemoteAudioProvider;

    [ObservableProperty]
    private string _secondaryTestStatus = string.Empty;

    [ObservableProperty]
    private string _embeddingTestStatus = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunKnowledgeMaintenanceCommand))]
    private bool _isRunningMaintenance;

    [ObservableProperty]
    private string _maintenanceStatus = string.Empty;

    public bool CanRunMaintenance => !IsRunningMaintenance;

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

    /// <summary>浏览器智能体模型来源开关：true=跟随主模型，false=自定义。绑定到 ToggleSwitch。</summary>
    public bool UseMainModelForBrowser
    {
        get => Config.BrowserModelSource == BrowserModelSource.InheritMain;
        set
        {
            var newSource = value ? BrowserModelSource.InheritMain : BrowserModelSource.Custom;
            if (Config.BrowserModelSource != newSource)
            {
                Config.BrowserModelSource = newSource; // 触发 Config.PropertyChanged → 自动保存 + 下方通知
            }
        }
    }

    /// <summary>是否使用自定义浏览器模型（决定下方独立配置字段是否显示）。</summary>
    public bool IsBrowserModelCustom => Config.BrowserModelSource == BrowserModelSource.Custom;

    /// <summary>子代理模型来源开关：true=跟随主模型，false=自定义。绑定到 ToggleSwitch。</summary>
    public bool UseMainModelForSubAgent
    {
        get => Config.SubAgentModelSource == SubAgentModelSource.InheritMain;
        set
        {
            var newSource = value ? SubAgentModelSource.InheritMain : SubAgentModelSource.Custom;
            if (Config.SubAgentModelSource != newSource)
            {
                Config.SubAgentModelSource = newSource; // 触发 Config.PropertyChanged → 自动保存 + 下方通知
            }
        }
    }

    /// <summary>是否使用自定义子代理模型（决定下方独立配置字段是否显示）。</summary>
    public bool IsSubAgentModelCustom => Config.SubAgentModelSource == SubAgentModelSource.Custom;

    /// <summary>知识库整理 Agent 的模型来源候选。</summary>
    public ObservableCollection<KnowledgeMaintenanceModelSource> KnowledgeMaintenanceModelSources { get; } = new()
    {
        KnowledgeMaintenanceModelSource.InheritSecondary,
        KnowledgeMaintenanceModelSource.InheritMain,
        KnowledgeMaintenanceModelSource.Custom
    };

    /// <summary>是否使用整理专属自定义模型（决定下方独立配置字段是否显示）。</summary>
    public bool IsMaintenanceModelCustom => Config.KnowledgeMaintenanceModelSource == KnowledgeMaintenanceModelSource.Custom;

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

    public ObservableCollection<string> AudioProviders { get; } = new(AudioProviderUrls.Keys);

    public ObservableCollection<DocumentParserMode> DocumentParserModes { get; } = new()
    {
        DocumentParserMode.AgentLightweight,
        DocumentParserMode.Precision
    };

    // 仅精度解析方式需要 Token；极速解析无需登录。
    public bool CanEditParserToken => Config.DocumentParserEnabled && Config.DocumentParserMode == DocumentParserMode.Precision;

    [ObservableProperty]
    private int _selectedLanguageIndex;

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

    public ConfigTabViewModel() : this(null, null, null, null, null, null, null, null, null, null, null, null) { }

    public ConfigTabViewModel(IConfigService? configService, IChatService? chatService, IEmbeddingService? embeddingService, IConversationHistoryService? historyService, ILocalizationService? localizationService, IWebSearchService? webSearchService, IHeadlessBrowserService? browserService = null, IBrowserVisionService? browserVisionService = null, IAudioPlaybackService? audioPlaybackService = null, ISystemAudioService? systemAudioService = null, IModelCatalogService? modelCatalogService = null, IKnowledgeBaseMaintenanceService? maintenanceService = null)
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
        _modelCatalogService = modelCatalogService;
        _maintenanceService = maintenanceService;

        if (_maintenanceService != null)
        {
            _maintenanceService.StateChanged += OnMaintenanceStateChanged;
            UpdateMaintenanceStatus();
        }

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
            // Config 被整体替换后，刷新依赖其值的计算属性（外部工具改配置等场景）
            OnPropertyChanged(nameof(IsBrowserVisionEnabled));
            OnPropertyChanged(nameof(UseMainModelForBrowser));
            OnPropertyChanged(nameof(IsBrowserModelCustom));
            OnPropertyChanged(nameof(UseMainModelForSubAgent));
            OnPropertyChanged(nameof(IsSubAgentModelCustom));
            OnPropertyChanged(nameof(IsMaintenanceModelCustom));
            // 订阅 Config 属性变化，触发 UI 更新
            value.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(AppConfig.WebSearchProvider))
                {
                    OnPropertyChanged(nameof(IsBaiduProvider));
                }
                else if (e.PropertyName == nameof(AppConfig.ChatAudioProvider))
                {
                    OnPropertyChanged(nameof(IsRemoteAudioProvider));
                    OnPropertyChanged(nameof(CanEditRemoteAudioFields));
                }
                else if (e.PropertyName == nameof(AppConfig.ChatAudioEnabled))
                {
                    OnPropertyChanged(nameof(CanEditRemoteAudioFields));
                }
                else if (e.PropertyName == nameof(AppConfig.BrowserObservationMode))
                {
                    OnPropertyChanged(nameof(IsBrowserVisionEnabled));
                }
                else if (e.PropertyName == nameof(AppConfig.BrowserModelSource))
                {
                    OnPropertyChanged(nameof(UseMainModelForBrowser));
                    OnPropertyChanged(nameof(IsBrowserModelCustom));
                }
                else if (e.PropertyName == nameof(AppConfig.SubAgentModelSource))
                {
                    OnPropertyChanged(nameof(UseMainModelForSubAgent));
                    OnPropertyChanged(nameof(IsSubAgentModelCustom));
                }
                else if (e.PropertyName == nameof(AppConfig.KnowledgeMaintenanceModelSource))
                {
                    OnPropertyChanged(nameof(IsMaintenanceModelCustom));
                }
                else if (e.PropertyName == nameof(AppConfig.DocumentParserMode)
                         || e.PropertyName == nameof(AppConfig.DocumentParserEnabled))
                {
                    OnPropertyChanged(nameof(CanEditParserToken));
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
            else if (e.PropertyName == nameof(AppConfig.BrowserAgentProvider))
            {
                if (ProviderUrls.TryGetValue(config.BrowserAgentProvider, out var url))
                {
                    config.BrowserAgentBaseUrl = url;
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
        Config.BrowserAgentMaxTokens = Math.Clamp(Config.BrowserAgentMaxTokens, 100, 8000);
        Config.BrowserAgentTemperature = Math.Clamp(Config.BrowserAgentTemperature, 0, 2);
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
            Config.ChatAudioVoice = "alloy";
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
        }
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
    private string _browserAgentTestStatus = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestBrowserAgentCommand))]
    private bool _isTestingBrowserAgent;

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

    [RelayCommand(CanExecute = nameof(CanTestBrowserAgent))]
    private async Task TestBrowserAgentAsync()
    {
        if (_browserVisionService == null) { BrowserAgentTestStatus = _localizationService?.GetString("Status.ServiceNotInitialized") ?? "Service not initialized"; return; }
        if (!Config.BrowserEnabled) { BrowserAgentTestStatus = _localizationService?.GetString("Status.EnableBrowserFirst") ?? "Please enable Browser first"; return; }
        if (string.IsNullOrWhiteSpace(Config.BrowserAgentApiKey) && string.IsNullOrWhiteSpace(Config.ApiKey)) { BrowserAgentTestStatus = _localizationService?.GetString("Status.EnterApiKeyFirst") ?? "Please enter API Key first"; return; }

        IsTestingBrowserAgent = true;
        BrowserAgentTestStatus = _localizationService?.GetString("Status.TestingConnection") ?? "Testing...";
        try
        {
            NormalizeBrowserConfig();
            if (_configService != null)
            {
                await _configService.SaveAsync(Config);
            }

            var (success, message) = await _browserVisionService.TestConnectionAsync();
            BrowserAgentTestStatus = message;
        }
        finally { IsTestingBrowserAgent = false; }
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

    [RelayCommand(CanExecute = nameof(CanRunMaintenance))]
    private async Task RunKnowledgeMaintenanceAsync()
    {
        if (_maintenanceService == null)
        {
            MaintenanceStatus = GetString("Status.ServiceNotInitialized", "Service not initialized");
            return;
        }

        IsRunningMaintenance = true;
        MaintenanceStatus = GetString("Config.KbMaintenanceRunning", "正在整理知识库…");
        try
        {
            await _maintenanceService.RunNowAsync();
            UpdateMaintenanceStatus();
        }
        catch (Exception ex)
        {
            MaintenanceStatus = ex.Message;
        }
        finally { IsRunningMaintenance = false; }
    }

    private void OnMaintenanceStateChanged(object? sender, EventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            IsRunningMaintenance = _maintenanceService?.IsRunning ?? false;
            UpdateMaintenanceStatus();
        });
    }

    private void UpdateMaintenanceStatus()
    {
        var state = _maintenanceService?.State;
        if (state == null || state.LastRunUtc == null)
        {
            MaintenanceStatus = GetString("Config.KbMaintenanceNeverRun", "尚未整理");
            return;
        }

        // 只展示精简结论，不暴露整理 Agent 的原始摘要（原文仍在状态文件与日志里）。
        var time = state.LastRunUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        var conclusion = state.LastOutcome switch
        {
            "Succeeded" => string.Format(GetString("Config.KbMaintenanceDoneMerged", "已处理 {0} 组疑似重复"), state.LastMergedGroups),
            "NoDuplicates" => GetString("Config.KbMaintenanceNoDup", "未发现重复文件"),
            "Failed" => GetString("Config.KbMaintenanceFailed", "整理失败（详见日志）"),
            "Skipped" => GetString("Config.KbMaintenanceSkipped", "已跳过（Embedding 未配置）"),
            _ => state.LastOutcome ?? string.Empty
        };
        MaintenanceStatus = $"{time} · {conclusion}";
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
    private void ToggleAudioTestPlayback()
    {
        if (AudioTestAttachment == null)
        {
            return;
        }

        if (AudioTestAttachment.IsPlaying)
        {
            StopAudioTestPlayback();
            return;
        }

        StopAudioTestPlayback();

        if (AudioTestAttachment.UsesSystemAudioPlayback && _systemAudioService?.IsSupported == true)
        {
            // Fire-and-forget so the command returns immediately and the Stop
            // button's CanExecute stays true during playback.
            _ = RunSystemAudioTestPlaybackAsync(AudioTestAttachment);
            return;
        }

        if (_audioPlaybackService == null || !_audioPlaybackService.Play(AudioTestAttachment))
        {
            AudioOutputTestStatus = GetString("Chat.Audio.PlaybackUnavailable", "Audio playback is unavailable on this device.");
        }
    }

    private async Task RunSystemAudioTestPlaybackAsync(ChatAttachment attachment)
    {
        var cts = new CancellationTokenSource();
        _audioTestCts = cts;
        attachment.IsPlaying = true;
        try
        {
            var result = await _systemAudioService!.PlayFileAsync(attachment.StoredPath, cts.Token);
            if (!result.Success && !cts.IsCancellationRequested)
            {
                AudioOutputTestStatus = string.Format(
                    GetString("Chat.Audio.PlaybackFailedDetail", "Failed to play system audio: {0}"),
                    result.Message);
            }
        }
        catch (OperationCanceledException)
        {
            // User pressed Stop — expected.
        }
        finally
        {
            if (_audioTestCts == cts)
            {
                _audioTestCts.Dispose();
                _audioTestCts = null;
                attachment.IsPlaying = false;
            }
        }
    }

    private void StopAudioTestPlayback()
    {
        if (_audioTestCts != null)
        {
            _audioTestCts.Cancel();
            _audioTestCts.Dispose();
            _audioTestCts = null;
        }

        _audioPlaybackService?.Stop();
        if (AudioTestAttachment != null)
        {
            AudioTestAttachment.IsPlaying = false;
        }
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
        if (ChatTabViewModel == null) return;

        // 撤销上一次压缩：恢复被归档的消息与压缩前的摘要（非破坏性，可重新压缩）
        if (ChatTabViewModel.InternalUndoCompression())
        {
            _logger.Information("已撤销上一次上下文压缩");
            return;
        }

        await MessageBox.ShowAsync(
            message: _localizationService?.GetString("Dialog.NothingToUndoCompression") ?? "There is no recent compression to undo in the current conversation.",
            title: _localizationService?.GetString("Dialog.Title.Info") ?? "Info",
            button: MessageBoxButton.OK,
            icon: MessageBoxIcon.Information);
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

    #region 模型列表拉取（可编辑下拉框）

    /// <summary>主模型可选列表（通过 /v1/models 拉取，可手动输入覆盖）。</summary>
    public ObservableCollection<string> PrimaryModelOptions { get; } = new();
    public ObservableCollection<string> SecondaryModelOptions { get; } = new();
    public ObservableCollection<string> EmbeddingModelOptions { get; } = new();
    public ObservableCollection<string> BrowserAgentModelOptions { get; } = new();

    [ObservableProperty]
    private bool _isLoadingPrimaryModels;
    [ObservableProperty]
    private bool _isLoadingSecondaryModels;
    [ObservableProperty]
    private bool _isLoadingEmbeddingModels;
    [ObservableProperty]
    private bool _isLoadingBrowserAgentModels;

    [ObservableProperty]
    private string _primaryModelsStatus = string.Empty;
    [ObservableProperty]
    private string _secondaryModelsStatus = string.Empty;
    [ObservableProperty]
    private string _embeddingModelsStatus = string.Empty;
    [ObservableProperty]
    private string _browserAgentModelsStatus = string.Empty;

    [RelayCommand]
    private Task LoadModelsAsync(string? target) => LoadModelsCoreAsync(target);

    private async Task LoadModelsCoreAsync(string? target)
    {
        // 解析目标字段：各自的 BaseUrl/Key，次要/嵌入/视觉为空时回退到主配置。
        ObservableCollection<string> options;
        string? baseUrl;
        string? apiKey;
        Action<bool> setLoading;
        Action<string> setStatus;
        bool alreadyLoading;

        switch (target)
        {
            case "Primary":
                options = PrimaryModelOptions;
                baseUrl = Config.BaseUrl;
                apiKey = Config.ApiKey;
                alreadyLoading = IsLoadingPrimaryModels;
                setLoading = v => IsLoadingPrimaryModels = v;
                setStatus = v => PrimaryModelsStatus = v;
                break;
            case "Secondary":
                options = SecondaryModelOptions;
                baseUrl = FirstNonBlank(Config.SecondaryBaseUrl, Config.BaseUrl);
                apiKey = FirstNonBlank(Config.SecondaryApiKey, Config.ApiKey);
                alreadyLoading = IsLoadingSecondaryModels;
                setLoading = v => IsLoadingSecondaryModels = v;
                setStatus = v => SecondaryModelsStatus = v;
                break;
            case "Embedding":
                options = EmbeddingModelOptions;
                baseUrl = FirstNonBlank(Config.EmbeddingBaseUrl, Config.BaseUrl);
                apiKey = FirstNonBlank(Config.EmbeddingApiKey, Config.ApiKey);
                alreadyLoading = IsLoadingEmbeddingModels;
                setLoading = v => IsLoadingEmbeddingModels = v;
                setStatus = v => EmbeddingModelsStatus = v;
                break;
            case "BrowserAgent":
                options = BrowserAgentModelOptions;
                baseUrl = FirstNonBlank(Config.BrowserAgentBaseUrl, Config.BaseUrl);
                apiKey = FirstNonBlank(Config.BrowserAgentApiKey, Config.ApiKey);
                alreadyLoading = IsLoadingBrowserAgentModels;
                setLoading = v => IsLoadingBrowserAgentModels = v;
                setStatus = v => BrowserAgentModelsStatus = v;
                break;
            default:
                return;
        }

        if (alreadyLoading) return; // 防重入

        if (_modelCatalogService == null)
        {
            setStatus(GetString("Status.ServiceNotInitialized", "Service not initialized"));
            return;
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            setStatus(GetString("Status.EnterApiKeyFirst", "Please enter API Key first"));
            return;
        }

        setLoading(true);
        setStatus(GetString("Status.LoadingModels", "Loading models..."));
        try
        {
            var result = await _modelCatalogService.GetModelsAsync(baseUrl, apiKey);

            if (!result.Success)
            {
                setStatus(string.Format(
                    GetString("Status.LoadModelsFailed", "Failed to load models: {0}"),
                    result.ErrorMessage));
                return;
            }

            options.Clear();
            foreach (var model in result.Models)
            {
                options.Add(model);
            }

            if (result.Models.Count == 0)
            {
                setStatus(GetString("Status.NoModelsReturned", "Endpoint returned no models"));
            }
            else
            {
                setStatus(string.Format(
                    GetString("Status.ModelsLoaded", "Loaded {0} models"),
                    result.Models.Count));
            }
        }
        catch (OperationCanceledException)
        {
            // 页面切换等外部取消，无需提示。
        }
        catch (Exception ex)
        {
            setStatus(string.Format(
                GetString("Status.LoadModelsFailed", "Failed to load models: {0}"),
                ex.Message));
        }
        finally
        {
            setLoading(false);
        }
    }

    private static string? FirstNonBlank(string? primary, string? fallback)
        => string.IsNullOrWhiteSpace(primary) ? fallback : primary;

    #endregion
}
