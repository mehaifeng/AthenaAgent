using System.Threading;
using System.Threading.Tasks;
using Athena.UI.Models;
using System.Collections.Generic;

namespace Athena.UI.Services.Interfaces;

public interface IImageGenerationService
{
    bool IsConfigured { get; }

    Task<ImageGenerationResult> GenerateImageAsync(ImageGenerationRequest request, CancellationToken cancellationToken = default);
}

public sealed class ImageGenerationRequest
{
    public string Prompt { get; init; } = string.Empty;
    public IReadOnlyList<ImageGenerationReferenceImage> ReferenceImages { get; init; } = [];
}

public sealed class ImageGenerationReferenceImage
{
    public string StoredPath { get; init; } = string.Empty;
    public string? FileName { get; init; }
    public string MimeType { get; init; } = "image/png";
}

public sealed class ImageGenerationResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? RevisedPrompt { get; init; }
    public ChatAttachment? Attachment { get; init; }
    public bool UsedPixelContinuity { get; init; }
    public bool UsedPromptOnlyFallback { get; init; }
    public string? Warning { get; init; }
}
