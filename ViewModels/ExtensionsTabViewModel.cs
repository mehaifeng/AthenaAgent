using Athena.UI.Models;
using Athena.UI.Services;
using Athena.UI.Services.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.ViewModels;

/// <summary>
/// 「神工坊」扩展页 ViewModel：扩展能力（语音/生图/搜索/浏览器/子代理/文档解析）、
/// MCP 服务器与工具审批。与 ConfigTabViewModel 共享同一个 AppConfig 实例——
/// 配置的加载、监听与防抖自动保存仍由 ConfigTabViewModel（配置属主）负责，
/// 本 VM 只承载扩展相关的 UI 状态、计算属性与命令。
/// </summary>
public partial class ExtensionsTabViewModel : ViewModelBase
{
    private readonly IConfigService? _configService;
    private readonly IChatService? _chatService;
    private readonly ILocalizationService? _localizationService;
    private readonly IWebSearchService? _webSearchService;
    private readonly IHeadlessBrowserService? _browserService;
    private readonly IBrowserVisionService? _browserVisionService;
    private readonly IAudioPlaybackService? _audioPlaybackService;
    private readonly ISystemAudioService? _systemAudioService;
    private readonly IModelCatalogService? _modelCatalogService;
    // Cancels in-flight system playback so Stop can kill the external process.
    private CancellationTokenSource? _audioTestCts;
    private readonly ILogger _logger = Log.ForContext<ExtensionsTabViewModel>();

    /// <summary>与 ConfigTabViewModel 共享的配置实例（由 Initialize 注入并跟随其替换）。</summary>
    [ObservableProperty]
    private AppConfig _config = new();

    public ExtensionsTabViewModel() : this(null, null, null, null, null, null, null, null, null) { }

    public ExtensionsTabViewModel(
        IConfigService? configService,
        IChatService? chatService,
        ILocalizationService? localizationService,
        IWebSearchService? webSearchService,
        IHeadlessBrowserService? browserService = null,
        IBrowserVisionService? browserVisionService = null,
        IAudioPlaybackService? audioPlaybackService = null,
        ISystemAudioService? systemAudioService = null,
        IModelCatalogService? modelCatalogService = null)
    {
        _configService = configService;
        _chatService = chatService;
        _localizationService = localizationService;
        _webSearchService = webSearchService;
        _browserService = browserService;
        _browserVisionService = browserVisionService;
        _audioPlaybackService = audioPlaybackService;
        _systemAudioService = systemAudioService;
        _modelCatalogService = modelCatalogService;

        if (_audioPlaybackService != null)
        {
            _audioPlaybackService.PlaybackStateChanged += OnAudioPlaybackStateChanged;
        }

        _ = LoadSystemVoicesAsync();
    }

    /// <summary>
    /// 绑定到配置属主：镜像其 Config 实例，并在属主因外部变更整体替换 Config 时跟随。
    /// </summary>
    public void Initialize(ConfigTabViewModel configOwner)
    {
        Config = configOwner.Config;
        configOwner.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ConfigTabViewModel.Config))
            {
                Config = configOwner.Config;
            }
        };
    }

    partial void OnConfigChanged(AppConfig value)
    {
        if (value == null) return;

        // Config 实例被整体替换后，刷新依赖其值的计算属性
        OnPropertyChanged(nameof(IsBrowserVisionEnabled));
        OnPropertyChanged(nameof(UseMainModelForBrowser));
        OnPropertyChanged(nameof(IsBrowserModelCustom));
        OnPropertyChanged(nameof(UseMainModelForSubAgent));
        OnPropertyChanged(nameof(IsSubAgentModelCustom));
        OnPropertyChanged(nameof(UseMainCredentialForImageGeneration));
        OnPropertyChanged(nameof(IsImageGenerationCredentialCustom));
        OnPropertyChanged(nameof(UseMainCredentialForChatAudio));
        OnPropertyChanged(nameof(IsChatAudioCredentialCustom));
        OnPropertyChanged(nameof(IsRemoteAudioProvider));
        OnPropertyChanged(nameof(CanEditRemoteAudioFields));
        OnPropertyChanged(nameof(IsBaiduProvider));
        OnPropertyChanged(nameof(CanEditParserToken));

        // 订阅属性级变化，驱动本页计算属性的通知（持久化由配置属主的监听负责）
        value.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(AppConfig.WebSearchProvider):
                    OnPropertyChanged(nameof(IsBaiduProvider));
                    break;
                case nameof(AppConfig.ChatAudioProvider):
                    OnPropertyChanged(nameof(IsRemoteAudioProvider));
                    OnPropertyChanged(nameof(CanEditRemoteAudioFields));
                    break;
                case nameof(AppConfig.ChatAudioEnabled):
                    OnPropertyChanged(nameof(CanEditRemoteAudioFields));
                    break;
                case nameof(AppConfig.BrowserObservationMode):
                    OnPropertyChanged(nameof(IsBrowserVisionEnabled));
                    break;
                case nameof(AppConfig.BrowserModelSource):
                    OnPropertyChanged(nameof(UseMainModelForBrowser));
                    OnPropertyChanged(nameof(IsBrowserModelCustom));
                    break;
                case nameof(AppConfig.SubAgentModelSource):
                    OnPropertyChanged(nameof(UseMainModelForSubAgent));
                    OnPropertyChanged(nameof(IsSubAgentModelCustom));
                    break;
                case nameof(AppConfig.ImageGenerationCredentialSource):
                    OnPropertyChanged(nameof(UseMainCredentialForImageGeneration));
                    OnPropertyChanged(nameof(IsImageGenerationCredentialCustom));
                    break;
                case nameof(AppConfig.ChatAudioCredentialSource):
                    OnPropertyChanged(nameof(UseMainCredentialForChatAudio));
                    OnPropertyChanged(nameof(IsChatAudioCredentialCustom));
                    break;
                case nameof(AppConfig.DocumentParserMode):
                case nameof(AppConfig.DocumentParserEnabled):
                    OnPropertyChanged(nameof(CanEditParserToken));
                    break;
            }
        };
    }

    private string GetString(string key, string defaultValue)
    {
        return _localizationService?.GetString(key, defaultValue) ?? defaultValue;
    }

    #region 集合与计算属性

    /// <summary>API 供应商候选（与配置页共用同一份目录）。</summary>
    public ObservableCollection<string> Providers { get; } = new(ConfigTabViewModel.ProviderNames);

    public ObservableCollection<BrowserObservationMode> BrowserObservationModes { get; } = new()
    {
        BrowserObservationMode.VisionWithSom,
        BrowserObservationMode.DomOnly
    };

    public bool IsBrowserVisionEnabled => Config.BrowserObservationMode != BrowserObservationMode.DomOnly;

    public ObservableCollection<string> WebSearchProviders { get; } = new() { "Tavily", "WebSearchAPI", "Zhipu", "Baidu" };

    public bool IsBaiduProvider => Config.WebSearchProvider == "Baidu";

    private static readonly System.Collections.Generic.Dictionary<string, string> WebSearchUrls = new()
    {
        { "Tavily", "https://api.tavily.com" },
        { "WebSearchAPI", "https://api.websearchapi.ai" },
        { "Zhipu", "https://open.bigmodel.cn/api/paas/v4" },
        { "Baidu", "https://qianfan.baidubce.com/v2/ai_search/web_search" }
    };

    public ObservableCollection<string> AudioProviders { get; } = new(ConfigTabViewModel.AudioProviderNames);

    // 系统 TTS 已安装的语音（Windows SAPI / macOS say / Linux espeak-ng），供 provider=System 时的输入下拉建议。
    public ObservableCollection<string> AudioVoices { get; } = new();

    public ObservableCollection<DocumentParserMode> DocumentParserModes { get; } = new()
    {
        DocumentParserMode.AgentLightweight,
        DocumentParserMode.Precision
    };

    // 仅精度解析方式需要 Token；极速解析无需登录。
    public bool CanEditParserToken => Config.DocumentParserEnabled && Config.DocumentParserMode == DocumentParserMode.Precision;

    public bool IsRemoteAudioProvider => Config.ChatAudioProvider != "System";
    public bool CanEditRemoteAudioFields => Config.ChatAudioEnabled && IsRemoteAudioProvider;

    /// <summary>浏览器智能体模型来源开关：true=跟随主模型，false=自定义。绑定到 ToggleSwitch。</summary>
    public bool UseMainModelForBrowser
    {
        get => Config.BrowserModelSource == BrowserModelSource.InheritMain;
        set
        {
            var newSource = value ? BrowserModelSource.InheritMain : BrowserModelSource.Custom;
            if (Config.BrowserModelSource != newSource)
            {
                Config.BrowserModelSource = newSource; // 触发 Config.PropertyChanged → 属主自动保存 + 下方通知
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
                Config.SubAgentModelSource = newSource; // 触发 Config.PropertyChanged → 属主自动保存 + 下方通知
            }
        }
    }

    /// <summary>是否使用自定义子代理模型（决定下方独立配置字段是否显示）。</summary>
    public bool IsSubAgentModelCustom => Config.SubAgentModelSource == SubAgentModelSource.Custom;

    /// <summary>图像生成凭据来源开关。</summary>
    public bool UseMainCredentialForImageGeneration
    {
        get => Config.ImageGenerationCredentialSource == ModelCredentialSource.InheritMain;
        set => SetCredentialSource(v => Config.ImageGenerationCredentialSource = v, Config.ImageGenerationCredentialSource, value);
    }

    public bool IsImageGenerationCredentialCustom => Config.ImageGenerationCredentialSource == ModelCredentialSource.Custom;

    /// <summary>音频凭据来源开关（InheritMain 仅继承 ApiKey，BaseUrl 按 provider 默认端点解析）。</summary>
    public bool UseMainCredentialForChatAudio
    {
        get => Config.ChatAudioCredentialSource == ModelCredentialSource.InheritMain;
        set => SetCredentialSource(v => Config.ChatAudioCredentialSource = v, Config.ChatAudioCredentialSource, value);
    }

    public bool IsChatAudioCredentialCustom => Config.ChatAudioCredentialSource == ModelCredentialSource.Custom;

    private static void SetCredentialSource(Action<ModelCredentialSource> setter, ModelCredentialSource current, bool inherit)
    {
        var newSource = inherit ? ModelCredentialSource.InheritMain : ModelCredentialSource.Custom;
        if (current != newSource)
        {
            setter(newSource); // 触发 Config.PropertyChanged → 属主自动保存 + 下方通知
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

    #endregion

    #region MCP 服务器

    /// <summary>新增一个空 MCP 服务器条目，等待用户填入命令。</summary>
    [RelayCommand]
    private async Task AddMcpServerAsync()
    {
        var idx = Config.McpServers.Count + 1;
        Config.McpServers.Add(new McpServerConfig
        {
            Name = $"server-{idx}",
            Enabled = false,
            Command = string.Empty,
            StartupTimeoutSeconds = 15,
            CallTimeoutSeconds = 60
        });
        if (_configService != null) await _configService.SaveAsync(Config);
    }

    /// <summary>删除指定 MCP 服务器条目。</summary>
    [RelayCommand]
    private async Task RemoveMcpServerAsync(McpServerConfig? server)
    {
        if (server != null && Config.McpServers.Remove(server) && _configService != null)
        {
            await _configService.SaveAsync(Config);
        }
    }

    /// <summary>给指定服务器追加一个空参数条目。</summary>
    [RelayCommand]
    private async Task AddMcpArgAsync(McpServerConfig? server)
    {
        if (server == null) return;
        server.Arguments.Add(new McpArgEntry());
        if (_configService != null) await _configService.SaveAsync(Config);
    }

    /// <summary>删除一个参数条目（在所有服务器里查找其归属）。</summary>
    [RelayCommand]
    private async Task RemoveMcpArgAsync(McpArgEntry? entry)
    {
        if (entry == null) return;
        foreach (var s in Config.McpServers)
        {
            if (s.Arguments.Remove(entry)) break;
        }
        if (_configService != null) await _configService.SaveAsync(Config);
    }

    /// <summary>给指定服务器追加一个空环境变量条目。</summary>
    [RelayCommand]
    private async Task AddMcpEnvAsync(McpServerConfig? server)
    {
        if (server == null) return;
        server.Environment.Add(new McpEnvEntry());
        if (_configService != null) await _configService.SaveAsync(Config);
    }

    /// <summary>删除一个环境变量条目（在所有服务器里查找其归属）。</summary>
    [RelayCommand]
    private async Task RemoveMcpEnvAsync(McpEnvEntry? entry)
    {
        if (entry == null) return;
        foreach (var s in Config.McpServers)
        {
            if (s.Environment.Remove(entry)) break;
        }
        if (_configService != null) await _configService.SaveAsync(Config);
    }

    /// <summary>给指定 Http 服务器追加一个空请求头条目。</summary>
    [RelayCommand]
    private async Task AddMcpHeaderAsync(McpServerConfig? server)
    {
        if (server == null) return;
        server.Headers.Add(new McpEnvEntry());
        if (_configService != null) await _configService.SaveAsync(Config);
    }

    /// <summary>删除一个请求头条目（在所有服务器里查找其归属）。</summary>
    [RelayCommand]
    private async Task RemoveMcpHeaderAsync(McpEnvEntry? entry)
    {
        if (entry == null) return;
        foreach (var s in Config.McpServers)
        {
            if (s.Headers.Remove(entry)) break;
        }
        if (_configService != null) await _configService.SaveAsync(Config);
    }

    /// <summary>重新应用 MCP 配置：再次保存即触发 ConfigChanged → 生命周期重连未生效（含失败）的服务器。</summary>
    [RelayCommand]
    private async Task ReconnectMcpAsync()
    {
        if (_configService != null) await _configService.SaveAsync(Config);
    }

    /// <summary>粘贴的 MCP JSON（Claude Desktop 格式），供导入命令读取。</summary>
    [ObservableProperty]
    private string _mcpImportJson = string.Empty;

    /// <summary>导入结果提示（成功计数或错误信息）。</summary>
    [ObservableProperty]
    private string _mcpImportStatus = string.Empty;

    /// <summary>解析粘贴的 JSON，按名称合并进 McpServers（同名覆盖）。</summary>
    [RelayCommand]
    private async Task ImportMcpJsonAsync()
    {
        if (string.IsNullOrWhiteSpace(McpImportJson))
        {
            McpImportStatus = "请粘贴 MCP 配置 JSON。";
            return;
        }

        try
        {
            var parsed = Services.Mcp.McpConfigImporter.Parse(McpImportJson);
            foreach (var incoming in parsed)
            {
                var existing = Config.McpServers.FirstOrDefault(
                    s => string.Equals(s.Name, incoming.Name, StringComparison.OrdinalIgnoreCase));
                if (existing != null) Config.McpServers.Remove(existing);
                Config.McpServers.Add(incoming);
            }
            McpImportStatus = $"已导入 {parsed.Count} 个服务器。";
            McpImportJson = string.Empty;
            if (_configService != null) await _configService.SaveAsync(Config);
        }
        catch (Exception ex)
        {
            McpImportStatus = $"导入失败：{ex.Message}";
        }
    }

    #endregion

    #region 诊断测试（搜索 / 浏览器 / 音频）

    public bool CanTestWebSearch => !IsTestingWebSearch;
    public bool CanTestBrowserRuntime => !IsTestingBrowserRuntime && !IsInstallingBrowserRuntime;
    public bool CanInstallBrowserRuntime => !IsInstallingBrowserRuntime && !IsTestingBrowserRuntime;
    public bool CanTestBrowserAgent => !IsTestingBrowserAgent;
    public bool CanTestAudioOutput => !IsTestingAudioOutput;

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

    [ObservableProperty]
    private string _audioOutputTestStatus = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestAudioOutputCommand))]
    private bool _isTestingAudioOutput;

    [ObservableProperty]
    private ChatAttachment? _audioTestAttachment;

    [RelayCommand(CanExecute = nameof(CanTestWebSearch))]
    private async Task TestWebSearchAsync()
    {
        if (_webSearchService == null) { WebSearchTestStatus = GetString("Status.ServiceNotInitialized", "Service not initialized"); return; }
        if (!Config.WebSearchEnabled) { WebSearchTestStatus = GetString("Status.EnableWebSearchFirst", "Please enable Web Search first"); return; }
        if (string.IsNullOrWhiteSpace(Config.WebSearchApiKey)) { WebSearchTestStatus = GetString("Status.EnterApiKeyFirst", "Please enter API Key first"); return; }

        IsTestingWebSearch = true;
        WebSearchTestStatus = GetString("Status.TestingConnection", "Testing...");
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
        if (_browserVisionService == null) { BrowserAgentTestStatus = GetString("Status.ServiceNotInitialized", "Service not initialized"); return; }
        if (!Config.BrowserEnabled) { BrowserAgentTestStatus = GetString("Status.EnableBrowserFirst", "Please enable Browser first"); return; }
        if (string.IsNullOrWhiteSpace(Config.BrowserAgentApiKey) && string.IsNullOrWhiteSpace(Config.ApiKey)) { BrowserAgentTestStatus = GetString("Status.EnterApiKeyFirst", "Please enter API Key first"); return; }

        IsTestingBrowserAgent = true;
        BrowserAgentTestStatus = GetString("Status.TestingConnection", "Testing...");
        try
        {
            ConfigTabViewModel.NormalizeBrowserConfig(Config);
            if (_configService != null)
            {
                await _configService.SaveAsync(Config);
            }

            var (success, message) = await _browserVisionService.TestConnectionAsync();
            BrowserAgentTestStatus = message;
        }
        finally { IsTestingBrowserAgent = false; }
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

    private async Task LoadSystemVoicesAsync()
    {
        if (_systemAudioService is not { IsSupported: true }) return;
        try
        {
            var voices = await _systemAudioService.GetInstalledVoicesAsync();
            if (voices.Count == 0) return;
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                AudioVoices.Clear();
                foreach (var v in voices) AudioVoices.Add(v);
            });
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to load system voices for extensions UI");
        }
    }

    #endregion

    #region 浏览器智能体模型列表拉取（可编辑下拉框）

    public ObservableCollection<string> BrowserAgentModelOptions { get; } = new();

    [ObservableProperty]
    private bool _isLoadingBrowserAgentModels;

    [ObservableProperty]
    private string _browserAgentModelsStatus = string.Empty;

    /// <summary>与配置页共用 XAML 结构（CommandParameter="BrowserAgent"），本页仅支持该目标。</summary>
    [RelayCommand]
    private async Task LoadModelsAsync(string? target)
    {
        if (target != "BrowserAgent" || IsLoadingBrowserAgentModels) return;

        if (_modelCatalogService == null)
        {
            BrowserAgentModelsStatus = GetString("Status.ServiceNotInitialized", "Service not initialized");
            return;
        }

        var baseUrl = FirstNonBlank(Config.BrowserAgentBaseUrl, Config.BaseUrl);
        var apiKey = FirstNonBlank(Config.BrowserAgentApiKey, Config.ApiKey);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            BrowserAgentModelsStatus = GetString("Status.EnterApiKeyFirst", "Please enter API Key first");
            return;
        }

        IsLoadingBrowserAgentModels = true;
        BrowserAgentModelsStatus = GetString("Status.LoadingModels", "Loading models...");
        try
        {
            var result = await _modelCatalogService.GetModelsAsync(baseUrl, apiKey);
            if (!result.Success)
            {
                BrowserAgentModelsStatus = string.Format(
                    GetString("Status.LoadModelsFailed", "Failed to load models: {0}"),
                    result.ErrorMessage);
                return;
            }

            BrowserAgentModelOptions.Clear();
            foreach (var model in result.Models)
            {
                BrowserAgentModelOptions.Add(model);
            }

            BrowserAgentModelsStatus = result.Models.Count == 0
                ? GetString("Status.NoModelsReturned", "Endpoint returned no models")
                : string.Format(GetString("Status.ModelsLoaded", "Loaded {0} models"), result.Models.Count);
        }
        catch (OperationCanceledException)
        {
            // 页面切换等外部取消，无需提示。
        }
        catch (Exception ex)
        {
            BrowserAgentModelsStatus = string.Format(
                GetString("Status.LoadModelsFailed", "Failed to load models: {0}"),
                ex.Message);
        }
        finally
        {
            IsLoadingBrowserAgentModels = false;
        }
    }

    private static string? FirstNonBlank(string? primary, string? fallback)
        => string.IsNullOrWhiteSpace(primary) ? fallback : primary;

    #endregion
}
