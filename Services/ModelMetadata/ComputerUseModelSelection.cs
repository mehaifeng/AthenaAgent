using Athena.UI.Models;
using System;
using System.Linq;

namespace Athena.UI.Services.ModelMetadata;

internal static class ComputerUseModelSelection
{
    internal static CapabilitySupport Classify(ProviderModelDescriptor model, ResolvedModelMetadata metadata)
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
        var resolver = new ModelMetadataResolver(new ModelIdentityMatcher());
        foreach (var legacy in new[] { models.ImageRecognition, models.MainConversation })
        {
            var provider = models.Providers.FirstOrDefault(item => item.Id == legacy.ProviderId && !string.IsNullOrWhiteSpace(item.ApiKey));
            if (provider == null || string.IsNullOrWhiteSpace(legacy.Model)) continue;
            var existing = provider.Models.FirstOrDefault(item => item.Id == legacy.Model);
            var model = existing ?? new() { Id = legacy.Model, DisplayName = legacy.Model, IsManual = true };
            var profile = models.ModelMetadataProfiles.FirstOrDefault(item => item.ProviderId == provider.Id && item.ExternalModelId == model.Id);
            var vision = Classify(model, resolver.ResolveForCapabilities(provider, model, profile, OpenRouterCatalogSnapshot.Empty, false));
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
}
