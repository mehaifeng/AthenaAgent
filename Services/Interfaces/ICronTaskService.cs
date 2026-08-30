using Athena.UI.Models;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Athena.UI.Services.Interfaces;

/// <summary>
/// cron 任务的状态与生命周期所有者：CRUD、启停、领取与完成运行。
///
/// 边界：不触碰 Avalonia，不创建会话，不暴露可修改的集合。所有读取返回不可变快照（深拷贝），
/// 变更只能经由这里的方法，UI 通过 <see cref="TasksChanged"/> 重建自己的投影。
/// </summary>
public interface ICronTaskService
{
    /// <summary>任务集合发生任何变化后触发（含运行状态推进）。携带不可变快照。</summary>
    event EventHandler<CronTaskListChangedEventArgs>? TasksChanged;

    /// <summary>加载时被隔离的损坏记录数，用于 UI 提示。</summary>
    int CorruptedRecordCount { get; }

    /// <summary>当前全部任务的不可变快照。</summary>
    IReadOnlyList<CronTask> GetTasks();

    /// <summary>取单个任务的不可变快照；不存在返回 null。</summary>
    CronTask? GetTask(string taskId);

    Task<CronTaskMutationResult> CreateAsync(CronTaskDraft draft);

    Task<CronTaskMutationResult> UpdateAsync(string taskId, CronTaskDraft draft);

    Task<bool> DeleteAsync(string taskId);

    /// <summary>启停任务。启用时按当前时刻重算下一次触发。</summary>
    Task<CronTaskMutationResult> SetEnabledAsync(string taskId, bool isEnabled);

    /// <summary>
    /// 领取所有到期的运行。错过（应用未运行期间跨过的）触发固定按 Skip 处理：
    /// 记录一条 Skipped，周期任务重算 now 之后的下一次，单次任务停用，绝不补跑全部错过的次数。
    /// </summary>
    Task<IReadOnlyList<CronTaskClaim>> ClaimDueAsync(DateTimeOffset now);

    /// <summary>
    /// 领取一次手动运行。不改变 cron 的下一次计划，也不会因 RunOnce 而停用任务。
    /// </summary>
    Task<CronTaskClaim?> ClaimManualAsync(string taskId, DateTimeOffset now);

    /// <summary>把一次已领取的运行推进到 Running。</summary>
    Task MarkRunStartedAsync(string taskId, string runId, DateTimeOffset startedAt);

    /// <summary>写回一次运行的终态。没有实际执行过的运行绝不能被写成 Succeeded。</summary>
    Task CompleteRunAsync(
        string taskId,
        string runId,
        CronRunState state,
        DateTimeOffset completedAt,
        string? conversationId = null,
        string? historyId = null,
        string? note = null,
        string? error = null);

    /// <summary>
    /// 启动时把上次进程退出时遗留的 Queued/Running 记录收敛为 Interrupted，绝不自动重放。
    /// 返回被收敛的记录数。
    /// </summary>
    Task<int> ReconcileOrphanedRunsAsync(DateTimeOffset now);
}
