using CommunityToolkit.Mvvm.ComponentModel;

namespace Athena.UI.Services;

/// <summary>
/// 供应商在一次 API 响应中回报的真实 token 用量快照。
/// 由 OpenAI 兼容协议的 <c>usage</c> 字段而来，是上下文占用的权威值（供应商自身分词器计得）。
/// </summary>
public readonly record struct TokenUsageSnapshot(
    int InputTokens,
    int CachedInputTokens,
    int OutputTokens,
    int TotalTokens);

/// <summary>
/// 跨页面共享的 Token 统计服务。
/// 采用「真实锚点优先，估算兜底」模型：一旦拿到某轮 API 的真实 usage，
/// 上下文大小即以 <c>InputTokens + OutputTokens</c>（当前上下文全量，含本轮回复）为准，
/// 估算漂移随每次响应清零、不再累积。仅在冷启动/供应商不回 usage/压缩回滚等
/// 尚无真实值的时刻，才以整段上下文估算作为降级基准。
/// </summary>
public interface ITokenService
{
    /// <summary>当前上下文占用（派生：真实锚点或估算兜底）。只读——写入请走下列方法。</summary>
    int CurrentTokens { get; }
    int MaxTokens { get; set; }
    /// <summary>最近一次真实 usage 中的缓存命中输入 token（纯展示；缓存只省钱不省窗口）。</summary>
    int CachedInputTokens { get; }
    /// <summary>当前显示的数值是否来自真实 usage（false=估算兜底）。</summary>
    bool IsRealUsage { get; }
    string CompressionPreview { get; set; }
    string TokenInfoText { get; }
    bool IsWarningLimit { get; }
    bool IsNearLimit { get; }

    /// <summary>写入一次真实用量锚点（每轮 API 响应回来时调用）。此后估算不再覆盖它。</summary>
    void ApplyUsage(TokenUsageSnapshot usage);

    /// <summary>估算刷新：仅在尚无真实锚点时以整段上下文估算填充显示，真实锚点存在时忽略。</summary>
    void RefreshEstimate(int estimatedTotalTokens);

    /// <summary>强制以估算作为基准（压缩/回滚/fork 改了上下文却未发 API 时），下一次真实响应会自动重锚。</summary>
    void ApplyEstimatedBaseline(int estimatedTotalTokens);

    /// <summary>会话重置：清空所有统计。</summary>
    void ResetUsage();
}

public partial class TokenService : ObservableObject, ITokenService
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TokenInfoText))]
    [NotifyPropertyChangedFor(nameof(IsWarningLimit))]
    [NotifyPropertyChangedFor(nameof(IsNearLimit))]
    private int _currentTokens;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TokenInfoText))]
    [NotifyPropertyChangedFor(nameof(IsWarningLimit))]
    [NotifyPropertyChangedFor(nameof(IsNearLimit))]
    private int _maxTokens = 4000;

    [ObservableProperty]
    private int _cachedInputTokens;

    [ObservableProperty]
    private bool _isRealUsage;

    [ObservableProperty]
    private string _compressionPreview = string.Empty;

    public string TokenInfoText => $"{CurrentTokens} / {MaxTokens} tokens";

    public bool IsWarningLimit => MaxTokens > 0 && CurrentTokens >= MaxTokens * 0.6 && CurrentTokens < MaxTokens * 0.8;

    public bool IsNearLimit => MaxTokens > 0 && CurrentTokens >= MaxTokens * 0.8;

    public void ApplyUsage(TokenUsageSnapshot usage)
    {
        // 真实锚点 = 本轮输入 + 本轮输出：等于响应后上下文的全量占用（输入已含 persona/工具/历史，输出为刚产出的回复）。
        CurrentTokens = usage.InputTokens + usage.OutputTokens;
        CachedInputTokens = usage.CachedInputTokens;
        IsRealUsage = true;
    }

    public void RefreshEstimate(int estimatedTotalTokens)
    {
        // 已有真实锚点时，估算一律不覆盖——真实值永远优先。
        if (IsRealUsage) return;
        CurrentTokens = estimatedTotalTokens;
    }

    public void ApplyEstimatedBaseline(int estimatedTotalTokens)
    {
        CurrentTokens = estimatedTotalTokens;
        CachedInputTokens = 0;
        IsRealUsage = false;
    }

    public void ResetUsage()
    {
        CurrentTokens = 0;
        CachedInputTokens = 0;
        IsRealUsage = false;
    }
}
