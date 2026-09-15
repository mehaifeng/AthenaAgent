using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using Avalonia.Media.Imaging;
using Athena.UI.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace Athena.UI.ViewModels;

/// <summary>
/// 单只"猫头鹰"子代理的实时状态。UI 直接绑定本对象；编排器/Runner 在 UI 线程更新它。
/// </summary>
public partial class SubAgentViewModel : ObservableObject, ISubAgentProgress
{
    public string Id { get; } = Guid.NewGuid().ToString("N");

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _agentType = "general";

    [ObservableProperty]
    private SubAgentZone _zone = SubAgentZone.Meditation;

    [ObservableProperty]
    private string _currentAction = string.Empty;

    [ObservableProperty]
    private int _step;

    [ObservableProperty]
    private SubAgentState _state = SubAgentState.Pending;

    [ObservableProperty]
    private string _resultSummary = string.Empty;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    /// <summary>整批结束后的谢幕标记：置真触发小镇里的淡出缩小动画，随后由编排器移除。</summary>
    [ObservableProperty]
    private bool _isVanishing;

    /// <summary>过程日志（M4 的"查看过程"展示用）。</summary>
    public ObservableCollection<SubAgentLogEntry> Log { get; } = new();

    /// <summary>每只猫头鹰独立的取消源（与批次令牌联动）。由编排器赋值。</summary>
    public CancellationTokenSource? Cts { get; set; }

    /// <summary>由编排器设置的单代理超时截止点，用于区分超时与用户手动取消。</summary>
    public DateTime TimeoutAt { get; set; }

    public bool WasCancelledByUser { get; private set; }

    [RelayCommand]
    private void Cancel()
    {
        WasCancelledByUser = true;
        Cts?.Cancel();
    }

    // ===== 小镇画布定位 =====
    // 与 OwlVillageView 中的画布/猫头鹰尺寸保持同步：左上角 = 场所中心 - 半个身位 + 漂移。
    public const double OwlSize = 80;

    private static readonly Dictionary<SubAgentZone, (double X, double Y)> ZoneCenters = new()
    {
        // 2×3 网格（画布 636×522，格 300×158）：上排 文件|电脑，中排 冥想|书房，下排 工坊|归巢。
        [SubAgentZone.Files] = (162, 91),
        [SubAgentZone.Web] = (486, 91),
        [SubAgentZone.Meditation] = (162, 261),
        [SubAgentZone.Library] = (486, 261),
        [SubAgentZone.Workshop] = (162, 431),
        [SubAgentZone.Perch] = (486, 431),
    };

    // 各场所内猫头鹰可漂移的半幅（左上角相对场所中心，保证不越出场所边框）。
    private static readonly Dictionary<SubAgentZone, (double X, double Y)> ZoneExtents = new()
    {
        // 所有格同尺寸（300×158），漂移半幅一致。
        [SubAgentZone.Files] = (101, 32),
        [SubAgentZone.Web] = (101, 32),
        [SubAgentZone.Meditation] = (101, 32),
        [SubAgentZone.Library] = (101, 32),
        [SubAgentZone.Workshop] = (101, 32),
        [SubAgentZone.Perch] = (101, 32),
    };

    private static readonly Random _rng = new();

    // 当前区域内的漂移偏移（由 RepositionWander 周期性随机更新）。初值按 Id 散开，避免首帧前叠在一起。
    private double _wanderX;
    private double _wanderY;

    private readonly OwlAnimationPlayer _animation;
    internal static double AnimationNow => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;
    public double OwlX => _animation.X;
    public double OwlY => _animation.Y - _animation.Lift;
    public double OwlShadowOpacity => Math.Max(0.04, 0.15 - _animation.Lift / 220);
    public double OwlShadowScale => 1 - _animation.Lift / 60;
    public double OwlGroundOffset => _animation.Lift;
    public bool IsOwlTravelling => _animation.IsTravelling;
    internal bool HasPendingOwlPresentation
        => _deferredZone.HasValue || _hasPendingZone || _animation.HasRequiredPresentation;

    private readonly ILocalizationService? _localizationService;
    private bool _disposed;

    public SubAgentViewModel(ILocalizationService? localizationService = null)
    {
        _localizationService = localizationService;
        var h = (int)((uint)Id.GetHashCode() % int.MaxValue);
        _wanderX = (h % 31) - 15;
        _wanderY = ((h / 31) % 27) - 13;
        _animation = new OwlAnimationPlayer(CanvasX, CanvasY, AnimationNow, h);
        ScheduleNextWander(DateTime.UtcNow);
        if (_localizationService != null)
        {
            _localizationService.LanguageChanged += OnLanguageChanged;
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => RefreshStatusLabel();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _dwellTimer?.Stop();
        _hasPendingZone = false;
        _deferredZone = null;
        if (_localizationService != null)
        {
            _localizationService.LanguageChanged -= OnLanguageChanged;
        }
    }

    /// <summary>猫头鹰在小镇画布上的左上角 X（= 场所中心 - 半个身位 + 漂移）。</summary>
    public double CanvasX => ZoneCenters[Zone].X - OwlSize / 2 + _wanderX;

    /// <summary>猫头鹰在小镇画布上的左上角 Y。</summary>
    public double CanvasY => ZoneCenters[Zone].Y - OwlSize / 2 + _wanderY;

    /// <summary>设置区域内目标；同一时间轴负责位移与步态，飞行 / 终态不受游走干扰。</summary>
    public void SetWander(double x, double y)
    {
        var nextCanvasX = ZoneCenters[Zone].X - OwlSize / 2 + x;
        if (_disposed || _animation.IsTravelling || IsVanishing || State is SubAgentState.Done or SubAgentState.Error or SubAgentState.Cancelled) return;
        _animation.MoveTo(nextCanvasX, ZoneCenters[Zone].Y - OwlSize / 2 + y, false, AnimationNow);
        _wanderX = x;
        _wanderY = y;
        OnPropertyChanged(nameof(CanvasX));
        OnPropertyChanged(nameof(CanvasY));
        RefreshOwlPresentation();
    }

    // ===== 随机游走节拍 =====
    // 每只猫头鹰有自己的下次挪窝时间（5~9s 随机），互不同步；终态（完成/出错/取消）后静止。
    private DateTime _nextWanderAt = DateTime.MinValue;

    private bool ShouldWanderNow(DateTime now)
        => !_disposed && !IsVanishing && !_animation.IsTravelling && (State is SubAgentState.Pending or SubAgentState.Running) && now >= _nextWanderAt;

    private void ScheduleNextWander(DateTime now)
        => _nextWanderAt = now + TimeSpan.FromMilliseconds(5000 + _rng.NextDouble() * 4000);

    /// <summary>
    /// 为一组猫头鹰在各自所处场所内随机选取新的漂移目标，并保证同场所内两两间距 ≥ 半个身位
    /// （重叠不超过 50%）。由小镇视图高频节拍调用；每只猫头鹰只在自己的随机时刻到点才挪动，
    /// 终态的猫头鹰不再移动，但其当前位置仍参与避让。
    /// </summary>
    public static void RepositionWander(IReadOnlyList<SubAgentViewModel> owls)
    {
        const double threshold = OwlSize * 0.5; // 40px：重叠上限 50%
        var now = DateTime.UtcNow;
        foreach (var group in owls.GroupBy(o => o.Zone))
        {
            var center = ZoneCenters[group.Key];
            var ext = ZoneExtents[group.Key];
            // 本轮不动的（未到点/已终态）先占位，让要挪的躲开它们。
            var placed = group.Where(o => !o.ShouldWanderNow(now))
                              .Select(o => (X: o.CanvasX, Y: o.CanvasY))
                              .ToList();
            foreach (var owl in group)
            {
                if (!owl.ShouldWanderNow(now)) continue;
                double wx = 0, wy = 0, px = 0, py = 0;
                var found = false;
                for (var attempt = 0; attempt < 12; attempt++)
                {
                    wx = (_rng.NextDouble() * 2 - 1) * ext.X;
                    wy = (_rng.NextDouble() * 2 - 1) * ext.Y;
                    px = center.X - OwlSize / 2 + wx;
                    py = center.Y - OwlSize / 2 + wy;

                    var ok = true;
                    foreach (var p in placed)
                    {
                        var dx = px - p.X;
                        var dy = py - p.Y;
                        if (dx * dx + dy * dy < threshold * threshold) { ok = false; break; }
                    }
                    if (ok) { found = true; break; }
                }
                if (found) owl.SetWander(wx, wy);
                owl.ScheduleNextWander(now);
                placed.Add((owl.CanvasX, owl.CanvasY));
            }
        }
    }

    public Bitmap OwlFrame => OwlFrameLibrary.Get(_animation.Action, _animation.Frame);
    public double OwlSpriteScaleX => _animation.FacingLeft ? -1 : 1;
    public double OwlSpriteSize => 80;
    internal OwlAction CurrentOwlAction => _animation.Action;

    public void AdvanceAnimation(double now)
    {
        if (_disposed) return;
        var action = _animation.Action;
        var frame = _animation.Frame;
        var x = OwlX;
        var y = OwlY;
        _animation.Sample(now);
        TryBeginDeferredZone(now);
        if (action != _animation.Action || frame != _animation.Frame) OnPropertyChanged(nameof(OwlFrame));
        if (x != OwlX || y != OwlY) RefreshOwlPosition();
    }

    private void RefreshOwlPosition()
    {
        OnPropertyChanged(nameof(OwlX));
        OnPropertyChanged(nameof(OwlY));
        OnPropertyChanged(nameof(OwlShadowOpacity));
        OnPropertyChanged(nameof(OwlShadowScale));
        OnPropertyChanged(nameof(OwlGroundOffset));
    }

    private void RefreshOwlPresentation()
    {
        RefreshOwlPosition();
        OnPropertyChanged(nameof(OwlFrame));
        OnPropertyChanged(nameof(OwlSpriteScaleX));
    }

    partial void OnIsVanishingChanged(bool value)
    {
        if (value)
        {
            _dwellTimer?.Stop();
            _hasPendingZone = false;
            _deferredZone = null;
            _animation.Vanish(AnimationNow);
        }
        RefreshOwlPresentation();
    }
    public bool IsRunning => State == SubAgentState.Running;
    public bool IsDone => State == SubAgentState.Done;
    public bool IsError => State == SubAgentState.Error;
    public bool IsCancelled => State == SubAgentState.Cancelled;

    /// <summary>小镇中展示在猫头鹰头部的业务状态；成功后不再显示，避免干扰归巢画面。</summary>
    public string StatusLabel => State switch
    {
        SubAgentState.Pending => L("SubAgent.Status.Pending", "Pending"),
        SubAgentState.Running => RunningStatusLabel(),
        SubAgentState.Error => CurrentAction switch
        {
            "timeout" => L("SubAgent.Status.Timeout", "Timed out"),
            "incomplete" => L("SubAgent.Status.MaxSteps", "Exceeded max steps"),
            _ => L("SubAgent.Status.Failed", "Failed")
        },
        SubAgentState.Cancelled => L("SubAgent.Status.Cancelled", "Cancelled"),
        _ => string.Empty
    };

    private string L(string key, string fallback)
        => _localizationService?.GetString(key, fallback) ?? fallback;

    public bool HasStatusLabel => !string.IsNullOrWhiteSpace(StatusLabel);

    partial void OnZoneChanged(SubAgentZone value)
    {
        _animation.SetActivity(State, value, AnimationNow);
        OnPropertyChanged(nameof(CanvasX));
        OnPropertyChanged(nameof(CanvasY));
        RefreshOwlPresentation();
        RefreshStatusLabel();
    }

    partial void OnCurrentActionChanged(string value) => RefreshStatusLabel();

    partial void OnResultSummaryChanged(string value) => RefreshStatusLabel();

    partial void OnErrorMessageChanged(string value) => RefreshStatusLabel();

    // ===== 快速区域请求合并 =====
    // 相邻工具调用只保留最新区域，避免飞行目标频繁抖动。业务执行不等待这里的计时器；
    // 完成后的归巢另由完整的“落地 → 区域动作”演出门闩控制。
    private const double MinDwellSeconds = 1.2;
    private DateTime _lastZoneAppliedAt = DateTime.MinValue;
    private SubAgentZone _pendingZone;
    private bool _hasPendingZone;
    private SubAgentZone? _deferredZone;
    private DispatcherTimer? _dwellTimer;

    /// <summary>请求切换到某场所（须在 UI 线程调用）；受最小停留节流。</summary>
    public void RequestZone(SubAgentZone zone)
    {
        if (_disposed || IsVanishing || State is SubAgentState.Error or SubAgentState.Cancelled) return;
        if (zone == SubAgentZone.Perch && State == SubAgentState.Done)
        {
            if (Zone == SubAgentZone.Perch)
            {
                _deferredZone = null;
                return;
            }
            _deferredZone = SubAgentZone.Perch;
            TryBeginDeferredZone(AnimationNow);
            return;
        }
        // The runner requests Meditation before every model round. Do not let that passive
        // request retarget an owl that still owes the preceding tool's landing/action.
        if (zone == SubAgentZone.Meditation && Zone != SubAgentZone.Meditation
            && (_hasPendingZone || _animation.HasRequiredPresentation))
        {
            _deferredZone = SubAgentZone.Meditation;
            return;
        }

        // A concrete new tool destination supersedes a queued return-to-thinking trip.
        _deferredZone = null;
        var now = DateTime.UtcNow;
        var elapsed = (now - _lastZoneAppliedAt).TotalSeconds;
        if (elapsed >= MinDwellSeconds)
        {
            _dwellTimer?.Stop();
            _hasPendingZone = false;
            ApplyZone(zone, now);
            return;
        }

        // 尚在停留期内：记下最新目标，等停留满后再应用。
        _pendingZone = zone;
        _hasPendingZone = true;
        _dwellTimer ??= CreateDwellTimer();
        _dwellTimer.Stop();
        _dwellTimer.Interval = TimeSpan.FromSeconds(MinDwellSeconds - elapsed);
        _dwellTimer.Start();
    }

    private DispatcherTimer CreateDwellTimer()
    {
        var timer = new DispatcherTimer();
        timer.Tick += (_, _) =>
        {
            _dwellTimer!.Stop();
            _hasPendingZone = false;
            ApplyZone(_pendingZone, DateTime.UtcNow);
        };
        return timer;
    }

    private void ApplyZone(SubAgentZone zone, DateTime now)
    {
        _lastZoneAppliedAt = now;
        var animationNow = AnimationNow;
        if (Zone != zone)
        {
            var nextCanvasX = ZoneCenters[zone].X - OwlSize / 2 + _wanderX;
            _animation.MoveTo(nextCanvasX, ZoneCenters[zone].Y - OwlSize / 2 + _wanderY, true, animationNow);
        }
        Zone = zone; // 相等时 SetProperty 自动不触发；不同则 OnZoneChanged 刷新位置
        if (zone != SubAgentZone.Perch)
            _animation.PresentActivity(ActivityForZone(zone), animationNow);
        RefreshOwlPresentation();
    }

    private void TryBeginDeferredZone(double now)
    {
        if (!_deferredZone.HasValue || _hasPendingZone || _animation.HasRequiredPresentation) return;
        var destination = _deferredZone.Value;
        _deferredZone = null;
        if (Zone == destination) return;
        _lastZoneAppliedAt = DateTime.UtcNow;
        var nextCanvasX = ZoneCenters[destination].X - OwlSize / 2 + _wanderX;
        _animation.MoveTo(nextCanvasX, ZoneCenters[destination].Y - OwlSize / 2 + _wanderY, true, now);
        Zone = destination;
        if (destination != SubAgentZone.Perch)
            _animation.PresentActivity(ActivityForZone(destination), now);
        RefreshOwlPresentation();
    }

    private static OwlAction ActivityForZone(SubAgentZone zone) => zone switch
    {
        SubAgentZone.Files or SubAgentZone.Library => OwlAction.Read,
        SubAgentZone.Web or SubAgentZone.Workshop => OwlAction.Work,
        _ => OwlAction.Think
    };

    private string RunningStatusLabel()
    {
        if (CurrentAction.StartsWith("run_browser_task", StringComparison.Ordinal)) return L("SubAgent.Action.BrowserTask", "Browser task in progress");
        if (CurrentAction.StartsWith("web_search", StringComparison.Ordinal)) return L("SubAgent.Action.WebSearch", "Web search in progress");
        if (CurrentAction.StartsWith("recall_from_memory", StringComparison.Ordinal)) return L("SubAgent.Action.RecallMemory", "Recalling memory");
        if (CurrentAction.StartsWith("create_new_memory", StringComparison.Ordinal)) return L("SubAgent.Action.CreateMemory", "Writing memory");
        if (CurrentAction.StartsWith("execute_terminal_command", StringComparison.Ordinal)) return L("SubAgent.Action.Terminal", "Running terminal command");
        if (CurrentAction.StartsWith("generate_image", StringComparison.Ordinal)) return L("SubAgent.Action.GenerateImage", "Generating image");
        if (CurrentAction.StartsWith("view_self_configuration", StringComparison.Ordinal)
            || CurrentAction.StartsWith("modify_self_configuration", StringComparison.Ordinal)) return L("SubAgent.Action.Config", "Processing configuration");
        if (CurrentAction.StartsWith("create_task", StringComparison.Ordinal)
            || CurrentAction.StartsWith("update_task", StringComparison.Ordinal)
            || CurrentAction.StartsWith("list_tasks", StringComparison.Ordinal)
            || CurrentAction.StartsWith("cancel_task", StringComparison.Ordinal)
            || CurrentAction.StartsWith("run_task_now", StringComparison.Ordinal)) return L("SubAgent.Action.Task", "Processing task");
        if (CurrentAction.StartsWith("get_file_info", StringComparison.Ordinal)
            || CurrentAction.StartsWith("search_in_file", StringComparison.Ordinal)
            || CurrentAction.StartsWith("get_document_outline", StringComparison.Ordinal)
            || CurrentAction.StartsWith("read_system_file", StringComparison.Ordinal)
            || CurrentAction.StartsWith("write_system_file", StringComparison.Ordinal)
            || CurrentAction.StartsWith("modify_system_file", StringComparison.Ordinal)
            || CurrentAction.StartsWith("delete_system_file", StringComparison.Ordinal)
            || CurrentAction.StartsWith("list_system_directory", StringComparison.Ordinal)
            || CurrentAction.StartsWith("create_directory", StringComparison.Ordinal)
            || CurrentAction.StartsWith("move_system_file", StringComparison.Ordinal)
            || CurrentAction.StartsWith("copy_system_file", StringComparison.Ordinal)) return L("SubAgent.Action.File", "Processing file");

        return Zone switch
        {
            SubAgentZone.Files => L("SubAgent.Action.File", "Processing file"),
            SubAgentZone.Web => L("SubAgent.Action.Web", "Web task in progress"),
            SubAgentZone.Library => L("SubAgent.Action.Memory", "Processing memory"),
            SubAgentZone.Workshop => L("SubAgent.Action.Tools", "Processing tools"),
            _ => L("SubAgent.Action.Thinking", "Thinking")
        };
    }

    private void RefreshStatusLabel()
    {
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(HasStatusLabel));
    }

    partial void OnStateChanged(SubAgentState value)
    {
        _animation.SetActivity(value, Zone, AnimationNow);
        if (value is SubAgentState.Error or SubAgentState.Cancelled)
        {
            _dwellTimer?.Stop();
            _hasPendingZone = false;
            _deferredZone = null;
        }
        RefreshOwlPresentation();
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsDone));
        OnPropertyChanged(nameof(IsError));
        OnPropertyChanged(nameof(IsCancelled));
        RefreshStatusLabel();
    }
}
