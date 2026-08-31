using Athena.UI.Models;
using Athena.UI.Services;
using Athena.UI.Services.Interfaces;
using Athena.UI.Services.VirtualPet;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Athena.UI.ViewModels;

public enum VirtualPetState
{
    Idle,
    Thinking,
    Working,
    Celebrating,
    Alert,
    Sleeping,
    /// <summary>正在回应一次用户互动（摸头/投喂/陪玩）。短暂的瞬时状态。</summary>
    Interacting
}

public enum VirtualPetMotionState
{
    None,
    Dragging,
    Falling,
    MovingLeft,
    MovingRight
}

/// <summary>
/// 选中 PetDex 伙伴的表现层状态归约器。它观察对话、工具与子代理活动，
/// 但从不拥有、也从不发起业务操作。
///
/// 养成数值不住在这里：每个会话都有自己的实例，而窗口上只有一只宠物，
/// 等级和心情必须活在会话之上（见 <see cref="IVirtualPetProgressionService"/>）。
/// 这个类只做投影和手势编排。
/// </summary>
public partial class VirtualPetViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan SleepAfter = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan CelebrationDuration = TimeSpan.FromSeconds(1.6);
    private static readonly TimeSpan AlertDuration = TimeSpan.FromSeconds(2.2);
    private static readonly TimeSpan InteractionDuration = TimeSpan.FromSeconds(1.4);

    /// <summary>气泡停留时长。够读完一句 20 字的话，又不至于一直挡着界面。</summary>
    public static readonly TimeSpan BubbleDuration = TimeSpan.FromSeconds(4.5);

    /// <summary>
    /// 等模型台词期间，气泡最多能被按住多久。必须盖得住 <see cref="PetChatterService.ModelTimeout"/>：
    /// 气泡只活 4.5 秒，而一次模型调用允许慢到 8 秒，先到期就等于那句台词永远显示不出来。
    /// 上限本身也是必要的——一个挂死的请求不能把气泡永久钉在屏幕上。
    /// </summary>
    public static readonly TimeSpan ModelLineGrace = PetChatterService.ModelTimeout + TimeSpan.FromSeconds(1);

    /// <summary>养成推进的调用节流。服务端自己还有 5 秒的最小间隔，这里只是少拿几次锁。</summary>
    private static readonly TimeSpan ProgressionPollInterval = TimeSpan.FromSeconds(1);

    private readonly IVirtualPetProgressionService _progression;
    private readonly IPetChatterService _chatter;
    /// <summary>只有 <see cref="CreateDetached"/> 自造的服务会进这个列表；DI 提供的不由这里释放。</summary>
    private readonly IReadOnlyList<IDisposable> _ownedServices;
    private readonly ILocalizationService? _localizationService;
    private readonly ILogger _logger;
    private DateTime _lastActivityAt = DateTime.UtcNow;
    private DateTime _animationStartedAt = DateTime.UtcNow;
    private DateTime _transientUntil = DateTime.MinValue;
    private DateTime _bubbleUntil = DateTime.MinValue;
    private DateTime _lastProgressionPollAt = DateTime.MinValue;
    private VirtualPetState? _transientState;
    private PetDexAnimationState _interactionAnimation = PetDexAnimationState.Waving;
    private bool _conversationActive;
    private bool _queued;
    private bool _subAgentsActive;
    private string? _activeTool;
    private string? _lastToolName;
    private string? _topicHint;
    private PetNeedKind _lastAnnouncedNeed = PetNeedKind.None;
    private long _bubbleToken;
    /// <summary>正在等模型台词的那个气泡；0 表示没有在途请求。</summary>
    private long _pendingModelLineToken;
    private DateTime _modelLineGraceUntil = DateTime.MinValue;
    private bool _disposed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCatchFiles))]
    private bool _isEnabled = true;

    /// <summary>互动闭环总开关。关掉后宠物退回纯状态指示器，拖动与漫游不受影响。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInteract))]
    [NotifyPropertyChangedFor(nameof(CanSpeak))]
    private bool _interactionEnabled = true;

    /// <summary>模型台词开关。关闭时只用本地台词库。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSpeak))]
    private bool _chatterEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCelebrating))]
    [NotifyPropertyChangedFor(nameof(IsAlert))]
    [NotifyPropertyChangedFor(nameof(CanAutoRoam))]
    private bool _reducedMotion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyPropertyChangedFor(nameof(IsCelebrating))]
    [NotifyPropertyChangedFor(nameof(IsAlert))]
    [NotifyPropertyChangedFor(nameof(IsSleeping))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(CueSymbol))]
    [NotifyPropertyChangedFor(nameof(HasCue))]
    [NotifyPropertyChangedFor(nameof(AnimationState))]
    [NotifyPropertyChangedFor(nameof(CanAutoRoam))]
    private VirtualPetState _state = VirtualPetState.Idle;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AnimationState))]
    private VirtualPetMotionState _motionState;

    [ObservableProperty]
    private int _frameIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PetWidth))]
    [NotifyPropertyChangedFor(nameof(PetHeight))]
    [NotifyPropertyChangedFor(nameof(ViewWidth))]
    [NotifyPropertyChangedFor(nameof(ViewHeight))]
    [NotifyPropertyChangedFor(nameof(GroundOffset))]
    [NotifyPropertyChangedFor(nameof(BubbleOffsetY))]
    private double _petScale = 0.5;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PetWidth))]
    [NotifyPropertyChangedFor(nameof(PetHeight))]
    [NotifyPropertyChangedFor(nameof(ViewWidth))]
    [NotifyPropertyChangedFor(nameof(ViewHeight))]
    [NotifyPropertyChangedFor(nameof(GroundOffset))]
    [NotifyPropertyChangedFor(nameof(PetDisplayName))]
    [NotifyPropertyChangedFor(nameof(BubbleOffsetY))]
    private string _petSlug = PetDexPetLibrary.DefaultSlug;

    [ObservableProperty]
    private bool _roamingEnabled = true;

    [ObservableProperty]
    private bool _gravityEnabled = true;

    [ObservableProperty]
    private VirtualPetRoamArea _roamArea = VirtualPetRoamArea.LowerHalf;

    /// <summary>当前养成快照。所有档案面板属性都从它投影。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Level))]
    [NotifyPropertyChangedFor(nameof(RankTitle))]
    [NotifyPropertyChangedFor(nameof(LevelProgress))]
    [NotifyPropertyChangedFor(nameof(LevelProgressText))]
    [NotifyPropertyChangedFor(nameof(MoodValue))]
    [NotifyPropertyChangedFor(nameof(EnergyValue))]
    [NotifyPropertyChangedFor(nameof(BondValue))]
    [NotifyPropertyChangedFor(nameof(CompanionDays))]
    [NotifyPropertyChangedFor(nameof(TotalPats))]
    [NotifyPropertyChangedFor(nameof(TotalFeeds))]
    [NotifyPropertyChangedFor(nameof(TotalPlays))]
    [NotifyPropertyChangedFor(nameof(TotalConversations))]
    [NotifyPropertyChangedFor(nameof(TotalToolCalls))]
    [NotifyPropertyChangedFor(nameof(TotalNeedsMet))]
    [NotifyPropertyChangedFor(nameof(ActiveNeed))]
    [NotifyPropertyChangedFor(nameof(HasActiveNeed))]
    [NotifyPropertyChangedFor(nameof(NeedText))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(CueSymbol))]
    [NotifyPropertyChangedFor(nameof(HasCue))]
    private VirtualPetSnapshot _snapshot = VirtualPetSnapshot.Empty;

    /// <summary>气泡当前显示的台词；空串表示不显示。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBubble))]
    private string _bubbleText = string.Empty;

    /// <summary>宠物档案面板是否展开。</summary>
    [ObservableProperty]
    private bool _isProfileOpen;

    /// <summary>当前会话是否还能接收附件。决定宠物是否接住拖进来的文件。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCatchFiles))]
    private bool _canAcceptFiles;

    public VirtualPetViewModel(
        IVirtualPetProgressionService progression,
        IPetChatterService chatter,
        ILocalizationService? localizationService = null)
        : this(progression, chatter, localizationService, ownedServices: [])
    {
    }

    private VirtualPetViewModel(
        IVirtualPetProgressionService progression,
        IPetChatterService chatter,
        ILocalizationService? localizationService,
        IReadOnlyList<IDisposable> ownedServices)
    {
        _progression = progression;
        _chatter = chatter;
        _ownedServices = ownedServices;
        _localizationService = localizationService;
        _logger = Log.ForContext<VirtualPetViewModel>();
        _progression.SnapshotChanged += OnSnapshotChanged;
        Snapshot = _progression.GetSnapshot(PetSlug);
        _lastAnnouncedNeed = Snapshot.ActiveNeed;
    }

    /// <summary>
    /// 设计器 / 测试构造。养成写进内存、台词只用本地库——
    /// 显式工厂，而不是"依赖可空、悄悄不工作"（见 CLAUDE.md「Review Rules」第 1 条）。
    /// </summary>
    public static VirtualPetViewModel CreateDetached(ILocalizationService? localizationService = null)
    {
        var progression = new VirtualPetProgressionService(new InMemoryPetProfileStore(), new SystemClock(), Log.Logger);
        var chatter = PetChatterService.CreateLocalOnly(localizationService, Log.Logger);
        // 这两个服务是这条路径自己造的，所以由 ViewModel 负责释放；
        // 生产路径上它们来自 DI 容器，ViewModel 不碰它们的生命周期。
        return new VirtualPetViewModel(progression, chatter, localizationService, [progression, chatter]);
    }

    /// <summary>宠物接住了拖进来的文件。宿主会话把它们变成待发送附件。</summary>
    public event EventHandler<IReadOnlyList<IStorageFile>>? FilesDropped;

    /// <summary>用户从宠物菜单里请求隐藏宠物。宿主负责写配置。</summary>
    public event EventHandler? HideRequested;

    /// <summary>陪玩时请求一次可见的位移冲刺。视图层持有运动引擎，由它执行。</summary>
    public event EventHandler? PlayBurstRequested;

    public bool IsBusy => State is VirtualPetState.Thinking or VirtualPetState.Working;
    public bool IsCelebrating => State == VirtualPetState.Celebrating && !ReducedMotion;
    public bool IsAlert => State == VirtualPetState.Alert && !ReducedMotion;
    public bool IsSleeping => State == VirtualPetState.Sleeping;
    public bool CanAutoRoam => State == VirtualPetState.Idle && !ReducedMotion;
    public bool HasCue => !string.IsNullOrEmpty(CueSymbol);
    public bool HasBubble => !string.IsNullOrEmpty(BubbleText);
    public bool CanInteract => InteractionEnabled;

    /// <summary>
    /// 宠物是否接住拖进来的文件。视图用它决定拖放效果，命令路径用同一个判断，
    /// 免得出现"看上去能放，放下去没反应"。
    /// </summary>
    public bool CanCatchFiles => IsEnabled && CanAcceptFiles;
    public bool CanSpeak => InteractionEnabled && ChatterEnabled && _chatter.IsModelChatterAvailable;
    public double PetWidth => PetDexPetLibrary.Resolve(PetSlug).FrameWidth * PetScale;
    public double PetHeight => PetDexPetLibrary.Resolve(PetSlug).FrameHeight * PetScale;
    public double ViewWidth => PetWidth;
    public double ViewHeight => PetHeight;
    public double GroundOffset => PetDexPetLibrary.Resolve(PetSlug).BottomTransparentPixels * PetScale;
    public string PetDisplayName => PetDexPetLibrary.Resolve(PetSlug).DisplayName;

    /// <summary>
    /// 气泡底边离宠物根面板底边的距离（Canvas.Bottom），即"挂在宠物正上方 8px"。
    /// 宠物高度随缩放变，所以这个值必须算出来，不能写死在 XAML 里。
    /// </summary>
    public double BubbleOffsetY => PetHeight + 8;

    public int Level => Snapshot.Level;
    public double LevelProgress => Snapshot.LevelProgress;
    public double MoodValue => Snapshot.Mood;
    public double EnergyValue => Snapshot.Energy;
    public int BondValue => Snapshot.Bond;
    public int CompanionDays => Snapshot.CompanionDays;
    public int TotalPats => Snapshot.TotalPats;
    public int TotalFeeds => Snapshot.TotalFeeds;
    public int TotalPlays => Snapshot.TotalPlays;
    public int TotalConversations => Snapshot.TotalConversations;
    public int TotalToolCalls => Snapshot.TotalToolCalls;
    public int TotalNeedsMet => Snapshot.TotalNeedsMet;
    public PetNeedKind ActiveNeed => Snapshot.ActiveNeed;
    public bool HasActiveNeed => InteractionEnabled && Snapshot.ActiveNeed != PetNeedKind.None;

    public string LevelProgressText => Snapshot.IsMaxLevel
        ? L("Pet.Profile.MaxLevel", "MAX")
        : $"{Snapshot.ExpIntoLevel} / {Snapshot.ExpForNextLevel}";

    /// <summary>等级段位称号。等级本身是个数字，称号才是用户记得住的东西。</summary>
    public string RankTitle => Snapshot.Level switch
    {
        <= 3 => L("Pet.Rank.Hatchling", "Hatchling"),
        <= 7 => L("Pet.Rank.Buddy", "Buddy"),
        <= 11 => L("Pet.Rank.Companion", "Companion"),
        <= 16 => L("Pet.Rank.Confidant", "Confidant"),
        _ => L("Pet.Rank.Soulmate", "Soulmate")
    };

    public string NeedText => Snapshot.ActiveNeed switch
    {
        PetNeedKind.Hungry => L("Pet.Need.Hungry", "Hungry - feed it a snack"),
        PetNeedKind.Bored => L("Pet.Need.Bored", "Bored - play with it"),
        PetNeedKind.Lonely => L("Pet.Need.Lonely", "Lonely - give it a pat"),
        _ => string.Empty
    };

    public PetDexAnimationState AnimationState => State switch
    {
        VirtualPetState.Celebrating => PetDexAnimationState.Jumping,
        VirtualPetState.Alert => PetDexAnimationState.Failed,
        _ when MotionState == VirtualPetMotionState.Dragging => PetDexAnimationState.Waving,
        VirtualPetState.Interacting => _interactionAnimation,
        _ when MotionState == VirtualPetMotionState.Falling => PetDexAnimationState.Jumping,
        _ when MotionState == VirtualPetMotionState.MovingLeft => PetDexAnimationState.RunningLeft,
        _ when MotionState == VirtualPetMotionState.MovingRight => PetDexAnimationState.RunningRight,
        VirtualPetState.Thinking when _queued => PetDexAnimationState.Waiting,
        VirtualPetState.Thinking => PetDexAnimationState.Review,
        VirtualPetState.Working => PetDexAnimationState.Running,
        _ => PetDexAnimationState.Idle
    };

    public string CueSymbol => State switch
    {
        VirtualPetState.Thinking => _queued ? "⋯" : "✦",
        VirtualPetState.Working => ToolSymbol(_activeTool),
        VirtualPetState.Celebrating => "✓",
        VirtualPetState.Alert => "!",
        VirtualPetState.Interacting => "♥",
        // 需求提示只在闲下来时出现：模型正在干活时，工具符号才是用户要看的东西。
        _ => NeedSymbol()
    };

    public string StatusText => State switch
    {
        VirtualPetState.Thinking when _queued => L("Pet.Status.Queued", "Waiting for a turn"),
        VirtualPetState.Thinking => L("Pet.Status.Thinking", "Athena is thinking"),
        VirtualPetState.Working when _subAgentsActive && string.IsNullOrWhiteSpace(_activeTool)
            => L("Pet.Status.SubAgents", "The owl village is at work"),
        VirtualPetState.Working => L("Pet.Status.Working", "Athena is using a tool"),
        VirtualPetState.Celebrating => L("Pet.Status.Complete", "Response complete"),
        VirtualPetState.Alert => L("Pet.Status.Error", "Something needs attention"),
        VirtualPetState.Interacting => IdentityLine(),
        VirtualPetState.Sleeping when HasActiveNeed => NeedText,
        VirtualPetState.Sleeping => L("Pet.Status.Sleeping", "Resting"),
        _ when HasActiveNeed => NeedText,
        _ => IdentityLine()
    };

    public void ApplySettings(AppConfig config)
    {
        IsEnabled = config.VirtualPetEnabled;
        ReducedMotion = config.VirtualPetReducedMotion;
        var slug = PetDexPetLibrary.Resolve(config.VirtualPetSlug).Slug;
        var slugChanged = !string.Equals(slug, PetSlug, StringComparison.OrdinalIgnoreCase);
        PetSlug = slug;
        PetScale = PetDexPetLibrary.ClampScale(config.VirtualPetScale);
        RoamingEnabled = config.VirtualPetRoamingEnabled;
        GravityEnabled = config.VirtualPetGravityEnabled;
        RoamArea = config.VirtualPetRoamArea;
        InteractionEnabled = config.VirtualPetInteractionEnabled;
        ChatterEnabled = config.VirtualPetChatterEnabled;
        if (ReducedMotion)
            FrameIndex = 0;
        if (!InteractionEnabled)
        {
            IsProfileOpen = false;
            ClearBubble();
        }
        // 换宠物 = 认识一位新伙伴：档案按 slug 分别记账，这里只是切到另一本。
        if (slugChanged) Snapshot = _progression.GetSnapshot(PetSlug);
        _lastAnnouncedNeed = Snapshot.ActiveNeed;
        SpeakCommand.NotifyCanExecuteChanged();
    }

    public void SetMotion(VirtualPetMotionState motion) => MotionState = motion;

    public void SetConversationActivity(bool active, bool queued)
    {
        _conversationActive = active;
        _queued = queued;
        if (active || queued) Touch();
        ResolveState(DateTime.UtcNow);
    }

    public void SetSubAgentsRunning(bool active)
    {
        _subAgentsActive = active;
        if (active) Touch();
        ResolveState(DateTime.UtcNow);
    }

    /// <summary>
    /// 本轮用户输入的开头一小段，仅在模型台词开启时随请求发送，用来让台词贴合当前话题。
    /// </summary>
    public void SetTopicHint(string? text)
        => _topicHint = string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    public void BeginTool(string? toolName)
    {
        _activeTool = string.IsNullOrWhiteSpace(toolName) ? null : toolName;
        if (_activeTool != null) _lastToolName = _activeTool;
        Touch();
        ResolveState(DateTime.UtcNow);
    }

    public void FinishTool(bool succeeded, string? nextRunningTool = null)
    {
        _activeTool = string.IsNullOrWhiteSpace(nextRunningTool) ? null : nextRunningTool;
        Touch();
        if (!succeeded) _progression.RecordToolFailure(PetSlug);
        if (!succeeded && string.IsNullOrWhiteSpace(_activeTool))
            SetTransient(VirtualPetState.Alert, AlertDuration);
        else
            ResolveState(DateTime.UtcNow);
    }

    /// <param name="toolCalls">本轮发生的工具调用数。只在收尾汇总一次，避免每次调用都写盘。</param>
    public void CompleteResponse(bool succeeded, bool interrupted, int toolCalls = 0)
    {
        _conversationActive = false;
        _queued = false;
        _activeTool = null;
        Touch();
        if (interrupted)
        {
            ClearTransient();
            ResolveState(DateTime.UtcNow);
            return;
        }

        _progression.RecordConversationCompleted(PetSlug, succeeded, toolCalls);
        SetTransient(
            succeeded ? VirtualPetState.Celebrating : VirtualPetState.Alert,
            succeeded ? CelebrationDuration : AlertDuration);
        if (succeeded) ShowLine(PetChatterTopic.ResponseDone, withModelLine: true);
    }

    /// <summary>推进精灵动画行、过期瞬时状态与气泡，并按真实时间推进养成数值。</summary>
    public void Advance(DateTime now)
    {
        ResolveState(now);
        ExpireBubble(now);
        PollProgression(now);

        if (ReducedMotion)
        {
            if (FrameIndex != 0) FrameIndex = 0;
            return;
        }

        var pet = PetDexPetLibrary.Resolve(PetSlug);
        var frameCount = pet.FrameCount(AnimationState);
        var frameDuration = pet.LoopMs / (double)frameCount;
        var next = (int)((now - _animationStartedAt).TotalMilliseconds / frameDuration) % frameCount;
        if (next != FrameIndex) FrameIndex = next;
    }

    /// <summary>宠物接住了一批拖进来的文件。</summary>
    public void AcceptDroppedFiles(IReadOnlyList<IStorageFile> files)
    {
        if (files.Count == 0 || !CanCatchFiles) return;
        FilesDropped?.Invoke(this, files);
        Touch();
        ClearTransient();
        PlayInteractionAnimation(PetDexAnimationState.Jumping);
        ShowLine(PetChatterTopic.FileCaught, withModelLine: false);
    }

    // ===== 命令 =====

    /// <summary>单击宠物。有需求时优先满足需求，否则就是摸摸头。</summary>
    [RelayCommand]
    private void Poke()
    {
        Touch();
        ClearTransient();
        if (!InteractionEnabled)
        {
            ResolveState(DateTime.UtcNow);
            return;
        }
        // 头顶挂着需求时，单击直接回应那个需求——用户看到的提示符就是这次点击的含义。
        var kind = Snapshot.ActiveNeed != PetNeedKind.None
            ? VirtualPetProgressionRules.SatisfyingInteraction(Snapshot.ActiveNeed)
            : PetInteractionKind.Pat;
        RunInteraction(kind);
    }

    [RelayCommand(CanExecute = nameof(CanInteract))]
    private void Pat() => RunInteraction(PetInteractionKind.Pat);

    [RelayCommand(CanExecute = nameof(CanInteract))]
    private void Feed() => RunInteraction(PetInteractionKind.Feed);

    [RelayCommand(CanExecute = nameof(CanInteract))]
    private void Play() => RunInteraction(PetInteractionKind.Play);

    /// <summary>让宠物说一句模型生成的话。模型台词不可用时命令不可执行。</summary>
    [RelayCommand(CanExecute = nameof(CanSpeak))]
    private void Speak()
    {
        Touch();
        ClearTransient();
        // userRequested：这一句是用户点出来的，不该被后台台词刚用掉的最短间隔挡回去。
        ShowLine(PetChatterTopic.Idle, withModelLine: true, userRequested: true);
    }

    [RelayCommand]
    private void ToggleProfile() => IsProfileOpen = !IsProfileOpen;

    [RelayCommand]
    private void CloseProfile() => IsProfileOpen = false;

    /// <summary>让宠物立刻进入休息。下一次活动或互动会把它叫醒。</summary>
    [RelayCommand]
    private void Rest()
    {
        ClearTransient();
        ClearBubble();
        _lastActivityAt = DateTime.UtcNow - SleepAfter;
        ResolveState(DateTime.UtcNow);
    }

    [RelayCommand]
    private void Hide()
    {
        IsProfileOpen = false;
        ClearBubble();
        HideRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Wake()
    {
        Touch();
        ClearTransient();
        ResolveState(DateTime.UtcNow);
    }

    // ===== 内部 =====

    private void RunInteraction(PetInteractionKind kind)
    {
        if (!InteractionEnabled) return;
        Touch();
        ClearTransient();

        var result = _progression.Interact(PetSlug, kind);
        Snapshot = result.Snapshot;

        if (kind == PetInteractionKind.Play && result.Accepted)
            PlayBurstRequested?.Invoke(this, EventArgs.Empty);

        PlayInteractionAnimation(kind switch
        {
            PetInteractionKind.Feed => PetDexAnimationState.Jumping,
            PetInteractionKind.Play => PetDexAnimationState.Running,
            _ => PetDexAnimationState.Waving
        });

        var topic = TopicFor(kind, result);
        ShowLine(topic, withModelLine: result.Accepted);
        _lastAnnouncedNeed = Snapshot.ActiveNeed;
    }

    private static PetChatterTopic TopicFor(PetInteractionKind kind, PetInteractionResult result)
    {
        if (result.LeveledUp) return PetChatterTopic.LevelUp;
        if (result.SatisfiedANeed) return PetChatterTopic.NeedMet;
        if (!result.Accepted)
        {
            return kind switch
            {
                PetInteractionKind.Feed => PetChatterTopic.FeedCooldown,
                PetInteractionKind.Play => PetChatterTopic.PlayCooldown,
                _ => PetChatterTopic.PatCooldown
            };
        }
        return kind switch
        {
            PetInteractionKind.Feed => PetChatterTopic.Feed,
            PetInteractionKind.Play => PetChatterTopic.Play,
            _ => PetChatterTopic.Pat
        };
    }

    private void PlayInteractionAnimation(PetDexAnimationState animation)
    {
        if (ReducedMotion) return;
        _interactionAnimation = animation;
        SetTransient(VirtualPetState.Interacting, InteractionDuration);
    }

    /// <summary>
    /// 先把本地台词放进气泡，再（可选地）用模型那句替换。顺序是刻意的：
    /// 用户永远立刻看到一句话，模型慢了、限流了、没配置，都只是"没有被替换"。
    /// </summary>
    private void ShowLine(PetChatterTopic topic, bool withModelLine, bool userRequested = false)
    {
        if (!InteractionEnabled) return;
        var token = ++_bubbleToken;
        BubbleText = _chatter.GetLocalLine(topic, Snapshot.MoodBand);
        var now = DateTime.UtcNow;
        _bubbleUntil = now + BubbleDuration;
        if (!withModelLine || !ChatterEnabled || !_chatter.IsModelChatterAvailable) return;
        _pendingModelLineToken = token;
        _modelLineGraceUntil = now + ModelLineGrace;
        _ = ReplaceWithModelLineAsync(topic, token, userRequested);
    }

    private async Task ReplaceWithModelLineAsync(PetChatterTopic topic, long token, bool userRequested)
    {
        try
        {
            var request = new PetChatterRequest(
                topic,
                PetDisplayName,
                Snapshot.Level,
                Snapshot.Mood,
                Snapshot.Energy,
                Snapshot.ActiveNeed,
                _lastToolName,
                _topicHint,
                _localizationService?.CurrentLanguage ?? "en-US",
                userRequested);
            var line = await _chatter.TryGenerateAsync(request);
            // 气泡在等待期间可能已经被换成别的场景，甚至已经消失：只认自己那一次。
            if (string.IsNullOrWhiteSpace(line) || token != _bubbleToken || !HasBubble) return;
            BubbleText = line;
            _bubbleUntil = DateTime.UtcNow + BubbleDuration;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Pet chatter replacement failed; keeping the local line");
        }
        finally
        {
            // 请求有结果了（哪怕是 null 或异常），气泡就该恢复正常的到期节奏。
            if (_pendingModelLineToken == token) _pendingModelLineToken = 0;
        }
    }

    private void ExpireBubble(DateTime now)
    {
        if (!HasBubble) return;
        // 模型台词还在路上时不让气泡到期：此刻的本地台词是它的占位符。气泡只活 4.5 秒，
        // 而一次模型调用允许慢到 8 秒——先到期就等于慢回来的那句永远显示不出来，
        // "说句话"于是看起来从不走模型。宽限期有上限，挂死的请求按不住气泡。
        if (_pendingModelLineToken == _bubbleToken && now < _modelLineGraceUntil) return;
        if (now >= _bubbleUntil) ClearBubble();
    }

    private void ClearBubble()
    {
        _bubbleToken++;
        if (HasBubble) BubbleText = string.Empty;
        _bubbleUntil = DateTime.MinValue;
    }

    private void PollProgression(DateTime now)
    {
        if (!InteractionEnabled) return;
        if (now - _lastProgressionPollAt < ProgressionPollInterval) return;
        _lastProgressionPollAt = now;

        var busy = IsBusy || MotionState != VirtualPetMotionState.None;
        var snapshot = _progression.Advance(PetSlug, busy);
        if (!string.Equals(snapshot.Slug, PetSlug, StringComparison.OrdinalIgnoreCase)) return;
        Snapshot = snapshot;
        AnnounceNeedIfNew();
    }

    /// <summary>需求刚冒出来时说一句，之后就只靠头顶的提示符，不反复打扰。</summary>
    private void AnnounceNeedIfNew()
    {
        if (Snapshot.ActiveNeed == _lastAnnouncedNeed) return;
        _lastAnnouncedNeed = Snapshot.ActiveNeed;
        if (Snapshot.ActiveNeed == PetNeedKind.None || ReducedMotion) return;
        ShowLine(Snapshot.ActiveNeed switch
        {
            PetNeedKind.Hungry => PetChatterTopic.NeedHungry,
            PetNeedKind.Bored => PetChatterTopic.NeedBored,
            _ => PetChatterTopic.NeedLonely
        }, withModelLine: false);
    }

    /// <summary>
    /// 养成服务的变更通知。事件可能来自任意线程（流式回合的续体就常常落在线程池上），
    /// 而 <see cref="Snapshot"/> 一改就会发 PropertyChanged，绑定只能在 UI 线程上收。
    ///
    /// 这里<b>不</b>往 dispatcher 投递：投递会在还没启动消息循环的宿主里凭空多出一个
    /// 跨线程作业（无头套件就是这样炸的）。非 UI 线程上直接丢掉这一次通知——
    /// <see cref="PollProgression"/> 每秒在 UI 线程上重读一次快照，最多迟一秒就补上。
    /// </summary>
    private void OnSnapshotChanged(object? sender, VirtualPetSnapshot snapshot)
    {
        if (_disposed || !Dispatcher.UIThread.CheckAccess()) return;
        if (!string.Equals(snapshot.Slug, PetSlug, StringComparison.OrdinalIgnoreCase)) return;
        Snapshot = snapshot;
    }

    private string NeedSymbol() => HasActiveNeed
        ? Snapshot.ActiveNeed switch
        {
            PetNeedKind.Hungry => "♨",
            PetNeedKind.Bored => "♪",
            PetNeedKind.Lonely => "♡",
            _ => string.Empty
        }
        : State == VirtualPetState.Sleeping ? "☾" : string.Empty;

    private string IdentityLine() => InteractionEnabled
        ? string.Format(
            _localizationService?.GetString("Pet.Status.Identity", "{0} · Lv.{1} {2}")
            ?? "{0} · Lv.{1} {2}",
            PetDisplayName,
            Snapshot.Level,
            RankTitle)
        : L("Pet.Status.Idle", "Ready");

    private void ResolveState(DateTime now)
    {
        if (_transientState.HasValue && now >= _transientUntil) ClearTransient();

        var next = _transientState
                   ?? (!string.IsNullOrWhiteSpace(_activeTool) ? VirtualPetState.Working
                       : _subAgentsActive ? VirtualPetState.Working
                       : _conversationActive || _queued ? VirtualPetState.Thinking
                       : now - _lastActivityAt >= SleepAfter ? VirtualPetState.Sleeping
                       : VirtualPetState.Idle);
        SetState(next, now);
    }

    private void SetTransient(VirtualPetState state, TimeSpan duration)
    {
        _transientState = state;
        _transientUntil = DateTime.UtcNow + duration;
        SetState(state, DateTime.UtcNow);
    }

    private void ClearTransient()
    {
        _transientState = null;
        _transientUntil = DateTime.MinValue;
    }

    private void SetState(VirtualPetState next, DateTime now)
    {
        if (State == next) return;
        State = next;
        _animationStartedAt = now;
        FrameIndex = 0;
    }

    private void Touch() => _lastActivityAt = DateTime.UtcNow;

    partial void OnInteractionEnabledChanged(bool value)
    {
        PatCommand.NotifyCanExecuteChanged();
        FeedCommand.NotifyCanExecuteChanged();
        PlayCommand.NotifyCanExecuteChanged();
        SpeakCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasActiveNeed));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(CueSymbol));
        OnPropertyChanged(nameof(HasCue));
    }

    partial void OnChatterEnabledChanged(bool value) => SpeakCommand.NotifyCanExecuteChanged();

    private static string ToolSymbol(string? toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName)) return "✦";
        if (toolName.Contains("terminal", StringComparison.OrdinalIgnoreCase)) return ">_";
        if (toolName.Contains("file", StringComparison.OrdinalIgnoreCase)
            || toolName.Contains("document", StringComparison.OrdinalIgnoreCase)) return "▤";
        if (toolName.Contains("web", StringComparison.OrdinalIgnoreCase)
            || toolName.Contains("browser", StringComparison.OrdinalIgnoreCase)) return "◎";
        if (toolName.Contains("memory", StringComparison.OrdinalIgnoreCase)) return "◇";
        if (toolName.Contains("image", StringComparison.OrdinalIgnoreCase)) return "◈";
        if (toolName.Contains("task", StringComparison.OrdinalIgnoreCase)
            || toolName.Contains("schedule", StringComparison.OrdinalIgnoreCase)) return "◷";
        if (toolName.Contains("configuration", StringComparison.OrdinalIgnoreCase)
            || toolName.Contains("config", StringComparison.OrdinalIgnoreCase)) return "⚙";
        return "✦";
    }

    private string L(string key, string fallback)
        => _localizationService?.GetString(key, fallback) ?? fallback;

    public void RefreshLocalization()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(CueSymbol));
        OnPropertyChanged(nameof(RankTitle));
        OnPropertyChanged(nameof(NeedText));
        OnPropertyChanged(nameof(LevelProgressText));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _progression.SnapshotChanged -= OnSnapshotChanged;
        foreach (var owned in _ownedServices) owned.Dispose();
        GC.SuppressFinalize(this);
    }
}
