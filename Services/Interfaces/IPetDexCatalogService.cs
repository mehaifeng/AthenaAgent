using Avalonia.Media.Imaging;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.Interfaces;

public sealed record PetDexCatalogEntry(
    string Slug,
    string DisplayName,
    string Kind,
    string SubmittedBy,
    string SpritesheetUrl,
    string PetJsonUrl,
    bool IsBuiltIn,
    bool IsInstalled,
    bool IsCurated);

public interface IPetDexCatalogService
{
    IReadOnlyList<PetDexCatalogEntry> GetLocalCatalog();
    Task<IReadOnlyList<PetDexCatalogEntry>> GetCatalogAsync(CancellationToken cancellationToken = default);
    Task InstallAsync(PetDexCatalogEntry entry, CancellationToken cancellationToken = default);
    Task<Bitmap?> GetThumbnailAsync(PetDexCatalogEntry entry, CancellationToken cancellationToken = default);
}
