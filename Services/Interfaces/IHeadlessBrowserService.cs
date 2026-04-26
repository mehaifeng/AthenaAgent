using Athena.UI.Models;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.Interfaces;

public interface IHeadlessBrowserService
{
    Task<BrowserRuntimeStatus> GetRuntimeStatusAsync(CancellationToken cancellationToken = default);

    Task<BrowserRuntimeInstallResult> InstallRuntimeAsync(CancellationToken cancellationToken = default);

    Task<BrowserSessionInfo> CreateSessionAsync(BrowserSessionOptions options, CancellationToken cancellationToken = default);

    Task<BrowserActionResult> NavigateAsync(string sessionId, string url, CancellationToken cancellationToken = default);

    Task<SomObservation> ObserveAsync(string sessionId, CancellationToken cancellationToken = default);

    Task<BrowserActionResult> ClickAsync(string sessionId, string elementId, CancellationToken cancellationToken = default);

    Task<BrowserActionResult> TypeAsync(string sessionId, string elementId, string text, CancellationToken cancellationToken = default);

    Task<BrowserActionResult> PressKeyAsync(string sessionId, string key, CancellationToken cancellationToken = default);

    Task<BrowserActionResult> ScrollAsync(string sessionId, int deltaX, int deltaY, CancellationToken cancellationToken = default);

    Task<BrowserActionResult> WaitAsync(string sessionId, int milliseconds, CancellationToken cancellationToken = default);

    Task<BrowserActionResult> ExtractTextAsync(string sessionId, CancellationToken cancellationToken = default);

    Task CloseSessionAsync(string sessionId, CancellationToken cancellationToken = default);

    Task<(bool Success, string Message)> TestRuntimeAsync(CancellationToken cancellationToken = default);
}
