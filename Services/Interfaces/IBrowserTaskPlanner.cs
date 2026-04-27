using Athena.UI.Models;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.Interfaces;

public interface IBrowserTaskPlanner
{
    Task<BrowserTaskPlan> CreatePlanAsync(BrowserTaskRequest request, CancellationToken cancellationToken = default);
}
