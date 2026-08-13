using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.Context;

public sealed class TokenCalibrationService : ITokenCalibrationService, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _gate = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly string _path;
    private readonly TokenFingerprintService _fingerprints;
    private readonly ILogger _logger;
    private readonly Timer _flushTimer;
    private TokenCalibrationDocument _document;
    private readonly Dictionary<string, (ContextFeatureSnapshot Features, long Actual)> _previous = new(StringComparer.Ordinal);
    private bool _dirty;
    private long _mutationVersion;

    public TokenCalibrationService(
        IPlatformPathService paths,
        TokenFingerprintService fingerprints,
        ILogger logger)
    {
        _path = paths.GetTokenCalibrationFilePath();
        _fingerprints = fingerprints;
        _logger = logger.ForContext<TokenCalibrationService>();
        _document = Load();
        _flushTimer = new Timer(_ => _ = FlushSafelyAsync(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    public CalibratedTokenEstimate Estimate(ContextFeatureSnapshot features)
    {
        lock (_gate)
        {
            if (!_document.Profiles.TryGetValue(features.ModelProfileKey, out var profile)
                || profile.EstimatorVersion != features.EstimatorVersion
                || profile.KeyId != _fingerprints.KeyId)
                return new CalibratedTokenEstimate(features.HeuristicEstimate, Conservative(features.HeuristicEstimate, 0), 0, features.ModelProfileKey, 0);
            var mean = ClampToken(Predict(profile, features));
            var imageError = IsImageCorrectionEnabled(profile, features)
                ? profile.ImageEwmaAbsoluteError
                : features.ImagePriorTokens * 0.25;
            var decision = Conservative(mean, profile.EwmaAbsoluteError + imageError);
            var confidence = features.ImageBinaryIncluded && features.ImageCount > 0
                ? Math.Min(Confidence(profile), ImageConfidence(profile))
                : Confidence(profile);
            return new CalibratedTokenEstimate(mean, decision, confidence, features.ModelProfileKey, profile.SampleCount);
        }
    }

    // 增量标度的合法区间与冷启动带宽。区间同 Observe 的 ratio 门槛，避免异常样本污染。
    private const double MinDeltaScale = 0.25;
    private const double MaxDeltaScale = 4.0;
    private const double ColdStartDeltaMape = 0.35;

    public DeltaTokenEstimate EstimateDelta(string profileKey, long deltaCharScore)
    {
        if (deltaCharScore <= 0) return new DeltaTokenEstimate(0, 0, 0, 1, 0);
        lock (_gate)
        {
            _document.Profiles.TryGetValue(profileKey, out var profile);
            var trained = profile is { DeltaSampleCount: > 0 } && profile.KeyId == _fingerprints.KeyId;
            var scale = trained ? profile!.DeltaScale : 1.0;
            var mape = profile is { DeltaSampleCount: >= 3 } && profile.KeyId == _fingerprints.KeyId
                ? Math.Clamp(profile.DeltaMape, 0.05, 1.0)
                : ColdStartDeltaMape;
            var expected = ClampToken(deltaCharScore * scale);
            // 带宽按增量本身的相对误差算，因此它随增量缩小而缩小——不会像整段估算那样
            // 把一次历史失准以固定的绝对量挂在后续每一次判定上。
            var band = Math.Max(64, expected * mape);
            var samples = profile?.DeltaSampleCount ?? 0;
            return new DeltaTokenEstimate(
                ClampToken(Math.Max(0, expected - band)),
                expected,
                ClampToken(expected + band),
                samples >= 10 ? 0.9 : samples >= 3 ? 0.6 : 0.2,
                samples);
        }
    }

    public bool ObserveDelta(string profileKey, long deltaCharScore, long actualDeltaTokens)
    {
        if (string.IsNullOrEmpty(profileKey) || deltaCharScore <= 0 || actualDeltaTokens <= 0) return false;
        var ratio = actualDeltaTokens / (double)deltaCharScore;
        if (!double.IsFinite(ratio) || ratio is < MinDeltaScale or > MaxDeltaScale) return false;

        lock (_gate)
        {
            var profile = GetOrCreateDeltaProfile(profileKey);
            var predicted = deltaCharScore * (profile.DeltaSampleCount > 0 ? profile.DeltaScale : 1.0);
            var percentageError = Math.Abs(actualDeltaTokens - predicted) / Math.Max(1, actualDeltaTokens);
            var alpha = profile.DeltaSampleCount < 3 ? 0.5 : 0.2;
            profile.DeltaScale = profile.DeltaSampleCount == 0
                ? Math.Clamp(ratio, MinDeltaScale, MaxDeltaScale)
                : Math.Clamp(profile.DeltaScale * (1 - alpha) + ratio * alpha, MinDeltaScale, MaxDeltaScale);
            profile.DeltaMape = profile.DeltaSampleCount == 0
                ? percentageError
                : profile.DeltaMape * (1 - alpha) + percentageError * alpha;
            profile.DeltaSampleCount++;
            profile.LastUpdatedAtUtc = DateTimeOffset.UtcNow;
            _dirty = true;
            _mutationVersion++;
            return true;
        }
    }

    private TokenCalibrationProfile GetOrCreateDeltaProfile(string profileKey)
    {
        if (!_document.Profiles.TryGetValue(profileKey, out var profile)
            || profile.KeyId != _fingerprints.KeyId)
        {
            profile = new TokenCalibrationProfile
            {
                ProfileKey = profileKey,
                EstimatorVersion = ContextRequestPreparer.EstimatorVersion,
                KeyId = _fingerprints.KeyId
            };
            _document.Profiles[profileKey] = profile;
        }
        return profile;
    }

    public bool Observe(
        ContextFeatureSnapshot features,
        long actualInputTokens,
        bool allowCleanDelta = true,
        ProviderInputModalityUsage? modalityUsage = null)
    {
        if (actualInputTokens <= 0 || features.HeuristicEstimate <= 0) return false;
        var ratio = actualInputTokens / (double)features.HeuristicEstimate;
        if (!double.IsFinite(ratio) || ratio is < 0.25 or > 4) return false;

        lock (_gate)
        {
            var profile = GetOrCreateCompatibleProfile(features);
            bool observed;
            if (features.ImageBinaryIncluded && features.ImageCount > 0)
                observed = ObserveImage(profile, features, actualInputTokens, modalityUsage);
            else
                observed = ObserveText(profile, features, actualInputTokens, allowCleanDelta);

            _previous[features.ModelProfileKey] = (features, actualInputTokens);
            if (!observed) return false;
            _dirty = true;
            _mutationVersion++;
            return true;
        }
    }

    private TokenCalibrationProfile GetOrCreateCompatibleProfile(ContextFeatureSnapshot features)
    {
        if (!_document.Profiles.TryGetValue(features.ModelProfileKey, out var profile)
            || profile.EstimatorVersion != features.EstimatorVersion
            || profile.KeyId != _fingerprints.KeyId)
        {
            profile = new TokenCalibrationProfile
            {
                ProfileKey = features.ModelProfileKey,
                EstimatorVersion = features.EstimatorVersion,
                KeyId = _fingerprints.KeyId
            };
            _document.Profiles[features.ModelProfileKey] = profile;
        }
        return profile;
    }

    private bool ObserveText(
        TokenCalibrationProfile profile,
        ContextFeatureSnapshot features,
        long actualInputTokens,
        bool allowCleanDelta)
    {
        var predictedBefore = PredictText(profile, features);
        var error = Math.Abs(actualInputTokens - predictedBefore);
        var percentageError = error / Math.Max(1, actualInputTokens);
        var alpha = profile.SampleCount < 3 ? 0.35 : 0.15;
        var textBase = TextBase(features);
        var jsonBase = JsonBase(features);
        var scaledWithoutBias = textBase * profile.TextScale
                                + jsonBase * profile.JsonScale
                                + MessageCount(features) * profile.MessageOverhead;
        var targetScale = Math.Clamp((actualInputTokens - profile.FixedBias - MessageCount(features) * profile.MessageOverhead)
                                     / Math.Max(1, textBase + jsonBase), 0.25, 4);
        if (jsonBase > textBase)
            profile.JsonScale = Math.Clamp(profile.JsonScale * (1 - alpha) + targetScale * alpha, 0.25, 4);
        else
            profile.TextScale = Math.Clamp(profile.TextScale * (1 - alpha) + targetScale * alpha, 0.25, 4);
        var residual = actualInputTokens - scaledWithoutBias;
        profile.FixedBias = Math.Clamp(profile.FixedBias * (1 - alpha) + residual * alpha, -262_144, 262_144);
        if (MessageCount(features) > 0)
        {
            var targetOverhead = Math.Clamp((actualInputTokens - profile.FixedBias - textBase * profile.TextScale - jsonBase * profile.JsonScale)
                                            / MessageCount(features), 0, 64);
            profile.MessageOverhead = Math.Clamp(profile.MessageOverhead * (1 - alpha * 0.25) + targetOverhead * alpha * 0.25, 0, 64);
        }
        profile.EwmaAbsoluteError = profile.SampleCount == 0 ? error : profile.EwmaAbsoluteError * 0.85 + error * 0.15;
        profile.Mape = profile.SampleCount == 0 ? percentageError : profile.Mape * 0.85 + percentageError * 0.15;
        profile.SampleCount++;
        profile.MinimumObservedHeuristic = profile.MinimumObservedHeuristic == 0
            ? features.HeuristicEstimate
            : Math.Min(profile.MinimumObservedHeuristic, features.HeuristicEstimate);
        profile.MaximumObservedHeuristic = Math.Max(profile.MaximumObservedHeuristic, features.HeuristicEstimate);
        profile.LastUpdatedAtUtc = DateTimeOffset.UtcNow;

        if (allowCleanDelta
            && _previous.TryGetValue(features.ModelProfileKey, out var previous)
            && (!previous.Features.ImageBinaryIncluded || previous.Features.ImageCount == 0)
            && previous.Features.FixedOverheadFingerprint == features.FixedOverheadFingerprint
            && features.HeuristicEstimate > previous.Features.HeuristicEstimate
            && actualInputTokens > previous.Actual)
            profile.CleanDeltaSampleCount++;
        return true;
    }

    private bool ObserveImage(
        TokenCalibrationProfile profile,
        ContextFeatureSnapshot features,
        long actualInputTokens,
        ProviderInputModalityUsage? modalityUsage)
    {
        if (features.ImagePriorTokens <= 0) return false;
        var directImageTokens = modalityUsage?.ImageTokens is > 0
                                && modalityUsage.ImageTokens <= actualInputTokens
            ? modalityUsage.ImageTokens.Value
            : (long?)null;
        var clean = directImageTokens.HasValue
            ? features.ImageCount == 1
              && features.KnownDimensionImageCount == 1
              && features.UnknownDimensionImageCount == 0
            : IsCleanResidualImageSample(profile, features);

        // Residual inference is only safe after the text profile has reached
        // medium confidence and the request is dominated by one known image.
        if (!directImageTokens.HasValue && (!clean || Confidence(profile) < 0.65))
            return false;

        var actualImageTokens = directImageTokens
                                ?? ClampToken(actualInputTokens - PredictText(profile, features));
        if (actualImageTokens <= 0) return false;
        var ratio = actualImageTokens / (double)features.ImagePriorTokens;
        if (!double.IsFinite(ratio) || ratio is < 0.1 or > 8) return false;

        var weight = clean ? 1.0 : 0.2;
        var alpha = (profile.ImageSampleCount < 3 ? 0.35 : 0.15) * weight;
        var predictedBefore = features.ImagePriorTokens * profile.ImageScale;
        var error = Math.Abs(actualImageTokens - predictedBefore);
        var percentageError = error / Math.Max(1, actualImageTokens);
        profile.ImageScale = Math.Clamp(profile.ImageScale * (1 - alpha) + ratio * alpha, 0.1, 8);
        profile.ImageEwmaAbsoluteError = profile.ImageSampleCount == 0
            ? error
            : profile.ImageEwmaAbsoluteError * (1 - alpha) + error * alpha;
        profile.ImageMape = profile.ImageSampleCount == 0
            ? percentageError
            : profile.ImageMape * (1 - alpha) + percentageError * alpha;
        profile.ImageSampleCount++;
        if (clean) profile.CleanImageSampleCount++;
        else profile.LowWeightImageSampleCount++;
        if (directImageTokens.HasValue) profile.DirectImageUsageSampleCount++;
        profile.LastUpdatedAtUtc = DateTimeOffset.UtcNow;
        return true;
    }

    private bool IsCleanResidualImageSample(
        TokenCalibrationProfile profile,
        ContextFeatureSnapshot features)
    {
        if (features.ImageCount != 1
            || features.KnownDimensionImageCount != 1
            || features.UnknownDimensionImageCount != 0
            || !_previous.TryGetValue(features.ModelProfileKey, out var previous)
            || (previous.Features.ImageBinaryIncluded && previous.Features.ImageCount > 0)
            || previous.Features.FixedOverheadFingerprint != features.FixedOverheadFingerprint)
            return false;

        var currentText = ClampToken(PredictText(profile, features));
        var previousText = ClampToken(PredictText(profile, previous.Features));
        return Math.Abs(currentText - previousText) <= Math.Max(128, previousText / 10);
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        TokenCalibrationDocument snapshot;
        long snapshotVersion;
        lock (_gate)
        {
            if (!_dirty) return;
            snapshotVersion = _mutationVersion;
            snapshot = JsonSerializer.Deserialize<TokenCalibrationDocument>(
                           JsonSerializer.Serialize(_document, JsonOptions), JsonOptions)
                       ?? new TokenCalibrationDocument { KeyId = _fingerprints.KeyId };
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp-" + Guid.NewGuid().ToString("N");
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, snapshot, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, _path, overwrite: true);
            lock (_gate)
            {
                if (_mutationVersion == snapshotVersion)
                    _dirty = false;
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _document = new TokenCalibrationDocument { KeyId = _fingerprints.KeyId };
            _previous.Clear();
            _dirty = true;
            _mutationVersion++;
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Deleting the aggregate first makes failure non-publishing: a read-only
            // or locked data directory leaves the live profiles untouched.
            if (File.Exists(_path)) File.Delete(_path);
            lock (_gate)
            {
                _document = new TokenCalibrationDocument { KeyId = _fingerprints.KeyId };
                _previous.Clear();
                _dirty = false;
                _mutationVersion++;
            }
            _logger.Information("CalibrationProfileUpdated/Reset Scope=All");
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public TokenCalibrationDiagnostics GetDiagnostics()
    {
        lock (_gate)
        {
            var profiles = _document.Profiles.Values.ToArray();
            return new TokenCalibrationDiagnostics(
                profiles.Length,
                profiles.Sum(profile => (long)profile.SampleCount),
                profiles.Sum(profile => (long)profile.CleanDeltaSampleCount),
                profiles.Sum(profile => (long)profile.ImageSampleCount),
                profiles.Sum(profile => (long)profile.CleanImageSampleCount),
                profiles.Sum(profile => (long)profile.DirectImageUsageSampleCount),
                profiles.Length == 0 ? null : profiles.Max(profile => profile.LastUpdatedAtUtc),
                ContextRequestPreparer.EstimatorVersion,
                _fingerprints.KeyId);
        }
    }

    private TokenCalibrationDocument Load()
    {
        try
        {
            if (!File.Exists(_path)) return new TokenCalibrationDocument { KeyId = _fingerprints.KeyId };
            var loaded = JsonSerializer.Deserialize<TokenCalibrationDocument>(File.ReadAllText(_path), JsonOptions);
            if (loaded == null || loaded.SchemaVersion != 1 || loaded.KeyId != _fingerprints.KeyId)
                return new TokenCalibrationDocument { KeyId = _fingerprints.KeyId };
            loaded.Profiles = new Dictionary<string, TokenCalibrationProfile>(loaded.Profiles, StringComparer.Ordinal);
            return loaded;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Token calibration file could not be loaded; starting with empty aggregate profiles");
            return new TokenCalibrationDocument { KeyId = _fingerprints.KeyId };
        }
    }

    private async Task FlushSafelyAsync()
    {
        try { await FlushAsync().ConfigureAwait(false); }
        catch (Exception ex) { _logger.Warning(ex, "Token calibration aggregate flush failed"); }
    }

    private static long Conservative(long mean, double absoluteError)
        => ClampToken(mean + Math.Max(mean * 0.1, absoluteError * 2));

    private static long ClampToken(double value)
        => !double.IsFinite(value) || value <= 0 ? 0 : value >= long.MaxValue ? long.MaxValue : (long)Math.Ceiling(value);

    private static double Confidence(TokenCalibrationProfile profile)
    {
        if (profile.SampleCount < 3) return 0;
        if (profile.SampleCount >= 30 && profile.Mape <= 0.10) return 0.9;
        if (profile.SampleCount >= 10 && profile.Mape <= 0.20) return 0.65;
        return 0.3;
    }

    private static double ImageConfidence(TokenCalibrationProfile profile)
    {
        if (profile.CleanImageSampleCount < 3) return 0;
        if (profile.CleanImageSampleCount >= 10 && profile.ImageMape <= 0.10) return 0.9;
        if (profile.CleanImageSampleCount >= 5 && profile.ImageMape <= 0.20) return 0.65;
        return 0.3;
    }

    private static double Predict(TokenCalibrationProfile profile, ContextFeatureSnapshot features)
        => PredictText(profile, features) + PredictImage(profile, features);

    private static double PredictText(TokenCalibrationProfile profile, ContextFeatureSnapshot features)
        => profile.FixedBias
           + TextBase(features) * profile.TextScale
           + JsonBase(features) * profile.JsonScale
           + MessageCount(features) * profile.MessageOverhead;

    private static double PredictImage(TokenCalibrationProfile profile, ContextFeatureSnapshot features)
    {
        if (!features.ImageBinaryIncluded || features.ImageCount <= 0) return 0;
        return IsImageCorrectionEnabled(profile, features)
            ? features.ImagePriorTokens * profile.ImageScale
            : features.ImagePriorTokens;
    }

    private static bool IsImageCorrectionEnabled(TokenCalibrationProfile profile, ContextFeatureSnapshot features)
        => features.ImageBinaryIncluded
           && features.ImageCount > 0
           && profile.CleanImageSampleCount >= 3;

    private static long TextBase(ContextFeatureSnapshot features)
        => features.CjkTextChars
           + (features.OtherTextChars + 3) / 4
           + (features.ToolDeclarationChars + 3) / 4
           + (features.AttachmentManifestChars + 3) / 4;

    private static long JsonBase(ContextFeatureSnapshot features) => (features.StructuredJsonChars + 3) / 4;

    private static int MessageCount(ContextFeatureSnapshot features)
        => features.SystemMessageCount + features.UserMessageCount + features.AssistantMessageCount + features.ToolMessageCount;

    public async ValueTask DisposeAsync()
    {
        await _flushTimer.DisposeAsync().ConfigureAwait(false);
        await FlushAsync().ConfigureAwait(false);
        _writeGate.Dispose();
    }
}
