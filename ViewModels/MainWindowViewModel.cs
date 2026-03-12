using Athena.UI.Models;
using Athena.UI.Services;
using Athena.UI.Services.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using System;
using System.Threading.Tasks;

namespace Athena.UI.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly ILogger _logger = Log.ForContext<MainWindowViewModel>();
    private readonly ILocalizationService? _localizationService;

    #region Tab ViewModels

    [ObservableProperty]
    private ChatTabViewModel _chatTabViewModel;

    [ObservableProperty]
    private ConfigTabViewModel _configTabViewModel;

    [ObservableProperty]
    private TasksTabViewModel _tasksTabViewModel;

    [ObservableProperty]
    private HistoryTabViewModel? _historyTabViewModel;

    [ObservableProperty]
    private FilesTabViewModel _filesTabViewModel;

    [ObservableProperty]
    private LogsTabViewModel _logsTabViewModel;

    [ObservableProperty]
    private AboutTabViewModel _aboutTabViewModel;

    #endregion

    #region Tab Navigation Properties

    [ObservableProperty]
    private int _selectedTabIndex;

    [ObservableProperty]
    private bool _isShowConfigSaveReset;

    partial void OnSelectedTabIndexChanged(int value)
    {
        // CONFIG tab is at index 1
        IsShowConfigSaveReset = value == 1;

        // When switching away from Chat, maybe save?
        // Or when switching to Logs, refresh?
        if (value == 5) // LOGS
        {
            _ = LogsTabViewModel.RefreshLogsAsync();
        }
    }

    #endregion

    /// <summary>
    /// 默认构造函数（用于设计时）
    /// </summary>
    public MainWindowViewModel()
    {
        _chatTabViewModel = new ChatTabViewModel();
        _configTabViewModel = new ConfigTabViewModel();
        _tasksTabViewModel = new TasksTabViewModel();
        _filesTabViewModel = new FilesTabViewModel();
        _logsTabViewModel = new LogsTabViewModel();
        _aboutTabViewModel = new AboutTabViewModel();
    }

    /// <summary>
    /// 依赖注入构造函数
    /// </summary>
    public MainWindowViewModel(
        IChatService? chatService,
        IConfigService? configService,
        ITaskScheduler? taskScheduler,
        IConversationHistoryService? historyService,
        IPromptService? promptService,
        ILogService? logService,
        IKnowledgeBaseService? knowledgeBaseService,
        IEmbeddingService? embeddingService,
        ILocalizationService? localizationService)
    {
        _localizationService = localizationService;

        // Initialize Tab ViewModels
        _chatTabViewModel = new ChatTabViewModel(chatService, configService, historyService, promptService, taskScheduler);
        _configTabViewModel = new ConfigTabViewModel(configService, chatService, embeddingService, historyService, localizationService);
        _tasksTabViewModel = new TasksTabViewModel(taskScheduler);
        _filesTabViewModel = new FilesTabViewModel(knowledgeBaseService);
        _logsTabViewModel = new LogsTabViewModel(logService);
        _aboutTabViewModel = new AboutTabViewModel();

        if (historyService != null)
        {
            _historyTabViewModel = new HistoryTabViewModel(historyService);
            _historyTabViewModel.LoadHistoryRequested += OnLoadHistoryRequested;
            _historyTabViewModel.HistoryDeleted += OnHistoryDeleted;
        }

        // Wire up events
        _chatTabViewModel.SwitchToTasksTabRequested += (s, e) => SelectedTabIndex = 2;
        _chatTabViewModel.TokensInfoChanged += (s, e) => 
        {
            _configTabViewModel.UpdateTokensInfo(e.Current, e.Preview);
        };

        // 拉取初始状态，确保 ConfigTabView 加载时数据不为 0
        _configTabViewModel.UpdateTokensInfo(_chatTabViewModel.ContextTokens, _chatTabViewModel.CompressionPreview);

        _configTabViewModel.SaveRequested += async (s, e) => 
        {
            await _chatTabViewModel.RefreshSettingsAsync();
            SelectedTabIndex = 0;
        };

        _configTabViewModel.CompressContextRequested += async (s, e) => 
        {
            await _chatTabViewModel.InternalCompressContextAsync();
            SelectedTabIndex = 0;
        };

        _configTabViewModel.UndoCompressionRequested += (s, e) => 
        {
            _chatTabViewModel.InternalUndoCompression();
            SelectedTabIndex = 0;
        };
        _configTabViewModel.ResetRequested += (s, e) => { /* Handle reset if needed */ };

        _logger.Information("MainWindowViewModel 初始化完成");
    }

    private void OnHistoryDeleted(object? sender, string id)
    {
        ChatTabViewModel.NotifyHistoryDeleted(id);
    }

    private async void OnLoadHistoryRequested(object? sender, ConversationHistoryItem item)
    {
        _logger.Information("请求加载历史对话: {Id}", item.Id);
        await ChatTabViewModel.LoadHistoryConversationAsync(item);
        SelectedTabIndex = 0; // Switch to CHAT
    }

    #region Global Commands (Proxy to Tab ViewModels if needed)

    [RelayCommand]
    private async Task SaveConfigAsync() => await ConfigTabViewModel.SaveConfigAsync();

    [RelayCommand]
    private async Task ResetConfigAsync() => await ConfigTabViewModel.ResetConfigAsync();

    #endregion
}
