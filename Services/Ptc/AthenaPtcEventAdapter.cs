using System.Threading;
using System.Threading.Tasks;
using Athena.UI.Models;
using Ptc.Core;

namespace Athena.UI.Services.Ptc;

/// <summary>The UI callback receives metadata on a separate channel, never fake tool messages.</summary>
public sealed class AthenaPtcEventAdapter(AthenaPtcInvocation invocation) : IPtcEventSink
{
    public ValueTask OnEventAsync(PtcEvent item, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        invocation.Progress?.Invoke(new PtcProgress(item.RunId, item.ParentToolCallId, invocation.ConversationId,
            item.Sequence, item.Kind, item.CallId, item.ToolName, item.ErrorCode));
        // A missing progress callback is intentional for service-only consumers; records still persist.
        return ValueTask.CompletedTask;
    }
}
