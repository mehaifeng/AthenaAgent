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
    private readonly ILogger _logger = Log.ForContext<ConfigTabViewModel>();

    [ObservableProperty]
    private AppConfig _config = new();

    [ObservableProperty]
    private string _connectionStatus = string.Empty;

    [ObservableProperty]
    private bool _isTestingConnection;

    [ObservableProperty]
    private bool _isCompressing;

    [ObservableProperty]
    private bool _isSaving;

    [ObservableProperty]
    private bool _isResetting;

    [ObservableProperty]
    private ChatTabViewModel? _chatTabViewModel;

    [ObservableProperty]
    private ITokenService? _tokenService;

    [ObservableProperty]
    private int _contextTokensThreshold = 64000;

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

    public ObservableCollection<string> Languages { get; }

    public ObservableCollection<string> WebSearchProviders { get; } = new() { "Tavily", "Zhipu", "Baidu" };

    private static readonly Dictionary<string, string> WebSearchUrls = new()
    {
        { "Tavily", "https://api.tavily.com" },
        { "Zhipu", "https://open.bigmodel.cn/api/paas/v4" },
        { "Baidu", "https://qianfan.baidubce.com/v2/app/conversation/runs" }
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

    public event EventHandler? SaveRequested;
    public event EventHandler? ResetRequested;

    public ConfigTabViewModel() : this(null, null, null, null, null, null) { }

    public ConfigTabViewModel(IConfigService? configService, IChatService? chatService, IEmbeddingService? embeddingService, IConversationHistoryService? historyService, ILocalizationService? localizationService, IWebSearchService? webSearchService)
    {
        _configService = configService;
        _chatService = chatService;
        _embeddingService = embeddingService;
        _historyService = historyService;
        _localizationService = localizationService;
        _webSearchService = webSearchService;

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

    partial void OnConfigChanged(AppConfig value) => SetupConfigListener(value);

    /// <summary>
    /// 外部（如 LLM 工具调用）修改配置后，在 UI 线程上刷新 ConfigTab 显示
    /// </summary>
    private void OnExternalConfigChanged(object? sender, AppConfig newConfig)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
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
        };
    }

    private async Task LoadConfigAsync()
    {
        if (_configService != null)
        {
            Config = await _configService.LoadAsync();
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

    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        if (_chatService == null) { ConnectionStatus = "服务未初始化"; return; }
        if (string.IsNullOrWhiteSpace(Config.ApiKey)) { ConnectionStatus = "请先输入 API Key"; return; }
        IsTestingConnection = true;
        ConnectionStatus = "测试中...";
        try
        {
            _chatService.UpdateConfig(Config);
            var (success, message) = await _chatService.TestConnectionAsync();
            ConnectionStatus = message.TrimEnd().Replace("\n", " ");
        }
        finally { IsTestingConnection = false; }
    }

    [ObservableProperty]
    private string _webSearchTestStatus = string.Empty;

    [ObservableProperty]
    private bool _isTestingWebSearch;

    [RelayCommand]
    private async Task TestWebSearchAsync()
    {
        if (_webSearchService == null) { WebSearchTestStatus = "服务未初始化"; return; }
        if (!Config.WebSearchEnabled) { WebSearchTestStatus = "请先启用 Web Search"; return; }
        if (string.IsNullOrWhiteSpace(Config.WebSearchApiKey)) { WebSearchTestStatus = "请先输入 API Key"; return; }

        IsTestingWebSearch = true;
        WebSearchTestStatus = "测试中...";
        try
        {
            // 刷新配置
            if (_webSearchService is WebSearchService ws) ws.RefreshConfig();
            var (success, message) = await _webSearchService.TestConnectionAsync();
            WebSearchTestStatus = message;
        }
        finally { IsTestingWebSearch = false; }
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
    public async Task SaveConfigAsync()
    {
        if (IsSaving) return;
        IsSaving = true;
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

                await _configService.SaveAsync(Config);
                App.SetTheme(Config.Theme);
                _chatService?.UpdateConfig(Config);
                if (_embeddingService is OpenAIEmbeddingService openAIEmbedding) openAIEmbedding.UpdateConfig(Config);
                if (_historyService is ConversationHistoryService historyService) historyService.UpdateSecondaryConfig(Config);
                
                if (ChatTabViewModel != null)
                {
                    await ChatTabViewModel.RefreshSettingsAsync();
                }
                
                if (TokenService != null) TokenService.MaxTokens = Config.MaxContextTokens;
            }
            _logger.Information("配置已保存");
            SaveRequested?.Invoke(this, EventArgs.Empty);
            await Task.Delay(500); // Give user some visual feedback
        }
        finally
        {
            IsSaving = false;
        }
    }

    [RelayCommand]
    public async Task ResetConfigAsync()
    {
        if (IsResetting) return;
        
        var result = await MessageBox.ShowAsync(
            message: _localizationService?.GetString("Dialog.ConfirmResetConfig") ?? "Are you sure you want to reset all settings to default? This cannot be undone.",
            title: _localizationService?.GetString("Dialog.Title.Warning") ?? "Warning",
            button: MessageBoxButton.YesNo,
            icon: MessageBoxIcon.Warning);

        if (result == MessageBoxResult.Yes)
        {
            IsResetting = true;
            try
            {
                Config = new AppConfig();
                if (_configService != null) await _configService.SaveAsync(Config);
                if (TokenService != null) TokenService.MaxTokens = Config.MaxContextTokens;
                _logger.Information("配置已重置");
                ResetRequested?.Invoke(this, EventArgs.Empty);
                await Task.Delay(500);
            }
            finally
            {
                IsResetting = false;
            }
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
}
