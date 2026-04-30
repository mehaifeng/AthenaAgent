using System.Threading;
using System.Threading.Tasks;
using Athena.UI.Models;

namespace Athena.UI.Services.Interfaces;

public interface IUpdateService
{
    string GetCurrentVersion();
    Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default);
    Task<UpdateApplyResult> PrepareAndLaunchUpdateAsync(UpdateCheckResult checkResult, CancellationToken cancellationToken = default);
}
