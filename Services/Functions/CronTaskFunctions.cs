using Athena.UI.Models;
using Athena.UI.Services.Cron;
using Athena.UI.Services.Interfaces;
using Serilog;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Athena.UI.Services.Functions;

/// <summary>
/// cron 定时任务的 Function Calling 实现。
///
/// 与旧实现的根本差别：不再解析 "in 2 hours" / "明天下午" 这类自然语言时间。
/// 调度语义只有一种——标准五段 cron 表达式 + 明确的时区。任何失败都返回结构化 validation，
/// 让模型能据此改正而不是反复重试同一个坏输入。
/// </summary>
public class CronTaskFunctions
{
    private readonly ICronTaskService _taskService;
    private readonly ICronScheduleService _scheduleService;
    private readonly CronExecutionWorker _executionWorker;
    private readonly ILogger _logger;

    public CronTaskFunctions(
        ICronTaskService taskService,
        ICronScheduleService scheduleService,
        CronExecutionWorker executionWorker,
        ILogger logger)
    {
        _taskService = taskService;
        _scheduleService = scheduleService;
        _executionWorker = executionWorker;
        _logger = logger.ForContext<CronTaskFunctions>();
    }

    /// <summary>创建一个 cron 定时任务。</summary>
    public async Task<FunctionResult> CreateTask(
        string name,
        string instruction,
        string cronExpression,
        string? timeZoneId = null,
        bool runOnce = false,
        bool notifyOnCompletion = true,
        string? workspaceId = null)
    {
        try
        {
            var draft = new CronTaskDraft
            {
                Name = name,
                Instruction = instruction,
                CronExpression = cronExpression,
                TimeZoneId = timeZoneId,
                RunOnce = runOnce,
                NotifyOnCompletion = notifyOnCompletion,
                WorkspaceId = workspaceId,
                IsEnabled = true
            };

            var result = await _taskService.CreateAsync(draft);
            if (!result.Success || result.Task == null)
            {
                return FunctionResult.FailureResult(
                    "Cron task validation failed. Fix the reported issues and call create_task again.",
                    BuildValidationPayload(result.Validation));
            }

            var task = result.Task;
            _logger.Information("Function: created cron task {TaskId} [{Expression}]", task.Id, task.CronExpression);

            return FunctionResult.SuccessResult(
                $"Cron task '{task.Name}' created. Next run: {DescribeNext(task)}.",
                BuildTaskPayload(task));
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to create a cron task");
            return FunctionResult.FailureResult(
                $"Failed to create the cron task: {ex.Message}",
                BuildValidationPayload(new CronValidationResult().AddIssue("create_failed", ex.Message)));
        }
    }

    /// <summary>更新一个已存在的 cron 定时任务（全量替换字段）。</summary>
    public async Task<FunctionResult> UpdateTask(
        string taskId,
        string name,
        string instruction,
        string cronExpression,
        string? timeZoneId = null,
        bool runOnce = false,
        bool notifyOnCompletion = true,
        string? workspaceId = null,
        bool isEnabled = true)
    {
        try
        {
            var draft = new CronTaskDraft
            {
                Name = name,
                Instruction = instruction,
                CronExpression = cronExpression,
                TimeZoneId = timeZoneId,
                RunOnce = runOnce,
                NotifyOnCompletion = notifyOnCompletion,
                WorkspaceId = workspaceId,
                IsEnabled = isEnabled
            };

            var result = await _taskService.UpdateAsync(taskId, draft);
            if (!result.Success || result.Task == null)
            {
                return FunctionResult.FailureResult(
                    "Cron task update failed. Fix the reported issues and try again.",
                    BuildValidationPayload(result.Validation));
            }

            _logger.Information("Function: updated cron task {TaskId}", taskId);
            return FunctionResult.SuccessResult(
                $"Cron task '{result.Task.Name}' updated. Next run: {DescribeNext(result.Task)}.",
                BuildTaskPayload(result.Task));
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to update cron task {TaskId}", taskId);
            return FunctionResult.FailureResult(
                $"Failed to update the cron task: {ex.Message}",
                BuildValidationPayload(new CronValidationResult().AddIssue("update_failed", ex.Message)));
        }
    }

    /// <summary>删除一个 cron 定时任务。</summary>
    public async Task<FunctionResult> CancelTask(string taskId)
    {
        try
        {
            var removed = await _taskService.DeleteAsync(taskId);
            if (!removed)
            {
                return FunctionResult.FailureResult(
                    $"No cron task with id '{taskId}'.",
                    BuildValidationPayload(new CronValidationResult().AddIssue("task_not_found", $"No cron task with id '{taskId}'.")));
            }

            _logger.Information("Function: cancelled cron task {TaskId}", taskId);
            return FunctionResult.SuccessResult($"Cron task '{taskId}' was deleted.", new { taskId });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to cancel cron task {TaskId}", taskId);
            return FunctionResult.FailureResult(
                $"Failed to delete the cron task: {ex.Message}",
                BuildValidationPayload(new CronValidationResult().AddIssue("cancel_failed", ex.Message)));
        }
    }

    /// <summary>列出全部 cron 定时任务。</summary>
    public Task<FunctionResult> ListTasks()
    {
        try
        {
            var tasks = _taskService.GetTasks()
                .Select(BuildTaskPayload)
                .ToList();

            _logger.Information("Function: listed {Count} cron task(s)", tasks.Count);
            return Task.FromResult(FunctionResult.SuccessResult($"{tasks.Count} cron task(s).", tasks));
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to list cron tasks");
            return Task.FromResult(FunctionResult.FailureResult($"Failed to list cron tasks: {ex.Message}"));
        }
    }

    /// <summary>
    /// 立即运行一次任务。会创建一个全新会话，但**不改变 cron 的下一次计划**，
    /// 也不会因 RunOnce 而消耗掉那一次计划中的执行。
    /// </summary>
    public async Task<FunctionResult> RunTaskNow(string taskId)
    {
        try
        {
            var task = _taskService.GetTask(taskId);
            if (task == null)
            {
                return FunctionResult.FailureResult(
                    $"No cron task with id '{taskId}'.",
                    BuildValidationPayload(new CronValidationResult().AddIssue("task_not_found", $"No cron task with id '{taskId}'.")));
            }

            var claim = await _executionWorker.RunNowAsync(taskId);
            if (claim == null)
            {
                return FunctionResult.FailureResult(
                    $"Could not queue a manual run for cron task '{taskId}'.",
                    BuildValidationPayload(new CronValidationResult().AddIssue("claim_failed", "The task could not be claimed for a manual run.")));
            }

            _logger.Information("Function: queued manual run {RunId} for cron task {TaskId}", claim.Run.RunId, taskId);
            return FunctionResult.SuccessResult(
                $"Queued a manual run of '{task.Name}'. It opens its own new session; the next scheduled run is unchanged ({DescribeNext(task)}).",
                new
                {
                    taskId,
                    runId = claim.Run.RunId,
                    trigger = "manual",
                    nextOccurrenceUtc = task.NextOccurrence?.ToString("O"),
                    nextOccurrenceLocal = task.NextOccurrence == null
                        ? null
                        : _scheduleService.FormatInZone(task.NextOccurrence.Value, task.TimeZoneId)
                });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to start a manual run for cron task {TaskId}", taskId);
            return FunctionResult.FailureResult(
                $"Failed to start a manual run: {ex.Message}",
                BuildValidationPayload(new CronValidationResult().AddIssue("run_now_failed", ex.Message)));
        }
    }

    private object BuildTaskPayload(CronTask task) => new
    {
        taskId = task.Id,
        name = task.Name,
        instruction = task.Instruction,
        cronExpression = task.CronExpression,
        cronDescription = _scheduleService.Describe(task.CronExpression),
        timeZoneId = task.TimeZoneId,
        runOnce = task.RunOnce,
        notifyOnCompletion = task.NotifyOnCompletion,
        workspaceId = task.WorkspaceId,
        isEnabled = task.IsEnabled,
        createdAtUtc = task.CreatedAt.ToString("O"),
        updatedAtUtc = task.UpdatedAt.ToString("O"),
        nextOccurrenceUtc = task.NextOccurrence?.ToString("O"),
        nextOccurrenceLocal = task.NextOccurrence == null
            ? null
            : _scheduleService.FormatInZone(task.NextOccurrence.Value, task.TimeZoneId),
        upcomingOccurrencesLocal = _scheduleService
            .Preview(task.CronExpression, task.TimeZoneId, DateTimeOffset.UtcNow, 5)
            .Select(occurrence => _scheduleService.FormatInZone(occurrence, task.TimeZoneId))
            .ToArray(),
        recentRuns = task.RecentRuns.Select(run => new
        {
            runId = run.RunId,
            trigger = run.Trigger.ToString().ToLowerInvariant(),
            state = run.State.ToString().ToLowerInvariant(),
            scheduledForUtc = run.ScheduledFor?.ToString("O"),
            startedAtUtc = run.StartedAt?.ToString("O"),
            completedAtUtc = run.CompletedAt?.ToString("O"),
            conversationId = run.ConversationId,
            historyId = run.HistoryId,
            note = run.Note,
            error = run.Error
        }).ToArray()
    };

    private static object BuildValidationPayload(CronValidationResult validation) => new
    {
        validation = new
        {
            isValid = validation.IsValid,
            issues = validation.Issues,
            supportedExpressionFormats = CronValidationResult.SupportedFormats
        }
    };

    private string DescribeNext(CronTask task)
        => task.NextOccurrence == null
            ? "not scheduled (the task is disabled or has no future occurrence)"
            : $"{_scheduleService.FormatInZone(task.NextOccurrence.Value, task.TimeZoneId)} ({task.TimeZoneId})";
}
