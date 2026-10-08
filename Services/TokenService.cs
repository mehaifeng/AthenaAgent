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

/// <summary>
/// 用量只有两种状态：供应商刚报过（<see cref="ApiExact"/>），或者还没有可信的数字（<see cref="Unanchored"/>）——
/// 冷启动，或者压缩/清理/撤销/回退/切换会话之后请求变了、等下一次请求来测。本地不再有任何按字符猜 token 的中间态。
/// </summary>
public enum TokenMeasurementKind
{
    Unanchored,
    ApiExact
}

public sealed record ConversationUsageState(
    bool HasEverReceivedValidUsage,
    TokenMeasurementKind Kind,
    long CurrentTokens,
    long CachedInputTokens,
    DateTimeOffset? LastUsageAt,
    string? LastRequestId,
    string ModelFingerprint,
    long ContextRevision);

public interface ITokenService
{
    long CurrentTokens { get; }
    long MaxTokens { get; set; }
    long CompressionThresholdTokens { get; set; }
    long CachedInputTokens { get; }
    bool IsRealUsage { get; }
    bool HasVisibleUsage { get; }
    /// <summary>
    /// 待测期间的可信下界（token）：压缩摘要的实测 output tokens。它是供应商回报的真实数字，
    /// 说的是「新上下文至少这么大」，不是「现在是这么大」。锚定或撤销压缩时清零。
    /// </summary>
    long LowerBoundTokens { get; set; }
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
    /// <summary>
    /// 请求变了（压缩、清理、撤销、回退、切换会话……），上一次的数字不再描述它：显示「待测」，
    /// 等下一次响应的 usage 重新锚定。<see cref="CurrentTokens"/> 保留上一次实测值——压缩节省角标要拿它做被减数。
    /// 从未收到过 usage 的会话什么都不做。
    /// </summary>
    void MarkPending(long contextRevision = 0);
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
    [NotifyPropertyChangedFor(nameof(IsWarningLimit))]
    [NotifyPropertyChangedFor(nameof(IsNearLimit))]
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
    private long _contextRevision;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TokenInfoText))]
    [NotifyPropertyChangedFor(nameof(TokenUsageBarText))]
    [NotifyPropertyChangedFor(nameof(IsWarningLimit))]
    [NotifyPropertyChangedFor(nameof(IsNearLimit))]
    private long _lowerBoundTokens;

    [ObservableProperty]
    private string _compressionPreview = string.Empty;

    public bool IsRealUsage => MeasurementKind == TokenMeasurementKind.ApiExact;
    public bool HasVisibleUsage => HasEverReceivedValidUsage;
    public ConversationUsageState State => new(
        HasEverReceivedValidUsage, MeasurementKind, CurrentTokens, CachedInputTokens,
        LastUsageAt, LastRequestId, ModelFingerprint, ContextRevision);

    /// <summary>
    /// 待测时不显示实测数字——一个过期的数字比没有数字更会误导。但如果有可信下界
    /// （压缩摘要的 output tokens），就显示它并标注「≥」：它说的是「至少这么大」，
    /// 不是「现在是这么大」，既给了用户一个参考又不会谎报精度。
    /// </summary>
    public string TokenInfoText => IsRealUsage
        ? $"{Compact(CurrentTokens)} / {Compact(MaxTokens)}"
        : LowerBoundTokens > 0
            ? $"≥{Compact(LowerBoundTokens)} / {Compact(MaxTokens)}"
            : $"— / {Compact(MaxTokens)}";

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
            if (!IsRealUsage)
            {
                if (LowerBoundTokens <= 0)
                    return $"—/{Compact(MaxTokens)}[{new string(TokenBarEmpty, TokenBarSegments)}]";
                // 待测但有可信下界：条按下界画，数字带「≥」。它只保证「至少用到这么多」，
                // 所以填充段是事实、空白段是未知，不谎称精度。
                double lowerRatio = Math.Clamp((double)LowerBoundTokens / MaxTokens, 0d, 1d);
                int lowerFilled = (int)Math.Floor(lowerRatio * TokenBarSegments);
                string lowerBar = new string(TokenBarFill, lowerFilled) + new string(TokenBarEmpty, TokenBarSegments - lowerFilled);
                return $"≥{Compact(LowerBoundTokens)}/{Compact(MaxTokens)}[{lowerBar}]";
            }
            return $"{Compact(CurrentTokens)}/{Compact(MaxTokens)}[{bar}]{percent}%";
        }
    }

    public bool IsWarningLimit => IsRealUsage
                                  && MaxTokens > 0
                                  && CurrentTokens >= Math.Min(CompressionThresholdTokens, MaxTokens) * 8 / 10
                                  && CurrentTokens < Math.Min(CompressionThresholdTokens, MaxTokens);

    public bool IsNearLimit => IsRealUsage && MaxTokens > 0 && CurrentTokens >= Math.Min(CompressionThresholdTokens, MaxTokens);

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
        // 真实测量到达：待测期间的审计下界已被它取代，留着只会在下一次待测窗口里复活一个旧数字。
        LowerBoundTokens = 0;
        LastUsageAt = usage.ObservedAtUtc ?? DateTimeOffset.UtcNow;
        LastRequestId = usage.RequestId;
        ModelFingerprint = string.Join('\u001f', usage.ProviderId ?? string.Empty, usage.ModelId ?? string.Empty);
        ContextRevision = contextRevision;
        return true;
    }

    public void ApplyUsage(TokenUsageSnapshot usage) => _ = TryApplyUsage(usage);

    public void MarkPending(long contextRevision = 0)
    {
        if (!HasEverReceivedValidUsage) return;
        ContextRevision = contextRevision;
        MeasurementKind = TokenMeasurementKind.Unanchored;
        // 请求又变了：下界是「压完那一刻」的事实，之后新增的消息都建立在它之上，
        // 继续显示它会把一次压缩的下界拖成整段会话的常驻数字。由下一次压缩重新给出。
        LowerBoundTokens = 0;
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
        ContextRevision = 0;
        LowerBoundTokens = 0;
    }

    private static string Compact(long value) => Math.Abs(value) switch
    {
        >= 1_000_000 => $"{value / 1_000_000d:0.#}M",
        >= 1_000 => $"{value / 1_000d:0.#}K",
        _ => value.ToString("N0")
    };
}
