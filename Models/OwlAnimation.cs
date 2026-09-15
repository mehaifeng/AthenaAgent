using System;
using System.Collections.Generic;
using System.Linq;

namespace Athena.UI.Models;

public enum OwlAction
{
    Idle, Blink, Think, Read, Work, Walk, Takeoff, Fly, Glide, Land, Success, Error, Cancel, Farewell
}

/// <summary>Frame timing is independent of UI ticks. Single shots hold their last frame.</summary>
public sealed class OwlAnimationClip
{
    public OwlAction Action { get; }
    public IReadOnlyList<int> Durations { get; }
    public bool Loop { get; }
    public int Duration { get; }

    public OwlAnimationClip(OwlAction action, int[] durations, bool loop)
    {
        if (durations.Length == 0 || durations.Any(d => d <= 0))
            throw new ArgumentException("An owl clip needs positive frame durations.", nameof(durations));
        Action = action;
        Durations = Array.AsReadOnly((int[])durations.Clone());
        Duration = durations.Sum();
        Loop = loop;
    }

    public int FrameAt(double milliseconds)
    {
        var elapsed = Math.Max(0, milliseconds);
        if (Loop) elapsed %= Duration;
        else if (elapsed >= Duration) return Durations.Count - 1;
        for (var i = 0; i < Durations.Count; i++)
        {
            if (elapsed < Durations[i]) return i;
            elapsed -= Durations[i];
        }
        return Durations.Count - 1;
    }
}

public static class OwlAnimationClips
{
    private static int[] Even(int frames, int ms) => Enumerable.Repeat(ms, frames).ToArray();
    public static IReadOnlyDictionary<OwlAction, OwlAnimationClip> All { get; } =
        new Dictionary<OwlAction, OwlAnimationClip>
        {
            [OwlAction.Idle] = new(OwlAction.Idle, Even(8, 400), true),
            [OwlAction.Blink] = new(OwlAction.Blink, [70, 45, 40, 60, 50, 40, 45, 70], false),
            [OwlAction.Think] = new(OwlAction.Think, [150, 100, 100, 500, 300, 100, 100, 250], false),
            [OwlAction.Read] = new(OwlAction.Read, [180, 130, 180, 300, 130, 130, 180, 270], false),
            [OwlAction.Work] = new(OwlAction.Work, [180, 100, 90, 140, 90, 100, 160, 300], false),
            [OwlAction.Walk] = new(OwlAction.Walk, Even(12, 55), true),
            [OwlAction.Takeoff] = new(OwlAction.Takeoff, [90, 70, 60, 50, 50, 50, 50, 60], false),
            [OwlAction.Fly] = new(OwlAction.Fly, [60, 55, 50, 45, 45, 50, 60, 65, 70, 70, 65, 65], true),
            [OwlAction.Glide] = new(OwlAction.Glide, Even(8, 100), true),
            [OwlAction.Land] = new(OwlAction.Land, Even(12, 55), false),
            [OwlAction.Success] = new(OwlAction.Success, [120, 90, 90, 160, 120, 100, 100, 200], false),
            [OwlAction.Error] = new(OwlAction.Error, [100, 80, 80, 160, 120, 120, 120, 180], false),
            [OwlAction.Cancel] = new(OwlAction.Cancel, Even(8, 100), false),
            [OwlAction.Farewell] = new(OwlAction.Farewell, [60, 60, 55, 55, 55, 55, 55, 55], false)
        };
    public static OwlAnimationClip Get(OwlAction action) => All[action];
}

/// <summary>Pure monotonic-time motion sampler; no timers, bitmaps or business side effects.</summary>
public sealed class OwlAnimationPlayer
{
    private double _startedAt;
    private double _nextGestureAt;
    private readonly double _phase;
    private bool _gesture;
    private bool _blinkNext;
    private SubAgentState _state;
    private SubAgentZone _zone;
    private double _fromX, _fromY, _toX, _toY, _travelStart, _travelDuration;
    private bool _flying;
    private double _walkPhase;
    private int _walkCycles;
    private OwlAction? _arrivalAction;
    private bool _requiredActivity;
    private bool _terminalPresentation;
    public OwlAction Action { get; private set; } = OwlAction.Idle;
    public int Frame { get; private set; }
    public double X { get; private set; }
    public double Y { get; private set; }
    public double Lift { get; private set; }
    public bool FacingLeft { get; private set; }
    public bool IsTravelling { get; private set; }
    public bool IsVanishing => Action == OwlAction.Farewell;
    /// <summary>
    /// True while a semantic presentation still has to finish. Business execution never awaits this;
    /// the village uses it only to defer a return flight or curtain call.
    /// </summary>
    public bool HasRequiredPresentation => IsTravelling || _arrivalAction.HasValue || _requiredActivity || _terminalPresentation;

    public OwlAnimationPlayer(double x, double y, double now, int seed)
    {
        X = _toX = x;
        Y = _toY = y;
        _phase = (uint)seed % 2300;
        _startedAt = now - _phase;
        _nextGestureAt = now + 3000 + _phase;
    }

    public void SetActivity(SubAgentState state, SubAgentZone zone, double now)
    {
        Sample(now);
        var changed = _state != state;
        _state = state;
        _zone = zone;
        if (IsVanishing) return;
        if (state is SubAgentState.Error or SubAgentState.Cancelled)
        {
            if (!changed) return;
            // Stop at the currently rendered point, including the current height: no teleport.
            Y -= Lift;
            Lift = 0;
            IsTravelling = false;
            _arrivalAction = null;
            _requiredActivity = false;
            _terminalPresentation = false;
            Start(state == SubAgentState.Error ? OwlAction.Error : OwlAction.Cancel, now);
        }
        else if (changed && !IsTravelling && !_requiredActivity && !_arrivalAction.HasValue)
        {
            _terminalPresentation = state == SubAgentState.Done && zone == SubAgentZone.Perch;
            Start(_terminalPresentation ? OwlAction.Success : OwlAction.Idle, now);
        }
    }

    /// <summary>
    /// Presents one complete semantic action. If travel is active, the action begins at the
    /// completed landing time; repeated requests for the same action coalesce.
    /// </summary>
    public void PresentActivity(OwlAction action, double now)
    {
        Sample(now);
        if (IsVanishing || _state is SubAgentState.Error or SubAgentState.Cancelled) return;
        _terminalPresentation = false;
        if (IsTravelling)
        {
            _arrivalAction = action;
            return;
        }
        if (_requiredActivity && Action == action) return;
        _arrivalAction = null;
        Start(action, now);
        _requiredActivity = true;
    }

    public void MoveTo(double x, double y, bool flying, double now)
    {
        Sample(now);
        if (IsVanishing || _state is SubAgentState.Error or SubAgentState.Cancelled) return;
        if (!flying && (IsTravelling || _state == SubAgentState.Done)) return;
        var distance = Math.Sqrt((x - X) * (x - X) + (y - Y) * (y - Y));
        if (distance < 0.5) return;
        _fromX = X;
        _fromY = Y - Lift;
        _toX = x;
        _toY = y;
        if (Math.Abs(x - X) > 1) FacingLeft = x < X;
        var alreadyFlying = IsTravelling && _flying;
        _flying = flying;
        IsTravelling = true;
        _terminalPresentation = false;
        _travelDuration = flying ? Math.Clamp(distance / 230 * 1000, 800, 1900)
            : Math.Clamp(distance / 65 * 1000, 500, 2000);
        _travelStart = now;
        _walkCycles = Math.Max(1, (int)Math.Round(distance / 50));
        // Retarget an airborne owl from its sampled point, without another takeoff.
        if (alreadyFlying) _travelStart -= OwlAnimationClips.Get(OwlAction.Takeoff).Duration;
        Start(flying && !alreadyFlying ? OwlAction.Takeoff : flying ? OwlAction.Fly : OwlAction.Walk, now);
        Sample(now);
    }

    public void Vanish(double now)
    {
        Sample(now);
        IsTravelling = false;
        _arrivalAction = null;
        _requiredActivity = false;
        _terminalPresentation = false;
        Start(OwlAction.Farewell, now);
    }

    public void Sample(double now)
    {
        if (IsTravelling)
        {
            var elapsed = Math.Max(0, now - _travelStart);
            var takeoff = _flying ? OwlAnimationClips.Get(OwlAction.Takeoff).Duration : 0;
            var landing = _flying ? OwlAnimationClips.Get(OwlAction.Land).Duration : 0;
            var progress = Math.Clamp((elapsed - takeoff) / _travelDuration, 0, 1);
            var eased = progress * progress * (3 - 2 * progress);
            _walkPhase = eased * _walkCycles * OwlAnimationClips.Get(OwlAction.Walk).Duration;
            X = _fromX + (_toX - _fromX) * eased;
            Y = _fromY + (_toY - _fromY) * eased;
            Lift = _flying ? Math.Sin(progress * Math.PI) * 24 : 0;
            if (elapsed >= takeoff + _travelDuration + landing)
            {
                IsTravelling = false;
                Lift = 0;
                var arrivedAt = _travelStart + takeoff + _travelDuration + landing;
                if (_arrivalAction is { } arrivalAction)
                {
                    _arrivalAction = null;
                    Start(arrivalAction, arrivedAt);
                    _requiredActivity = true;
                }
                else
                {
                    _terminalPresentation = _state == SubAgentState.Done && _zone == SubAgentZone.Perch;
                    Start(_terminalPresentation ? OwlAction.Success : OwlAction.Idle, arrivedAt);
                }
                _nextGestureAt = now + 2300 + _phase;
            }
            else if (_flying)
            {
                var next = elapsed < takeoff ? OwlAction.Takeoff
                    : progress >= 1 ? OwlAction.Land
                    : _travelDuration > 1300 && progress > .4 && progress < .7 ? OwlAction.Glide : OwlAction.Fly;
                var start = next == OwlAction.Takeoff ? _travelStart
                    : next == OwlAction.Land ? _travelStart + takeoff + _travelDuration
                    : _travelStart + takeoff;
                if (Action != next) Start(next, start);
            }
        }
        else if (_requiredActivity)
        {
            if (now - _startedAt >= OwlAnimationClips.Get(Action).Duration)
            {
                _requiredActivity = false;
                Start(OwlAction.Idle, _startedAt + OwlAnimationClips.Get(Action).Duration);
                _nextGestureAt = now + 2500 + _phase;
            }
        }
        else if (_terminalPresentation)
        {
            // Keep the final success frame visible, but release the curtain-call gate.
            if (now - _startedAt >= OwlAnimationClips.Get(Action).Duration)
                _terminalPresentation = false;
        }
        else if (!IsVanishing && _state is SubAgentState.Pending or SubAgentState.Running)
        {
            if (_gesture && now - _startedAt >= OwlAnimationClips.Get(Action).Duration)
            {
                Start(OwlAction.Idle, now);
                _nextGestureAt = now + 2500 + _phase;
            }
            else if (now >= _nextGestureAt && !_gesture)
            {
                var next = _blinkNext || _state == SubAgentState.Pending ? OwlAction.Blink : _zone switch
                {
                    SubAgentZone.Files or SubAgentZone.Library => OwlAction.Read,
                    SubAgentZone.Web or SubAgentZone.Workshop => OwlAction.Work,
                    _ => OwlAction.Think
                };
                _blinkNext = !_blinkNext;
                Start(next, now);
                _gesture = true;
            }
        }
        Frame = OwlAnimationClips.Get(Action).FrameAt(Action == OwlAction.Walk ? _walkPhase : now - _startedAt);
    }

    private void Start(OwlAction action, double now)
    {
        Action = action;
        _startedAt = now;
        Frame = 0;
        _gesture = false;
    }
}
