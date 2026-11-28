using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Athena.UI.Services.ComputerUse;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Serilog;

namespace Athena.UI.Controls;

internal sealed class DesktopCursorDisplay : IDesktopCursorDisplay
{
    private CursorWindow? _window;
    private bool _unavailable;

    public async Task ShowAsync(ComputerUseFrame frame, ComputerUseCursor cursor)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_unavailable) return;
            try
            {
                _window ??= new CursorWindow();
                _window.Present(frame, cursor);
            }
            catch (Exception ex)
            {
                _unavailable = true;
                _window?.Close();
                _window = null;
                Log.Warning(ex, "Could not display the desktop Computer Use cursor");
            }
        });
    }

    public async Task<bool> HideForCaptureAsync() =>
        await Dispatcher.UIThread.InvokeAsync(() => _window?.HideCursor() == true);

    public async Task CloseAsync() => await Dispatcher.UIThread.InvokeAsync(() =>
    {
        _window?.Close();
        _window = null;
        _unavailable = false;
    });

    internal static bool MatchesPortalMonitor(PixelRect logical, PixelRect pixels)
    {
        // Mutter gives the portal logical monitor coordinates. XWayland maps the whole
        // layout into root pixels with one scale, including on fractional-scale monitors.
        if (logical.Width <= 0 || logical.Height <= 0) return false;
        double factor = (double)pixels.Width / logical.Width;
        return Math.Abs(logical.X * factor - pixels.X) <= 2 && Math.Abs(logical.Y * factor - pixels.Y) <= 2
            && Math.Abs(logical.Height * factor - pixels.Height) <= 2;
    }

    internal sealed class CursorWindow : Window
    {
        private readonly CursorGlyph _glyph = new();
        private readonly DispatcherTimer _timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        private readonly bool _desktopWide;
        private PixelRect? _targetRegion;
        private double _targetScale = 1;
        private bool _configured;

        internal CursorWindow(bool desktopWide = true)
        {
            _desktopWide = desktopWide;
            Title = "Athena Computer Use Cursor";
            Width = 164;
            Height = 86;
            CanResize = CanMinimize = CanMaximize = false;
            ShowActivated = Focusable = IsHitTestVisible = false;
            Topmost = desktopWide;
            WindowStartupLocation = WindowStartupLocation.Manual;
            WindowDecorations = WindowDecorations.None;
            Background = TransparencyBackgroundFallback = Brushes.Transparent;
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
            Content = _glyph;
            _timer.Tick += (_, _) => DrawPosition();
            Closed += (_, _) => _timer.Stop();
        }

        internal void Present(ComputerUseFrame frame, ComputerUseCursor cursor)
        {
            var screens = Screens.All;
            var screen = screens.Count == 1 ? screens[0]
                : frame.FromPortal ? Match(screens, frame.PortalMonitor) : Screens.Primary;
            if (screen is null) { HideCursor(); return; }
            var scale = OperatingSystem.IsMacOS() ? 1d : screen.Scaling;
            PresentInRegion(screen.Bounds, scale, cursor, owner: null);
        }

        internal void PresentInRegion(PixelRect screenRegion, double coordinateScale, ComputerUseCursor cursor, Window? owner = null)
        {
            if (screenRegion.Width <= 0 || screenRegion.Height <= 0)
            {
                HideCursor();
                return;
            }
            _targetRegion = screenRegion;
            _targetScale = coordinateScale > 0 ? coordinateScale : 1d;
            if (!_configured)
            {
                // Configure native hit testing before the very first map, including on XWayland.
                DesktopCursorPlatform.MakeClickThrough(this, _desktopWide);
                ShowInTaskbar = false;
                _configured = true;
            }
            _glyph.CursorState = cursor;
            DrawPosition();
            if (!IsVisible)
            {
                if (!_desktopWide && owner is not null) Show(owner);
                else Show();
            }
            if (cursor.IsAnimating) _timer.Start();
        }

        private static Screen? Match(IReadOnlyList<Screen> screens, PixelRect? logical)
        {
            if (logical is not { } area) return null;
            Screen? match = null;
            foreach (var screen in screens)
            {
                if (!MatchesPortalMonitor(area, screen.Bounds)) continue;
                if (match != null) return null;
                match = screen;
            }
            return match;
        }

        internal bool HideCursor()
        {
            _timer.Stop();
            if (!IsVisible) return false;
            Hide();
            return true;
        }

        private void DrawPosition()
        {
            if (_targetRegion is not { } bounds || _glyph.CursorState is not { } cursor) return;
            var (nx, ny) = cursor.Position;
            var scale = _targetScale;
            var x = bounds.X + Math.Clamp((int)Math.Round(nx * bounds.Width), 0, Math.Max(0, bounds.Width - 1));
            var y = bounds.Y + Math.Clamp((int)Math.Round(ny * bounds.Height), 0, Math.Max(0, bounds.Height - 1));
            var left = Math.Clamp(x - (int)(28 * scale), bounds.X, Math.Max(bounds.X, bounds.Right - (int)Math.Ceiling(Width * scale)));
            var top = Math.Clamp(y - (int)(28 * scale), bounds.Y, Math.Max(bounds.Y, bounds.Bottom - (int)Math.Ceiling(Height * scale)));
            var origin = new PixelPoint(left, top);
            if (Position != origin) Position = origin;
            _glyph.Hotspot = new((x - left) / scale, (y - top) / scale);
            _glyph.InvalidateVisual();
            if (!cursor.IsAnimating) _timer.Stop();
        }
    }

    private sealed class CursorGlyph : Control
    {
        internal ComputerUseCursor? CursorState;
        internal Point Hotspot;
        public override void Render(DrawingContext context)
        {
            if (CursorState is { } cursor)
                ComputerUseCursorDrawing.Draw(context, Hotspot, new Rect(Bounds.Size), cursor);
        }
    }
}

/// <summary>
/// Owned, click-through floating cursor overlay bound directly to the Athena host window (never globally
/// topmost) so it stays strictly above the embedded CEF child view yet beneath any unrelated third-party
/// app covering Athena, without polluting CDP page screenshots or mutating page DOM.
/// </summary>
internal sealed class BrowserCursorOverlay : IDisposable
{
    private DesktopCursorDisplay.CursorWindow? _window;
    private bool _unavailable;

    public void Show(Window owner, PixelRect screenRegion, double coordinateScale, ComputerUseCursor cursor)
    {
        if (_unavailable || CefNativeBridge.IsHeadlessRuntime) return;
        try
        {
            _window ??= new DesktopCursorDisplay.CursorWindow(desktopWide: false);
            _window.PresentInRegion(screenRegion, coordinateScale, cursor, owner);
        }
        catch (Exception ex)
        {
            _unavailable = true;
            _window?.Close();
            _window = null;
            Log.Warning(ex, "Could not display the embedded browser Computer Use cursor overlay");
        }
    }

    public void Hide() => _window?.HideCursor();

    public void Dispose()
    {
        _window?.Close();
        _window = null;
    }
}
