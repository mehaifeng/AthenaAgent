using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using Serilog;

namespace Athena.UI.Services.Functions;

public class ImageGenerationFunctions
{
    private readonly IImageGenerationService _imageGenerationService;
    private readonly IImageGenerationSessionService _imageSessionService;
    private readonly IConversationSessionAccessor _conversationSessionAccessor;
    private readonly ILogger _logger;

    public ImageGenerationFunctions(
        IImageGenerationService imageGenerationService,
        IImageGenerationSessionService imageSessionService,
        IConversationSessionAccessor conversationSessionAccessor,
        ILogger logger)
    {
        _imageGenerationService = imageGenerationService;
        _imageSessionService = imageSessionService;
        _conversationSessionAccessor = conversationSessionAccessor;
        _logger = logger.ForContext<ImageGenerationFunctions>();
    }

    public async Task<FunctionResult> GenerateImageAsync(string prompt, string continuityMode = "new_root", string? referenceQuery = null)
    {
        try
        {
            if (!_imageGenerationService.IsConfigured)
            {
                return FunctionResult.FailureResult("Image generation is not configured or is disabled.");
            }

            var conversationId = _conversationSessionAccessor.CurrentConversationId;
            if (string.IsNullOrWhiteSpace(conversationId))
            {
                return FunctionResult.FailureResult("Image generation is unavailable because no active conversation context was found.");
            }

            var mode = ParseContinuityMode(continuityMode);
            var referenceResolution = await ResolveReferenceTurnAsync(conversationId, mode, referenceQuery);
            if (!referenceResolution.Success)
            {
                return referenceResolution.Failure!;
            }

            var referenceTurn = referenceResolution.ReferenceTurn;
            var result = await _imageGenerationService.GenerateImageAsync(new ImageGenerationRequest
            {
                Prompt = prompt,
                ReferenceImages = referenceTurn == null
                    ? []
                    :
                    [
                        new ImageGenerationReferenceImage
                        {
                            StoredPath = referenceTurn.StoredPath,
                            FileName = referenceTurn.FileName,
                            MimeType = string.IsNullOrWhiteSpace(referenceTurn.MimeType)
                                ? GuessMimeType(referenceTurn.FileName)
                                : referenceTurn.MimeType
                        }
                    ]
            });
            if (!result.Success || result.Attachment == null)
            {
                return FunctionResult.FailureResult(result.Message);
            }

            await _imageSessionService.CaptureAndPersistAsync(
                conversationId,
                historyId: null,
                new ImageGenerationSessionUpdate
                {
                    ContinuityMode = mode,
                    ReferenceTurnId = mode == ImageContinuityMode.ContinueMatched ? referenceTurn?.Id : null,
                    Prompt = prompt,
                    RevisedPrompt = result.RevisedPrompt,
                    Attachment = result.Attachment,
                    UsedPixelContinuity = result.UsedPixelContinuity,
                    UsedPromptOnlyFallback = result.UsedPromptOnlyFallback,
                    Warning = result.Warning
                });

            var data = new
            {
                revisedPrompt = result.RevisedPrompt,
                fileName = result.Attachment.FileName,
                mimeType = result.Attachment.MimeType,
                width = result.Attachment.Width,
                height = result.Attachment.Height,
                continuityMode = continuityMode,
                referenceSelectionMode = referenceResolution.ReferenceSelectionMode,
                referenceQuery,
                resolvedTurnId = referenceTurn?.Id,
                resolvedLineageId = referenceTurn?.LineageId,
                resolvedTurnCreatedAt = referenceTurn?.CreatedAt,
                resolvedPromptPreview = BuildPromptPreview(referenceTurn),
                continuityStatus = result.UsedPixelContinuity ? "reference_image_generation" : "new_root_generation",
                warning = result.Warning
            };

            _logger.Information("Function: generated image {FileName}", result.Attachment.FileName);

            return FunctionResult.SuccessResult(
                result.Message,
                data,
                generatedAttachments: new List<Models.ChatAttachment> { result.Attachment });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "GenerateImageAsync failed");
            return FunctionResult.FailureResult($"Image generation failed: {ex.Message}");
        }
    }

    private static ImageContinuityMode ParseContinuityMode(string? continuityMode)
    {
        if (string.Equals(continuityMode, "continue_last", StringComparison.OrdinalIgnoreCase))
        {
            return ImageContinuityMode.ContinueLast;
        }

        if (string.Equals(continuityMode, "continue_match", StringComparison.OrdinalIgnoreCase))
        {
            return ImageContinuityMode.ContinueMatched;
        }

        return ImageContinuityMode.NewRoot;
    }

    private async Task<ReferenceTurnSelection> ResolveReferenceTurnAsync(string conversationId, ImageContinuityMode mode, string? referenceQuery)
    {
        if (mode == ImageContinuityMode.NewRoot)
        {
            return ReferenceTurnSelection.ForNewRoot();
        }

        if (mode == ImageContinuityMode.ContinueLast)
        {
            var activeTurn = await _imageSessionService.GetActiveTurnAsync(conversationId);
            if (activeTurn == null)
            {
                return ReferenceTurnSelection.ForFailure(
                    "No prior generated image is available in the current conversation. Ask the user whether to start a new image instead.",
                    new
                    {
                        code = "no_reference_images_available"
                    });
            }

            if (!File.Exists(activeTurn.StoredPath))
            {
                return ReferenceTurnSelection.ForFailure(
                    "The latest generated image is no longer available as a reference image in the current conversation.",
                    new
                    {
                        code = "reference_asset_missing"
                    });
            }

            return ReferenceTurnSelection.ForResolved(activeTurn, "latest");
        }

        if (string.IsNullOrWhiteSpace(referenceQuery))
        {
            return ReferenceTurnSelection.ForFailure(
                "referenceQuery is required when continuityMode is continue_match.",
                new
                {
                    code = "reference_query_required",
                    referenceQuery
                });
        }

        var resolution = await _imageSessionService.ResolveReferenceTurnAsync(conversationId, referenceQuery);
        return resolution.Status switch
        {
            ImageReferenceResolutionStatus.Resolved when resolution.ResolvedTurn != null => ReferenceTurnSelection.ForResolved(resolution.ResolvedTurn, "semantic_match"),
            ImageReferenceResolutionStatus.NoImages => ReferenceTurnSelection.ForFailure(
                "No reference images are available in the current conversation.",
                new
                {
                    code = "no_reference_images_available",
                    referenceQuery
                }),
            ImageReferenceResolutionStatus.InvalidQuery => ReferenceTurnSelection.ForFailure(
                "referenceQuery is required when continuityMode is continue_match.",
                new
                {
                    code = "reference_query_required",
                    referenceQuery
                }),
            ImageReferenceResolutionStatus.NoMatch => ReferenceTurnSelection.ForFailure(
                "No matching historical image was found in the current conversation for the provided referenceQuery.",
                new
                {
                    code = "reference_no_match",
                    referenceQuery
                }),
            ImageReferenceResolutionStatus.Ambiguous => ReferenceTurnSelection.ForFailure(
                "The referenceQuery matched multiple historical image lineages in the current conversation.",
                new
                {
                    code = "reference_ambiguous",
                    referenceQuery,
                    candidates = resolution.Candidates
                }),
            ImageReferenceResolutionStatus.AssetMissing => ReferenceTurnSelection.ForFailure(
                "Matching historical image records exist, but their reference image files are no longer available.",
                new
                {
                    code = "reference_asset_missing",
                    referenceQuery
                }),
            _ => ReferenceTurnSelection.ForFailure(
                "Unable to resolve the requested historical reference image.",
                new
                {
                    code = "reference_no_match",
                    referenceQuery
                })
        };
    }

    private static string? BuildPromptPreview(ImageGenerationTurnRecord? turn)
    {
        if (turn == null)
        {
            return null;
        }

        var preview = string.IsNullOrWhiteSpace(turn.RevisedPrompt) ? turn.Prompt : turn.RevisedPrompt;
        if (string.IsNullOrWhiteSpace(preview))
        {
            return null;
        }

        return preview.Length <= 120 ? preview : preview[..120];
    }

    private static string GuessMimeType(string? fileName)
    {
        var extension = Path.GetExtension(fileName);
        return extension?.ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            _ => "image/png"
        };
    }

    private sealed class ReferenceTurnSelection
    {
        public bool Success { get; private init; }

        public ImageGenerationTurnRecord? ReferenceTurn { get; private init; }

        public string? ReferenceSelectionMode { get; private init; }

        public FunctionResult? Failure { get; private init; }

        public static ReferenceTurnSelection ForNewRoot() => new()
        {
            Success = true
        };

        public static ReferenceTurnSelection ForResolved(ImageGenerationTurnRecord turn, string selectionMode) => new()
        {
            Success = true,
            ReferenceTurn = turn,
            ReferenceSelectionMode = selectionMode
        };

        public static ReferenceTurnSelection ForFailure(string message, object data) => new()
        {
            Success = false,
            Failure = FunctionResult.FailureResult(message, data)
        };
    }
}
