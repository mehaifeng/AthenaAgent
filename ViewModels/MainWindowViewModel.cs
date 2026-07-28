using Athena.UI.Models;
using Athena.UI.Services;
using Athena.UI.Services.Interfaces;
using Athena.UI.Views;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Athena.UI.ViewModels;

public partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private readonly ILogger _logger = Log.ForContext<MainWindowViewModel>();
    private readonly ILocalizationService? _localizationService;
    private readonly ChatSessionFactory? _chatSessionFactory;
    private readonly IConversationArchiveService? _conversationArchiveService;
    private readonly IConversationArchiveStore? _conversationStore;
    private readonly IWorkspaceService? _workspaceService;
    private readonly IUserInteractionService? _userInteractionService;
    private readonly IConfigService? _configService;
    private readonly ITaskScheduler? _taskScheduler;
    private readonly ApprovalQueueViewModel? _approvalQueue;
    private readonly DispatcherTimer? _compactLogTimer;
    private readonly Func<SkillsConnectorsWindowViewModel>? _skillsConnectorsFactory;
    private readonly Func<AppSettingsWindowViewModel>? _appSettingsFactory;
    private bool _disposed;

    public WorkspaceWorkbenchViewModel? Workbench { get; }

    public AppSettingsViewModel? AppSettings { get; }


    public bool IsSidePanelsSwapped => AppSettings?.Config.MainLayout.SidePanelsSwapped == true;

    public ObservableCollection<LogEntryViewModel> CompactLogEntries { get; } = new();

    [ObservableProperty]
    private string _selectedLogScope = "全部";

    [ObservableProperty]
    private int _globalErrorCount;

    partial void OnSelectedLogScopeChanged(string value) => RebuildCompactLogs();

    public ObservableCollection<WorkspaceConversationGroupViewModel> ConversationGroups { get; } = new();

    public ObservableCollection<ConversationSessionItemViewModel> PinnedConversations { get; } = new();

    public bool HasPinnedConversations => PinnedConversations.Count > 0;

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
        var previousConversation = MainConversationViewModel;
        if (oldValue != null) oldValue.IsSelected = false;
        if (newValue == null) return;
        if (oldValue != null
            && !ReferenceEquals(oldValue, newValue)
            && oldValue.Chat.Messages.Count == 0
            && string.IsNullOrWhiteSpace(oldValue.Chat.InputText))
        {
            var oldGroup = ConversationGroups.FirstOrDefault(group => group.Conversations.Contains(oldValue));
            oldGroup?.Conversations.Remove(oldValue);
            PinnedConversations.Remove(oldValue);
            OnPropertyChanged(nameof(HasPinnedConversations));
            oldValue.Dispose();
            if (_conversationStore != null) _ = _conversationStore.DeleteAsync(oldValue.HistoryId);
        }
        newValue.IsSelected = true;
        MainConversationViewModel = newValue.Chat;
        if (!ReferenceEquals(previousConversation, newValue.Chat)
            && ConversationGroups.SelectMany(group => group.Conversations)
                .All(session => !ReferenceEquals(session.Chat, previousConversation)))
        {
            previousConversation.Dispose();
        }
        _workspaceService?.SetActiveWorkspace(newValue.Workspace);
        if (Workbench != null) _ = Workbench.SetWorkspaceAsync(newValue.Workspace);
        RebuildCompactLogs();
    }

    #region Feature ViewModels

    [ObservableProperty]
    private MainConversationViewModel _mainConversationViewModel;

    [ObservableProperty]
    private TasksViewModel _tasksViewModel;

    [ObservableProperty]
    private KnowledgeBaseViewModel _knowledgeBaseViewModel;

    [ObservableProperty]
    private LogsViewModel _logsViewModel;

    #endregion

    #region Sub-Agents

    /// <summary>子代理编排器（下传给 MainConversationViewModel，供 Sub-Agents 弹出小镇绑定）。</summary>
    public ISubAgentOrchestrator? Orchestrator { get; private set; }

    #endregion

    /// <summary>
    /// 默认构造函数（用于设计时）
    /// </summary>
    public MainWindowViewModel()
    {
        _mainConversationViewModel = new MainConversationViewModel();
        _tasksViewModel = new TasksViewModel();
        _knowledgeBaseViewModel = new KnowledgeBaseViewModel();
        _logsViewModel = new LogsViewModel();
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
        ILocalizationService? localizationService,
        IFileSystemService? fileSystemService,
        IPlatformPathService? platformPathService,
        IFunctionRegistry? functionRegistry,
        ITokenService? tokenService,
        IAttachmentStoreService? attachmentStoreService,
        ISystemAudioService? systemAudioService,
        IConversationArchiveService? archiveService,
        IImageGenerationSessionService? imageGenerationSessionService,
        IScreenCaptureService? screenCaptureService = null,
        ISubAgentOrchestrator? subAgentOrchestrator = null,
        IKnowledgeBaseMaintenanceService? knowledgeMaintenanceService = null,
        IWorkspaceService? workspaceService = null,
        IConversationSessionAccessor? conversationSessionAccessor = null,
        IUserInteractionService? userInteractionService = null,
        ConversationExecutionCoordinator? executionCoordinator = null,
        ChatSessionFactory? chatSessionFactory = null,
        IConversationArchiveStore? conversationStore = null,
        WorkspaceWorkbenchViewModel? workbench = null,
        AppSettingsViewModel? appSettings = null,
        ApprovalQueueViewModel? approvalQueue = null,
        AppConfigurationSession? configurationSession = null,
        Func<SkillsConnectorsWindowViewModel>? skillsConnectorsFactory = null,
        Func<AppSettingsWindowViewModel>? appSettingsFactory = null)
    {
        _localizationService = localizationService;
        _chatSessionFactory = chatSessionFactory;
        _conversationArchiveService = archiveService;
        _conversationStore = conversationStore;
        _workspaceService = workspaceService;
        _userInteractionService = userInteractionService;
        _configService = configService;
        _taskScheduler = taskScheduler;
        Workbench = workbench;
        AppSettings = appSettings;
        _approvalQueue = approvalQueue;
        _skillsConnectorsFactory = skillsConnectorsFactory;
        _appSettingsFactory = appSettingsFactory;
        if (_approvalQueue != null) _approvalQueue.Pending.CollectionChanged += OnApprovalQueueChanged;
        Orchestrator = subAgentOrchestrator;

        // Initialize the live feature view models.
        _mainConversationViewModel = new MainConversationViewModel(chatService, configService, contextCompressionService, promptService, taskScheduler, functionRegistry, tokenService, localizationService, attachmentStoreService, systemAudioService, archiveService, imageGenerationSessionService, screenCaptureService, subAgentOrchestrator, workspaceService, conversationSessionAccessor, userInteractionService, executionCoordinator);
        _tasksViewModel = new TasksViewModel(taskScheduler, localizationService);
        _knowledgeBaseViewModel = new KnowledgeBaseViewModel(
            fileSystemService,
            platformPathService,
            knowledgeBaseService,
            localizationService,
            userInteractionService,
            knowledgeMaintenanceService,
            configurationSession);
        _logsViewModel = new LogsViewModel(logService, localizationService, userInteractionService);

        if (archiveService != null)
        {
            archiveService.ArchiveStaged += OnArchiveStaged;
            archiveService.ArchiveCompleted += OnArchiveCompleted;
            archiveService.ArchiveFailed += OnArchiveFailed;
        }

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
            _compactLogTimer.Tick += OnCompactLogTimerTick;
            _compactLogTimer.Start();
        }
    }

    private void OnApprovalQueueChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshApprovalStates();

    private async void OnCompactLogTimerTick(object? sender, EventArgs e) => await RefreshCompactLogsAsync();

    private void OnProactiveMessageTriggered(object? sender, ProactiveMessageEventArgs e)
    {
        // 必须在 UI 线程处理，因为会更新当前会话与 ObservableCollection。
        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            var executionResult = TaskExecutionResult.Failed("Proactive task did not start.");
            _logger.Information("收到主动消息触发事件: {Intent}", e.Intent);

            try
            {
                App.StartTrayFlashing();
                executionResult = await MainConversationViewModel.ProcessProactiveMessageAsync(e.Intent);
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

    private void OnArchiveStaged(object? sender, ConversationArchiveResultEventArgs e)
    {
        RunOnUiThread(() =>
        {
            FindConversation(e.Snapshot)?.SetArchivePending(
                _localizationService?.GetString("History.PendingStatus", "正在总结中") ?? "正在总结中");
        });
    }

    private void OnArchiveCompleted(object? sender, ConversationArchiveResultEventArgs e)
    {
        RunOnUiThread(() => _ = HandleArchiveCompletedAsync(e));
    }

    private async Task HandleArchiveCompletedAsync(ConversationArchiveResultEventArgs e)
    {
        var historyId = e.HistoryItem?.Id ?? e.Snapshot.HistoryId;
        try
        {
            var history = e.HistoryItem
                          ?? (!string.IsNullOrWhiteSpace(e.Snapshot.HistoryId) && _conversationArchiveService != null
                              ? await _conversationArchiveService.LoadByIdAsync(e.Snapshot.HistoryId)
                              : null);
            if (history == null) return;
            await UpsertConversationTreeItemAsync(history, e.Snapshot);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "会话树同步归档完成事件失败: {HistoryId}", historyId);
        }
    }

    private void OnArchiveFailed(object? sender, ConversationArchiveResultEventArgs e)
    {
        RunOnUiThread(() =>
        {
            FindConversation(e.Snapshot)?.SetArchiveFailed(
                _localizationService?.GetString("Chat.Archive.RetryLater", "归档失败，已保留并等待重试")
                ?? "归档失败，已保留并等待重试");
        });
    }

    private static void RunOnUiThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }

    private ConversationSessionItemViewModel? FindConversation(ConversationArchiveSnapshot snapshot) =>
        ConversationGroups
            .SelectMany(group => group.Conversations)
            .FirstOrDefault(session =>
                (!string.IsNullOrWhiteSpace(snapshot.HistoryId)
                 && string.Equals(session.HistoryId, snapshot.HistoryId, StringComparison.Ordinal))
                || (!string.IsNullOrWhiteSpace(snapshot.ConversationId)
                    && string.Equals(session.ConversationId, snapshot.ConversationId, StringComparison.Ordinal)));

    private async Task UpsertConversationTreeItemAsync(
        ConversationHistoryItem history,
        ConversationArchiveSnapshot? snapshot = null)
    {
        var session = ConversationGroups
            .SelectMany(group => group.Conversations)
            .FirstOrDefault(candidate =>
                string.Equals(candidate.HistoryId, history.Id, StringComparison.Ordinal)
                || (!string.IsNullOrWhiteSpace(history.ConversationId)
                    && string.Equals(candidate.ConversationId, history.ConversationId, StringComparison.Ordinal)))
            ?? (snapshot == null ? null : FindConversation(snapshot));

        if (session != null)
        {
            session.Title = string.IsNullOrWhiteSpace(history.Summary) ? session.Title : history.Summary;
            session.UpdatedAt = history.UpdatedAt;
            session.IsPinned = history.IsPinned;
            session.WasInterrupted = string.Equals(history.RuntimeStatus, "interrupted", StringComparison.OrdinalIgnoreCase);
            session.SetArchiveCompleted();
            RefreshPinnedConversations();
            return;
        }

        var group = ConversationGroups.FirstOrDefault(candidate => candidate.Workspace?.Id == history.WorkspaceId)
                    ?? GlobalConversationGroup;
        if (group == null) return;

        _logger.Information("会话树正在插入外部归档: {HistoryId}", history.Id);
        var chat = CreateChatSession();
        await chat.InitializeWorkspacesAsync();
        chat.RestorePersistedConversation(history);
        chat.AssignWorkspace(group.Workspace);
        chat.InputText = history.Draft;
        session = new ConversationSessionItemViewModel(chat, group.Workspace, _conversationStore, history.Id)
        {
            Title = string.IsNullOrWhiteSpace(history.Summary) ? "新对话" : history.Summary,
            IsPinned = history.IsPinned,
            UpdatedAt = history.UpdatedAt,
            WasInterrupted = string.Equals(history.RuntimeStatus, "interrupted", StringComparison.OrdinalIgnoreCase),
            ForkedFromConversationId = history.ForkedFromConversationId,
            ForkedFromHistoryId = history.ForkedFromHistoryId
        };
        session.SetArchiveCompleted();
        WireSession(session);
        group.Conversations.Insert(0, session);
        RefreshPinnedConversations();
        OnConversationSearchTextChanged(ConversationSearchText);
        _logger.Information("会话树已插入外部归档: {HistoryId}", history.Id);
    }

    private MainConversationViewModel CreateChatSession() =>
        _chatSessionFactory?.Create()
        ?? new MainConversationViewModel(
            null,
            null,
            null,
            null,
            null,
            null,
            new TokenService(),
            _localizationService,
            archiveService: _conversationArchiveService,
            workspaceService: _workspaceService,
            userInteractionService: _userInteractionService);

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
            WireGroup(globalGroup);
            ConversationGroups.Add(globalGroup);

            var workspaces = _workspaceService == null ? [] : await _workspaceService.LoadAllAsync();
            foreach (var workspace in workspaces)
            {
                var group = new WorkspaceConversationGroupViewModel(workspace);
                WireGroup(group);
                ConversationGroups.Add(group);
            }

            var histories = _conversationArchiveService == null ? [] : await _conversationArchiveService.LoadAllAsync();
            foreach (var history in histories.OrderByDescending(item => item.IsPinned).ThenByDescending(item => item.UpdatedAt))
            {
                var group = ConversationGroups.FirstOrDefault(candidate => candidate.Workspace?.Id == history.WorkspaceId) ?? globalGroup;
                var chat = CreateChatSession();
                await chat.InitializeWorkspacesAsync();
                chat.RestorePersistedConversation(history);
                chat.AssignWorkspace(group.Workspace);
                chat.InputText = history.Draft;
                var session = new ConversationSessionItemViewModel(chat, group.Workspace, _conversationStore, history.Id)
                {
                    Title = string.IsNullOrWhiteSpace(history.Summary) ? "新对话" : history.Summary,
                    IsPinned = history.IsPinned,
                    UpdatedAt = history.UpdatedAt,
                    WasInterrupted = string.Equals(history.RuntimeStatus, "interrupted", StringComparison.OrdinalIgnoreCase),
                    ForkedFromConversationId = history.ForkedFromConversationId,
                    ForkedFromHistoryId = history.ForkedFromHistoryId
                };
                WireSession(session);
                group.Conversations.Add(session);
            }
            RefreshPinnedConversations();

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
            if (ConversationGroups.Count == 0)
            {
                var globalGroup = new WorkspaceConversationGroupViewModel(null);
                WireGroup(globalGroup);
                ConversationGroups.Add(globalGroup);
            }
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
        var chat = CreateChatSession();
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
            WireGroup(group);
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
        RefreshPinnedConversations();
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
        if (MainOwner is { } owner) await new KnowledgeBaseWindow(KnowledgeBaseViewModel).ShowDialog(owner);
    }

    [RelayCommand]
    private async Task OpenTasksAsync()
    {
        if (MainOwner is { } owner) await new TasksWindow(TasksViewModel).ShowDialog(owner);
    }

    [RelayCommand]
    private async Task OpenSkillsConnectorsAsync()
    {
        if (MainOwner is { } owner && _skillsConnectorsFactory?.Invoke() is { } viewModel)
            await new SkillsConnectorsWindow { DataContext = viewModel }.ShowDialog(owner);
    }

    [RelayCommand]
    private async Task OpenAppSettingsAsync()
    {
        if (MainOwner is { } owner && _appSettingsFactory?.Invoke() is { } viewModel)
            await new AppSettingsWindow { DataContext = viewModel }.ShowDialog(owner);
    }

    [RelayCommand]
    private async Task OpenDetailedLogsAsync()
    {
        await LogsViewModel.RefreshLogsAsync();
        if (MainOwner is { } owner) await new DetailedLogsWindow(LogsViewModel).ShowDialog(owner);
    }

    [RelayCommand]
    private async Task ToggleSidePanelsAsync()
    {
        if (AppSettings == null || _configService == null) return;
        AppSettings.Config.MainLayout.SidePanelsSwapped = !AppSettings.Config.MainLayout.SidePanelsSwapped;
        OnPropertyChanged(nameof(IsSidePanelsSwapped));
        await _configService.SaveAsync(AppSettings.Config);
    }

    private async Task RefreshCompactLogsAsync()
    {
        await LogsViewModel.RefreshLogsAsync();
        RebuildCompactLogs();
    }

    private void RebuildCompactLogs()
    {
        GlobalErrorCount = LogsViewModel.LogEntries.Count(entry => entry.Level is "ERROR" or "FATAL");
        var workspaceId = SelectedConversation?.Workspace?.Id;
        var conversationId = SelectedConversation?.ConversationId;
        var filtered = LogsViewModel.LogEntries.Where(entry => SelectedLogScope switch
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
            MessageCount = 0,
            Messages = [],
            WorkspaceId = original.WorkspaceId,
            ForkedFromConversationId = original.ConversationId,
            ForkedFromHistoryId = original.Id
        };
        await _conversationStore.SaveAsync(fork);
        var group = ConversationGroups.First(candidate => candidate.Conversations.Contains(source));
        var chat = CreateChatSession();
        await chat.InitializeWorkspacesAsync();
        chat.RestorePersistedConversation(fork);
        chat.AssignWorkspace(group.Workspace);
        var session = new ConversationSessionItemViewModel(chat, group.Workspace, _conversationStore, fork.Id)
        {
            Title = fork.Summary,
            UpdatedAt = fork.UpdatedAt,
            ForkedFromConversationId = fork.ForkedFromConversationId,
            ForkedFromHistoryId = fork.ForkedFromHistoryId
        };
        WireSession(session);
        var sourceIndex = group.Conversations.IndexOf(source);
        group.Conversations.Insert(sourceIndex < 0 ? 0 : sourceIndex + 1, session);
        SelectedConversation = session;
    }

    private void WireSession(ConversationSessionItemViewModel session)
    {
        session.DeleteRequested += async (_, _) => await DeleteConversationAsync(session);
        session.ForkRequested += async (_, _) => await ForkConversationAsync(session);
        session.ExportRequested += async (_, _) => await ExportConversationAsync(session);
        session.PinChanged += (_, _) => RefreshPinnedConversations();
        RefreshApprovalStates();
    }

    private void WireGroup(WorkspaceConversationGroupViewModel group)
    {
        group.RenameCommitted += async (_, _) =>
        {
            if (group.Workspace != null && _workspaceService != null)
            {
                await _workspaceService.SaveAsync(group.Workspace);
            }
        };
        group.RevealRequested += (_, _) => RevealWorkspace(group);
        group.CopyPathRequested += async (_, _) => await CopyWorkspacePathAsync(group);
        group.DeleteRequested += async (_, _) => await DeleteWorkspaceAsync(group);
    }

    private void RefreshPinnedConversations()
    {
        var pinned = ConversationGroups
            .SelectMany(group => group.Conversations)
            .Where(session => session.IsPinned)
            .OrderByDescending(session => session.UpdatedAt)
            .ToList();
        PinnedConversations.Clear();
        foreach (var session in pinned) PinnedConversations.Add(session);
        OnPropertyChanged(nameof(HasPinnedConversations));
    }

    private async Task CopyWorkspacePathAsync(WorkspaceConversationGroupViewModel group)
    {
        if (!group.IsWorkspace || string.IsNullOrWhiteSpace(group.DirectoryPath)) return;
        var clipboard = MainOwner == null ? null : TopLevel.GetTopLevel(MainOwner)?.Clipboard;
        if (clipboard != null) await clipboard.SetTextAsync(group.DirectoryPath);
    }

    private void RevealWorkspace(WorkspaceConversationGroupViewModel group)
    {
        if (!group.IsWorkspace || !Directory.Exists(group.DirectoryPath)) return;
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("explorer.exe", $"\"{group.DirectoryPath}\"") { UseShellExecute = true }
            : OperatingSystem.IsMacOS()
                ? new ProcessStartInfo("open", $"\"{group.DirectoryPath}\"") { UseShellExecute = true }
                : new ProcessStartInfo("xdg-open", $"\"{group.DirectoryPath}\"") { UseShellExecute = true };
        Process.Start(start);
    }

    private async Task DeleteWorkspaceAsync(WorkspaceConversationGroupViewModel group)
    {
        if (group.Workspace == null || _workspaceService == null) return;
        if (_userInteractionService == null || !await _userInteractionService.ConfirmAsync(
                "删除工作区",
                $"确定要删除“{group.Name}”吗？工作区知识文件将被删除，会话历史仍会保留。",
                "删除",
                "取消")) return;
        if (!await _workspaceService.DeleteAsync(group.Workspace.Id)) return;

        foreach (var session in group.Conversations)
        {
            if (session.IsRunning) session.Chat.StopResponseCommand.Execute(null);
            PinnedConversations.Remove(session);
        }

        if (SelectedConversation != null && group.Conversations.Contains(SelectedConversation))
        {
            SelectedConversation = ConversationGroups
                .Where(candidate => !ReferenceEquals(candidate, group))
                .SelectMany(candidate => candidate.Conversations)
                .OrderByDescending(session => session.UpdatedAt)
                .FirstOrDefault()
                ?? await CreateConversationCoreAsync(GlobalConversationGroup ?? ConversationGroups[0]);
        }

        ConversationGroups.Remove(group);
        foreach (var session in group.Conversations) session.Dispose();
        RefreshPinnedConversations();
    }

    private async Task ExportConversationAsync(ConversationSessionItemViewModel session)
    {
        if (_userInteractionService == null) return;
        var safeTitle = string.Concat(session.Title.Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        var target = await _userInteractionService.PickSaveFileAsync(
            "导出对话",
            $"{safeTitle}.md",
            "Markdown",
            ["*.md"]);
        if (string.IsNullOrWhiteSpace(target)) return;

        var markdown = new StringBuilder()
            .Append("# ").AppendLine(session.Title)
            .AppendLine()
            .Append("> 导出时间：").AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
            .AppendLine();
        foreach (var message in session.Chat.Messages.Where(message => message.IsVisibleToUser))
        {
            var role = message.Role switch
            {
                "user" => "用户",
                "assistant" => "Athena",
                "system" => "系统",
                _ => message.Role
            };
            markdown.Append("## ").Append(role).Append(" · ")
                .AppendLine(message.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"))
                .AppendLine()
                .AppendLine(message.Content)
                .AppendLine();
            foreach (var attachment in message.Attachments)
            {
                markdown.Append("- 附件：").Append(attachment.DisplayName)
                    .Append("（").Append(attachment.DisplayKind).AppendLine("）");
            }
            if (message.Attachments.Count > 0) markdown.AppendLine();
        }
        await File.WriteAllTextAsync(target, markdown.ToString());
    }

    private void RefreshApprovalStates()
    {
        foreach (var session in ConversationGroups.SelectMany(group => group.Conversations))
        {
            session.IsWaitingForApproval = _approvalQueue?.Pending.Any(item => item.ConversationId == session.ConversationId) == true;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_approvalQueue != null)
            _approvalQueue.Pending.CollectionChanged -= OnApprovalQueueChanged;
        if (_conversationArchiveService != null)
        {
            _conversationArchiveService.ArchiveStaged -= OnArchiveStaged;
            _conversationArchiveService.ArchiveCompleted -= OnArchiveCompleted;
            _conversationArchiveService.ArchiveFailed -= OnArchiveFailed;
        }
        if (_taskScheduler != null)
            _taskScheduler.ProactiveMessageTriggered -= OnProactiveMessageTriggered;
        if (_compactLogTimer != null)
        {
            _compactLogTimer.Stop();
            _compactLogTimer.Tick -= OnCompactLogTimerTick;
        }

        foreach (var session in ConversationGroups
                     .SelectMany(group => group.Conversations)
                     .Distinct())
        {
            session.Dispose();
        }
        ConversationGroups.Clear();
        PinnedConversations.Clear();
    }
}
