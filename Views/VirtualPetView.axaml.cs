using Athena.UI.Models;
using Athena.UI.Services;
using Athena.UI.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System;
using System.Diagnostics;
using System.Linq;

namespace Athena.UI.Views;

public partial class VirtualPetView : UserControl
{
    /// <summary>指针位移小于这个距离才算"点了一下"，否则是拖动。</summary>
    private const double ClickDistanceThreshold = 5;

    private readonly DispatcherTimer _spriteTimer;
    private readonly DispatcherTimer _motionTimer;
    private readonly VirtualPetMotionEngine _motion = new();
    private VirtualPetViewModel? _boundPet;
    private Point _dragStartPointer;
    private double _dragStartX;
    private double _dragStartY;
    private Point _lastPointer;
    private long _lastPointerAt;
    private long _lastMotionAt;
    private double _dragDistance;
    private Point _targetPanelOrigin;
    private double _targetPanelWidth;
    private double _targetPanelHeight;

    public VirtualPetView()
    {
        InitializeComponent();
        _spriteTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _spriteTimer.Tick += (_, _) => AdvanceSprite();
        _motionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _motionTimer.Tick += (_, _) => AdvanceMotion();

        AddHandler(PointerPressedEvent, OnPetPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerMovedEvent, OnPetPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnPetPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerCaptureLostEvent, OnPetPointerCaptureLost, RoutingStrategies.Bubble, handledEventsToo: true);

        // 把文件拖到宠物身上 = 让它叼进当前会话的待发送附件。
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnPetDragOver);
        AddHandler(DragDrop.DropEvent, OnPetDrop);

        DataContextChanged += (_, _) => BindPet(DataContext as VirtualPetViewModel);

        AttachedToVisualTree += (_, _) =>
        {
            _lastMotionAt = Stopwatch.GetTimestamp();
            BindPet(DataContext as VirtualPetViewModel);
            AdvanceSprite();
            UpdateMotionBounds();
            ApplyMotion();
            _spriteTimer.Start();
            _motionTimer.Start();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _spriteTimer.Stop();
            _motionTimer.Stop();
            if (DataContext is VirtualPetViewModel pet) pet.SetMotion(VirtualPetMotionState.None);
            BindPet(null);
        };
    }

    /// <summary>
    /// 运动引擎住在视图里，而"陪它玩"是 ViewModel 上的一条命令，
    /// 所以冲刺请求靠一个事件跨过来。挂/摘成对，否则换会话会留下悬挂订阅。
    /// </summary>
    private void BindPet(VirtualPetViewModel? pet)
    {
        if (ReferenceEquals(_boundPet, pet)) return;
        if (_boundPet != null) _boundPet.PlayBurstRequested -= OnPlayBurstRequested;
        _boundPet = pet;
        if (_boundPet != null) _boundPet.PlayBurstRequested += OnPlayBurstRequested;
    }

    private void OnPlayBurstRequested(object? sender, EventArgs e)
    {
        if (DataContext is not VirtualPetViewModel pet || !pet.IsEnabled || pet.ReducedMotion) return;
        UpdateMotionBounds();
        _motion.TriggerPlayBurst(pet.GravityEnabled);
    }

    private void AdvanceSprite()
    {
        if (DataContext is VirtualPetViewModel pet && pet.IsEnabled)
            pet.Advance(DateTime.UtcNow);
    }

    private void AdvanceMotion()
    {
        var now = Stopwatch.GetTimestamp();
        var elapsed = Stopwatch.GetElapsedTime(_lastMotionAt, now).TotalSeconds;
        _lastMotionAt = now;
        if (DataContext is not VirtualPetViewModel pet || !pet.IsEnabled)
        {
            _motion.StopAutomaticMotion();
            return;
        }

        UpdateMotionBounds();
        _motion.Tick(
            elapsed,
            pet.RoamingEnabled,
            pet.GravityEnabled,
            pet.CanAutoRoam);
        ApplyMotion();
        UpdateMotionAnimation(pet);
    }

    private void UpdateMotionBounds()
    {
        if (DataContext is not VirtualPetViewModel pet || Parent is not Grid shellGrid) return;
        var targetPanel = ResolveTargetPanel(shellGrid, pet.RoamArea);
        var targetOrigin = targetPanel?.TranslatePoint(new Point(0, 0), shellGrid);
        if (targetPanel is null || targetOrigin is null) return;

        _targetPanelOrigin = targetOrigin.Value;
        _targetPanelWidth = targetPanel.Bounds.Width;
        _targetPanelHeight = targetPanel.Bounds.Height;

        _motion.SetBounds(_targetPanelWidth, _targetPanelHeight, pet.ViewWidth, pet.ViewHeight, pet.RoamArea);
    }

    private static Control? ResolveTargetPanel(Grid shellGrid, VirtualPetRoamArea roamArea) => roamArea switch
    {
        VirtualPetRoamArea.SessionListBottom => shellGrid.FindControl<Border>("LeftPanel"),
        VirtualPetRoamArea.LogTerminalBottom => shellGrid.FindControl<TabControl>("UtilityTabControl")?.FindAncestorOfType<Border>(),
        _ => shellGrid.FindControl<MainConversationView>("MainConversationView")
    };

    private void ApplyMotion()
    {
        if (RenderTransform is not TranslateTransform transform) return;
        if (DataContext is not VirtualPetViewModel pet) return;
        var groundOffset = pet.GroundOffset;
        var petWidth = pet.ViewWidth;
        var petHeight = pet.ViewHeight;
        // Position the pet at the target panel's bottom-right corner, plus motion offsets.
        transform.X = Math.Round(_targetPanelOrigin.X + _targetPanelWidth - petWidth + _motion.X);
        transform.Y = Math.Round(_targetPanelOrigin.Y + _targetPanelHeight - petHeight + _motion.Y + groundOffset);
    }

    private void UpdateMotionAnimation(VirtualPetViewModel pet)
    {
        var motion = _motion.IsDragging
            ? VirtualPetMotionState.Dragging
            : pet.GravityEnabled && _motion.Y < -0.5
                ? VirtualPetMotionState.Falling
                : _motion.HorizontalVelocity < -8
                    ? VirtualPetMotionState.MovingLeft
                    : _motion.HorizontalVelocity > 8
                        ? VirtualPetMotionState.MovingRight
                        : VirtualPetMotionState.None;
        pet.SetMotion(motion);
    }

    private void OnPetPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not VirtualPetViewModel pet || !pet.IsEnabled) return;
        var point = e.GetCurrentPoint(this);
        // 右键留给上下文菜单：这里一旦 Handled，菜单就再也弹不出来了。
        if (!point.Properties.IsLeftButtonPressed || Parent is not Control host) return;

        UpdateMotionBounds();
        _motion.BeginDrag();
        _dragStartPointer = e.GetPosition(host);
        _lastPointer = _dragStartPointer;
        _dragStartX = _motion.X;
        _dragStartY = _motion.Y;
        _dragDistance = 0;
        _lastPointerAt = Stopwatch.GetTimestamp();
        e.Pointer.Capture(this);
        pet.SetMotion(VirtualPetMotionState.Dragging);
        e.Handled = true;
    }

    private void OnPetPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_motion.IsDragging || Parent is not Control host) return;
        var position = e.GetPosition(host);
        var now = Stopwatch.GetTimestamp();
        var elapsed = Stopwatch.GetElapsedTime(_lastPointerAt, now).TotalSeconds;
        _lastPointerAt = now;
        _dragDistance += Math.Sqrt(
            Math.Pow(position.X - _lastPointer.X, 2)
            + Math.Pow(position.Y - _lastPointer.Y, 2));
        _lastPointer = position;
        _motion.DragTo(
            _dragStartX + position.X - _dragStartPointer.X,
            _dragStartY + position.Y - _dragStartPointer.Y,
            elapsed);
        ApplyMotion();
        e.Handled = true;
    }

    private void OnPetPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_motion.IsDragging) return;
        var pet = DataContext as VirtualPetViewModel;
        _motion.EndDrag(pet?.GravityEnabled == true);
        e.Pointer.Capture(null);
        // 几乎没动过 = 点了一下。有需求时这一下就是回应需求，否则是摸摸头。
        if (_dragDistance < ClickDistanceThreshold) pet?.PokeCommand.Execute(null);
        if (pet is not null) UpdateMotionAnimation(pet);
        e.Handled = true;
    }

    private void OnPetPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (!_motion.IsDragging) return;
        var pet = DataContext as VirtualPetViewModel;
        _motion.EndDrag(pet?.GravityEnabled == true);
        if (pet is not null) UpdateMotionAnimation(pet);
    }

    private void OnPetDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = CanCatchFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnPetDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not VirtualPetViewModel pet || !CanCatchFiles(e))
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        var files = e.DataTransfer.TryGetFiles()?.OfType<IStorageFile>().ToList() ?? [];
        if (files.Count == 0)
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }

        e.DragEffects = DragDropEffects.Copy;
        e.Handled = true;
        pet.AcceptDroppedFiles(files);
    }

    private bool CanCatchFiles(DragEventArgs e)
        => DataContext is VirtualPetViewModel { CanCatchFiles: true }
           && e.DataTransfer.TryGetFiles()?.OfType<IStorageFile>().Any() == true;
}
