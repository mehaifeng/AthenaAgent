using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using Serilog;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Athena.UI.Services.Functions;

/// <summary>
/// 主动消息相关的 Function Calling 实现
/// </summary>
public class ProactiveMessagingFunctions
{
    private readonly ITaskScheduler _taskScheduler;
    private readonly IRecurrenceService _recurrenceService;
    private readonly ILogger _logger;

    public ProactiveMessagingFunctions(ITaskScheduler taskScheduler, IRecurrenceService recurrenceService, ILogger logger)
    {
        _taskScheduler = taskScheduler;
        _recurrenceService = recurrenceService;
        _logger = logger.ForContext<ProactiveMessagingFunctions>();
    }

    /// <summary>
    /// 安排主动消息
    /// </summary>
    /// <param name="scheduledTime">触发时间（如 "2024-02-10 08:00", "in 2 hours", "tomorrow morning"）</param>
    /// <param name="intent">任务意图（AI 对自己的提醒）</param>
    /// <param name="recurrence">结构化循环规则</param>
    /// <returns>操作结果</returns>
    public async Task<FunctionResult> ScheduleProactiveMessage(
        string scheduledTime,
        string intent,
        RecurrenceRuleInput? recurrence = null)
    {
        try
        {
            var triggerBoundary = ParseScheduleTime(scheduledTime);
            var validation = new RecurrenceValidationResult();
            var normalizedRecurrence = _recurrenceService.Normalize(recurrence, validation);
            validation = MergeValidation(validation, _recurrenceService.Validate(normalizedRecurrence));

            var normalizedTrigger = _recurrenceService.GetFirstTriggerTime(triggerBoundary, normalizedRecurrence, DateTime.Now);
            if (!normalizedTrigger.HasValue)
            {
                validation.Issues.Add(new RecurrenceValidationIssue
                {
                    Code = "trigger_not_in_future",
                    Message = "The first actual trigger time must be in the future."
                });
            }
            else if (normalizedTrigger.Value != triggerBoundary)
            {
                validation.Warnings.Add($"First actual trigger normalized to {normalizedTrigger.Value:yyyy-MM-dd HH:mm}.");
            }

            var recurrenceSummary = _recurrenceService.GetSummary(normalizedRecurrence);
            var validationData = new
            {
                isValid = validation.IsValid,
                issues = validation.Issues,
                warnings = validation.Warnings,
                supportedRecurrencePatterns = validation.SupportedRecurrencePatterns
            };

            if (!validation.IsValid || !normalizedTrigger.HasValue)
            {
                return FunctionResult.FailureResult(
                    "Task validation failed. Ask the user to choose a supported recurrence rule or a future trigger time.",
                    new
                    {
                        validation = validationData,
                        normalizedRecurrence,
                        normalizedTriggerTime = normalizedTrigger?.ToString("O"),
                        recurrenceSummary
                    });
            }

            var task = new ScheduledTask
            {
                Id = Guid.NewGuid().ToString(),
                TriggerTime = normalizedTrigger.Value,
                ScheduleBoundary = triggerBoundary,
                Intent = intent.Trim(),
                RecurrenceRule = normalizedRecurrence,
                CreatedAt = DateTime.Now,
                TaskType = TaskType.Proactive
            };

            await _taskScheduler.ScheduleAsync(task);

            _logger.Information("Function: scheduled proactive message {TaskId} at {TriggerTime}",
                task.Id, normalizedTrigger.Value);

            return FunctionResult.SuccessResult(
                $"Task scheduled for {normalizedTrigger:yyyy-MM-dd HH:mm}. Task ID: {task.Id}",
                new
                {
                    taskId = task.Id,
                    normalizedTriggerTime = normalizedTrigger.Value.ToString("O"),
                    normalizedRecurrence,
                    recurrenceSummary,
                    warnings = validation.Warnings,
                    validation = validationData
                });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to schedule proactive message");
            return FunctionResult.FailureResult($"安排失败: {ex.Message}",
                new
                {
                    validation = new
                    {
                        isValid = false,
                        issues = new[]
                        {
                            new RecurrenceValidationIssue
                            {
                                Code = "schedule_parse_failed",
                                Message = ex.Message
                            }
                        },
                        warnings = Array.Empty<string>(),
                        supportedRecurrencePatterns = new[]
                        {
                            "once",
                            "every N minutes",
                            "every N hours",
                            "every N days",
                            "every N weeks",
                            "every N weeks on specific weekdays"
                        }
                    }
                });
        }
    }

    /// <summary>
    /// 取消已安排的消息
    /// </summary>
    public async Task<FunctionResult> CancelScheduledMessage(string taskId)
    {
        try
        {
            var success = await _taskScheduler.CancelAsync(taskId);

            if (success)
            {
                _logger.Information("Function: cancelled task {TaskId}", taskId);
                return FunctionResult.SuccessResult("任务已取消");
            }

            return FunctionResult.FailureResult("Task not found.");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to cancel task");
            return FunctionResult.FailureResult($"取消失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 列出所有已安排的消息
    /// </summary>
    public async Task<FunctionResult> ListScheduledMessages()
    {
        try
        {
            var tasks = await _taskScheduler.GetUpcomingTasksAsync();

            var taskList = tasks.Select(t => new
            {
                taskId = t.Id,
                scheduledTime = t.TriggerTime.ToString("yyyy-MM-dd HH:mm"),
                intent = t.Intent,
                normalizedRecurrence = _recurrenceService.Normalize(t.RecurrenceRule),
                recurrenceSummary = _recurrenceService.GetSummary(t.RecurrenceRule),
                createdAt = t.CreatedAt.ToString("yyyy-MM-dd HH:mm"),
                taskType = t.TaskType.ToString(),
                lastExecutionAt = t.LastExecutionAt?.ToString("O"),
                lastExecutionOutcome = t.LastExecutionOutcome,
                lastExecutionNote = t.LastExecutionNote
            }).ToList();

            _logger.Information("Function: listed {Count} scheduled task(s)", taskList.Count);

            return FunctionResult.SuccessResult(
                $"共有 {taskList.Count} 个活动任务",
                taskList);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to list tasks");
            return FunctionResult.FailureResult($"查询失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 解析时间字符串
    /// </summary>
    private DateTime ParseScheduleTime(string timeString)
    {
        var input = timeString.Trim().ToLowerInvariant();

        if (input.StartsWith("in ", StringComparison.Ordinal))
        {
            return ParseRelativeTime(input[3..]);
        }

        if (input.Contains("tomorrow", StringComparison.Ordinal))
        {
            return ParseTomorrowTime(input);
        }

        if (DateTime.TryParse(timeString, out var result))
        {
            return result;
        }

        throw new ArgumentException($"无法解析时间: {timeString}");
    }

    private DateTime ParseRelativeTime(string relative)
    {
        var parts = relative.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && int.TryParse(parts[0], out var amount))
        {
            var unit = parts[1].ToLowerInvariant();
            return unit switch
            {
                "second" or "seconds" or "sec" => DateTime.Now.AddSeconds(amount),
                "minute" or "minutes" or "min" => DateTime.Now.AddMinutes(amount),
                "hour" or "hours" or "hr" => DateTime.Now.AddHours(amount),
                "day" or "days" => DateTime.Now.AddDays(amount),
                "week" or "weeks" => DateTime.Now.AddDays(amount * 7),
                _ => throw new ArgumentException($"未知的时间单位: {unit}")
            };
        }

        throw new ArgumentException($"无法解析相对时间: {relative}");
    }

    private DateTime ParseTomorrowTime(string input)
    {
        var tomorrow = DateTime.Today.AddDays(1);
        var timePart = input.Replace("tomorrow", "", StringComparison.Ordinal).Trim();
        if (!string.IsNullOrEmpty(timePart))
        {
            if (TimeSpan.TryParse(timePart, out var time))
            {
                return tomorrow + time;
            }

            if (timePart.Contains("morning", StringComparison.Ordinal))
            {
                return tomorrow.AddHours(9);
            }

            if (timePart.Contains("afternoon", StringComparison.Ordinal))
            {
                return tomorrow.AddHours(14);
            }

            if (timePart.Contains("evening", StringComparison.Ordinal))
            {
                return tomorrow.AddHours(18);
            }
        }

        return tomorrow.AddHours(9);
    }

    private static RecurrenceValidationResult MergeValidation(RecurrenceValidationResult left, RecurrenceValidationResult right)
    {
        foreach (var issue in right.Issues)
        {
            left.Issues.Add(issue);
        }

        foreach (var warning in right.Warnings)
        {
            left.Warnings.Add(warning);
        }

        foreach (var pattern in right.SupportedRecurrencePatterns.Where(p => !left.SupportedRecurrencePatterns.Contains(p)))
        {
            left.SupportedRecurrencePatterns.Add(pattern);
        }

        return left;
    }
}
