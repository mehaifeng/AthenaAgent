using System;
using System.Globalization;
using Athena.UI.Services.ComputerUse;
using Avalonia;
using Avalonia.Media;

namespace Athena.UI.Controls;

internal static class ComputerUseCursorDrawing
{
    private static readonly StreamGeometry Pointer = StreamGeometry.Parse("M 0,0 L 4,24 L 10,17 L 18,19 Z");
    private static readonly IBrush PointerBrush = new SolidColorBrush(Color.Parse("#5EEAD4"));
    private static readonly Pen PointerOutline = new(new SolidColorBrush(Color.Parse("#132625")), 1.5);
    private static readonly IBrush LabelBackground = new SolidColorBrush(Color.Parse("#09242A"));
    private static readonly FormattedText AgentLabel = CreateLabel("Athena");
    private static readonly FormattedText TypingLabel = CreateLabel("Athena · Typing");

    internal static void Draw(DrawingContext context, Point position, Rect viewport, ComputerUseCursor cursor)
    {
        var pulse = cursor.Pulse;
        if (pulse < 1)
        {
            var ring = new Pen(new SolidColorBrush(Color.FromArgb((byte)(255 * (1 - pulse)), 94, 234, 212)), 2.5);
            context.DrawEllipse(null, ring, position, 5 + pulse * 20, 5 + pulse * 20);
        }
        using (context.PushTransform(Matrix.CreateTranslation(position.X, position.Y)))
            context.DrawGeometry(PointerBrush, PointerOutline, Pointer);

        var label = cursor.IsTyping ? TypingLabel : AgentLabel;
        var width = label.Width + 14;
        var height = label.Height + 8;
        var left = position.X + 21 + width <= viewport.Right ? position.X + 21 : position.X - width - 9;
        var top = position.Y + 21 + height <= viewport.Bottom ? position.Y + 21 : position.Y - height - 9;
        var bounds = new Rect(Math.Max(viewport.X, left), Math.Max(viewport.Y, top), width, height);
        context.DrawRectangle(LabelBackground, null, bounds, 6, 6);
        context.DrawText(label, bounds.TopLeft + new Vector(7, 4));
    }

    private static FormattedText CreateLabel(string text) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 12, Brushes.White);
}
