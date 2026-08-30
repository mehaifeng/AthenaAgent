using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.Cron;

/// <summary>
/// cron 任务状态机。
///
/// 关键不变量：
/// - 对外只交出深拷贝，调用方拿到的任何 <see cref="CronTask"/> 都改不动内部状态。
/// - 错过的触发固定 Skip：无论跨过多少次 occurrence，只记一条 Skipped，下一次从 now 之后算起。
/// - RunOnce 在"被领取"的瞬间停用，而不是在"运行成功"之后——失败也不重试，是刻意的。
/// - 手动运行不动 <see cref="CronTask.NextOccurrence"/>，也不触发 RunOnce 的停用。
/// </summary>
public sealed class CronTaskService : ICronTaskService, IDisposable
{
    /// <summary>
    /// 到期容差：检查按整分钟对齐，一次正常 tick 最多让 NextOccurrence 落后约 60 秒。
    /// 超出这个窗口说明应用当时没在运行（或被挂起），按"错过"处理。
    /// </summary>
    public static readonly TimeSpan MissedRunThreshold = TimeSpan.FromMinutes(2);

    private readonly ICronTaskStore _store;
    private readonly ICronScheduleService _scheduleService;
    private readonly ISystemClock _clock;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly List<CronTask> _tasks = new();
    private readonly object _readLock = new();

    public event EventHandler<CronTaskListChangedEventArgs>? TasksChanged;

    public int CorruptedRecordCount { get; private set; }

    public CronTaskService(
        ICronTaskStore store,
        ICronScheduleService scheduleService,
        ISystemClock clock,
        ILogger logger)
    {
        _store = store;
        _scheduleService = scheduleService;
        _clock = clock;
        _logger = logger.ForContext<CronTaskService>();

        _store.DeleteLegacyStoreIfPresent();

        var loaded = _store.Load();
        CorruptedRecordCount = loaded.CorruptedCount;
        lock (_readLock)
        {
            _tasks.AddRange(loaded.Tasks);
        }

        RevalidateLoadedTasks();
    }

    public IReadOnlyList<CronTask> GetTasks()
    {
        lock (_readLock)
        {
            return _tasks.Select(task => task.Clone()).ToList();
        }
    }

    public CronTask? GetTask(string taskId)
    {
        lock (_readLock)
        {
            return _tasks.FirstOrDefault(task => string.Equals(task.Id, taskId, StringComparison.Ordinal))?.Clone();
        }
    }

    public async Task<CronTaskMutationResult> CreateAsync(CronTaskDraft draft)
    {
        var validation = ValidateDraft(draft);
        if (!validation.IsValid) return CronTaskMutationResult.Invalid(validation);

        var now = _clock.UtcNow;
        var task = new CronTask
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = ResolveName(draft),
            Instruction = draft.Instruction.Trim(),
            CronExpression = validation.NormalizedExpression!,
            TimeZoneId = validation.NormalizedTimeZoneId!,
            RunOnce = draft.RunOnce,
            NotifyOnCompletion = draft.NotifyOnCompletion,
            WorkspaceId = string.IsNullOrWhiteSpace(draft.WorkspaceId) ? null : draft.WorkspaceId.Trim(),
            IsEnabled = draft.IsEnabled,
            CreatedAt = now,
            UpdatedAt = now
        };

        task.NextOccurrence = task.IsEnabled
            ? _scheduleService.GetNextOccurrence(task.CronExpression, task.TimeZoneId, now)
            : null;

        if (task.IsEnabled && task.NextOccurrence == null)
        {
            validation.AddIssue(
                "no_future_occurrence",
                $"The cron expression '{task.CronExpression}' has no future occurrence in time zone '{task.TimeZoneId}'.");
            return CronTaskMutationResult.Invalid(validation);
        }

        await MutateAsync(tasks => tasks.Add(task));
        _logger.Information(
            "Cron task created: {TaskId} '{Name}' [{Expression} @ {TimeZone}] next={Next}",
            task.Id, task.Name, task.CronExpression, task.TimeZoneId, task.NextOccurrence);

        return CronTaskMutationResult.Ok(task.Clone(), validation);
    }

    public async Task<CronTaskMutationResult> UpdateAsync(string taskId, CronTaskDraft draft)
    {
        var validation = ValidateDraft(draft);
        if (!validation.IsValid) return CronTaskMutationResult.Invalid(validation);

        CronTask? updated = null;
        var found = false;

        await MutateAsync(tasks =>
        {
            var existing = tasks.FirstOrDefault(task => string.Equals(task.Id, taskId, StringComparison.Ordinal));
            if (existing == null) return;
            found = true;

            var now = _clock.UtcNow;
            existing.Name = ResolveName(draft);
            existing.Instruction = draft.Instruction.Trim();
            existing.CronExpression = validation.NormalizedExpression!;
            existing.TimeZoneId = validation.NormalizedTimeZoneId!;
            existing.RunOnce = draft.RunOnce;
            existing.NotifyOnCompletion = draft.NotifyOnCompletion;
            existing.WorkspaceId = string.IsNullOrWhiteSpace(draft.WorkspaceId) ? null : draft.WorkspaceId.Trim();
            existing.IsEnabled = draft.IsEnabled;
            existing.UpdatedAt = now;
            existing.NextOccurrence = existing.IsEnabled
                ? _scheduleService.GetNextOccurrence(existing.CronExpression, existing.TimeZoneId, now)
                : null;

            updated = existing.Clone();
        });

        if (!found) return CronTaskMutationResult.NotFound(taskId);

        _logger.Information("Cron task updated: {TaskId} next={Next}", taskId, updated?.NextOccurrence);
        return CronTaskMutationResult.Ok(updated!, validation);
    }

    public async Task<bool> DeleteAsync(string taskId)
    {
        var removed = false;
        await MutateAsync(tasks =>
        {
            var index = tasks.FindIndex(task => string.Equals(task.Id, taskId, StringComparison.Ordinal));
            if (index < 0) return;
            tasks.RemoveAt(index);
            removed = true;
        });

        if (removed) _logger.Information("Cron task deleted: {TaskId}", taskId);
        return removed;
    }

    public async Task<CronTaskMutationResult> SetEnabledAsync(string taskId, bool isEnabled)
    {
        CronTask? updated = null;
        var found = false;

        await MutateAsync(tasks =>
        {
            var existing = tasks.FirstOrDefault(task => string.Equals(task.Id, taskId, StringComparison.Ordinal));
            if (existing == null) return;
            found = true;

            var now = _clock.UtcNow;
            existing.IsEnabled = isEnabled;
            existing.UpdatedAt = now;
            existing.NextOccurrence = isEnabled
                ? _scheduleService.GetNextOccurrence(existing.CronExpression, existing.TimeZoneId, now)
                : null;
            updated = existing.Clone();
        });

        if (!found) return CronTaskMutationResult.NotFound(taskId);

        _logger.Information("Cron task {TaskId} enabled={Enabled} next={Next}", taskId, isEnabled, updated?.NextOccurrence);
        return CronTaskMutationResult.Ok(updated!, new CronValidationResult());
    }

    public async Task<IReadOnlyList<CronTaskClaim>> ClaimDueAsync(DateTimeOffset now)
    {
        var claims = new List<CronTaskClaim>();
        var skipped = 0;

        await MutateAsync(tasks =>
        {
            foreach (var task in tasks)
            {
                if (!task.IsEnabled || task.NextOccurrence == null) continue;
                var scheduledFor = task.NextOccurrence.Value;
                if (scheduledFor > now) continue;

                var isMissed = now - scheduledFor > MissedRunThreshold;
                if (isMissed)
                {
                    // 错过多少次都只记一条：开机瞬间开出 N 个会话是灾难，不是补偿。
                    task.AddRun(new CronTaskRunRecord
                    {
                        Trigger = CronRunTrigger.Scheduled,
                        ScheduledFor = scheduledFor,
                        CompletedAt = now,
                        State = CronRunState.Skipped,
                        Note = $"Missed while the app was not running (scheduled {scheduledFor:u})."
                    });
                    AdvanceAfterClaim(task, now);
                    task.UpdatedAt = now;
                    skipped++;
                    continue;
                }

                var run = new CronTaskRunRecord
                {
                    Trigger = CronRunTrigger.Scheduled,
                    ScheduledFor = scheduledFor,
                    State = CronRunState.Queued
                };
                task.AddRun(run);
                AdvanceAfterClaim(task, now);
                task.UpdatedAt = now;
                claims.Add(new CronTaskClaim { Task = task.Clone(), Run = run.Clone() });
            }
        });

        if (skipped > 0) _logger.Information("Skipped {Count} missed cron occurrence(s)", skipped);
        if (claims.Count > 0) _logger.Information("Claimed {Count} due cron run(s)", claims.Count);
        return claims;
    }

    public async Task<CronTaskClaim?> ClaimManualAsync(string taskId, DateTimeOffset now)
    {
        CronTaskClaim? claim = null;

        await MutateAsync(tasks =>
        {
            var task = tasks.FirstOrDefault(candidate => string.Equals(candidate.Id, taskId, StringComparison.Ordinal));
            if (task == null) return;

            // 手动运行刻意不动 NextOccurrence，也不触发 RunOnce 停用：
            // "现在跑一次"不应该消耗掉用户排好的那一次计划。
            var run = new CronTaskRunRecord
            {
                Trigger = CronRunTrigger.Manual,
                ScheduledFor = null,
                State = CronRunState.Queued
            };
            task.AddRun(run);
            task.UpdatedAt = now;
            claim = new CronTaskClaim { Task = task.Clone(), Run = run.Clone() };
        });

        if (claim != null) _logger.Information("Claimed a manual cron run for {TaskId}", taskId);
        return claim;
    }

    public Task MarkRunStartedAsync(string taskId, string runId, DateTimeOffset startedAt)
        => MutateAsync(tasks =>
        {
            var run = FindRun(tasks, taskId, runId, out var task);
            if (run == null || task == null) return;
            run.State = CronRunState.Running;
            run.StartedAt = startedAt;
            task.UpdatedAt = startedAt;
        });

    public Task CompleteRunAsync(
        string taskId,
        string runId,
        CronRunState state,
        DateTimeOffset completedAt,
        string? conversationId = null,
        string? historyId = null,
        string? note = null,
        string? error = null)
        => MutateAsync(tasks =>
        {
            var run = FindRun(tasks, taskId, runId, out var task);
            if (run == null || task == null)
            {
                _logger.Warning("Completion reported for an unknown cron run {RunId} on task {TaskId}", runId, taskId);
                return;
            }

            run.State = state;
            run.CompletedAt = completedAt;
            run.ConversationId = conversationId ?? run.ConversationId;
            run.HistoryId = historyId ?? run.HistoryId;
            run.Note = note ?? run.Note;
            run.Error = error;
            task.UpdatedAt = completedAt;

            _logger.Information(
                "Cron run {RunId} on task {TaskId} finished as {State} (conversation={ConversationId})",
                runId, taskId, state, run.ConversationId);
        });

    public async Task<int> ReconcileOrphanedRunsAsync(DateTimeOffset now)
    {
        var reconciled = 0;
        await MutateAsync(tasks =>
        {
            foreach (var task in tasks)
            {
                foreach (var run in task.RecentRuns.Where(candidate => !candidate.IsTerminal))
                {
                    run.State = CronRunState.Interrupted;
                    run.CompletedAt = now;
                    run.Note = "The app exited while this run was still in flight; it is never replayed automatically.";
                    reconciled++;
                }
            }
        });

        if (reconciled > 0)
        {
            _logger.Warning("Converged {Count} orphaned cron run(s) to interrupted on startup", reconciled);
        }

        return reconciled;
    }

    /// <summary>领取之后推进计划：RunOnce 立即停用，周期任务从 now 之后重算。</summary>
    private void AdvanceAfterClaim(CronTask task, DateTimeOffset now)
    {
        if (task.RunOnce)
        {
            task.IsEnabled = false;
            task.NextOccurrence = null;
            return;
        }

        task.NextOccurrence = _scheduleService.GetNextOccurrence(task.CronExpression, task.TimeZoneId, now);
        if (task.NextOccurrence == null)
        {
            task.IsEnabled = false;
            _logger.Warning("Cron task {TaskId} has no further occurrence and was disabled", task.Id);
        }
    }

    private CronValidationResult ValidateDraft(CronTaskDraft draft)
    {
        var validation = _scheduleService.Validate(draft.CronExpression, draft.TimeZoneId);

        if (string.IsNullOrWhiteSpace(draft.Instruction))
        {
            validation.AddIssue("missing_instruction", "An instruction is required: it is the text handed to the new session when the task fires.");
        }

        return validation;
    }

    private static string ResolveName(CronTaskDraft draft)
    {
        if (!string.IsNullOrWhiteSpace(draft.Name)) return draft.Name.Trim();

        var instruction = draft.Instruction.Trim();
        if (instruction.Length == 0) return "Scheduled task";
        return instruction.Length <= 40 ? instruction : instruction[..40];
    }

    private static CronTaskRunRecord? FindRun(List<CronTask> tasks, string taskId, string runId, out CronTask? owner)
    {
        owner = tasks.FirstOrDefault(task => string.Equals(task.Id, taskId, StringComparison.Ordinal));
        return owner?.RecentRuns.FirstOrDefault(run => string.Equals(run.RunId, runId, StringComparison.Ordinal));
    }

    /// <summary>
    /// 加载后重新校验：cron 或时区已失效的任务被停用（而不是被丢弃——用户还要能看到并修好它）；
    /// 启用中但没有下次触发时刻的任务补算一次。已经过期的 NextOccurrence 刻意保留，
    /// 交给 <see cref="ClaimDueAsync"/> 记为 Skipped，否则那次错过会无声消失。
    /// </summary>
    private void RevalidateLoadedTasks()
    {
        var now = _clock.UtcNow;
        var changed = false;

        lock (_readLock)
        {
            foreach (var task in _tasks)
            {
                var validation = _scheduleService.Validate(task.CronExpression, task.TimeZoneId);
                if (!validation.IsValid)
                {
                    if (task.IsEnabled || task.NextOccurrence != null)
                    {
                        task.IsEnabled = false;
                        task.NextOccurrence = null;
                        changed = true;
                    }
                    _logger.Warning(
                        "Cron task {TaskId} was disabled on load: {Reason}",
                        task.Id, validation.FirstMessage);
                    continue;
                }

                task.CronExpression = validation.NormalizedExpression!;
                task.TimeZoneId = validation.NormalizedTimeZoneId!;

                if (task.IsEnabled && task.NextOccurrence == null)
                {
                    task.NextOccurrence = _scheduleService.GetNextOccurrence(task.CronExpression, task.TimeZoneId, now);
                    changed = true;
                }
                else if (!task.IsEnabled && task.NextOccurrence != null)
                {
                    task.NextOccurrence = null;
                    changed = true;
                }
            }
        }

        if (changed) _ = PersistAsync();
    }

    private async Task MutateAsync(Action<List<CronTask>> mutation)
    {
        await _mutationGate.WaitAsync();
        try
        {
            lock (_readLock)
            {
                mutation(_tasks);
            }

            await PersistAsync();
        }
        finally
        {
            _mutationGate.Release();
        }

        RaiseTasksChanged();
    }

    private Task PersistAsync() => _store.SaveAsync(GetTasks());

    private void RaiseTasksChanged()
        => TasksChanged?.Invoke(this, new CronTaskListChangedEventArgs { Tasks = GetTasks() });

    public void Dispose() => _mutationGate.Dispose();
}
