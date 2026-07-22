using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services;

/// <summary>同一工作区的所有写/删/移动严格串行，不同工作区可并行。</summary>
public sealed class WorkspaceOperationCoordinator
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);
    private readonly ConversationExecutionCoordinator? _conversationCoordinator;
    private readonly Interfaces.IConversationSessionAccessor? _sessionAccessor;

    public WorkspaceOperationCoordinator(
        ConversationExecutionCoordinator? conversationCoordinator = null,
        Interfaces.IConversationSessionAccessor? sessionAccessor = null)
    {
        _conversationCoordinator = conversationCoordinator;
        _sessionAccessor = sessionAccessor;
    }

    public async Task RunAsync(string workspaceId, Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default)
    {
        async Task ExecuteAsync()
        {
            var gate = _gates.GetOrAdd(workspaceId, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken);
            try
            {
                await operation(cancellationToken);
            }
            finally
            {
                gate.Release();
            }
        }

        if (_conversationCoordinator == null)
        {
            await ExecuteAsync();
            return;
        }
        await _conversationCoordinator.RunWithoutModelSlotAsync(
            _sessionAccessor?.CurrentConversationId,
            ExecuteAsync,
            cancellationToken);
    }
}
