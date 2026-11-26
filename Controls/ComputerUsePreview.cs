using Athena.UI.Services.ComputerUse;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using System;
using System.Globalization;
using System.IO;

namespace Athena.UI.Controls;

public sealed class ComputerUsePreview : Control, IDisposable
{
    private static readonly StreamGeometry Pointer = StreamGeometry.Parse("M 0,0 L 4,24 L 10,17 L 18,19 Z");
    private static readonly IBrush PointerBrush = new SolidColorBrush(Color.Parse("#5EEAD4"));
    private static readonly Pen PointerOutline = new(new SolidColorBrush(Color.Parse("#132625")), 1.5);
    private static readonly IBrush LabelBackground = new SolidColorBrush(Color.Parse("#09242A"));
    private static readonly FormattedText AgentLabel = CreateLabel("Athena");
    private static readonly FormattedText TypingLabel = CreateLabel("Athena · Typing");
    private readonly DispatcherTimer _timer;
    private ComputerUseFrame? _frame;
    private Bitmap? _image;
    private ComputerUseCursor? _cursor;
    private bool _showLabel;

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
            SetCursor(ComputerUseCursor.Move(_cursor, x, y));
        if (progress.Phase == ComputerUsePhase.Clicking && _cursor is { } cursor)
            SetCursor(cursor.Click());
        InvalidateVisual();
    }

    internal void ShowBrowserCursor(ComputerUseCursor? cursor)
    {
        _showLabel = true;
        SetCursor(cursor);
    }

    private void SetCursor(ComputerUseCursor? cursor)
    {
        if (ReferenceEquals(_cursor, cursor))
            return;
        _cursor = cursor;
        if (cursor?.IsAnimating == true)
            _timer.Start();
        else
            _timer.Stop();
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width <= 0 || Bounds.Height <= 0)
            return;
        var content = new Rect(Bounds.Size);
        if (_image is { } image)
        {
            var scale = Math.Min(content.Width / image.Size.Width, content.Height / image.Size.Height);
            content = content.CenterRect(new Rect(image.Size * scale));
            context.DrawImage(image, new Rect(image.Size), content);
        }
        if (_cursor is not { } cursor)
            return;

        var (x, y) = cursor.Position;
        var position = new Point(content.X + x * content.Width, content.Y + y * content.Height);
        var pulse = cursor.Pulse;
        if (pulse < 1)
        {
            var ring = new Pen(new SolidColorBrush(Color.FromArgb((byte)(255 * (1 - pulse)), 94, 234, 212)), 2.5);
            context.DrawEllipse(null, ring, position, 5 + pulse * 20, 5 + pulse * 20);
        }
        using (context.PushTransform(Matrix.CreateTranslation(position.X, position.Y)))
            context.DrawGeometry(PointerBrush, PointerOutline, Pointer);

        if (_showLabel)
        {
            var label = cursor.IsTyping ? TypingLabel : AgentLabel;
            var bounds = new Rect(position.X + 21, position.Y + 21, label.Width + 14, label.Height + 8);
            context.DrawRectangle(LabelBackground, null, bounds, 6, 6);
            context.DrawText(label, bounds.TopLeft + new Vector(7, 4));
        }
    }

    public void Reset()
    {
        SetCursor(null);
        _image?.Dispose();
        _image = null;
        _frame = null;
        InvalidateVisual();
    }

    private void Tick()
    {
        InvalidateVisual();
        if (_cursor?.IsAnimating != true)
            _timer.Stop();
    }

    private static FormattedText CreateLabel(string text) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 12, Brushes.White);

    public void Dispose() => Reset();
}
