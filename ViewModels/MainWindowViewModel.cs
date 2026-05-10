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
    private KnowledgeBaseTabViewModel _knowledgeBaseTabViewModel;

    [ObservableProperty]
    private LogsTabViewModel _logsTabViewModel;

    [ObservableProperty]
    private AboutTabViewModel _aboutTabViewModel;

    #endregion

    #region Tab Navigation Properties

    [ObservableProperty]
    private int _selectedTabIndex;

    partial void OnSelectedTabIndexChanged(int value)
    {
        if (value == 3) // HISTORY
        {
            _ = HistoryTabViewModel?.LoadHistoryAsync();
        }
        else if (value == 5) // LOGS
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
        _knowledgeBaseTabViewModel = new KnowledgeBaseTabViewModel();
        _logsTabViewModel = new LogsTabViewModel();
        _aboutTabViewModel = new AboutTabViewModel(_localizationService, null);
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
        ILocalizationService? localizationService,
        IFileSystemService? fileSystemService,
        IPlatformPathService? platformPathService,
        IFunctionRegistry? functionRegistry,
        ITokenService? tokenService,
        IWebSearchService? webSearchService,
        IUpdateService? updateService,
        IAttachmentStoreService? attachmentStoreService,
        IHeadlessBrowserService? browserService = null,
        IBrowserVisionService? browserVisionService = null)
    {
        _localizationService = localizationService;

        // Initialize Tab ViewModels
        _chatTabViewModel = new ChatTabViewModel(chatService, configService, historyService, promptService, taskScheduler, functionRegistry, tokenService, localizationService, attachmentStoreService);
        _configTabViewModel = new ConfigTabViewModel(configService, chatService, embeddingService, historyService, localizationService, webSearchService, browserService, browserVisionService);
        _configTabViewModel.Initialize(_chatTabViewModel, tokenService);
        _tasksTabViewModel = new TasksTabViewModel(taskScheduler, localizationService);
        _knowledgeBaseTabViewModel = new KnowledgeBaseTabViewModel(fileSystemService, platformPathService, knowledgeBaseService, localizationService);
        _logsTabViewModel = new LogsTabViewModel(logService);
        _aboutTabViewModel = new AboutTabViewModel(localizationService, updateService);

        if (historyService != null)
        {
            _historyTabViewModel = new HistoryTabViewModel(historyService);
            _historyTabViewModel.LoadHistoryRequested += OnLoadHistoryRequested;
            _historyTabViewModel.HistoryDeleted += OnHistoryDeleted;
        }

        // Wire up events
        _chatTabViewModel.SwitchToTasksTabRequested += (s, e) => SelectedTabIndex = 2;

        if (taskScheduler != null)
        {
            taskScheduler.ProactiveMessageTriggered += OnProactiveMessageTriggered;
        }

        _logger.Information("MainWindowViewModel 初始化完成");
    }

    private void OnProactiveMessageTriggered(object? sender, ProactiveMessageEventArgs e)
    {
        // 必须在 UI 线程处理，因为涉及切换 Tab 和修改 ObservableCollection
        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            _logger.Information("收到主动消息触发事件: {Intent}", e.Intent);
            
            // 1. 托盘闪烁提醒
            App.StartTrayFlashing();

            // 2. 切换到聊天 Tab
            SelectedTabIndex = 0;

            // 3. 执行消息注入和 AI 响应逻辑
            await ChatTabViewModel.ProcessProactiveMessageAsync(e.Intent);
        });
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

    public void PersistSessionState()
    {
        ChatTabViewModel.PersistDraft();
    }

    #region Global Commands (Proxy to Tab ViewModels if needed)

    #endregion
}
