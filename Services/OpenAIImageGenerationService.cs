using System;
using System.ClientModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using OpenAI;
using OpenAI.Images;
using Serilog;

namespace Athena.UI.Services;

public class OpenAIImageGenerationService : IImageGenerationService
{
    private readonly IConfigService _configService;
    private readonly IAttachmentStoreService _attachmentStoreService;
    private readonly ILogger _logger;

    public OpenAIImageGenerationService(
        IConfigService configService,
        IAttachmentStoreService attachmentStoreService,
        ILogger logger)
    {
        _configService = configService;
        _attachmentStoreService = attachmentStoreService;
        _logger = logger.ForContext<OpenAIImageGenerationService>();
    }

    public bool IsConfigured
    {
        get
        {
            var config = _configService.Load();
            return config.ImageGenerationEnabled
                && !string.IsNullOrWhiteSpace(GetEffectiveApiKey(config))
                && !string.IsNullOrWhiteSpace(GetEffectiveModel(config));
        }
    }

    public async Task<ImageGenerationResult> GenerateImageAsync(string prompt, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return new ImageGenerationResult
            {
                Success = false,
                Message = "Image prompt is required."
            };
        }

        var config = _configService.Load();
        if (!config.ImageGenerationEnabled)
        {
            return new ImageGenerationResult
            {
                Success = false,
                Message = "Image generation is disabled in settings."
            };
        }

        var apiKey = GetEffectiveApiKey(config);
        var model = GetEffectiveModel(config);
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(model))
        {
            return new ImageGenerationResult
            {
                Success = false,
                Message = "Image generation is not configured."
            };
        }

        try
        {
            var options = new OpenAIClientOptions();
            var baseUrl = GetEffectiveBaseUrl(config);
            if (!string.IsNullOrWhiteSpace(baseUrl))
            {
                options.Endpoint = new Uri(baseUrl);
            }

            var client = new OpenAIClient(new ApiKeyCredential(apiKey), options).GetImageClient(model);
#pragma warning disable OPENAI001
            var imageOptions = new ImageGenerationOptions
            {
                ResponseFormat = GeneratedImageFormat.Bytes,
                OutputFileFormat = GeneratedImageFileFormat.Png,
                Size = GeneratedImageSize.W1024xH1024
            };
#pragma warning restore OPENAI001

            var image = await client.GenerateImageAsync(prompt, imageOptions, cancellationToken);
            if (image.Value?.ImageBytes is null)
            {
                return new ImageGenerationResult
                {
                    Success = false,
                    Message = "Image generation returned no bytes."
                };
            }

            var fileName = $"generated-{DateTime.Now:yyyyMMdd-HHmmss}.png";
            var attachment = await _attachmentStoreService.CreateGeneratedImageAsync(
                image.Value.ImageBytes.ToArray(),
                fileName,
                "image/png",
                cancellationToken);

            return new ImageGenerationResult
            {
                Success = true,
                Message = "Image generated successfully.",
                RevisedPrompt = image.Value.RevisedPrompt,
                Attachment = attachment
            };
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Image generation failed");
            return new ImageGenerationResult
            {
                Success = false,
                Message = ex.Message
            };
        }
    }

    private static string GetEffectiveApiKey(AppConfig config) =>
        string.IsNullOrWhiteSpace(config.ImageGenerationApiKey) ? config.ApiKey : config.ImageGenerationApiKey;

    private static string GetEffectiveBaseUrl(AppConfig config) =>
        string.IsNullOrWhiteSpace(config.ImageGenerationBaseUrl) ? config.BaseUrl : config.ImageGenerationBaseUrl;

    private static string GetEffectiveModel(AppConfig config) =>
        string.IsNullOrWhiteSpace(config.ImageGenerationModel) ? "gpt-image-1" : config.ImageGenerationModel;
}
