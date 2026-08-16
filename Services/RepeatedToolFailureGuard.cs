using System;
using System.Collections.Concurrent;

namespace Athena.UI.Services;

/// <summary>
/// 重复失败熔断器。
///
/// 模型拿到一个它读不懂的失败时，最常见的反应是把同一个调用原样重发。参数一字未改，
/// 结果必然一模一样，于是一轮轮空转直到撞上迭代轮数上限——实测一次 create_directory
/// 的误报失败烧掉了 30 多轮模型调用。
///
/// 轮数上限只保证「最终会停」，这里保证「很快就停，并且告诉模型该换路子」。
/// 只统计失败：重复成功是正常的（幂等工具、分批写入），一旦成功就清零，
/// 免得早先的偶发失败在后面误伤同一个调用。
///
/// 并发批次里可能同时跑到同一个键，因此内部用并发字典。
/// </summary>
public sealed class RepeatedToolFailureGuard
{
    private readonly ConcurrentDictionary<string, int> _failures = new(StringComparer.Ordinal);
    private readonly int _limit;

    public RepeatedToolFailureGuard(int limit = 3)
    {
        _limit = Math.Max(1, limit);
    }

    /// <summary>该调用是否已连续失败到上限、不应再执行。</summary>
    public bool ShouldBlock(string functionName, string? argumentsJson)
        => FailureCount(functionName, argumentsJson) >= _limit;

    public int FailureCount(string functionName, string? argumentsJson)
        => _failures.TryGetValue(BuildKey(functionName, argumentsJson), out var count) ? count : 0;

    /// <summary>记录一次真实执行的结果。被 <see cref="ShouldBlock"/> 拦下的调用不得调用此方法。</summary>
    public void Record(string functionName, string? argumentsJson, bool succeeded)
    {
        var key = BuildKey(functionName, argumentsJson);
        if (succeeded) _failures.TryRemove(key, out _);
        else _failures.AddOrUpdate(key, 1, static (_, count) => count + 1);
    }

    /// <summary>给模型的说明：说清没有执行、为什么重试没用、以及有哪些出路。</summary>
    public string BuildBlockedMessage(string functionName, string? argumentsJson) =>
        $"Not executed: '{functionName}' already failed {FailureCount(functionName, argumentsJson)} times with exactly these arguments. "
        + "An identical call cannot produce a different result. Re-read the earlier error message, then do one of: "
        + "change the arguments, use a different tool, accept that the goal is already satisfied and move on, "
        + "or stop and tell the user what is blocking you.";

    // 用 NUL 作分隔符：工具名与参数 JSON 都不可能包含它，因此不同调用不会撞键。
    private static string BuildKey(string functionName, string? argumentsJson)
        => functionName + '\0' + (argumentsJson ?? string.Empty);
}
