using Athena.UI.Services.Interfaces;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services;

/// <summary>跨会话的公平模型并发队列；同一会话的串行由 ChatTabViewModel 保证。</summary>
public sealed class ConversationExecutionCoordinator
{
    private readonly object _sync = new();
    private readonly Queue<TaskCompletionSource<Lease>> _waiters = new();
    private readonly IConfigService _configService;
    private readonly ConcurrentDictionary<string, Lease> _leases = new(StringComparer.Ordinal);
    private int _active;

    public ConversationExecutionCoordinator(IConfigService configService)
    {
        _configService = configService;
        _configService.ConfigChanged += (_, _) => Drain();
    }

    public Task<Lease> AcquireAsync(string conversationId, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_active < Limit && _waiters.Count == 0)
            {
                _active++;
                var lease = new Lease(this, conversationId);
                _leases[conversationId] = lease;
                return Task.FromResult(lease);
            }

            var waiter = new TaskCompletionSource<Lease>(TaskCreationOptions.RunContinuationsAsynchronously);
            waiter.Task.ContinueWith(task =>
            {
                if (task.Status == TaskStatus.RanToCompletion) _leases[conversationId] = task.Result;
            }, System.Threading.Tasks.TaskScheduler.Default);
            _pendingConversationIds[waiter] = conversationId;
            _waiters.Enqueue(waiter);
            cancellationToken.Register(() => waiter.TrySetCanceled(cancellationToken));
            return waiter.Task;
        }
    }

    private readonly ConcurrentDictionary<TaskCompletionSource<Lease>, string> _pendingConversationIds = new();

    public async Task<T> RunWithoutModelSlotAsync<T>(string? conversationId, Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(conversationId)
            || !_leases.TryGetValue(conversationId, out var lease)
            || !lease.Pause())
        {
            return await operation();
        }

        try
        {
            return await operation();
        }
        finally
        {
            await lease.ResumeAsync(cancellationToken);
        }
    }

    public async Task RunWithoutModelSlotAsync(string? conversationId, Func<Task> operation, CancellationToken cancellationToken)
        => await RunWithoutModelSlotAsync(conversationId, async () =>
        {
            await operation();
            return true;
        }, cancellationToken);

    private int Limit => Math.Clamp(_configService.Load().MainConversationMaxParallel, 1, 16);

    private void Release()
    {
        lock (_sync)
        {
            _active = Math.Max(0, _active - 1);
            DrainLocked();
        }
    }

    private void Drain()
    {
        lock (_sync)
        {
            DrainLocked();
        }
    }

    private void DrainLocked()
    {
        while (_active < Limit && _waiters.Count > 0)
        {
            var waiter = _waiters.Dequeue();
            if (waiter.Task.IsCompleted) continue;
            _active++;
            _pendingConversationIds.TryRemove(waiter, out var conversationId);
            if (!waiter.TrySetResult(new Lease(this, conversationId ?? Guid.NewGuid().ToString("N")))) _active--;
        }
    }

    public sealed class Lease : IDisposable
    {
        private ConversationExecutionCoordinator? _owner;
        private bool _slotHeld = true;
        private readonly string _conversationId;

        internal Lease(ConversationExecutionCoordinator owner, string conversationId)
        {
            _owner = owner;
            _conversationId = conversationId;
        }

        internal bool Pause()
        {
            lock (this)
            {
                if (_owner == null || !_slotHeld) return false;
                _slotHeld = false;
                _owner.Release();
                return true;
            }
        }

        internal async Task ResumeAsync(CancellationToken cancellationToken)
        {
            ConversationExecutionCoordinator? owner;
            lock (this)
            {
                if (_owner == null || _slotHeld) return;
                owner = _owner;
            }
            var temporary = await owner.AcquireAsync(_conversationId, cancellationToken);
            lock (temporary)
            {
                temporary._slotHeld = false;
                temporary._owner = null;
            }
            lock (this)
            {
                if (_owner != null) _slotHeld = true;
                else owner.Release();
            }
            owner._leases[_conversationId] = this;
        }

        public void Dispose()
        {
            ConversationExecutionCoordinator? owner;
            bool release;
            lock (this)
            {
                owner = _owner;
                if (owner == null) return;
                _owner = null;
                release = _slotHeld;
                _slotHeld = false;
            }
            owner._leases.TryRemove(new KeyValuePair<string, Lease>(_conversationId, this));
            if (release) owner.Release();
        }
    }
}
