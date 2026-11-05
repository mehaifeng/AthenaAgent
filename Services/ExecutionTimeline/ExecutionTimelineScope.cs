using System;
using System.Threading;

namespace Athena.UI.Services.ExecutionTimeline;

/// <summary>
/// Host-only execution scope. The main loop enters it directly next to Registry execution; PTC
/// restores it explicitly at the actual child invocation rather than trusting async-iterator flow.
/// </summary>
public static class ExecutionTimelineScope
{
    public sealed record Context(ExecutionTimelineCollector Recorder, long ParentSpanId);
    private static readonly AsyncLocal<Context?> CurrentScope = new();
    public static Context? Current => CurrentScope.Value;

    public static IDisposable Enter(ExecutionTimelineCollector? recorder, long parentSpanId)
    {
        var previous = CurrentScope.Value;
        CurrentScope.Value = recorder is null || parentSpanId == 0 ? null : new Context(recorder, parentSpanId);
        return new Scope(previous);
    }

    private sealed class Scope(Context? previous) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            CurrentScope.Value = previous;
        }
    }
}
