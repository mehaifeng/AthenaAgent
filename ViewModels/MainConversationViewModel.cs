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
using System.Security.Cryptography;
using System.Text;

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
    private readonly IContextPolicyProvider? _contextPolicyProvider;
    private readonly ICompressionPlanner? _compressionPlanner;
    private readonly ICompressionCandidateGenerator? _compressionCandidateGenerator;
    private readonly ICompressionValidator? _compressionValidator;
    private IConversationCompressionCommitter? _compressionCommitter;
    private EffectiveContextPolicySnapshot? _effectiveContextPolicy;
    private bool _policyRefreshPending;
    private string _requestContentIdentity = string.Empty;
    private readonly ILogger _logger = Log.ForContext<MainConversationViewModel>();
    private bool _disposed;

    public bool IsDisposed => _disposed;

    /// <summary>响应封口、压缩和撤销等需要立即持久化的语义状态变化。</summary>
    public event EventHandler? PersistenceStateChanged;

    /// <summary>
    /// 消息级分支请求。会话 ViewModel 只负责校验并转发，实际分支由外层会话项转发给
    /// 窗口 ViewModel 执行（持久化父会话、创建新分支会话并插入会话树）。
    /// </summary>
    public event EventHandler<MessageForkRequestedEventArgs>? MessageForkRequested;

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
    [NotifyPropertyChangedFor(nameof(CanGenerateCompressionCandidate))]
    [NotifyPropertyChangedFor(nameof(CanApplyCompressionCandidate))]
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

    public long Revision => _revision;

    public string? ActiveContextSummary => _activeContextSummary;

    public string? OrphanedLegacySummary => _orphanedLegacySummary;

    public bool HasActiveContextSummary => !string.IsNullOrWhiteSpace(_activeContextSummary);

    public bool HasOrphanedLegacySummary => !string.IsNullOrWhiteSpace(_orphanedLegacySummary);

    public string? ForkedFromConversationId => _forkedFromConversationId;

    public string? ForkedFromHistoryId => _forkedFromHistoryId;

    public string? ForkedAtMessageId => _forkedAtMessageId;

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
    [NotifyPropertyChangedFor(nameof(CanGenerateCompressionCandidate))]
    [NotifyPropertyChangedFor(nameof(CanApplyCompressionCandidate))]
    private bool _isCompressing;

    [ObservableProperty]
    private string _compressionStatusMessage = string.Empty;

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

    [ObservableProperty]
    private bool _isContextInspectorOpen;

    [ObservableProperty]
    private int _selectedContextInspectorTab;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCompressionImpactPreview))]
    private string _compressionImpactPreview = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCompressionCandidatePreview))]
    private string _compressionCandidatePreview = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGenerateCompressionCandidate))]
    [NotifyPropertyChangedFor(nameof(CanApplyCompressionCandidate))]
    private bool _isCompressionPreviewBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGenerateCompressionCandidate))]
    [NotifyPropertyChangedFor(nameof(CanApplyCompressionCandidate))]
    private bool _isCompressionPreviewStale;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCompressionPreviewStatus))]
    private string _compressionPreviewStatus = string.Empty;

    [ObservableProperty]
    private bool _isRawContextLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRawContextStatus))]
    private string _rawContextStatus = string.Empty;

    [ObservableProperty]
    private string _rawContextSnapshotInfo = string.Empty;

    /// <summary>调试：原始上下文按消息拆分的条目（避免单一大文本框选择卡顿）。</summary>
    public ObservableCollection<RawContextEntry> RawContextEntries { get; } = new();

    public bool HasCompressionImpactPreview => !string.IsNullOrWhiteSpace(CompressionImpactPreview);
    public bool HasCompressionCandidatePreview => !string.IsNullOrWhiteSpace(CompressionCandidatePreview);
    public bool HasCompressionPreviewStatus => !string.IsNullOrWhiteSpace(CompressionPreviewStatus);
    public bool HasRawContextStatus => !string.IsNullOrWhiteSpace(RawContextStatus);
    public bool CanGenerateCompressionCandidate =>
        _compressionPreviewPlan != null && !IsCompressionPreviewStale && !IsCompressionPreviewBusy && !IsSending && !IsCompressing;
    public bool CanApplyCompressionCandidate =>
        _compressionPreviewPlan != null && _compressionPreviewCandidate != null && _compressionPreviewValidation?.IsValid == true
        && !IsCompressionPreviewStale && !IsCompressionPreviewBusy && !IsSending && !IsCompressing;

    /// <summary>仅当对话流处于「完成」态（非发送/压缩/解析/重置）时，才允许切换 raw 视图。</summary>
    public bool CanToggleRawContext => !IsSending && !IsCompressing && !IsResetting;

    /// <summary>仅在聊天处于可发送状态时接受选择、粘贴或拖放附件。</summary>
    public bool CanAcceptAttachments => !IsSending && !IsCompressing && !IsResetting;

    public BulkObservableCollection<ChatMessage> Messages { get; } = new();

    public ObservableCollection<ChatAttachment> PendingAttachments { get; } = new();

    public bool HasPendingAttachments => PendingAttachments.Count > 0;

    public bool HasAttachmentStatusMessage => !string.IsNullOrWhiteSpace(AttachmentStatusMessage);

    public bool HasBackgroundArchiveStatusMessage => !string.IsNullOrWhiteSpace(BackgroundArchiveStatusMessage);

    public bool HasBackgroundArchiveNeutralStatus => HasBackgroundArchiveStatusMessage && !IsBackgroundArchiveError;

    public bool HasBackgroundArchiveErrorStatus => HasBackgroundArchiveStatusMessage && IsBackgroundArchiveError;

    public string ContextTokensInfo => _tokenService?.HasVisibleUsage == true
        ? _tokenService.TokenInfoText
        : GetString("Chat.Context.Unanchored", "Context usage will appear after the provider reports Usage.");

    public ITokenService? TokenService => _tokenService;

    public string ContextInspectorModelText => _effectiveContextPolicy == null
        ? GetString("Settings.Context.Unconfigured", "Not configured")
        : $"{_effectiveContextPolicy.ProviderId} / {_effectiveContextPolicy.ExternalModelId}";

    public string ContextInspectorMatchText => _effectiveContextPolicy == null
        ? "—"
        : $"{_effectiveContextPolicy.Metadata.Match.Status} · {_effectiveContextPolicy.Metadata.ContextWindowTokens.Source}";

    public string ContextInspectorPolicyText => _effectiveContextPolicy == null
        ? "—"
        : $"W {_effectiveContextPolicy.Policy.ContextWindowTokens:N0} · R {_effectiveContextPolicy.Policy.OutputReserveTokens:N0} · "
          + $"S {_effectiveContextPolicy.Policy.SafetyMarginTokens:N0} · B {_effectiveContextPolicy.Policy.AvailableInputBudgetTokens:N0} · "
          + $"T {_effectiveContextPolicy.Policy.CompressionThresholdTokens:N0}";

    public string ContextInspectorUsageText => _tokenService?.HasVisibleUsage == true
        ? $"{_tokenService.MeasurementKind} · {_tokenService.CurrentTokens:N0} / {_tokenService.MaxTokens:N0}"
        : GetString("Chat.Context.Unanchored", "Context usage will appear after the provider first reports Usage.");

    public string ContextInspectorUsageDetails => _tokenService?.HasVisibleUsage == true
        ? string.Format(
            GetString("ContextInspector.Usage.ReportedDetails", "Cached input {0:N0} · Last Usage {1}"),
            _tokenService.CachedInputTokens,
            _tokenService.State.LastUsageAt?.ToLocalTime().ToString("g") ?? "—")
        : string.Format(
            GetString("ContextInspector.Usage.EstimateDetails", "≈ {0:N0} internal protection estimate"),
            _currentContext.EstimatedTokenCount);

    public string ContextInspectorWorkspaceText => CurrentWorkspace == null
        ? GetString("ContextInspector.Workspace.App", "App defaults (no Workspace override)")
        : string.Format(
            GetString("ContextInspector.Workspace.Chain", "{0} · Workspace → App → model metadata"),
            CurrentWorkspace.Name);

    public string ContextInspectorCompressionModelText
    {
        get
        {
            var role = _configService?.Load().AiModels.ContextCompression;
            return role == null || string.IsNullOrWhiteSpace(role.Model)
                ? GetString("Settings.Context.Unconfigured", "Not configured")
                : $"{role.ProviderId} / {role.Model}";
        }
    }

    public string ContextInspectorWarningsText
    {
        get
        {
            var warnings = new List<string>();
            if (_effectiveContextPolicy?.Metadata.Match.Status == ModelMatchStatus.Unmatched)
                warnings.Add(GetString("ContextInspector.Warning.UnknownModel", "Unmatched model: using the 1M / 256K assumption."));
            if (_effectiveContextPolicy != null)
            {
                warnings.AddRange(_effectiveContextPolicy.Metadata.Warnings);
                warnings.AddRange(_effectiveContextPolicy.Policy.Warnings);
            }
            if (!string.IsNullOrWhiteSpace(CompressionStatusMessage)) warnings.Add(CompressionStatusMessage);
            return string.Join(Environment.NewLine, warnings.Distinct(StringComparer.Ordinal));
        }
    }

    public bool HasContextInspectorWarnings => !string.IsNullOrWhiteSpace(ContextInspectorWarningsText);

    public string CompressionSummaryDetails
    {
        get
        {
            if (_compressionHistory.Count == 0) return string.Empty;
            var checkpoint = _compressionHistory.Peek();
            return string.Format(
                GetString(
                    "ContextInspector.Summary.Details",
                    "{0} · {1:g} · {2} messages · {3:N0} → {4:N0} tokens · Prompt v{5} · {6}"),
                checkpoint.Mode,
                checkpoint.CreatedAt,
                checkpoint.Batch.Count,
                checkpoint.PreCompressionTokens,
                checkpoint.PostCompressionTokens,
                checkpoint.PromptVersion,
                checkpoint.CompressionModelFingerprint);
        }
    }

    [ObservableProperty]
    private string _currentModelName = string.Empty;

    public string InputPlaceholder => "Chat.InputPlaceholder";

    private ConversationContext _currentContext = new();
    private CancellationTokenSource? _responseCts;
    private CancellationTokenSource? _previewLoadCts;
    private CancellationTokenSource? _compressionPreviewCts;
    private CancellationTokenSource? _rawContextCts;
    private CompressionPlan? _compressionPreviewPlan;
    private CompressionCandidate? _compressionPreviewCandidate;
    private CompressionValidationResult? _compressionPreviewValidation;
    private readonly SemaphoreSlim _conversationTransitionLock = new(1, 1);
    private int _conversationEpoch;

    // 记录当前加载的历史对话 ID，如果是新对话则为空
    private string? _currentHistoryId;

    // 记录加载历史时的初始签名，用于判断是否发生了修改
    private string? _initialConversationSignature;
    private string _conversationId = Guid.NewGuid().ToString("N");
    private long _revision;

    // 当前会话的上下文压缩摘要（唯一真源；TokenService.CompressionPreview 仅作设置页展示镜像）。
    private string? _activeContextSummary;
    private string? _orphanedLegacySummary;

    // fork 元数据：当前会话若是从其他会话 fork 出的分支，归档时随快照持久化
    private string? _forkedFromConversationId;
    private string? _forkedFromHistoryId;
    private string? _forkedAtMessageId;

    // 会话内压缩撤销栈：每次压缩入栈一个检查点，撤销时弹出还原。切换/重置会话时清空。
    private readonly Stack<CompressionCheckpoint> _compressionHistory = new();

    private sealed record CompressionCheckpoint(
        string CompressionId,
        long AppliedRevision,
        string? PreviousSummary,
        string? AppliedSummary,
        IReadOnlyList<ChatMessage> Batch,
        DateTime CreatedAt,
        CompressionTriggerMode Mode = CompressionTriggerMode.Manual,
        string SummaryAfterHash = "",
        string CompressionModelFingerprint = "",
        int PromptVersion = 1,
        long PreCompressionTokens = 0,
        long PostCompressionTokens = 0,
        bool UsedLocalFallback = false);

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
        InvalidateCompressionPreview();
        RefreshContextInspectorProperties();
        if (!IsSending) RefreshEffectiveContextPolicy();
        else _policyRefreshPending = true;
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

    public MainConversationViewModel() : this(null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null) { }

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
        ConversationExecutionCoordinator? executionCoordinator = null,
        IContextPolicyProvider? contextPolicyProvider = null,
        ICompressionPlanner? compressionPlanner = null,
        ICompressionCandidateGenerator? compressionCandidateGenerator = null,
        ICompressionValidator? compressionValidator = null)
    {
        Orchestrator = subAgentOrchestrator;
        if (Orchestrator != null)
        {
            Orchestrator.ActiveAgents.CollectionChanged += OnActiveSubAgentsChanged;
            OnActiveSubAgentsChanged(null, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
        _chatService = chatService;
        _configService = configService;
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
        _contextPolicyProvider = contextPolicyProvider;
        _compressionPlanner = compressionPlanner;
        _compressionCandidateGenerator = compressionCandidateGenerator;
        _compressionValidator = compressionValidator;
        if (_contextPolicyProvider != null)
            _contextPolicyProvider.EffectivePolicyChanged += OnEffectivePolicyChanged;

        // Initialize from config
        if (_configService != null)
        {
            var config = _configService.Load();
            CurrentModelName = config.AiModels.MainConversation.Model;
            _requestContentIdentity = ComputeRequestContentIdentity(config);
            CurrentTheme = config.Theme;
            ThemeIcon = config.Theme == "Dark" ? "Moon" : "Sun";
        }

        RefreshEffectiveContextPolicy();

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

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        RefreshToolCallSummaries();
        OnPropertyChanged(nameof(ContextTokensInfo));
        RefreshContextInspectorProperties();
    }

    partial void OnCompressionStatusMessageChanged(string value)
        => RefreshContextInspectorProperties();

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
        _revision++;
        InvalidateCompressionPreview();
        UpdateContextTokensDisplay();
        UpdateBubbleButtonVisibility();
        RefreshContextInspectorProperties();
    }

    private void OnConfigChanged(object? sender, AppConfig config)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed) return;
            var nextContentIdentity = ComputeRequestContentIdentity(config);
            if (!string.Equals(_requestContentIdentity, nextContentIdentity, StringComparison.Ordinal)
                && _tokenService?.HasVisibleUsage == true)
            {
                _tokenService.ApplyEstimatedBaseline(_tokenService.CurrentTokens, _revision);
            }
            _requestContentIdentity = nextContentIdentity;
            InvalidateCompressionPreview();
            CurrentModelName = config.AiModels.MainConversation.Model;
            UpdateBubbleButtonVisibility();
            UpdateContextTokensDisplay();
            RefreshContextInspectorProperties();
        });
    }

    private void OnEffectivePolicyChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed) return;
            if (IsSending)
            {
                _policyRefreshPending = true;
                return;
            }
            RefreshEffectiveContextPolicy();
        });
    }

    private void RefreshEffectiveContextPolicy()
    {
        if (_contextPolicyProvider == null) return;
        try
        {
            var previous = _effectiveContextPolicy;
            _effectiveContextPolicy = _contextPolicyProvider.Resolve(CurrentWorkspace?.ContextPolicyOverride);
            if (_tokenService != null && _effectiveContextPolicy != null)
            {
                if (previous != null
                    && (!string.Equals(previous.ProviderId, _effectiveContextPolicy.ProviderId, StringComparison.Ordinal)
                        || !string.Equals(previous.ExternalModelId, _effectiveContextPolicy.ExternalModelId, StringComparison.Ordinal)
                        || !string.Equals(previous.Metadata.TokenizerHint, _effectiveContextPolicy.Metadata.TokenizerHint, StringComparison.Ordinal))
                    && _tokenService.HasVisibleUsage)
                {
                    _tokenService.ApplyEstimatedBaseline(_tokenService.CurrentTokens, _revision);
                }
                _tokenService.MaxTokens = checked((int)Math.Min(
                    _effectiveContextPolicy.Policy.AvailableInputBudgetTokens,
                    int.MaxValue));
                _tokenService.CompressionThresholdTokens = _effectiveContextPolicy.Policy.CompressionThresholdTokens;
            }
            OnPropertyChanged(nameof(ContextTokensInfo));
            InvalidateCompressionPreview();
            RefreshContextInspectorProperties();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "空闲会话有效上下文策略刷新失败");
        }
    }

    private string ComputeRequestContentIdentity(AppConfig config)
        => string.Join('|',
            config.EnableMcp,
            config.EnableSkills,
            _functionRegistry?.GetToolDeclarationTokenCount() ?? 0,
            _promptService?.GetPrompt(PromptType.MainPersona).GetHashCode(StringComparison.Ordinal) ?? 0);

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
        // 新用户消息/附件不属于上一 Usage 覆盖范围；若已解锁，立即转为显式近似态。
        UpdateContextTokensDisplay(forceEstimateBaseline: true);

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

    partial void OnIsRawContextViewChanged(bool value)
    {
        if (value)
        {
            IsContextInspectorOpen = true;
            SelectedContextInspectorTab = 3;
            IsRawContextView = false;
        }
    }

    partial void OnIsContextInspectorOpenChanged(bool value)
    {
        if (value)
        {
            RefreshContextInspectorProperties();
            if (SelectedContextInspectorTab == 2) RefreshCompressionPlan();
            if (SelectedContextInspectorTab == 3) _ = RefreshRawContextAsync();
            return;
        }
        _compressionPreviewCts?.Cancel();
        _rawContextCts?.Cancel();
    }

    partial void OnSelectedContextInspectorTabChanged(int value)
    {
        if (!IsContextInspectorOpen) return;
        if (value == 2) RefreshCompressionPlan();
        if (value == 3) _ = RefreshRawContextAsync();
    }

    [RelayCommand]
    private void ToggleContextInspector()
        => IsContextInspectorOpen = !IsContextInspectorOpen;

    [RelayCommand]
    private void CloseContextInspector()
        => IsContextInspectorOpen = false;

    [RelayCommand]
    private void RefreshCompressionPlan()
    {
        _compressionPreviewCts?.Cancel();
        _compressionPreviewPlan = null;
        _compressionPreviewCandidate = null;
        _compressionPreviewValidation = null;
        CompressionCandidatePreview = string.Empty;
        IsCompressionPreviewStale = false;

        if (_compressionPlanner == null || _contextPolicyProvider == null)
        {
            CompressionImpactPreview = string.Empty;
            CompressionPreviewStatus = GetString(
                "ContextInspector.Preview.Unavailable",
                "Transactional compression is unavailable.");
            NotifyCompressionPreviewState();
            return;
        }

        var main = _effectiveContextPolicy ?? _contextPolicyProvider.Resolve(CurrentWorkspace?.ContextPolicyOverride);
        var compression = _contextPolicyProvider.ResolveRole(AiModelRole.ContextCompression);
        if (main == null || compression == null)
        {
            CompressionImpactPreview = string.Empty;
            CompressionPreviewStatus = GetString(
                "ContextInspector.Preview.ModelUnavailable",
                "Compression model policy is unavailable.");
            NotifyCompressionPreviewState();
            return;
        }

        UpdateConversationContext();
        var estimate = Math.Max(_currentContext.EstimatedTokenCount, _tokenService?.CurrentTokens ?? 0);
        var result = _compressionPlanner.CreatePlan(new CompressionPlanRequest(
            _conversationId,
            _revision,
            ComputeCompressionContextFingerprint(),
            CompressionTriggerMode.Manual,
            _activeContextSummary,
            Messages.ToList(),
            main.Policy.KeepRecentRounds,
            estimate,
            main.Policy.TargetSummaryTokens,
            main.Policy,
            compression.Policy));
        if (result.Plan == null)
        {
            CompressionImpactPreview = string.Empty;
            CompressionPreviewStatus = result.Reason;
            NotifyCompressionPreviewState();
            return;
        }

        _compressionPreviewPlan = result.Plan;
        CompressionImpactPreview = string.Format(
            GetString(
                "ContextInspector.Preview.ImpactFormat",
                "Compress {0} messages; retain {1}. Estimate {2:N0} tokens; summary target {3:N0}. IDs {4} → {5}."),
            result.Plan.CompressMessageIds.Count,
            result.Plan.RetainMessageIds.Count,
            result.Plan.PreCompressionEstimate,
            result.Plan.TargetSummaryTokens,
            result.Plan.CompressMessageIds.First(),
            result.Plan.CompressMessageIds.Last());
        CompressionPreviewStatus = GetString(
            "ContextInspector.Preview.LocalOnly",
            "Local impact preview only. No model request or charge has occurred.");
        NotifyCompressionPreviewState();
    }

    [RelayCommand]
    private async Task GenerateCompressionCandidateAsync()
    {
        if (!CanGenerateCompressionCandidate
            || _compressionCandidateGenerator == null
            || _compressionValidator == null
            || _compressionPreviewPlan == null)
            return;

        var plan = _compressionPreviewPlan;
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _compressionPreviewCts, cts)?.Cancel();
        IsCompressionPreviewBusy = true;
        CompressionPreviewStatus = GetString(
            "ContextInspector.Preview.Generating",
            "Generating a candidate with the configured compression model; provider charges may apply.");
        try
        {
            var generated = await _compressionCandidateGenerator.GenerateAsync(plan, cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            if (plan.BaseRevision != _revision
                || !string.Equals(plan.BaseContextFingerprint, ComputeCompressionContextFingerprint(), StringComparison.Ordinal))
            {
                IsCompressionPreviewStale = true;
                CompressionPreviewStatus = GetString("ContextInspector.Preview.Stale", "Preview is stale. Refresh the local plan.");
                return;
            }
            if (generated.Candidate == null)
            {
                CompressionPreviewStatus = generated.Error;
                return;
            }
            var validation = _compressionValidator.Validate(plan, generated.Candidate, cts.Token);
            if (!validation.IsValid)
            {
                CompressionPreviewStatus = validation.Error;
                return;
            }
            _compressionPreviewCandidate = generated.Candidate;
            _compressionPreviewValidation = validation;
            CompressionCandidatePreview = generated.Candidate.Summary;
            CompressionPreviewStatus = string.Format(
                GetString(
                    "ContextInspector.Preview.CandidateReady",
                    "Candidate ready. Estimated {0:N0} → {1:N0} tokens. Review it before Apply."),
                plan.PreCompressionEstimate,
                validation.PostCompressionEstimate);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            CompressionPreviewStatus = GetString("ContextInspector.Preview.Cancelled", "Candidate generation cancelled; conversation unchanged.");
        }
        finally
        {
            _ = Interlocked.CompareExchange(ref _compressionPreviewCts, null, cts);
            cts.Dispose();
            IsCompressionPreviewBusy = false;
            NotifyCompressionPreviewState();
        }
    }

    [RelayCommand]
    private async Task ApplyCompressionCandidateAsync()
    {
        if (!CanApplyCompressionCandidate
            || _compressionCommitter == null
            || _compressionPreviewPlan == null
            || _compressionPreviewCandidate == null
            || _compressionPreviewValidation == null)
            return;
        var plan = _compressionPreviewPlan;
        var candidate = _compressionPreviewCandidate;
        var validation = _compressionPreviewValidation;
        IsCompressionPreviewBusy = true;
        try
        {
            var transition = new CompressionTransition(
                plan.PlanId,
                candidate.CandidateId,
                _conversationId,
                plan.BaseRevision,
                plan.BaseContextFingerprint,
                CompressionTriggerMode.Manual,
                plan.CompressMessageIds,
                plan.ExistingSummary,
                candidate.Summary,
                candidate.CompressionModelFingerprint,
                candidate.PromptVersion,
                plan.PreCompressionEstimate,
                validation.PostCompressionEstimate,
                candidate.UsedLocalFallback);
            var committed = await _compressionCommitter.CommitCompressionAsync(transition);
            if (!committed.IsCommitted)
            {
                IsCompressionPreviewStale = committed.Status == CompressionCommitStatus.Stale;
                CompressionPreviewStatus = committed.Error;
                return;
            }
            CompressionPreviewStatus = GetString("ContextInspector.Preview.Applied", "Candidate applied and persisted atomically.");
            _compressionPreviewPlan = null;
            _compressionPreviewCandidate = null;
            _compressionPreviewValidation = null;
            CompressionImpactPreview = string.Empty;
            CompressionCandidatePreview = string.Empty;
            RefreshContextInspectorProperties();
        }
        finally
        {
            IsCompressionPreviewBusy = false;
            NotifyCompressionPreviewState();
        }
    }

    [RelayCommand]
    private void CancelCompressionPreview()
        => _compressionPreviewCts?.Cancel();

    [RelayCommand]
    private async Task RefreshRawContextAsync()
    {
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _rawContextCts, cts)?.Cancel();
        IsRawContextLoading = true;
        RawContextStatus = string.Empty;
        RawContextEntries.Clear();

        if (_chatService == null)
        {
            RawContextStatus = GetString("Chat.Raw.ServiceUnavailable", "Chat service is unavailable.");
            IsRawContextLoading = false;
            return;
        }

        UpdateConversationContext();
        var snapshot = _currentContext.Clone();
        var revision = _revision;
        var fingerprint = ComputeCompressionContextFingerprint();
        try
        {
            var entries = await Task.Run(() => _chatService.BuildRawContext(snapshot, cts.Token), cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            if (!IsContextInspectorOpen
                || revision != _revision
                || !string.Equals(fingerprint, ComputeCompressionContextFingerprint(), StringComparison.Ordinal))
            {
                RawContextStatus = GetString("ContextInspector.Raw.Stale", "RAW snapshot became stale; refresh it.");
                return;
            }
            foreach (var entry in entries) RawContextEntries.Add(entry);
            RawContextSnapshotInfo = $"{DateTime.Now:g} · RequestFormat v1 · Revision {revision} · {fingerprint[..Math.Min(16, fingerprint.Length)]}";
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            RawContextStatus = GetString("ContextInspector.Raw.Cancelled", "RAW context build cancelled.");
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "构建 raw 上下文失败");
            RawContextStatus = string.Format(
                GetString("ContextInspector.Raw.Failed", "Failed to build RAW context: {0}"),
                ex.Message);
        }
        finally
        {
            _ = Interlocked.CompareExchange(ref _rawContextCts, null, cts);
            cts.Dispose();
            IsRawContextLoading = false;
        }
    }

    [RelayCommand]
    private void CancelRawContextBuild() => _rawContextCts?.Cancel();

    [RelayCommand]
    private void CopyContextText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (App.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            _ = TopLevel.GetTopLevel(desktop.MainWindow)?.Clipboard?.SetTextAsync(text);
    }

    private void InvalidateCompressionPreview()
    {
        if (_compressionPreviewPlan == null) return;
        IsCompressionPreviewStale = true;
        CompressionPreviewStatus = GetString("ContextInspector.Preview.Stale", "Preview is stale. Refresh the local plan.");
        _compressionPreviewCts?.Cancel();
        NotifyCompressionPreviewState();
    }

    private void NotifyCompressionPreviewState()
    {
        OnPropertyChanged(nameof(CanGenerateCompressionCandidate));
        OnPropertyChanged(nameof(CanApplyCompressionCandidate));
        OnPropertyChanged(nameof(HasCompressionImpactPreview));
        OnPropertyChanged(nameof(HasCompressionCandidatePreview));
    }

    private void RefreshContextInspectorProperties()
    {
        OnPropertyChanged(nameof(ContextInspectorModelText));
        OnPropertyChanged(nameof(ContextInspectorMatchText));
        OnPropertyChanged(nameof(ContextInspectorPolicyText));
        OnPropertyChanged(nameof(ContextInspectorUsageText));
        OnPropertyChanged(nameof(ContextInspectorUsageDetails));
        OnPropertyChanged(nameof(ContextInspectorWorkspaceText));
        OnPropertyChanged(nameof(ContextInspectorCompressionModelText));
        OnPropertyChanged(nameof(ContextInspectorWarningsText));
        OnPropertyChanged(nameof(HasContextInspectorWarnings));
        OnPropertyChanged(nameof(CompressionSummaryDetails));
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
    /// 从该条用户消息处发起分支请求：本会话不做任何原地修改，仅校验后触发
    /// <see cref="MessageForkRequested"/>。实际分支由外层会话项转发、窗口 ViewModel 执行：
    /// 先持久化父会话，再创建全新分支会话（保留 fork 点之前上下文、附件物理克隆、
    /// fork 点消息回填输入区），插入会话树并选中。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanForkFromMessage))]
    private void ForkFromMessage(ChatMessage? message)
    {
        if (message == null || !CanForkFromMessage(message)) return;
        MessageForkRequested?.Invoke(this, new MessageForkRequestedEventArgs(message));
    }

    // 消息级分支仅在「可回滚的 user 消息、非发送/压缩中」时可用。分支创建本身在
    // MainWindowViewModel 以「先持久化父会话、再建全新分支会话」的安全模式执行，
    // 本会话 ViewModel 不再承担身份切换/消息截断，避免分支覆盖父历史行。
    private bool CanForkFromMessage(ChatMessage? message) =>
        message != null
        && message.Role == "user"
        && message.CanRewind
        && !message.IsCompressed
        && !IsSending
        && !IsCompressing;

    /// <summary>
    /// 把一批附件挂回输入区待发送列表；超出上限的部分按需清理物理文件并提示。
    /// internal：新分支会话由 MainWindowViewModel 创建，需要回填 fork 点附件克隆。
    /// </summary>
    internal void RestoreAttachmentsToPending(IReadOnlyList<ChatAttachment> attachments, bool deleteOverflowFiles = false)
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
        string targetConversationId,
        IReadOnlyDictionary<string, ChatAttachment> clonedById)
    {
        if (_imageGenerationSessionService == null) return;

        var parentSnapshot = await _imageGenerationSessionService.CreateSnapshotAsync(parentConversationId);
        if (parentSnapshot == null) return;

        var branchSnapshot = new ImageGenerationSessionSnapshot
        {
            ConversationId = targetConversationId,
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
        _compressionPreviewCts?.Cancel();
        _rawContextCts?.Cancel();
        IsContextInspectorOpen = false;
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
            SchemaVersion = ConversationPersistenceSnapshot.CurrentSchemaVersion,
            ConversationId = _conversationId,
            HistoryId = _currentHistoryId,
            Revision = _revision,
            CreatedAt = messages.FirstOrDefault()?.Timestamp ?? DateTime.Now,
            UpdatedAt = DateTime.Now,
            ContextSummary = _activeContextSummary,
            OrphanedLegacySummary = _orphanedLegacySummary,
            CompressionHistory = CaptureCompressionHistory(),
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
        _revision = 0;
        _forkedFromConversationId = null;
        _forkedFromHistoryId = null;
        _forkedAtMessageId = null;

        _compressionHistory.Clear();
        SetActiveContextSummary(null);
        SetOrphanedLegacySummary(null);
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
        requestContext.Revision = _revision;
        var requestContentIdentityAtStart = _requestContentIdentity;
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
        requestContext.Revision = _revision;

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
                        // 工具结果发生在刚才的 API Usage 之后，必须降为近似态等待下一轮 Usage 重锚。
                        UpdateConversationContext();
                        UpdateContextTokensDisplay(forceEstimateBaseline: true);
                    }
                    requestContext.Revision = _revision;
                },
                onUsageReported: usage =>
                {
                    if (!IsCurrentConversationEpoch(epoch)) return;
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (!IsCurrentConversationEpoch(epoch) || _tokenService == null) return;
                        var currentRole = _configService?.Load().AiModels.MainConversation;
                        if (_tokenService.TryApplyUsage(
                                usage,
                                currentRole?.ProviderId,
                                currentRole?.Model,
                                _revision))
                        {
                            OnPropertyChanged(nameof(ContextTokensInfo));
                            RefreshContextInspectorProperties();
                        }
                        else
                        {
                            _logger.Warning(
                                "UsageObserved/Rejected RequestId={RequestId} Provider={ProviderId} Model={Model}",
                                usage.RequestId, usage.ProviderId, usage.ModelId);
                        }
                    });
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
                addToContext: addToContext,
                onCompressionTransition: (transition, token) =>
                    CommitAutomaticCompressionAsync(
                        transition,
                        epoch,
                        requestContentIdentityAtStart,
                        token),
                onContextWarning: warning =>
                {
                    if (IsCurrentConversationEpoch(epoch)) CompressionStatusMessage = warning;
                }))
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
                if (_policyRefreshPending)
                {
                    _policyRefreshPending = false;
                    RefreshEffectiveContextPolicy();
                }
                UpdateContextTokensDisplay();
                UpdateBubbleButtonVisibility();
                MarkPersistenceStateChanged();

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
        OnPropertyChanged(nameof(ActiveContextSummary));
        OnPropertyChanged(nameof(HasActiveContextSummary));
        OnPropertyChanged(nameof(CompressionSummaryDetails));
        RefreshContextInspectorProperties();
    }

    private void SetOrphanedLegacySummary(string? summary)
    {
        _orphanedLegacySummary = string.IsNullOrWhiteSpace(summary) ? null : summary;
        OnPropertyChanged(nameof(OrphanedLegacySummary));
        OnPropertyChanged(nameof(HasOrphanedLegacySummary));
        RefreshContextInspectorProperties();
    }

    private void MarkPersistenceStateChanged()
    {
        _revision++;
        PersistenceStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void MarkPersistenceMetadataChanged() => MarkPersistenceStateChanged();

    public void AttachCompressionCommitter(IConversationCompressionCommitter committer)
    {
        _compressionCommitter = committer;
    }

    public ConversationPersistenceSnapshot CapturePersistenceSnapshot(
        string historyId,
        string title,
        DateTime updatedAt,
        bool isPinned,
        string? workspaceId)
    {
        var messages = Messages
            .Where(ConversationPersistenceHelper.ShouldPersistMessage)
            .Select(ConversationPersistenceHelper.CloneMessage)
            .ToList();
        return new ConversationPersistenceSnapshot
        {
            ConversationId = _conversationId,
            HistoryId = historyId,
            Revision = _revision,
            Title = title,
            CreatedAt = messages.FirstOrDefault()?.Timestamp ?? updatedAt,
            UpdatedAt = updatedAt,
            ContextSummary = _activeContextSummary,
            OrphanedLegacySummary = _orphanedLegacySummary,
            CompressionHistory = CaptureCompressionHistory(),
            ForkedFromConversationId = _forkedFromConversationId,
            ForkedFromHistoryId = _forkedFromHistoryId,
            ForkedAtMessageId = _forkedAtMessageId,
            Messages = messages,
            WorkspaceId = workspaceId,
            Draft = InputText,
            IsPinned = isPinned,
            RuntimeStatus = IsSending ? "interrupted" : "idle"
        };
    }

    /// <summary>
    /// 为分支会话捕获一份快照（**不做 fork 相关的身份/消息变更**，仅按既定模式
    /// <see cref="FinalizePendingAssistantMessages"/> 冻结未定型助手消息）：
    /// <paramref name="forkPointMessage"/> 为空时全量复制，否则只保留 fork 点之前的消息
    /// （fork 点消息回填为 Draft，其附件克隆作为待发送附件返回）。所有保留附件均物理克隆，
    /// 图像生成会话复制到 <paramref name="forkConversationId"/> 下。
    /// 父会话必须先由调用方持久化。
    /// </summary>
    public async Task<(ConversationPersistenceSnapshot Snapshot, IReadOnlyList<ChatAttachment> PendingAttachmentClones)>
        CaptureForkSnapshotAsync(
            string forkHistoryId,
            string forkConversationId,
            string? forkedFromHistoryId,
            string title,
            DateTime updatedAt,
            bool isPinned,
            string? workspaceId,
            ChatMessage? forkPointMessage = null)
    {
        FinalizePendingAssistantMessages();

        IEnumerable<ChatMessage> keptSource = Messages;
        if (forkPointMessage != null)
        {
            int forkIndex = Messages.IndexOf(forkPointMessage);
            if (forkIndex < 0)
            {
                throw new ArgumentException("fork 点消息不在当前会话中", nameof(forkPointMessage));
            }

            keptSource = Messages.Take(forkIndex);
        }

        var keptClones = new List<ChatMessage>();
        var clonedById = new Dictionary<string, ChatAttachment>(StringComparer.Ordinal);
        foreach (var original in keptSource)
        {
            if (!ConversationPersistenceHelper.ShouldPersistMessage(original))
            {
                continue;
            }

            var clone = ConversationPersistenceHelper.CloneMessage(original);
            if (_attachmentStoreService != null)
            {
                for (int i = 0; i < clone.Attachments.Count; i++)
                {
                    var physical = await _attachmentStoreService.CloneStoredAttachmentAsync(original.Attachments[i]);
                    physical.PreviewImage = original.Attachments[i].PreviewImage;
                    clone.Attachments[i] = physical;
                    clonedById[physical.Id] = physical;
                }
            }

            clone.ResolveSegmentAttachments();
            keptClones.Add(clone);
        }

        var pendingClones = forkPointMessage == null
            ? new List<ChatAttachment>()
            : await CloneAttachmentsForForkAsync(forkPointMessage.Attachments.ToList());

        await CopyImageSessionForForkAsync(_conversationId, forkConversationId, clonedById);

        var snapshot = new ConversationPersistenceSnapshot
        {
            ConversationId = forkConversationId,
            HistoryId = forkHistoryId,
            Revision = 0,
            Title = title,
            CreatedAt = keptClones.FirstOrDefault()?.Timestamp ?? updatedAt,
            UpdatedAt = updatedAt,
            ContextSummary = _activeContextSummary,
            OrphanedLegacySummary = _orphanedLegacySummary,
            CompressionHistory = CaptureCompressionHistory(),
            ForkedFromConversationId = _conversationId,
            ForkedFromHistoryId = forkedFromHistoryId,
            ForkedAtMessageId = forkPointMessage?.Id,
            Messages = keptClones,
            WorkspaceId = workspaceId,
            Draft = forkPointMessage?.Content ?? string.Empty,
            IsPinned = isPinned,
            RuntimeStatus = "idle"
        };

        return (snapshot, pendingClones);
    }

    private List<CompressionCheckpointRecord> CaptureCompressionHistory()
    {
        return _compressionHistory
            .Reverse()
            .Select(checkpoint => new CompressionCheckpointRecord
            {
                CompressionId = checkpoint.CompressionId,
                AppliedRevision = checkpoint.AppliedRevision,
                MessageIds = checkpoint.Batch.Select(message => message.Id).ToList(),
                SummaryBefore = checkpoint.PreviousSummary,
                SummaryAfter = checkpoint.AppliedSummary,
                SummaryAfterHash = checkpoint.SummaryAfterHash,
                Mode = checkpoint.Mode,
                CompressionModelFingerprint = checkpoint.CompressionModelFingerprint,
                PromptVersion = checkpoint.PromptVersion,
                PreCompressionTokens = checkpoint.PreCompressionTokens,
                PostCompressionTokens = checkpoint.PostCompressionTokens,
                UsedLocalFallback = checkpoint.UsedLocalFallback,
                CreatedAt = checkpoint.CreatedAt
            })
            .ToList();
    }

    private void UpdateConversationContext()
    {
        _currentContext.Clear();
        _currentContext.ConversationId = _conversationId;
        _currentContext.Revision = _revision;

        // 赋予当前的压缩摘要（如果有）——读会话级真源，而非 UI 单例
        _currentContext.SetSummary(string.IsNullOrEmpty(_activeContextSummary) ? null : _activeContextSummary);

        foreach (var msg in Messages)
        {
            // 已被压缩归档的消息不再进入发送给大模型的 context.messages 列表
            if (msg.IsCompressed) continue;

            if (msg.Role == "user")
            {
                _currentContext.AddUserMessage(msg.Content, msg.Timestamp, msg.Attachments, msg.Id);
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
                        msg.OutputAudioReferenceId,
                        msg.Id);
                }
            }
            else if (msg.Role == "tool")
            {
                _currentContext.AddToolMessage(msg.Content, msg.ToolCallId, msg.Id);
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
            _tokenService.ApplyEstimatedBaseline(estimated, _revision);
        }
        else
        {
            _tokenService.RefreshEstimate(estimated, _revision);
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

        // CanRewind 是逐消息状态，变化时需手动刷新命令可用性（IsSending/IsCompressing
        // 已通过 NotifyCanExecuteChangedFor 自动刷新）。
        ForkFromMessageCommand.NotifyCanExecuteChanged();
    }

    public async Task RefreshSettingsAsync()
    {
        if (_configService != null)
        {
            _ = await _configService.LoadAsync();
            RefreshEffectiveContextPolicy();
            UpdateContextTokensDisplay();
        }
    }

    public async Task InternalCompressContextAsync(CancellationToken cancellationToken = default)
    {
        if (_configService == null) return;

        IsCompressing = true;
        CompressionStatusMessage = string.Empty;
        try
        {
            if (_compressionPlanner == null
                || _compressionCandidateGenerator == null
                || _compressionValidator == null)
            {
                CompressionStatusMessage = "Transactional compression is unavailable; the conversation remains unchanged.";
                return;
            }
            if (_compressionCommitter == null)
            {
                CompressionStatusMessage = "Conversation persistence is unavailable; compression was not applied.";
                return;
            }
            var main = _effectiveContextPolicy ?? _contextPolicyProvider?.Resolve(CurrentWorkspace?.ContextPolicyOverride);
            var compression = _contextPolicyProvider?.ResolveRole(AiModelRole.ContextCompression);
            if (main == null || compression == null)
            {
                CompressionStatusMessage = "Compression model policy is unavailable; the conversation remains unchanged.";
                return;
            }

            UpdateConversationContext();
            var fingerprint = ComputeCompressionContextFingerprint();
            var preEstimate = Math.Max(
                _currentContext.EstimatedTokenCount,
                _tokenService?.CurrentTokens ?? 0);
            var planResult = _compressionPlanner.CreatePlan(new CompressionPlanRequest(
                _conversationId,
                _revision,
                fingerprint,
                CompressionTriggerMode.Manual,
                _activeContextSummary,
                Messages.ToList(),
                main.Policy.KeepRecentRounds,
                preEstimate,
                main.Policy.TargetSummaryTokens,
                main.Policy,
                compression.Policy));
            if (planResult.Plan == null)
            {
                CompressionStatusMessage = planResult.Reason;
                return;
            }

            var generated = await _compressionCandidateGenerator.GenerateAsync(planResult.Plan, cancellationToken);
            if (generated.Candidate == null)
            {
                CompressionStatusMessage = generated.Error;
                return;
            }
            var validation = _compressionValidator.Validate(planResult.Plan, generated.Candidate, cancellationToken);
            if (!validation.IsValid)
            {
                CompressionStatusMessage = validation.Error;
                return;
            }

            var transition = new CompressionTransition(
                planResult.Plan.PlanId,
                generated.Candidate.CandidateId,
                _conversationId,
                planResult.Plan.BaseRevision,
                planResult.Plan.BaseContextFingerprint,
                planResult.Plan.TriggerMode,
                planResult.Plan.CompressMessageIds,
                planResult.Plan.ExistingSummary,
                generated.Candidate.Summary,
                generated.Candidate.CompressionModelFingerprint,
                generated.Candidate.PromptVersion,
                planResult.Plan.PreCompressionEstimate,
                validation.PostCompressionEstimate,
                generated.Candidate.UsedLocalFallback);
            var committed = await _compressionCommitter.CommitCompressionAsync(transition, cancellationToken);
            if (!committed.IsCommitted)
            {
                CompressionStatusMessage = committed.Error;
                return;
            }
            CompressionStatusMessage = string.Empty;
            _logger.Information("事务化上下文压缩已提交: Revision={Revision}, Messages={Count}",
                committed.Revision, transition.MessageIds.Count);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        finally
        {
            IsCompressing = false;
            await NotifySchedulerAvailabilityAsync();
        }
    }

    public (ConversationPersistenceSnapshot? Snapshot, string Error) PrepareCompressionCommitSnapshot(
        CompressionTransition transition,
        string historyId,
        string title,
        DateTime updatedAt,
        bool isPinned,
        string? workspaceId)
    {
        if (transition.ConversationId != _conversationId
            || transition.BaseRevision != _revision
            || !string.Equals(transition.BaseContextFingerprint, ComputeCompressionContextFingerprint(), StringComparison.Ordinal)
            || !string.Equals(transition.SummaryBefore, _activeContextSummary, StringComparison.Ordinal))
            return (null, "Compression plan is stale.");
        if (transition.MessageIds.Count == 0
            || transition.MessageIds.Distinct(StringComparer.Ordinal).Count() != transition.MessageIds.Count)
            return (null, "Compression transition contains invalid message identities.");

        var snapshot = CapturePersistenceSnapshot(historyId, title, updatedAt, isPinned, workspaceId);
        var byId = snapshot.Messages.ToDictionary(message => message.Id, StringComparer.Ordinal);
        foreach (var id in transition.MessageIds)
        {
            if (!byId.TryGetValue(id, out var message) || message.IsCompressed)
                return (null, "Compression plan message set is stale or incomplete.");
            message.IsCompressed = true;
        }
        snapshot.Revision = transition.BaseRevision + 1;
        snapshot.ContextSummary = transition.SummaryAfter;
        snapshot.CompressionHistory.Add(CreateCheckpointRecord(transition, snapshot.Revision));
        if (snapshot.CompressionHistory.Count > 20)
            snapshot.CompressionHistory = snapshot.CompressionHistory.TakeLast(20).ToList();
        return (snapshot, string.Empty);
    }

    private async Task<CompressionCommitResult> CommitAutomaticCompressionAsync(
        CompressionTransition transition,
        int epoch,
        string requestContentIdentityAtStart,
        CancellationToken cancellationToken)
    {
        if (_compressionCommitter == null)
            return CompressionCommitResult.Failed(
                CompressionCommitStatus.PersistenceUnavailable,
                _revision,
                "Conversation persistence is unavailable.");

        CompressionTransition? liveTransition = null;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!IsCurrentConversationEpoch(epoch)
                || transition.ConversationId != _conversationId
                || transition.BaseRevision != _revision
                || !string.Equals(requestContentIdentityAtStart, _requestContentIdentity, StringComparison.Ordinal)
                || !string.Equals(transition.SummaryBefore, _activeContextSummary, StringComparison.Ordinal))
                return;
            var activeIds = Messages
                .Where(message => !message.IsCompressed)
                .Select(message => message.Id)
                .ToHashSet(StringComparer.Ordinal);
            if (transition.MessageIds.Any(id => !activeIds.Contains(id))) return;
            liveTransition = transition with
            {
                BaseContextFingerprint = ComputeCompressionContextFingerprint()
            };
        });
        if (liveTransition == null)
            return CompressionCommitResult.Failed(
                CompressionCommitStatus.Stale,
                _revision,
                "Automatic compression plan became stale before commit.");
        return await _compressionCommitter.CommitCompressionAsync(liveTransition, cancellationToken);
    }

    public bool PublishCompressionCommit(CompressionTransition transition, long committedRevision, string historyId)
    {
        if (transition.BaseRevision != _revision
            || committedRevision != transition.BaseRevision + 1
            || !string.Equals(transition.BaseContextFingerprint, ComputeCompressionContextFingerprint(), StringComparison.Ordinal))
            return false;
        var byId = Messages.ToDictionary(message => message.Id, StringComparer.Ordinal);
        var batch = new List<ChatMessage>(transition.MessageIds.Count);
        foreach (var id in transition.MessageIds)
        {
            if (!byId.TryGetValue(id, out var message) || message.IsCompressed) return false;
            batch.Add(message);
        }
        foreach (var message in batch) message.IsCompressed = true;
        SetActiveContextSummary(transition.SummaryAfter);
        _compressionHistory.Push(CreateCheckpoint(transition, committedRevision, batch));
        TrimCompressionHistory();
        _currentHistoryId = historyId;
        _revision = committedRevision;
        UpdateConversationContext();
        UpdateContextTokensDisplay(forceEstimateBaseline: true);
        UpdateBubbleButtonVisibility();
        UndoCompressionCommand.NotifyCanExecuteChanged();
        return true;
    }

    public string CaptureCompressionContextFingerprint() => ComputeCompressionContextFingerprint();

    private string ComputeCompressionContextFingerprint()
    {
        var messages = Messages
            .Where(ConversationPersistenceHelper.ShouldPersistMessage)
            .Select(ConversationPersistenceHelper.CloneMessage)
            .ToList();
        var material = JsonSerializer.Serialize(new
        {
            ConversationId = _conversationId,
            Summary = _activeContextSummary,
            RequestContentIdentity = _requestContentIdentity,
            WorkspaceId = _currentContext.WorkspaceId,
            Messages = messages
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    }

    private static CompressionCheckpointRecord CreateCheckpointRecord(CompressionTransition transition, long revision) => new()
    {
        CompressionId = transition.CandidateId,
        AppliedRevision = revision,
        MessageIds = transition.MessageIds.ToList(),
        SummaryBefore = transition.SummaryBefore,
        SummaryAfter = transition.SummaryAfter,
        SummaryAfterHash = HashSummary(transition.SummaryAfter),
        Mode = transition.Mode,
        CompressionModelFingerprint = transition.CompressionModelFingerprint,
        PromptVersion = transition.PromptVersion,
        PreCompressionTokens = transition.PreCompressionTokens,
        PostCompressionTokens = transition.PostCompressionTokens,
        UsedLocalFallback = transition.UsedLocalFallback,
        CreatedAt = DateTime.UtcNow
    };

    private static CompressionCheckpoint CreateCheckpoint(
        CompressionTransition transition,
        long revision,
        IReadOnlyList<ChatMessage> batch) => new(
        transition.CandidateId,
        revision,
        transition.SummaryBefore,
        transition.SummaryAfter,
        batch,
        DateTime.UtcNow,
        transition.Mode,
        HashSummary(transition.SummaryAfter),
        transition.CompressionModelFingerprint,
        transition.PromptVersion,
        transition.PreCompressionTokens,
        transition.PostCompressionTokens,
        transition.UsedLocalFallback);

    private static string HashSummary(string summary)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(summary))).ToLowerInvariant();

    private void TrimCompressionHistory()
    {
        if (_compressionHistory.Count <= 20) return;
        var newest = _compressionHistory.Take(20).Reverse().ToArray();
        _compressionHistory.Clear();
        foreach (var checkpoint in newest) _compressionHistory.Push(checkpoint);
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
    private async Task UndoCompressionAsync() => await InternalUndoCompressionAsync();

    private bool CanUndoCompression() => !IsSending && !IsCompressing && _compressionHistory.Count > 0;

    /// <summary>
    /// 撤销上一次上下文压缩：把该批被归档的消息重新激活，并恢复压缩前的摘要。
    /// 仅支持会话内（内存）撤销；切换/重置会话后栈清空。
    /// </summary>
    public async Task<bool> InternalUndoCompressionAsync(CancellationToken cancellationToken = default)
    {
        if (_compressionHistory.Count == 0 || IsSending || IsCompressing) return false;

        if (_compressionCommitter != null)
        {
            IsCompressing = true;
            try
            {
                var checkpoint = _compressionHistory.Peek();
                var transition = new CompressionUndoTransition(
                    checkpoint.CompressionId,
                    _conversationId,
                    _revision,
                    ComputeCompressionContextFingerprint(),
                    checkpoint.Batch.Select(message => message.Id).ToArray(),
                    _activeContextSummary,
                    checkpoint.PreviousSummary);
                var committed = await _compressionCommitter.CommitUndoCompressionAsync(transition, cancellationToken);
                CompressionStatusMessage = committed.IsCommitted ? string.Empty : committed.Error;
                return committed.IsCommitted;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            finally
            {
                IsCompressing = false;
                await NotifySchedulerAvailabilityAsync();
            }
        }

        return ApplyLegacyUndoInMemory();
    }

    /// <summary>Compatibility wrapper for isolated legacy tests; production commands use the async transaction.</summary>
    public bool InternalUndoCompression()
        => _compressionCommitter == null
            ? ApplyLegacyUndoInMemory()
            : InternalUndoCompressionAsync().GetAwaiter().GetResult();

    private bool ApplyLegacyUndoInMemory()
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
        MarkPersistenceStateChanged();

        _logger.Information("已撤销上一次上下文压缩，恢复 {Count} 条消息", checkpoint.Batch.Count);
        return true;
    }

    public (ConversationPersistenceSnapshot? Snapshot, string Error) PrepareCompressionUndoSnapshot(
        CompressionUndoTransition transition,
        string historyId,
        string title,
        DateTime updatedAt,
        bool isPinned,
        string? workspaceId)
    {
        if (_compressionHistory.Count == 0
            || transition.ConversationId != _conversationId
            || transition.BaseRevision != _revision
            || !string.Equals(transition.BaseContextFingerprint, ComputeCompressionContextFingerprint(), StringComparison.Ordinal))
            return (null, "Compression undo is stale.");
        var checkpoint = _compressionHistory.Peek();
        if (!string.Equals(checkpoint.CompressionId, transition.CompressionId, StringComparison.Ordinal)
            || !string.Equals(_activeContextSummary, transition.SummaryBeforeUndo, StringComparison.Ordinal)
            || !checkpoint.Batch.Select(message => message.Id).SequenceEqual(transition.MessageIds, StringComparer.Ordinal))
            return (null, "Only the latest complete compression checkpoint can be undone.");
        if (!string.IsNullOrWhiteSpace(checkpoint.SummaryAfterHash)
            && !string.Equals(checkpoint.SummaryAfterHash, HashSummary(_activeContextSummary ?? string.Empty), StringComparison.Ordinal))
            return (null, "The active summary no longer matches the checkpoint.");

        var snapshot = CapturePersistenceSnapshot(historyId, title, updatedAt, isPinned, workspaceId);
        var byId = snapshot.Messages.ToDictionary(message => message.Id, StringComparer.Ordinal);
        foreach (var id in transition.MessageIds)
        {
            if (!byId.TryGetValue(id, out var message) || !message.IsCompressed)
                return (null, "Compression undo message set is incomplete.");
            message.IsCompressed = false;
        }
        snapshot.Revision = transition.BaseRevision + 1;
        snapshot.ContextSummary = transition.SummaryAfterUndo;
        if (snapshot.CompressionHistory.Count == 0
            || !string.Equals(snapshot.CompressionHistory[^1].CompressionId, transition.CompressionId, StringComparison.Ordinal))
            return (null, "Persisted checkpoint stack does not match the undo request.");
        snapshot.CompressionHistory.RemoveAt(snapshot.CompressionHistory.Count - 1);
        return (snapshot, string.Empty);
    }

    public bool PublishCompressionUndo(CompressionUndoTransition transition, long committedRevision, string historyId)
    {
        if (_compressionHistory.Count == 0
            || transition.BaseRevision != _revision
            || committedRevision != transition.BaseRevision + 1
            || !string.Equals(transition.BaseContextFingerprint, ComputeCompressionContextFingerprint(), StringComparison.Ordinal))
            return false;
        var checkpoint = _compressionHistory.Peek();
        if (!string.Equals(checkpoint.CompressionId, transition.CompressionId, StringComparison.Ordinal)) return false;
        var byId = Messages.ToDictionary(message => message.Id, StringComparer.Ordinal);
        var batch = new List<ChatMessage>();
        foreach (var id in transition.MessageIds)
        {
            if (!byId.TryGetValue(id, out var message) || !message.IsCompressed) return false;
            batch.Add(message);
        }
        foreach (var message in batch) message.IsCompressed = false;
        _compressionHistory.Pop();
        SetActiveContextSummary(transition.SummaryAfterUndo);
        _currentHistoryId = historyId;
        _revision = committedRevision;
        UpdateConversationContext();
        UpdateContextTokensDisplay(forceEstimateBaseline: true);
        UpdateBubbleButtonVisibility();
        UndoCompressionCommand.NotifyCanExecuteChanged();
        _logger.Information("已原子撤销上下文压缩，恢复 {Count} 条消息", batch.Count);
        return true;
    }

    /// <summary>
    /// Restores a persisted item into a newly-created conversation VM.
    /// This does not stage or replace another live conversation; each tree item owns its own
    /// conversation VM.
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
        _revision = Math.Max(0, history.Revision);
        _forkedFromConversationId = history.ForkedFromConversationId;
        _forkedFromHistoryId = history.ForkedFromHistoryId;
        _forkedAtMessageId = history.ForkedAtMessageId;

        CurrentWorkspace = !string.IsNullOrEmpty(history.WorkspaceId)
            ? AvailableWorkspaces.FirstOrDefault(workspace => workspace.Id == history.WorkspaceId)
            : null;

        _compressionHistory.Clear();
        SetActiveContextSummary(history.ContextSummary);
        SetOrphanedLegacySummary(history.OrphanedLegacySummary);
        UndoCompressionCommand.NotifyCanExecuteChanged();

        var restoredMessages = ConversationPersistenceHelper.CloneMessages(history.Messages ?? []);
        foreach (var message in restoredMessages)
        {
            ConversationPersistenceHelper.PrepareRestoredMessage(message);
        }
        _isBulkLoadingMessages = true;
        try
        {
            Messages.ReplaceAll(restoredMessages);
        }
        finally
        {
            _isBulkLoadingMessages = false;
        }
        RestoreCompressionHistory(history.CompressionHistory, restoredMessages);
        LoadPreviewsInBackground(restoredMessages);

        _ = ReconcileImageGenerationSessionAsync();
        _initialConversationSignature = CreateConversationSignature();
        _tokenService?.ResetUsage();
        UpdateConversationContext();
        UpdateContextTokensDisplay();
        UpdateBubbleButtonVisibility();
    }

    private void RestoreCompressionHistory(
        IEnumerable<CompressionCheckpointRecord>? records,
        IReadOnlyCollection<ChatMessage> restoredMessages)
    {
        if (records == null) return;
        var byId = restoredMessages.ToDictionary(message => message.Id, StringComparer.Ordinal);
        foreach (var record in records.OrderBy(record => record.AppliedRevision))
        {
            var batch = new List<ChatMessage>();
            var complete = true;
            foreach (var id in record.MessageIds)
            {
                if (!byId.TryGetValue(id, out var message))
                {
                    complete = false;
                    break;
                }
                batch.Add(message);
            }
            if (!complete || batch.Count == 0) continue;
            _compressionHistory.Push(new CompressionCheckpoint(
                record.CompressionId,
                record.AppliedRevision,
                record.SummaryBefore,
                record.SummaryAfter,
                batch,
                record.CreatedAt,
                record.Mode,
                record.SummaryAfterHash,
                record.CompressionModelFingerprint,
                record.PromptVersion,
                record.PreCompressionTokens,
                record.PostCompressionTokens,
                record.UsedLocalFallback));
        }
        UndoCompressionCommand.NotifyCanExecuteChanged();
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
            SchemaVersion = ConversationPersistenceSnapshot.CurrentSchemaVersion,
            Revision = _revision,
            ConversationId = _conversationId,
            CurrentHistoryId = _currentHistoryId,
            InitialConversationSignature = _initialConversationSignature,
            ContextSummary = _activeContextSummary,
            OrphanedLegacySummary = _orphanedLegacySummary,
            CompressionHistory = CaptureCompressionHistory(),
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
        _revision = Math.Max(0, snapshot.Revision);
        _initialConversationSignature = snapshot.InitialConversationSignature;
        _forkedFromConversationId = snapshot.ForkedFromConversationId;
        _forkedFromHistoryId = snapshot.ForkedFromHistoryId;
        _forkedAtMessageId = snapshot.ForkedAtMessageId;

        _compressionHistory.Clear();
        SetActiveContextSummary(snapshot.ContextSummary);
        SetOrphanedLegacySummary(snapshot.OrphanedLegacySummary);
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
            RestoreCompressionHistory(snapshot.CompressionHistory, snapshot.Messages);
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
        if (_contextPolicyProvider != null)
            _contextPolicyProvider.EffectivePolicyChanged -= OnEffectivePolicyChanged;
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

        _compressionPreviewCts?.Cancel();
        _compressionPreviewCts = null;
        _rawContextCts?.Cancel();
        _rawContextCts = null;

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
