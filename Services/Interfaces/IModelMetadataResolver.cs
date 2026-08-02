using Athena.UI.Models;

namespace Athena.UI.Services.Interfaces;

public interface IModelMetadataResolver
{
    ResolvedModelMetadata Resolve(
        OpenAiProviderConfiguration provider,
        ProviderModelDescriptor model,
        ProviderModelMetadataProfile? profile,
        OpenRouterCatalogSnapshot snapshot,
        bool isCatalogStale = false);
}
