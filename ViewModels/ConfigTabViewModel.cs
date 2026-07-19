using Athena.UI.Models;
using Athena.UI.Services;
using Athena.UI.Services.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
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
    private readonly ILocalizationService? _localizationService;
    private readonly IModelCatalogService? _modelCatalogService;
    private readonly IKnowledgeBaseMaintenanceService? _maintenanceService;
    private readonly IKnowledgeBaseService? _knowledgeBaseService;
    private readonly IHeadlessBrowserService? _browserService;
    private readonly IBrowserVisionService? _browserVisionService;
    private readonly ILogger _logger = Log.ForContext<ConfigTabViewModel>();

    /// <summary>已应用到 embedding 服务的向量身份（模型/提供商/端点/凭据源）。变化时需重建知识库向量索引。</summary>
    private string? _appliedEmbeddingIdentity;

    [ObservableProperty]
    private AppConfig _config = new();

    [ObservableProperty]
    private string _connectionStatus = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshModelOptionsCommand))]
    private bool _isRefreshingModelOptions;

    [ObservableProperty]
    private string _modelOptionsStatus = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestConnectionCommand))]
    private bool _isTestingConnection;

    [ObservableProperty]
    private bool _isCompressing;

    [ObservableProperty]
    private ChatTabViewModel? _chatTabViewModel;

    [ObservableProperty]
    private ITokenService? _tokenService;

    public bool CanTestConnection => !IsTestingConnection;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunKnowledgeMaintenanceCommand))]
    private bool _isRunningMaintenance;

    [ObservableProperty]
    private string _maintenanceStatus = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RebuildVectorIndexCommand))]
    private bool _isRebuildingVectorIndex;

    [ObservableProperty]
    private string _vectorIndexStatus = string.Empty;

    public bool CanRunMaintenance => !IsRunningMaintenance;
    public bool CanRebuildVectorIndex => !IsRebuildingVectorIndex;

    public bool CanTestBrowserRuntime => !IsTestingBrowserRuntime && !IsInstallingBrowserRuntime;
    public bool CanInstallBrowserRuntime => !IsInstallingBrowserRuntime && !IsTestingBrowserRuntime;
    public bool CanTestBrowserAgent => !IsTestingBrowserAgent;

    [ObservableProperty] private string _browserRuntimeStatus = string.Empty;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(TestBrowserRuntimeCommand)), NotifyCanExecuteChangedFor(nameof(InstallBrowserRuntimeCommand))] private bool _isTestingBrowserRuntime;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(TestBrowserRuntimeCommand)), NotifyCanExecuteChangedFor(nameof(InstallBrowserRuntimeCommand))] private bool _isInstallingBrowserRuntime;
    [ObservableProperty] private string _browserAgentTestStatus = string.Empty;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(TestBrowserAgentCommand))] private bool _isTestingBrowserAgent;

    public bool HasBrowserRuntimeStatus => !string.IsNullOrWhiteSpace(BrowserRuntimeStatus);
    public bool HasBrowserAgentTestStatus => !string.IsNullOrWhiteSpace(BrowserAgentTestStatus);

    partial void OnBrowserRuntimeStatusChanged(string value) => OnPropertyChanged(nameof(HasBrowserRuntimeStatus));
    partial void OnBrowserAgentTestStatusChanged(string value) => OnPropertyChanged(nameof(HasBrowserAgentTestStatus));

    private CancellationTokenSource? _autoSaveCts;
    private bool _isInternalSaving;
    private readonly List<Action> _configUnsubscribers = new();

    public ObservableCollection<string> Providers { get; } = new(ProviderCatalog.ChatProviders);
    public ObservableCollection<EmbeddingConnectionSource> EmbeddingCredentialSources { get; } = new()
    {
        EmbeddingConnectionSource.Provider,
        EmbeddingConnectionSource.Custom
    };
    public ObservableCollection<BrowserObservationMode> BrowserObservationModes { get; } = new()
    {
        BrowserObservationMode.VisionWithSom,
        BrowserObservationMode.DomOnly
    };
    public ObservableCollection<BrowserStructuredOutputMode> BrowserStructuredOutputModes { get; } = new()
    {
        BrowserStructuredOutputMode.Auto,
        BrowserStructuredOutputMode.JsonObject,
        BrowserStructuredOutputMode.PromptOnly
    };
    public ObservableCollection<string> Themes { get; } = new() { "Dark", "Light" };
    public ObservableCollection<ModelRoleAssignmentViewModel> AiModelRoles { get; } = new();
    public ObservableCollection<string> EmbeddingModelOptions { get; } = new()
    {
        "text-embedding-3-small", "text-embedding-3-large"
    };
    public OpenAiProviderConfiguration AiProvider => Config.AiModels.Provider;
    public bool IsAutomaticApproval => Config.ToolApprovalMode == ToolApprovalMode.Automatic;
    public bool IsEmbeddingCustomConnection => Config.EmbeddingCredentialSource == EmbeddingConnectionSource.Custom;
    public bool IsBrowserVisionEnabled => Config.BrowserObservationMode != BrowserObservationMode.DomOnly;

    /// <summary>工具审批模式候选。</summary>
    public ObservableCollection<ToolApprovalMode> ToolApprovalModes { get; } = new()
    {
        ToolApprovalMode.Off,
        ToolApprovalMode.Balanced,
        ToolApprovalMode.Strict,
        ToolApprovalMode.Automatic
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

    public ConfigTabViewModel() : this(null, null, null, null) { }

    public ConfigTabViewModel(IConfigService? configService, IChatService? chatService, IEmbeddingService? embeddingService, ILocalizationService? localizationService, IModelCatalogService? modelCatalogService = null, IKnowledgeBaseMaintenanceService? maintenanceService = null, IKnowledgeBaseService? knowledgeBaseService = null, IHeadlessBrowserService? browserService = null, IBrowserVisionService? browserVisionService = null)
    {
        _configService = configService;
        _chatService = chatService;
        _embeddingService = embeddingService;
        _localizationService = localizationService;
        _modelCatalogService = modelCatalogService;
        _maintenanceService = maintenanceService;
        _knowledgeBaseService = knowledgeBaseService;
        _browserService = browserService;
        _browserVisionService = browserVisionService;

        if (_maintenanceService != null)
        {
            _maintenanceService.StateChanged += OnMaintenanceStateChanged;
            UpdateMaintenanceStatus();
        }

        if (_localizationService != null)
        {
            Languages = new ObservableCollection<string>(_localizationService.AvailableLanguageNames);
            _localizationService.LanguageChanged += (_, _) => RebuildAiModelRoles();
        }
        else
        {
            Languages = new ObservableCollection<string> { "English", "中文" };
        }

        SetupConfigListener(Config);
        RebuildAiModelRoles();
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
        RebuildAiModelRoles();
        OnPropertyChanged(nameof(AiProvider));
        OnPropertyChanged(nameof(IsAutomaticApproval));
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
        foreach (var unsubscribe in _configUnsubscribers) unsubscribe();
        _configUnsubscribers.Clear();
        if (config == null) return;
        PropertyChangedEventHandler configChanged = (s, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(AppConfig.MaxContextTokens):
                    if (TokenService != null) TokenService.MaxTokens = config.MaxContextTokens;
                    break;

                case nameof(AppConfig.WebSearchProvider):
                    if (ProviderCatalog.TryGetWebSearchBaseUrl(config.WebSearchProvider, out var searchUrl))
                        config.WebSearchBaseUrl = searchUrl;
                    break;
                case nameof(AppConfig.ChatAudioProvider):
                    if (AudioConfigResolver.TryGetDefaultBaseUrl(config.ChatAudioProvider, out var audioUrl))
                        config.ChatAudioBaseUrl = audioUrl;
                    break;

                case nameof(AppConfig.ToolApprovalMode):
                    OnPropertyChanged(nameof(IsAutomaticApproval));
                    break;
                case nameof(AppConfig.EmbeddingCredentialSource):
                    OnPropertyChanged(nameof(IsEmbeddingCustomConnection));
                    break;
                case nameof(AppConfig.BrowserObservationMode):
                    OnPropertyChanged(nameof(IsBrowserVisionEnabled));
                    break;
            }

            // Trigger auto-save on any property change
            RequestAutoSave();
        };
        config.PropertyChanged += configChanged;
        _configUnsubscribers.Add(() => config.PropertyChanged -= configChanged);

        // MCP 服务器是嵌套对象/集合，其内部编辑不会冒泡为 AppConfig.PropertyChanged，
        // 必须深度订阅，否则用户填的命令/参数/环境变量（如 API Key）不会落盘。
        HookMcpDeep(config);
        HookAiModelsDeep(config);
    }

    private void HookAiModelsDeep(AppConfig config)
    {
        void HookRole(ModelRoleSettings role)
        {
            PropertyChangedEventHandler changed = (_, _) => RequestAutoSave();
            role.PropertyChanged += changed;
            _configUnsubscribers.Add(() => role.PropertyChanged -= changed);
        }
        void HookProvider(OpenAiProviderConfiguration provider)
        {
            PropertyChangedEventHandler changed = (_, e) =>
            {
                if (e.PropertyName == nameof(OpenAiProviderConfiguration.ProviderPreset))
                {
                    provider.DisplayName = provider.ProviderPreset;
                    if (ProviderCatalog.TryGetChatBaseUrl(provider.ProviderPreset, out var url))
                        provider.BaseUrl = url;
                }
                RequestAutoSave();
            };
            provider.PropertyChanged += changed;
            _configUnsubscribers.Add(() => provider.PropertyChanged -= changed);
        }

        HookProvider(config.AiModels.Provider);
        foreach (var role in GetRoleSettings(config.AiModels)) HookRole(role);
    }

    private static IEnumerable<ModelRoleSettings> GetRoleSettings(AiModelConfiguration models)
    {
        yield return models.MainConversation;
        yield return models.TitleGeneration;
        yield return models.ContextCompression;
        yield return models.Approval;
        yield return models.Embedding;
        yield return models.BrowserAgent;
        yield return models.SubAgent;
        yield return models.KnowledgeMaintenance;
    }

    private void RebuildAiModelRoles()
    {
        if (Config?.AiModels == null) return;
        AiModelRoles.Clear();
        void Add(AiModelRole role, string key, string fallback, ModelRoleSettings settings) =>
            AiModelRoles.Add(new ModelRoleAssignmentViewModel(
                role,
                _localizationService?.GetString(key, fallback) ?? fallback,
                settings));
        Add(AiModelRole.MainConversation, "Config.ModelRole.Main", "Main conversation", Config.AiModels.MainConversation);
        Add(AiModelRole.TitleGeneration, "Config.ModelRole.Title", "Title generation", Config.AiModels.TitleGeneration);
        Add(AiModelRole.ContextCompression, "Config.ModelRole.Compression", "Context compression", Config.AiModels.ContextCompression);
        Add(AiModelRole.Approval, "Config.ModelRole.Approval", "Automatic approval", Config.AiModels.Approval);
        Add(AiModelRole.BrowserAgent, "Config.ModelRole.Browser", "Automatic browser", Config.AiModels.BrowserAgent);
        Add(AiModelRole.SubAgent, "Config.ModelRole.SubAgent", "Sub-agent", Config.AiModels.SubAgent);
        Add(AiModelRole.KnowledgeMaintenance, "Config.ModelRole.Maintenance", "Knowledge maintenance", Config.AiModels.KnowledgeMaintenance);
        if (!EmbeddingModelOptions.Contains(Config.AiModels.Embedding.Model))
            EmbeddingModelOptions.Add(Config.AiModels.Embedding.Model);
        foreach (var role in AiModelRoles)
            if (!role.ModelOptions.Contains(role.Settings.Model)) role.ModelOptions.Add(role.Settings.Model);
    }

    /// <summary>深度订阅 McpServers 集合及其每个条目的变更 → 触发自动保存。</summary>
    private void HookMcpDeep(AppConfig config)
    {
        foreach (var server in config.McpServers)
            HookMcpServer(server);

        NotifyCollectionChangedEventHandler serversChanged = (_, e) =>
        {
            if (e.NewItems != null)
                foreach (McpServerConfig s in e.NewItems) HookMcpServer(s);
            RequestAutoSave();
        };
        config.McpServers.CollectionChanged += serversChanged;
        _configUnsubscribers.Add(() => config.McpServers.CollectionChanged -= serversChanged);
    }

    private void HookMcpServer(McpServerConfig server)
    {
        PropertyChangedEventHandler serverChanged = (_, e) =>
        {
            // 运行期状态字段由连接过程回填，不应触发保存（否则连接→状态变→保存→重连 死循环）。
            // IsExpanded 是纯 UI 折叠状态，同样不落盘、不触发重连。
            if (e.PropertyName is nameof(McpServerConfig.Status)
                or nameof(McpServerConfig.StatusDetail)
                or nameof(McpServerConfig.DiscoveredToolCount)
                or nameof(McpServerConfig.IsExpanded))
                return;
            RequestAutoSave();
        };
        server.PropertyChanged += serverChanged;
        _configUnsubscribers.Add(() => server.PropertyChanged -= serverChanged);

        void HookArgument(McpArgEntry argument)
        {
            PropertyChangedEventHandler changed = (_, _) => RequestAutoSave();
            argument.PropertyChanged += changed;
            _configUnsubscribers.Add(() => argument.PropertyChanged -= changed);
        }
        NotifyCollectionChangedEventHandler argumentsChanged = (_, e) =>
        {
            if (e.NewItems != null)
                foreach (McpArgEntry a in e.NewItems) HookArgument(a);
            RequestAutoSave();
        };
        server.Arguments.CollectionChanged += argumentsChanged;
        _configUnsubscribers.Add(() => server.Arguments.CollectionChanged -= argumentsChanged);
        foreach (var a in server.Arguments) HookArgument(a);

        void HookEnvironment(McpEnvEntry environment)
        {
            PropertyChangedEventHandler changed = (_, _) => RequestAutoSave();
            environment.PropertyChanged += changed;
            _configUnsubscribers.Add(() => environment.PropertyChanged -= changed);
        }
        NotifyCollectionChangedEventHandler environmentChanged = (_, e) =>
        {
            if (e.NewItems != null)
                foreach (McpEnvEntry en in e.NewItems) HookEnvironment(en);
            RequestAutoSave();
        };
        server.Environment.CollectionChanged += environmentChanged;
        _configUnsubscribers.Add(() => server.Environment.CollectionChanged -= environmentChanged);
        foreach (var en in server.Environment) HookEnvironment(en);

        void HookHeader(McpEnvEntry header)
        {
            PropertyChangedEventHandler changed = (_, _) => RequestAutoSave();
            header.PropertyChanged += changed;
            _configUnsubscribers.Add(() => header.PropertyChanged -= changed);
        }
        NotifyCollectionChangedEventHandler headersChanged = (_, e) =>
        {
            if (e.NewItems != null)
                foreach (McpEnvEntry h in e.NewItems) HookHeader(h);
            RequestAutoSave();
        };
        server.Headers.CollectionChanged += headersChanged;
        _configUnsubscribers.Add(() => server.Headers.CollectionChanged -= headersChanged);
        foreach (var h in server.Headers) HookHeader(h);
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
    {
        try
        {
            var effective = OpenAiModelRuntimeFactory.Resolve(config, AiModelRole.Embedding);
            return string.Join('|', effective.ProviderPreset, effective.BaseUrl, effective.Model);
        }
        catch
        {
            return string.Empty;
        }
    }

    private async Task LoadConfigAsync()
    {
        if (_configService != null)
        {
            Config = await _configService.LoadAsync();
            AppConfigNormalizer.NormalizeAudio(Config);
            _appliedEmbeddingIdentity = ComputeEmbeddingIdentity(Config);
            await LoadModelOptionsAsync(showStatus: false);
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

    [RelayCommand(CanExecute = nameof(CanRefreshModelOptions))]
    private Task RefreshModelOptionsAsync() => LoadModelOptionsAsync(showStatus: true);

    private bool CanRefreshModelOptions => !IsRefreshingModelOptions;

    private async Task LoadModelOptionsAsync(bool showStatus)
    {
        if (_modelCatalogService == null) return;

        IsRefreshingModelOptions = true;
        if (showStatus) ModelOptionsStatus = GetString("Status.LoadingModels", "Loading model list…");
        try
        {
            var provider = AiProvider;
            var textResult = await _modelCatalogService.GetTextModelsAsync(provider.BaseUrl, provider.ApiKey);
            if (!textResult.Success)
            {
                if (showStatus)
                    ModelOptionsStatus = string.Format(
                        GetString("Status.LoadModelsFailed", "Failed to load model list: {0}"),
                        textResult.ErrorMessage);
                return;
            }

            var selectedRoleModels = AiModelRoles.ToDictionary(
                role => role.Role,
                role => role.Settings.Model);
            foreach (var role in AiModelRoles)
            {
                var selectedModel = selectedRoleModels[role.Role];
                role.ModelOptions.Clear();
                foreach (var model in textResult.Models) role.ModelOptions.Add(model);
                if (!string.IsNullOrWhiteSpace(selectedModel) && !role.ModelOptions.Contains(selectedModel))
                    role.ModelOptions.Add(selectedModel);
                role.Settings.Model = selectedModel;
            }

            var embeddingBaseUrl = IsEmbeddingCustomConnection ? Config.EmbeddingBaseUrl : provider.BaseUrl;
            var embeddingApiKey = IsEmbeddingCustomConnection ? Config.EmbeddingApiKey : provider.ApiKey;
            var embeddingIsOpenRouter = IsOpenRouter(embeddingBaseUrl);
            var embeddingResult = await _modelCatalogService.GetEmbeddingModelsAsync(embeddingBaseUrl, embeddingApiKey);
            if (embeddingResult.Success)
            {
                var selectedEmbeddingModel = Config.AiModels.Embedding.Model;
                var embeddingModels = embeddingIsOpenRouter
                    ? embeddingResult.Models
                    : embeddingResult.Models.Where(IsEmbeddingModel).ToList();
                EmbeddingModelOptions.Clear();
                foreach (var model in embeddingModels) EmbeddingModelOptions.Add(model);
                if (!string.IsNullOrWhiteSpace(selectedEmbeddingModel) && !EmbeddingModelOptions.Contains(selectedEmbeddingModel))
                    EmbeddingModelOptions.Add(selectedEmbeddingModel);
                Config.AiModels.Embedding.Model = selectedEmbeddingModel;
            }

            if (showStatus)
            {
                ModelOptionsStatus = embeddingResult.Success
                    ? string.Format(
                        GetString("Status.TextAndEmbeddingModelsLoaded", "Loaded {0} text model(s) and {1} embedding model(s)"),
                        textResult.Models.Count,
                        EmbeddingModelOptions.Count)
                    : string.Format(
                        GetString("Status.TextModelsLoadedEmbeddingFailed", "Loaded {0} text model(s); embedding models failed: {1}"),
                        textResult.Models.Count,
                        embeddingResult.ErrorMessage);
            }
        }
        finally
        {
            IsRefreshingModelOptions = false;
        }
    }

    private static bool IsEmbeddingModel(string model) =>
        new[] { "embedding", "embed", "gte", "bge" }
            .Any(keyword => model.Contains(keyword, StringComparison.OrdinalIgnoreCase));

    private static bool IsOpenRouter(string? baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
        && (uri.Host.Equals("openrouter.ai", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".openrouter.ai", StringComparison.OrdinalIgnoreCase));

    [RelayCommand(CanExecute = nameof(CanTestConnection))]
    private async Task TestConnectionAsync()
    {
        if (_chatService == null) { ConnectionStatus = _localizationService?.GetString("Status.ServiceNotInitialized") ?? "Service not initialized"; return; }
        try
        {
            OpenAiModelRuntimeFactory.Resolve(Config, AiModelRole.MainConversation).ValidateChatRole(AiModelRole.MainConversation);
        }
        catch
        {
            ConnectionStatus = _localizationService?.GetString("Status.EnterApiKeyFirst") ?? "Please enter API Key and model first";
            return;
        }
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

    [RelayCommand(CanExecute = nameof(CanTestBrowserRuntime))]
    private async Task TestBrowserRuntimeAsync()
    {
        if (_browserService == null) { BrowserRuntimeStatus = GetString("Status.ServiceNotInitialized", "Service not initialized"); return; }
        if (!Config.BrowserEnabled) { BrowserRuntimeStatus = GetString("Status.EnableBrowserFirst", "Please enable Browser first"); return; }

        IsTestingBrowserRuntime = true;
        BrowserRuntimeStatus = GetString("Status.TestingConnection", "Testing...");
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
        if (_browserService == null) { BrowserRuntimeStatus = GetString("Status.ServiceNotInitialized", "Service not initialized"); return; }
        if (!Config.BrowserEnabled) { BrowserRuntimeStatus = GetString("Status.EnableBrowserFirst", "Please enable Browser first"); return; }

        IsInstallingBrowserRuntime = true;
        BrowserRuntimeStatus = GetString("Status.InstallingBrowserRuntime", "Installing browser runtime...");
        try { BrowserRuntimeStatus = (await _browserService.InstallRuntimeAsync()).Message; }
        finally { IsInstallingBrowserRuntime = false; }
    }

    [RelayCommand(CanExecute = nameof(CanTestBrowserAgent))]
    private async Task TestBrowserAgentAsync()
    {
        if (_browserVisionService == null) { BrowserAgentTestStatus = GetString("Status.ServiceNotInitialized", "Service not initialized"); return; }
        if (!Config.BrowserEnabled) { BrowserAgentTestStatus = GetString("Status.EnableBrowserFirst", "Please enable Browser first"); return; }

        IsTestingBrowserAgent = true;
        BrowserAgentTestStatus = GetString("Status.TestingConnection", "Testing...");
        try
        {
            AppConfigNormalizer.NormalizeBrowser(Config);
            if (_configService != null) await _configService.SaveAsync(Config);
            BrowserAgentTestStatus = (await _browserVisionService.TestConnectionAsync()).Message;
        }
        finally { IsTestingBrowserAgent = false; }
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

    [RelayCommand(CanExecute = nameof(CanRebuildVectorIndex))]
    private async Task RebuildVectorIndexAsync()
    {
        if (_knowledgeBaseService == null)
        {
            VectorIndexStatus = GetString("Status.ServiceNotInitialized", "Service not initialized");
            return;
        }

        IsRebuildingVectorIndex = true;
        VectorIndexStatus = GetString("Config.VectorIndexRebuilding", "Rebuilding vector index…");
        try
        {
            // 确保刚编辑的 Embedding 配置先保存并应用，再据此生成新向量。
            _autoSaveCts?.Cancel();
            await ExecuteAutoSaveAsync();
            await _knowledgeBaseService.RebuildVectorIndexAsync();
            VectorIndexStatus = GetString("Config.VectorIndexRebuilt", "Vector index rebuilt");
        }
        catch (Exception ex)
        {
            VectorIndexStatus = string.Format(
                GetString("Config.VectorIndexRebuildFailed", "Failed to rebuild vector index: {0}"),
                ex.Message);
        }
        finally
        {
            IsRebuildingVectorIndex = false;
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

    private string GetString(string key, string defaultValue)
    {
        return _localizationService?.GetString(key, defaultValue) ?? defaultValue;
    }

}
