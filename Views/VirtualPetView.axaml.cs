using Athena.UI.Services;
using Athena.UI.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using System;
using System.Diagnostics;

namespace Athena.UI.Views;

public partial class VirtualPetView : UserControl
{
    private readonly DispatcherTimer _spriteTimer;
    private readonly DispatcherTimer _motionTimer;
    private readonly VirtualPetMotionEngine _motion = new();
    private Point _dragStartPointer;
    private double _dragStartX;
    private double _dragStartY;
    private Point _lastPointer;
    private long _lastPointerAt;
    private long _lastMotionAt;
    private double _dragDistance;

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

        AttachedToVisualTree += (_, _) =>
        {
            _lastMotionAt = Stopwatch.GetTimestamp();
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
        };
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
        if (DataContext is not VirtualPetViewModel pet || Parent is not Control host) return;
        var availableHeight = host.Bounds.Height;
        if (host is Grid grid)
        {
            var row = Grid.GetRow(this);
            if (row >= 0 && row < grid.RowDefinitions.Count)
                availableHeight = grid.RowDefinitions[row].ActualHeight;
        }
        _motion.SetBounds(
            host.Bounds.Width,
            availableHeight,
            pet.ViewWidth,
            pet.ViewHeight,
            pet.RoamArea);
    }

    private void ApplyMotion()
    {
        if (RenderTransform is not TranslateTransform transform) return;
        var groundOffset = DataContext is VirtualPetViewModel pet ? pet.GroundOffset : 0;
        // Pixel-aligned compositor positions keep nearest-neighbour sprites stable
        // while the independent motion timer updates at 60 Hz. GroundOffset moves
        // the spritesheet's transparent bottom padding through the row boundary so
        // the visible feet land exactly on the input area's upper edge.
        transform.X = Math.Round(_motion.X);
        transform.Y = Math.Round(_motion.Y + groundOffset);
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
        if (_dragDistance < 5) pet?.WakeCommand.Execute(null);
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
}
