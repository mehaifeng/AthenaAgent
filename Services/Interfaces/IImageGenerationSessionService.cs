using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Athena.UI.Models;

namespace Athena.UI.Services.Interfaces;

public interface IImageGenerationSessionService
{
    Task<ImageGenerationSessionRecord> GetOrCreateAsync(string conversationId, CancellationToken cancellationToken = default);

    Task<ImageGenerationSessionRecord?> LoadAsync(string conversationId, CancellationToken cancellationToken = default);

    Task<ImageGenerationSessionRecord?> CaptureAndPersistAsync(
        string conversationId,
        string? historyId,
        ImageGenerationSessionUpdate update,
        CancellationToken cancellationToken = default);

    Task<ImageGenerationTurnRecord?> GetActiveTurnAsync(string conversationId, CancellationToken cancellationToken = default);

    Task<ImageReferenceResolutionResult> ResolveReferenceTurnAsync(
        string conversationId,
        string referenceQuery,
        CancellationToken cancellationToken = default);

    Task<ImageGenerationSessionSnapshot?> CreateSnapshotAsync(string conversationId, CancellationToken cancellationToken = default);

    Task PersistSnapshotAsync(ImageGenerationSessionSnapshot snapshot, CancellationToken cancellationToken = default);

    Task<ImageGenerationSessionRecord?> ReconcileAsync(
        string conversationId,
        IReadOnlyCollection<string> survivingAttachmentIds,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(string conversationId, CancellationToken cancellationToken = default);
}
