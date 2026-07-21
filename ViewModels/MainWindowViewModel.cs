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
    private ExtensionsTabViewModel _extensionsTabViewModel;

    [ObservableProperty]
    private SkillsTabViewModel _skillsTabViewModel;

    [ObservableProperty]
    private McpTabViewModel _mcpTabViewModel;

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
        if (value == 6) // HISTORY
        {
            _ = HistoryTabViewModel?.LoadHistoryAsync();
        }
        else if (value == 8) // LOGS
        {
            _ = LogsTabViewModel.RefreshLogsAsync();
        }
    }

    #endregion

    #region Sub-Agents

    /// <summary>子代理编排器（下传给 ChatTabViewModel，供 Sub-Agents 弹出小镇绑定）。</summary>
    public ISubAgentOrchestrator? Orchestrator { get; private set; }

    #endregion

    /// <summary>
    /// 默认构造函数（用于设计时）
    /// </summary>
    public MainWindowViewModel()
    {
        _chatTabViewModel = new ChatTabViewModel();
        _configTabViewModel = new ConfigTabViewModel();
        _extensionsTabViewModel = new ExtensionsTabViewModel();
        _extensionsTabViewModel.Initialize(_configTabViewModel);
        _skillsTabViewModel = new SkillsTabViewModel();
        _skillsTabViewModel.Initialize(_configTabViewModel);
        _mcpTabViewModel = new McpTabViewModel();
        _mcpTabViewModel.Initialize(_configTabViewModel);
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
        IContextCompressionService? contextCompressionService,
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
        ISystemAudioService? systemAudioService,
        IConversationArchiveService? archiveService,
        IImageGenerationSessionService? imageGenerationSessionService,
        IHeadlessBrowserService? browserService = null,
        IBrowserVisionService? browserVisionService = null,
        IDocumentParserService? documentParserService = null,
        IModelCatalogService? modelCatalogService = null,
        IScreenCaptureService? screenCaptureService = null,
        ISubAgentOrchestrator? subAgentOrchestrator = null,
        IKnowledgeBaseMaintenanceService? knowledgeMaintenanceService = null,
        IWorkspaceService? workspaceService = null,
        IConversationSessionAccessor? conversationSessionAccessor = null,
        ISkillCatalogService? skillCatalog = null,
        OpenAiModelRuntimeFactory? modelRuntimeFactory = null,
        IUserInteractionService? userInteractionService = null)
    {
        _localizationService = localizationService;
        Orchestrator = subAgentOrchestrator;

        // Initialize Tab ViewModels
        _chatTabViewModel = new ChatTabViewModel(chatService, configService, contextCompressionService, promptService, taskScheduler, functionRegistry, tokenService, localizationService, attachmentStoreService, systemAudioService, archiveService, imageGenerationSessionService, documentParserService, screenCaptureService, subAgentOrchestrator, workspaceService, conversationSessionAccessor, userInteractionService);
        _configTabViewModel = new ConfigTabViewModel(configService, chatService, embeddingService, localizationService, modelCatalogService, knowledgeMaintenanceService, knowledgeBaseService, browserService, browserVisionService);
        _configTabViewModel.Initialize(_chatTabViewModel, tokenService);
        _extensionsTabViewModel = new ExtensionsTabViewModel(configService, chatService, localizationService, webSearchService, systemAudioService);
        _extensionsTabViewModel.Initialize(_configTabViewModel);
        _skillsTabViewModel = new SkillsTabViewModel(skillCatalog, configService, workspaceService, localizationService, userInteractionService);
        _skillsTabViewModel.Initialize(_configTabViewModel);
        _mcpTabViewModel = new McpTabViewModel(configService, localizationService);
        _mcpTabViewModel.Initialize(_configTabViewModel);
        _tasksTabViewModel = new TasksTabViewModel(taskScheduler, localizationService);
        _knowledgeBaseTabViewModel = new KnowledgeBaseTabViewModel(fileSystemService, platformPathService, knowledgeBaseService, localizationService, userInteractionService);
        _logsTabViewModel = new LogsTabViewModel(logService, localizationService, userInteractionService);
        _aboutTabViewModel = new AboutTabViewModel(localizationService, updateService);

        if (archiveService != null)
        {
            _historyTabViewModel = new HistoryTabViewModel(archiveService, localizationService, workspaceService);
            _historyTabViewModel.LoadHistoryRequested += OnLoadHistoryRequested;
            _historyTabViewModel.HistoryDeleted += OnHistoryDeleted;
        }

        // Wire up events
        _chatTabViewModel.SwitchToTasksTabRequested += (s, e) => SelectedTabIndex = 5;

        if (taskScheduler != null)
        {
            taskScheduler.ProactiveMessageTriggered += OnProactiveMessageTriggered;
        }

        _logger.Information("MainWindowViewModel 初始化完成");

        // 加载工作区列表并恢复上次活跃工作区
        _ = ChatTabViewModel.InitializeWorkspacesAsync();
    }

    private void OnProactiveMessageTriggered(object? sender, ProactiveMessageEventArgs e)
    {
        // 必须在 UI 线程处理，因为涉及切换 Tab 和修改 ObservableCollection
        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            var executionResult = TaskExecutionResult.Failed("Proactive task did not start.");
            _logger.Information("收到主动消息触发事件: {Intent}", e.Intent);

            try
            {
                App.StartTrayFlashing();
                SelectedTabIndex = 0;
                executionResult = await ChatTabViewModel.ProcessProactiveMessageAsync(e.Intent);
            }
            catch (Exception ex)
            {
                executionResult = TaskExecutionResult.Failed(ex.Message);
                _logger.Error(ex, "处理主动消息时发生异常: {TaskId}", e.TaskId);
            }
            finally
            {
                if (sender is ITaskScheduler taskScheduler)
                {
                    await taskScheduler.CompleteTaskExecutionAsync(e.TaskId, executionResult.Outcome, executionResult.Note);
                }
            }
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
