using Athena.UI.Services.ComputerUse;
using Athena.UI.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using System;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Views;

[SuppressMessage("Design", "CA1001",
    Justification = "The frame reader owns and disposes its lease. The view cancels it when hidden or detached.")]
public sealed partial class EmbeddedBrowserView : UserControl
{
    public static readonly StyledProperty<bool> PreviewEnabledProperty =
        AvaloniaProperty.Register<EmbeddedBrowserView, bool>(nameof(PreviewEnabled));

    private EmbeddedBrowserViewModel? _model;
    private Window? _owner;
    private ChromeBrowserPage? _page;
    private ChromeDevTools? _viewer;
    private CancellationTokenSource? _viewLifetime;
    private BrowserPreview? _preview;
    private WriteableBitmap? _bitmap;
    private BrowserViewportSize? _preferredViewport;
    private BrowserViewportSize? _renderedViewport;
    private bool _attached;
    private bool _pressed;
    private int _clickCount;
    private string _button = "left";
    private int _buttons;
    private bool _moving;
    private Point _lastPoint;
    private long _lastMove;
    private readonly SemaphoreSlim _manualInput = new(1, 1);

    private bool CanShowPreview => _attached && PreviewEnabled
        && _owner is not { WindowState: WindowState.Minimized } && _owner is not { IsVisible: false };

    public bool PreviewEnabled
    {
        get => GetValue(PreviewEnabledProperty);
        set => SetValue(PreviewEnabledProperty, value);
    }

    public EmbeddedBrowserView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => BindModel();
        BrowserSurface.SizeChanged += (_, _) => UpdatePreferredViewport();
        BrowserSurface.PointerPressed += OnPointerPressed;
        BrowserSurface.PointerReleased += OnPointerReleased;
        BrowserSurface.PointerMoved += OnPointerMoved;
        BrowserSurface.PointerWheelChanged += OnWheel;
        BrowserSurface.AddHandler(InputElement.KeyDownEvent, OnBrowserKeyDown, RoutingStrategies.Tunnel);
        BrowserSurface.AddHandler(InputElement.TextInputEvent, OnBrowserTextInput, RoutingStrategies.Tunnel);
        AddressBox.GotFocus += (_, _) => _model?.SetAddressEditing(true);
        AddressBox.LostFocus += (_, _) => _model?.SetAddressEditing(false);
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
        }
        BindModel();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        if (_owner is not null)
        {
            _owner.PropertyChanged -= OnOwnerPropertyChanged;
            _owner.ScalingChanged -= OnOwnerScalingChanged;
        }
        _owner = null;
        BindModel();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PreviewEnabledProperty)
            Reconnect();
    }

    private void OnOwnerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty || e.Property == IsVisibleProperty)
            Reconnect();
    }

    private void OnOwnerScalingChanged(object? sender, EventArgs e) => UpdatePreferredViewport();

    private void BindModel()
    {
        if (_model is not null)
            _model.PropertyChanged -= OnModelPropertyChanged;
        _model = _attached ? DataContext as EmbeddedBrowserViewModel : null;
        if (_model is not null)
            _model.PropertyChanged += OnModelPropertyChanged;
        Reconnect();
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EmbeddedBrowserViewModel.CurrentPage))
            Reconnect();
        else if (e.PropertyName == nameof(EmbeddedBrowserViewModel.CanTakeOver))
            RefreshCursor();
    }

    private void Reconnect()
    {
        UpdatePreferredViewport();
        var page = CanShowPreview ? _model?.CurrentPage : null;
        if (ReferenceEquals(page, _page) && (page is null || _viewLifetime is not null)) return;

        if (_page is not null) _page.CursorChanged -= OnCursorChanged;
        var previous = _viewLifetime;
        _viewLifetime = null;
        _viewer = null;
        _preview = null;
        _page = page;
        previous?.Cancel();
        _pressed = false;
        InvalidateFrame();
        ClearBitmap();
        if (page is not null)
        {
            page.CursorChanged += OnCursorChanged;
            _viewLifetime = new CancellationTokenSource();
            _preview = page.Driver.OpenPreview();
            _ = ReadFramesAsync(page, _preview, _viewLifetime);
        }
    }

    private void UpdatePreferredViewport()
    {
        if (!CanShowPreview || BrowserSurface.Bounds is not { Width: > 0, Height: > 0 } bounds)
        {
            _preferredViewport = null;
            return;
        }
        var size = new BrowserViewportSize(
            Math.Max(1, (int)Math.Round(bounds.Width)), Math.Max(1, (int)Math.Round(bounds.Height)), _owner?.RenderScaling ?? 1);
        _model?.SetPreferredViewport(size);
        if (size == _preferredViewport) return;
        _preferredViewport = size;
        _preview?.Signal();
    }

    private async Task ReadFramesAsync(ChromeBrowserPage page, BrowserPreview preview, CancellationTokenSource lease)
    {
        using (lease)
        {
            var token = lease.Token;
            var viewer = page.Driver;
            try
            {
                if (!ReferenceEquals(lease, _viewLifetime)) return;
                _viewer = viewer;
                BrowserViewportSize? appliedSize = null;
                var revision = 0;
                var repair = false;
                while (!token.IsCancellationRequested)
                {
                    if (!_pressed && _preferredViewport is { } size
                        && (size != appliedSize || repair || page.ViewportRevision != revision))
                    {
                        InvalidateFrame();
                        try
                        {
                            if (size != appliedSize || page.ViewportSize != size)
                                await page.ResizeAsync(size, token);
                            else if (repair && await page.RepairPreviewViewportAsync(size, token))
                                ClearBitmap();
                            token.ThrowIfCancellationRequested();
                            revision = page.ViewportRevision;
                            await preview.ConfigureAsync(revision, token);
                            appliedSize = size;
                            repair = false;
                        }
                        catch (Exception exception) when (viewer.IsOpen && !token.IsCancellationRequested
                            && exception is ChromeDevToolsException { IsNavigation: true } or OperationCanceledException)
                        {
                            await Task.Delay(200, token);
                            continue;
                        }
                        if (_preferredViewport != size && !_pressed) continue;
                    }

                    using (var frame = preview.TakeFrame())
                    {
                        if (frame is null)
                        {
                            await preview.WaitAsync(token);
                            continue;
                        }
                        token.ThrowIfCancellationRequested();
                        if (!repair && appliedSize is { } frameSize && _preferredViewport == frameSize
                            && frame.Revision == revision && page.ViewportRevision == revision)
                        {
                            if (frame.Width == frameSize.PixelWidth && frame.Height == frameSize.PixelHeight)
                            {
                                await Dispatcher.UIThread.InvokeAsync(() =>
                                {
                                    if (_preferredViewport == frameSize && page.ViewportRevision == revision)
                                        PresentFrame(frame, frameSize);
                                }, DispatcherPriority.Background, token);
                            }
                            else
                            {
                                repair = true;
                                InvalidateFrame();
                            }
                        }
                    }
                    await preview.AcknowledgeAsync(token);
                    if (repair && !_pressed) await Task.Delay(80, token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception exception)
            {
                if (ReferenceEquals(lease, _viewLifetime))
                    _model?.ReportPreviewError(exception.Message);
            }
            finally
            {
                if (ReferenceEquals(lease, _viewLifetime))
                {
                    _viewLifetime = null;
                    _viewer = null;
                    _preview = null;
                    InvalidateFrame();
                }
                preview.Dispose();
                if (viewer.IsOpen)
                {
                    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    try { await preview.StopAsync(stop.Token); }
                    catch (Exception exception) when (exception is ChromeDevToolsException or OperationCanceledException or IOException) { }
                }
            }
        }
    }

    private void PresentFrame(BrowserPreviewFrame frame, BrowserViewportSize size)
    {
        var pixels = new PixelSize(frame.Width, frame.Height);
        if (_bitmap is null || _bitmap.PixelSize != pixels)
        {
            ClearBitmap();
            // Skia samples the source rectangle in pixels. The Image fits the full raster to the panel.
            _bitmap = new WriteableBitmap(pixels, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            BrowserImage.Source = _bitmap;
        }
        using (var buffer = _bitmap.Lock())
        {
            var rowLength = frame.Width * 4;
            if (buffer.RowBytes == rowLength)
                Marshal.Copy(frame.Pixels, 0, buffer.Address, rowLength * frame.Height);
            else
                for (var y = 0; y < frame.Height; y++)
                    Marshal.Copy(frame.Pixels, y * rowLength, buffer.Address + y * buffer.RowBytes, rowLength);
        }
        BrowserImage.InvalidateVisual();
        var changed = _renderedViewport != size;
        _renderedViewport = size;
        if (changed) RefreshCursor();
    }

    private void OnCursorChanged(ChromeBrowserPage page) => Dispatcher.UIThread.Post(() =>
    {
        if (ReferenceEquals(_page, page)) RefreshCursor();
    });

    private void RefreshCursor() => BrowserCursor.ShowBrowserCursor(
        _model?.CanTakeOver == true && _renderedViewport is { } size && _page?.ViewportSize == size ? _page.Cursor : null);

    private void InvalidateFrame()
    {
        _renderedViewport = null;
        BrowserCursor.ShowBrowserCursor(null);
    }

    private void ClearBitmap()
    {
        BrowserImage.Source = null;
        _bitmap?.Dispose();
        _bitmap = null;
    }

    private bool TryMapPoint(PointerEventArgs e, out Point result)
    {
        result = default;
        var bounds = BrowserImage.Bounds.Size;
        if (_model is not { HasPage: true, HasPrompt: false } || _viewer is null || _renderedViewport is not { } size
            || _preferredViewport != size || bounds.Width <= 0 || bounds.Height <= 0)
            return false;

        var point = e.GetPosition(BrowserImage);
        if (!new Rect(bounds).Contains(point))
            return false;
        result = new Point(point.X * size.Width / bounds.Width, point.Y * size.Height / bounds.Height);
        _lastPoint = result;
        return true;
    }

    private async void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var button = e.GetCurrentPoint(BrowserSurface).Properties.PointerUpdateKind switch
        {
            PointerUpdateKind.LeftButtonPressed => ("left", 1),
            PointerUpdateKind.RightButtonPressed => ("right", 2),
            PointerUpdateKind.MiddleButtonPressed => ("middle", 4),
            _ => ("", 0)
        };
        if (_pressed || button.Item2 == 0 || !TryMapPoint(e, out var point))
            return;
        var position = e.GetPosition(BrowserSurface);
        BrowserImeInput.Margin = new Thickness(position.X, position.Y, 0, 0);
        BrowserImeInput.Focus();
        e.Pointer.Capture(BrowserSurface);
        _pressed = true;
        (_button, _buttons) = button;
        _clickCount = e.ClickCount;
        var clicks = _clickCount;
        e.Handled = true;
        await SendInputAsync((viewer, token) => viewer.CallAsync("Input.dispatchMouseEvent", new
        {
            type = "mousePressed", x = point.X, y = point.Y, button = button.Item1, buttons = button.Item2,
            clickCount = clicks, modifiers = ModifierMask(e.KeyModifiers)
        }, token));
    }

    private async void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_pressed) return;
        _buttons = 0;
        e.Pointer.Capture(null);
        var preview = _preview;
        var point = TryMapPoint(e, out var current) ? current : _lastPoint;
        var button = _button;
        var clicks = _clickCount;
        e.Handled = true;
        try
        {
            await SendInputAsync((viewer, token) => viewer.CallAsync("Input.dispatchMouseEvent", new
            {
                type = "mouseReleased", x = point.X, y = point.Y, button, buttons = 0, clickCount = clicks,
                modifiers = ModifierMask(e.KeyModifiers)
            }, token));
        }
        finally
        {
            if (ReferenceEquals(preview, _preview))
            {
                _pressed = false;
                preview?.Signal();
            }
        }
    }

    private async void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        var now = Environment.TickCount64;
        if (_model?.CanHoverPage != true || _moving || now - _lastMove < 16 || !TryMapPoint(e, out var point)) return;
        _lastMove = now;
        _moving = true;
        try
        {
            await SendInputAsync((viewer, token) => viewer.CallAsync("Input.dispatchMouseEvent", new
            {
                type = "mouseMoved", x = point.X, y = point.Y, buttons = _pressed ? _buttons : 0,
                modifiers = ModifierMask(e.KeyModifiers)
            }, token));
        }
        finally
        {
            _moving = false;
        }
    }

    private async void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (!TryMapPoint(e, out var point)) return;
        e.Handled = true;
        await SendInputAsync((viewer, token) => viewer.CallAsync("Input.dispatchMouseEvent", new
        {
            type = "mouseWheel", x = point.X, y = point.Y, deltaX = -e.Delta.X * 120, deltaY = -e.Delta.Y * 120,
            modifiers = ModifierMask(e.KeyModifiers)
        }, token));
    }

    private async void OnBrowserKeyDown(object? sender, KeyEventArgs e)
    {
        if (_model is not { HasPage: true, HasPrompt: false } || _viewer is null)
            return;
        if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0 && e.Key == Key.V)
        {
            e.Handled = true;
            await SendInputAsync(async (viewer, token) =>
            {
                if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard && await clipboard.TryGetTextAsync() is { } text)
                    await viewer.CallAsync("Input.insertText", new { text }, token);
            });
            return;
        }
        var key = e.Key switch
        {
            Key.Enter => "Return",
            Key.Tab => "Tab",
            Key.Escape => "Escape",
            Key.Left => "Left",
            Key.Right => "Right",
            Key.Up => "Up",
            Key.Down => "Down",
            Key.Home => "Home",
            Key.End => "End",
            Key.PageUp => "Page_Up",
            Key.PageDown => "Page_Down",
            Key.Back => "BackSpace",
            Key.Delete => "Delete",
            _ => null
        };
        if (key is null) return;
        e.Handled = true;
        await SendInputAsync((viewer, token) => BrowserComputerUseSession.PressKeyAsync(viewer, key, token));
    }

    private async void OnBrowserTextInput(object? sender, TextInputEventArgs e)
    {
        if (_model is not { HasPage: true, HasPrompt: false } || _viewer is null || string.IsNullOrEmpty(e.Text)) return;
        e.Handled = true;
        var text = e.Text;
        await SendInputAsync((viewer, token) => BrowserComputerUseSession.TypeCommittedTextAsync(viewer, text, token));
    }

    private async Task SendInputAsync(Func<ChromeDevTools, CancellationToken, Task> send)
    {
        if (_model is not { HasPage: true, HasPrompt: false } model || _page is not { } page
            || _viewer is not { } viewer || _viewLifetime is not { } lease)
            return;
        var token = lease.Token;
        try
        {
            // Request ownership immediately. The first pointer/key event waits for any current AI input.
            await _manualInput.WaitAsync(token);
            try
            {
                if (ReferenceEquals(lease, _viewLifetime))
                    await model.WithManualInputAsync(page, () => send(viewer, token), token);
            }
            finally { _manualInput.Release(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (ReferenceEquals(lease, _viewLifetime))
                _model?.ReportPreviewError(exception.Message);
        }
    }

    private static int ModifierMask(KeyModifiers keys) =>
        ((keys & KeyModifiers.Alt) != 0 ? 1 : 0)
        | ((keys & KeyModifiers.Control) != 0 ? 2 : 0)
        | ((keys & KeyModifiers.Meta) != 0 ? 4 : 0)
        | ((keys & KeyModifiers.Shift) != 0 ? 8 : 0);
}
