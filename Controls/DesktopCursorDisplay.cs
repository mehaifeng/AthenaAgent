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

    private sealed class CursorWindow : Window
    {
        private readonly CursorGlyph _glyph = new();
        private readonly DispatcherTimer _timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        private Screen? _screen;

        internal CursorWindow()
        {
            Title = "Athena Computer Use Cursor";
            Width = 164;
            Height = 86;
            CanResize = CanMinimize = CanMaximize = false;
            ShowActivated = Focusable = IsHitTestVisible = false;
            Topmost = true;
            WindowStartupLocation = WindowStartupLocation.Manual;
            WindowDecorations = WindowDecorations.None;
            Background = TransparencyBackgroundFallback = Brushes.Transparent;
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
            Content = _glyph;
            _timer.Tick += (_, _) => DrawPosition();
            Closed += (_, _) => _timer.Stop();
        }

        private bool _configured;
        internal void Present(ComputerUseFrame frame, ComputerUseCursor cursor)
        {
            var screens = Screens.All;
            _screen = screens.Count == 1 ? screens[0]
                : frame.FromPortal ? Match(screens, frame.PortalMonitor) : Screens.Primary;
            if (_screen is null) { HideCursor(); return; }
            if (!_configured)
            {
                // Configure native hit testing before the very first map, including on XWayland.
                DesktopCursorPlatform.MakeClickThrough(this);
                ShowInTaskbar = false;
                _configured = true;
            }
            _glyph.CursorState = cursor;
            DrawPosition();
            if (!IsVisible) Show();
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
            if (_screen is not { } screen || _glyph.CursorState is not { } cursor) return;
            var (nx, ny) = cursor.Position;
            var bounds = screen.Bounds;
            // Cocoa positions top-level windows in points even when the backing store is Retina.
            var scale = OperatingSystem.IsMacOS() ? 1d : screen.Scaling;
            var x = bounds.X + Math.Clamp((int)Math.Round(nx * bounds.Width), 0, bounds.Width - 1);
            var y = bounds.Y + Math.Clamp((int)Math.Round(ny * bounds.Height), 0, bounds.Height - 1);
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
