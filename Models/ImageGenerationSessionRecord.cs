using System;
using System.Collections.Generic;

namespace Athena.UI.Models;

public enum ImageContinuityMode
{
    NewRoot,
    ContinueLast,
    ContinueMatched
}

public enum ImageContinuityStatus
{
    PixelContinuity,
    PromptOnlyFallback
}

public class ImageGenerationSessionRecord
{
    public string ConversationId { get; set; } = Guid.NewGuid().ToString("N");

    public string? HistoryId { get; set; }

    public string? ActiveLineageId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    public List<ImageGenerationTurnRecord> Turns { get; set; } = new();
}

public class ImageGenerationTurnRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string LineageId { get; set; } = Guid.NewGuid().ToString("N");

    public string? ParentTurnId { get; set; }

    public string Prompt { get; set; } = string.Empty;

    public string? RevisedPrompt { get; set; }

    public string AttachmentId { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public string StoredPath { get; set; } = string.Empty;

    public string MimeType { get; set; } = "image/png";

    public ImageContinuityMode ContinuityMode { get; set; }

    public ImageContinuityStatus ContinuityStatus { get; set; } = ImageContinuityStatus.PixelContinuity;

    public string? Warning { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;
}

public class ImageGenerationSessionSnapshot
{
    public string ConversationId { get; init; } = Guid.NewGuid().ToString("N");

    public string? HistoryId { get; init; }

    public string? ActiveLineageId { get; init; }

    public DateTime CreatedAt { get; init; } = DateTime.Now;

    public DateTime UpdatedAt { get; init; } = DateTime.Now;

    public List<ImageGenerationTurnRecord> Turns { get; init; } = new();
}

public sealed class ImageGenerationSessionUpdate
{
    public required ImageContinuityMode ContinuityMode { get; init; }

    public string? ReferenceTurnId { get; init; }

    public required string Prompt { get; init; }

    public string? RevisedPrompt { get; init; }

    public required ChatAttachment Attachment { get; init; }

    public bool UsedPixelContinuity { get; init; }

    public bool UsedPromptOnlyFallback { get; init; }

    public string? Warning { get; init; }
}

public enum ImageReferenceResolutionStatus
{
    Resolved,
    NoMatch,
    Ambiguous,
    NoImages,
    InvalidQuery,
    AssetMissing
}

public sealed class ImageReferenceTurnCandidate
{
    public required string TurnId { get; init; }

    public required string LineageId { get; init; }

    public required DateTime CreatedAt { get; init; }

    public string FileName { get; init; } = string.Empty;

    public string PromptPreview { get; init; } = string.Empty;

    public double MatchScore { get; init; }
}

public sealed class ImageReferenceResolutionResult
{
    public required ImageReferenceResolutionStatus Status { get; init; }

    public ImageGenerationTurnRecord? ResolvedTurn { get; init; }

    public IReadOnlyList<ImageReferenceTurnCandidate> Candidates { get; init; } = [];
}
