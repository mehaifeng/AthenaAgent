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
    private readonly IModelCatalogService? _modelCatalogService;
    private readonly IKnowledgeBaseMaintenanceService? _maintenanceService;
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

    /// <summary>API 供应商名称目录（供扩展页 ViewModel 共用同一份候选）。</summary>
    internal static IReadOnlyCollection<string> ProviderNames => ProviderUrls.Keys;

    /// <summary>音频供应商名称目录（供扩展页 ViewModel 共用同一份候选）。</summary>
    internal static IReadOnlyCollection<string> AudioProviderNames => AudioProviderUrls.Keys;

    public ObservableCollection<string> Providers { get; } = new(ProviderUrls.Keys);
    public ObservableCollection<string> Themes { get; } = new() { "Dark", "Light" };

    /// <summary>工具审批模式候选。</summary>
    public ObservableCollection<ToolApprovalMode> ToolApprovalModes { get; } = new()
    {
        ToolApprovalMode.Off,
        ToolApprovalMode.Balanced,
        ToolApprovalMode.Strict
    };

    /// <summary>撤销某个「永久放行」的工具。集合变更不触发 AppConfig.PropertyChanged，故显式保存。</summary>
    [RelayCommand]
    private async Task RevokeAutoAllowedToolAsync(string? tool)
    {
        if (!string.IsNullOrEmpty(tool) && Config.AutoAllowedTools.Remove(tool) && _configService != null)
        {
            await _configService.SaveAsync(Config);
        }
    }

    /// <summary>撤销某个终端命令白名单项。</summary>
    [RelayCommand]
    private async Task RevokeTerminalAllowlistAsync(string? command)
    {
        if (!string.IsNullOrEmpty(command) && Config.TerminalAllowlist.Remove(command) && _configService != null)
        {
            await _configService.SaveAsync(Config);
        }
    }

    /// <summary>次级模型凭据来源开关：true=跟随主模型凭据，false=自定义。绑定到 ToggleSwitch。</summary>
    public bool UseMainCredentialForSecondary
    {
        get => Config.SecondaryCredentialSource == ModelCredentialSource.InheritMain;
        set => SetCredentialSource(v => Config.SecondaryCredentialSource = v, Config.SecondaryCredentialSource, value);
    }

    public bool IsSecondaryCredentialCustom => Config.SecondaryCredentialSource == ModelCredentialSource.Custom;

    /// <summary>Embedding 凭据来源开关。</summary>
    public bool UseMainCredentialForEmbedding
    {
        get => Config.EmbeddingCredentialSource == ModelCredentialSource.InheritMain;
        set => SetCredentialSource(v => Config.EmbeddingCredentialSource = v, Config.EmbeddingCredentialSource, value);
    }

    public bool IsEmbeddingCredentialCustom => Config.EmbeddingCredentialSource == ModelCredentialSource.Custom;

    private static void SetCredentialSource(Action<ModelCredentialSource> setter, ModelCredentialSource current, bool inherit)
    {
        var newSource = inherit ? ModelCredentialSource.InheritMain : ModelCredentialSource.Custom;
        if (current != newSource)
        {
            setter(newSource); // 触发 Config.PropertyChanged → 自动保存 + 下方通知
        }
    }

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

    private static readonly Dictionary<string, string> AudioProviderUrls = new()
    {
        { "OpenAI", "https://api.openai.com/v1/audio/speech" },
        { "OpenRouter", "https://openrouter.ai/api/v1/audio/speech" },
        { "System", string.Empty },
        { "Custom", string.Empty }
    };

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

    public ConfigTabViewModel() : this(null, null, null, null, null) { }

    public ConfigTabViewModel(IConfigService? configService, IChatService? chatService, IEmbeddingService? embeddingService, IConversationHistoryService? historyService, ILocalizationService? localizationService, IModelCatalogService? modelCatalogService = null, IKnowledgeBaseMaintenanceService? maintenanceService = null)
    {
        _configService = configService;
        _chatService = chatService;
        _embeddingService = embeddingService;
        _historyService = historyService;
        _localizationService = localizationService;
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
            OnPropertyChanged(nameof(IsMaintenanceModelCustom));
            OnPropertyChanged(nameof(UseMainCredentialForSecondary));
            OnPropertyChanged(nameof(IsSecondaryCredentialCustom));
            OnPropertyChanged(nameof(UseMainCredentialForEmbedding));
            OnPropertyChanged(nameof(IsEmbeddingCredentialCustom));
            // 订阅 Config 属性变化，触发 UI 更新
            value.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(AppConfig.KnowledgeMaintenanceModelSource))
                {
                    OnPropertyChanged(nameof(IsMaintenanceModelCustom));
                }
                else if (e.PropertyName == nameof(AppConfig.SecondaryCredentialSource))
                {
                    OnPropertyChanged(nameof(UseMainCredentialForSecondary));
                    OnPropertyChanged(nameof(IsSecondaryCredentialCustom));
                }
                else if (e.PropertyName == nameof(AppConfig.EmbeddingCredentialSource))
                {
                    OnPropertyChanged(nameof(UseMainCredentialForEmbedding));
                    OnPropertyChanged(nameof(IsEmbeddingCredentialCustom));
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

            // Trigger auto-save on any property change
            RequestAutoSave();
        };

        // MCP 服务器是嵌套对象/集合，其内部编辑不会冒泡为 AppConfig.PropertyChanged，
        // 必须深度订阅，否则用户填的命令/参数/环境变量（如 API Key）不会落盘。
        HookMcpDeep(config);
    }

    /// <summary>深度订阅 McpServers 集合及其每个条目的变更 → 触发自动保存。</summary>
    private void HookMcpDeep(AppConfig config)
    {
        foreach (var server in config.McpServers)
            HookMcpServer(server);

        config.McpServers.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null)
                foreach (McpServerConfig s in e.NewItems) HookMcpServer(s);
            RequestAutoSave();
        };
    }

    private void HookMcpServer(McpServerConfig server)
    {
        server.PropertyChanged += (_, e) =>
        {
            // 运行期状态字段由连接过程回填，不应触发保存（否则连接→状态变→保存→重连 死循环）。
            if (e.PropertyName is nameof(McpServerConfig.Status)
                or nameof(McpServerConfig.StatusDetail)
                or nameof(McpServerConfig.DiscoveredToolCount))
                return;
            RequestAutoSave();
        };

        server.Arguments.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null)
                foreach (McpArgEntry a in e.NewItems) a.PropertyChanged += (_, _) => RequestAutoSave();
            RequestAutoSave();
        };
        foreach (var a in server.Arguments) a.PropertyChanged += (_, _) => RequestAutoSave();

        server.Environment.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null)
                foreach (McpEnvEntry en in e.NewItems) en.PropertyChanged += (_, _) => RequestAutoSave();
            RequestAutoSave();
        };
        foreach (var en in server.Environment) en.PropertyChanged += (_, _) => RequestAutoSave();

        server.Headers.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null)
                foreach (McpEnvEntry h in e.NewItems) h.PropertyChanged += (_, _) => RequestAutoSave();
            RequestAutoSave();
        };
        foreach (var h in server.Headers) h.PropertyChanged += (_, _) => RequestAutoSave();
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

                NormalizeBrowserConfig(Config);
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

    /// <summary>钳制浏览器相关配置到合法区间（自动保存与扩展页的浏览器诊断共用）。</summary>
    internal static void NormalizeBrowserConfig(AppConfig config)
    {
        config.BrowserViewportWidth = Math.Clamp(config.BrowserViewportWidth, 320, 3840);
        config.BrowserViewportHeight = Math.Clamp(config.BrowserViewportHeight, 240, 2160);
        config.BrowserMaxSteps = Math.Clamp(config.BrowserMaxSteps, 1, 50);
        config.BrowserOperationTimeoutSeconds = Math.Clamp(config.BrowserOperationTimeoutSeconds, 5, 300);
        config.BrowserSessionTtlMinutes = Math.Clamp(config.BrowserSessionTtlMinutes, 1, 120);
        config.BrowserScreenshotScale = Math.Clamp(config.BrowserScreenshotScale, 0.25, 2.0);
        config.BrowserImageQuality = Math.Clamp(config.BrowserImageQuality, 30, 100);
        config.BrowserSomMaxElements = Math.Clamp(config.BrowserSomMaxElements, 10, 200);
        config.BrowserAgentMaxTokens = Math.Clamp(config.BrowserAgentMaxTokens, 100, 8000);
        config.BrowserAgentTemperature = Math.Clamp(config.BrowserAgentTemperature, 0, 2);
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

    private string GetString(string key, string defaultValue)
    {
        return _localizationService?.GetString(key, defaultValue) ?? defaultValue;
    }

    #region 模型列表拉取（可编辑下拉框）

    /// <summary>主模型可选列表（通过 /v1/models 拉取，可手动输入覆盖）。</summary>
    public ObservableCollection<string> PrimaryModelOptions { get; } = new();
    public ObservableCollection<string> SecondaryModelOptions { get; } = new();
    public ObservableCollection<string> EmbeddingModelOptions { get; } = new();

    [ObservableProperty]
    private bool _isLoadingPrimaryModels;
    [ObservableProperty]
    private bool _isLoadingSecondaryModels;
    [ObservableProperty]
    private bool _isLoadingEmbeddingModels;

    [ObservableProperty]
    private string _primaryModelsStatus = string.Empty;
    [ObservableProperty]
    private string _secondaryModelsStatus = string.Empty;
    [ObservableProperty]
    private string _embeddingModelsStatus = string.Empty;

    [RelayCommand]
    private Task LoadModelsAsync(string? target) => LoadModelsCoreAsync(target);

    private async Task LoadModelsCoreAsync(string? target)
    {
        // 解析目标字段：各自的 BaseUrl/Key，次要/嵌入为空时回退到主配置。
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

            var filtered = target == "Embedding"
                ? result.Models.Where(m => m?.IndexOf("embedding", StringComparison.OrdinalIgnoreCase) >= 0).ToList()
                : result.Models.ToList();

            options.Clear();
            foreach (var model in filtered)
            {
                options.Add(model);
            }

            if (filtered.Count == 0)
            {
                setStatus(GetString("Status.NoModelsReturned", "Endpoint returned no models"));
            }
            else
            {
                setStatus(string.Format(
                    GetString("Status.ModelsLoaded", "Loaded {0} models"),
                    filtered.Count));
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
