using Athena.UI.Models;
using Athena.UI.Services;
using Athena.UI.Services.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;

namespace Athena.UI.ViewModels;

public enum VirtualPetState
{
    Idle,
    Thinking,
    Working,
    Celebrating,
    Alert,
    Sleeping
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
/// Presentation-only state reducer for the selected PetDex companion. It observes conversation activity,
/// tool use, and sub-agent activity, but never owns or initiates business operations.
/// </summary>
public partial class VirtualPetViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan SleepAfter = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan CelebrationDuration = TimeSpan.FromSeconds(1.6);
    private static readonly TimeSpan AlertDuration = TimeSpan.FromSeconds(2.2);

    private readonly ILocalizationService? _localizationService;
    private DateTime _lastActivityAt = DateTime.UtcNow;
    private DateTime _animationStartedAt = DateTime.UtcNow;
    private DateTime _transientUntil = DateTime.MinValue;
    private VirtualPetState? _transientState;
    private bool _conversationActive;
    private bool _queued;
    private bool _subAgentsActive;
    private string? _activeTool;
    [ObservableProperty]
    private bool _isEnabled = true;

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
    private double _petScale = 0.5;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PetWidth))]
    [NotifyPropertyChangedFor(nameof(PetHeight))]
    [NotifyPropertyChangedFor(nameof(ViewWidth))]
    [NotifyPropertyChangedFor(nameof(ViewHeight))]
    [NotifyPropertyChangedFor(nameof(GroundOffset))]
    private string _petSlug = PetDexPetLibrary.DefaultSlug;

    [ObservableProperty]
    private bool _roamingEnabled = true;

    [ObservableProperty]
    private bool _gravityEnabled = true;

    [ObservableProperty]
    private VirtualPetRoamArea _roamArea = VirtualPetRoamArea.LowerHalf;

    public VirtualPetViewModel(ILocalizationService? localizationService = null)
    {
        _localizationService = localizationService;
    }

    public bool IsBusy => State is VirtualPetState.Thinking or VirtualPetState.Working;
    public bool IsCelebrating => State == VirtualPetState.Celebrating && !ReducedMotion;
    public bool IsAlert => State == VirtualPetState.Alert && !ReducedMotion;
    public bool IsSleeping => State == VirtualPetState.Sleeping;
    public bool CanAutoRoam => State == VirtualPetState.Idle && !ReducedMotion;
    public bool HasCue => State != VirtualPetState.Idle;
    public double PetWidth => PetDexPetLibrary.Resolve(PetSlug).FrameWidth * PetScale;
    public double PetHeight => PetDexPetLibrary.Resolve(PetSlug).FrameHeight * PetScale;
    public double ViewWidth => PetWidth;
    public double ViewHeight => PetHeight;
    public double GroundOffset => PetDexPetLibrary.Resolve(PetSlug).BottomTransparentPixels * PetScale;

    public PetDexAnimationState AnimationState => State switch
    {
        VirtualPetState.Celebrating => PetDexAnimationState.Jumping,
        VirtualPetState.Alert => PetDexAnimationState.Failed,
        _ when MotionState == VirtualPetMotionState.Dragging => PetDexAnimationState.Waving,
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
        VirtualPetState.Sleeping => "☾",
        _ => string.Empty
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
        VirtualPetState.Sleeping => L("Pet.Status.Sleeping", "Resting"),
        _ => L("Pet.Status.Idle", "Ready")
    };

    public void ApplySettings(AppConfig config)
    {
        IsEnabled = config.VirtualPetEnabled;
        ReducedMotion = config.VirtualPetReducedMotion;
        PetSlug = PetDexPetLibrary.Resolve(config.VirtualPetSlug).Slug;
        PetScale = PetDexPetLibrary.ClampScale(config.VirtualPetScale);
        RoamingEnabled = config.VirtualPetRoamingEnabled;
        GravityEnabled = config.VirtualPetGravityEnabled;
        RoamArea = config.VirtualPetRoamArea;
        if (ReducedMotion)
            FrameIndex = 0;
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

    public void BeginTool(string? toolName)
    {
        _activeTool = string.IsNullOrWhiteSpace(toolName) ? null : toolName;
        Touch();
        ResolveState(DateTime.UtcNow);
    }

    public void FinishTool(bool succeeded, string? nextRunningTool = null)
    {
        _activeTool = string.IsNullOrWhiteSpace(nextRunningTool) ? null : nextRunningTool;
        Touch();
        if (!succeeded)
            SetTransient(VirtualPetState.Alert, AlertDuration);
        else
            ResolveState(DateTime.UtcNow);
    }

    public void CompleteResponse(bool succeeded, bool interrupted)
    {
        _conversationActive = false;
        _queued = false;
        _activeTool = null;
        Touch();
        if (interrupted)
        {
            ClearTransient();
            ResolveState(DateTime.UtcNow);
        }
        else
        {
            SetTransient(
                succeeded ? VirtualPetState.Celebrating : VirtualPetState.Alert,
                succeeded ? CelebrationDuration : AlertDuration);
        }
    }

    /// <summary>Advances the selected PetDex action row and expires transient/idle states.</summary>
    public void Advance(DateTime now)
    {
        ResolveState(now);
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

    [RelayCommand]
    private void Wake()
    {
        Touch();
        ClearTransient();
        ResolveState(DateTime.UtcNow);
    }

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
    }

    public void Dispose() { }
}
