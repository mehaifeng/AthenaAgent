using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.ComputerUse;

/// <summary>Coordinates page input ownership, and gives each model observation a cancellable generation.</summary>
internal sealed class BrowserTaskControl : IDisposable
{
    private readonly object _state = new();
    private readonly SemaphoreSlim _input = new(1, 1);
    private readonly CancellationToken _run;
    private readonly CancellationTokenSource _deadline;
    private readonly Action<string?> _paused;
    private readonly Action _resumed;
    private CancellationTokenSource _observation;
    private TaskCompletionSource? _resume;
    private long _activeSince;
    private TimeSpan _remaining;
    private int _generation;
    private bool _afterTakeover;
    private bool _disposed;

    public BrowserTaskControl(CancellationToken run, CancellationTokenSource deadline, TimeSpan limit, Action<string?> paused, Action resumed)
    {
        _run = run;
        _observation = CancellationTokenSource.CreateLinkedTokenSource(run);
        _deadline = deadline;
        _paused = paused;
        _resumed = resumed;
        _remaining = limit;
        _activeSince = Stopwatch.GetTimestamp();
        _deadline.CancelAfter(limit);
    }

    public bool IsPaused { get { lock (_state) return _resume != null; } }

    public void Pause(string? reason = null)
    {
        lock (_state)
        {
            if (_disposed || _resume != null || _run.IsCancellationRequested) return;
            _remaining -= Stopwatch.GetElapsedTime(_activeSince);
            if (_remaining <= TimeSpan.Zero) return;
            _deadline.CancelAfter(Timeout.InfiniteTimeSpan);
            _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _generation++;
            _afterTakeover = true;
            _observation.Cancel();
            _paused(reason);
        }
    }

    public async Task ResumeAsync(CancellationToken token)
    {
        await _input.WaitAsync(token).ConfigureAwait(false);
        try
        {
            lock (_state)
            {
                if (_disposed || _resume == null || _run.IsCancellationRequested) return;
                var resume = _resume;
                _observation.Dispose();
                _observation = CancellationTokenSource.CreateLinkedTokenSource(_run);
                _resume = null;
                _activeSince = Stopwatch.GetTimestamp();
                _deadline.CancelAfter(_remaining);
                resume.TrySetResult();
                _resumed();
            }
        }
        finally { _input.Release(); }
    }

    public async Task<(int Generation, CancellationToken Token, bool AfterTakeover)> NextObservationAsync(CancellationToken token)
    {
        while (true)
        {
            Task wait;
            lock (_state)
            {
                token.ThrowIfCancellationRequested();
                if (_resume == null) return (_generation, _observation.Token, _afterTakeover);
                wait = _resume.Task;
            }
            await wait.WaitAsync(token).ConfigureAwait(false);
        }
    }

    public async Task<InputLease?> BeginAgentInputAsync(int generation, CancellationToken token)
    {
        await _input.WaitAsync(token).ConfigureAwait(false);
        lock (_state)
        {
            if (!_disposed && _resume == null && _generation == generation) return new InputLease(_input);
            _input.Release();
            return null;
        }
    }

    public async Task WithHumanInputAsync(Func<Task> send, CancellationToken token, bool changesPage)
    {
        await _input.WaitAsync(token).ConfigureAwait(false);
        try { await send().ConfigureAwait(false); }
        finally
        {
            // A model may have observed the page while this input was in flight.
            if (changesPage) PageChanged();
            _input.Release();
        }
    }

    public void PageChanged()
    {
        lock (_state)
        {
            if (_disposed || _resume != null || _run.IsCancellationRequested) return;
            _generation++;
            _afterTakeover = false;
        }
    }

    public void Dispose()
    {
        lock (_state)
        {
            _disposed = true;
            _observation.Dispose();
            _resume?.TrySetCanceled();
        }
    }

    internal sealed class InputLease(SemaphoreSlim input) : IDisposable
    {
        public void Dispose() => input.Release();
    }
}
