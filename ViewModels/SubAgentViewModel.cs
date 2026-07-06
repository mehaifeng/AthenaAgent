using Athena.UI.Models;
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

/// <summary>子代理过程日志的一条记录（助手文本 / 工具结果），供"查看过程"展示。</summary>
public sealed class SubAgentLogEntry
{
    public string Kind { get; init; } = string.Empty;   // "assistant" | "tool"
    public string Text { get; init; } = string.Empty;
    public DateTime Timestamp { get; } = DateTime.Now;
}

/// <summary>
/// 单只"猫头鹰"子代理的实时状态。UI 直接绑定本对象；编排器/Runner 在 UI 线程更新它。
/// </summary>
public partial class SubAgentViewModel : ObservableObject
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

    [RelayCommand]
    private void Cancel() => Cts?.Cancel();

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

    public SubAgentViewModel()
    {
        var h = Math.Abs(Id.GetHashCode());
        _wanderX = (h % 31) - 15;
        _wanderY = ((h / 31) % 27) - 13;
    }

    /// <summary>猫头鹰在小镇画布上的左上角 X（= 场所中心 - 半个身位 + 漂移）。</summary>
    public double CanvasX => ZoneCenters[Zone].X - OwlSize / 2 + _wanderX;

    /// <summary>猫头鹰在小镇画布上的左上角 Y。</summary>
    public double CanvasY => ZoneCenters[Zone].Y - OwlSize / 2 + _wanderY;

    /// <summary>设置区域内漂移偏移并刷新位置绑定（配合 TransformOperationsTransition 产生平滑游走）。</summary>
    public void SetWander(double x, double y)
    {
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

    public bool IsRunning => State == SubAgentState.Running;
    public bool IsDone => State == SubAgentState.Done;
    public bool IsError => State == SubAgentState.Error;
    public bool IsCancelled => State == SubAgentState.Cancelled;

    partial void OnZoneChanged(SubAgentZone value)
    {
        OnPropertyChanged(nameof(CanvasX));
        OnPropertyChanged(nameof(CanvasY));
        OnPropertyChanged(nameof(OwlTransform));
    }

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
        Zone = zone; // 相等时 SetProperty 自动不触发；不同则 OnZoneChanged 刷新位置
    }

    partial void OnStateChanged(SubAgentState value)
    {
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsDone));
        OnPropertyChanged(nameof(IsError));
        OnPropertyChanged(nameof(IsCancelled));
    }
}
