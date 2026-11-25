using Athena.UI.Services.ComputerUse;
using Athena.UI.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using System;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
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
    private Bitmap? _bitmap;
    private int _width, _height;
    private bool _attached;
    private bool _pressed;
    private int _clickCount;
    private bool _moving;
    private Point _lastPoint;
    private long _lastMove;

    public bool PreviewEnabled
    {
        get => GetValue(PreviewEnabledProperty);
        set => SetValue(PreviewEnabledProperty, value);
    }

    public EmbeddedBrowserView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => BindModel();
        BrowserSurface.PointerPressed += OnPointerPressed;
        BrowserSurface.PointerReleased += OnPointerReleased;
        BrowserSurface.PointerMoved += OnPointerMoved;
        BrowserSurface.PointerWheelChanged += OnWheel;
        BrowserSurface.KeyDown += OnBrowserKeyDown;
        BrowserSurface.TextInput += OnBrowserTextInput;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        if (TopLevel.GetTopLevel(this) is Window owner)
        {
            _owner = owner;
            owner.PropertyChanged += OnOwnerPropertyChanged;
        }
        BindModel();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        if (_owner is not null)
            _owner.PropertyChanged -= OnOwnerPropertyChanged;
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
    }

    private void Reconnect()
    {
        var page = _attached && PreviewEnabled && _owner is not { WindowState: WindowState.Minimized }
            && _owner is not { IsVisible: false } ? _model?.CurrentPage : null;
        if (ReferenceEquals(page, _page)) return;

        var previous = _viewLifetime;
        _viewLifetime = null;
        _viewer = null;
        _page = page;
        previous?.Cancel();
        _pressed = false;
        _width = _height = 0;
        SetBitmap(null);
        if (page is not null && _model is { } model)
        {
            _viewLifetime = new CancellationTokenSource();
            _ = ReadFramesAsync(page, model.CanBrowse, _viewLifetime);
        }
    }

    private async Task ReadFramesAsync(ChromeBrowserPage page, bool canBrowse, CancellationTokenSource lease)
    {
        using (lease)
        using (var viewer = new ChromeDevTools())
        {
            var token = lease.Token;
            try
            {
                await viewer.ConnectAsync(page.Socket, token);
                if (!ReferenceEquals(lease, _viewLifetime)) return;
                _viewer = viewer;
                (int Width, int Height) appliedSize = default;
                while (!token.IsCancellationRequested)
                {
                    if (canBrowse && BrowserSurface.Bounds is { Width: > 0, Height: > 0 } bounds)
                    {
                        var size = (Math.Clamp((int)bounds.Width, 320, 1440), Math.Clamp((int)bounds.Height, 220, 1200));
                        if (size != appliedSize)
                        {
                            // The installed viewport belongs to the page's connection, even after this viewer is hidden.
                            using var resize = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                            await page.ResizeAsync(size.Item1, size.Item2, resize.Token);
                            appliedSize = size;
                        }
                    }

                    try
                    {
                        await CaptureFrameAsync(page, viewer, lease, token);
                    }
                    catch (ChromeDevToolsException exception) when (exception.IsNavigation)
                    {
                        // The new document will have a new execution context on the next frame.
                    }
                    await Task.Delay(80, token);
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
                }
            }
        }
    }

    private async Task CaptureFrameAsync(ChromeBrowserPage page, ChromeDevTools viewer,
        CancellationTokenSource lease, CancellationToken token)
    {
        var metrics = await viewer.CallAsync("Runtime.evaluate", new
        {
            expression = "({width: innerWidth, height: innerHeight, url: location.href})",
            returnByValue = true
        }, token);
        if (!metrics.GetProperty("result").TryGetProperty("value", out var viewport)) return;
        var capture = await viewer.CallAsync("Page.captureScreenshot",
            new { format = "jpeg", quality = 84, captureBeyondViewport = false }, token);
        if (!ReferenceEquals(lease, _viewLifetime)) return;
        using var bytes = new MemoryStream(capture.GetProperty("data").GetBytesFromBase64());
        _width = viewport.GetProperty("width").GetInt32();
        _height = viewport.GetProperty("height").GetInt32();
        SetBitmap(new Bitmap(bytes));
        if (Uri.TryCreate(viewport.GetProperty("url").GetString(), UriKind.Absolute, out var address)
            && EmbeddedBrowser.IsPageUri(address))
            _model?.UpdateAddress(page, address, AddressBox.IsFocused);
    }

    private void SetBitmap(Bitmap? bitmap)
    {
        BrowserImage.Source = bitmap;
        _bitmap?.Dispose();
        _bitmap = bitmap;
    }

    private bool TryMapPoint(PointerEventArgs e, out Point result)
    {
        result = default;
        var bounds = BrowserImage.Bounds.Size;
        if (_model?.CanBrowse != true || _viewer is null || _width <= 0 || _height <= 0)
            return false;

        var scale = Math.Min(bounds.Width / _width, bounds.Height / _height);
        if (scale <= 0) return false;
        var image = new Rect((bounds.Width - _width * scale) / 2, (bounds.Height - _height * scale) / 2,
            _width * scale, _height * scale);
        var point = e.GetPosition(BrowserImage);
        if (!image.Contains(point))
            return false;
        result = new Point((point.X - image.X) / scale, (point.Y - image.Y) / scale);
        _lastPoint = result;
        return true;
    }

    private async void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(BrowserSurface).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed
            || !TryMapPoint(e, out var point))
            return;
        BrowserSurface.Focus();
        e.Pointer.Capture(BrowserSurface);
        _pressed = true;
        _clickCount = e.ClickCount;
        e.Handled = true;
        await SendInputAsync((viewer, token) => viewer.CallAsync("Input.dispatchMouseEvent", new
        {
            type = "mousePressed", x = point.X, y = point.Y, button = "left", buttons = 1,
            clickCount = _clickCount, modifiers = ModifierMask(e.KeyModifiers)
        }, token));
    }

    private async void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_pressed) return;
        _pressed = false;
        e.Pointer.Capture(null);
        var point = TryMapPoint(e, out var current) ? current : _lastPoint;
        e.Handled = true;
        await SendInputAsync((viewer, token) => viewer.CallAsync("Input.dispatchMouseEvent", new
        {
            type = "mouseReleased", x = point.X, y = point.Y, button = "left", buttons = 0, clickCount = _clickCount,
            modifiers = ModifierMask(e.KeyModifiers)
        }, token));
    }

    private async void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        var now = Environment.TickCount64;
        if (_moving || now - _lastMove < 40 || !TryMapPoint(e, out var point)) return;
        _lastMove = now;
        _moving = true;
        try
        {
            await SendInputAsync((viewer, token) => viewer.CallAsync("Input.dispatchMouseEvent", new
            {
                type = "mouseMoved", x = point.X, y = point.Y, buttons = _pressed ? 1 : 0,
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
        if (_model?.CanBrowse != true || _viewer is null)
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
        if (_model?.CanBrowse != true || _viewer is null || string.IsNullOrEmpty(e.Text)) return;
        e.Handled = true;
        await SendInputAsync((viewer, token) => viewer.CallAsync("Input.insertText", new { text = e.Text }, token));
    }

    private async Task SendInputAsync(Func<ChromeDevTools, CancellationToken, Task> send)
    {
        if (_model?.CanBrowse != true || _viewer is not { } viewer || _viewLifetime is not { } lease)
            return;
        try
        {
            await send(viewer, lease.Token);
        }
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
