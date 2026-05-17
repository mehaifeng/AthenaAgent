using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Athena.UI.Services.Interfaces;
using Serilog;

namespace Athena.UI.Services.Functions;

public class ImageGenerationFunctions
{
    private readonly IImageGenerationService _imageGenerationService;
    private readonly ILogger _logger;

    public ImageGenerationFunctions(IImageGenerationService imageGenerationService, ILogger logger)
    {
        _imageGenerationService = imageGenerationService;
        _logger = logger.ForContext<ImageGenerationFunctions>();
    }

    public async Task<FunctionResult> GenerateImageAsync(string prompt)
    {
        try
        {
            if (!_imageGenerationService.IsConfigured)
            {
                return FunctionResult.FailureResult("Image generation is not configured or is disabled.");
            }

            var result = await _imageGenerationService.GenerateImageAsync(prompt);
            if (!result.Success || result.Attachment == null)
            {
                return FunctionResult.FailureResult(result.Message);
            }

            var data = new
            {
                revisedPrompt = result.RevisedPrompt,
                fileName = result.Attachment.FileName,
                mimeType = result.Attachment.MimeType,
                width = result.Attachment.Width,
                height = result.Attachment.Height
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
}
