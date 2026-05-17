using System.Threading;
using System.Threading.Tasks;
using Athena.UI.Models;

namespace Athena.UI.Services.Interfaces;

public interface IImageGenerationService
{
    bool IsConfigured { get; }

    Task<ImageGenerationResult> GenerateImageAsync(string prompt, CancellationToken cancellationToken = default);
}

public sealed class ImageGenerationResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? RevisedPrompt { get; init; }
    public ChatAttachment? Attachment { get; init; }
}
