using CommunityToolkit.Mvvm.ComponentModel;
using System;

namespace Athena.UI.Models;

/// <summary>
/// 流中途失败的自动重试策略。
///
/// 覆盖的是 SDK 重试策略**够不到**的那一段：<c>ClientRetryPolicy</c> 只管建连与状态码，
/// HTTP 200 一回来、响应体开始流，它就再也插不上手了，而上游抖动恰恰最常落在那里
/// （OpenRouter 的 <c>finish_reason=error</c>，见 <c>ProviderStreamSanitizer</c>）。
/// 两者的失败集不相交，所以这不是"在 SDK 重试之上又套一层业务重试"。
/// </summary>
public partial class ProviderRetryOptions : ObservableObject
{
    public const int MaxAllowedAttempts = 5;
    public const int MinDelaySeconds = 1;
    public const int MaxDelaySeconds = 30;

    /// <summary>退避倍数、单次上限、总等待上限与抖动幅度都是内部常量——旋钮越多越没人调得对。</summary>
    private const double BackoffMultiplier = 2;
    private const double MaxSingleDelaySeconds = 30;
    private const double MaxTotalWaitSeconds = 60;
    private const double JitterFraction = 0.2;

    [ObservableProperty]
    private bool _enabled = true;

    /// <summary>首次请求之外的重试次数；0 等同于关闭。</summary>
    [ObservableProperty]
    private int _maxAttempts = 2;

    [ObservableProperty]
    private int _initialDelaySeconds = 2;

    public int EffectiveMaxAttempts => Enabled ? Math.Clamp(MaxAttempts, 0, MaxAllowedAttempts) : 0;

    /// <summary>
    /// 已经失败 <paramref name="completedAttempts"/> 次（首次请求算第 0 次）之后该等多久再重发；
    /// 返回 null 表示不再重试。<paramref name="alreadyWaited"/> 是本轮已经为重试等掉的时间——
    /// 用户盯着一个"正在重试"最多只该等这么久，超了就把结论给他，而不是继续悄悄等下去。
    /// </summary>
    public TimeSpan? ResolveDelay(int completedAttempts, TimeSpan alreadyWaited)
    {
        if (completedAttempts < 0 || completedAttempts >= EffectiveMaxAttempts) return null;

        var remaining = TimeSpan.FromSeconds(MaxTotalWaitSeconds) - alreadyWaited;
        if (remaining <= TimeSpan.Zero) return null;

        var seconds = Math.Clamp(InitialDelaySeconds, MinDelaySeconds, MaxDelaySeconds)
                      * Math.Pow(BackoffMultiplier, completedAttempts);
        // ±20% 抖动：同一次上游故障往往同时打在多个会话和定时任务上，同频重发只会再压一次。
        // 封顶放在抖动之后：先封顶再抖，抖上去的那 20% 就越过了上限。
        seconds *= 1 + ((Random.Shared.NextDouble() * 2) - 1) * JitterFraction;
        seconds = Math.Min(seconds, MaxSingleDelaySeconds);

        var delay = TimeSpan.FromSeconds(seconds);
        return delay > remaining ? remaining : delay;
    }
}
