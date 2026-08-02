using Athena.UI.Models;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.Interfaces;

public interface IConversationCompressionCommitter
{
    Task<CompressionCommitResult> CommitCompressionAsync(
        CompressionTransition transition,
        CancellationToken cancellationToken = default);

    Task<CompressionCommitResult> CommitUndoCompressionAsync(
        CompressionUndoTransition transition,
        CancellationToken cancellationToken = default);
}
