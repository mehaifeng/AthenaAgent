using Athena.UI.Models;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.Interfaces;

public interface ITokenCalibrationService
{
    CalibratedTokenEstimate Estimate(ContextFeatureSnapshot features);
    bool Observe(
        ContextFeatureSnapshot features,
        long actualInputTokens,
        bool allowCleanDelta = true,
        ProviderInputModalityUsage? modalityUsage = null);
    Task FlushAsync(CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
    TokenCalibrationDiagnostics GetDiagnostics();
    void Clear();
}
