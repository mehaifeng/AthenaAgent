using Athena.UI.Controls;
using Athena.UI.Services.ComputerUse;
using Athena.UI.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Serilog;
using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Views;

[SuppressMessage("Design", "CA1001",
    Justification = "The cursor overlay and headless resize token are disposed when the view detaches from the visual tree.")]
public sealed partial class EmbeddedBrowserView : UserControl
{
    private const double TabWheelStepDip = 48d;
    private const double TabDragThresholdDip = 6d;
    private const double TabSpacingDip = 4d;
    private const double TabEdgeAutoScrollZoneDip = 24d;
    private const double TabEdgeAutoScrollStepDip = 18d;

    public static readonly StyledProperty<bool> PreviewEnabledProperty =
        AvaloniaProperty.Register<EmbeddedBrowserView, bool>(nameof(PreviewEnabled));

    private readonly BrowserCursorOverlay _cursorOverlay = new();
    private EmbeddedBrowserViewModel? _model;
    private Window? _owner;
    private ChromeBrowserPage? _page;
    private CancellationTokenSource? _headlessResizeCts;
    private BrowserViewportSize? _lastHeadlessSize;
    private bool _attached;

    // Tab edge fade state
    private double _lastFadeViewportWidth = -1;

    // Intra-strip tab drag reorder state
    private BrowserTabItemViewModel? _dragTab;
    private Border? _dragTabBorder;
    private double _dragStartX;
    private bool _isDraggingTab;

    private bool CanShowSurface => _attached && PreviewEnabled && IsEffectivelyVisible
        && _model?.IsConversationTabSelected != true
        && _owner is not { WindowState: WindowState.Minimized } && _owner is not { IsVisible: false };

    public bool PreviewEnabled
    {
        get => GetValue(PreviewEnabledProperty);
        set => SetValue(PreviewEnabledProperty, value);
    }

    internal ComputerUseCursor? ActiveCursor { get; private set; }
    internal bool IsRightTabEdgeFaded { get; private set; }
    internal bool IsLeftTabEdgeFaded { get; private set; }

    public EmbeddedBrowserView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => BindModel();
        BrowserSurface.SizeChanged += (_, _) => SyncSurfaceAndCursor();
        CefHost.SizeChanged += (_, _) => SyncSurfaceAndCursor();
        CefHost.NativeHandleChanged += _ => SyncSurfaceAndCursor();
        AddressBox.GotFocus += (_, _) => _model?.SetAddressEditing(true);
        AddressBox.LostFocus += (_, _) => _model?.SetAddressEditing(false);

        TabStripScrollViewer.ScrollChanged += (_, _) => UpdateTabStripEdgeFade();
        TabStripScrollViewer.SizeChanged += (_, _) => UpdateTabStripEdgeFade();
        BrowserTabsControl.SizeChanged += (_, _) => UpdateTabStripEdgeFade();
        TabStripScrollViewer.AddHandler(PointerWheelChangedEvent, OnTabStripPointerWheelChanged, RoutingStrategies.Tunnel);

        BrowserTabsControl.AddHandler(PointerPressedEvent, OnTabsPointerPressed, RoutingStrategies.Tunnel);
        BrowserTabsControl.AddHandler(PointerMovedEvent, OnTabsPointerMoved, RoutingStrategies.Tunnel);
        BrowserTabsControl.AddHandler(PointerReleasedEvent, OnTabsPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        BrowserTabsControl.AddHandler(PointerCaptureLostEvent, OnTabsPointerCaptureLost, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        if (TopLevel.GetTopLevel(this) is Window owner)
        {
            _owner = owner;
            owner.PropertyChanged += OnOwnerPropertyChanged;
            owner.ScalingChanged += OnOwnerScalingChanged;
            owner.PositionChanged += OnOwnerPositionChanged;
        }
        BindModel();
        UpdateTabStripEdgeFade();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        ResetTabDrag();
        if (_owner is not null)
        {
            _owner.PropertyChanged -= OnOwnerPropertyChanged;
            _owner.ScalingChanged -= OnOwnerScalingChanged;
            _owner.PositionChanged -= OnOwnerPositionChanged;
        }
        _owner = null;
        _headlessResizeCts?.Cancel();
        _headlessResizeCts?.Dispose();
        _headlessResizeCts = null;
        _cursorOverlay.Dispose();
        if (_page is not null)
        {
            _page.CursorChanged -= OnCursorChanged;
            _page = null;
        }
        _model?.DetachSurface();
        BindModel();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PreviewEnabledProperty || change.Property == IsVisibleProperty)
            SyncSurfaceAndCursor();
    }

    private void OnOwnerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty || e.Property == IsVisibleProperty)
        {
            SyncSurfaceAndCursor();
            // Window maximize/restore/fullscreen transitions finalize native bounds on the next render pass.
            Dispatcher.UIThread.Post(SyncSurfaceAndCursor, DispatcherPriority.Render);
        }
    }

    private void OnOwnerScalingChanged(object? sender, EventArgs e) => SyncSurfaceAndCursor();

    private void OnOwnerPositionChanged(object? sender, EventArgs e) => RefreshCursor();

    private void BindModel()
    {
        if (_model is not null)
        {
            _model.PropertyChanged -= OnModelPropertyChanged;
            _model.Tabs.CollectionChanged -= OnTabsCollectionChanged;
            _model.SetClipboardHandler(null);
        }
        _model = _attached ? DataContext as EmbeddedBrowserViewModel : null;
        if (_model is not null)
        {
            _model.PropertyChanged += OnModelPropertyChanged;
            _model.Tabs.CollectionChanged += OnTabsCollectionChanged;
            _model.SetClipboardHandler(CopyTextToTopLevelClipboardAsync);
            CefHost.SetRuntimeDirectory(_model.RuntimeDirectory);
        }
        SyncSurfaceAndCursor();
        UpdateTabStripEdgeFade();
    }

    private Task CopyTextToTopLevelClipboardAsync(string text)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        return clipboard is not null ? clipboard.SetTextAsync(text) : Task.CompletedTask;
    }

    private void OnTabsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_attached) return;
            UpdateTabStripEdgeFade();
            if (!_isDraggingTab && e.Action == NotifyCollectionChangedAction.Add)
                BringActiveTabIntoView();
        }, DispatcherPriority.Render);
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(EmbeddedBrowserViewModel.CurrentPage) or nameof(EmbeddedBrowserViewModel.HasPage)
            or nameof(EmbeddedBrowserViewModel.HasLivePage) or nameof(EmbeddedBrowserViewModel.ActivePage))
        {
            SyncSurfaceAndCursor();
            if (!_isDraggingTab)
                Dispatcher.UIThread.Post(BringActiveTabIntoView, DispatcherPriority.Render);
        }
        else if (e.PropertyName is nameof(EmbeddedBrowserViewModel.IsFullView)
                 or nameof(EmbeddedBrowserViewModel.IsConversationTabSelected))
        {
            SyncSurfaceAndCursor();
            Dispatcher.UIThread.Post(() =>
            {
                if (!_attached) return;
                SyncSurfaceAndCursor();
                UpdateTabStripEdgeFade();
            }, DispatcherPriority.Render);
        }
        else if (e.PropertyName == nameof(EmbeddedBrowserViewModel.CanTakeOver))
        {
            RefreshCursor();
        }
    }

    private void OnTabStripPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        var extent = TabStripScrollViewer.Extent.Width;
        var viewport = TabStripScrollViewer.Viewport.Width;
        var maxOffset = Math.Max(0d, extent - viewport);
        if (maxOffset <= 0.5)
            return;

        var rawDelta = Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y) ? -e.Delta.X : -e.Delta.Y;
        if (Math.Abs(rawDelta) < 0.0001)
            return;

        var nextX = Math.Clamp(TabStripScrollViewer.Offset.X + rawDelta * TabWheelStepDip, 0d, maxOffset);
        TabStripScrollViewer.Offset = new Vector(nextX, 0d);
        e.Handled = true;
        UpdateTabStripEdgeFade();
    }

    private void UpdateTabStripEdgeFade()
    {
        var viewport = TabStripScrollViewer.Viewport.Width;
        var extent = TabStripScrollViewer.Extent.Width;
        var offset = TabStripScrollViewer.Offset.X;
        var hasOverflow = extent > viewport + 1d && viewport > 1d;
        var hasLeft = hasOverflow && offset > 1d;
        var hasRight = hasOverflow && (offset + viewport) < extent - 1d;

        if (hasLeft == IsLeftTabEdgeFaded && hasRight == IsRightTabEdgeFaded
            && (!hasOverflow || Math.Abs(viewport - _lastFadeViewportWidth) <= 2d))
            return;

        IsLeftTabEdgeFaded = hasLeft;
        IsRightTabEdgeFaded = hasRight;
        _lastFadeViewportWidth = viewport;

        if (!hasLeft && !hasRight)
        {
            TabStripScrollViewer.OpacityMask = null;
            return;
        }

        var fadeWidth = Math.Min(36d, viewport * 0.28);
        var fadeFraction = Math.Clamp(fadeWidth / Math.Max(1d, viewport), 0.04, 0.45);
        var opaque = Colors.Black;
        var transparent = Color.FromArgb(0, 0, 0, 0);
        var stops = new GradientStops();
        if (hasLeft)
        {
            stops.Add(new GradientStop(transparent, 0d));
            stops.Add(new GradientStop(opaque, fadeFraction));
        }
        else
        {
            stops.Add(new GradientStop(opaque, 0d));
        }

        if (hasRight)
        {
            stops.Add(new GradientStop(opaque, 1d - fadeFraction));
            stops.Add(new GradientStop(transparent, 1d));
        }
        else
        {
            stops.Add(new GradientStop(opaque, 1d));
        }

        TabStripScrollViewer.OpacityMask = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0d, 0.5, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1d, 0.5, RelativeUnit.Relative),
            GradientStops = stops
        };
    }

    private void BringActiveTabIntoView()
    {
        if (!_attached || _model is null || _model.IsConversationTabSelected || _model.ActivePage is null)
            return;
        var index = _model.Tabs.IndexOf(_model.ActivePage);
        if (index < 0) return;
        if (BrowserTabsControl.ContainerFromIndex(index) is not Control container) return;

        var viewport = TabStripScrollViewer.Viewport.Width;
        var extent = TabStripScrollViewer.Extent.Width;
        var maxOffset = Math.Max(0d, extent - viewport);
        if (maxOffset <= 0.5)
        {
            UpdateTabStripEdgeFade();
            return;
        }

        var left = container.Bounds.Left;
        var right = container.Bounds.Right;
        var current = TabStripScrollViewer.Offset.X;
        var next = current;
        if (left < current)
            next = left;
        else if (right > current + viewport)
            next = right - viewport;

        next = Math.Clamp(next, 0d, maxOffset);
        if (Math.Abs(next - current) > 0.5)
            TabStripScrollViewer.Offset = new Vector(next, 0d);
        UpdateTabStripEdgeFade();
    }

    private void OnTabsPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_model is null) return;
        if (!e.GetCurrentPoint(BrowserTabsControl).Properties.IsLeftButtonPressed)
            return;

        if (e.Source is not Visual sourceVisual) return;
        var tabBorder = FindTabBorderFromSource(sourceVisual, out var hitCloseButton);
        if (tabBorder is null || hitCloseButton || tabBorder.DataContext is not BrowserTabItemViewModel tab)
            return;

        _model.SelectTab(tab);
        _dragTab = tab;
        _dragTabBorder = tabBorder;
        _dragStartX = e.GetPosition(BrowserTabsControl).X;
        _isDraggingTab = false;
        e.Pointer.Capture(BrowserTabsControl);
    }

    private void OnTabsPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragTab is null || _model is null) return;
        if (!e.GetCurrentPoint(BrowserTabsControl).Properties.IsLeftButtonPressed)
        {
            ResetTabDrag();
            return;
        }

        var currentX = e.GetPosition(BrowserTabsControl).X;
        var deltaX = currentX - _dragStartX;
        if (!_isDraggingTab)
        {
            if (Math.Abs(deltaX) < TabDragThresholdDip) return;
            _isDraggingTab = true;
            if (_dragTabBorder is not null)
                _dragTabBorder.ZIndex = 10;
        }

        // Auto-scroll when dragging near left/right edge of the tab viewport
        var scrollPos = e.GetPosition(TabStripScrollViewer).X;
        var viewport = TabStripScrollViewer.Viewport.Width;
        var maxOffset = Math.Max(0d, TabStripScrollViewer.Extent.Width - viewport);
        if (maxOffset > 0.5)
        {
            if (scrollPos < TabEdgeAutoScrollZoneDip && TabStripScrollViewer.Offset.X > 0d)
            {
                var nextOffset = Math.Max(0d, TabStripScrollViewer.Offset.X - TabEdgeAutoScrollStepDip);
                TabStripScrollViewer.Offset = new Vector(nextOffset, 0d);
                UpdateTabStripEdgeFade();
            }
            else if (scrollPos > viewport - TabEdgeAutoScrollZoneDip && TabStripScrollViewer.Offset.X < maxOffset)
            {
                var nextOffset = Math.Min(maxOffset, TabStripScrollViewer.Offset.X + TabEdgeAutoScrollStepDip);
                TabStripScrollViewer.Offset = new Vector(nextOffset, 0d);
                UpdateTabStripEdgeFade();
            }
        }

        var currentIndex = _model.Tabs.IndexOf(_dragTab);
        if (currentIndex < 0)
        {
            ResetTabDrag();
            return;
        }

        // Handle single-step and fast-flick multi-tab swaps smoothly within one pointer move.
        while (BrowserTabsControl.ContainerFromIndex(currentIndex) is Control currentContainer)
        {
            var draggedCenter = currentContainer.Bounds.Center.X + deltaX;
            if (deltaX > 0 && currentIndex < _model.Tabs.Count - 1
                && BrowserTabsControl.ContainerFromIndex(currentIndex + 1) is Control nextContainer
                && draggedCenter > nextContainer.Bounds.Center.X)
            {
                var shift = nextContainer.Bounds.Width + TabSpacingDip;
                ClearDraggedBorderTransform();
                if (!_model.MoveTab(_dragTab.Id, currentIndex + 1)) break;
                _dragStartX += shift;
                deltaX = currentX - _dragStartX;
                currentIndex++;
                BrowserTabsControl.UpdateLayout();
                RebindDraggedBorder(_dragTab);
                continue;
            }
            if (deltaX < 0 && currentIndex > 0
                && BrowserTabsControl.ContainerFromIndex(currentIndex - 1) is Control prevContainer
                && draggedCenter < prevContainer.Bounds.Center.X)
            {
                var shift = prevContainer.Bounds.Width + TabSpacingDip;
                ClearDraggedBorderTransform();
                if (!_model.MoveTab(_dragTab.Id, currentIndex - 1)) break;
                _dragStartX -= shift;
                deltaX = currentX - _dragStartX;
                currentIndex--;
                BrowserTabsControl.UpdateLayout();
                RebindDraggedBorder(_dragTab);
                continue;
            }
            break;
        }

        if (_dragTabBorder is not null)
            _dragTabBorder.RenderTransform = new TranslateTransform(deltaX, 0d);
        e.Handled = true;
    }

    private void OnTabsPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragTab is null) return;
        if (Equals(e.Pointer.Captured, BrowserTabsControl))
            e.Pointer.Capture(null);
        ResetTabDrag();
    }

    private void OnTabsPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) => ResetTabDrag();

    private void ClearDraggedBorderTransform()
    {
        if (_dragTabBorder is null) return;
        _dragTabBorder.RenderTransform = null;
        _dragTabBorder.ZIndex = 0;
    }

    private void RebindDraggedBorder(BrowserTabItemViewModel tab)
    {
        if (_model is null) return;
        var index = _model.Tabs.IndexOf(tab);
        if (index < 0) return;
        if (BrowserTabsControl.ContainerFromIndex(index) is Control container)
        {
            _dragTabBorder = container.GetVisualDescendants()
                .OfType<Border>()
                .FirstOrDefault(b => b.Classes.Contains("browser-tab"));
            if (_dragTabBorder is not null)
                _dragTabBorder.ZIndex = 10;
        }
    }

    private void ResetTabDrag()
    {
        ClearDraggedBorderTransform();
        _dragTab = null;
        _dragTabBorder = null;
        _isDraggingTab = false;
    }

    private static Border? FindTabBorderFromSource(Visual source, out bool hitCloseButton)
    {
        hitCloseButton = false;
        for (Visual? current = source; current is not null; current = current.GetVisualParent())
        {
            if (current is Button button && button.Classes.Contains("browser-tab-close"))
                hitCloseButton = true;
            if (current is Border border && border.Classes.Contains("browser-tab"))
                return border;
        }
        return null;
    }

    private void SyncSurfaceAndCursor()
    {
        var visible = CanShowSurface;
        var page = visible && _model?.CurrentPage is { IsPlaceholder: false } live ? live : null;

        if (!ReferenceEquals(page, _page))
        {
            if (_page is not null) _page.CursorChanged -= OnCursorChanged;
            _page = page;
            _lastHeadlessSize = null;
            if (_page is not null) _page.CursorChanged += OnCursorChanged;
        }

        if (_model is not null)
        {
            var bounds = BrowserSurface.Bounds;
            var scale = _owner?.RenderScaling ?? 1d;
            if (visible && bounds.Width > 0 && bounds.Height > 0)
            {
                var size = new BrowserViewportSize(
                    Math.Max(1, (int)Math.Round(bounds.Width)),
                    Math.Max(1, (int)Math.Round(bounds.Height)),
                    scale);
                _model.UpdateSurface(CefHost.NativeHandle, size, visible: page is not null, displayedPage: page);
                if (CefNativeBridge.IsHeadlessRuntime && page is not null && size != _lastHeadlessSize)
                {
                    _lastHeadlessSize = size;
                    ScheduleHeadlessResize(page, size);
                }
            }
            else
            {
                _model.UpdateSurface(CefHost.NativeHandle, new BrowserViewportSize(800, 720, scale), visible: false, displayedPage: null);
            }
        }

        RefreshCursor();
    }

    private void ScheduleHeadlessResize(ChromeBrowserPage page, BrowserViewportSize size)
    {
        _headlessResizeCts?.Cancel();
        _headlessResizeCts?.Dispose();
        var cts = new CancellationTokenSource();
        _headlessResizeCts = cts;
        _ = ApplyHeadlessResizeAsync(page, size, cts);
    }

    private static async Task ApplyHeadlessResizeAsync(ChromeBrowserPage page, BrowserViewportSize size, CancellationTokenSource cts)
    {
        try
        {
            await page.ResizeAsync(size, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Superseded by a newer layout pass or view detach.
        }
        catch (Exception ex)
        {
            Log.Debug("Headless CEF viewport resize ended: {Error}", ex.Message);
        }
    }

    private void OnCursorChanged(ChromeBrowserPage page) => Dispatcher.UIThread.Post(() =>
    {
        if (ReferenceEquals(_page, page)) RefreshCursor();
    });

    private void RefreshCursor()
    {
        var cursor = CanShowSurface && _model?.CanTakeOver == true ? _page?.Cursor : null;
        ActiveCursor = cursor;
        if (cursor is null || _owner is null || BrowserSurface.Bounds is not { Width: > 0, Height: > 0 } bounds)
        {
            _cursorOverlay.Hide();
            return;
        }

        var topLeft = BrowserSurface.PointToScreen(new Point(0, 0));
        var bottomRight = BrowserSurface.PointToScreen(new Point(bounds.Width, bounds.Height));
        var region = new PixelRect(topLeft, bottomRight);
        var scale = OperatingSystem.IsMacOS() ? 1d : _owner.RenderScaling;
        _cursorOverlay.Show(_owner, region, scale, cursor);
    }
}
