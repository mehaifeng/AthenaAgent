using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Athena.UI.Services.Functions;

/// <summary>
/// 主动消息相关的 Function Calling 实现
/// </summary>
public class ProactiveMessagingFunctions
{
    private readonly ITaskScheduler _taskScheduler;
    private readonly ILogger _logger;

    public ProactiveMessagingFunctions(ITaskScheduler taskScheduler, ILogger logger)
    {
        _taskScheduler = taskScheduler;
        _logger = logger.ForContext<ProactiveMessagingFunctions>();
    }

    /// <summary>
    /// 安排主动消息
    /// </summary>
    /// <param name="scheduledTime">触发时间（如 "2024-02-10 08:00", "in 2 hours", "tomorrow morning"）</param>
    /// <param name="intent">任务意图（AI 对自己的提醒）</param>
    /// <param name="recurrence">循环模式（none, daily, weekly, every N days）</param>
    /// <returns>操作结果</returns>
    public async Task<FunctionResult> ScheduleProactiveMessage(
        string scheduledTime,
        string intent,
        string recurrence = "none")
    {
        try
        {
            var triggerTime = ParseScheduleTime(scheduledTime);

            if (triggerTime <= DateTime.Now)
            {
                return FunctionResult.FailureResult("触发时间必须是未来时间");
            }

            var task = new ScheduledTask
            {
                Id = Guid.NewGuid().ToString(),
                TriggerTime = triggerTime,
                Intent = intent,
                Recurrence = NormalizeRecurrence(recurrence),
                CreatedAt = DateTime.Now
            };

            await _taskScheduler.ScheduleAsync(task);

            _logger.Information("Function: 安排主动消息 {TaskId} 于 {TriggerTime}",
                task.Id, triggerTime);

            return FunctionResult.SuccessResult(
                $"已安排于 {triggerTime:yyyy-MM-dd HH:mm} 触发。任务ID: {task.Id}",
                new { taskId = task.Id, triggerTime = triggerTime.ToString("O") });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "安排主动消息失败");
            return FunctionResult.FailureResult($"安排失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 取消已安排的消息
    /// </summary>
    /// <param name="taskId">任务 ID</param>
    /// <returns>操作结果</returns>
    public async Task<FunctionResult> CancelScheduledMessage(string taskId)
    {
        try
        {
            var success = await _taskScheduler.CancelAsync(taskId);

            if (success)
            {
                _logger.Information("Function: 取消任务 {TaskId}", taskId);
                return FunctionResult.SuccessResult("任务已取消");
            }
            else
            {
                return FunctionResult.FailureResult("未找到该任务");
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "取消任务失败");
            return FunctionResult.FailureResult($"取消失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 列出所有已安排的消息
    /// </summary>
    /// <returns>任务列表</returns>
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
                recurrence = t.Recurrence,
                createdAt = t.CreatedAt.ToString("yyyy-MM-dd HH:mm")
            }).ToList();

            _logger.Information("Function: 列出 {Count} 个计划任务", taskList.Count);

            return FunctionResult.SuccessResult(
                $"共有 {taskList.Count} 个待执行任务",
                taskList);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "列出任务失败");
            return FunctionResult.FailureResult($"查询失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 解析时间字符串
    /// </summary>
    private DateTime ParseScheduleTime(string timeString)
    {
        var input = timeString.Trim().ToLower();

        // 尝试解析相对时间
        if (input.StartsWith("in "))
        {
            return ParseRelativeTime(input[3..]);
        }

        // 尝试解析特殊关键词
        if (input.Contains("tomorrow"))
        {
            return ParseTomorrowTime(input);
        }

        // 尝试直接解析日期时间
        if (DateTime.TryParse(timeString, out var result))
        {
            return result;
        }

        throw new ArgumentException($"无法解析时间: {timeString}");
    }

    /// <summary>
    /// 解析相对时间（如 "2 hours", "30 minutes", "30 seconds"）
    /// </summary>
    private DateTime ParseRelativeTime(string relative)
    {
        var parts = relative.Trim().Split(' ');
        if (parts.Length >= 2 && int.TryParse(parts[0], out var amount))
        {
            var unit = parts[1].ToLower();
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

    /// <summary>
    /// 解析 "tomorrow" 相关时间
    /// </summary>
    private DateTime ParseTomorrowTime(string input)
    {
        var tomorrow = DateTime.Today.AddDays(1);

        // 提取时间部分
        var timePart = input.Replace("tomorrow", "").Trim();
        if (!string.IsNullOrEmpty(timePart))
        {
            if (TimeSpan.TryParse(timePart, out var time))
            {
                return tomorrow + time;
            }

            // 尝试解析 "morning" = 9:00, "afternoon" = 14:00, "evening" = 18:00
            if (timePart.Contains("morning"))
                return tomorrow.AddHours(9);
            if (timePart.Contains("afternoon"))
                return tomorrow.AddHours(14);
            if (timePart.Contains("evening"))
                return tomorrow.AddHours(18);
        }

        // 默认明天早上 9 点
        return tomorrow.AddHours(9);
    }

    /// <summary>
    /// 标准化循环模式
    /// </summary>
    private string NormalizeRecurrence(string recurrence)
    {
        var normalized = recurrence.Trim().ToLower();

        return normalized switch
        {
            "none" or "once" or "one-time" or "一次性" => "none",
            "daily" or "every day" or "每天" => "daily",
            "weekly" or "every week" or "每周" => "weekly",
            _ when normalized.StartsWith("every ") => normalized,
            _ => "none"
        };
    }
}
