using Athena.UI.Models;
using Athena.UI.Services;
using Athena.UI.Services.Interfaces;
using Athena.UI.Views;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace Athena.UI.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly ILogger _logger = Log.ForContext<MainWindowViewModel>();
    private readonly ILocalizationService? _localizationService;
    private readonly ChatSessionFactory? _chatSessionFactory;
    private readonly IConversationArchiveService? _conversationArchiveService;
    private readonly IConversationArchiveStore? _conversationStore;
    private readonly IWorkspaceService? _workspaceService;
    private readonly IUserInteractionService? _userInteractionService;
    private readonly IConfigService? _configService;
    private readonly ApprovalQueueViewModel? _approvalQueue;
    private readonly DispatcherTimer? _compactLogTimer;

    public WorkspaceWorkbenchViewModel? Workbench { get; }

    public AppSettingsViewModel? AppSettings { get; }

    public ExtensionsConfigurationViewModel? ExtensionSettings { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsExtensionConnectorSection))]
    private int _selectedConnectorSection;

    public bool IsExtensionConnectorSection => SelectedConnectorSection >= 3;

    public bool IsSidePanelsSwapped => AppSettings?.Config.MainLayout.SidePanelsSwapped == true;

    [ObservableProperty]
    private bool _compactLogsPaused;

    public ObservableCollection<LogEntryViewModel> CompactLogEntries { get; } = new();

    [ObservableProperty]
    private string _selectedLogScope = "全部";

    [ObservableProperty]
    private int _globalErrorCount;

    partial void OnSelectedLogScopeChanged(string value) => RebuildCompactLogs();

    public ObservableCollection<WorkspaceConversationGroupViewModel> ConversationGroups { get; } = new();

    [ObservableProperty]
    private ConversationSessionItemViewModel? _selectedConversation;

    [ObservableProperty]
    private string _conversationSearchText = string.Empty;

    partial void OnConversationSearchTextChanged(string value)
    {
        var query = value.Trim();
        foreach (var group in ConversationGroups)
        {
            var groupMatches = string.IsNullOrEmpty(query)
                               || group.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                               || group.DirectoryPath.Contains(query, StringComparison.OrdinalIgnoreCase);
            foreach (var session in group.Conversations)
            {
                session.IsSearchMatch = groupMatches
                                        || session.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                                        || session.Chat.Messages.Any(message => message.Content.Contains(query, StringComparison.OrdinalIgnoreCase));
            }
            group.IsSearchMatch = groupMatches || group.Conversations.Any(session => session.IsSearchMatch);
            if (!string.IsNullOrEmpty(query) && group.IsSearchMatch) group.IsExpanded = true;
        }
    }

    [ObservableProperty]
    private bool _isConversationTreeLoading = true;

    public WorkspaceConversationGroupViewModel? GlobalConversationGroup => ConversationGroups.FirstOrDefault(group => group.Workspace == null);

    partial void OnSelectedConversationChanged(ConversationSessionItemViewModel? oldValue, ConversationSessionItemViewModel? newValue)
    {
        if (oldValue != null) oldValue.IsSelected = false;
        if (newValue == null) return;
        if (oldValue != null
            && !ReferenceEquals(oldValue, newValue)
            && oldValue.Chat.Messages.Count == 0
            && string.IsNullOrWhiteSpace(oldValue.Chat.InputText))
        {
            var oldGroup = ConversationGroups.FirstOrDefault(group => group.Conversations.Contains(oldValue));
            oldGroup?.Conversations.Remove(oldValue);
            oldValue.Dispose();
            if (_conversationStore != null) _ = _conversationStore.DeleteAsync(oldValue.HistoryId);
        }
        newValue.IsSelected = true;
        ChatTabViewModel = newValue.Chat;
        _workspaceService?.SetActiveWorkspace(newValue.Workspace);
        if (Workbench != null) _ = Workbench.SetWorkspaceAsync(newValue.Workspace);
        RebuildCompactLogs();
    }

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
        IModelCatalogService? modelCatalogService = null,
        IScreenCaptureService? screenCaptureService = null,
        ISubAgentOrchestrator? subAgentOrchestrator = null,
        IKnowledgeBaseMaintenanceService? knowledgeMaintenanceService = null,
        IWorkspaceService? workspaceService = null,
        IConversationSessionAccessor? conversationSessionAccessor = null,
        ISkillCatalogService? skillCatalog = null,
        OpenAiModelRuntimeFactory? modelRuntimeFactory = null,
        IUserInteractionService? userInteractionService = null,
        ConversationExecutionCoordinator? executionCoordinator = null,
        ChatSessionFactory? chatSessionFactory = null,
        IConversationArchiveStore? conversationStore = null,
        WorkspaceWorkbenchViewModel? workbench = null,
        AppSettingsViewModel? appSettings = null,
        ExtensionsConfigurationViewModel? extensionSettings = null,
        ApprovalQueueViewModel? approvalQueue = null)
    {
        _localizationService = localizationService;
        _chatSessionFactory = chatSessionFactory;
        _conversationArchiveService = archiveService;
        _conversationStore = conversationStore;
        _workspaceService = workspaceService;
        _userInteractionService = userInteractionService;
        _configService = configService;
        Workbench = workbench;
        AppSettings = appSettings;
        ExtensionSettings = extensionSettings;
        _approvalQueue = approvalQueue;
        if (_approvalQueue != null) _approvalQueue.Pending.CollectionChanged += (_, _) => RefreshApprovalStates();
        Orchestrator = subAgentOrchestrator;

        // Initialize Tab ViewModels
        _chatTabViewModel = new ChatTabViewModel(chatService, configService, contextCompressionService, promptService, taskScheduler, functionRegistry, tokenService, localizationService, attachmentStoreService, systemAudioService, archiveService, imageGenerationSessionService, screenCaptureService, subAgentOrchestrator, workspaceService, conversationSessionAccessor, userInteractionService, executionCoordinator);
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
            _chatTabViewModel.CurrentConversationDeleted += OnCurrentConversationDeleted;
        }

        // Wire up events
        _chatTabViewModel.SwitchToTasksTabRequested += (s, e) => SelectedTabIndex = 5;

        if (taskScheduler != null)
        {
            taskScheduler.ProactiveMessageTriggered += OnProactiveMessageTriggered;
        }

        _logger.Information("MainWindowViewModel 初始化完成");

        _ = InitializeConversationTreeAsync();
        _ = RefreshCompactLogsAsync();
        if (logService != null)
        {
            _compactLogTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _compactLogTimer.Tick += async (_, _) => await RefreshCompactLogsAsync();
            _compactLogTimer.Start();
        }
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

    private async void OnCurrentConversationDeleted(object? sender, string id)
    {
        if (HistoryTabViewModel != null)
        {
            await HistoryTabViewModel.LoadHistoryAsync();
        }
    }

    private async void OnLoadHistoryRequested(object? sender, ConversationHistoryItem item)
    {
        _logger.Information("请求加载历史对话: {Id}", item.Id);
        await ChatTabViewModel.LoadHistoryConversationAsync(item);
        SelectedTabIndex = 0; // Switch to CHAT
    }

    public void PersistSessionState()
    {
        var saves = ConversationGroups
            .SelectMany(group => group.Conversations)
            .Select(session => session.PersistNowAsync());
        Task.WhenAll(saves).GetAwaiter().GetResult();
    }

    private async Task InitializeConversationTreeAsync()
    {
        IsConversationTreeLoading = true;
        try
        {
            ConversationGroups.Clear();
            var globalGroup = new WorkspaceConversationGroupViewModel(null);
            ConversationGroups.Add(globalGroup);

            var workspaces = _workspaceService == null ? [] : await _workspaceService.LoadAllAsync();
            foreach (var workspace in workspaces)
            {
                ConversationGroups.Add(new WorkspaceConversationGroupViewModel(workspace));
            }

            var histories = _conversationArchiveService == null ? [] : await _conversationArchiveService.LoadAllAsync();
            foreach (var history in histories.OrderByDescending(item => item.IsPinned).ThenByDescending(item => item.UpdatedAt))
            {
                var group = ConversationGroups.FirstOrDefault(candidate => candidate.Workspace?.Id == history.WorkspaceId) ?? globalGroup;
                var chat = _chatSessionFactory?.Create() ?? ChatTabViewModel;
                await chat.InitializeWorkspacesAsync();
                await chat.LoadHistoryConversationAsync(history);
                chat.AssignWorkspace(group.Workspace);
                chat.InputText = history.Draft;
                var session = new ConversationSessionItemViewModel(chat, group.Workspace, _conversationStore, history.Id)
                {
                    Title = string.IsNullOrWhiteSpace(history.Summary) ? "新对话" : history.Summary,
                    IsPinned = history.IsPinned,
                    UpdatedAt = history.UpdatedAt,
                    WasInterrupted = string.Equals(history.RuntimeStatus, "interrupted", StringComparison.OrdinalIgnoreCase)
                };
                WireSession(session);
                group.Conversations.Add(session);
            }

            var initial = ConversationGroups.SelectMany(group => group.Conversations).OrderByDescending(session => session.UpdatedAt).FirstOrDefault();
            if (initial == null)
            {
                initial = await CreateConversationCoreAsync(globalGroup);
            }
            SelectedConversation = initial;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "初始化多会话树失败");
            if (ConversationGroups.Count == 0) ConversationGroups.Add(new WorkspaceConversationGroupViewModel(null));
            SelectedConversation = await CreateConversationCoreAsync(ConversationGroups[0]);
        }
        finally
        {
            IsConversationTreeLoading = false;
        }
    }

    [RelayCommand]
    private async Task CreateConversationAsync(WorkspaceConversationGroupViewModel? group)
    {
        group ??= GlobalConversationGroup;
        if (group == null) return;
        SelectedConversation = await CreateConversationCoreAsync(group);
    }

    private async Task<ConversationSessionItemViewModel> CreateConversationCoreAsync(WorkspaceConversationGroupViewModel group)
    {
        var chat = _chatSessionFactory?.Create() ?? ChatTabViewModel;
        await chat.InitializeWorkspacesAsync();
        chat.AssignWorkspace(group.Workspace);
        var session = new ConversationSessionItemViewModel(chat, group.Workspace, _conversationStore);
        WireSession(session);
        group.Conversations.Insert(0, session);
        return session;
    }

    [RelayCommand]
    private void SelectConversation(ConversationSessionItemViewModel? session)
    {
        if (session != null) SelectedConversation = session;
    }

    [RelayCommand]
    private async Task AddWorkspaceAsync()
    {
        if (_workspaceService == null || _userInteractionService == null) return;
        var path = await _userInteractionService.PickFolderAsync("选择工作区目录");
        if (string.IsNullOrWhiteSpace(path)) return;
        var existing = await _workspaceService.FindByDirectoryAsync(path);
        var group = existing == null ? null : ConversationGroups.FirstOrDefault(candidate => candidate.Workspace?.Id == existing.Id);
        if (group == null)
        {
            var workspace = existing ?? new WorkspaceProfile
            {
                Name = System.IO.Path.GetFileName(path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)),
                DirectoryPath = path
            };
            if (existing == null) await _workspaceService.SaveAsync(workspace);
            group = new WorkspaceConversationGroupViewModel(workspace);
            ConversationGroups.Add(group);
        }
        SelectedConversation = await CreateConversationCoreAsync(group);
    }

    [RelayCommand]
    private async Task DeleteConversationAsync(ConversationSessionItemViewModel? session)
    {
        if (session == null) return;
        if (session.IsRunning) session.Chat.StopResponseCommand.Execute(null);
        var group = ConversationGroups.FirstOrDefault(candidate => candidate.Conversations.Contains(session));
        group?.Conversations.Remove(session);
        if (_conversationStore != null) await _conversationStore.DeleteAsync(session.HistoryId);
        session.Dispose();
        if (ReferenceEquals(SelectedConversation, session))
        {
            SelectedConversation = ConversationGroups.SelectMany(candidate => candidate.Conversations).OrderByDescending(candidate => candidate.UpdatedAt).FirstOrDefault()
                ?? await CreateConversationCoreAsync(GlobalConversationGroup ?? ConversationGroups[0]);
        }
    }

    private static Window? MainOwner =>
        (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    [RelayCommand]
    private async Task OpenProviderModelsAsync()
    {
        var owner = MainOwner;
        var viewModel = App.Services?.GetService(typeof(ProviderModelsViewModel)) as ProviderModelsViewModel;
        if (owner == null || viewModel == null) return;
        await new ProviderModelsWindow { DataContext = viewModel }.ShowDialog(owner);
    }

    [RelayCommand]
    private async Task OpenKnowledgeBaseAsync()
    {
        if (MainOwner is { } owner) await new KnowledgeBaseWindow(KnowledgeBaseTabViewModel).ShowDialog(owner);
    }

    [RelayCommand]
    private async Task OpenScheduledMessagesAsync()
    {
        if (MainOwner is { } owner) await new ScheduledMessagesWindow(TasksTabViewModel).ShowDialog(owner);
    }

    [RelayCommand]
    private async Task OpenSkillsConnectorsAsync()
    {
        if (MainOwner is { } owner) await new SkillsConnectorsWindow { DataContext = this }.ShowDialog(owner);
    }

    [RelayCommand]
    private async Task OpenAppSettingsAsync()
    {
        if (MainOwner is { } owner) await new AppSettingsWindow { DataContext = this }.ShowDialog(owner);
    }

    [RelayCommand]
    private async Task OpenDetailedLogsAsync()
    {
        await LogsTabViewModel.RefreshLogsAsync();
        if (MainOwner is { } owner) await new DetailedLogsWindow(LogsTabViewModel).ShowDialog(owner);
    }

    [RelayCommand]
    private async Task ToggleSidePanelsAsync()
    {
        if (AppSettings == null || _configService == null) return;
        AppSettings.Config.MainLayout.SidePanelsSwapped = !AppSettings.Config.MainLayout.SidePanelsSwapped;
        OnPropertyChanged(nameof(IsSidePanelsSwapped));
        await _configService.SaveAsync(AppSettings.Config);
    }

    [RelayCommand]
    private async Task RefreshCompactLogsAsync()
    {
        if (CompactLogsPaused) return;
        await LogsTabViewModel.RefreshLogsAsync();
        RebuildCompactLogs();
    }

    private void RebuildCompactLogs()
    {
        GlobalErrorCount = LogsTabViewModel.LogEntries.Count(entry => entry.Level is "ERROR" or "FATAL");
        var workspaceId = SelectedConversation?.Workspace?.Id;
        var conversationId = SelectedConversation?.ConversationId;
        var filtered = LogsTabViewModel.LogEntries.Where(entry => SelectedLogScope switch
        {
            "当前工作区" => !string.IsNullOrWhiteSpace(workspaceId) && entry.Properties?.Contains(workspaceId, StringComparison.Ordinal) == true,
            "当前对话" => !string.IsNullOrWhiteSpace(conversationId) && entry.Properties?.Contains(conversationId, StringComparison.Ordinal) == true,
            _ => true
        }).Take(200);
        CompactLogEntries.Clear();
        foreach (var entry in filtered) CompactLogEntries.Add(entry);
    }

    [RelayCommand]
    private async Task ForkConversationAsync(ConversationSessionItemViewModel? source)
    {
        if (source == null || _conversationStore == null) return;
        await source.PersistNowAsync();
        var original = await _conversationStore.LoadByIdAsync(source.HistoryId);
        if (original == null) return;
        var fork = new ConversationHistoryItem
        {
            Id = Guid.NewGuid().ToString(),
            ConversationId = Guid.NewGuid().ToString("N"),
            Summary = source.Title + " · 分支",
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now,
            MessageCount = original.MessageCount,
            Messages = ConversationPersistenceHelper.CloneMessages(original.Messages),
            WorkspaceId = original.WorkspaceId,
            ForkedFromConversationId = original.ConversationId,
            ForkedFromHistoryId = original.Id
        };
        await _conversationStore.SaveAsync(fork);
        var group = ConversationGroups.First(candidate => candidate.Conversations.Contains(source));
        var chat = _chatSessionFactory?.Create() ?? ChatTabViewModel;
        await chat.InitializeWorkspacesAsync();
        await chat.LoadHistoryConversationAsync(fork);
        chat.AssignWorkspace(group.Workspace);
        var session = new ConversationSessionItemViewModel(chat, group.Workspace, _conversationStore, fork.Id)
        {
            Title = fork.Summary,
            UpdatedAt = fork.UpdatedAt
        };
        WireSession(session);
        group.Conversations.Insert(0, session);
        SelectedConversation = session;
    }

    private void WireSession(ConversationSessionItemViewModel session)
    {
        session.DeleteRequested += async (_, _) => await DeleteConversationAsync(session);
        session.ForkRequested += async (_, _) => await ForkConversationAsync(session);
        RefreshApprovalStates();
    }

    private void RefreshApprovalStates()
    {
        foreach (var session in ConversationGroups.SelectMany(group => group.Conversations))
        {
            session.IsWaitingForApproval = _approvalQueue?.Pending.Any(item => item.ConversationId == session.ConversationId) == true;
        }
    }

    #region Global Commands (Proxy to Tab ViewModels if needed)

    #endregion
}
