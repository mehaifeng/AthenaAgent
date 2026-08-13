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

    /// <summary>估算「自锚点以来新增内容」的 token 区间。<paramref name="deltaCharScore"/> 由
    /// <see cref="Athena.UI.Services.Context.ContextRequestPreparer.ComputeDeltaCharScore"/> 计算。</summary>
    DeltaTokenEstimate EstimateDelta(string profileKey, long deltaCharScore);

    /// <summary>用一次干净差分（固定开销未变时两次真实 input 之差）训练增量标度。</summary>
    bool ObserveDelta(string profileKey, long deltaCharScore, long actualDeltaTokens);
    Task FlushAsync(CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
    TokenCalibrationDiagnostics GetDiagnostics();
    void Clear();
}
