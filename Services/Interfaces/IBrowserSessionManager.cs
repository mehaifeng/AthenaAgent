using Athena.UI.Models;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.Interfaces;

public interface IBrowserSessionManager
{
    Task<BrowserSessionInfo> CreateAsync(BrowserSessionOptions options, CancellationToken cancellationToken = default);

    Task<BrowserSessionInfo?> GetAsync(string sessionId, CancellationToken cancellationToken = default);

    Task TouchAsync(string sessionId, string? currentUrl = null, CancellationToken cancellationToken = default);

    Task CloseAsync(string sessionId, CancellationToken cancellationToken = default);

    Task<int> CleanupExpiredAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> GetExpiredSessionIdsAsync(CancellationToken cancellationToken = default);

    IReadOnlyCollection<BrowserSessionInfo> ActiveSessions { get; }
}
