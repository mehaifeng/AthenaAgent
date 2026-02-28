using Athena.UI.Models;
using Athena.UI.Services;
using Athena.UI.Services.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace Athena.UI.ViewModels;

public partial class ConfigTabViewModel : ViewModelBase
{
    private readonly IConfigService? _configService;
    private readonly IChatService? _chatService;
    private readonly IEmbeddingService? _embeddingService;
    private readonly IConversationHistoryService? _historyService;
    private readonly ILogger _logger = Log.ForContext<ConfigTabViewModel>();

    [ObservableProperty]
    private AppConfig _config = new();

    [ObservableProperty]
    private string _connectionStatus = string.Empty;

    [ObservableProperty]
    private bool _isTestingConnection;

    [ObservableProperty]
    private int _contextTokens;

    [ObservableProperty]
    private int _contextTokensThreshold = 4000;

    public string ContextTokensInfo => $"{ContextTokens} / {ContextTokensThreshold} tokens";

    public bool IsNearCompressionThreshold => ContextTokens > ContextTokensThreshold * 0.8;

    [ObservableProperty]
    private string _compressionPreview = string.Empty;

    public ObservableCollection<string> Providers { get; } = new() { "OpenAI", "Azure", "Custom" };
    public ObservableCollection<string> Themes { get; } = new() { "Dark", "Light" };

    public event EventHandler? SaveRequested;
    public event EventHandler? ResetRequested;
    public event EventHandler? CompressContextRequested;
    public event EventHandler? ClearContextRequested;

    public ConfigTabViewModel() : this(null, null, null, null) { }

    public ConfigTabViewModel(IConfigService? configService, IChatService? chatService, IEmbeddingService? embeddingService, IConversationHistoryService? historyService)
    {
        _configService = configService;
        _chatService = chatService;
        _embeddingService = embeddingService;
        _historyService = historyService;
        LoadConfigAsync().ConfigureAwait(false);
    }

    private async Task LoadConfigAsync()
    {
        if (_configService != null)
        {
            Config = await _configService.LoadAsync();
            ContextTokensThreshold = Config.CompressionThreshold;
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

    [RelayCommand]
    public async Task SaveConfigAsync()
    {
        if (_configService != null)
        {
            await _configService.SaveAsync(Config);
            _chatService?.UpdateConfig(Config);
            if (_embeddingService is OpenAIEmbeddingService openAIEmbedding) openAIEmbedding.UpdateConfig(Config);
            if (_historyService is ConversationHistoryService historyService) historyService.UpdateSecondaryConfig(Config);
        }
        _logger.Information("配置已保存");
        SaveRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    public async Task ResetConfigAsync()
    {
        Config = new AppConfig();
        if (_configService != null) await _configService.SaveAsync(Config);
        _logger.Information("配置已重置");
        ResetRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void CompressContext() => CompressContextRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void ClearContext() => ClearContextRequested?.Invoke(this, EventArgs.Empty);

    public void UpdateTokensInfo(int current, int threshold, string preview)
    {
        ContextTokens = current;
        ContextTokensThreshold = threshold;
        CompressionPreview = preview;
        OnPropertyChanged(nameof(ContextTokensInfo));
        OnPropertyChanged(nameof(IsNearCompressionThreshold));
    }
}
