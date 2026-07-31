using System;
using System.Buffers;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
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
            var effective = GetEffective(config);
            return config.ImageGenerationEnabled
                && !string.IsNullOrWhiteSpace(effective.ApiKey)
                && !string.IsNullOrWhiteSpace(effective.Model);
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
        var effective = GetEffective(config);
        if (!config.ImageGenerationEnabled)
        {
            return new ImageGenerationResult
            {
                Success = false,
                Message = "Image generation is disabled in settings."
            };
        }

        var apiKey = effective.ApiKey;
        var model = effective.Model;
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
            var baseUrl = effective.BaseUrl;
            if (effective.Provider is "Fal" or "Krea" or "OpenRouter" or "OpenAICodex")
            {
                var specialBytes = await GenerateProviderSpecificAsync(
                    prompt,
                    request.ReferenceImages ?? [],
                    effective,
                    cancellationToken);
                var specialAttachment = await _attachmentStoreService.CreateGeneratedImageAsync(
                    specialBytes,
                    $"generated-{DateTime.Now:yyyyMMdd-HHmmss}.png",
                    "image/png",
                    cancellationToken);
                return new ImageGenerationResult
                {
                    Success = true,
                    Message = "Image generated successfully.",
                    Attachment = specialAttachment,
                    UsedPixelContinuity = request.ReferenceImages?.Count > 0
                };
            }

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

    // 图像生成引用统一供应商连接，不再保存重复 API Key。
    private static ImageModelConfig GetEffective(AppConfig config)
    {
        var settings = config.ImageProviderSettings.FirstOrDefault(
            item => item.ProviderId.Equals(config.ImageGenerationProvider, StringComparison.OrdinalIgnoreCase));
        var defaults = ExtensionProviderCatalog.ImageProviders.FirstOrDefault(
            item => item.Id.Equals(config.ImageGenerationProvider, StringComparison.OrdinalIgnoreCase));
        return new ImageModelConfig(
            config.ImageGenerationProvider,
            settings?.BaseUrl ?? defaults?.DefaultBaseUrl ?? string.Empty,
            settings?.ApiKey ?? string.Empty,
            string.IsNullOrWhiteSpace(settings?.Model)
                ? defaults?.DefaultModel ?? string.Empty
                : settings.Model,
            string.IsNullOrWhiteSpace(settings?.AspectRatio) ? "1:1" : settings.AspectRatio);
    }

    private readonly record struct ImageModelConfig(
        string Provider,
        string BaseUrl,
        string ApiKey,
        string Model,
        string AspectRatio);

    private async Task<byte[]> GenerateProviderSpecificAsync(
        string prompt,
        IReadOnlyList<ImageGenerationReferenceImage> references,
        ImageModelConfig config,
        CancellationToken cancellationToken)
    {
        if (config.Provider == "Krea")
            return await GenerateKreaAsync(prompt, config, cancellationToken);

        using var request = config.Provider switch
        {
            "Fal" => BuildJsonRequest(
                $"{config.BaseUrl.TrimEnd('/')}/{config.Model.TrimStart('/')}",
                config.ApiKey,
                new
                {
                    prompt,
                    image_size = AspectToFalSize(config.AspectRatio),
                    num_images = 1,
                    output_format = "png"
                },
                "Key"),
            "OpenRouter" => BuildOpenRouterRequest(prompt, references, config),
            "OpenAICodex" => BuildCodexRequest(prompt, references, config),
            _ => throw new NotSupportedException($"Unsupported image provider: {config.Provider}")
        };
        using var response = await SharedHttpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {body}");
        return await ExtractImageBytesAsync(body, cancellationToken);
    }

    private static HttpRequestMessage BuildJsonRequest(
        string url,
        string token,
        object payload,
        string scheme = "Bearer")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(payload)
        };
        request.Headers.Authorization = new(scheme, token);
        return request;
    }

    private static HttpRequestMessage BuildOpenRouterRequest(
        string prompt,
        IReadOnlyList<ImageGenerationReferenceImage> references,
        ImageModelConfig config)
    {
        var content = new List<object> { new { type = "text", text = prompt } };
        foreach (var image in PrepareReferenceImages(references))
            content.Add(new { type = "image_url", image_url = new { url = image.DataUri } });
        return BuildJsonRequest(
            $"{config.BaseUrl.TrimEnd('/')}/chat/completions",
            config.ApiKey,
            new
            {
                model = config.Model,
                modalities = new[] { "image", "text" },
                messages = new[] { new { role = "user", content } },
                image_config = new { aspect_ratio = config.AspectRatio }
            });
    }

    private static HttpRequestMessage BuildCodexRequest(
        string prompt,
        IReadOnlyList<ImageGenerationReferenceImage> references,
        ImageModelConfig config)
    {
        var content = new List<object> { new { type = "input_text", text = prompt } };
        foreach (var image in PrepareReferenceImages(references))
            content.Add(new { type = "input_image", image_url = image.DataUri });
        var size = config.AspectRatio switch
        {
            "16:9" or "4:3" => "1536x1024",
            "9:16" or "3:4" => "1024x1536",
            _ => "1024x1024"
        };
        return BuildJsonRequest(
            $"{config.BaseUrl.TrimEnd('/')}/codex/responses",
            config.ApiKey,
            new
            {
                model = string.IsNullOrWhiteSpace(config.Model) ? "gpt-5.6" : config.Model,
                store = false,
                instructions = "Use the image_generation tool to create the requested image and return its result.",
                input = new[] { new { type = "message", role = "user", content } },
                tools = new[]
                {
                    new
                    {
                        type = "image_generation",
                        model = "gpt-image-2",
                        size,
                        quality = "high",
                        output_format = "png",
                        background = "opaque",
                        partial_images = 1
                    }
                },
                stream = true
            });
    }

    private async Task<byte[]> GenerateKreaAsync(
        string prompt,
        ImageModelConfig config,
        CancellationToken cancellationToken)
    {
        var root = config.BaseUrl.TrimEnd('/');
        var path = config.Model.Contains("turbo", StringComparison.OrdinalIgnoreCase)
            ? "medium-turbo"
            : "medium";
        using var submit = BuildJsonRequest(
            $"{root}/generate/image/krea/krea-2/{path}",
            config.ApiKey,
            new
            {
                prompt,
                aspect_ratio = config.AspectRatio,
                resolution = "1K",
                creativity = "medium"
            });
        using var submitResponse = await SharedHttpClient.SendAsync(submit, cancellationToken);
        var submitBody = await submitResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!submitResponse.IsSuccessStatusCode)
            throw new HttpRequestException($"Krea submit HTTP {(int)submitResponse.StatusCode}: {submitBody}");
        using var submitJson = JsonDocument.Parse(submitBody);
        var jobId = submitJson.RootElement.GetProperty("job_id").GetString()
            ?? throw new InvalidOperationException("Krea response did not contain job_id.");

        var deadline = DateTime.UtcNow.AddMinutes(3);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            using var poll = new HttpRequestMessage(HttpMethod.Get, $"{root}/jobs/{jobId}");
            poll.Headers.Authorization = new("Bearer", config.ApiKey);
            using var pollResponse = await SharedHttpClient.SendAsync(poll, cancellationToken);
            var body = await pollResponse.Content.ReadAsStringAsync(cancellationToken);
            if (!pollResponse.IsSuccessStatusCode)
                throw new HttpRequestException($"Krea poll HTTP {(int)pollResponse.StatusCode}: {body}");
            using var json = JsonDocument.Parse(body);
            var status = FindJsonString(json.RootElement, "status") ?? string.Empty;
            if (status is "failed" or "cancelled")
                throw new InvalidOperationException($"Krea job ended with status {status}.");
            var url = FindJsonString(json.RootElement, "url");
            if (!string.IsNullOrWhiteSpace(url))
                return await SharedHttpClient.GetByteArrayAsync(url, cancellationToken);
        }
        throw new TimeoutException("Krea image generation did not complete within 3 minutes.");
    }

    private async Task<byte[]> ExtractImageBytesAsync(string body, CancellationToken cancellationToken)
    {
        if (body.Contains("\ndata:", StringComparison.Ordinal) || body.StartsWith("data:", StringComparison.Ordinal))
        {
            string? newest = null;
            foreach (var line in body.Split('\n'))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("data:", StringComparison.Ordinal)) continue;
                var payload = trimmed[5..].Trim();
                if (payload.Length == 0 || payload == "[DONE]") continue;
                try
                {
                    using var eventJson = JsonDocument.Parse(payload);
                    newest = FindJsonString(eventJson.RootElement, "result")
                        ?? FindJsonString(eventJson.RootElement, "partial_image_b64")
                        ?? newest;
                }
                catch (JsonException)
                {
                }
            }
            if (!string.IsNullOrWhiteSpace(newest))
                return Convert.FromBase64String(newest);
        }
        using var json = JsonDocument.Parse(body);
        var base64 = FindJsonString(json.RootElement, "b64_json")
            ?? FindJsonString(json.RootElement, "result")
            ?? FindJsonString(json.RootElement, "partial_image_b64");
        if (!string.IsNullOrWhiteSpace(base64) && !base64.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            var comma = base64.IndexOf(',');
            return Convert.FromBase64String(comma >= 0 ? base64[(comma + 1)..] : base64);
        }
        var url = FindJsonString(json.RootElement, "url")
            ?? FindJsonString(json.RootElement, "image_url");
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException("Image provider returned no image data.");
        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return Convert.FromBase64String(url[(url.IndexOf(',') + 1)..]);
        return await SharedHttpClient.GetByteArrayAsync(url, cancellationToken);
    }

    private static string? FindJsonString(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals(name) && property.Value.ValueKind == JsonValueKind.String)
                    return property.Value.GetString();
                var nested = FindJsonString(property.Value, name);
                if (nested != null) return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindJsonString(item, name);
                if (nested != null) return nested;
            }
        }
        return null;
    }

    private static object AspectToFalSize(string aspectRatio) => aspectRatio switch
    {
        "16:9" => new { width = 1344, height = 768 },
        "9:16" => new { width = 768, height = 1344 },
        "4:3" => new { width = 1152, height = 864 },
        "3:4" => new { width = 864, height = 1152 },
        _ => new { width = 1024, height = 1024 }
    };

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
