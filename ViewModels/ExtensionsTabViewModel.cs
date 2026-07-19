using Athena.UI.Models;
using Athena.UI.Services;
using Athena.UI.Services.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.ViewModels;

/// <summary>
/// 「神工坊」扩展页 ViewModel：扩展能力（语音/生图/搜索/浏览器/文档解析）。
/// MCP 服务器管理已拆分至 McpTabViewModel。与 ConfigTabViewModel 共享同一个 AppConfig 实例——
/// 配置的加载、监听与防抖自动保存仍由 ConfigTabViewModel（配置属主）负责，
/// 本 VM 只承载扩展相关的 UI 状态、计算属性与命令。
/// </summary>
public partial class ExtensionsTabViewModel : ViewModelBase
{
    private readonly IConfigService? _configService;
    private readonly IChatService? _chatService;
    private readonly ILocalizationService? _localizationService;
    private readonly IWebSearchService? _webSearchService;
    private readonly ISystemAudioService? _systemAudioService;
    // Cancels in-flight system playback so Stop can kill the external process.
    private CancellationTokenSource? _audioTestCts;
    private readonly ILogger _logger = Log.ForContext<ExtensionsTabViewModel>();

    /// <summary>与 ConfigTabViewModel 共享的配置实例（由 Initialize 注入并跟随其替换）。</summary>
    [ObservableProperty]
    private AppConfig _config = new();

    public ExtensionsTabViewModel() : this(null, null, null, null, null) { }

    public ExtensionsTabViewModel(
        IConfigService? configService,
        IChatService? chatService,
        ILocalizationService? localizationService,
        IWebSearchService? webSearchService,
        ISystemAudioService? systemAudioService = null)
    {
        _configService = configService;
        _chatService = chatService;
        _localizationService = localizationService;
        _webSearchService = webSearchService;
        _systemAudioService = systemAudioService;

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
        RefreshAudioVoices();
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
                    RefreshAudioVoices();
                    break;
                case nameof(AppConfig.ChatAudioEnabled):
                    OnPropertyChanged(nameof(CanEditRemoteAudioFields));
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
    public ObservableCollection<string> Providers { get; } = new(ProviderCatalog.ChatProviders);

    public ObservableCollection<string> WebSearchProviders { get; } = new(ProviderCatalog.WebSearchProviders);

    public bool IsBaiduProvider => Config.WebSearchProvider == "Baidu";

    public ObservableCollection<string> AudioProviders { get; } = new(AudioConfigResolver.ProviderNames);

    // 音色下拉建议：provider=System 时为本机已安装语音（Windows SAPI / macOS say / Linux espeak-ng），
    // 远端 provider 时为 OpenAI 官方预置音色（服务端没有"列出音色"API，兼容服务商可手输自定义音色名）。
    public ObservableCollection<string> AudioVoices { get; } = new();

    private static readonly string[] OpenAiPresetVoices =
    {
        "alloy", "ash", "ballad", "coral", "echo", "fable", "onyx", "nova", "sage", "shimmer", "verse"
    };

    private System.Collections.Generic.IReadOnlyList<string> _systemVoices = Array.Empty<string>();

    private void RefreshAudioVoices()
    {
        AudioVoices.Clear();
        var source = IsRemoteAudioProvider ? OpenAiPresetVoices : _systemVoices;
        foreach (var v in source) AudioVoices.Add(v);
    }

    public ObservableCollection<DocumentParserMode> DocumentParserModes { get; } = new()
    {
        DocumentParserMode.AgentLightweight,
        DocumentParserMode.Precision
    };

    // 仅精度解析方式需要 Token；极速解析无需登录。
    public bool CanEditParserToken => Config.DocumentParserEnabled && Config.DocumentParserMode == DocumentParserMode.Precision;

    public bool IsRemoteAudioProvider => Config.ChatAudioProvider != "System";
    public bool CanEditRemoteAudioFields => Config.ChatAudioEnabled && IsRemoteAudioProvider;

    #endregion

    #region 诊断测试（搜索 / 浏览器 / 音频）

    public bool CanTestWebSearch => !IsTestingWebSearch;
    public bool CanTestAudioOutput => !IsTestingAudioOutput;

    [ObservableProperty]
    private string _webSearchTestStatus = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestWebSearchCommand))]
    private bool _isTestingWebSearch;

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

        if (_systemAudioService?.IsSupported != true)
        {
            AudioOutputTestStatus = GetString("Chat.Audio.PlaybackUnavailable", "Audio playback is unavailable on this device.");
            return;
        }

        // Fire-and-forget so the command returns immediately and the Stop
        // button's CanExecute stays true during playback.
        _ = RunSystemAudioTestPlaybackAsync(AudioTestAttachment);
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

        if (AudioTestAttachment != null)
        {
            AudioTestAttachment.IsPlaying = false;
        }
    }

    private async Task LoadSystemVoicesAsync()
    {
        // 远端 provider 的预置音色先就位，系统语音异步补充。
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(RefreshAudioVoices);

        if (_systemAudioService is not { IsSupported: true }) return;
        try
        {
            var voices = await _systemAudioService.GetInstalledVoicesAsync();
            if (voices.Count == 0) return;
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                _systemVoices = voices;
                RefreshAudioVoices();
            });
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to load system voices for extensions UI");
        }
    }

    #endregion

}
