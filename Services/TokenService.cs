using CommunityToolkit.Mvvm.ComponentModel;
using System;

namespace Athena.UI.Services;

public readonly record struct TokenUsageSnapshot(
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    long TotalTokens,
    string? RequestId = null,
    string? ProviderId = null,
    string? ModelId = null,
    DateTimeOffset? ObservedAtUtc = null);

public enum TokenMeasurementKind
{
    Unanchored,
    ApiExact,
    CalibratedEstimate,
    HeuristicAfterAnchor
}

public sealed record ConversationUsageState(
    bool HasEverReceivedValidUsage,
    TokenMeasurementKind Kind,
    long CurrentTokens,
    long CachedInputTokens,
    DateTimeOffset? LastUsageAt,
    string? LastRequestId,
    string ModelFingerprint,
    double Confidence,
    long ContextRevision);

public interface ITokenService
{
    long CurrentTokens { get; }
    long MaxTokens { get; set; }
    long CompressionThresholdTokens { get; set; }
    long CachedInputTokens { get; }
    bool IsRealUsage { get; }
    bool HasVisibleUsage { get; }
    TokenMeasurementKind MeasurementKind { get; }
    ConversationUsageState State { get; }
    string CompressionPreview { get; set; }
    string TokenInfoText { get; }
    string TokenUsageBarText { get; }
    bool IsWarningLimit { get; }
    bool IsNearLimit { get; }

    bool TryApplyUsage(
        TokenUsageSnapshot usage,
        string? expectedProviderId = null,
        string? expectedModelId = null,
        long contextRevision = 0);
    void ApplyUsage(TokenUsageSnapshot usage);
    void RefreshEstimate(long estimatedTotalTokens, long contextRevision = 0, bool calibrated = false, double confidence = 0);
    void ApplyEstimatedBaseline(long estimatedTotalTokens, long contextRevision = 0, bool calibrated = false, double confidence = 0);
    void ResetUsage();
}

public partial class TokenService : ObservableObject, ITokenService
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TokenInfoText))]
    [NotifyPropertyChangedFor(nameof(TokenUsageBarText))]
    [NotifyPropertyChangedFor(nameof(IsWarningLimit))]
    [NotifyPropertyChangedFor(nameof(IsNearLimit))]
    [NotifyPropertyChangedFor(nameof(State))]
    private long _currentTokens;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TokenInfoText))]
    [NotifyPropertyChangedFor(nameof(TokenUsageBarText))]
    [NotifyPropertyChangedFor(nameof(IsWarningLimit))]
    [NotifyPropertyChangedFor(nameof(IsNearLimit))]
    private long _maxTokens = 4000;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWarningLimit))]
    [NotifyPropertyChangedFor(nameof(IsNearLimit))]
    private long _compressionThresholdTokens = 3200;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(State))]
    private long _cachedInputTokens;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TokenInfoText))]
    [NotifyPropertyChangedFor(nameof(TokenUsageBarText))]
    [NotifyPropertyChangedFor(nameof(IsRealUsage))]
    [NotifyPropertyChangedFor(nameof(State))]
    private TokenMeasurementKind _measurementKind = TokenMeasurementKind.Unanchored;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVisibleUsage))]
    [NotifyPropertyChangedFor(nameof(State))]
    private bool _hasEverReceivedValidUsage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(State))]
    private DateTimeOffset? _lastUsageAt;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(State))]
    private string? _lastRequestId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(State))]
    private string _modelFingerprint = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(State))]
    private double _confidence;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(State))]
    private long _contextRevision;

    [ObservableProperty]
    private string _compressionPreview = string.Empty;

    public bool IsRealUsage => MeasurementKind == TokenMeasurementKind.ApiExact;
    public bool HasVisibleUsage => HasEverReceivedValidUsage;
    public ConversationUsageState State => new(
        HasEverReceivedValidUsage, MeasurementKind, CurrentTokens, CachedInputTokens,
        LastUsageAt, LastRequestId, ModelFingerprint, Confidence, ContextRevision);
    public string TokenInfoText => $"{(IsRealUsage ? string.Empty : "≈")}{Compact(CurrentTokens)} / {Compact(MaxTokens)}";

    private const char TokenBarFill = '▬'; // ▬ 已填充段
    private const char TokenBarEmpty = '\\';    // \ 空白段
    private const int TokenBarSegments = 16;

    public string TokenUsageBarText
    {
        get
        {
            if (MaxTokens <= 0) return TokenInfoText;
            double ratio = Math.Clamp((double)CurrentTokens / MaxTokens, 0d, 1d);
            int filled = (int)Math.Round(ratio * TokenBarSegments, MidpointRounding.AwayFromZero);
            string bar = new string(TokenBarFill, filled) + new string(TokenBarEmpty, TokenBarSegments - filled);
            int percent = (int)Math.Round(ratio * 100, MidpointRounding.AwayFromZero);
            return $"{(IsRealUsage ? string.Empty : "≈")}{Compact(CurrentTokens)}/{Compact(MaxTokens)}[{bar}]{percent}%";
        }
    }

    public bool IsWarningLimit => MaxTokens > 0
                                  && CurrentTokens >= Math.Min(CompressionThresholdTokens, MaxTokens) * 8 / 10
                                  && CurrentTokens < Math.Min(CompressionThresholdTokens, MaxTokens);

    public bool IsNearLimit => MaxTokens > 0 && CurrentTokens >= Math.Min(CompressionThresholdTokens, MaxTokens);

    public bool TryApplyUsage(
        TokenUsageSnapshot usage,
        string? expectedProviderId = null,
        string? expectedModelId = null,
        long contextRevision = 0)
    {
        if (usage.InputTokens <= 0
            || usage.OutputTokens < 0
            || usage.CachedInputTokens < 0
            || usage.TotalTokens < 0
            || usage.CachedInputTokens > usage.InputTokens
            || (usage.TotalTokens > 0 && usage.TotalTokens < usage.InputTokens + usage.OutputTokens)
            || (expectedProviderId != null && !string.Equals(expectedProviderId, usage.ProviderId, StringComparison.Ordinal))
            || (expectedModelId != null && !string.Equals(expectedModelId, usage.ModelId, StringComparison.Ordinal)))
            return false;

        long current;
        try { current = checked(usage.InputTokens + usage.OutputTokens); }
        catch (OverflowException) { return false; }
        CurrentTokens = current;
        CachedInputTokens = usage.CachedInputTokens;
        HasEverReceivedValidUsage = true;
        MeasurementKind = TokenMeasurementKind.ApiExact;
        LastUsageAt = usage.ObservedAtUtc ?? DateTimeOffset.UtcNow;
        LastRequestId = usage.RequestId;
        ModelFingerprint = string.Join('\u001f', usage.ProviderId ?? string.Empty, usage.ModelId ?? string.Empty);
        Confidence = 1;
        ContextRevision = contextRevision;
        return true;
    }

    public void ApplyUsage(TokenUsageSnapshot usage) => _ = TryApplyUsage(usage);

    public void RefreshEstimate(long estimatedTotalTokens, long contextRevision = 0, bool calibrated = false, double confidence = 0)
    {
        if (MeasurementKind == TokenMeasurementKind.ApiExact) return;
        ApplyEstimate(estimatedTotalTokens, contextRevision, calibrated, confidence);
    }

    public void ApplyEstimatedBaseline(long estimatedTotalTokens, long contextRevision = 0, bool calibrated = false, double confidence = 0)
        => ApplyEstimate(estimatedTotalTokens, contextRevision, calibrated, confidence);

    private void ApplyEstimate(long estimate, long revision, bool calibrated, double confidence)
    {
        CurrentTokens = Math.Max(0, estimate);
        CachedInputTokens = 0;
        ContextRevision = revision;
        Confidence = Math.Clamp(confidence, 0, 1);
        MeasurementKind = HasEverReceivedValidUsage
            ? calibrated ? TokenMeasurementKind.CalibratedEstimate : TokenMeasurementKind.HeuristicAfterAnchor
            : TokenMeasurementKind.Unanchored;
    }

    public void ResetUsage()
    {
        CurrentTokens = 0;
        CachedInputTokens = 0;
        HasEverReceivedValidUsage = false;
        MeasurementKind = TokenMeasurementKind.Unanchored;
        LastUsageAt = null;
        LastRequestId = null;
        ModelFingerprint = string.Empty;
        Confidence = 0;
        ContextRevision = 0;
    }

    private static string Compact(long value) => Math.Abs(value) switch
    {
        >= 1_000_000 => $"{value / 1_000_000d:0.#}M",
        >= 1_000 => $"{value / 1_000d:0.#}K",
        _ => value.ToString("N0")
    };
}
