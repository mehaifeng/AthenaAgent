using Athena.UI.Models;

namespace Athena.UI.Services.Interfaces;

public interface ICompressionPlanner
{
    CompressionPlanResult CreatePlan(CompressionPlanRequest request);
}
