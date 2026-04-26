using Athena.UI.Models;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.Interfaces;

public interface IBrowserAgentService
{
    Task<BrowserTaskResult> RunTaskAsync(BrowserTaskRequest request, CancellationToken cancellationToken = default);
}
