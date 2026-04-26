using Athena.UI.Models;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.Interfaces;

public interface IBrowserVisionService
{
    Task<BrowserActionRequest> DecideNextActionAsync(
        BrowserTaskRequest task,
        SomObservation observation,
        IReadOnlyList<BrowserActionResult> actionHistory,
        CancellationToken cancellationToken = default);

    Task<(bool Success, string Message)> TestConnectionAsync(CancellationToken cancellationToken = default);
}
