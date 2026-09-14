using Athena.UI.Models;
using Athena.UI.Services;
using Athena.UI.Services.Cron;
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
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Athena.UI.ViewModels;

public partial class MainWindowViewModel : ViewModelBase, IDisposable, ICronSessionHost, IConversationNavigationTarget
{
    private readonly ILogger _logger = Log.ForContext<MainWindowViewModel>();
    private readonly ILocalizationService? _localizationService;
    private readonly ISystemNotificationService? _notifications;
    private readonly IAppForegroundProbe? _foregroundProbe;
    private readonly ChatSessionFactory? _chatSessionFactory;
    private readonly IConversationArchiveService? _conversationArchiveService;
    private readonly IConversationArchiveStore? _conversationStore;
    private readonly IWorkspaceService? _workspaceService;
    private readonly IContextPolicyProvider? _contextPolicyProvider;
    private readonly IUserInteractionService? _userInteractionService;
    private readonly AppConfigurationSession? _configurationSession;
    private readonly IPlatformPathService? _platformPathService;
    private readonly ICronSessionLauncher? _cronSessionLauncher;
    private readonly IConversationNavigator? _conversationNavigator;
    private readonly CronExecutionWorker? _cronExecutionWorker;
    private readonly ApprovalQueueViewModel? _approvalQueue;
    private readonly ILogService? _logService;
    private readonly Func<SkillsConnectorsWindowViewModel>? _skillsConnectorsFactory;
    private readonly Func<AppSettingsWindowViewModel>? _appSettingsFactory;
    private readonly IConversationTitleGenerator? _titleGenerator;
    private bool _disposed;

    public WorkspaceWorkbenchViewModel? Workbench { get; }

    public AppConfig? Config => _configurationSession?.Current;

    public bool IsSidePanelsSwapped => Config?.MainLayout.SidePanelsSwapped == true;

    /// <summary>用户配置的"面板透明度"分率（0 = 完全不透明，0.8 = 80% 透明）。</summary>
    public double PanelTransparency => Config?.MainLayout.PanelTransparency ?? 0.0;

    /// <summary>XAML 直接消费的 Border.Opacity 值：透明度 0 对应不透明（Opacity=1），透明度 0.8 对应 20% 不透明（Opacity=0.2）。</summary>
    public double ShellPanelOpacity => 1.0 - PanelTransparency;

    /// <summary>
    /// 面板是否使用毛玻璃材质。这里只暴露用户意图，材质本身（tint 上限、描边、投影、模糊底图）
    /// 由 MainWindow.ApplyShellPanelMaterial 渲染——ViewModel 不持有任何画笔或效果。
    /// </summary>
    public bool PanelGlassEnabled => Config?.MainLayout.PanelGlassEnabled == true;

    private MainLayoutSettings? _trackedMainLayout;

    private void TrackMainLayout(MainLayoutSettings? layout)
    {
        if (ReferenceEquals(_trackedMainLayout, layout)) return;
        if (_trackedMainLayout != null)
            _trackedMainLayout.PropertyChanged -= OnMainLayoutPropertyChanged;
        _trackedMainLayout = layout;
        if (_trackedMainLayout != null)
            _trackedMainLayout.PropertyChanged += OnMainLayoutPropertyChanged;
    }

    private void OnMainLayoutPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 任意子属性变化都重抛顶层派生属性，避免遗漏其他将来新增的 MainLayout 字段。
        if (e.PropertyName == nameof(MainLayoutSettings.PanelTransparency))
        {
            OnPropertyChanged(nameof(PanelTransparency));
            OnPropertyChanged(nameof(ShellPanelOpacity));
        }
        else if (e.PropertyName == nameof(MainLayoutSettings.PanelGlassEnabled))
        {
            OnPropertyChanged(nameof(PanelGlassEnabled));
        }
    }

    public ObservableCollection<LogEntryViewModel> CompactLogEntries { get; } = new();

    [ObservableProperty]
    private int _globalErrorCount;

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
        // 重的那一半（气泡树重排）不在这一拍做，见 BeginConversationSurfaceSwap。
        BeginConversationSurfaceSwap(newValue);
        if (!ReferenceEquals(previousConversation, newValue.Chat)
            && ConversationGroups.SelectMany(group => group.Conversations)
                .All(session => !ReferenceEquals(session.Chat, previousConversation)))
        {
            previousConversation.Dispose();
        }
        _workspaceService?.SetActiveWorkspace(newValue.Workspace);
        if (Workbench != null) _ = Workbench.SetWorkspaceAsync(newValue.Workspace);
        TerminalPanelViewModel.ActivateScope(
            newValue.Workspace?.Id,
            newValue.Workspace?.DirectoryPath);
        if (SelectedUtilityTabIndex == 1)
            _ = TerminalPanelViewModel.EnsureTerminalAsync();

        // 切走旧会话时，静默在后台生成/更新其标题（无 UI 提示，仅日志），
        // 完成后立即回写到会话树。空会话已在上方被移除，删除中的会话不在树中，均不会触发。
        if (oldValue != null
            && !ReferenceEquals(oldValue, newValue)
            && ConversationGroups.Any(group => group.Conversations.Contains(oldValue)))
        {
            _ = GenerateSilentTitleAsync(oldValue);
        }
    }

    /// <summary>
    /// 会话切换遮罩：中间对话面板在切换期间盖一层与面板同色的幕布。
    ///
    /// 它买的不是速度——切换根本没有 I/O，所有会话在启动时就已经 restore 进各自的 VM 了，
    /// 那 2~3 秒全是 MessagesItemsControl 一次性实体化整段气泡树的布局开销（消息列表不虚拟化，
    /// 见 CLAUDE.md「Bubble Rendering Budget」）。遮罩买的是「把假死换成可解释的等待」：
    /// 左侧列表这一拍就跟手切过去了，中间那块明确显示它正在排布。
    /// </summary>
    [ObservableProperty]
    private bool _isConversationSwitching;

    /// <summary>
    /// 中间对话面板当前正在显示的会话 VM，比 <see cref="MainConversationViewModel"/> 晚一帧落地。
    ///
    /// 刻意分成两个属性而不是把 MainConversationViewModel 本身延后：标题栏主题按钮、
    /// 桌宠这些轻绑定没有理由陪着等一帧，而「选中的会话」这个语义也不该因为一个渲染技巧变得不同步。
    /// 只有真正贵的那个绑定（MainWindow.axaml 中间列）消费这个属性。
    /// </summary>
    [ObservableProperty]
    private MainConversationViewModel _displayedConversation;

    /// <summary>
    /// 切换代次。连切时只有最后一次真正重排气泡树——按住方向键连过 5 个会话，
    /// 改动前要付 5 次全量布局，现在只付落点那一次。
    /// </summary>
    private int _conversationSurfaceGeneration;

    /// <summary>会话行选中动效跑完之前不换绑，见 <see cref="ShellMaterial.RowSelectionSettle"/>。</summary>
    private DispatcherTimer? _surfaceSwapTimer;

    /// <summary>
    /// 会话切换的第一拍：升起幕布、让出时间给选中动效，**不换绑**。
    ///
    /// **绝不能把换绑写在这一拍里。** 同一个 dispatcher turn 内赋值再换绑，UI 线程会直接进入
    /// measure/arrange，幕布那一帧一个像素都画不出来，屏幕上照样冻着旧会话，等 2~3 秒后幕布
    /// 和新内容同帧出现再立刻消失——白做。这和 OnUtilityTabSelectionChanged 上那条注释是同一个坑，
    /// 方向相反：那里是多排了一个 turn 而闪帧，这里是少排了一个 turn 而根本不显示。
    ///
    /// 让出的这一段是 RowSelectionSettle 而不是「一个 dispatcher turn」：Transition 由 UI 线程的
    /// 动画时钟驱动，紧接着冻住 2~3 秒会把选中行的生长动效钉死在第一帧，读起来就是
    /// 「选中要等对话加载完」。先把选中态跑完，再去付布局的账。
    ///
    /// 计时器在连切时被重置，所以在列表里连着走不会触发任何一次气泡树重排——手停下来才付一次。
    /// </summary>
    private void BeginConversationSurfaceSwap(ConversationSessionItemViewModel target)
    {
        if (ReferenceEquals(DisplayedConversation, target.Chat)) return;
        _conversationSurfaceGeneration++;
        IsConversationSwitching = true;
        _surfaceSwapTimer ??= new DispatcherTimer(
            ShellMaterial.RowSelectionSettle,
            DispatcherPriority.Background,
            OnConversationSurfaceSwapDue);
        _surfaceSwapTimer.Stop();
        _surfaceSwapTimer.Start();
    }

    /// <summary>
    /// 第二拍：真正换绑。UI 线程在这里冻住整段气泡树的首次布局，屏幕上停的是已经提交出去的幕布帧。
    /// </summary>
    private void OnConversationSurfaceSwapDue(object? sender, EventArgs e)
    {
        _surfaceSwapTimer?.Stop();
        var generation = _conversationSurfaceGeneration;
        if (SelectedConversation is { } target) DisplayedConversation = target.Chat;
        // ContextIdle(3) 比 ScrollToBottom 用的 Loaded/Background 还低，
        // 保证落幕排在「布局 → 滚到底」之后，幕布不会比内容先掀开。
        Dispatcher.UIThread.Post(
            () => EndConversationSurfaceSwap(generation),
            DispatcherPriority.ContextIdle);
    }

    /// <summary>
    /// 第三拍：落幕。幕布卡住不消失是典型的静默失败，所以第二拍的每一条路径都通向这里。
    /// </summary>
    private void EndConversationSurfaceSwap(int generation)
    {
        if (generation != _conversationSurfaceGeneration) return;
        IsConversationSwitching = false;
    }

    /// <summary>
    /// 静默后台生成会话标题：不显示任何生成中/失败提示，仅记录日志；
    /// 标题生成后立即更新到树中对应会话，并触发一次持久化（revision 提升后写入，保证落库）。
    /// </summary>
    private async Task GenerateSilentTitleAsync(ConversationSessionItemViewModel session)
    {
        if (_titleGenerator == null || !session.ShouldGenerateTitleSilently) return;
        try
        {
            List<ChatMessage> messages;
            if (Dispatcher.UIThread.CheckAccess())
            {
                messages = CaptureTitleMessages(session);
            }
            else
            {
                messages = await Dispatcher.UIThread.InvokeAsync(() => CaptureTitleMessages(session));
            }
            if (messages.Count == 0) return;

            var title = await _titleGenerator.GenerateAsync(messages, useAi: true);
            if (string.IsNullOrWhiteSpace(title)) return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                // 生成期间会话可能已被删除：已不在树中则放弃写入（含持久化复活）。
                if (!ConversationGroups.Any(group => group.Conversations.Contains(session))) return;
                // 无论标题是否变化，本次生成的结果都代表当前内容，必须先标记——
                // 否则生成结果与当前标题相同时（模型回退到首条消息等）每次切换都会重复生成。
                session.MarkSilentTitleGenerated();
                if (string.Equals(title, session.Title, StringComparison.Ordinal)) return;
                session.Title = title;
                // 提升 revision 并触发直接持久化，让 AI 标题落库（旧 revision 会被存储层拒绝）。
                session.Chat.MarkPersistenceMetadataChanged();
            });
            _logger.Information("Silent background title generated for conversation {ConversationId}: {Title}",
                session.ConversationId, title);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Silent background title generation failed for conversation {ConversationId}",
                session.ConversationId);
        }
    }

    private static List<ChatMessage> CaptureTitleMessages(ConversationSessionItemViewModel session)
        => session.Chat.Messages
            .Where(ConversationPersistenceHelper.ShouldPersistMessage)
            .Select(ConversationPersistenceHelper.CloneMessage)
            .ToList();

    private void OnAllTerminalsClosed(object? sender, EventArgs e) =>
        SelectedUtilityTabIndex = 0;

    #region Feature ViewModels

    [ObservableProperty]
    private MainConversationViewModel _mainConversationViewModel;

    [ObservableProperty]
    private TasksViewModel _tasksViewModel;

    [ObservableProperty]
    private KnowledgeBaseViewModel _knowledgeBaseViewModel;

    [ObservableProperty]
    private LogsViewModel _logsViewModel;

    public TerminalPanelViewModel TerminalPanelViewModel { get; }

    [ObservableProperty]
    private int _selectedUtilityTabIndex;

    partial void OnSelectedUtilityTabIndexChanged(int value)
    {
        if (value == 1)
            _ = TerminalPanelViewModel.EnsureTerminalAsync();
    }

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
        _displayedConversation = _mainConversationViewModel;
        _tasksViewModel = new TasksViewModel();
        _knowledgeBaseViewModel = new KnowledgeBaseViewModel();
        _logsViewModel = new LogsViewModel();
        TerminalPanelViewModel = new TerminalPanelViewModel();
    }

    /// <summary>
    /// 依赖注入构造函数
    /// </summary>
    public MainWindowViewModel(
        IChatService? chatService,
        IConfigService? configService,
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
        ApprovalQueueViewModel? approvalQueue = null,
        AppConfigurationSession? configurationSession = null,
        Func<SkillsConnectorsWindowViewModel>? skillsConnectorsFactory = null,
        Func<AppSettingsWindowViewModel>? appSettingsFactory = null,
        TerminalPanelViewModel? terminalPanelViewModel = null,
        IContextPolicyProvider? contextPolicyProvider = null,
        IConversationTitleGenerator? titleGenerator = null,
        ICronTaskService? cronTaskService = null,
        ICronScheduleService? cronScheduleService = null,
        CronExecutionWorker? cronExecutionWorker = null,
        ICronSessionLauncher? cronSessionLauncher = null,
        IConversationNavigator? conversationNavigator = null,
        ISystemNotificationService? notifications = null,
        IAppForegroundProbe? foregroundProbe = null)
    {
        _notifications = notifications;
        _foregroundProbe = foregroundProbe;
        _titleGenerator = titleGenerator;
        _localizationService = localizationService;
        _chatSessionFactory = chatSessionFactory;
        _conversationArchiveService = archiveService;
        _conversationStore = conversationStore;
        _workspaceService = workspaceService;
        _contextPolicyProvider = contextPolicyProvider;
        _userInteractionService = userInteractionService;
        _platformPathService = platformPathService;
        _cronExecutionWorker = cronExecutionWorker;
        _cronSessionLauncher = cronSessionLauncher;
        _conversationNavigator = conversationNavigator;
        Workbench = workbench;
        _configurationSession = configurationSession;
        _approvalQueue = approvalQueue;
        _logService = logService;
        _skillsConnectorsFactory = skillsConnectorsFactory;
        _appSettingsFactory = appSettingsFactory;
        if (_configurationSession != null)
            _configurationSession.CurrentChanged += OnCurrentConfigChanged;
        // 跟踪初始配置里的 MainLayout，让面板透明度滑块第一次拖动就能即时反映到主窗口。
        TrackMainLayout(_configurationSession?.Current?.MainLayout);
        if (_approvalQueue != null) _approvalQueue.Pending.CollectionChanged += OnApprovalQueueChanged;
        Orchestrator = subAgentOrchestrator;

        // Initialize the live feature view models.
        // Production fallback/initial sessions must receive the same transactional compression
        // pipeline as sessions loaded into the tree. The factory is the composition root for that
        // per-session state; design-time and isolated tests may still use the lightweight fallback.
        _mainConversationViewModel = chatSessionFactory?.Create()
            ?? new MainConversationViewModel(chatService, configService, contextCompressionService, promptService, functionRegistry, tokenService, localizationService, attachmentStoreService, systemAudioService, archiveService, imageGenerationSessionService, screenCaptureService, subAgentOrchestrator, workspaceService, conversationSessionAccessor, userInteractionService, executionCoordinator, contextPolicyProvider);
        // 中间面板的初始内容与 MainConversationViewModel 同源；此后由 BeginConversationSurfaceSwap 晚一帧跟进。
        _displayedConversation = _mainConversationViewModel;
        _tasksViewModel = new TasksViewModel(
            cronTaskService,
            cronScheduleService ?? new Services.Cron.CronScheduleService(localizationService),
            cronExecutionWorker,
            conversationNavigator,
            workspaceService,
            localizationService);
        _knowledgeBaseViewModel = new KnowledgeBaseViewModel(
            fileSystemService,
            platformPathService,
            knowledgeBaseService,
            localizationService,
            userInteractionService,
            knowledgeMaintenanceService,
            configurationSession);
        _logsViewModel = new LogsViewModel(logService, localizationService, userInteractionService);
        TerminalPanelViewModel = terminalPanelViewModel ?? new TerminalPanelViewModel();
        TerminalPanelViewModel.ActivateScope(null, null);
        TerminalPanelViewModel.AllTerminalsClosed += OnAllTerminalsClosed;

        if (archiveService != null)
        {
            archiveService.ArchiveStaged += OnArchiveStaged;
            archiveService.ArchiveCompleted += OnArchiveCompleted;
            archiveService.ArchiveFailed += OnArchiveFailed;
        }

        // cron 侧的挂载：worker → launcher → 本窗口。依赖方向单向，不成环。
        _cronSessionLauncher?.AttachHost(this);
        _conversationNavigator?.AttachTarget(this);

        _logger.Information("MainWindowViewModel initialized");

        _ = InitializeConversationTreeAsync();
        _ = RefreshCompactLogsAsync();
        if (_logService != null)
            _logService.LogsChanged += OnLogsChanged;
    }

    private void OnApprovalQueueChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshApprovalStates();

    private void OnLogsChanged()
    {
        // SQLiteSink 在后台线程触发，需切回 UI 线程刷新集合。
        Dispatcher.UIThread.Post(async () => await RefreshCompactLogsAsync());
    }

    /// <summary>
    /// cron 触发的落地点：造一个全新会话、绑定工作区、插入会话树，然后交回给 launcher 去跑。
    ///
    /// 这里有一条绝不能破的规则：**不碰 <see cref="SelectedConversation"/>**。
    /// 整套"每次触发开新 session"的设计就是为了不打断用户正在看的那个会话；
    /// 一旦这里顺手选中新会话，用户的视图会被凭空抢走，等于回到了旧的插消息行为。
    /// </summary>
    public async Task<CronSessionAttachment> AttachScheduledSessionAsync(CronTask task, CronTaskRunRecord run)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            return await Dispatcher.UIThread.InvokeAsync(() => AttachScheduledSessionAsync(task, run));
        }

        var globalGroup = GlobalConversationGroup;
        var targetGroup = string.IsNullOrWhiteSpace(task.WorkspaceId)
            ? globalGroup
            : ConversationGroups.FirstOrDefault(group => group.Workspace?.Id == task.WorkspaceId);

        var workspaceFellBack = !string.IsNullOrWhiteSpace(task.WorkspaceId) && targetGroup == null;
        targetGroup ??= globalGroup ?? ConversationGroups.FirstOrDefault();

        if (targetGroup == null)
        {
            throw new InvalidOperationException("The conversation tree has no group to place the scheduled session in.");
        }

        if (workspaceFellBack)
        {
            _logger.Warning(
                "Cron task {TaskId} targets workspace {WorkspaceId}, which no longer exists; placing its session in the global group",
                task.Id, task.WorkspaceId);
        }

        var chat = CreateChatSession();
        await chat.InitializeWorkspacesAsync();
        chat.AssignWorkspace(targetGroup.Workspace);
        // 溯源必须在第一次持久化之前打上，否则首版快照会以普通会话落库，重启后再也认不出它。
        chat.MarkAsScheduledRun(task.Id, run.RunId, run.ScheduledFor);

        var session = new ConversationSessionItemViewModel(chat, targetGroup.Workspace, _conversationStore, null, _localizationService)
        {
            CreatedByCronTaskId = task.Id,
            CronTaskRunId = run.RunId
        };
        // 占位标题在静默标题生成完成之前也能让用户看出这是哪个任务跑出来的。
        session.Title = task.Name;

        WireSession(session);
        targetGroup.Conversations.Insert(0, session);
        RefreshPinnedConversations();
        OnConversationSearchTextChanged(ConversationSearchText);

        _logger.Information(
            "Scheduled session created for cron task {TaskId} run {RunId}: conversation={ConversationId}, workspace={WorkspaceId}",
            task.Id, run.RunId, session.ConversationId, targetGroup.Workspace?.Id);

        return new CronSessionAttachment
        {
            ConversationId = session.ConversationId,
            HistoryId = session.HistoryId,
            WorkspaceFellBack = workspaceFellBack,
            RunInstructionAsync = (instruction, cancellationToken) =>
                Dispatcher.UIThread.InvokeAsync(() => chat.RunScheduledInstructionAsync(instruction, cancellationToken)),
            ReportOutcome = (state, notifyOnCompletion) =>
                RunOnUiThread(() => CompleteScheduledSession(session, state, notifyOnCompletion))
        };
    }

    /// <summary>把一次定时运行的结果反映到会话树条目上，并触发静默标题生成。</summary>
    private void CompleteScheduledSession(
        ConversationSessionItemViewModel session,
        CronRunState state,
        bool notifyOnCompletion)
    {
        session.ApplyScheduledRunOutcome(state, notifyOnCompletion);

        var isFailure = state is CronRunState.Failed or CronRunState.Interrupted;
        // 失败始终提示；成功才看 notifyOnCompletion。托盘本身已会在窗口处于前台时跳过闪烁。
        if (isFailure || notifyOnCompletion)
        {
            App.StartTrayFlashing();
            NotifyScheduledRunCompleted(session, isFailure);
        }

        _ = GenerateSilentTitleAsync(session);
        _ = session.PersistNowAsync();
    }

    /// <summary>
    /// 定时运行结束时的系统通知。这是三个通知场景里最需要它的一个——
    /// 会话是在后台新开的，用户根本不知道发生过什么。
    ///
    /// 用 Normal 而不是 Critical：跑完的任务不阻塞任何东西，
    /// 而且会话树上的未读点/失败点本来就会一直留着，通知消失并不会丢失现场。
    /// </summary>
    private void NotifyScheduledRunCompleted(ConversationSessionItemViewModel session, bool isFailure)
    {
        if (_notifications == null) return;
        // 前台时不发：未读点已经在用户眼前了。
        if (_foregroundProbe?.IsForeground ?? false) return;

        var title = isFailure
            ? _localizationService?.GetString("Notification.Cron.FailedTitle", "A scheduled task did not finish")
              ?? "A scheduled task did not finish"
            : _localizationService?.GetString("Notification.Cron.DoneTitle", "A scheduled task finished")
              ?? "A scheduled task finished";

        _ = _notifications.ShowAsync(new SystemNotificationRequest
        {
            Title = title,
            // 标题此刻是任务名（静默标题生成还没跑完），这正是用户能认出来的那个名字。
            Body = session.Title,
            Urgency = SystemNotificationUrgency.Normal,
            // 按会话取键：每次触发都是一个新会话，所以多次运行不会互相顶掉。
            Key = "athena.cron:" + session.ConversationId
        });
    }

    /// <summary>
    /// 任务页"打开这次运行的会话"的落点。任务页只认 <see cref="IConversationNavigator"/>，
    /// 不直接翻本窗口的集合。
    /// </summary>
    public async Task<bool> TryNavigateToConversationAsync(string? historyId, string? conversationId)
    {
        if (string.IsNullOrWhiteSpace(historyId) && string.IsNullOrWhiteSpace(conversationId)) return false;

        // 选中会话必须在 UI 线程上做，但绝不阻塞等它：await InvokeAsync 会在等待期间
        // 让出调用线程，而 GetAwaiter().GetResult() 会把它钉死——后台线程钉住 UI 线程、
        // UI 线程又在等后台，就是 PersistSessionStateAsync 注释里那种只能强杀的退出死锁。
        if (!Dispatcher.UIThread.CheckAccess())
        {
            return await Dispatcher.UIThread.InvokeAsync(
                () => TryNavigateToConversationCore(historyId, conversationId));
        }

        return TryNavigateToConversationCore(historyId, conversationId);
    }

    private bool TryNavigateToConversationCore(string? historyId, string? conversationId)
    {
        var session = ConversationGroups
            .SelectMany(group => group.Conversations)
            .FirstOrDefault(candidate =>
                (!string.IsNullOrWhiteSpace(historyId) && string.Equals(candidate.HistoryId, historyId, StringComparison.Ordinal))
                || (!string.IsNullOrWhiteSpace(conversationId) && string.Equals(candidate.ConversationId, conversationId, StringComparison.Ordinal)));

        if (session == null) return false;

        SelectedConversation = session;
        return true;
    }

    private void OnArchiveStaged(object? sender, ConversationArchiveResultEventArgs e)
    {
        RunOnUiThread(() =>
        {
            FindConversation(e.Snapshot)?.SetArchivePending(
                _localizationService?.GetString("History.PendingStatus", "Summarizing") ?? "Summarizing");
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
            _logger.Error(ex, "Session tree sync after archive completion event failed: {HistoryId}", historyId);
        }
    }

    private void OnArchiveFailed(object? sender, ConversationArchiveResultEventArgs e)
    {
        RunOnUiThread(() =>
        {
            FindConversation(e.Snapshot)?.SetArchiveFailed(
                _localizationService?.GetString("Chat.Archive.RetryLater", "Failed to archive the previous chat; will retry later.")
                ?? "Failed to archive the previous chat; will retry later.");
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

        _logger.Information("Session tree inserting external archive: {HistoryId}", history.Id);
        var chat = CreateChatSession();
        await chat.InitializeWorkspacesAsync();
        chat.RestorePersistedConversation(history);
        chat.AssignWorkspace(group.Workspace);
        chat.InputText = history.Draft;
        session = new ConversationSessionItemViewModel(chat, group.Workspace, _conversationStore, history.Id, _localizationService)
        {
            Title = string.IsNullOrWhiteSpace(history.Summary) ? _localizationService?.GetString("Session.Title.NewConversation", "New chat") ?? "New chat" : history.Summary,
            IsPinned = history.IsPinned,
            UpdatedAt = history.UpdatedAt,
            WasInterrupted = string.Equals(history.RuntimeStatus, "interrupted", StringComparison.OrdinalIgnoreCase),
            ForkedFromConversationId = history.ForkedFromConversationId,
            ForkedFromHistoryId = history.ForkedFromHistoryId,
            CreatedByCronTaskId = history.CreatedByCronTaskId,
            CronTaskRunId = history.CronTaskRunId,
            ForkDepth = ResolveForkDepth(history)
        };
        session.SetArchiveCompleted();
        WireSession(session);
        group.Conversations.Insert(0, session);
        RefreshPinnedConversations();
        OnConversationSearchTextChanged(ConversationSearchText);
        _logger.Information("Session tree inserted external archive: {HistoryId}", history.Id);
    }

    private MainConversationViewModel CreateChatSession() =>
        _chatSessionFactory?.Create()
        ?? new MainConversationViewModel(
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

    public async Task PersistSessionStateAsync()
    {
        // 会话持久化包含真正的异步 I/O（SQLite），且 PersistNowAsync 在非 UI 线程上
        // 需要回到 UI 线程捕获快照。退出流程必须在 UI 线程上 await 本方法：UI 线程在
        // 每次 await 处让出，分发器才能泵送 InvokeAsync 回调，避免“UI 线程同步等待 +
        // 线程池任务等待 UI 线程取快照”的互等死锁（旧实现 GetAwaiter().GetResult()
        // 会把 UI 线程阻塞死，导致应用退出只能强杀）。
        var saves = ConversationGroups
            .SelectMany(group => group.Conversations)
            .Select(session => session.PersistNowAsync());
        await Task.WhenAll(saves);
    }

    /// <summary>
    /// 由全部历史条目构建 historyId → 分支深度 映射（0=非分支，1=直接分支，
    /// 2=分支的分支……）。通过 ForkedFromHistoryId 追溯父链；父不在本次加载集合
    /// （如已删除）时按深度 1 计，保证分支图标照常显示。
    /// </summary>
    private static Dictionary<string, int> BuildForkDepthMap(IReadOnlyList<ConversationHistoryItem> histories)
    {
        var byId = histories.ToDictionary(history => history.Id);
        var depths = new Dictionary<string, int>(histories.Count);

        int DepthOf(ConversationHistoryItem history)
        {
            if (depths.TryGetValue(history.Id, out var cached)) return cached;

            int depth = 0;
            if (!string.IsNullOrWhiteSpace(history.ForkedFromHistoryId))
            {
                depth = byId.TryGetValue(history.ForkedFromHistoryId, out var parent)
                    ? DepthOf(parent) + 1
                    : 1;
            }

            depths[history.Id] = depth;
            return depth;
        }

        foreach (var history in histories)
        {
            DepthOf(history);
        }

        return depths;
    }

    /// <summary>
    /// 为外部归档单条插入解析分支深度：优先复用当前树中父会话的深度（父会话通常已存在，
    /// 其深度在创建时已计算），否则按直接分支（深度 1）兜底。
    /// </summary>
    private int ResolveForkDepth(ConversationHistoryItem history)
    {
        if (string.IsNullOrWhiteSpace(history.ForkedFromHistoryId)) return 0;

        var parent = ConversationGroups
            .SelectMany(group => group.Conversations)
            .FirstOrDefault(session => session.HistoryId == history.ForkedFromHistoryId);
        return parent?.ForkDepth + 1 ?? 1;
    }

    private async Task InitializeConversationTreeAsync()
    {
        IsConversationTreeLoading = true;
        try
        {
            DisposeConversationGroups();
            var globalGroup = new WorkspaceConversationGroupViewModel(null, _platformPathService?.GetHistoryDirectory() ?? string.Empty, _localizationService);
            WireGroup(globalGroup);
            ConversationGroups.Add(globalGroup);

            var workspaces = _workspaceService == null ? [] : await _workspaceService.LoadAllAsync();
            foreach (var workspace in workspaces)
            {
                var group = new WorkspaceConversationGroupViewModel(workspace, string.Empty, _localizationService);
                WireGroup(group);
                ConversationGroups.Add(group);
            }

            var histories = _conversationArchiveService == null ? [] : await _conversationArchiveService.LoadAllAsync();
            var forkDepths = BuildForkDepthMap(histories);
            foreach (var history in histories.OrderByDescending(item => item.IsPinned).ThenByDescending(item => item.UpdatedAt))
            {
                var group = ConversationGroups.FirstOrDefault(candidate => candidate.Workspace?.Id == history.WorkspaceId) ?? globalGroup;
                var chat = CreateChatSession();
                await chat.InitializeWorkspacesAsync();
                chat.RestorePersistedConversation(history);
                chat.AssignWorkspace(group.Workspace);
                chat.InputText = history.Draft;
                var session = new ConversationSessionItemViewModel(chat, group.Workspace, _conversationStore, history.Id, _localizationService)
                {
                    Title = string.IsNullOrWhiteSpace(history.Summary) ? _localizationService?.GetString("Session.Title.NewConversation", "New chat") ?? "New chat" : history.Summary,
                    IsPinned = history.IsPinned,
                    UpdatedAt = history.UpdatedAt,
                    WasInterrupted = string.Equals(history.RuntimeStatus, "interrupted", StringComparison.OrdinalIgnoreCase),
                    ForkedFromConversationId = history.ForkedFromConversationId,
                    ForkedFromHistoryId = history.ForkedFromHistoryId,
                    CreatedByCronTaskId = history.CreatedByCronTaskId,
                    CronTaskRunId = history.CronTaskRunId,
                    ForkDepth = forkDepths.TryGetValue(history.Id, out var forkDepth) ? forkDepth : 0
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
            _logger.Error(ex, "Failed to initialize multi-session tree");
            if (ConversationGroups.Count == 0)
            {
                var globalGroup = new WorkspaceConversationGroupViewModel(null, _platformPathService?.GetHistoryDirectory() ?? string.Empty, _localizationService);
                WireGroup(globalGroup);
                ConversationGroups.Add(globalGroup);
            }
            SelectedConversation = await CreateConversationCoreAsync(ConversationGroups[0]);
        }
        finally
        {
            IsConversationTreeLoading = false;
            // cron 执行器必须等到这里才启动：工作区分组和会话树都已就位，
            // 第一批到期任务才有地方落。提前启动只会让它们撞上一棵空树。
            await StartCronExecutionAsync();
        }
    }

    private async Task StartCronExecutionAsync()
    {
        if (_cronExecutionWorker == null) return;
        try
        {
            await _cronExecutionWorker.StartAsync();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to start the cron execution worker");
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
        var session = new ConversationSessionItemViewModel(chat, group.Workspace, _conversationStore, null, _localizationService);
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
        var path = await _userInteractionService.PickFolderAsync(_localizationService?.GetString("Chat.Workspace.SelectFolder", "Select workspace directory") ?? "Select workspace directory");
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
            group = new WorkspaceConversationGroupViewModel(workspace, string.Empty, _localizationService);
            WireGroup(group);
            ConversationGroups.Add(group);
        }
        SelectedConversation = await CreateConversationCoreAsync(group);
    }

    [RelayCommand]
    private async Task DeleteConversationAsync(ConversationSessionItemViewModel? session)
    {
        if (session == null) return;
        _logger.Information("MainWindow deleting conversation: Id={Id}, Title={Title}", session.HistoryId, session.Title);
        if (session.IsRunning) session.Chat.StopResponseCommand.Execute(null);
        var group = ConversationGroups.FirstOrDefault(candidate => candidate.Conversations.Contains(session));
        group?.Conversations.Remove(session);
        RefreshPinnedConversations();
        if (_conversationStore != null)
        {
            await _conversationStore.DeleteAsync(session.HistoryId);
            _logger.Information("MainWindow conversation deleted: Id={Id}", session.HistoryId);
        }
        else
        {
            _logger.Warning("MainWindow conversation delete not persisted (no store): Id={Id}", session.HistoryId);
        }
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
        if (MainOwner is { } owner) await new KnowledgeBaseWindow(KnowledgeBaseViewModel, _localizationService).ShowDialog(owner);
    }

    [RelayCommand]
    private async Task OpenTasksAsync()
    {
        if (MainOwner is { } owner) await new TasksWindow(TasksViewModel, _localizationService).ShowDialog(owner);
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
        _logger.Information("MainWindow opening AppSettings");
        if (MainOwner is { } owner && _appSettingsFactory?.Invoke() is { } viewModel)
            await new AppSettingsWindow { DataContext = viewModel }.ShowDialog(owner);
    }

    [RelayCommand]
    private async Task OpenDetailedLogsAsync()
    {
        await LogsViewModel.RefreshLogsAsync();
        if (MainOwner is { } owner) await new DetailedLogsWindow(LogsViewModel, _localizationService).ShowDialog(owner);
    }

    [RelayCommand]
    private async Task ToggleSidePanelsAsync()
    {
        if (Config == null || _configurationSession == null) return;
        Config.MainLayout.SidePanelsSwapped = !Config.MainLayout.SidePanelsSwapped;
        _logger.Information("MainWindow toggled side panels: NewValue={Value}", Config.MainLayout.SidePanelsSwapped);
        OnPropertyChanged(nameof(IsSidePanelsSwapped));
        await _configurationSession.SaveNowAsync();
    }

    public Task SaveConfigurationNowAsync() =>
        _configurationSession?.SaveNowAsync() ?? Task.CompletedTask;

    private void OnCurrentConfigChanged(object? sender, AppConfig config)
    {
        OnPropertyChanged(nameof(Config));
        OnPropertyChanged(nameof(IsSidePanelsSwapped));
        OnPropertyChanged(nameof(PanelTransparency));
        OnPropertyChanged(nameof(ShellPanelOpacity));
        OnPropertyChanged(nameof(PanelGlassEnabled));
        TrackMainLayout(config.MainLayout);
    }

    private async Task RefreshCompactLogsAsync()
    {
        if (_logService == null) return;
        var result = await _logService.QueryLogsAsync(new LogQueryParams
        {
            Page = 1,
            PageSize = 200
        });
        var entries = result.Entries.Select(entry => new LogEntryViewModel(entry)).ToList();
        GlobalErrorCount = entries.Count(entry => entry.Level is "ERROR" or "FATAL");
        CompactLogEntries.Clear();
        foreach (var entry in entries)
            CompactLogEntries.Add(entry);
    }

    [RelayCommand]
    private async Task ForkConversationAsync(ConversationSessionItemViewModel? source)
    {
        if (source == null || _conversationStore == null) return;

        // 1. 持久化父会话（同时覆盖「父会话尚未归档」的场景——旧实现的 LoadByIdAsync 会
        //    因返回 null 而中止；新流程总是先建行，血缘取自 source.Chat/source.HistoryId）。
        await source.PersistNowAsync();

        // 2. 全量复制（forkPointMessage 为空）：完整保留消息、附件物理克隆、图像续作复制。
        var forkHistoryId = Guid.NewGuid().ToString();
        var forkConversationId = Guid.NewGuid().ToString("N");
        var (snapshot, _) = await source.Chat.CaptureForkSnapshotAsync(
            forkHistoryId, forkConversationId, source.HistoryId,
            source.Title + " · 分支", DateTime.Now,
            isPinned: false, workspaceId: source.WorkspaceId,
            forkPointMessage: null);

        // 3. 落库为全新历史行。
        var fork = ConversationSessionItemViewModel.ToHistoryItem(snapshot);
        await _conversationStore.SaveAsync(fork);

        // 4. 新分支会话（不修改父会话任何状态）。
        var group = ConversationGroups.First(candidate => candidate.Conversations.Contains(source));
        var chat = CreateChatSession();
        await chat.InitializeWorkspacesAsync();
        chat.RestorePersistedConversation(fork);
        chat.AssignWorkspace(group.Workspace);
        chat.MarkPersistenceMetadataChanged();

        // 5. 插入源下方并选中。
        var session = new ConversationSessionItemViewModel(chat, group.Workspace, _conversationStore, fork.Id, _localizationService)
        {
            Title = fork.Summary,
            UpdatedAt = fork.UpdatedAt,
            ForkedFromConversationId = fork.ForkedFromConversationId,
            ForkedFromHistoryId = fork.ForkedFromHistoryId,
            ForkDepth = source.ForkDepth + 1
        };
        // 分支标题（"父标题 · 分支"）是显式命名，静默标题生成不再覆盖。
        session.DisableSilentTitleGeneration();
        WireSession(session);
        var sourceIndex = group.Conversations.IndexOf(source);
        group.Conversations.Insert(sourceIndex < 0 ? 0 : sourceIndex + 1, session);
        SelectedConversation = session;
    }

    /// <summary>
    /// 消息级分支：保留 fork 点之前的上下文、丢弃其后消息，fork 点消息回填输入区
    /// （内容 + 附件克隆）。以「先持久化父会话、再建全新分支会话」的安全模式执行，
    /// 父会话在树中原样保留、其历史行不被覆盖。
    /// </summary>
    private async Task ForkFromMessageAsync(ConversationSessionItemViewModel? source, ChatMessage? message)
    {
        if (source == null || message == null || _conversationStore == null) return;
        if (message.Role != "user" || source.Chat.IsSending || source.Chat.IsCompressing) return;

        // 1. 持久的父保存：分支血缘锚点必须真实存在，防止分支后续写入父历史行。
        await source.PersistNowAsync();

        // 2. 非侵入式捕获截断分支（不修改父会话）。
        var forkHistoryId = Guid.NewGuid().ToString();
        var forkConversationId = Guid.NewGuid().ToString("N");
        var (snapshot, pendingClones) = await source.Chat.CaptureForkSnapshotAsync(
            forkHistoryId, forkConversationId, source.HistoryId,
            source.Title + " · 分支", DateTime.Now,
            isPinned: false, workspaceId: source.WorkspaceId,
            forkPointMessage: message);

        // 3. 落库。
        var fork = ConversationSessionItemViewModel.ToHistoryItem(snapshot);
        await _conversationStore.SaveAsync(fork);

        // 4. 新分支会话。
        var group = ConversationGroups.First(candidate => candidate.Conversations.Contains(source));
        var chat = CreateChatSession();
        await chat.InitializeWorkspacesAsync();
        chat.RestorePersistedConversation(fork);
        chat.AssignWorkspace(group.Workspace);

        // 5. 回填 fork 点：内容进输入框、附件克隆挂回待发送列表。
        //    restore 内部会清空待发送附件，因此必须在此之后回填。
        chat.InputText = message.Content ?? string.Empty;
        chat.RestoreAttachmentsToPending(pendingClones, deleteOverflowFiles: false);

        // 6. 把 revision 拉到 1：restore 后 _revision 为 0，若用户先编辑回填草稿再发送，
        //    首次 autosave 仍是 revision 0 会触发「同 revision 不同 payload」冲突。
        //    须在会话项订阅之前调用，避免触发多余的同步保存。
        chat.MarkPersistenceMetadataChanged();

        // 7. 插入源下方并选中。
        var session = new ConversationSessionItemViewModel(chat, group.Workspace, _conversationStore, fork.Id, _localizationService)
        {
            Title = fork.Summary,
            UpdatedAt = fork.UpdatedAt,
            ForkedFromConversationId = fork.ForkedFromConversationId,
            ForkedFromHistoryId = fork.ForkedFromHistoryId,
            ForkDepth = source.ForkDepth + 1
        };
        // 分支标题（"父标题 · 分支"）是显式命名，静默标题生成不再覆盖。
        session.DisableSilentTitleGeneration();
        WireSession(session);
        var sourceIndex = group.Conversations.IndexOf(source);
        group.Conversations.Insert(sourceIndex < 0 ? 0 : sourceIndex + 1, session);
        SelectedConversation = session;
    }

    private void WireSession(ConversationSessionItemViewModel session)
    {
        session.DeleteRequested += async (_, _) => await DeleteConversationAsync(session);
        session.ForkRequested += async (_, _) => await ForkConversationAsync(session);
        session.MessageForkRequested += async (_, e) => await ForkFromMessageAsync(session, e.Message);
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
        group.ContextSettingsRequested += async (_, _) => await OpenWorkspaceContextSettingsAsync(group);
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
        if (string.IsNullOrWhiteSpace(group.DirectoryPath)) return;
        var clipboard = MainOwner == null ? null : TopLevel.GetTopLevel(MainOwner)?.Clipboard;
        if (clipboard != null) await clipboard.SetTextAsync(group.DirectoryPath);
    }

    private void RevealWorkspace(WorkspaceConversationGroupViewModel group)
    {
        if (string.IsNullOrWhiteSpace(group.DirectoryPath) || !Directory.Exists(group.DirectoryPath)) return;
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("explorer.exe", $"\"{group.DirectoryPath}\"") { UseShellExecute = true }
            : OperatingSystem.IsMacOS()
                ? new ProcessStartInfo("open", $"\"{group.DirectoryPath}\"") { UseShellExecute = true }
                : new ProcessStartInfo("xdg-open", $"\"{group.DirectoryPath}\"") { UseShellExecute = true };
        Process.Start(start);
    }

    private async Task OpenWorkspaceContextSettingsAsync(WorkspaceConversationGroupViewModel group)
    {
        if (group.Workspace == null
            || _workspaceService == null
            || _contextPolicyProvider == null
            || Config == null
            || MainOwner is not { } owner)
            return;
        var viewModel = new WorkspaceContextSettingsViewModel(
            group.Workspace,
            Config,
            _contextPolicyProvider,
            _workspaceService,
            _localizationService);
        await new WorkspaceContextSettingsWindow { DataContext = viewModel }.ShowDialog(owner);
    }

    private async Task DeleteWorkspaceAsync(WorkspaceConversationGroupViewModel group)
    {
        _logger.Information("MainWindow deleting workspace: Name={Name}", group?.Name);
        if (group?.Workspace == null || _workspaceService == null) return;
        if (_userInteractionService == null || !await _userInteractionService.ConfirmAsync(
                _localizationService?.GetString("MainWindow.DeleteWorkspace.Title", "Delete workspace") ?? "Delete workspace",
                string.Format(_localizationService?.GetString("MainWindow.DeleteWorkspace.Message", "Delete \"{0}\"? Workspace knowledge files will be deleted, but conversation history is preserved.") ?? "Delete \"{0}\"? Workspace knowledge files will be deleted, but conversation history is preserved.", group.Name),
                _localizationService?.GetString("MainWindow.Menu.Delete", "Delete") ?? "Delete",
                _localizationService?.GetString("Common.Cancel", "Cancel") ?? "Cancel")) return;
        if (!await _workspaceService.DeleteAsync(group.Workspace.Id)) return;
        await TerminalPanelViewModel.CloseScopeAsync(group.Workspace.Id);

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
        group.Dispose();
        RefreshPinnedConversations();
    }

    private async Task ExportConversationAsync(ConversationSessionItemViewModel session)
    {
        if (_userInteractionService == null) return;
        var safeTitle = string.Concat(session.Title.Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        var target = await _userInteractionService.PickSaveFileAsync(
            _localizationService?.GetString("MainWindow.Export.PickerTitle", "Export chat") ?? "Export chat",
            $"{safeTitle}.md",
            _localizationService?.GetString("MainWindow.Export.FileExtension", "Markdown") ?? "Markdown",
            new[] { _localizationService?.GetString("MainWindow.Export.FileFilter", "*.md") ?? "*.md" });
        if (string.IsNullOrWhiteSpace(target)) return;

        var markdown = new StringBuilder()
            .Append("# ").AppendLine(session.Title)
            .AppendLine()
            .Append("> ").AppendLine(string.Format(_localizationService?.GetString("MainWindow.Export.Header", "Exported at: {0}") ?? "Exported at: {0}", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")))
            .AppendLine();
        foreach (var message in session.Chat.Messages.Where(message => message.IsVisibleToUser))
        {
            var role = message.Role switch
            {
                "user" => _localizationService?.GetString("MainWindow.Export.Role.User", "You") ?? "You",
                "assistant" => _localizationService?.GetString("MainWindow.Export.Role.Assistant", "Athena") ?? "Athena",
                "system" => _localizationService?.GetString("MainWindow.Export.Role.System", "System") ?? "System",
                _ => message.Role
            };
            markdown.Append("## ").Append(role).Append(" · ")
                .AppendLine(message.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"))
                .AppendLine()
                .AppendLine(message.Content)
                .AppendLine();
            foreach (var attachment in message.Attachments)
            {
                markdown.Append(string.Format(_localizationService?.GetString("MainWindow.Export.Attachment", "- Attachment: {0} ({1})") ?? "- Attachment: {0} ({1})", attachment.DisplayName, attachment.DisplayKind))
                    .AppendLine();
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
        _surfaceSwapTimer?.Stop();
        _surfaceSwapTimer = null;
        _cronSessionLauncher?.DetachHost(this);
        _conversationNavigator?.DetachTarget(this);
        if (_configurationSession != null)
            _configurationSession.CurrentChanged -= OnCurrentConfigChanged;
        TrackMainLayout(null);
        if (_logService != null)
            _logService.LogsChanged -= OnLogsChanged;

        foreach (var session in ConversationGroups
                     .SelectMany(group => group.Conversations)
                     .Distinct())
        {
            session.Dispose();
        }
        DisposeConversationGroups();
        PinnedConversations.Clear();
        KnowledgeBaseViewModel.Dispose();
        this.TasksViewModel.Dispose();
        LogsViewModel.Dispose();
        TerminalPanelViewModel.AllTerminalsClosed -= OnAllTerminalsClosed;
        TerminalPanelViewModel.Dispose();
    }

    private void DisposeConversationGroups()
    {
        foreach (var group in ConversationGroups) group.Dispose();
        ConversationGroups.Clear();
    }
}
