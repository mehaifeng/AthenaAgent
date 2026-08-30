using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.ClientModel;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Athena.UI.Models;
using Athena.UI.Services.Context;
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
using LiveMarkdown.Avalonia;
using System.Collections.Specialized;
using System.Security.Cryptography;
using System.Text;

namespace Athena.UI.ViewModels;

public partial class MainConversationViewModel : ViewModelBase, IDisposable
{
    /// <summary>
    /// 累计推理文本里的回合隔断。界面上每轮思考已经各自成段，这份累计文本只服务于
    /// 上下文回放与压缩，因此不再掺装饰性分隔线——那是给人看的，模型只需要知道换了一轮。
    /// </summary>
    private const string ReasoningRoundSeparator = "\n\n";

    private enum TransitionStageResult
    {
        NotNeeded,
        Staged,
        Failed
    }

    private readonly IChatService? _chatService;
    private readonly IConfigService? _configService;
    private readonly IPromptService? _promptService;
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

    // 本轮流式正文是否已经落进某个 Markdown 段。工具轮封口据此决定要不要补建段——
    // 不能用「最后一段是不是 Markdown」来判断：正文之后又来一段思考时最后一段是思考段，
    // 于是本轮正文会被再补一份，气泡里出现两句一模一样的话。
    private bool _activeTextMaterialized;

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

    partial void OnIsSendingChanged(bool value)
        => Pet.SetConversationActivity(value, IsQueued);

    partial void OnIsQueuedChanged(bool value)
        => Pet.SetConversationActivity(IsSending, value);

    public string ActivityStatusText => IsQueued
        ? GetString("Session.Activity.Queued", "Queued")
        : IsSending
            ? GetString("Session.Activity.Running", "Running")
            : GetString("Session.Activity.Ready", "Ready");

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

    /// <summary>创建本会话的 cron 任务 ID；普通会话为 null。</summary>
    public string? CreatedByCronTaskId => _createdByCronTaskId;

    /// <summary>对应的那一次 cron 运行 ID。</summary>
    public string? CronTaskRunId => _cronTaskRunId;

    /// <summary>该次 cron 触发的计划时刻（UTC）；手动运行为 null。</summary>
    public DateTimeOffset? ScheduledFiredAt => _scheduledFiredAt;

    public bool IsScheduledRun => !string.IsNullOrWhiteSpace(_createdByCronTaskId);

    /// <summary>
    /// 打上 cron 溯源标记。必须在会话创建后、第一次持久化之前调用，
    /// 否则第一版快照会以普通会话落库，重启后就再也认不出它是定时产物。
    /// </summary>
    public void MarkAsScheduledRun(string cronTaskId, string cronTaskRunId, DateTimeOffset? scheduledFiredAt)
    {
        _createdByCronTaskId = cronTaskId;
        _cronTaskRunId = cronTaskRunId;
        _scheduledFiredAt = scheduledFiredAt;
        OnPropertyChanged(nameof(CreatedByCronTaskId));
        OnPropertyChanged(nameof(CronTaskRunId));
        OnPropertyChanged(nameof(ScheduledFiredAt));
        OnPropertyChanged(nameof(IsScheduledRun));
        MarkPersistenceMetadataChanged();
    }

    [ObservableProperty]
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

    /// <summary>
    /// 压缩未成功/被跳过时的常驻提示。以前只汇进默认关闭的 Context Inspector 警告块，
    /// 等于没有出口；现在直接挂在输入框上方，因为上下文仍然超阈值，下一轮会撞同一堵墙。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCompressionStatusMessage))]
    private string _compressionStatusMessage = string.Empty;

    public bool HasCompressionStatusMessage => !string.IsNullOrWhiteSpace(CompressionStatusMessage);

    /// <summary>压缩刚提交时在用量条旁短暂出现的徽标，回答「刚才那段等待换来了什么」。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCompressionSavingBadge))]
    private string _compressionSavingBadge = string.Empty;

    public bool HasCompressionSavingBadge => !string.IsNullOrWhiteSpace(CompressionSavingBadge);

    /// <summary>压缩边界分隔线上的说明文字（条数与时间）。</summary>
    [ObservableProperty]
    private string _compressionBoundaryText = string.Empty;

    /// <summary>正在自动压缩，且尚未提交——只有此时「跳过压缩」才有意义。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SkipCompressionCommand))]
    private bool _isContextMaintenanceRunning;

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
        : _effectiveContextPolicy.Policy.BudgetSummary;

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
            var metadataWarnings = _effectiveContextPolicy?.Metadata.Warnings ?? [];
            // 「未匹配」与「按默认窗口假设」通常同时成立，说的是同一件事；元数据已经给出
            // 假设码时不再多列一行（Distinct 去不掉两句不同措辞的重复）。
            if (_effectiveContextPolicy?.Metadata.Match.Status == ModelMatchStatus.Unmatched
                && !metadataWarnings.Contains(ModelWarnings.UnknownModelAssumption))
                warnings.Add(GetString("ContextInspector.Warning.UnknownModel", "Unmatched model: using the 1M / 256K assumption."));
            // 诊断码不能直接进界面：ContextCapClampedToModel 这类裸标识符对用户毫无意义。
            warnings.AddRange(metadataWarnings.Select(code => ModelWarnings.Describe(code, GetString)));
            if (_effectiveContextPolicy != null)
                warnings.AddRange(_effectiveContextPolicy.Policy.Warnings
                    .Select(code => ModelWarnings.Describe(code, GetString)));
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

    public string InputPlaceholder => GetString("Chat.InputPlaceholder", "Type a message…");

    private ConversationContext _currentContext = new();
    private CancellationTokenSource? _responseCts;
    private CancellationTokenSource? _previewLoadCts;
    private CancellationTokenSource? _compressionPreviewCts;
    /// <summary>只取消自动压缩、不取消整轮请求的令牌源，每轮发送重建一次。</summary>
    private CancellationTokenSource? _compressionSkipCts;
    /// <summary>用量条徽标的代次，用来让后到的隐藏任务不误伤新一次压缩的徽标。</summary>
    private int _compressionBadgeGeneration;
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

    // cron 溯源元数据：本会话若由定时任务触发，归档时随快照持久化，重启后据此恢复时钟标记
    private string? _createdByCronTaskId;
    private string? _cronTaskRunId;
    private DateTimeOffset? _scheduledFiredAt;

    // 会话内压缩撤销栈：每次压缩入栈一个检查点，撤销时弹出还原。切换/重置会话时清空。
    private readonly Stack<CompressionCheckpoint> _compressionHistory = new();

    // 供应商回报的真实用量锚点。会话级真源，随快照落盘；UpdateConversationContext 会反复
    // 重建消息列表，但测量结果必须跨重建、跨回溯、跨重启存活。
    private List<ContextAnchorRecord> _contextAnchors = new();

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

    /// <summary>当前会话的常驻猫头鹰；只投影运行状态，不拥有任何工具执行权限。</summary>
    public VirtualPetViewModel Pet { get; }

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
        Pet.SetSubAgentsRunning(HasRunningSubAgents);

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

    public MainConversationViewModel() : this(null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null) { }

    public MainConversationViewModel(
        IChatService? chatService,
        IConfigService? configService,
        IContextCompressionService? contextCompressionService,
        IPromptService? promptService,
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
        Pet = new VirtualPetViewModel(localizationService);
        Orchestrator = subAgentOrchestrator;
        if (Orchestrator != null)
        {
            Orchestrator.ActiveAgents.CollectionChanged += OnActiveSubAgentsChanged;
            OnActiveSubAgentsChanged(null, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
        _chatService = chatService;
        _configService = configService;
        _promptService = promptService;
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
            Pet.ApplySettings(config);
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
        Pet.RefreshLocalization();
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
            Pet.ApplySettings(config);
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
            _logger.Warning(ex, "Idle session effective context policy refresh failed");
        }
    }

    private string ComputeRequestContentIdentity(AppConfig config)
        => string.Join('|',
            config.EnableMcp,
            config.EnableSkills,
            _functionRegistry?.GetToolDeclarationTokenCount(
                Athena.UI.Services.OfficeToolRelevance.IsRelevant(_currentContext)) ?? 0,
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
        _logger.Information("User requested to stop the current reply");
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
            CompressionPreviewStatus = FormatCompressionFailure(
                "ContextInspector.Preview.PlanUnavailable", "No compression plan could be built: {0}", result.Reason);
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
                CompressionPreviewStatus = FormatCompressionFailure(
                    "ContextInspector.Preview.Failed", "Candidate generation failed: {0}", generated.Error);
                return;
            }
            var validation = _compressionValidator.Validate(plan, generated.Candidate, cts.Token);
            if (!validation.IsValid)
            {
                CompressionPreviewStatus = FormatCompressionFailure(
                    "ContextInspector.Preview.Rejected", "The candidate failed validation: {0}", validation.Error);
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
                CompressionPreviewStatus = FormatCompressionFailure(
                    "ContextInspector.Preview.ApplyFailed",
                    "Apply failed and the conversation is unchanged: {0}",
                    committed.Error);
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
            _logger.Warning(ex, "Failed to build RAW context");
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
        _logger.Information("Conversation rolled back to before message {MessageId}", message.Id);
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
    /// 打开 Markdown 气泡中的超链接：http/https 走系统浏览器，
    /// file:// 本地文件优先在右侧文件编辑区打开（全局对话无工作区也可）。
    /// 其他 scheme 一律忽略，防止注入。
    /// </summary>
    [RelayCommand]
    private async Task OpenMarkdownLinkAsync(LinkClickedEventArgs? args)
    {
        if (args?.HRef is { IsAbsoluteUri: true } uri) await OpenMarkdownLinkUriAsync(uri);
    }

    /// <summary>
    /// Markdown 链接统一分派（左键点击与右键“打开文件/打开链接”共用）。
    /// </summary>
    [RelayCommand]
    private async Task OpenMarkdownLinkUriAsync(Uri? href)
    {
        if (href is not { IsAbsoluteUri: true } uri) return;
        if (uri.Scheme == Uri.UriSchemeFile)
        {
            await OpenLocalFileAsync(uri.LocalPath);
            return;
        }
        if (uri.Scheme is "http" or "https") LaunchExternally(uri.AbsoluteUri);
    }

    /// <summary>
    /// 右键菜单：本地文件“打开方式”——系统选择可用应用打开。
    /// </summary>
    [RelayCommand]
    private async Task OpenMarkdownLinkWithAsync(Uri? href)
    {
        if (href is not { IsAbsoluteUri: true } || href.Scheme != Uri.UriSchemeFile) return;
        await OpenWithSystemPickerAsync(href.LocalPath);
    }

    /// <summary>
    /// 右键菜单：复制本地文件路径或网络链接地址。
    /// </summary>
    [RelayCommand]
    private async Task CopyMarkdownLinkAddressAsync(Uri? href)
    {
        string? text = href is { IsAbsoluteUri: true }
            ? href.Scheme == Uri.UriSchemeFile ? href.LocalPath : href.AbsoluteUri
            : null;
        if (text == null) return;

        var clipboard = GetMainWindow()?.Clipboard;
        if (clipboard != null) await clipboard.SetTextAsync(text);
    }

    /// <summary>
    /// 右键菜单：在系统文件管理器中显示本地文件。
    /// </summary>
    [RelayCommand]
    private void RevealMarkdownLink(Uri? href)
    {
        if (href is not { IsAbsoluteUri: true } || href.Scheme != Uri.UriSchemeFile) return;
        RevealInFolder(href.LocalPath);
    }

    /// <summary>
    /// 在应用内文件编辑区打开本地文件（全局对话无工作区也可打开）；文件不存在时回退系统默认应用。
    /// </summary>
    private async Task OpenLocalFileAsync(string path)
    {
        var workbench = App.Services?.GetService(typeof(WorkspaceWorkbenchViewModel)) as WorkspaceWorkbenchViewModel;
        if (workbench != null && File.Exists(path))
        {
            await workbench.OpenFileByPathAsync(path);
            return;
        }
        LaunchExternally(path);
    }

    /// <summary>
    /// 交给系统按默认方式打开（文件用默认应用，URL 用默认浏览器）。
    /// </summary>
    private void LaunchExternally(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = target,
                UseShellExecute = true
            });
            _logger.Information("Opened externally: {Target}", target);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to open externally: {Target}", target);
        }
    }

    /// <summary>
    /// “打开方式”：Windows 弹系统打开方式对话框；macOS 用 osascript 弹系统应用选择器后 open -a；
    /// Linux 无系统选择器，退化为默认应用打开。
    /// </summary>
    private async Task OpenWithSystemPickerAsync(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { Verb = "openas", UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to show open-with picker for {Path}", path);
            }
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            try
            {
                var script = $"choose application with prompt \"{GetString("Chat.Link.OpenWith.Prompt", "Choose an application to open this file")}\"";
                var psi = new ProcessStartInfo("osascript")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false
                };
                psi.ArgumentList.Add("-e");
                psi.ArgumentList.Add(script);
                using var process = Process.Start(psi);
                if (process == null) return;

                var output = (await process.StandardOutput.ReadToEndAsync()).Trim().Trim('"');
                await process.WaitForExitAsync();
                if (process.ExitCode != 0 || output.Length == 0) return;
                // 输出可能是 application "TextEdit" 前缀、应用名（TextEdit）或应用路径
                // （/System/Applications/TextEdit.app），open -a 对后两者都接受。
                if (output.StartsWith("application ", StringComparison.Ordinal))
                    output = output["application ".Length..].Trim().Trim('"');
                if (output.Length == 0) return;
                var open = new ProcessStartInfo("open") { UseShellExecute = true };
                open.ArgumentList.Add("-a");
                open.ArgumentList.Add(output);
                open.ArgumentList.Add(path);
                Process.Start(open);
                _logger.Information("Opened {Path} with app {App} via macOS picker", path, output);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to run open-with picker for {Path}", path);
            }
            return;
        }

        LaunchExternally(path);
    }

    /// <summary>
    /// 在系统文件管理器中选中并显示文件。
    /// </summary>
    private void RevealInFolder(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS())
                Process.Start(new ProcessStartInfo("open", $"-R \"{path}\"") { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo("xdg-open", $"\"{dir}\"") { UseShellExecute = true });
            _logger.Information("Revealed in folder: {Path}", path);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to reveal in folder: {Path}", path);
        }
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
            _logger.Warning(ex, "Failed to add attachment");
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
            _logger.Warning(ex, "Screenshot capture failed");
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
            _logger.Warning(ex, "Background polling of screenshot clipboard failed");
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
            _logger.Warning(ex, "Failed to paste image");
            AttachmentStatusMessage = ToAttachmentErrorMessage(ex);
        }
    }

    /// <summary>
    /// 在本会话里执行一次 cron 计划指令。
    ///
    /// 这里没有"忙则推迟"的分支：每次 cron 触发都开一个全新会话，本实例刚被创建，
    /// 结构上不可能正在发送或压缩。旧实现里那套 Busy 排队逻辑正是为"往当前会话插消息"
    /// 服务的，随该设计一并去掉。
    /// </summary>
    public async Task<TaskExecutionResult> RunScheduledInstructionAsync(string instruction, CancellationToken cancellationToken = default)
    {
        if (_chatService == null || _promptService == null)
        {
            _logger.Warning("Ignoring scheduled instruction: chat or prompt service is not initialized");
            return TaskExecutionResult.Failed("Chat service or prompt service is not available.");
        }

        if (string.IsNullOrWhiteSpace(instruction))
        {
            return TaskExecutionResult.Failed("The scheduled instruction is empty.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        _logger.Information("Running scheduled instruction in conversation {ConversationId}", _conversationId);

        var scheduledPrompt = _promptService.GetProactiveMessagePrompt(instruction, DateTime.Now);

        // 多数 LLM API 不允许消息序列以 System 结尾，因此计划指令作为一条“隐藏的用户消息”注入：
        // 上下文里有它，UI 里看不见它。
        Messages.Add(new ChatMessage
        {
            Role = "user",
            Content = scheduledPrompt,
            IsHidden = true,
            Timestamp = DateTime.Now
        });

        UpdateConversationContext();

        // addToContext 为 false：上面已手动入列并刷新过上下文。
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
            Anchors = CaptureAnchors(),
            ForkedFromConversationId = _forkedFromConversationId,
            ForkedFromHistoryId = _forkedFromHistoryId,
            ForkedAtMessageId = _forkedAtMessageId,
            CreatedByCronTaskId = _createdByCronTaskId,
            CronTaskRunId = _cronTaskRunId,
            ScheduledFiredAt = _scheduledFiredAt,
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
        // 重置等于开一段全新对话；沿用旧的 cron 溯源会让新内容被错误地归到那次触发名下。
        _createdByCronTaskId = null;
        _cronTaskRunId = null;
        _scheduledFiredAt = null;

        _compressionHistory.Clear();
        _contextAnchors = new List<ContextAnchorRecord>();
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
        // 与 responseCts 分开：跳过压缩只作废这一次压缩，本轮请求照常发出。
        _compressionSkipCts?.Dispose();
        var compressionSkipCts = new CancellationTokenSource();
        _compressionSkipCts = compressionSkipCts;
        // 上一轮遗留的压缩提示不该冒充本轮的结论。
        CompressionStatusMessage = string.Empty;
        var requestContext = _currentContext.Clone();
        requestContext.ConversationId = _conversationId;
        requestContext.Revision = _revision;
        var requestContentIdentityAtStart = _requestContentIdentity;
        var outcome = TaskExecutionResult.Succeeded();

        IsSending = true;
        ConversationExecutionCoordinator.Lease? executionLease = null;
        _forceNewAssistantTextSegment = false;
        _activeTextMaterialized = false;
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

        // 推理流式状态机：非 null = 本回合正在流式接收推理文本，增量都写进这一段。
        // 新一轮推理首个增量到达时另起一段并按设置自动展开；
        // 回合结束时（onMessageAdded 到达）收起该段，下一轮推理再新起一段。
        ChatMessageSegment? activeReasoning = null;

        // 该思考段之后已经有正文落地。供应商在同一回合里交错输出「思考 → 正文 → 思考」时，
        // 后半段思考绝不能续写 activeReasoning：那个框排在正文上方，续写等于把「看完正文
        // 之后才想的事」倒插回正文前面，气泡的时间顺序当场错乱。置位后另起一段接在末尾。
        var reasoningSealedByText = false;

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

                        // 本回合推理结束：收起该思考段（下一轮推理开始时另起一段）。
                        activeReasoning = EndReasoningRound(activeReasoning);

                        // 为本轮的每个工具调用追加一行「执行中」。
                        // 先补工具行、再关闭 loading：切换过程中气泡始终有可见内容承接，避免空气泡塌缩。
                        assistantMsg.ToolExecutionSummary = string.Empty;
                        AddToolCallEntries(assistantMsg, msg.ToolCallsJson);
                        assistantMsg.IsComposingFileText = false;
                        assistantMsg.IsLoading = false;
                    }
                    else if (msg.Role == "assistant" && !string.IsNullOrEmpty(msg.ReasoningContent))
                    {
                        if (activeReasoning != null)
                        {
                            // 推理增量已在 onReasoningDelta 实时流入该段，此处只做回合收尾。
                            activeReasoning = EndReasoningRound(activeReasoning);
                        }
                        else
                        {
                            // 非流式到达（供应商只在回合末尾给整段推理）：补一段，保持收起。
                            // 跨工具轮累计：每个回合的思考都值得保留（工具执行前的推理往往最有信息量）。
                            AppendReasoningSegment(assistantMsg, msg.ReasoningContent, expanded: false);
                            assistantMsg.ReasoningContent = string.IsNullOrEmpty(assistantMsg.ReasoningContent)
                                ? msg.ReasoningContent
                                : assistantMsg.ReasoningContent + ReasoningRoundSeparator + msg.ReasoningContent;
                        }
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
                        var toolSucceeded = CompleteToolCallEntry(assistantMsg, msg.ToolCallId, msg.ToolName, msg.Content);
                        var nextRunningTool = assistantMsg.Segments
                            .Where(segment => segment.IsToolCallGroup)
                            .SelectMany(segment => segment.ToolCalls)
                            .FirstOrDefault(tool => tool.IsRunning)?.Name;
                        Pet.FinishTool(toolSucceeded != false, nextRunningTool);

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
                    if (!IsCurrentConversationEpoch(epoch))
                    {
                        return;
                    }

                    // 工具调用参数已经开始输出，说明当前推理增量阶段已结束。
                    if (activeReasoning != null)
                    {
                        activeReasoning.IsAppending = false;
                    }

                    if (functionName != "write_system_file" && functionName != "modify_system_file") return;

                    assistantMsg.IsComposingFileText = true;
                    assistantMsg.IsLoading = true;
                },
                onReasoningDelta: delta =>
                {
                    if (!IsCurrentConversationEpoch(epoch)) return;

                    if (activeReasoning == null || reasoningSealedByText)
                    {
                        // 新一段思考开始：在已到达的正文/工具段之后另起一个思考段，
                        // 之后的增量都追加进这一段；给模型回放的累计文本另加隔断。
                        // 上一段（若有）就地收尾：同一时刻只留一个展开着的思考框。
                        activeReasoning = EndReasoningRound(activeReasoning);
                        if (!string.IsNullOrEmpty(assistantMsg.ReasoningContent))
                        {
                            assistantMsg.ReasoningContent += ReasoningRoundSeparator;
                        }

                        activeReasoning = AppendReasoningSegment(
                            assistantMsg,
                            string.Empty,
                            expanded: _configService?.Load().AutoExpandReasoning ?? true);
                        reasoningSealedByText = false;
                    }

                    // 每个推理增量都会重新点亮灯泡；若供应商交错输出正文与推理，也能正确恢复动画。
                    activeReasoning.IsAppending = true;
                    activeReasoning.Text += delta;
                    assistantMsg.ReasoningContent += delta;
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
                },
                onCompressionProgress: progress => ApplyCompressionProgress(progress, assistantMsg, epoch),
                skipCompressionToken: compressionSkipCts.Token,
                onAnchorObserved: anchor =>
                {
                    if (IsCurrentConversationEpoch(epoch)) OnContextAnchorObserved(anchor);
                }))
            {
                if (!IsCurrentConversationEpoch(epoch))
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(contentDelta))
                {
                    // 正文开始追加后，灯泡不再闪烁；思考段仍按既有回合收尾逻辑自动收起。
                    if (activeReasoning != null)
                    {
                        activeReasoning.IsAppending = false;
                        // 正文已经排在这段思考之后，这段就此封口：再来的推理增量另起一段。
                        reasoningSealedByText = true;
                    }

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
                _logger.Information("Current reply stopped");
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
            if (ReferenceEquals(_compressionSkipCts, compressionSkipCts))
            {
                _compressionSkipCts = null;
            }

            compressionSkipCts.Dispose();
            // 本轮已经结束，压缩不可能还在跑：无论会话是否被切走都必须复位，
            // 否则「跳过压缩」按钮会永久亮在那里。
            IsContextMaintenanceRunning = false;

            if (IsCurrentConversationEpoch(epoch))
            {
                // 回复生命周期结束：先落内容态，最后统一撤下 loading/streaming 生命线，气泡去留由下方清理判定。
                assistantMsg.IsLoading = false;
                assistantMsg.IsComposingFileText = false;
                assistantMsg.IsStreaming = false;
                assistantMsg.ToolExecutionSummary = string.Empty;
                assistantMsg.ContextMaintenanceStatus = string.Empty;

                // 输出结束（成功/停止/报错均经此）：收起仍展开着的思考段，尊重用户手动操作
                CollapseStreamingReasoning(assistantMsg);

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
                Pet.CompleteResponse(
                    outcome.Outcome == TaskExecutionOutcome.Succeeded,
                    outcome.Outcome == TaskExecutionOutcome.Interrupted);
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
            Anchors = CaptureAnchors(),
            ForkedFromConversationId = _forkedFromConversationId,
            ForkedFromHistoryId = _forkedFromHistoryId,
            ForkedAtMessageId = _forkedAtMessageId,
            CreatedByCronTaskId = _createdByCronTaskId,
            CronTaskRunId = _cronTaskRunId,
            ScheduledFiredAt = _scheduledFiredAt,
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
            Anchors = CaptureAnchors(),
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

    /// <summary>快照锚点账本。裁剪已由 <see cref="UpdateConversationContext"/> 统一负责。</summary>
    private List<ContextAnchorRecord> CaptureAnchors() => new(_contextAnchors);

    /// <summary>收下一次新测量。前缀内容是否仍匹配由 ContextAnchorLedger 在使用时用摘要校验。</summary>
    private void OnContextAnchorObserved(ContextAnchorRecord anchor)
    {
        _contextAnchors = ContextAnchorLedger.Append(_contextAnchors, anchor);
        _currentContext.Anchors = _contextAnchors;
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
        _currentContext.Anchors = _contextAnchors;

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

        // 回溯、分支、压缩提交都会裁掉消息：长于当前上下文的测量已无意义。前缀内容是否仍逐条
        // 一致由 ContextAnchorLedger 取用时以摘要校验，这里只做廉价的长度裁剪。所有重建上下文
        // 的路径都汇聚到这里，因此不必在每个调用点各自裁一遍。
        _contextAnchors = ContextAnchorLedger.TrimTo(_contextAnchors, _currentContext.Messages.Count);
        _currentContext.Anchors = _contextAnchors;
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
        var systemPrompt = _promptService.GetPrompt(PromptType.MainPersona)
                           + "\n\n---\n\n"
                           + PromptTemplates.LocalFileLinkPolicy;
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
        // 估算必须与实际下发的工具集一致：Office 工具是否携带由同一判据决定，
        // 否则非 Office 会话会被虚报出一份并不存在的声明开销。
        _currentContext.ToolsDeclarationTokenCount = functionCallingEnabled
            ? _functionRegistry.GetToolDeclarationTokenCount(
                Athena.UI.Services.OfficeToolRelevance.IsRelevant(_currentContext))
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
        // 取消语义不能取决于「这份材料恰好可不可压」：可行性判定会在规划期直接返回，
        // 若不在入口先检查，一个已取消的令牌会静默走完并正常返回。
        cancellationToken.ThrowIfCancellationRequested();

        IsCompressing = true;
        CompressionStatusMessage = string.Empty;
        try
        {
            if (_compressionPlanner == null
                || _compressionCandidateGenerator == null
                || _compressionValidator == null)
            {
                CompressionStatusMessage = GetString("ContextInspector.Preview.Unavailable", "Transactional compression is unavailable.");
                return;
            }
            if (_compressionCommitter == null)
            {
                CompressionStatusMessage = GetString("Audio.StorageUnavailable", "Storage unavailable; the operation was not applied.");
                return;
            }
            var main = _effectiveContextPolicy ?? _contextPolicyProvider?.Resolve(CurrentWorkspace?.ContextPolicyOverride);
            var compression = _contextPolicyProvider?.ResolveRole(AiModelRole.ContextCompression);
            if (main == null || compression == null)
            {
                CompressionStatusMessage = GetString("ContextInspector.Preview.ModelUnavailable", "Compression model policy is unavailable.");
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
                CompressionStatusMessage = FormatCompressionFailure(
                    "ContextInspector.Preview.PlanUnavailable", "No compression plan could be built: {0}", planResult.Reason);
                return;
            }

            var generated = await _compressionCandidateGenerator.GenerateAsync(planResult.Plan, cancellationToken);
            if (generated.Candidate == null)
            {
                CompressionStatusMessage = FormatCompressionFailure(
                    "ContextInspector.Preview.Failed", "Candidate generation failed: {0}", generated.Error);
                return;
            }
            var validation = _compressionValidator.Validate(planResult.Plan, generated.Candidate, cancellationToken);
            if (!validation.IsValid)
            {
                CompressionStatusMessage = FormatCompressionFailure(
                    "ContextInspector.Preview.Rejected", "The candidate failed validation: {0}", validation.Error);
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
                CompressionStatusMessage = FormatCompressionFailure(
                    "ContextInspector.Preview.ApplyFailed",
                    "Apply failed and the conversation is unchanged: {0}",
                    committed.Error);
                return;
            }
            CompressionStatusMessage = string.Empty;
            _logger.Information("Transactional context compression committed: Revision={Revision}, Messages={Count}",
                committed.Revision, transition.MessageIds.Count);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        finally
        {
            IsCompressing = false;
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

    /// <summary>
    /// 把自动压缩的阶段回报翻译成界面状态。只有 Map/Reduce 会写状态行——规划与校验是
    /// 本地瞬时操作，报给界面只会闪一下，反而像故障。
    /// </summary>
    private void ApplyCompressionProgress(CompressionProgress progress, ChatMessage assistantMsg, int epoch)
    {
        if (!IsCurrentConversationEpoch(epoch)) return;
        switch (progress.Phase)
        {
            case CompressionProgressPhase.Mapping:
                IsContextMaintenanceRunning = true;
                assistantMsg.ContextMaintenanceStatus = string.Format(
                    GetString("Chat.Context.Compressing", "Condensing context · part {0}/{1}"),
                    progress.Index,
                    progress.Total);
                break;
            case CompressionProgressPhase.Reducing:
                IsContextMaintenanceRunning = true;
                assistantMsg.ContextMaintenanceStatus = string.Format(
                    GetString("Chat.Context.Reducing", "Merging summaries · layer {0}"),
                    progress.Depth);
                break;
            case CompressionProgressPhase.Committed:
                IsContextMaintenanceRunning = false;
                assistantMsg.ContextMaintenanceStatus = string.Empty;
                ShowCompressionSavingBadge(progress.TokensBefore - progress.TokensAfter);
                break;
            case CompressionProgressPhase.Skipped:
                IsContextMaintenanceRunning = false;
                assistantMsg.ContextMaintenanceStatus = string.Empty;
                CompressionStatusMessage = GetString(
                    "Chat.Context.CompressionSkipped",
                    "Compression skipped. This reply continues with the context unchanged.");
                break;
            case CompressionProgressPhase.Failed:
                // 具体措辞紧接着由 onContextWarning 给出，这里只负责收掉进行中状态。
                IsContextMaintenanceRunning = false;
                assistantMsg.ContextMaintenanceStatus = string.Empty;
                break;
        }
    }

    /// <summary>
    /// 放弃这一次自动压缩。整轮请求不受影响，会带着原上下文照常发出——
    /// 用户唯一的逃生手段不该是「按停止把整轮作废」。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSkipCompression))]
    private void SkipCompression() => _compressionSkipCts?.Cancel();

    private bool CanSkipCompression() => IsContextMaintenanceRunning;

    /// <summary>压缩省下了多少——把「刚才那段等待」和「换来了什么」绑在同一个视觉事件上。</summary>
    private void ShowCompressionSavingBadge(long savedTokens)
    {
        if (savedTokens <= 0)
        {
            CompressionSavingBadge = string.Empty;
            return;
        }

        CompressionSavingBadge = string.Format(
            GetString("Chat.Context.CompressedBadge", "Compressed −{0}"),
            FormatTokenCount(savedTokens));
        var generation = ++_compressionBadgeGeneration;
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(4));
            Dispatcher.UIThread.Post(() =>
            {
                if (generation == _compressionBadgeGeneration) CompressionSavingBadge = string.Empty;
            });
        });
    }

    private static string FormatTokenCount(long tokens) => tokens >= 1000
        ? (tokens / 1000d).ToString("0.#", CultureInfo.InvariantCulture) + "k"
        : tokens.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// 重算压缩边界。边界永远只有一条：最后一条已归档消息之后。逐条「已归档」角标
    /// 回答不了「从哪儿开始模型看到的是摘要」，这条线才回答得了。
    /// </summary>
    private void RefreshCompressionBoundary()
    {
        // 必须挂在「看得见的」那条上：消息行整体绑定 IsBubbleVisible，挂到隐藏的
        // tool/工具调用载体消息上，这条线会连同整行一起被隐藏掉。
        ChatMessage? boundary = null;
        foreach (var message in Messages)
        {
            if (message.IsCompressed && message.IsBubbleVisible) boundary = message;
        }
        foreach (var message in Messages)
        {
            message.IsCompressionBoundary = ReferenceEquals(message, boundary);
        }

        if (boundary == null)
        {
            CompressionBoundaryText = string.Empty;
            return;
        }

        var archived = Messages.Count(message => message.IsCompressed);
        CompressionBoundaryText = _compressionHistory.Count > 0
            ? string.Format(
                GetString("Chat.Context.BoundaryAt", "Context compressed · {0} messages → summary · {1:t}"),
                archived,
                _compressionHistory.Peek().CreatedAt.ToLocalTime())
            : string.Format(
                GetString("Chat.Context.Boundary", "Context compressed · {0} messages → summary"),
                archived);
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
        RefreshCompressionBoundary();
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
                CompressionStatusMessage = committed.IsCommitted
                    ? string.Empty
                    : FormatCompressionFailure(
                        "ContextInspector.Preview.UndoFailed",
                        "Undo failed and the conversation is unchanged: {0}",
                        committed.Error);
                return committed.IsCommitted;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            finally
            {
                IsCompressing = false;
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
        RefreshCompressionBoundary();
        UndoCompressionCommand.NotifyCanExecuteChanged();
        MarkPersistenceStateChanged();

        _logger.Information("Previous context compression reverted; restored {Count} message(s)", checkpoint.Batch.Count);
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
        RefreshCompressionBoundary();
        UndoCompressionCommand.NotifyCanExecuteChanged();
        _logger.Information("Context compression atomically reverted; restored {Count} message(s)", batch.Count);
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
        _createdByCronTaskId = history.CreatedByCronTaskId;
        _cronTaskRunId = history.CronTaskRunId;
        _scheduledFiredAt = history.ScheduledFiredAt;

        CurrentWorkspace = !string.IsNullOrEmpty(history.WorkspaceId)
            ? AvailableWorkspaces.FirstOrDefault(workspace => workspace.Id == history.WorkspaceId)
            : null;

        _compressionHistory.Clear();
        // 恢复已落盘的真实测量：切换会话后不必再从零估算整段上下文。
        // 长度裁剪与前缀校验分别由 UpdateConversationContext 和 ContextAnchorLedger 负责。
        _contextAnchors = new List<ContextAnchorRecord>(history.Anchors ?? []);
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
        if (records == null)
        {
            // 没有落盘的检查点，消息上仍可能带着 IsCompressed：边界照样要重算，否则会留着上一段会话的线。
            RefreshCompressionBoundary();
            return;
        }
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
        RefreshCompressionBoundary();
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
            _logger.Warning(ex, "Background attachment preview backfill failed");
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
            _logger.Error(ex, "Failed to write to pending archive queue");
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
            Anchors = CaptureAnchors(),
            ForkedFromConversationId = _forkedFromConversationId,
            ForkedFromHistoryId = _forkedFromHistoryId,
            ForkedAtMessageId = _forkedAtMessageId,
            CreatedByCronTaskId = _createdByCronTaskId,
            CronTaskRunId = _cronTaskRunId,
            ScheduledFiredAt = _scheduledFiredAt,
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
        _createdByCronTaskId = snapshot.CreatedByCronTaskId;
        _cronTaskRunId = snapshot.CronTaskRunId;
        _scheduledFiredAt = snapshot.ScheduledFiredAt;

        _compressionHistory.Clear();
        _contextAnchors = new List<ContextAnchorRecord>(snapshot.Anchors ?? []);
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
        _logger.Information("Main conversation draft restored, message count: {Count}", Messages.Count);
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

    /// <summary>
    /// 给压缩链路的失败原因套一个本地化外框。原因本身是排障文本（规划器判定、验收结论、
    /// 供应商报错原文），刻意保持原样以便与日志逐字比对；但直接摆一句没有主语的英文异常，
    /// 用户根本无从判断这是失败、失败在哪一步、会话有没有被改动。
    /// </summary>
    private string FormatCompressionFailure(string key, string defaultFormat, string? reason)
        => string.Format(
            GetString(key, defaultFormat),
            string.IsNullOrWhiteSpace(reason) ? "—" : reason.Trim());

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
            _logger.Warning(ex, "Background generation of assistant voice failed");
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
            _logger.Warning(ex, "Auto-play of assistant audio failed");
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

    private void EnsureSegmentLayout(ChatMessage message)
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
            // 本轮正文已经有段承载：工具轮封口时不要再补一份。
            _activeTextMaterialized = true;
        }

        message.NotifySegmentsChanged();
    }

    private void AppendAssistantMarkdownSegment(ChatMessage message, string contentDelta)
    {
        if (message.Segments.Count == 0)
        {
            return;
        }

        _activeTextMaterialized = true;

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

        // 末尾是思考段/工具段（正文之后又插了别的）：正文另起一段接在后面，保持时间顺序。
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
            string? firstToolName = null;

            foreach (var node in toolCalls)
            {
                if (node == null)
                {
                    continue;
                }

                var name = node["FunctionName"]?.ToString() ?? string.Empty;
                var id = node["Id"]?.ToString();
                var arguments = node["Arguments"]?.ToString();
                firstToolName ??= name;

                group ??= AppendToolCallGroup(message);
                group.ToolCalls.Add(new ToolCallEntry
                {
                    ToolCallId = id,
                    Name = name,
                    Summary = ToolCallDisplay.Summarize(name, arguments, _localizationService),
                    Arguments = ToolCallDisplay.PrettyArguments(arguments),
                    Status = ToolCallStatus.Running
                });
            }

            if (group != null)
            {
                message.NotifySegmentsChanged();
                Pet.BeginTool(firstToolName);
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

    // 每一轮工具调用都新建一个组并追加到段尾：段的先后就是回合的先后，
    // 于是「这段正文之后调了这些工具、然后才有下一段正文」在界面上是直读的。
    private static ChatMessageSegment AppendToolCallGroup(ChatMessage message)
    {
        var group = new ChatMessageSegment { Kind = ChatMessageSegmentKind.ToolCallGroup };
        message.Segments.Add(group);
        return group;
    }

    // 新一轮推理开始：追加一个思考段。正文可能已经流在 Content 里而尚未建段
    // （首轮 AppendAssistantMarkdownSegment 会跳过建段），此时必须先固化成 Markdown 段——
    // 一旦有了任何 segment，UsesSegmentLayout 就为真、legacy 渲染器关闭，
    // 已经流出来的正文会当场从界面消失。
    private ChatMessageSegment AppendReasoningSegment(ChatMessage message, string text, bool expanded)
    {
        EnsureSegmentLayout(message);

        var segment = new ChatMessageSegment
        {
            Kind = ChatMessageSegmentKind.Reasoning,
            Text = text,
            IsExpanded = expanded
        };

        message.Segments.Add(segment);
        message.NotifySegmentsChanged();
        return segment;
    }

    // 工具结果回填到对应项：按 ToolCallId 匹配，标记成功/失败并填入结果预览。
    private static bool? CompleteToolCallEntry(ChatMessage message, string? toolCallId, string toolName, string? resultJson)
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
            return null;
        }

        var (success, preview) = ToolCallDisplay.ParseResult(resultJson);
        entry.Status = success ? ToolCallStatus.Success : ToolCallStatus.Failed;
        entry.Result = preview;
        return success;
    }

    // 本回合推理收尾：停灯泡、收起（除非用户手动展开过）。返回 null，让调用方顺手清空活动段。
    private static ChatMessageSegment? EndReasoningRound(ChatMessageSegment? segment)
    {
        if (segment == null)
        {
            return null;
        }

        segment.IsAppending = false;
        if (!segment.UserToggled)
        {
            segment.IsExpanded = false;
        }

        return null;
    }

    // 回复结束：收起仍展开着的思考段（尊重用户已手动展开的意愿），并停掉灯泡动画。
    // 工具行本来就默认收起，无需处理。
    private static void CollapseStreamingReasoning(ChatMessage message)
    {
        foreach (var seg in message.Segments)
        {
            if (!seg.IsReasoning)
            {
                continue;
            }

            seg.IsAppending = false;
            if (!seg.UserToggled)
            {
                seg.IsExpanded = false;
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
        if (!string.IsNullOrWhiteSpace(bubble.Content) && !_activeTextMaterialized)
        {
            // 首轮（流式时 Segments 还为空，AppendAssistantMarkdownSegment 会跳过建段）：补建一段承载前导正文。
            // 判据是「这段正文有没有进过 Markdown 段」而不是「最后一段是不是 Markdown」：
            // 正文之后又来一段思考时，最后一段是思考段，按位置判断会把正文再补一份。
            bubble.Segments.Add(new ChatMessageSegment
            {
                Kind = ChatMessageSegmentKind.Markdown,
                Text = bubble.Content
            });
        }

        bubble.Content = string.Empty;
        _activeTextMaterialized = false;
        // 推理文本不清空：它是给模型回放的累计副本（CommitActiveBubbleRound 只固化正文段）。
        // 下一段正文另起一段：本轮工具段通常已天然隔开，但工具 JSON 解析失败时不会有工具段，
        // 这个标志是那种情况下的保底，避免两轮正文被拼成一段。
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
        Pet.Dispose();

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
