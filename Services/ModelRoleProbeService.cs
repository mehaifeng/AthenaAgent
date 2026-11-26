#pragma warning disable OPENAI001
using Athena.UI.Models;
using Athena.UI.Services.ComputerUse;
using Athena.UI.Services.Context;
using Athena.UI.Services.Interfaces;
using Athena.UI.Services.ModelMetadata;
using Athena.UI.Services.Protocol;
using OpenAI.Chat;
using SkiaSharp;
using System;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services;

public sealed class ModelRoleProbeService(
    IModelMetadataResolver? resolver = null,
    IOpenRouterModelMetadataCatalog? catalog = null)
{
    private readonly IModelMetadataResolver _resolver = resolver ?? new ModelMetadataResolver(new ModelIdentityMatcher());
    private readonly ConcurrentDictionary<VisionProof, byte> _verifiedVision = new();

    internal CapabilitySupport Vision(AppConfig config, OpenAiProviderConfiguration provider, ProviderModelDescriptor model)
    {
        if (model.Capability is ModelCapability.Embedding or ModelCapability.Speech)
            return CapabilitySupport.Unsupported;
        return Vision(model, Metadata(config, provider, model));
    }

    private static CapabilitySupport Vision(ProviderModelDescriptor model, ResolvedModelMetadata metadata)
    {
        if (model.Capability is ModelCapability.Embedding or ModelCapability.Speech)
            return CapabilitySupport.Unsupported;
        if (metadata.OutputModalities.Count != 0 && !metadata.OutputModalities.Contains("text", StringComparer.OrdinalIgnoreCase))
            return CapabilitySupport.Unsupported;
        if (metadata.InputModalities.Count != 0)
            return metadata.InputModalities.Contains("image", StringComparer.OrdinalIgnoreCase)
                ? CapabilitySupport.Supported : CapabilitySupport.Unsupported;
        return model.Capability == ModelCapability.Image ? CapabilitySupport.Unsupported : CapabilitySupport.Unknown;
    }

    private ResolvedModelMetadata Metadata(AppConfig config, OpenAiProviderConfiguration provider, ProviderModelDescriptor model) =>
        _resolver.Resolve(provider, model, config.AiModels.ModelMetadataProfiles.FirstOrDefault(profile =>
            profile.ProviderId == provider.Id && profile.ExternalModelId == model.Id),
            catalog?.Current ?? OpenRouterCatalogSnapshot.Empty, catalog?.IsStale ?? false);

    internal RoleModelRequest Resolve(AppConfig config, AiModelRole role)
    {
        var assignment = OpenAiModelRuntimeFactory.GetRoleSettings(config, role);
        var provider = config.AiModels.Providers.FirstOrDefault(candidate => candidate.Id == assignment.ProviderId);
        if (provider == null || string.IsNullOrWhiteSpace(assignment.Model) || string.IsNullOrWhiteSpace(provider.ApiKey))
            throw new InvalidOperationException(role == AiModelRole.ComputerUse
                ? "Select a vision model for Computer Use under Providers & models."
                : "Select a model and configure its provider credentials.");
        var model = provider.Models.FirstOrDefault(item => item.Id == assignment.Model) ?? new() { Id = assignment.Model };
        var effective = OpenAiModelRuntimeFactory.Resolve(config, role);
        var metadata = Metadata(config, provider, model);
        var officialFlash = IsOfficialDeepSeek(provider) && model.Id == "deepseek-flash";
        if (role is AiModelRole.MainConversation or AiModelRole.ComputerUse)
        {
            effective = effective with
            {
                Protocol = role == AiModelRole.ComputerUse && officialFlash && effective.Protocol == ProviderProtocol.Auto
                    ? ProviderProtocol.Responses
                    : ResponsesProtocolResolver.Resolve(effective.Protocol, effective.ProviderPreset, effective.BaseUrl, metadata)
            };
        }
        if (role == AiModelRole.ComputerUse && officialFlash && effective.Protocol == ProviderProtocol.Responses)
            effective = effective with { BaseUrl = "https://api.deepseek.com",
                Effort = effective.Effort == ReasoningEffort.Auto ? ReasoningEffort.Low : effective.Effort };
        var vision = Vision(model, metadata);
        if (vision == CapabilitySupport.Unknown && officialFlash && effective.Protocol == ProviderProtocol.Responses)
            vision = CapabilitySupport.Supported;
        return new(role, provider.Id, effective, config.Timeout, vision,
            effective.Protocol == ProviderProtocol.Responses && officialFlash
                || metadata.SupportsStructuredOutput.Value == CapabilitySupport.Supported,
            officialFlash && effective.Protocol == ProviderProtocol.Responses);
    }

    internal bool NeedsVisionProof(RoleModelRequest request)
    {
        if (request.Vision == CapabilitySupport.Unsupported)
            throw new InvalidOperationException("The selected Computer Use model does not support image input.");
        return request.Vision == CapabilitySupport.Unknown && !_verifiedVision.ContainsKey(VisionProof.For(request));
    }

    internal async Task TestAsync(RoleModelRequest request, CancellationToken token, PipelineTransport? transport = null)
    {
        var model = request.Model;
        if (request.Role == AiModelRole.Embedding)
        {
            var client = OpenAiModelRuntimeFactory.CreateClient(model, request.Timeout, transport).GetEmbeddingClient(model.Model);
            using var content = BinaryContent.Create(EmbeddingWireFormat.BuildRequest(model.Model, ["Athena model test"]));
            var reply = await client.GenerateEmbeddingsAsync(content, new RequestOptions { CancellationToken = token }).ConfigureAwait(false);
            var vector = EmbeddingWireFormat.ParseResponse(reply.GetRawResponse().Content, 1).Single();
            if (vector.Length == 0 || vector.Any(value => !float.IsFinite(value)) || !vector.Any(value => value != 0))
                throw new InvalidOperationException("The model did not return a usable embedding.");
            return;
        }

        var requiresImage = request.Role is AiModelRole.ComputerUse or AiModelRole.ImageRecognition;
        if (requiresImage && request.Vision == CapabilitySupport.Unsupported)
            throw new InvalidOperationException("The selected model does not support image input.");
        var instruction = "Reply with OK.";
        var code = requiresImage ? RandomNumberGenerator.GetInt32(100000, 1000000).ToString(CultureInfo.InvariantCulture) : "";
        var image = requiresImage ? RenderCode(code) : null;
        if (request.Role == AiModelRole.ComputerUse)
        {
            var planner = new ComputerUsePlanner(request,
                "Read the six digits in the image. Finish with success true and only those digits in summary.", true, transport);
            var result = await planner.DecideAsync(new(image!, "image/png", 328, 96, "", null, []), "", 0, token).ConfigureAwait(false);
            if (result is not { Action: "finish", Success: true } || result.Summary?.Trim() != code)
                throw new InvalidOperationException("The model did not read the image and return the expected Computer Use action.");
            _verifiedVision[VisionProof.For(request)] = 0;
            return;
        }

        var observation = image != null
            ? new UserChatMessage(ChatMessageContentPart.CreateTextPart("Read the six digits in this image. Reply with only the digits."),
                ChatMessageContentPart.CreateImagePart(BinaryData.FromBytes(image), "image/png"))
            : new UserChatMessage(instruction);
        string text;
        if (ResponsesCallHelpers.ShouldUseResponses(model))
        {
            var options = ResponsesCallHelpers.CreateOptions(model, "", null, 2048);
            ResponsesCallHelpers.AddInputItems(options, [observation]);
            var client = ResponsesCallHelpers.CreateResponsesClient(model, request.Timeout, transport);
            text = ResponsesCallHelpers.GetConcatenatedOutputText((await client.CreateResponseAsync(options, token).ConfigureAwait(false)).Value);
        }
        else
        {
            var client = OpenAiModelRuntimeFactory.CreateClient(model, request.Timeout, transport).GetChatClient(model.Model);
            var completion = await client.CompleteChatAsync([observation], new ChatCompletionOptions { MaxOutputTokenCount = 2048 }, token)
                .ConfigureAwait(false);
            text = string.Concat(completion.Value.Content.Select(part => part.Text));
        }
        if (image != null ? text.Trim() != code : string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException(image != null ? "The model did not read the digits in the test image."
                : "The model returned no usable text.");
    }

    internal static void MigrateComputerUse(AppConfig config)
    {
        var models = config.AiModels;
        if (models.Providers.FirstOrDefault(provider => IsOfficialDeepSeek(provider) &&
            !string.IsNullOrWhiteSpace(provider.ApiKey)) is { } deepSeek)
        {
            if (!deepSeek.Models.Any(item => item.Id == "deepseek-flash"))
                deepSeek.Models.Add(new() { Id = "deepseek-flash", DisplayName = "deepseek-flash", Capability = ModelCapability.Text, IsManual = true });
            models.ComputerUse = new() { ProviderId = deepSeek.Id, Model = "deepseek-flash" };
            return;
        }
        var service = new ModelRoleProbeService();
        foreach (var legacy in new[] { models.ImageRecognition, models.MainConversation })
        {
            var provider = models.Providers.FirstOrDefault(item => item.Id == legacy.ProviderId && !string.IsNullOrWhiteSpace(item.ApiKey));
            if (provider == null || string.IsNullOrWhiteSpace(legacy.Model)) continue;
            var existing = provider.Models.FirstOrDefault(item => item.Id == legacy.Model);
            var model = existing ?? new() { Id = legacy.Model, DisplayName = legacy.Model, IsManual = true };
            var vision = service.Vision(config, provider, model);
            if (vision == CapabilitySupport.Supported ||
                ReferenceEquals(legacy, models.ImageRecognition) && vision == CapabilitySupport.Unknown)
            {
                if (existing == null) provider.Models.Add(model);
                models.ComputerUse = new() { ProviderId = provider.Id, Model = model.Id };
                return;
            }
        }
    }

    internal static bool IsOfficialDeepSeek(OpenAiProviderConfiguration provider) =>
        Uri.TryCreate(provider.BaseUrl, UriKind.Absolute, out var address) && address.Scheme == "https"
        && address.Host == "api.deepseek.com" && address.IsDefaultPort && address.UserInfo.Length == 0;

    internal static byte[] RenderCode(string code)
    {
        using var surface = SKSurface.Create(new SKImageInfo(328, 96));
        surface.Canvas.Clear(SKColors.White);
        using var paint = new SKPaint { Color = SKColors.Black };
        // Seven-segment digits use the same geometry even in a minimal installation with no system fonts.
        string[] digits = ["012456", "25", "02346", "02356", "1235", "01356", "013456", "025", "0123456", "012356"];
        SKRect[] segments = [new(8, 0, 33, 7), new(0, 7, 7, 34), new(34, 7, 41, 34), new(8, 34, 33, 41),
            new(0, 41, 7, 67), new(34, 41, 41, 67), new(8, 67, 33, 74)];
        for (var i = 0; i < code.Length; i++)
        {
            surface.Canvas.Save();
            surface.Canvas.Translate(13 + 52 * i, 11);
            foreach (var segment in digits[code[i] - '0'])
                surface.Canvas.DrawRect(segments[segment - '0'], paint);
            surface.Canvas.Restore();
        }
        using var snapshot = surface.Snapshot();
        using var bytes = snapshot.Encode(SKEncodedImageFormat.Png, 100);
        return bytes.ToArray();
    }

    private sealed record VisionProof(string ProviderId, string Endpoint, string Model, ProviderProtocol Protocol,
        ReasoningEffort Effort, bool StructuredOutput, string CredentialHash)
    {
        public static VisionProof For(RoleModelRequest request) => new(request.ProviderId, request.Model.BaseUrl, request.Model.Model,
            request.Model.Protocol, request.Model.Effort, request.StructuredOutput,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.Model.ApiKey))));
    }
}

internal sealed record RoleModelRequest(AiModelRole Role, string ProviderId, EffectiveOpenAiModel Model,
    int Timeout, CapabilitySupport Vision, bool StructuredOutput, bool DeepSeekResponses);
