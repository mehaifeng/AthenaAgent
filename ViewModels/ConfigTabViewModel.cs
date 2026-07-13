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
    private readonly IKnowledgeBaseService? _knowledgeBaseService;
    private readonly ILogger _logger = Log.ForContext<ConfigTabViewModel>();

    /// <summary>已应用到 embedding 服务的向量身份（模型/提供商/端点/凭据源）。变化时需重建知识库向量索引。</summary>
    private string? _appliedEmbeddingIdentity;

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

    public ObservableCollection<string> Providers { get; } = new(ProviderCatalog.ChatProviders);
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

    /// <summary>子代理模型来源开关：true=跟随主模型（复用主对话模型凭据），false=自定义。绑定到 ToggleSwitch。</summary>
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

    public ConfigTabViewModel(IConfigService? configService, IChatService? chatService, IEmbeddingService? embeddingService, IConversationHistoryService? historyService, ILocalizationService? localizationService, IModelCatalogService? modelCatalogService = null, IKnowledgeBaseMaintenanceService? maintenanceService = null, IKnowledgeBaseService? knowledgeBaseService = null)
    {
        _configService = configService;
        _chatService = chatService;
        _embeddingService = embeddingService;
        _historyService = historyService;
        _localizationService = localizationService;
        _modelCatalogService = modelCatalogService;
        _maintenanceService = maintenanceService;
        _knowledgeBaseService = knowledgeBaseService;

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
        if (value == null) return;

        SetupConfigListener(value);
        // Config 被整体替换后，刷新依赖其值的计算属性（外部工具改配置等场景）
        NotifyCredentialSourceProperties();
    }

    /// <summary>刷新所有依赖 Config 取值的「来源开关」计算属性。</summary>
    private void NotifyCredentialSourceProperties()
    {
        OnPropertyChanged(nameof(IsMaintenanceModelCustom));
        OnPropertyChanged(nameof(UseMainCredentialForSecondary));
        OnPropertyChanged(nameof(IsSecondaryCredentialCustom));
        OnPropertyChanged(nameof(UseMainCredentialForEmbedding));
        OnPropertyChanged(nameof(IsEmbeddingCredentialCustom));
        OnPropertyChanged(nameof(UseMainModelForSubAgent));
        OnPropertyChanged(nameof(IsSubAgentModelCustom));
    }

    /// <summary>
    /// 外部（如 LLM 工具调用）修改配置后，在 UI 线程上刷新 ConfigTab 显示
    /// </summary>
    private void OnExternalConfigChanged(object? sender, AppConfig newConfig)
    {
        if (_isInternalSaving) return; // Ignore events triggered by our own auto-save

        // 其他页面（MCP/扩展）对共享实例的显式保存也会走到这里：
        // 同一实例说明本 VM 的监听早已就位，无需整体替换（替换同实例本身也会被 setter 相等性检查吞掉）。
        if (ReferenceEquals(newConfig, Config)) return;

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            AppConfigNormalizer.NormalizeAudio(newConfig);
            Config = newConfig;
            if (TokenService != null) TokenService.MaxTokens = newConfig.MaxContextTokens;
            _logger.Information("ConfigTab: 检测到外部配置变更，已刷新 UI");
        });
    }

    private void SetupConfigListener(AppConfig? config)
    {
        if (config == null) return;
        config.PropertyChanged += (s, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(AppConfig.MaxContextTokens):
                    if (TokenService != null) TokenService.MaxTokens = config.MaxContextTokens;
                    break;

                // 供应商切换 → 回填该类别的默认端点
                case nameof(AppConfig.Provider):
                    if (ProviderCatalog.TryGetChatBaseUrl(config.Provider, out var mainUrl))
                        config.BaseUrl = mainUrl;
                    break;
                case nameof(AppConfig.SecondaryProvider):
                    if (ProviderCatalog.TryGetChatBaseUrl(config.SecondaryProvider, out var secondaryUrl))
                        config.SecondaryBaseUrl = secondaryUrl;
                    break;
                case nameof(AppConfig.EmbeddingProvider):
                    if (ProviderCatalog.TryGetChatBaseUrl(config.EmbeddingProvider, out var embeddingUrl))
                        config.EmbeddingBaseUrl = embeddingUrl;
                    break;
                case nameof(AppConfig.BrowserAgentProvider):
                    if (ProviderCatalog.TryGetChatBaseUrl(config.BrowserAgentProvider, out var browserUrl))
                        config.BrowserAgentBaseUrl = browserUrl;
                    break;
                case nameof(AppConfig.WebSearchProvider):
                    if (ProviderCatalog.TryGetWebSearchBaseUrl(config.WebSearchProvider, out var searchUrl))
                        config.WebSearchBaseUrl = searchUrl;
                    break;
                case nameof(AppConfig.ChatAudioProvider):
                    if (AudioConfigResolver.TryGetDefaultBaseUrl(config.ChatAudioProvider, out var audioUrl))
                        config.ChatAudioBaseUrl = audioUrl;
                    break;

                // 模型/凭据来源切换 → 通知依赖的计算属性
                case nameof(AppConfig.KnowledgeMaintenanceModelSource):
                    OnPropertyChanged(nameof(IsMaintenanceModelCustom));
                    break;
                case nameof(AppConfig.SecondaryCredentialSource):
                    OnPropertyChanged(nameof(UseMainCredentialForSecondary));
                    OnPropertyChanged(nameof(IsSecondaryCredentialCustom));
                    break;
                case nameof(AppConfig.EmbeddingCredentialSource):
                    OnPropertyChanged(nameof(UseMainCredentialForEmbedding));
                    OnPropertyChanged(nameof(IsEmbeddingCredentialCustom));
                    break;
                case nameof(AppConfig.SubAgentModelSource):
                    OnPropertyChanged(nameof(UseMainModelForSubAgent));
                    OnPropertyChanged(nameof(IsSubAgentModelCustom));
                    break;
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
                    _logger.Information("压缩阈值已自动调整为最大上下文限制: {Value}", Config.MaxContextTokens);
                }

                AppConfigNormalizer.NormalizeBrowser(Config);
                AppConfigNormalizer.NormalizeAudio(Config);
                await _configService.SaveAsync(Config);

                // Dispatch UI-related updates back to the UI thread
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    App.SetTheme(Config.Theme);
                    _chatService?.UpdateConfig(Config);
                    if (_embeddingService is OpenAIEmbeddingService openAIEmbedding)
                    {
                        openAIEmbedding.UpdateConfig(Config);

                        // 换 embedding 模型/端点会改变向量空间：旧向量与新查询不可比，recall 将查无结果。
                        // 使内存向量缓存失效，下次检索时 KnowledgeBaseService 的指纹校验会触发全量重建。
                        var newIdentity = ComputeEmbeddingIdentity(Config);
                        if (newIdentity != _appliedEmbeddingIdentity)
                        {
                            _appliedEmbeddingIdentity = newIdentity;
                            if (_knowledgeBaseService != null)
                            {
                                await _knowledgeBaseService.RefreshVectorCacheAsync();
                                _logger.Information("Embedding 配置变化，已失效向量缓存，下次检索将重建知识库索引");
                            }
                        }
                    }
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

    /// <summary>汇总影响向量空间的 embedding 配置。任一变化都意味着旧向量与新查询不可比，需重建索引。</summary>
    private static string ComputeEmbeddingIdentity(AppConfig config)
        => string.Join('|',
            config.EmbeddingCredentialSource,
            config.EmbeddingProvider ?? string.Empty,
            config.EmbeddingBaseUrl ?? string.Empty,
            config.EmbeddingModel ?? string.Empty);

    private async Task LoadConfigAsync()
    {
        if (_configService != null)
        {
            Config = await _configService.LoadAsync();
            AppConfigNormalizer.NormalizeAudio(Config);
            _appliedEmbeddingIdentity = ComputeEmbeddingIdentity(Config);
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
        Func<string, bool>? filter = null;
        Func<IModelCatalogService, string?, string?, Task<ModelCatalogResult>>? fetch = null;

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
                if (IsOpenRouterUrl(baseUrl))
                {
                    // OpenRouter 支持服务端按 embeddings 精确过滤，无需本地关键字兜底。
                    fetch = (c, b, k) => c.GetEmbeddingModelsAsync(b, k);
                }
                else
                {
                    filter = IsEmbeddingModelId;
                }
                break;
            default:
                return;
        }

        if (alreadyLoading) return; // 防重入

        setLoading(true);
        try
        {
            await ModelOptionsLoader.LoadAsync(_modelCatalogService, baseUrl, apiKey, options, setStatus, GetString, filter, fetch);
        }
        finally
        {
            setLoading(false);
        }
    }

    private static string? FirstNonBlank(string? primary, string? fallback)
        => string.IsNullOrWhiteSpace(primary) ? fallback : primary;

    /// <summary>
    /// OpenAI 兼容的 /models 接口不区分模型类别，只能按 ID 命名启发式过滤出嵌入模型。
    /// 覆盖常见命名：embedding / embed（OpenAI、通义等）、GTE（阿里 general text embedding）、bge（BAAI）。
    /// </summary>
    private static readonly string[] EmbeddingModelKeywords = { "embedding", "embed", "gte", "bge" };

    private static bool IsEmbeddingModelId(string? modelId)
        => !string.IsNullOrWhiteSpace(modelId)
           && EmbeddingModelKeywords.Any(k => modelId.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);

    /// <summary>端点是否为 OpenRouter：其 /models 接口支持 output_modalities 服务端过滤。</summary>
    private static bool IsOpenRouterUrl(string? baseUrl)
        => !string.IsNullOrWhiteSpace(baseUrl)
           && Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri)
           && (uri.Host.Equals("openrouter.ai", StringComparison.OrdinalIgnoreCase)
               || uri.Host.EndsWith(".openrouter.ai", StringComparison.OrdinalIgnoreCase));

    #endregion
}
