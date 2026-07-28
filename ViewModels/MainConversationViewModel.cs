using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.ClientModel;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using Athena.UI.Services;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System.Collections.Specialized;

namespace Athena.UI.ViewModels;

public partial class MainConversationViewModel : ViewModelBase, IDisposable
{
    private enum TransitionStageResult
    {
        NotNeeded,
        Staged,
        Failed
    }

    private readonly IChatService? _chatService;
    private readonly IConfigService? _configService;
    private readonly IContextCompressionService? _contextCompressionService;
    private readonly IPromptService? _promptService;
    private readonly ITaskScheduler? _taskScheduler;
    private readonly IFunctionRegistry? _functionRegistry;
    private readonly ITokenService? _tokenService;
    private readonly ILocalizationService? _localizationService;
    private readonly IAttachmentStoreService? _attachmentStoreService;
    private readonly ISystemAudioService? _systemAudioService;
    // Cancels in-flight system (afplay/aplay/powershell) playback so Stop can
    // actually terminate the external process — pausing the libvlc player does
    // nothing for system-provider audio.
    private CancellationTokenSource? _systemAudioCts;
    // Tracks which attachment is currently playing. When a new clip starts, a
    // new ID is written. The stale finally block of the *previous* clip checks
    // this before clearing state — preventing it from undoing the new clip's
    // IsPlaying=true.
    private string? _playingAttachmentId;
    // Session-scoped cancellation for background TTS. Multiple messages may
    // generate audio concurrently (auto after a reply, or manual on demand),
    // so generations are NOT single-flight — sending a new message must not
    // kill the previous message's audio. Rewind / fork / conversation-switch
    // cancel + replace this token to abort every in-flight generation whose
    // target message or session is going away.
    private CancellationTokenSource _audioSessionCts = new();
    private readonly IConversationArchiveService? _archiveService;
    private readonly IImageGenerationSessionService? _imageGenerationSessionService;
    private readonly IScreenCaptureService? _screenCaptureService;
    private readonly IWorkspaceService? _workspaceService;
    private readonly IConversationSessionAccessor? _sessionAccessor;
    private readonly IUserInteractionService? _userInteractionService;
    private readonly ConversationExecutionCoordinator? _executionCoordinator;
    private readonly ILogger _logger = Log.ForContext<MainConversationViewModel>();
    private bool _disposed;

    public bool IsDisposed => _disposed;

    // 工具轮封口后置位，使下一段阶段性正文另起一个 Markdown 段（工具组置顶后不再天然分隔相邻文本）。
    private bool _forceNewAssistantTextSegment;

    // 批量恢复消息期间抑制 CollectionChanged 的逐条重算，灌完统一算一次
    private bool _isBulkLoadingMessages;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendMessageCommand))]
    private string _inputText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendMessageCommand))]
    [NotifyCanExecuteChangedFor(nameof(RewindToMessageCommand))]
    [NotifyCanExecuteChangedFor(nameof(ForkFromMessageCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopResponseCommand))]
    [NotifyCanExecuteChangedFor(nameof(UndoCompressionCommand))]
    [NotifyPropertyChangedFor(nameof(CanToggleRawContext))]
    [NotifyPropertyChangedFor(nameof(CanAcceptAttachments))]
    [NotifyPropertyChangedFor(nameof(ActivityStatusText))]
    private bool _isSending;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActivityStatusText))]
    private bool _isQueued;

    public string ActivityStatusText => IsQueued
        ? "排队中"
        : IsSending
            ? "运行中"
            : "就绪";

    public string ConversationId => _conversationId;

    public string? CurrentHistoryId => _currentHistoryId;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NewConversationCommand))]
    [NotifyPropertyChangedFor(nameof(CanToggleRawContext))]
    [NotifyPropertyChangedFor(nameof(CanAcceptAttachments))]
    private bool _isResetting;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendMessageCommand))]
    [NotifyCanExecuteChangedFor(nameof(RewindToMessageCommand))]
    [NotifyCanExecuteChangedFor(nameof(ForkFromMessageCommand))]
    [NotifyCanExecuteChangedFor(nameof(UndoCompressionCommand))]
    [NotifyPropertyChangedFor(nameof(CanToggleRawContext))]
    [NotifyPropertyChangedFor(nameof(CanAcceptAttachments))]
    private bool _isCompressing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAttachmentStatusMessage))]
    private string _attachmentStatusMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBackgroundArchiveStatusMessage))]
    [NotifyPropertyChangedFor(nameof(HasBackgroundArchiveNeutralStatus))]
    [NotifyPropertyChangedFor(nameof(HasBackgroundArchiveErrorStatus))]
    private string _backgroundArchiveStatusMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBackgroundArchiveNeutralStatus))]
    [NotifyPropertyChangedFor(nameof(HasBackgroundArchiveErrorStatus))]
    private bool _isBackgroundArchiveError;

    [ObservableProperty]
    private string _currentTheme = "Dark";

    [ObservableProperty]
    private string _themeIcon = "Moon"; // "Moon"=当前Dark点一下切Light, "Sun"=当前Light点一下切Dark

    // 调试：原始上下文（发送给主模型的 raw 消息）视图开关与内容。
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendMessageCommand))]
    [NotifyPropertyChangedFor(nameof(CanAcceptAttachments))]
    private bool _isRawContextView;

    /// <summary>调试：原始上下文按消息拆分的条目（避免单一大文本框选择卡顿）。</summary>
    public ObservableCollection<RawContextEntry> RawContextEntries { get; } = new();

    /// <summary>仅当对话流处于「完成」态（非发送/压缩/解析/重置）时，才允许切换 raw 视图。</summary>
    public bool CanToggleRawContext => !IsSending && !IsCompressing && !IsResetting;

    /// <summary>仅在聊天处于可发送状态时接受选择、粘贴或拖放附件。</summary>
    public bool CanAcceptAttachments => !IsSending && !IsCompressing && !IsResetting && !IsRawContextView;

    public BulkObservableCollection<ChatMessage> Messages { get; } = new();

    public ObservableCollection<ChatAttachment> PendingAttachments { get; } = new();

    public bool HasPendingAttachments => PendingAttachments.Count > 0;

    public bool HasAttachmentStatusMessage => !string.IsNullOrWhiteSpace(AttachmentStatusMessage);

    public bool HasBackgroundArchiveStatusMessage => !string.IsNullOrWhiteSpace(BackgroundArchiveStatusMessage);

    public bool HasBackgroundArchiveNeutralStatus => HasBackgroundArchiveStatusMessage && !IsBackgroundArchiveError;

    public bool HasBackgroundArchiveErrorStatus => HasBackgroundArchiveStatusMessage && IsBackgroundArchiveError;

    public string ContextTokensInfo => _tokenService?.TokenInfoText ?? "0 / 0 tokens";

    public ITokenService? TokenService => _tokenService;

    [ObservableProperty]
    private string _currentModelName = string.Empty;

    public string InputPlaceholder => "Chat.InputPlaceholder";

    /// <summary>历史会话完成替换后，请求聊天视图在下一轮布局完成时滚动到底部。</summary>
    public event EventHandler? HistoryConversationLoaded;

    private ConversationContext _currentContext = new();
    private CancellationTokenSource? _responseCts;
    private CancellationTokenSource? _previewLoadCts;
    private readonly SemaphoreSlim _conversationTransitionLock = new(1, 1);
    private int _conversationEpoch;

    // 记录当前加载的历史对话 ID，如果是新对话则为空
    private string? _currentHistoryId;

    // 记录加载历史时的初始签名，用于判断是否发生了修改
    private string? _initialConversationSignature;
    private string _conversationId = Guid.NewGuid().ToString("N");

    // 当前会话的上下文压缩摘要（唯一真源；TokenService.CompressionPreview 仅作设置页展示镜像）。
    private string? _activeContextSummary;

    // fork 元数据：当前会话若是从其他会话 fork 出的分支，归档时随快照持久化
    private string? _forkedFromConversationId;
    private string? _forkedFromHistoryId;
    private string? _forkedAtMessageId;

    // 会话内压缩撤销栈：每次压缩入栈一个检查点，撤销时弹出还原。切换/重置会话时清空。
    private readonly Stack<CompressionCheckpoint> _compressionHistory = new();

    private sealed record CompressionCheckpoint(string? PreviousSummary, IReadOnlyList<ChatMessage> Batch);

    private DateTime _latestArchiveCaptureAt = DateTime.MinValue;

    /// <summary>子代理编排器（供 RAW 旁的 Sub-Agents 弹出小镇绑定）。</summary>
    public ISubAgentOrchestrator? Orchestrator { get; }

    /// <summary>子代理"小镇"居中弹窗是否展开。</summary>
    [ObservableProperty]
    private bool _isSubAgentPopupOpen;

    /// <summary>小镇当前猫头鹰总数（Sub-Agents 按钮右下角角标）。</summary>
    [ObservableProperty]
    private int _subAgentCount;

    /// <summary>是否有猫头鹰在镇上（角标可见性）。</summary>
    [ObservableProperty]
    private bool _hasSubAgents;

    /// <summary>是否至少一只猫头鹰在干活（Pending/Running），驱动按钮的持续涟漪特效。</summary>
    [ObservableProperty]
    private bool _hasRunningSubAgents;

    // 已挂 State 监听的猫头鹰集合；集合变更时全量对账（Reset 拿不到旧项，无法逐项退订）。
    private readonly HashSet<SubAgentViewModel> _trackedSubAgents = new();

    private void OnActiveSubAgentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        var agents = Orchestrator?.ActiveAgents;
        if (agents == null) return;

        foreach (var stale in _trackedSubAgents.Where(a => !agents.Contains(a)).ToList())
        {
            stale.PropertyChanged -= OnSubAgentStatePropertyChanged;
            _trackedSubAgents.Remove(stale);
        }
        foreach (var agent in agents)
        {
            if (_trackedSubAgents.Add(agent))
            {
                agent.PropertyChanged += OnSubAgentStatePropertyChanged;
            }
        }

        RefreshSubAgentIndicators();
    }

    private void OnSubAgentStatePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SubAgentViewModel.State)) RefreshSubAgentIndicators();
    }

    private void RefreshSubAgentIndicators()
    {
        var agents = Orchestrator?.ActiveAgents;
        SubAgentCount = agents?.Count ?? 0;
        HasSubAgents = SubAgentCount > 0;
        HasRunningSubAgents = agents?.Any(a => a.State is SubAgentState.Pending or SubAgentState.Running) == true;

        // 整批谢幕：最后一只结束（完成/出错/取消/超时）后延时 2s，
        // 先播淡出动画再统一移出小镇；期间若有新批次派入则取消。
        if (HasSubAgents && !HasRunningSubAgents)
        {
            _subAgentClearTimer ??= CreateSubAgentClearTimer();
            if (!_subAgentClearTimer.IsEnabled) _subAgentClearTimer.Start();
        }
        else
        {
            _subAgentClearTimer?.Stop();
        }
    }

    private DispatcherTimer? _subAgentClearTimer;

    private DispatcherTimer CreateSubAgentClearTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var agents = Orchestrator?.ActiveAgents;
            if (agents == null || agents.Count == 0 || HasRunningSubAgents) return;

            foreach (var agent in agents)
            {
                agent.IsVanishing = true;
            }
            // 等淡出动画（0.45s）播完再真正移除。
            DispatcherTimer.RunOnce(() => Orchestrator?.ClearCompleted(), TimeSpan.FromMilliseconds(500));
        };
        return timer;
    }

    [RelayCommand]
    private void ToggleSubAgentPopup() => IsSubAgentPopupOpen = !IsSubAgentPopupOpen;

    [RelayCommand]
    private void CloseSubAgentPopup() => IsSubAgentPopupOpen = false;

    #region Workspace

    /// <summary>所有已注册的工作区列表（供下拉选择）</summary>
    public ObservableCollection<WorkspaceProfile> AvailableWorkspaces { get; } = new();

    /// <summary>当前激活的工作区</summary>
    [ObservableProperty]
    private WorkspaceProfile? _currentWorkspace;

    /// <summary>工作区管理面板是否展开</summary>
    [ObservableProperty]
    private bool _isWorkspaceFlyoutOpen;

    [RelayCommand]
    private void ToggleWorkspaceFlyout()
    {
        IsWorkspaceFlyoutOpen = !IsWorkspaceFlyoutOpen;
    }

    /// <summary>当前工作区显示名称（无工作区时显示"未选择工作区"）</summary>
    public string CurrentWorkspaceDisplayName =>
        CurrentWorkspace?.Name ?? GetString("Chat.Workspace.None", "未选择工作区");

    /// <summary>当前工作区目录路径（无则为空）</summary>
    public string? CurrentWorkspacePath => CurrentWorkspace?.DirectoryPath;

    partial void OnCurrentWorkspaceChanged(WorkspaceProfile? value)
    {
        OnPropertyChanged(nameof(CurrentWorkspaceDisplayName));
        OnPropertyChanged(nameof(CurrentWorkspacePath));
        ApplyWorkspaceToContext(value);
    }

    /// <summary>将工作区信息同步到当前对话上下文</summary>
    private void ApplyWorkspaceToContext(WorkspaceProfile? workspace)
    {
        _currentContext.WorkspaceId = workspace?.Id;
        _currentContext.WorkspaceDirectoryPath = workspace?.DirectoryPath;
        _currentContext.WorkspaceKnowledgeFilePath = workspace == null
            ? null
            : _workspaceService?.GetKnowledgeFilePath(workspace);
        _workspaceService?.SetActiveWorkspace(workspace);

        // 持久化最近活跃工作区
        if (_configService != null)
        {
            var config = _configService.Load();
            config.LastActiveWorkspaceId = workspace?.Id;
            _configService.SaveAsync(config);
        }

        UpdateContextTokensDisplay();
    }

    /// <summary>由三栏会话宿主在创建/恢复会话时设置固定工作区归属。</summary>
    public void AssignWorkspace(WorkspaceProfile? workspace) => CurrentWorkspace = workspace;

    /// <summary>加载工作区列表并恢复上次活跃工作区</summary>
    public async Task InitializeWorkspacesAsync()
    {
        if (_workspaceService == null) return;

        var workspaces = await _workspaceService.LoadAllAsync();
        AvailableWorkspaces.Clear();
        foreach (var ws in workspaces)
        {
            AvailableWorkspaces.Add(ws);
        }

        // 恢复上次活跃工作区
        if (_configService != null)
        {
            var config = _configService.Load();
            if (!string.IsNullOrEmpty(config.LastActiveWorkspaceId))
            {
                var last = workspaces.FirstOrDefault(w => w.Id == config.LastActiveWorkspaceId);
                if (last != null)
                {
                    CurrentWorkspace = last;
                }
            }
        }
    }

    /// <summary>选择工作区</summary>
    [RelayCommand]
    private async Task SelectWorkspaceAsync(WorkspaceProfile? workspace)
    {
        if (CurrentWorkspace?.Id == workspace?.Id)
        {
            IsWorkspaceFlyoutOpen = false;
            return;
        }

        // 会话的工作区归属应保持单一。已有消息时先归档并开启新会话，
        // 避免一段历史混入多个项目上下文后被错误归类。
        if (Messages.Count > 0)
        {
            var destination = workspace?.Name ?? GetString("Chat.Workspace.Global", "全局聊天（不使用工作区）");
            if (_userInteractionService == null || !await _userInteractionService.ConfirmAsync(
                GetString("Chat.Workspace.SwitchConfirm.Title", "切换工作区"),
                string.Format(
                    GetString("Chat.Workspace.SwitchConfirm.Message", "切换到“{0}”会先保存当前会话，并开启一个新会话，是否继续？"),
                    destination),
                GetString("Chat.Workspace.SwitchConfirm.Yes", "切换"),
                GetString("Chat.Workspace.SwitchConfirm.No", "取消"))) return;

            if (!await StartNewConversationForWorkspaceChangeAsync()) return;
        }

        CurrentWorkspace = workspace;
        IsWorkspaceFlyoutOpen = false;
    }

    private async Task<bool> StartNewConversationForWorkspaceChangeAsync()
    {
        await _conversationTransitionLock.WaitAsync();
        try
        {
            IsResetting = true;
            BeginConversationTransition();
            var stagedSnapshot = await TryStageCurrentConversationForTransitionAsync();
            if (stagedSnapshot == TransitionStageResult.Failed) return false;

            ResetConversationState();
            return true;
        }
        finally
        {
            IsResetting = false;
            _conversationTransitionLock.Release();
        }
    }

    /// <summary>添加工作区（弹出文件夹选择器）</summary>
    [RelayCommand]
    private async Task AddWorkspaceAsync()
    {
        if (_workspaceService == null) return;

        var dirPath = _userInteractionService == null
            ? null
            : await _userInteractionService.PickFolderAsync(GetString("Chat.Workspace.SelectFolder", "选择工作区目录"));
        if (string.IsNullOrWhiteSpace(dirPath)) return;

        // 检查是否已存在同目录的工作区
        var existing = await _workspaceService.FindByDirectoryAsync(dirPath);
        if (existing != null)
        {
            await SelectWorkspaceAsync(existing);
            return;
        }

        // 以目录名作为工作区名
        var name = System.IO.Path.GetFileName(dirPath.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(name)) name = "Workspace";

        var workspace = new WorkspaceProfile
        {
            Name = name,
            DirectoryPath = dirPath
        };

        await _workspaceService.SaveAsync(workspace);
        AvailableWorkspaces.Add(workspace);
        await SelectWorkspaceAsync(workspace);
    }

    /// <summary>删除工作区</summary>
    [RelayCommand]
    private async Task RemoveWorkspaceAsync(WorkspaceProfile? workspace)
    {
        if (workspace == null || _workspaceService == null) return;

        if (_userInteractionService == null || !await _userInteractionService.ConfirmAsync(
            GetString("Chat.Workspace.RemoveConfirm.Title", "移除工作区"),
            string.Format(
                GetString("Chat.Workspace.RemoveConfirm.Message", "确定要移除“{0}”吗？其工作区知识文件将被永久删除；会话历史会保留为未分组记录。"),
                workspace.Name),
            GetString("Chat.Workspace.RemoveConfirm.Yes", "移除"),
            GetString("Chat.Workspace.RemoveConfirm.No", "取消"))) return;

        var isCurrentWorkspace = CurrentWorkspace?.Id == workspace.Id;
        if (isCurrentWorkspace && Messages.Count > 0
            && !await StartNewConversationForWorkspaceChangeAsync())
        {
            return;
        }

        if (!await _workspaceService.DeleteAsync(workspace.Id)) return;
        AvailableWorkspaces.Remove(workspace);

        if (isCurrentWorkspace)
        {
            CurrentWorkspace = null;
        }
    }

    #endregion

    public MainConversationViewModel() : this(null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null) { }

    public MainConversationViewModel(
        IChatService? chatService,
        IConfigService? configService,
        IContextCompressionService? contextCompressionService,
        IPromptService? promptService,
        ITaskScheduler? taskScheduler,
        IFunctionRegistry? functionRegistry,
        ITokenService? tokenService,
        ILocalizationService? localizationService,
        IAttachmentStoreService? attachmentStoreService = null,
        ISystemAudioService? systemAudioService = null,
        IConversationArchiveService? archiveService = null,
        IImageGenerationSessionService? imageGenerationSessionService = null,
        IScreenCaptureService? screenCaptureService = null,
        ISubAgentOrchestrator? subAgentOrchestrator = null,
        IWorkspaceService? workspaceService = null,
        IConversationSessionAccessor? sessionAccessor = null,
        IUserInteractionService? userInteractionService = null,
        ConversationExecutionCoordinator? executionCoordinator = null)
    {
        Orchestrator = subAgentOrchestrator;
        if (Orchestrator != null)
        {
            Orchestrator.ActiveAgents.CollectionChanged += OnActiveSubAgentsChanged;
            OnActiveSubAgentsChanged(null, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
        _chatService = chatService;
        _configService = configService;
        _contextCompressionService = contextCompressionService;
        _promptService = promptService;
        _taskScheduler = taskScheduler;
        _functionRegistry = functionRegistry;
        _tokenService = tokenService;
        _localizationService = localizationService;
        if (_localizationService != null)
        {
            _localizationService.LanguageChanged += OnLanguageChanged;
        }
        _attachmentStoreService = attachmentStoreService;
        _systemAudioService = systemAudioService;
        _archiveService = archiveService;
        _imageGenerationSessionService = imageGenerationSessionService;
        _screenCaptureService = screenCaptureService;
        _workspaceService = workspaceService;
        _sessionAccessor = sessionAccessor;
        _userInteractionService = userInteractionService;
        _executionCoordinator = executionCoordinator;

        // Initialize from config
        if (_configService != null)
        {
            var config = _configService.Load();
            if (_tokenService != null) _tokenService.MaxTokens = config.MaxContextTokens;
            CurrentModelName = config.AiModels.MainConversation.Model;
            CurrentTheme = config.Theme;
            ThemeIcon = config.Theme == "Dark" ? "Moon" : "Sun";
        }

        // 监听全局主题变更（来自应用设置或其他入口），同步按钮状态
        App.ThemeChanged += OnThemeChanged;
        Messages.CollectionChanged += OnMessagesCollectionChanged;

        // 监听配置变更（如在扩展页开启/关闭语音合成）：刷新各气泡「生成语音」按钮显隐。
        if (_configService != null)
        {
            _configService.ConfigChanged += OnConfigChanged;
        }

        PendingAttachments.CollectionChanged += OnPendingAttachmentsCollectionChanged;

        // 计算初始 Token（系统提示词和工具声明的基底开销）
        UpdateContextTokensDisplay();

        if (_archiveService != null)
        {
            _archiveService.ArchiveCompleted += OnArchiveCompleted;
            _archiveService.ArchiveFailed += OnArchiveFailed;
        }

        RestoreDraftIfNeeded();
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => RefreshToolCallSummaries();

    private void OnThemeChanged(string theme)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed) return;
            CurrentTheme = theme;
            ThemeIcon = theme == "Dark" ? "Moon" : "Sun";
        });
    }

    private void OnMessagesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_disposed || _isBulkLoadingMessages) return;
        UpdateContextTokensDisplay();
        UpdateBubbleButtonVisibility();
    }

    private void OnConfigChanged(object? sender, AppConfig config)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed) return;
            CurrentModelName = config.AiModels.MainConversation.Model;
            UpdateBubbleButtonVisibility();
            UpdateContextTokensDisplay();
        });
    }

    private void OnPendingAttachmentsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_disposed) return;
        OnPropertyChanged(nameof(HasPendingAttachments));
        SendMessageCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanSendMessage))]
    private async Task SendMessageAsync()
    {
        if (string.IsNullOrWhiteSpace(InputText) && PendingAttachments.Count == 0) return;

        // 在发送前检查是否需要压缩上下文
        if (_tokenService != null && _configService != null)
        {
            var config = _configService.Load();
            if (config.AutoCompress && _tokenService.CurrentTokens > config.CompressionThreshold && !IsCompressing)
            {
                _logger.Information("检测到 Token 超过阈值 ({Tokens} > {Threshold})，触发自动压缩", _tokenService.CurrentTokens, config.CompressionThreshold);
                await InternalCompressContextAsync();
            }
        }

        var userContent = InputText;
        var attachments = PendingAttachments.Select(CloneAttachmentForMessage).ToList();
        InputText = string.Empty;
        PendingAttachments.Clear();
        AttachmentStatusMessage = string.Empty;

        Messages.Add(new ChatMessage
        {
            Role = "user",
            Content = userContent,
            Attachments = new ObservableCollection<ChatAttachment>(attachments),
            Timestamp = DateTime.Now
        });

        UpdateConversationContext();

        // 先让出 UI 线程跑一次渲染，确保用户气泡立即出现，再去做后续较重的请求准备
        // （BuildMessages / token 估算 / 读取配置等），避免发送后约 1s 才看到气泡。
        await Task.Yield();

        await GetAiResponseAsync(userContent, addToContext: false);
    }

    private bool CanSendMessage() => CanAcceptAttachments && (!string.IsNullOrWhiteSpace(InputText) || PendingAttachments.Count > 0);

    private bool CanStopResponse() => IsSending;

    private bool CanModifyMessages() => !IsSending && !IsCompressing;

    [RelayCommand(CanExecute = nameof(CanStopResponse))]
    private void StopResponse()
    {
        if (!IsSending) return;
        _logger.Information("用户请求停止当前回复");
        _responseCts?.Cancel();
    }

    [RelayCommand]
    private void ToggleTheme()
    {
        CurrentTheme = CurrentTheme == "Dark" ? "Light" : "Dark";
        ThemeIcon = CurrentTheme == "Dark" ? "Moon" : "Sun";
        App.SetTheme(CurrentTheme);
        if (_configService != null)
        {
            var config = _configService.Load();
            config.Theme = CurrentTheme;
            _ = _configService.SaveAsync(config);
        }
    }

    /// <summary>
    /// 调试：进入「原始上下文」视图时，即时构建一次发送给主模型的 raw 消息快照。
    /// </summary>
    partial void OnIsRawContextViewChanged(bool value)
    {
        if (value)
        {
            RefreshRawContext();
        }
    }

    private void RefreshRawContext()
    {
        RawContextEntries.Clear();

        if (_chatService == null)
        {
            RawContextEntries.Add(new RawContextEntry
            {
                Header = "error",
                Text = GetString("Chat.Raw.ServiceUnavailable", "Chat service is unavailable.")
            });
            return;
        }

        try
        {
            foreach (var entry in _chatService.BuildRawContext(_currentContext))
            {
                RawContextEntries.Add(entry);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "构建 raw 上下文失败");
            RawContextEntries.Add(new RawContextEntry { Header = "error", Text = "构建 raw 上下文失败: " + ex.Message });
        }
    }

    private bool CanStartNewConversation() => !IsResetting;

    [RelayCommand(CanExecute = nameof(CanStartNewConversation))]
    private async Task NewConversationAsync()
    {
        await _conversationTransitionLock.WaitAsync();
        try
        {
            IsResetting = true;
            BeginConversationTransition();

            var stagedSnapshot = await TryStageCurrentConversationForTransitionAsync();
            if (stagedSnapshot == TransitionStageResult.Failed)
            {
                return;
            }

            ResetConversationState();
        }
        finally
        {
            IsResetting = false;
            _conversationTransitionLock.Release();
        }
    }

    /// <summary>
    /// 回滚到该条用户消息之前：删除该消息及其后所有内容，消息文本与附件回填输入区。
    /// 不保存被丢弃的对话，属破坏性操作，默认需确认。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanModifyMessages))]
    private async Task RewindToMessageAsync(ChatMessage? message)
    {
        if (message == null || message.Role != "user") return;

        int msgIndex = Messages.IndexOf(message);
        if (msgIndex < 0) return;

        var config = _configService?.Load();
        if (config?.SkipRewindConfirm != true)
        {
            var vm = new ConfirmDialogViewModel
            {
                Title = GetString("Chat.RewindConfirm.Title", "回滚确认"),
                Message = GetString("Chat.RewindConfirm.Message", "回滚将删除这条消息及其后的所有对话内容且不保存，消息内容将回填到输入框，是否继续？"),
                ConfirmText = GetString("Chat.RewindConfirm.Yes", "是"),
                CancelText = GetString("Chat.RewindConfirm.No", "否")
            };

            var dialog = new Views.ConfirmDialog(vm);
            var owner = GetMainWindow();
            if (owner == null) return;
            await dialog.ShowDialog(owner);

            if (vm.Result != true) return;

            if (vm.ShouldNotAskAgain && _configService != null)
            {
                var cfg = await _configService.LoadAsync();
                cfg.SkipRewindConfirm = true;
                await _configService.SaveAsync(cfg);
            }
        }

        // 回缩会移除后续（含刚生成的助手）消息：先取消后台语音生成并止播，
        // 避免语音挂到即将被删除的消息上或继续为已撤回内容播放。
        CancelPendingAudio();

        // 目标消息自身的附件回收到输入区（不删物理文件）；超出挂载上限的部分只能放弃并清理
        RestoreAttachmentsToPending(message.Attachments.ToList());
        message.Attachments.Clear();

        while (Messages.Count > msgIndex)
        {
            DeleteMessageAttachments(Messages[msgIndex]);
            Messages.RemoveAt(msgIndex);
        }

        InputText = message.Content;

        UpdateConversationContext();
        await ReconcileImageGenerationSessionAsync();
        // 回滚裁掉了后续消息：强制以估算刷新，下一轮真实 usage 会重锚。
        UpdateContextTokensDisplay(forceEstimateBaseline: true);
        UpdateBubbleButtonVisibility();
        _logger.Information("会话已回滚到消息 {MessageId} 之前", message.Id);
    }

    /// <summary>
    /// 从该条用户消息处 fork 分支：原会话完整保存为历史，当前会话换新身份，
    /// 保留 fork 点之前的上下文（附件物理克隆以与父历史解耦），fork 点消息回填输入区。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanModifyMessages))]
    private async Task ForkFromMessageAsync(ChatMessage? message)
    {
        if (message == null || message.Role != "user") return;

        int msgIndex = Messages.IndexOf(message);
        if (msgIndex < 0) return;

        // 分支会截断 fork 点之后的消息（含刚生成的助手回复）：先取消后台语音生成并止播，
        // 避免语音挂到即将被移除的消息或为已切走的分支继续播放。
        CancelPendingAudio();

        FinalizePendingAssistantMessages();
        msgIndex = Messages.IndexOf(message);
        if (msgIndex < 0) return;

        // 1. 父会话完整入历史（后台归档队列）
        var stageResult = await TryStageCurrentConversationForTransitionAsync();
        if (stageResult == TransitionStageResult.Failed)
        {
            return;
        }

        var parentConversationId = _conversationId;
        var parentHistoryId = _currentHistoryId;

        // 2. 分支换新身份，防止后续归档 upsert 覆盖父历史
        _conversationId = Guid.NewGuid().ToString("N");
        _currentContext.ConversationId = _conversationId;
        _currentHistoryId = null;
        _initialConversationSignature = null;
        _forkedFromConversationId = parentConversationId;
        _forkedFromHistoryId = parentHistoryId;
        _forkedAtMessageId = message.Id;

        // 3. fork 点消息附件克隆后回收到输入区（父历史仍引用原文件，分支必须持有独立副本）
        var pendingClones = await CloneAttachmentsForForkAsync(message.Attachments.ToList());
        RestoreAttachmentsToPending(pendingClones, deleteOverflowFiles: true);

        // 4. 截断：fork 点消息及其后全部移除（fork 点消息的原附件归父历史所有，不删文件）
        message.Attachments.Clear();
        Messages.RemoveAt(msgIndex);
        while (Messages.Count > msgIndex)
        {
            // 后续消息的附件同样归父历史所有，此处仅从分支移除，不删物理文件
            Messages.RemoveAt(msgIndex);
        }

        // 5. 保留的消息附件全部替换为物理克隆，与父历史彻底解耦
        var clonedById = new Dictionary<string, ChatAttachment>(StringComparer.Ordinal);
        if (_attachmentStoreService != null)
        {
            foreach (var kept in Messages)
            {
                for (int i = 0; i < kept.Attachments.Count; i++)
                {
                    var clone = await _attachmentStoreService.CloneStoredAttachmentAsync(kept.Attachments[i]);
                    clone.PreviewImage = kept.Attachments[i].PreviewImage;
                    clonedById[clone.Id] = clone;
                    kept.Attachments[i] = clone;
                }

                kept.ResolveSegmentAttachments();
            }
        }

        // 6. 图像会话复制到新会话身份，续作链路指向克隆后的文件
        await CopyImageSessionForForkAsync(parentConversationId, clonedById);

        InputText = message.Content;

        UpdateConversationContext();
        await ReconcileImageGenerationSessionAsync();
        // fork 出的分支是裁剪后的上下文：强制以估算刷新，下一轮真实 usage 会重锚。
        UpdateContextTokensDisplay(forceEstimateBaseline: true);
        UpdateBubbleButtonVisibility();
        _logger.Information(
            "已从会话 {ParentId} 的消息 {MessageId} 处 fork 出分支 {BranchId}",
            parentConversationId, message.Id, _conversationId);
    }

    /// <summary>
    /// 把一批附件挂回输入区待发送列表；超出上限的部分按需清理物理文件并提示。
    /// </summary>
    private void RestoreAttachmentsToPending(IReadOnlyList<ChatAttachment> attachments, bool deleteOverflowFiles = false)
    {
        if (attachments.Count == 0) return;

        foreach (var attachment in attachments)
        {
            attachment.IsPlaying = false;
            PendingAttachments.Add(attachment);
        }
    }

    private async Task<List<ChatAttachment>> CloneAttachmentsForForkAsync(IReadOnlyList<ChatAttachment> attachments)
    {
        var clones = new List<ChatAttachment>(attachments.Count);
        if (_attachmentStoreService == null)
        {
            return clones;
        }

        foreach (var attachment in attachments)
        {
            var clone = await _attachmentStoreService.CloneStoredAttachmentAsync(attachment);
            clone.PreviewImage = attachment.PreviewImage;
            clones.Add(clone);
        }

        return clones;
    }

    /// <summary>
    /// fork 时把父会话的图像生成会话复制到分支的新 conversationId 下，
    /// 并把续作链路的文件指针改到分支自己的附件克隆上。
    /// </summary>
    private async Task CopyImageSessionForForkAsync(
        string parentConversationId,
        IReadOnlyDictionary<string, ChatAttachment> clonedById)
    {
        if (_imageGenerationSessionService == null) return;

        var parentSnapshot = await _imageGenerationSessionService.CreateSnapshotAsync(parentConversationId);
        if (parentSnapshot == null) return;

        var branchSnapshot = new ImageGenerationSessionSnapshot
        {
            ConversationId = _conversationId,
            HistoryId = null,
            ActiveLineageId = parentSnapshot.ActiveLineageId,
            CreatedAt = parentSnapshot.CreatedAt,
            UpdatedAt = DateTime.Now,
            Turns = parentSnapshot.Turns.Select(turn => new ImageGenerationTurnRecord
            {
                Id = turn.Id,
                LineageId = turn.LineageId,
                ParentTurnId = turn.ParentTurnId,
                Prompt = turn.Prompt,
                RevisedPrompt = turn.RevisedPrompt,
                AttachmentId = turn.AttachmentId,
                FileName = turn.FileName,
                StoredPath = clonedById.TryGetValue(turn.AttachmentId, out var clone)
                    ? clone.StoredPath ?? turn.StoredPath
                    : turn.StoredPath,
                MimeType = turn.MimeType,
                ContinuityMode = turn.ContinuityMode,
                ContinuityStatus = turn.ContinuityStatus,
                Warning = turn.Warning,
                CreatedAt = turn.CreatedAt
            }).ToList()
        };

        await _imageGenerationSessionService.PersistSnapshotAsync(branchSnapshot);
    }

    [RelayCommand]
    private void CopyMessage(ChatMessage? message)
    {
        if (message != null)
        {
            if (App.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var clipboard = TopLevel.GetTopLevel(desktop.MainWindow)?.Clipboard;
                clipboard?.SetTextAsync(message.Content ?? string.Empty);
                _logger.Debug("Copying message content to clipboard");
            }
        }
    }

    [RelayCommand]
    private async Task OpenImageAttachmentAsync(ChatAttachment? attachment)
    {
        if (attachment == null || !attachment.IsImage || string.IsNullOrWhiteSpace(attachment.StoredPath))
        {
            return;
        }

        if (_userInteractionService != null)
            await _userInteractionService.ShowImagePreviewAsync(attachment);
    }

    /// <summary>
    /// 获取主窗口用于弹窗
    /// </summary>
    private Window? GetMainWindow()
    {
        if (App.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            return desktop.MainWindow;
        }
        return null;
    }

    [RelayCommand]
    private async Task AttachFileAsync()
    {
        if (!CanAcceptAttachments) return;

        if (_attachmentStoreService == null)
        {
            AttachmentStatusMessage = GetString("Chat.Attach.ServiceUnavailable", "Attachment service is unavailable.");
            return;
        }

        var owner = GetMainWindow();
        var storageProvider = owner?.StorageProvider;
        if (storageProvider == null)
        {
            AttachmentStatusMessage = GetString("Chat.Attach.NoStorageProvider", "File picker is unavailable.");
            return;
        }

        var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = GetString("Chat.Attach.SelectFiles", "Select files"),
            AllowMultiple = true
        });

        await AddStorageFilesAsync(files);
    }

    [RelayCommand]
    private void RemovePendingAttachment(ChatAttachment? attachment)
    {
        if (attachment == null) return;

        if (PendingAttachments.Remove(attachment))
        {
            _attachmentStoreService?.DeleteStoredAttachment(attachment);
            ReleaseAttachmentPreviews([attachment]);
            AttachmentStatusMessage = string.Empty;
            SendMessageCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>
    /// 在后台将文档附件解析为 Markdown 文本（上传 + 轮询），并实时更新卡片状态。
    /// 解析完成前会阻止发送，确保附带的文档内容随消息一并送达 AI。
    /// </summary>

    public async Task AddStorageFilesAsync(IEnumerable<IStorageFile> files)
    {
        if (_attachmentStoreService == null || !CanAcceptAttachments) return;

        try
        {
            var imported = await _attachmentStoreService.ImportFilesAsync(files);
            foreach (var attachment in imported)
            {
                PendingAttachments.Add(attachment);
            }
            AttachmentStatusMessage = string.Empty;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "添加附件失败");
            AttachmentStatusMessage = ToAttachmentErrorMessage(ex);
        }
    }

    private bool _isCapturingScreenshot;

    // Windows 异步回退路径的后台剪贴板轮询；新一次截图启动时取消旧轮询。
    private CancellationTokenSource? _screenshotBackgroundPollCts;

    /// <summary>
    /// 调用系统原生截图工具（框选/裁剪/标注），完成后从剪贴板取回图片并作为待发送附件。
    /// </summary>
    /// <param name="mode">
    /// "keep" = 截图时保留本窗口（可截 AthenaAgent 自身）；其余值（含 null）= 截图时隐藏本窗口（截其它内容）。
    /// </param>
    [RelayCommand]
    private async Task CaptureScreenshotAsync(string? mode)
    {
        if (_isCapturingScreenshot) return;
        if (IsSending || IsCompressing) return;

        if (_screenCaptureService == null || !_screenCaptureService.IsSupported)
        {
            AttachmentStatusMessage = GetString("Chat.Screenshot.Unsupported", "Screenshot is not supported on this platform.");
            return;
        }

        var clipboard = TopLevel.GetTopLevel(GetMainWindow())?.Clipboard;
        if (clipboard == null)
        {
            AttachmentStatusMessage = GetString("Chat.Screenshot.Failed", "Failed to capture screenshot.");
            return;
        }

        // 默认隐藏窗口；mode == "keep" 时保留窗口，从而可以截到 AthenaAgent 自身。
        var hideWindow = !string.Equals(mode, "keep", StringComparison.OrdinalIgnoreCase);

        // 若上一次异步回退路径的后台轮询仍在运行，先取消，避免其抢走本次结果或误还原窗口。
        _screenshotBackgroundPollCts?.Cancel();
        _screenshotBackgroundPollCts = null;

        _isCapturingScreenshot = true;
        var mainWindow = GetMainWindow();
        var previousState = mainWindow?.WindowState ?? WindowState.Normal;
        var minimized = false;
        var handedOffToBackground = false;

        void RestoreWindow()
        {
            if (!minimized || mainWindow == null) return;
            mainWindow.WindowState = previousState;
            mainWindow.Activate();
            minimized = false;
        }

        try
        {
            AttachmentStatusMessage = string.Empty;

            // 以"清空剪贴板 → 截图 → 读取新位图"判定结果，避免误取旧剪贴板内容。
            try { await clipboard.ClearAsync(); } catch { /* 某些平台清空可能失败，忽略 */ }

            // 隐藏模式：最小化窗口以免遮挡截图目标；保留模式：原样显示，让截图浮层覆盖在窗口之上。
            if (hideWindow && mainWindow != null)
            {
                mainWindow.WindowState = WindowState.Minimized;
                await Task.Delay(250); // 等待最小化动画完成
                minimized = true;
            }

            var launch = await _screenCaptureService.LaunchInteractiveAsync();

            if (launch == ScreenCaptureLaunchResult.Failed || launch == ScreenCaptureLaunchResult.Unsupported)
            {
                AttachmentStatusMessage = GetString("Chat.Screenshot.Failed", "Failed to capture screenshot.");
                return;
            }

            if (launch == ScreenCaptureLaunchResult.Cancelled)
            {
                // 用户在截图工具中取消：立即结束、即时恢复按钮。个别工具的退出码语义
                // 不完全可靠（可能先复制再非零退出），补一次快速读取兜底。
                RestoreWindow();
                var quick = await clipboard.TryGetBitmapAsync();
                if (quick != null)
                {
                    await AddClipboardBitmapAsync(quick);
                }
                return;
            }

            if (launch == ScreenCaptureLaunchResult.CompletedBlocking)
            {
                // 截图交互已结束（mac/linux，以及 Windows 监听到覆盖层进程退出）：
                // 立即还原窗口，用短促快轮询容忍剪贴板写入延迟；成功时通常首次即命中，
                // 取消时最多 ~1.5 秒即恢复按钮。
                RestoreWindow();
                Bitmap? bitmap = null;
                for (var i = 0; i < 10 && bitmap == null; i++)
                {
                    bitmap = await clipboard.TryGetBitmapAsync();
                    if (bitmap == null)
                    {
                        await Task.Delay(150);
                    }
                }

                if (bitmap != null)
                {
                    await AddClipboardBitmapAsync(bitmap);
                }
                return;
            }

            // 异步型（Windows 未捕获到覆盖层进程的回退路径）：完成时机未知，把长轮询
            // 移入后台任务并立即归还命令，让按钮即时恢复可用；窗口保持隐藏，直到后台
            // 取回图片（或超时/被新一次截图接管）再还原。
            var cts = new CancellationTokenSource();
            _screenshotBackgroundPollCts = cts;
            handedOffToBackground = true;
            _ = PollScreenshotClipboardInBackgroundAsync(clipboard, RestoreWindow, cts);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "截图失败");
            AttachmentStatusMessage = GetString("Chat.Screenshot.Failed", "Failed to capture screenshot.");
        }
        finally
        {
            if (!handedOffToBackground)
            {
                RestoreWindow();
            }
            _isCapturingScreenshot = false;
        }
    }

    /// <summary>
    /// Windows 异步回退路径的后台剪贴板长轮询：不占用截图命令（按钮已恢复可用），
    /// 取到图片或超时后再还原窗口；若被新一次截图取消，则窗口交由新流程管理。
    /// </summary>
    private async Task PollScreenshotClipboardInBackgroundAsync(
        IClipboard clipboard, Action restoreWindow, CancellationTokenSource cts)
    {
        try
        {
            Bitmap? bitmap = null;
            for (var i = 0; i < 200 && bitmap == null; i++)
            {
                if (cts.IsCancellationRequested) return;
                bitmap = await clipboard.TryGetBitmapAsync();
                if (bitmap == null)
                {
                    await Task.Delay(300, cts.Token);
                }
            }

            restoreWindow();
            if (bitmap != null)
            {
                await AddClipboardBitmapAsync(bitmap);
            }
        }
        catch (OperationCanceledException)
        {
            // 被新一次截图接管，静默退出。
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "后台轮询截图剪贴板失败");
        }
        finally
        {
            if (ReferenceEquals(_screenshotBackgroundPollCts, cts))
            {
                _screenshotBackgroundPollCts = null;
            }
            cts.Dispose();
        }
    }

    public async Task AddClipboardBitmapAsync(Bitmap bitmap)
    {
        if (_attachmentStoreService == null || !CanAcceptAttachments) return;

        try
        {
            var attachment = await _attachmentStoreService.ImportBitmapAsync(
                bitmap,
                $"clipboard-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            PendingAttachments.Add(attachment);
            AttachmentStatusMessage = string.Empty;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "粘贴图片失败");
            AttachmentStatusMessage = ToAttachmentErrorMessage(ex);
        }
    }

    /// <summary>
    /// 处理来自调度器的主动消息
    /// </summary>
    public async Task<TaskExecutionResult> ProcessProactiveMessageAsync(string intent)
    {
        if (_chatService == null || _promptService == null)
        {
            _logger.Warning("忽略主动消息触发：服务未初始化");
            return TaskExecutionResult.Failed("Chat service or prompt service is not available.");
        }

        if (IsSending || IsCompressing)
        {
            _logger.Warning("延后主动消息触发：当前正忙 (IsSending={IsSending}, IsCompressing={IsCompressing})", IsSending, IsCompressing);
            return TaskExecutionResult.Busy("Foreground chat is busy.");
        }

        _logger.Information("开始处理主动消息逻辑: {Intent}", intent);

        // 构造主动触发指令
        var proactivePrompt = _promptService.GetProactiveMessagePrompt(intent, DateTime.Now);

        // 重要：为了绕过大多数 LLM API 不允许以 System 消息结尾或纯 System 消息序列的限制，
        // 我们将主动指令作为一条“隐藏的用户消息”注入。
        var triggerMsg = new ChatMessage
        {
            Role = "user",
            Content = proactivePrompt,
            IsHidden = true, // 在 UI 中不可见
            Timestamp = DateTime.Now
        };

        Messages.Add(triggerMsg);

        // 确保上下文包含这条新消息
        UpdateConversationContext();

        // 触发 AI 响应（addToContext 为 false 因为我们已经手动添加到 Messages 列表并更新了 Context）
        return await GetAiResponseAsync(string.Empty, addToContext: false);
    }

    private void BeginConversationTransition()
    {
        Interlocked.Increment(ref _conversationEpoch);
        _responseCts?.Cancel();
        CancelPendingPreviewLoading();
        CancelPendingAudio();
        FinalizePendingAssistantMessages();
        IsSending = false;
        UpdateConversationContext();
        UpdateContextTokensDisplay();
        UpdateBubbleButtonVisibility();
    }

    private async Task<ConversationArchiveSnapshot?> CaptureArchiveSnapshotIfNeededAsync()
    {
        if (!IsConversationModified())
        {
            return null;
        }

        var messages = Messages
            .Where(ConversationPersistenceHelper.ShouldPersistMessage)
            .Select(ConversationPersistenceHelper.CloneMessage)
            .ToList();

        if (messages.Count == 0)
        {
            return null;
        }

        ImageGenerationSessionSnapshot? imageSessionSnapshot = null;
        if (_imageGenerationSessionService != null)
        {
            imageSessionSnapshot = await _imageGenerationSessionService.CreateSnapshotAsync(_conversationId);
        }

        return new ConversationArchiveSnapshot
        {
            ConversationId = _conversationId,
            HistoryId = _currentHistoryId,
            ContextSummary = _activeContextSummary,
            ForkedFromConversationId = _forkedFromConversationId,
            ForkedFromHistoryId = _forkedFromHistoryId,
            ForkedAtMessageId = _forkedAtMessageId,
            WorkspaceId = _currentContext.WorkspaceId,
            Messages = messages,
            ImageSession = imageSessionSnapshot,
            CapturedAt = DateTime.Now,
            ForceGenerateSummary = true
        };
    }

    private void ResetConversationState(bool clearMessages = true)
    {
        ReleaseAttachmentPreviews(Messages.SelectMany(message => message.Attachments));
        if (clearMessages)
        {
            Messages.Clear();
        }
        InputText = string.Empty;
        ClearPendingAttachments(deleteStoredFiles: true);
        if (_imageGenerationSessionService != null)
        {
            _imageGenerationSessionService.DeleteAsync(_conversationId).GetAwaiter().GetResult();
        }
        _currentContext.Reset();
        _conversationId = Guid.NewGuid().ToString("N");
        _currentContext.ConversationId = _conversationId;
        _currentHistoryId = null;
        _initialConversationSignature = null;
        _forkedFromConversationId = null;
        _forkedFromHistoryId = null;
        _forkedAtMessageId = null;

        _compressionHistory.Clear();
        SetActiveContextSummary(null);
        UndoCompressionCommand.NotifyCanExecuteChanged();

        _archiveService?.DeleteDraft();
        // 新会话：清空真实用量锚点，避免沿用上一会话的过时数值。
        _tokenService?.ResetUsage();
        UpdateConversationContext();
        UpdateContextTokensDisplay();
        UpdateBubbleButtonVisibility();
    }

    private void FinalizePendingAssistantMessages()
    {
        foreach (var message in Messages.Where(m => m.Role == "assistant" && m.IsLoading).ToList())
        {
            var shouldRemove = !ConversationPersistenceHelper.ShouldPersistMessage(message);
            message.IsLoading = false;
            message.ToolExecutionSummary = string.Empty;

            if (shouldRemove)
            {
                DeleteMessageAttachments(message);
                Messages.Remove(message);
            }
        }
    }

    private bool IsCurrentConversationEpoch(int epoch)
    {
        return epoch == Volatile.Read(ref _conversationEpoch);
    }

    private async Task<TaskExecutionResult> GetAiResponseAsync(string input, bool addToContext = true)
    {
        if (_chatService == null)
        {
            return TaskExecutionResult.Failed("Chat service is not available.");
        }

        var epoch = Volatile.Read(ref _conversationEpoch);
        _responseCts?.Dispose();
        var responseCts = new CancellationTokenSource();
        _responseCts = responseCts;
        var cancellationToken = responseCts.Token;
        var requestContext = _currentContext.Clone();
        requestContext.ConversationId = _conversationId;
        var outcome = TaskExecutionResult.Succeeded();

        IsSending = true;
        ConversationExecutionCoordinator.Lease? executionLease = null;
        _forceNewAssistantTextSegment = false;
        var modelSettings = _configService?.Load().AiModels.MainConversation;
        var assistantMsg = new ChatMessage
        {
            Role = "assistant",
            Content = string.Empty,
            Timestamp = DateTime.Now,
            ProviderId = modelSettings?.ProviderId,
            ModelId = modelSettings?.Model,
            IsLoading = true,
            IsStreaming = true
        };
        Messages.Add(assistantMsg);

        try
        {
            if (_executionCoordinator != null)
            {
                IsQueued = true;
                try
                {
                    executionLease = await _executionCoordinator.AcquireAsync(_conversationId, cancellationToken);
                }
                finally
                {
                    IsQueued = false;
                }
            }

            await foreach (var contentDelta in _chatService.StreamMessageAsync(
                input,
                requestContext,
                cancellationToken: cancellationToken,
                onMessageAdded: msg =>
                {
                    if (!IsCurrentConversationEpoch(epoch))
                    {
                        return;
                    }

                    if (msg.Role == "assistant" && !string.IsNullOrEmpty(msg.ToolCallsJson))
                    {
                        // 真实的工具调用回合保存在隐藏消息里，并插入到活动气泡之前，
                        // 保证最终回复气泡始终在 Messages 末尾、排在 tool_call/tool 之后。
                        msg.IsHidden = true;
                        InsertBeforeActiveBubble(assistantMsg, msg);

                        // 把本轮的阶段性正文固化为可见段并清空 Content（去重），保留所有可见段
                        CommitActiveBubbleRound(assistantMsg);

                        // 为本轮的每个工具调用追加一张「执行中」卡片。
                        // 先补卡片、再关闭 loading：切换过程中气泡始终有可见内容承接，避免空气泡塌缩。
                        assistantMsg.ToolExecutionSummary = string.Empty;
                        AddToolCallEntries(assistantMsg, msg.ToolCallsJson);
                        assistantMsg.IsComposingFileText = false;
                        assistantMsg.IsLoading = false;
                    }
                    else if (msg.Role == "assistant" && !string.IsNullOrEmpty(msg.ReasoningContent))
                    {
                        assistantMsg.ReasoningContent = msg.ReasoningContent;
                    }
                    else if (msg.Role == "assistant" && msg.Attachments.Count > 0)
                    {
                        var attachments = msg.Attachments
                            .Select(CloneAttachmentForMessage)
                            .ToList();

                        foreach (var attachment in attachments)
                        {
                            assistantMsg.Attachments.Add(attachment);
                        }

                        assistantMsg.NotifyAttachmentsChanged();

                        var generatedImageAttachments = attachments
                            .Where(attachment => attachment.IsImage)
                            .ToList();

                        if (generatedImageAttachments.Count > 0)
                        {
                            EnsureSegmentLayout(assistantMsg);

                            foreach (var attachment in generatedImageAttachments)
                            {
                                assistantMsg.Segments.Add(new ChatMessageSegment
                                {
                                    Kind = ChatMessageSegmentKind.GeneratedImage,
                                    AttachmentId = attachment.Id,
                                    Attachment = attachment
                                });
                            }

                            assistantMsg.NotifySegmentsChanged();
                        }

                        if (!string.IsNullOrWhiteSpace(msg.OutputAudioReferenceId))
                        {
                            assistantMsg.OutputAudioReferenceId = msg.OutputAudioReferenceId;
                        }

                        if (!string.IsNullOrWhiteSpace(msg.AudioErrorMessage))
                        {
                            assistantMsg.AudioErrorMessage = msg.AudioErrorMessage;
                        }

                        assistantMsg.IsLoading = false;

                        var audioAttachment = attachments.FirstOrDefault(attachment => attachment.IsAudio);
                        if (audioAttachment != null)
                        {
                            TryAutoPlayAssistantAudio(audioAttachment, assistantMsg);
                        }
                    }
                    else if (msg.Role == "assistant" && (!string.IsNullOrWhiteSpace(msg.AudioErrorMessage) || !string.IsNullOrWhiteSpace(msg.OutputAudioReferenceId)))
                    {
                        assistantMsg.OutputAudioReferenceId = msg.OutputAudioReferenceId;
                        assistantMsg.AudioErrorMessage = msg.AudioErrorMessage;
                        assistantMsg.IsLoading = false;
                    }
                    else if (msg.Role == "tool")
                    {
                        InsertBeforeActiveBubble(assistantMsg, msg);

                        // 把工具结果回填到对应卡片（按 ToolCallId 匹配），并标记成功/失败
                        CompleteToolCallEntry(assistantMsg, msg.ToolCallId, msg.ToolName, msg.Content);

                        // 工具执行完毕，等待大模型下一步指示——保留思考动画
                        assistantMsg.ToolExecutionSummary = string.Empty;
                        assistantMsg.IsComposingFileText = false;
                        assistantMsg.IsLoading = true;
                    }
                },
                onContextCompressed: (summary, count) =>
                {
                    if (!IsCurrentConversationEpoch(epoch))
                    {
                        return;
                    }

                    // 捕获压缩前摘要，供撤销还原
                    var previousSummary = _activeContextSummary;

                    // 同步 UI 消息状态：标记前 count 条当前未压缩的消息为已压缩，并收集引用入撤销栈
                    var batch = new List<ChatMessage>(count);
                    foreach (var m in Messages)
                    {
                        if (!m.IsCompressed)
                        {
                            m.IsCompressed = true;
                            batch.Add(m);
                            if (batch.Count >= count) break;
                        }
                    }
                    if (batch.Count > 0)
                    {
                        _compressionHistory.Push(new CompressionCheckpoint(previousSummary, batch));
                    }
                    SetActiveContextSummary(summary);
                    // 上下文被压缩变小：强制以估算刷新显示，下一轮真实 usage 会自动重锚。
                    UpdateContextTokensDisplay(forceEstimateBaseline: true);
                    UpdateBubbleButtonVisibility();
                    UndoCompressionCommand.NotifyCanExecuteChanged();
                    _logger.Information("检测到中间压缩，UI 已同步标记 {Count} 条消息", count);
                },
                onUsageReported: usage =>
                {
                    if (!IsCurrentConversationEpoch(epoch)) return;
                    // 供应商回报的真实用量：作为上下文占用的权威锚点，覆盖此前一切估算。
                    _tokenService?.ApplyUsage(usage);
                    OnPropertyChanged(nameof(ContextTokensInfo));
                },
                onToolCallArgumentsStreaming: functionName =>
                {
                    if (!IsCurrentConversationEpoch(epoch)
                        || (functionName != "write_system_file" && functionName != "modify_system_file"))
                    {
                        return;
                    }

                    assistantMsg.IsComposingFileText = true;
                    assistantMsg.IsLoading = true;
                },
                addToContext: addToContext))
            {
                if (!IsCurrentConversationEpoch(epoch))
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(contentDelta))
                {
                    // 先写入正文、再关闭 loading：切换过程中气泡始终有可见内容承接，避免空气泡塌缩。
                    assistantMsg.ToolExecutionSummary = string.Empty; // 开始输出正式回复，隐藏工具调用状态
                    assistantMsg.IsComposingFileText = false;
                    assistantMsg.Content += contentDelta;
                    AppendAssistantMarkdownSegment(assistantMsg, contentDelta);
                    assistantMsg.IsLoading = false; // 收到文字后停止 loading 动画
                }
            }

            if (!IsCurrentConversationEpoch(epoch))
            {
                return TaskExecutionResult.Interrupted("Conversation context changed.");
            }

            UpdateConversationContext();

            // 回复结束触发图标闪烁
            if (string.IsNullOrEmpty(assistantMsg.ToolCallsJson) && !string.IsNullOrEmpty(assistantMsg.Content))
            {
                App.StartTrayFlashing();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (IsCurrentConversationEpoch(epoch))
            {
                _logger.Information("当前回复已停止");
            }
            outcome = TaskExecutionResult.Interrupted("Response was interrupted.");
        }
        catch (Exception ex)
        {
            if (!IsCurrentConversationEpoch(epoch))
            {
                return TaskExecutionResult.Interrupted("Conversation context changed.");
            }

            _logger.Error(ex, "Get AI response failed");
            assistantMsg.IsLoading = false;
            assistantMsg.IsComposingFileText = false;
            assistantMsg.ToolExecutionSummary = string.Empty;
            assistantMsg.Content = ToChatErrorMessage(ex);
            outcome = TaskExecutionResult.Failed(ex.Message);
        }
        finally
        {
            executionLease?.Dispose();
            if (ReferenceEquals(_responseCts, responseCts))
            {
                _responseCts = null;
            }

            responseCts.Dispose();

            if (IsCurrentConversationEpoch(epoch))
            {
                // 回复生命周期结束：先落内容态，最后统一撤下 loading/streaming 生命线，气泡去留由下方清理判定。
                assistantMsg.IsLoading = false;
                assistantMsg.IsComposingFileText = false;
                assistantMsg.IsStreaming = false;
                assistantMsg.ToolExecutionSummary = string.Empty;

                // 输出结束（成功/停止/报错均经此）：自动收起工具调用组（露 3 个 + peek），尊重用户手动操作
                CollapseToolGroups(assistantMsg);

                // Cleanup the empty main assistant message if it didn't generate any text and didn't call tools directly
                if (string.IsNullOrWhiteSpace(assistantMsg.Content)
                    && string.IsNullOrEmpty(assistantMsg.ToolCallsJson)
                    && string.IsNullOrEmpty(assistantMsg.ReasoningContent)
                    && !assistantMsg.HasSegments)
                {
                    if (assistantMsg.Attachments.Count == 0 && string.IsNullOrWhiteSpace(assistantMsg.AudioErrorMessage))
                    {
                        Messages.Remove(assistantMsg);
                    }
                }

                UpdateConversationContext();
                await ReconcileImageGenerationSessionAsync();
                IsSending = false;
                UpdateContextTokensDisplay();
                UpdateBubbleButtonVisibility();

                // 文本回复已结束、发送态已解除：语音在后台异步生成，不阻塞任何交互。
                // 仅对成功产出正文、非报错/中断的终态气泡生成语音。
                if (outcome.Outcome == TaskExecutionOutcome.Succeeded
                    && !string.IsNullOrWhiteSpace(assistantMsg.Content)
                    && string.IsNullOrEmpty(assistantMsg.ToolCallsJson)
                    && Messages.Contains(assistantMsg))
                {
                    _ = RunAudioGenerationAsync(assistantMsg, epoch, forcePlay: false);
                }
            }

            await NotifySchedulerAvailabilityAsync();
        }

        return outcome;
    }

    /// <summary>
    /// 设置当前会话的压缩摘要（唯一真源），并同步到 TokenService 的展示镜像。
    /// </summary>
    private void SetActiveContextSummary(string? summary)
    {
        _activeContextSummary = string.IsNullOrEmpty(summary) ? null : summary;
        if (_tokenService != null)
        {
            _tokenService.CompressionPreview = _activeContextSummary ?? string.Empty;
        }
    }

    private void UpdateConversationContext()
    {
        _currentContext.Clear();
        _currentContext.ConversationId = _conversationId;

        // 赋予当前的压缩摘要（如果有）——读会话级真源，而非 UI 单例
        _currentContext.SetSummary(string.IsNullOrEmpty(_activeContextSummary) ? null : _activeContextSummary);

        foreach (var msg in Messages)
        {
            // 已被压缩归档的消息不再进入发送给大模型的 context.messages 列表
            if (msg.IsCompressed) continue;

            if (msg.Role == "user")
            {
                _currentContext.AddUserMessage(msg.Content, msg.Timestamp, msg.Attachments);
            }
            else if (msg.Role == "assistant")
            {
                // 仅添加有内容的助手消息
                if (!string.IsNullOrEmpty(msg.Content)
                    || !string.IsNullOrEmpty(msg.ToolCallsJson)
                    || !string.IsNullOrEmpty(msg.ReasoningContent)
                    || msg.Attachments.Count > 0
                    || !string.IsNullOrEmpty(msg.OutputAudioReferenceId))
                {
                    _currentContext.AddAssistantMessage(
                        msg.Content,
                        msg.ToolCallsJson,
                        msg.ReasoningContent,
                        msg.Attachments,
                        msg.OutputAudioReferenceId);
                }
            }
            else if (msg.Role == "tool")
            {
                _currentContext.AddToolMessage(msg.Content, msg.ToolCallId);
            }
        }
    }

    private async Task ReconcileImageGenerationSessionAsync()
    {
        if (_imageGenerationSessionService == null)
        {
            return;
        }

        var survivingAttachmentIds = Messages
            .SelectMany(msg => msg.Attachments)
            .Where(attachment => attachment.IsImage)
            .Select(attachment => attachment.Id)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        await _imageGenerationSessionService.ReconcileAsync(_conversationId, survivingAttachmentIds);
    }

    /// <summary>
    /// 刷新上下文 token 显示的「估算兜底」。
    /// 真实 usage 锚点存在时（<see cref="ITokenService.IsRealUsage"/>），估算不覆盖真实值；
    /// 仅在冷启动/供应商不回 usage 时提供显示。
    /// </summary>
    /// <param name="forceEstimateBaseline">
    /// true 时强制以估算作为基准（用于压缩/回滚/fork——上下文已改却未发 API，需立即反映新大小），
    /// 下一次真实响应会自动重新锚定。
    /// </param>
    public void UpdateContextTokensDisplay(bool forceEstimateBaseline = false)
    {
        if (_tokenService == null || _promptService == null || _functionRegistry == null) return;
        var config = _configService?.Load();
        var functionCallingEnabled = _functionRegistry.HasFunctions;

        // persona + 工具声明是估算兜底的固定开销来源（真实请求的 system 消息在 ChatService 内另行构建）。
        var systemPrompt = _promptService.GetPrompt(PromptType.MainPersona);
        if (functionCallingEnabled)
        {
            systemPrompt = _promptService.GetPrompt(PromptType.ToolCallingPolicy) + "\n\n---\n\n" + systemPrompt;
        }

        // 工作区知识是全量写入 system prompt 的内容，必须按实际拼合后的文本计入估算；
        // 不能直接加配置预算，否则空/短知识库都会被高估。
        if (_workspaceService != null
            && !string.IsNullOrEmpty(_currentContext.WorkspaceId)
            && !string.IsNullOrEmpty(_currentContext.WorkspaceDirectoryPath))
        {
            systemPrompt += $"\n\n---\n\n## Current Workspace\nProject Directory: {_currentContext.WorkspaceDirectoryPath}";
            if (!string.IsNullOrEmpty(_currentContext.WorkspaceKnowledgeFilePath))
            {
                systemPrompt += $"\nWorkspace Knowledge File: {_currentContext.WorkspaceKnowledgeFilePath}\nUse modify_system_file to update this system-managed file. Do not create additional workspace knowledge files.";
            }

            var workspaceKnowledgeBudget = config?.WorkspaceKnowledgeTokenBudget ?? 2000;
            var workspaceKnowledge = _workspaceService.BuildWorkspaceKnowledgeContext(
                _currentContext.WorkspaceId,
                _currentContext.WorkspaceKnowledgeFilePath,
                workspaceKnowledgeBudget);
            if (!string.IsNullOrEmpty(workspaceKnowledge))
            {
                systemPrompt += $"\n\n---\n\n## Workspace Knowledge\n{workspaceKnowledge}";
            }
        }

        _currentContext.SetMainPersona(systemPrompt);
        _currentContext.ToolsDeclarationTokenCount = functionCallingEnabled
            ? _functionRegistry.GetToolDeclarationTokenCount()
            : 0;

        int estimated = _currentContext.EstimatedTokenCount;

        if (forceEstimateBaseline)
        {
            _tokenService.ApplyEstimatedBaseline(estimated);
        }
        else
        {
            _tokenService.RefreshEstimate(estimated);
        }

        OnPropertyChanged(nameof(ContextTokensInfo));
    }

    private void UpdateBubbleButtonVisibility()
    {
        // 语音功能开关回填到每条消息，驱动「生成语音」按钮显隐。
        // 放在早退之前：发送/压缩中也应保持该状态正确。
        var audioEnabled = _configService?.Load().ChatAudioEnabled == true;
        foreach (var msg in Messages)
        {
            msg.CanRewind = false;
            msg.AudioFeatureEnabled = audioEnabled;
        }

        // 发送中或压缩中，所有操作按钮不可用
        if (IsSending || IsCompressing || Messages.Count == 0) return;

        foreach (var msg in Messages)
        {
            // 已归档的消息不可回滚/fork
            if (!msg.IsCompressed && msg.Role == "user")
            {
                msg.CanRewind = true;
            }
        }
    }

    public async Task RefreshSettingsAsync()
    {
        if (_configService != null)
        {
            var config = await _configService.LoadAsync();
            if (_tokenService != null) _tokenService.MaxTokens = config.MaxContextTokens;
            UpdateContextTokensDisplay();
        }
    }

    public async Task InternalCompressContextAsync()
    {
        if (_contextCompressionService == null || _configService == null) return;

        var config = _configService.Load();
        IsCompressing = true;
        try
        {
            var previousSummary = _activeContextSummary;
            var messagesList = Messages.ToList();
            var result = await _contextCompressionService.CompressAsync(
                messagesList,
                previousSummary,
                config.KeepRecentRounds);
            if (result.Summary != null && result.CompressedCount > 0)
            {
                // 入压缩撤销栈：捕获压缩前摘要与被压缩消息引用
                _compressionHistory.Push(new CompressionCheckpoint(previousSummary, result.CompressedMessages));

                SetActiveContextSummary(result.Summary);

                // 更新对话上下文并重新计算 Token（上下文变小，强制以估算刷新，下轮真实 usage 重锚）
                UpdateConversationContext();
                UpdateContextTokensDisplay(forceEstimateBaseline: true);
                UpdateBubbleButtonVisibility();
                UndoCompressionCommand.NotifyCanExecuteChanged();

                _logger.Information("UI 上下文压缩显示已更新（按轮次压缩，{Mode}）", result.UsedFallback ? "本地兜底" : "AI");
            }
        }
        finally
        {
            IsCompressing = false;
            await NotifySchedulerAvailabilityAsync();
        }
    }

    private async Task NotifySchedulerAvailabilityAsync()
    {
        if (_taskScheduler == null || IsSending || IsCompressing)
        {
            return;
        }

        try
        {
            await _taskScheduler.RunDueTasksAsync();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "通知任务调度器重新检查到期任务失败");
        }
    }

    [RelayCommand(CanExecute = nameof(CanUndoCompression))]
    private void UndoCompression() => InternalUndoCompression();

    private bool CanUndoCompression() => !IsSending && !IsCompressing && _compressionHistory.Count > 0;

    /// <summary>
    /// 撤销上一次上下文压缩：把该批被归档的消息重新激活，并恢复压缩前的摘要。
    /// 仅支持会话内（内存）撤销；切换/重置会话后栈清空。
    /// </summary>
    public bool InternalUndoCompression()
    {
        if (_compressionHistory.Count == 0 || IsSending || IsCompressing) return false;

        var checkpoint = _compressionHistory.Pop();
        foreach (var msg in checkpoint.Batch)
        {
            msg.IsCompressed = false; // 恢复可见，并重新进入发送上下文
        }
        SetActiveContextSummary(checkpoint.PreviousSummary);

        UpdateConversationContext();
        // 撤销压缩使上下文重新变大：强制以估算刷新，下一轮真实 usage 会重锚。
        UpdateContextTokensDisplay(forceEstimateBaseline: true);
        UpdateBubbleButtonVisibility();
        UndoCompressionCommand.NotifyCanExecuteChanged();

        _logger.Information("已撤销上一次上下文压缩，恢复 {Count} 条消息", checkpoint.Batch.Count);
        return true;
    }

    public async Task LoadHistoryConversationAsync(ConversationHistoryItem item)
    {
        if (_archiveService == null || item.IsArchivePlaceholder) return;

        await _conversationTransitionLock.WaitAsync();
        try
        {
            IsResetting = true;
            BeginConversationTransition();

            var stagedSnapshot = await TryStageCurrentConversationForTransitionAsync();
            if (stagedSnapshot == TransitionStageResult.Failed)
            {
                return;
            }

            var history = await _archiveService.LoadByIdAsync(item.Id);
            if (history == null)
            {
                _logger.Warning("未找到要加载的历史对话: {Id}", item.Id);
                return;
            }

            ResetConversationState(clearMessages: false);
            _conversationId = string.IsNullOrWhiteSpace(history.ConversationId)
                ? Guid.NewGuid().ToString("N")
                : history.ConversationId;
            _currentContext.ConversationId = _conversationId;
            _currentHistoryId = history.Id;
            _forkedFromConversationId = history.ForkedFromConversationId;
            _forkedFromHistoryId = history.ForkedFromHistoryId;
            _forkedAtMessageId = history.ForkedAtMessageId;

            // 恢复工作区绑定
            if (!string.IsNullOrEmpty(history.WorkspaceId) && _workspaceService != null)
            {
                var ws = AvailableWorkspaces.FirstOrDefault(w => w.Id == history.WorkspaceId);
                if (ws != null)
                {
                    CurrentWorkspace = ws;
                }
                else
                {
                    // 工作区可能已删除，清理悬空引用
                    CurrentWorkspace = null;
                }
            }
            else
            {
                CurrentWorkspace = null;
            }

            _compressionHistory.Clear();
            SetActiveContextSummary(history.ContextSummary);
            UndoCompressionCommand.NotifyCanExecuteChanged();

            var restoredMessages = history.Messages ?? [];
            if (restoredMessages.Count > 0)
            {
                // 消息先全部上屏，预览图后台解码陆续回填（PreviewImage 是 ObservableProperty，绑定自动刷新）
                _isBulkLoadingMessages = true;
                try
                {
                    foreach (var msg in restoredMessages)
                    {
                        ConversationPersistenceHelper.PrepareRestoredMessage(msg);
                    }

                    Messages.ReplaceAll(restoredMessages);
                }
                finally
                {
                    _isBulkLoadingMessages = false;
                }

                LoadPreviewsInBackground(restoredMessages);
            }
            else
            {
                Messages.ReplaceAll([]);
            }

            await ReconcileImageGenerationSessionAsync();

            _initialConversationSignature = CreateConversationSignature();
            // 载入的是另一段会话：清空旧锚点，改由估算显示，其首次发送会重锚到真实 usage。
            _tokenService?.ResetUsage();
            UpdateConversationContext();
            UpdateContextTokensDisplay();
            UpdateBubbleButtonVisibility();
            HistoryConversationLoaded?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            IsResetting = false;
            _conversationTransitionLock.Release();
        }
    }

    /// <summary>
    /// Restores a persisted item into a newly-created conversation VM.
    /// Unlike <see cref="LoadHistoryConversationAsync"/>, this does not stage or replace an
    /// existing live conversation and is therefore the correct seam for the multi-session tree.
    /// </summary>
    public void RestorePersistedConversation(ConversationHistoryItem history)
    {
        BeginConversationTransition();
        ResetConversationState(clearMessages: false);
        _conversationId = string.IsNullOrWhiteSpace(history.ConversationId)
            ? Guid.NewGuid().ToString("N")
            : history.ConversationId;
        _currentContext.ConversationId = _conversationId;
        _currentHistoryId = history.Id;
        _forkedFromConversationId = history.ForkedFromConversationId;
        _forkedFromHistoryId = history.ForkedFromHistoryId;
        _forkedAtMessageId = history.ForkedAtMessageId;

        CurrentWorkspace = !string.IsNullOrEmpty(history.WorkspaceId)
            ? AvailableWorkspaces.FirstOrDefault(workspace => workspace.Id == history.WorkspaceId)
            : null;

        _compressionHistory.Clear();
        SetActiveContextSummary(history.ContextSummary);
        UndoCompressionCommand.NotifyCanExecuteChanged();

        var restoredMessages = ConversationPersistenceHelper.CloneMessages(history.Messages ?? []);
        foreach (var message in restoredMessages)
        {
            ConversationPersistenceHelper.PrepareRestoredMessage(message);
        }
        Messages.ReplaceAll(restoredMessages);
        LoadPreviewsInBackground(restoredMessages);

        _ = ReconcileImageGenerationSessionAsync();
        _initialConversationSignature = CreateConversationSignature();
        _tokenService?.ResetUsage();
        UpdateConversationContext();
        UpdateContextTokensDisplay();
        UpdateBubbleButtonVisibility();
    }

    /// <summary>
    /// 后台逐张解码附件预览图（PreviewImage 绑定 UI，赋值在 UI 线程完成），
    /// 全部完成后再刷一次 token 显示——此时才拿到真实图像尺寸。
    /// </summary>
    private void LoadPreviewsInBackground(IEnumerable<ChatMessage> messages)
    {
        CancelPendingPreviewLoading();
        if (_attachmentStoreService == null) return;

        var snapshot = messages
            .Where(m => m.Attachments.Count > 0)
            .SelectMany(m => m.Attachments)
            .ToList();
        if (snapshot.Count == 0) return;

        var cts = new CancellationTokenSource();
        _previewLoadCts = cts;
        var epoch = Volatile.Read(ref _conversationEpoch);
        _ = LoadPreviewsAsync(snapshot, cts, epoch);
    }

    private async Task LoadPreviewsAsync(
        IReadOnlyList<ChatAttachment> attachments,
        CancellationTokenSource cts,
        int epoch)
    {
        try
        {
            await _attachmentStoreService!.LoadPreviewsAsync(attachments, cts.Token);
            if (IsCurrentConversationEpoch(epoch) && !cts.IsCancellationRequested)
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => UpdateContextTokensDisplay());
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // 切换会话时的预期取消。
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "后台回填附件预览失败");
        }
        finally
        {
            Interlocked.CompareExchange(ref _previewLoadCts, null, cts);
            cts.Dispose();
        }
    }

    private void CancelPendingPreviewLoading()
    {
        Interlocked.Exchange(ref _previewLoadCts, null)?.Cancel();
    }

    private async Task<TransitionStageResult> TryStageCurrentConversationForTransitionAsync()
    {
        var snapshot = await CaptureArchiveSnapshotIfNeededAsync();
        if (snapshot == null)
        {
            return TransitionStageResult.NotNeeded;
        }

        if (_archiveService == null)
        {
            SetBackgroundArchiveStatus(GetString("Chat.Archive.ErrorUnavailable", "Background archive service is unavailable."), isError: true);
            return TransitionStageResult.Failed;
        }

        try
        {
            _latestArchiveCaptureAt = snapshot.CapturedAt;
            await _archiveService.StageArchiveAsync(snapshot);
            SetBackgroundArchiveStatus(
                GetString("Chat.Archive.Saving", "Previous conversation is being saved in the background."),
                isError: false);
            return TransitionStageResult.Staged;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "写入待归档队列失败");
            SetBackgroundArchiveStatus(
                string.Format(
                    GetString("Chat.Archive.StageFailed", "Failed to queue the previous conversation: {0}"),
                    ex.Message),
                isError: true);
            return TransitionStageResult.Failed;
        }
    }

    public void PersistDraft()
    {
        if (_archiveService == null) return;

        if (!HasConversationStateToPersist() || !IsConversationModified())
        {
            _archiveService.DeleteDraft();
            return;
        }

        var snapshot = new ConversationDraftSnapshot
        {
            ConversationId = _conversationId,
            CurrentHistoryId = _currentHistoryId,
            InitialConversationSignature = _initialConversationSignature,
            ContextSummary = _activeContextSummary,
            ForkedFromConversationId = _forkedFromConversationId,
            ForkedFromHistoryId = _forkedFromHistoryId,
            ForkedAtMessageId = _forkedAtMessageId,
            Messages = Messages
                .Where(ConversationPersistenceHelper.ShouldPersistMessage)
                .Select(ConversationPersistenceHelper.CloneMessage)
                .ToList(),
            UpdatedAt = DateTime.Now
        };

        if (snapshot.Messages.Count == 0 && string.IsNullOrWhiteSpace(snapshot.ContextSummary))
        {
            _archiveService.DeleteDraft();
            return;
        }

        _archiveService.SaveDraft(snapshot);
    }

    private void RestoreDraftIfNeeded()
    {
        if (_archiveService == null) return;

        var snapshot = _archiveService.LoadDraft();
        if (snapshot == null)
        {
            return;
        }

        if ((snapshot.Messages == null || snapshot.Messages.Count == 0) && string.IsNullOrWhiteSpace(snapshot.ContextSummary))
        {
            _archiveService.DeleteDraft();
            return;
        }

        _conversationId = string.IsNullOrWhiteSpace(snapshot.ConversationId)
            ? Guid.NewGuid().ToString("N")
            : snapshot.ConversationId;
        _currentContext.ConversationId = _conversationId;
        _currentHistoryId = snapshot.CurrentHistoryId;
        _initialConversationSignature = snapshot.InitialConversationSignature;
        _forkedFromConversationId = snapshot.ForkedFromConversationId;
        _forkedFromHistoryId = snapshot.ForkedFromHistoryId;
        _forkedAtMessageId = snapshot.ForkedAtMessageId;

        _compressionHistory.Clear();
        SetActiveContextSummary(snapshot.ContextSummary);
        UndoCompressionCommand.NotifyCanExecuteChanged();

        if (snapshot.Messages != null)
        {
            _isBulkLoadingMessages = true;
            try
            {
                foreach (var msg in snapshot.Messages)
                {
                    ConversationPersistenceHelper.PrepareRestoredMessage(msg);
                }

                Messages.ReplaceAll(snapshot.Messages);
            }
            finally
            {
                _isBulkLoadingMessages = false;
            }

            LoadPreviewsInBackground(snapshot.Messages);
        }
        else
        {
            Messages.ReplaceAll([]);
        }

        _ = ReconcileImageGenerationSessionAsync();

        // 恢复的是另一段会话快照：清空旧锚点，改由估算显示，其首次发送会重锚。
        _tokenService?.ResetUsage();
        UpdateConversationContext();
        UpdateContextTokensDisplay();
        UpdateBubbleButtonVisibility();
        _logger.Information("已恢复主对话草稿，消息数: {Count}", Messages.Count);
    }

    private bool HasConversationStateToPersist()
    {
        return Messages.Any(ConversationPersistenceHelper.ShouldPersistMessage)
            || !string.IsNullOrWhiteSpace(_activeContextSummary);
    }

    private bool IsConversationModified()
    {
        if (!HasConversationStateToPersist())
        {
            return false;
        }

        if (string.IsNullOrEmpty(_currentHistoryId))
        {
            return true;
        }

        return !string.Equals(_initialConversationSignature, CreateConversationSignature(), StringComparison.Ordinal);
    }

    private string CreateConversationSignature()
    {
        var signatureModel = new
        {
            ContextSummary = _activeContextSummary ?? string.Empty,
            Messages = Messages
                .Where(ConversationPersistenceHelper.ShouldPersistMessage)
                .Select(msg => new
                {
                    msg.Role,
                    msg.Content,
                    msg.Timestamp,
                    msg.ToolCallId,
                    msg.ToolCallsJson,
                    msg.ReasoningContent,
                    msg.IsCompressed,
                    Attachments = msg.Attachments.Select(a => new
                    {
                        a.Id,
                        a.Kind,
                        a.FileName,
                        a.StoredPath,
                        a.MimeType,
                        a.SizeBytes,
                        a.Width,
                        a.Height
                    }).ToList(),
                    Segments = msg.Segments.Select(segment => new
                    {
                        segment.Kind,
                        segment.Text,
                        segment.AttachmentId,
                        ToolCalls = segment.ToolCalls.Select(t => new
                        {
                            t.ToolCallId,
                            t.Name,
                            t.Summary,
                            t.Arguments,
                            t.Result,
                            t.Status
                        }).ToList()
                    }).ToList()
                })
                .ToList()
        };

        return JsonSerializer.Serialize(signatureModel);
    }

    private void ClearPendingAttachments(bool deleteStoredFiles)
    {
        var attachments = PendingAttachments.ToList();
        foreach (var attachment in attachments)
        {
            if (deleteStoredFiles)
            {
                _attachmentStoreService?.DeleteStoredAttachment(attachment);
            }
        }

        ReleaseAttachmentPreviews(attachments);
        PendingAttachments.Clear();
        AttachmentStatusMessage = string.Empty;
        OnPropertyChanged(nameof(CanToggleRawContext));
        SendMessageCommand.NotifyCanExecuteChanged();
    }

    private void OnArchiveCompleted(object? sender, ConversationArchiveResultEventArgs e)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            if (e.Snapshot.CapturedAt != _latestArchiveCaptureAt)
            {
                return;
            }

            await Task.Delay(2500);
            if (e.Snapshot.CapturedAt == _latestArchiveCaptureAt && !IsBackgroundArchiveError)
            {
                BackgroundArchiveStatusMessage = string.Empty;
                IsBackgroundArchiveError = false;
            }
        });
    }

    private void OnArchiveFailed(object? sender, ConversationArchiveResultEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (e.Snapshot.CapturedAt != _latestArchiveCaptureAt)
            {
                return;
            }

            SetBackgroundArchiveStatus(
                GetString("Chat.Archive.RetryLater", "Previous conversation save failed. It has been kept for retry."),
                isError: true);
        });
    }

    private void SetBackgroundArchiveStatus(string message, bool isError)
    {
        BackgroundArchiveStatusMessage = message;
        IsBackgroundArchiveError = isError;
    }

    private string GetString(string key, string defaultValue)
    {
        return _localizationService?.GetString(key, defaultValue) ?? defaultValue;
    }

    private string ToAttachmentErrorMessage(Exception ex)
    {
        if (ex.Message.Contains("too large", StringComparison.OrdinalIgnoreCase))
        {
            return GetString("Chat.Attach.ErrorTooLarge", "One of the selected images is too large.");
        }

        if (ex.Message.Contains("Unsupported", StringComparison.OrdinalIgnoreCase))
        {
            return GetString("Chat.Attach.ErrorUnsupported", "Only PNG, JPG, WEBP, and GIF images are supported for now.");
        }

        return string.Format(GetString("Chat.Attach.ErrorGeneric", "Failed to add attachment: {0}"), ex.Message);
    }

    private string ToChatErrorMessage(Exception ex)
    {
        if (_currentContext.Messages.Any(m => m.Attachments.Any(a => a.Kind == AttachmentKind.Image))
            && IsLikelyImageInputFailure(ex))
        {
            return GetString(
                "Chat.Error.ImageUnsupported",
                "The current model or endpoint does not support image input. Please switch the main model to a vision-capable model and try again.");
        }

        return string.Format(GetString("Chat.Error.Generic", "Error: {0}"), ex.Message);
    }

    private static bool IsLikelyImageInputFailure(Exception ex)
    {
        if (ex is ClientResultException clientException
            && clientException.Status is 400 or 415 or 422)
        {
            return true;
        }

        return ex.Message.Contains("image", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("vision", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("modal", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("unsupported", StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand]
    private void ToggleAudioPlayback(ChatAttachment? attachment)
    {
        if (attachment == null || !attachment.IsAudio)
        {
            return;
        }

        // Single toggle: if this clip is playing, stop it; otherwise start it.
        if (attachment.IsPlaying)
        {
            StopAudioPlayback();
            return;
        }

        // Only one clip plays at a time — tear down whatever is running first.
        StopAudioPlayback();

        // Fire-and-forget: the command must return immediately so that the
        // generated IAsyncRelayCommand keeps CanExecute == true while the
        // system process (afplay/aplay/powershell) is running — otherwise the
        // Stop button is greyed out for the entire playback duration.
        _ = PlaySystemAudioAttachmentAsync(attachment, null);
    }

    private void StopAudioPlayback()
    {
        if (_systemAudioCts != null)
        {
            _systemAudioCts.Cancel();
            _systemAudioCts.Dispose();
            _systemAudioCts = null;
        }

        _playingAttachmentId = null;
        UpdateAudioPlayingState(null, false);
    }

    /// <summary>
    /// 回缩/分支/切换会话时统一调用：取消并重建会话级语音令牌，以中止所有在飞的语音
    /// 生成，并停止当前播放。后台生成任务自身还会做 epoch + 消息归属复检，此处取消是
    /// 即时止损（尽快掐断网络请求/系统进程）。
    /// </summary>
    private void CancelPendingAudio()
    {
        var old = _audioSessionCts;
        _audioSessionCts = new CancellationTokenSource();
        try { old.Cancel(); } catch (ObjectDisposedException) { }
        old.Dispose();
        StopAudioPlayback();
    }

    private void UpdateAudioPlayingState(string? attachmentId, bool isPlaying)
    {
        foreach (var attachment in Messages.SelectMany(m => m.Attachments).Where(a => a.IsAudio))
        {
            attachment.IsPlaying = isPlaying && attachment.Id == attachmentId;
        }
    }

    /// <summary>
    /// 用户点击气泡底部「生成语音」按钮：为该条历史/漏生成的助手消息补生成语音并播放。
    /// forcePlay=true → 无视「自动播放」开关，用户既然主动点了就直接播。
    /// </summary>
    [RelayCommand]
    private Task GenerateMessageAudioAsync(ChatMessage? message)
    {
        if (message == null || !message.CanGenerateAudio)
        {
            return Task.CompletedTask;
        }
        var epoch = Volatile.Read(ref _conversationEpoch);
        return RunAudioGenerationAsync(message, epoch, forcePlay: true);
    }

    /// <summary>
    /// 后台生成助手语音（TTS），与发送态(IsSending)解耦：文本回复结束后异步进行，
    /// 期间用户可正常发送/回缩/分支。多条消息的语音可并发生成（不再单飞互斥，
    /// 因此发送新消息不会掐掉上一条正在生成的语音）。完成时若会话已切换(epoch 变)、
    /// 该消息已被回缩/分支移除，或本次生成被会话级取消，则丢弃音频（删除已落盘文件），
    /// 不挂载、不播放。
    /// </summary>
    /// <param name="forcePlay">true=用户手动点击，生成后直接播放；false=自动流程，仅在开启自动播放且该消息仍是最新回复时播放。</param>
    private async Task RunAudioGenerationAsync(ChatMessage assistantMsg, int epoch, bool forcePlay)
    {
        if (_chatService == null || _configService == null)
        {
            return;
        }

        AppConfig config;
        try
        {
            config = _configService.Load();
        }
        catch
        {
            return;
        }

        if (!config.ChatAudioEnabled)
        {
            return;
        }

        var text = assistantMsg.Content;
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        // 与会话级令牌联动：回缩/分支/切换会话会取消该令牌，从而中止所有在飞生成；
        // 但同一时刻的多条生成彼此独立、并发进行。
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_audioSessionCts.Token);

        assistantMsg.IsGeneratingAudio = true;
        try
        {
            var (attachment, errorMessage) = await _chatService.GenerateAssistantSpeechAsync(text, linkedCts.Token);

            // 生成期间发生了回缩/分支/会话切换：丢弃产物，绝不挂到已失效的消息/会话。
            if (linkedCts.IsCancellationRequested
                || !IsCurrentConversationEpoch(epoch)
                || !Messages.Contains(assistantMsg))
            {
                if (attachment != null)
                {
                    _attachmentStoreService?.DeleteStoredAttachment(attachment);
                }
                return;
            }

            if (attachment != null)
            {
                assistantMsg.Attachments.Add(attachment);
                assistantMsg.NotifyAttachmentsChanged();
                // 把音频并入持久化上下文（UpdateConversationContext 从 Messages 重建）。
                UpdateConversationContext();

                if (forcePlay)
                {
                    // 手动生成：无条件播放（用户明确点了「生成语音」）。
                    StopAudioPlayback();
                    _ = PlaySystemAudioAttachmentAsync(attachment, assistantMsg);
                }
                else if (ReferenceEquals(Messages.LastOrDefault(), assistantMsg))
                {
                    // 自动生成：仅当该消息仍是最新回复时才自动播放，避免旧回复的
                    // 语音在用户已发出新消息后回来抢占播放。
                    TryAutoPlayAssistantAudio(attachment, assistantMsg);
                }
            }
            else if (!string.IsNullOrWhiteSpace(errorMessage))
            {
                assistantMsg.AudioErrorMessage = errorMessage;
            }
        }
        catch (OperationCanceledException)
        {
            // 回缩/分支/切换会话触发的取消，属预期，静默。
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "后台生成助手语音失败");
        }
        finally
        {
            assistantMsg.IsGeneratingAudio = false;
        }
    }

    private void TryAutoPlayAssistantAudio(ChatAttachment attachment, ChatMessage message)
    {
        if (_configService == null)
        {
            return;
        }

        try
        {
            var config = _configService.Load();
            if (!config.ChatAudioEnabled || !config.ChatAudioAutoPlay)
            {
                return;
            }

            _ = PlaySystemAudioAttachmentAsync(attachment, message);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "自动播放 assistant 音频失败");
            message.AudioErrorMessage = GetString("Chat.Audio.PlaybackFailed", "Failed to start audio playback.");
        }
    }

    private async Task PlaySystemAudioAttachmentAsync(ChatAttachment attachment, ChatMessage? message)
    {
        if (_systemAudioService?.IsSupported != true)
        {
            if (message != null)
            {
                message.AudioErrorMessage = GetString("Chat.Audio.PlaybackUnavailable", "Audio playback is unavailable on this device.");
            }
            return;
        }

        var cts = new CancellationTokenSource();
        var id = attachment.Id;
        _systemAudioCts = cts;
        _playingAttachmentId = id;
        UpdateAudioPlayingState(id, true);
        try
        {
            var result = await _systemAudioService.PlayFileAsync(attachment.StoredPath, cts.Token);
            if (!result.Success && !cts.IsCancellationRequested)
            {
                var owner = message ?? Messages.FirstOrDefault(m => m.Attachments.Contains(attachment));
                if (owner != null)
                {
                    owner.AudioErrorMessage = string.Format(
                        GetString("Chat.Audio.PlaybackFailedDetail", "Failed to play system audio: {0}"),
                        result.Message);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // User pressed Stop or a new clip took over — expected, swallow.
        }
        finally
        {
            if (_systemAudioCts == cts)
            {
                _systemAudioCts.Dispose();
                _systemAudioCts = null;
            }

            // Only clear the playing state if *this* clip is still the active
            // one. If a new clip started while we were cancelled/completing,
            // its IsPlaying=true must not be clobbered.
            if (_playingAttachmentId == id)
            {
                _playingAttachmentId = null;
                UpdateAudioPlayingState(id, false);
            }
        }
    }

    private void DeleteMessageAttachments(ChatMessage message)
    {
        if (_attachmentStoreService == null)
        {
            return;
        }

        var attachments = message.Attachments.ToList();
        foreach (var attachment in attachments)
        {
            _attachmentStoreService.DeleteStoredAttachment(attachment);
        }

        ReleaseAttachmentPreviews(attachments);
    }

    private static void ReleaseAttachmentPreviews(IEnumerable<ChatAttachment> attachments)
    {
        var previews = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var attachment in attachments)
        {
            if (attachment.PreviewImage is { } preview)
            {
                previews.Add(preview);
                attachment.PreviewImage = null;
            }
        }

        foreach (var preview in previews.OfType<IDisposable>())
        {
            preview.Dispose();
        }
    }

    private static ChatAttachment CloneAttachmentForMessage(ChatAttachment attachment)
    {
        return ConversationPersistenceHelper.CloneAttachment(attachment);
    }

    private static void EnsureSegmentLayout(ChatMessage message)
    {
        if (message.Segments.Count > 0)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(message.Content))
        {
            message.Segments.Add(new ChatMessageSegment
            {
                Kind = ChatMessageSegmentKind.Markdown,
                Text = message.Content
            });
        }

        message.NotifySegmentsChanged();
    }

    private void AppendAssistantMarkdownSegment(ChatMessage message, string contentDelta)
    {
        if (message.Segments.Count == 0)
        {
            return;
        }

        // 工具轮刚封口：本段正文另起一个 Markdown 段，避免与上一轮文本拼接成一段
        if (_forceNewAssistantTextSegment)
        {
            _forceNewAssistantTextSegment = false;
            message.Segments.Add(new ChatMessageSegment
            {
                Kind = ChatMessageSegmentKind.Markdown,
                Text = contentDelta
            });
            message.NotifySegmentsChanged();
            return;
        }

        var lastSegment = message.Segments[^1];
        if (lastSegment.IsMarkdown)
        {
            lastSegment.Text += contentDelta;
            return;
        }

        message.Segments.Add(new ChatMessageSegment
        {
            Kind = ChatMessageSegmentKind.Markdown,
            Text = contentDelta
        });
        message.NotifySegmentsChanged();
    }

    // 为本轮工具调用追加「执行中」卡片（每个工具一张）。
    private void AddToolCallEntries(ChatMessage message, string? toolCallsJson)
    {
        if (string.IsNullOrWhiteSpace(toolCallsJson))
        {
            return;
        }

        try
        {
            var toolCalls = System.Text.Json.Nodes.JsonNode.Parse(toolCallsJson)?.AsArray();
            if (toolCalls == null)
            {
                return;
            }

            ChatMessageSegment? group = null;

            foreach (var node in toolCalls)
            {
                if (node == null)
                {
                    continue;
                }

                var name = node["FunctionName"]?.ToString() ?? string.Empty;
                var id = node["Id"]?.ToString();
                var arguments = node["Arguments"]?.ToString();

                group ??= GetOrCreateToolCallGroup(message);
                group.ToolCalls.Add(new ToolCallEntry
                {
                    ToolCallId = id,
                    Name = name,
                    Summary = ToolCallDisplay.Summarize(name, arguments, _localizationService),
                    Arguments = ToolCallDisplay.PrettyArguments(arguments),
                    Status = ToolCallStatus.Running
                });
            }

            // 流式期间展开整组，方便观察实时进度（除非用户已手动收起）
            if (group != null && !group.UserToggledGroup)
            {
                group.IsGroupExpanded = true;
            }

            if (group != null)
            {
                message.NotifySegmentsChanged();
            }
        }
        catch
        {
            // 工具调用 JSON 异常时跳过卡片渲染，不影响主流程
        }
    }

    private void RefreshToolCallSummaries()
    {
        foreach (var entry in Messages
                     .SelectMany(message => message.Segments)
                     .Where(segment => segment.IsToolCallGroup)
                     .SelectMany(segment => segment.ToolCalls))
        {
            entry.Summary = ToolCallDisplay.Summarize(entry.Name, entry.Arguments, _localizationService);
        }
    }

    // 一条消息内所有工具调用汇总进同一个组，并固定置顶（segment 索引 0），
    // 让全部工具调用集中在气泡顶部、阶段性回复文本依次排在其下方。
    private static ChatMessageSegment GetOrCreateToolCallGroup(ChatMessage message)
    {
        var group = message.Segments.FirstOrDefault(s => s.IsToolCallGroup);
        if (group == null)
        {
            group = new ChatMessageSegment { Kind = ChatMessageSegmentKind.ToolCallGroup };
            message.Segments.Insert(0, group);
        }

        return group;
    }

    // 工具结果回填到对应项：按 ToolCallId 匹配，标记成功/失败并填入结果预览。
    private static void CompleteToolCallEntry(ChatMessage message, string? toolCallId, string toolName, string? resultJson)
    {
        ToolCallEntry? entry = null;
        foreach (var seg in message.Segments)
        {
            if (!seg.IsToolCallGroup)
            {
                continue;
            }

            entry = seg.ToolCalls.FirstOrDefault(t =>
                !string.IsNullOrEmpty(t.ToolCallId) && t.ToolCallId == toolCallId);
            if (entry != null)
            {
                break;
            }
        }

        // 兜底：没有 Id 匹配时，取第一项仍在执行中的同名调用
        entry ??= message.Segments
            .Where(s => s.IsToolCallGroup)
            .SelectMany(s => s.ToolCalls)
            .FirstOrDefault(t => t.IsRunning && (string.IsNullOrEmpty(toolName) || t.Name == toolName));

        if (entry == null)
        {
            return;
        }

        var (success, preview) = ToolCallDisplay.ParseResult(resultJson);
        entry.Status = success ? ToolCallStatus.Success : ToolCallStatus.Failed;
        entry.Result = preview;
    }

    // 工具轮全部完成、开始/结束输出最终正文时收起工具组（尊重用户已手动展开/收起的意愿）。
    private static void CollapseToolGroups(ChatMessage message)
    {
        foreach (var seg in message.Segments)
        {
            if (seg.IsToolCallGroup && !seg.UserToggledGroup)
            {
                seg.IsGroupExpanded = false;
            }
        }
    }

    // 中间消息（tool_call / tool）插入到活动气泡之前，保证最终回复气泡始终在 Messages 末尾。
    private void InsertBeforeActiveBubble(ChatMessage active, ChatMessage msg)
    {
        int idx = Messages.IndexOf(active);
        if (idx < 0)
        {
            // 兜底：活动气泡已被清理（理论上不应发生于流式期间）。
            Messages.Add(msg);
        }
        else
        {
            Messages.Insert(idx, msg);
        }
    }

    // 工具轮封口：把本轮流式出的「阶段性正文」固化为可见 Markdown 段（避免随后续轮被覆盖），
    // 然后清空活动气泡的 Content/Reasoning。
    // 清空的原因仅是上下文去重——该正文已随隐藏的 tool_call 消息进入上下文（见 UpdateConversationContext），
    // 保留 Content 会让其重复计入；但可见的 Markdown 段必须保留，否则阶段性回复会从界面消失。
    private void CommitActiveBubbleRound(ChatMessage bubble)
    {
        if (!string.IsNullOrWhiteSpace(bubble.Content))
        {
            var last = bubble.Segments.LastOrDefault();
            if (last == null || !last.IsMarkdown)
            {
                // 首轮（流式时 Segments 还为空，AppendAssistantMarkdownSegment 会跳过建段）：补建一段承载前导正文
                bubble.Segments.Add(new ChatMessageSegment
                {
                    Kind = ChatMessageSegmentKind.Markdown,
                    Text = bubble.Content
                });
            }
            // 末尾已是 Markdown 段时，流式正文已逐字写入其中，无需重复添加
        }

        bubble.Content = string.Empty;
        bubble.ReasoningContent = null;
        // 下一段正文另起一段（工具组置顶后，相邻轮文本之间不再有工具段天然分隔）
        _forceNewAssistantTextSegment = true;
        bubble.NotifySegmentsChanged();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        App.ThemeChanged -= OnThemeChanged;
        Messages.CollectionChanged -= OnMessagesCollectionChanged;
        PendingAttachments.CollectionChanged -= OnPendingAttachmentsCollectionChanged;

        if (_localizationService != null)
            _localizationService.LanguageChanged -= OnLanguageChanged;
        if (_configService != null)
            _configService.ConfigChanged -= OnConfigChanged;
        if (_archiveService != null)
        {
            _archiveService.ArchiveCompleted -= OnArchiveCompleted;
            _archiveService.ArchiveFailed -= OnArchiveFailed;
        }
        if (Orchestrator != null)
        {
            Orchestrator.ActiveAgents.CollectionChanged -= OnActiveSubAgentsChanged;
            foreach (var agent in Orchestrator.ActiveAgents)
                agent.PropertyChanged -= OnSubAgentStatePropertyChanged;
        }

        _subAgentClearTimer?.Stop();
        _subAgentClearTimer = null;

        _responseCts?.Cancel();
        _responseCts?.Dispose();
        _responseCts = null;

        CancelPendingPreviewLoading();
        _screenshotBackgroundPollCts?.Cancel();
        _screenshotBackgroundPollCts = null;

        try { _audioSessionCts.Cancel(); } catch (ObjectDisposedException) { }
        _audioSessionCts.Dispose();
        StopAudioPlayback();

        ReleaseAttachmentPreviews(
            Messages.SelectMany(message => message.Attachments)
                .Concat(PendingAttachments));
    }
}
