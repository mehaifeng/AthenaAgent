using Athena.UI.Models;
using Avalonia.Platform;
using System;
using System.Text.Json;

namespace Athena.UI.Services.ModelMetadata;

public static class OpenRouterSeedCatalog
{
    private static readonly Uri SeedUri = new("avares://Athena.UI/Assets/ModelMetadata/openrouter-models.seed.json");

    public static OpenRouterCatalogSnapshot Load()
    {
        try
        {
            using var stream = AssetLoader.Open(SeedUri);
            using var document = JsonDocument.Parse(stream);
            var models = OpenRouterModelMetadataCatalog.ParsePage(document.RootElement, out _, out _);
            var hash = OpenRouterModelMetadataStore.ComputeContentHash(models);
            return new OpenRouterCatalogSnapshot(1, hash, DateTimeOffset.MinValue, OpenRouterModelMetadataCatalog.SourceUrl, hash, null, models);
        }
        catch
        {
            return OpenRouterCatalogSnapshot.Empty;
        }
    }
}
