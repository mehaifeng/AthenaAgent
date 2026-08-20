using System;
using System.Collections.Generic;
using Athena.UI.Models;

namespace Athena.UI.Services;

/// <summary>
/// 同一轮多个工具调用的并发编排。
///
/// 模型一轮可以发出多个工具调用，此前它们严格串行执行：「读三个文件」的墙钟延迟就是三倍，
/// 而这三次调用本来互不相关。这里决定哪些可以并发、以及怎么分批。
///
/// 两条不可让步的约束：
/// 1. 不重排。只合并「连续」的一段安全调用，绝不把后面的读提到前面的写之前——
///    模型完全可能发出 [写 A, 读 A]，重排会直接改变语义。
/// 2. 不并发弹窗。只有一定不会触发审批弹窗的调用才进批次（见 IsParallelSafe）。
/// </summary>
public static class ToolCallParallelism
{
    /// <summary>
    /// 该调用能否与相邻调用并发。判据是「一定不会弹窗、且没有写副作用」，而不是「大概安全」：
    /// - 只读档之外一律串行：写操作之间可能互相冲突，也需要逐个确认；
    /// - 审批模式必须是 Off 或 Balanced——这两档下只读工具走自动放行分支，不会弹窗。
    ///   Strict 会对只读工具也弹窗，Automatic 要另外调模型裁决，并发都意味着同时弹出
    ///   多个窗口或多路并发裁决，因此退回串行。
    /// </summary>
    public static bool IsParallelSafe(string functionName, string? argumentsJson, ToolApprovalMode mode)
    {
        if (mode != ToolApprovalMode.Off && mode != ToolApprovalMode.Balanced) return false;
        if (ToolRiskClassifier.Classify(functionName, argumentsJson).Risk == ToolRisk.ReadOnly) return true;

        // 浏览器任务是唯一的例外：每个任务独占一个隔离的 BrowserContext，彼此不共享状态，
        // 而且几乎全是网络与模型等待——「比较这三个网站」串行跑就是三倍墙钟时间。
        // 但它是敏感档、正常会弹窗，所以只在 Off（全程不弹窗）下才允许成批。
        return mode == ToolApprovalMode.Off
            && string.Equals(functionName, "run_browser_task", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 把 <paramref name="total"/> 个调用切成按原序执行的批次。安全调用成批（每批至多
    /// <paramref name="maxParallel"/> 个），其余各自成批（Count = 1），因此调用方可以
    /// 统一地「执行一批 → 按序回填结果」，不必分两条代码路径。
    /// </summary>
    public static List<(int Start, int Count)> PlanBatches(int total, int maxParallel, Func<int, bool> isSafe)
    {
        ArgumentNullException.ThrowIfNull(isSafe);
        var batches = new List<(int Start, int Count)>();
        var limit = Math.Max(1, maxParallel);

        for (int index = 0; index < total;)
        {
            int end = index;
            while (end < total && end - index < limit && isSafe(end)) end++;

            // 当前位置不安全（或并发上限为 1）时退化为单个调用，保持串行语义。
            if (end == index) end = index + 1;

            batches.Add((index, end - index));
            index = end;
        }

        return batches;
    }
}
