using System;
using System.Diagnostics;

namespace Athena.UI.Services.ComputerUse;

internal sealed record ComputerUseCursor(double FromX, double FromY, double X, double Y, long MoveStarted,
    long ClickStarted = 0, bool IsTyping = false)
{
    public const int MoveMilliseconds = 265;
    public double Pulse => Elapsed(ClickStarted, 390);
    public bool IsAnimating => Elapsed(MoveStarted, MoveMilliseconds) < 1 || Pulse < 1;

    public (double X, double Y) Position
    {
        get
        {
            var t = Elapsed(MoveStarted, MoveMilliseconds);
            var motion = t * t * (3 - 2 * t);
            return (FromX + (X - FromX) * motion, FromY + (Y - FromY) * motion);
        }
    }

    public static ComputerUseCursor Move(ComputerUseCursor? previous, double x, double y)
    {
        var (fromX, fromY) = previous?.Position ?? (0.12, 0.16);
        return new(fromX, fromY, x, y, Stopwatch.GetTimestamp(), previous?.ClickStarted ?? 0);
    }

    public ComputerUseCursor Click() => this with { ClickStarted = Stopwatch.GetTimestamp() };

    private static double Elapsed(long timestamp, int milliseconds) => timestamp == 0
        ? 1 : Math.Clamp(Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds / milliseconds, 0, 1);
}
