using System;
using System.Buffers;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
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
    private static readonly HttpClient SharedHttpClient = new();

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

    public async Task<ImageGenerationResult> GenerateImageAsync(ImageGenerationRequest request, CancellationToken cancellationToken = default)
    {
        var prompt = request.Prompt;
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
            var baseUrl = GetEffectiveBaseUrl(config);
            var clientOptions = OpenAiClientOptionsFactory.Create(baseUrl, config.Timeout);

            var referenceImages = request.ReferenceImages ?? [];
            var preparedReferenceImages = PrepareReferenceImages(referenceImages);
            var client = new OpenAIClient(new ApiKeyCredential(apiKey), clientOptions).GetImageClient(model);

            _logger.Information(
                "Generating image via /images/generations. Model={Model}, ReferenceImages={ReferenceImageCount}, Backend={Backend}",
                model,
                preparedReferenceImages.Count,
                NormalizeBackendName(baseUrl, model));

            var generatedImage = await GenerateImageWithReferencesAsync(
                client,
                model,
                prompt,
                preparedReferenceImages,
                baseUrl,
                cancellationToken);

            var imageBytes = await ResolveImageBytesAsync(generatedImage, cancellationToken);
            if (imageBytes.Length == 0)
            {
                return new ImageGenerationResult
                {
                    Success = false,
                    Message = "Image generation returned no image content."
                };
            }

            var fileName = $"generated-{DateTime.Now:yyyyMMdd-HHmmss}.png";
            var attachment = await _attachmentStoreService.CreateGeneratedImageAsync(
                imageBytes,
                fileName,
                "image/png",
                cancellationToken);

            return new ImageGenerationResult
            {
                Success = true,
                Message = "Image generated successfully.",
                RevisedPrompt = generatedImage.RevisedPrompt,
                Attachment = attachment,
                UsedPixelContinuity = preparedReferenceImages.Count > 0,
                UsedPromptOnlyFallback = false,
                Warning = null
            };
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Image generation failed");
            return new ImageGenerationResult
            {
                Success = false,
                Message = FormatGenerationFailure(ex, request.ReferenceImages?.Count > 0)
            };
        }
    }

    // 图像生成使用独立凭据，不继承主对话模型；BaseUrl 留空时使用 OpenAI 默认端点。
    private static EffectiveModelConfig GetEffective(AppConfig config) =>
        new(
            string.Empty,
            ModelCredentialResolver.FirstNonEmpty(config.ImageGenerationBaseUrl, "https://api.openai.com/v1"),
            config.ImageGenerationApiKey,
            ModelCredentialResolver.FirstNonEmpty(config.ImageGenerationModel, "gpt-image-1"));

    private static string GetEffectiveApiKey(AppConfig config) => GetEffective(config).ApiKey;

    private static string GetEffectiveBaseUrl(AppConfig config) => GetEffective(config).BaseUrl;

    private static string GetEffectiveModel(AppConfig config) => GetEffective(config).Model;

    private async Task<GeneratedImage> GenerateImageWithReferencesAsync(
        ImageClient client,
        string model,
        string prompt,
        IReadOnlyList<PreparedReferenceImage> referenceImages,
        string? baseUrl,
        CancellationToken cancellationToken)
    {
#pragma warning disable OPENAI001
        using var content = BinaryContent.Create(BuildGenerationRequestPayload(model, prompt, referenceImages, baseUrl));
        var requestOptions = cancellationToken.CanBeCanceled
            ? new RequestOptions { CancellationToken = cancellationToken }
            : null;
        var result = await client.GenerateImagesAsync(content, requestOptions).ConfigureAwait(false);
        var images = (GeneratedImageCollection)result;
#pragma warning restore OPENAI001

        if (images.Count == 0)
        {
            throw new InvalidOperationException("Image generation returned no images.");
        }

        if (images.Count > 1)
        {
            _logger.Warning("Image generation returned {ImageCount} images for a single-image request; using the first image.", images.Count);
        }

        return images[0];
    }

    internal static BinaryData BuildGenerationRequestPayload(
        string model,
        string prompt,
        IReadOnlyList<PreparedReferenceImage> referenceImages,
        string? baseUrl)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("model", model);
            writer.WriteString("prompt", prompt);
            writer.WriteString("response_format", "b64_json");
            writer.WriteString("output_format", "png");
            writer.WriteString("size", "1024x1024");

            if (referenceImages.Count > 0)
            {
                writer.WritePropertyName("image");
                writer.WriteStartArray();
                foreach (var referenceImage in referenceImages)
                {
                    writer.WriteStringValue(referenceImage.DataUri);
                }
                writer.WriteEndArray();
            }

            WriteBackendSpecificFields(writer, baseUrl, model, referenceImages.Count > 0);

            writer.WriteEndObject();
        }

        return BinaryData.FromBytes(buffer.WrittenSpan.ToArray());
    }

    internal static IReadOnlyList<PreparedReferenceImage> PrepareReferenceImages(IReadOnlyList<ImageGenerationReferenceImage> referenceImages)
    {
        if (referenceImages.Count == 0)
        {
            return [];
        }

        var prepared = new List<PreparedReferenceImage>(referenceImages.Count);
        foreach (var referenceImage in referenceImages)
        {
            if (string.IsNullOrWhiteSpace(referenceImage.StoredPath))
            {
                throw new InvalidOperationException("Reference image path is required for continuity generation.");
            }

            if (!File.Exists(referenceImage.StoredPath))
            {
                throw new FileNotFoundException("Reference image file not found for continuity generation.", referenceImage.StoredPath);
            }

            var bytes = File.ReadAllBytes(referenceImage.StoredPath);
            if (bytes.Length == 0)
            {
                throw new InvalidOperationException($"Reference image is empty: {referenceImage.StoredPath}");
            }

            var mimeType = string.IsNullOrWhiteSpace(referenceImage.MimeType)
                ? GuessMimeType(referenceImage.FileName ?? referenceImage.StoredPath)
                : referenceImage.MimeType;
            var dataUri = $"data:{mimeType};base64,{Convert.ToBase64String(bytes)}";
            prepared.Add(new PreparedReferenceImage(
                referenceImage.StoredPath,
                referenceImage.FileName ?? Path.GetFileName(referenceImage.StoredPath),
                mimeType,
                bytes.LongLength,
                dataUri));
        }

        return prepared;
    }

    private static void WriteBackendSpecificFields(Utf8JsonWriter writer, string? baseUrl, string model, bool hasReferenceImages)
    {
        if (!hasReferenceImages)
        {
            return;
        }

        if (!LooksLikeDoubaoBackend(baseUrl, model))
        {
            return;
        }

        writer.WriteBoolean("watermark", false);
        writer.WriteString("sequential_image_generation", "disabled");
    }

    private async Task<byte[]> ResolveImageBytesAsync(GeneratedImage generatedImage, CancellationToken cancellationToken)
    {
        if (generatedImage.ImageBytes is { } imageBytes)
        {
            return imageBytes.ToArray();
        }

        if (generatedImage.ImageUri == null)
        {
            return [];
        }

        _logger.Information("Image generation returned URL output; downloading from {ImageUri}", generatedImage.ImageUri);
        using var response = await SharedHttpClient.GetAsync(generatedImage.ImageUri, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string FormatGenerationFailure(Exception exception, bool usedReferenceImages)
    {
        if (usedReferenceImages && LooksLikeUnsupportedReferenceGeneration(exception))
        {
            return "The current image model or gateway does not support reference-image continuity via /images/generations.";
        }

        return exception.Message;
    }

    private static bool LooksLikeUnsupportedReferenceGeneration(Exception exception)
    {
        if (exception is FileNotFoundException)
        {
            return false;
        }

        if (exception is ClientResultException clientException && clientException.Status is 400 or 404 or 415 or 422)
        {
            return true;
        }

        var message = exception.Message;
        return message.Contains("image", StringComparison.OrdinalIgnoreCase)
            || message.Contains("unsupported", StringComparison.OrdinalIgnoreCase)
            || message.Contains("schema", StringComparison.OrdinalIgnoreCase)
            || message.Contains("not found", StringComparison.OrdinalIgnoreCase)
            || message.Contains("invalid", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeDoubaoBackend(string? baseUrl, string model)
    {
        if (!string.IsNullOrWhiteSpace(baseUrl)
            && Uri.TryCreate(baseUrl, UriKind.Absolute, out var endpoint)
            && endpoint.Host.Contains("volces.com", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return model.Contains("doubao", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeBackendName(string? baseUrl, string model)
    {
        if (LooksLikeDoubaoBackend(baseUrl, model))
        {
            return "doubao-compatible";
        }

        return "openai-compatible";
    }

    private static string GuessMimeType(string fileName)
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

    internal sealed record PreparedReferenceImage(
        string StoredPath,
        string FileName,
        string MimeType,
        long SizeBytes,
        string DataUri);
}
