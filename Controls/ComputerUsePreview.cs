using Athena.UI.Services.ComputerUse;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using System;
using System.IO;

namespace Athena.UI.Controls;

public sealed class ComputerUsePreview : Control, IDisposable
{
    private readonly DispatcherTimer _timer;
    private ComputerUseFrame? _frame;
    private Bitmap? _image;
    private ComputerUseCursor? _cursor;

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
        if (progress.Frame?.IsDesktop == true)
            SetCursor(progress.DesktopCursor);
        else if (progress.Phase == ComputerUsePhase.Moving && progress.X is { } x && progress.Y is { } y)
            SetCursor(ComputerUseCursor.Move(_cursor, x, y));
        else if (progress.Phase == ComputerUsePhase.Clicking && _cursor is { } cursor)
            SetCursor(cursor.Click());
        else if (progress.Phase == ComputerUsePhase.Typing && _cursor is { } typing)
            SetCursor(typing with { IsTyping = true });
        if (progress.Phase is ComputerUsePhase.Completed or ComputerUsePhase.Stopped or ComputerUsePhase.Failed or ComputerUsePhase.TimedOut)
            SetCursor(null);
        InvalidateVisual();
    }

    internal void ShowBrowserCursor(ComputerUseCursor? cursor)
    {
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
        ComputerUseCursorDrawing.Draw(context, position, content, cursor);
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

    public void Dispose() => Reset();
}
