using Athena.UI.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.Interfaces;

public interface IOpenRouterModelMetadataCatalog
{
    OpenRouterCatalogSnapshot Current { get; }
    bool IsStale { get; }
    event EventHandler? CatalogChanged;
    Task<ModelCatalogRefreshResult> RefreshAsync(bool force, CancellationToken cancellationToken = default);
    Task ClearLocalCacheAsync(CancellationToken cancellationToken = default);
}
