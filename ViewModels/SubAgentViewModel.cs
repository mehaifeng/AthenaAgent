using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace Athena.UI.ViewModels;

/// <summary>
/// 单只"猫头鹰"子代理的实时状态。UI 直接绑定本对象；编排器/Runner 在 UI 线程更新它。
/// </summary>
public partial class SubAgentViewModel : ObservableObject, ISubAgentProgress
{
    private enum OwlMotion
    {
        Idle,
        Walking,
        Flying,
        Landing
    }

    private const int TravelDurationMilliseconds = 700;
    private static readonly Bitmap[] IdleFrames = LoadFrames("owl-idle", 4);
    private static readonly Bitmap[] WalkingFrames = LoadFrames("owl-walk", 4);
    private static readonly Bitmap[] FlyingFrames = LoadFrames("owl-fly", 4);
    private static readonly Bitmap[] LandingFrames = LoadFrames("owl-land", 4);

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

    private OwlMotion _owlMotion = OwlMotion.Idle;
    private DateTime _motionStartedAt = DateTime.UtcNow;
    private DateTime _motionEndsAt = DateTime.MinValue;
    private int _owlFrameIndex;
    private bool _owlFacingLeft;

    private readonly ILocalizationService? _localizationService;
    private bool _disposed;

    public SubAgentViewModel(ILocalizationService? localizationService = null)
    {
        _localizationService = localizationService;
        var h = Math.Abs(Id.GetHashCode());
        _wanderX = (h % 31) - 15;
        _wanderY = ((h / 31) % 27) - 13;
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
        if (_localizationService != null)
        {
            _localizationService.LanguageChanged -= OnLanguageChanged;
        }
    }

    /// <summary>猫头鹰在小镇画布上的左上角 X（= 场所中心 - 半个身位 + 漂移）。</summary>
    public double CanvasX => ZoneCenters[Zone].X - OwlSize / 2 + _wanderX;

    /// <summary>猫头鹰在小镇画布上的左上角 Y。</summary>
    public double CanvasY => ZoneCenters[Zone].Y - OwlSize / 2 + _wanderY;

    /// <summary>设置区域内漂移偏移并刷新位置绑定（配合 TransformOperationsTransition 产生平滑游走）。</summary>
    public void SetWander(double x, double y)
    {
        var nextCanvasX = ZoneCenters[Zone].X - OwlSize / 2 + x;
        BeginMotion(OwlMotion.Walking, nextCanvasX < CanvasX);
        _wanderX = x;
        _wanderY = y;
        OnPropertyChanged(nameof(CanvasX));
        OnPropertyChanged(nameof(CanvasY));
        OnPropertyChanged(nameof(OwlTransform));
    }

    // ===== 随机游走节拍 =====
    // 每只猫头鹰有自己的下次挪窝时间（1.2~3.5s 随机），互不同步；终态（完成/出错/取消）后静止。
    private DateTime _nextWanderAt = DateTime.MinValue;

    private bool ShouldWanderNow(DateTime now)
        => State is SubAgentState.Pending or SubAgentState.Running && now >= _nextWanderAt;

    private void ScheduleNextWander(DateTime now)
        => _nextWanderAt = now + TimeSpan.FromMilliseconds(1200 + _rng.NextDouble() * 2300);

    /// <summary>
    /// 为一组猫头鹰在各自所处场所内随机选取新的漂移目标，并保证同场所内两两间距 ≥ 半个身位
    /// （重叠不超过 50%）。由小镇视图高频节拍调用；每只猫头鹰只在自己的随机时刻到点才挪动，
    /// 终态的猫头鹰不再移动，但其当前位置仍参与避让。
    /// </summary>
    public static void RepositionWander(IReadOnlyList<SubAgentViewModel> owls)
    {
        const double threshold = OwlSize * 0.5; // 23px：重叠上限 50%
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
                    if (ok) break;
                }
                owl.SetWander(wx, wy);
                owl.ScheduleNextWander(now);
                placed.Add((px, py));
            }
        }
    }

    /// <summary>
    /// 供 RenderTransform 绑定的 translate 变换字符串（配合 TransformOperationsTransition 产生滑翔）。
    /// 用不变区域性格式化，避免部分语言把小数点写成逗号导致解析失败。
    /// </summary>
    public string OwlTransform =>
        $"translate({CanvasX.ToString(CultureInfo.InvariantCulture)}px, {CanvasY.ToString(CultureInfo.InvariantCulture)}px)";

    /// <summary>当前展示帧；图片在 ViewModel 中缓存一次，避免每次动画节拍重新解码资源。</summary>
    public Bitmap OwlFrame => FramesFor(_owlMotion)[_owlFrameIndex];

    /// <summary>向左移动时复用右向精灵图，避免维护一套镜像资源。</summary>
    public double OwlSpriteScaleX => _owlFacingLeft ? -1 : 1;

    /// <summary>新精灵图没有旧 owl.webp 的大透明边距；按动作微调显示盒，防止飞翼被头像环裁掉。</summary>
    public double OwlSpriteSize => _owlMotion switch
    {
        OwlMotion.Flying => 64,
        OwlMotion.Walking => 58,
        OwlMotion.Landing => 56,
        _ => 58
    };

    /// <summary>由小镇视图的短周期计时器调用，推进显示帧并在移动完成后切回待机。</summary>
    public void AdvanceSprite(DateTime now)
    {
        if (_owlMotion != OwlMotion.Idle && now >= _motionEndsAt)
        {
            BeginMotion(_owlMotion == OwlMotion.Flying && Zone == SubAgentZone.Perch
                ? OwlMotion.Landing
                : OwlMotion.Idle, now: now);
            return;
        }

        var frameDuration = _owlMotion switch
        {
            OwlMotion.Idle => 300,
            OwlMotion.Walking => 140,
            OwlMotion.Flying => 120,
            OwlMotion.Landing => 90,
            _ => 300
        };
        var nextFrame = (int)((now - _motionStartedAt).TotalMilliseconds / frameDuration)
            % FramesFor(_owlMotion).Length;
        if (nextFrame != _owlFrameIndex)
        {
            _owlFrameIndex = nextFrame;
            OnPropertyChanged(nameof(OwlFrame));
        }
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
        OnPropertyChanged(nameof(CanvasX));
        OnPropertyChanged(nameof(CanvasY));
        OnPropertyChanged(nameof(OwlTransform));
        RefreshStatusLabel();
    }

    partial void OnCurrentActionChanged(string value) => RefreshStatusLabel();

    partial void OnResultSummaryChanged(string value) => RefreshStatusLabel();

    partial void OnErrorMessageChanged(string value) => RefreshStatusLabel();

    // ===== 最小停留（min-dwell）=====
    // 快工具会让 Zone 频繁跳变导致闪烁；这里节流：每个场所至少停留 MinDwellSeconds，
    // 不足则延后切换、只保留最新目标。Runner 一律用 RequestZone 而非直接写 Zone。
    private const double MinDwellSeconds = 1.2;
    private DateTime _lastZoneAppliedAt = DateTime.MinValue;
    private SubAgentZone _pendingZone;
    private DispatcherTimer? _dwellTimer;

    /// <summary>请求切换到某场所（须在 UI 线程调用）；受最小停留节流。</summary>
    public void RequestZone(SubAgentZone zone)
    {
        var now = DateTime.UtcNow;
        var elapsed = (now - _lastZoneAppliedAt).TotalSeconds;
        if (elapsed >= MinDwellSeconds)
        {
            _dwellTimer?.Stop();
            ApplyZone(zone, now);
            return;
        }

        // 尚在停留期内：记下最新目标，等停留满后再应用。
        _pendingZone = zone;
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
            ApplyZone(_pendingZone, DateTime.UtcNow);
        };
        return timer;
    }

    private void ApplyZone(SubAgentZone zone, DateTime now)
    {
        _lastZoneAppliedAt = now;
        if (Zone != zone)
        {
            var nextCanvasX = ZoneCenters[zone].X - OwlSize / 2 + _wanderX;
            BeginMotion(OwlMotion.Flying, nextCanvasX < CanvasX, now);
        }
        Zone = zone; // 相等时 SetProperty 自动不触发；不同则 OnZoneChanged 刷新位置
    }

    private void BeginMotion(OwlMotion motion, bool? facingLeft = null, DateTime? now = null)
    {
        var startedAt = now ?? DateTime.UtcNow;
        _owlMotion = motion;
        _motionStartedAt = startedAt;
        _motionEndsAt = motion switch
        {
            OwlMotion.Walking or OwlMotion.Flying => startedAt.AddMilliseconds(TravelDurationMilliseconds),
            OwlMotion.Landing => startedAt.AddMilliseconds(360),
            _ => DateTime.MaxValue
        };
        _owlFrameIndex = 0;
        if (facingLeft.HasValue && _owlFacingLeft != facingLeft.Value)
        {
            _owlFacingLeft = facingLeft.Value;
            OnPropertyChanged(nameof(OwlSpriteScaleX));
        }
        OnPropertyChanged(nameof(OwlFrame));
        OnPropertyChanged(nameof(OwlSpriteSize));
    }

    private static Bitmap[] FramesFor(OwlMotion motion) => motion switch
    {
        OwlMotion.Walking => WalkingFrames,
        OwlMotion.Flying => FlyingFrames,
        OwlMotion.Landing => LandingFrames,
        _ => IdleFrames
    };

    private static Bitmap[] LoadFrames(string prefix, int count)
    {
        var frames = new Bitmap[count];
        for (var i = 0; i < count; i++)
        {
            using var stream = AssetLoader.Open(new Uri($"avares://Athena.UI/Assets/SubAgents/{prefix}-{i + 1:D2}.png"));
            frames[i] = new Bitmap(stream);
        }
        return frames;
    }

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
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsDone));
        OnPropertyChanged(nameof(IsError));
        OnPropertyChanged(nameof(IsCancelled));
        RefreshStatusLabel();
    }
}
