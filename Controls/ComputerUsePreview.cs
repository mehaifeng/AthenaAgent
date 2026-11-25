using Athena.UI.Services.ComputerUse;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using System;
using System.Diagnostics;
using System.IO;

namespace Athena.UI.Controls;

public sealed class ComputerUsePreview : Control, IDisposable
{
    private static readonly StreamGeometry Pointer = StreamGeometry.Parse("M 0,0 L 4,24 L 10,17 L 18,19 Z");
    private static readonly IBrush PointerBrush = new SolidColorBrush(Color.Parse("#5EEAD4"));
    private static readonly Pen PointerOutline = new(new SolidColorBrush(Color.Parse("#132625")), 1.5);
    private readonly DispatcherTimer _timer;
    private ComputerUseFrame? _frame;
    private Bitmap? _image;
    private Point? _from;
    private Point? _target;
    private long _motion;
    private long _click;

    public ComputerUsePreview()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) => Tick();
    }

    public void Show(ComputerUseProgress progress)
    {
        if (progress.Frame is { } frame && !ReferenceEquals(frame, _frame))
        {
            using var source = new MemoryStream(frame.Image);
            var image = new Bitmap(source);
            _image?.Dispose();
            _image = image;
            _frame = frame;
        }
        if (progress.Phase == ComputerUsePhase.Moving && progress.X is { } x && progress.Y is { } y)
        {
            _from = Position() ?? new Point(0.12, 0.16);
            _target = new Point(x, y);
            _motion = Stopwatch.GetTimestamp();
            _timer.Start();
        }
        if (progress.Phase == ComputerUsePhase.Clicking)
        {
            _click = Stopwatch.GetTimestamp();
            _timer.Start();
        }
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_image is null || Bounds.Width <= 0 || Bounds.Height <= 0)
            return;
        var viewport = new Rect(Bounds.Size);
        var scale = Math.Min(viewport.Width / _image.Size.Width, viewport.Height / _image.Size.Height);
        var content = viewport.CenterRect(new Rect(_image.Size * scale));
        context.DrawImage(_image, new Rect(_image.Size), content);
        if (Position() is not { } cursor)
            return;

        var position = new Point(content.X + cursor.X * content.Width, content.Y + cursor.Y * content.Height);
        var pulse = Elapsed(_click, 390);
        if (pulse < 1)
        {
            var ring = new Pen(new SolidColorBrush(Color.FromArgb((byte)(255 * (1 - pulse)), 94, 234, 212)), 2.5);
            context.DrawEllipse(null, ring, position, 5 + pulse * 20, 5 + pulse * 20);
        }
        using (context.PushTransform(Matrix.CreateTranslation(position.X, position.Y)))
            context.DrawGeometry(PointerBrush, PointerOutline, Pointer);
    }

    public void Reset()
    {
        _timer.Stop();
        _image?.Dispose();
        _image = null;
        _frame = null;
        _target = null;
        _click = 0;
        InvalidateVisual();
    }

    private Point? Position()
    {
        if (_target is not { } target || _from is not { } from)
            return null;
        var t = Elapsed(_motion, 265);
        var motion = t * t * (3 - 2 * t);
        return new Point(from.X + (target.X - from.X) * motion, from.Y + (target.Y - from.Y) * motion);
    }

    private void Tick()
    {
        InvalidateVisual();
        if (Elapsed(_motion, 265) >= 1 && Elapsed(_click, 390) >= 1)
            _timer.Stop();
    }

    private static double Elapsed(long timestamp, double duration) => timestamp == 0
        ? 1 : Math.Clamp(Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds / duration, 0, 1);

    public void Dispose() => Reset();
}
