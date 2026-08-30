using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Athena.UI.Models;

/// <summary>一条结构化校验失败原因。Code 供调用方（含模型）分支，Message 给人看。</summary>
public sealed class CronValidationIssue
{
    public CronValidationIssue() { }

    public CronValidationIssue(string code, string message)
    {
        Code = code;
        Message = message;
    }

    public string Code { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// cron 表达式 + 时区的校验结果。校验通过时携带归一化后的表达式与时区，
/// 调用方应当使用归一化结果而不是原始输入。
/// </summary>
public sealed class CronValidationResult
{
    /// <summary>本实现只接受标准五段 cron，这里是给模型看的可用形态说明。</summary>
    public static readonly string[] SupportedFormats =
    [
        "minute hour day-of-month month day-of-week (5 fields, e.g. \"0 9 * * 1-5\")",
        "step values (\"*/15 * * * *\")",
        "lists and ranges (\"0 9,18 * * *\", \"0 9 * * 1-5\")"
    ];

    public List<CronValidationIssue> Issues { get; set; } = new();

    [JsonIgnore]
    public bool IsValid => Issues.Count == 0;

    /// <summary>归一化后的表达式（折叠多余空白）。校验失败时为 null。</summary>
    public string? NormalizedExpression { get; set; }

    /// <summary>归一化后的时区 ID。校验失败时为 null。</summary>
    public string? NormalizedTimeZoneId { get; set; }

    public string[] SupportedExpressionFormats => SupportedFormats;

    public CronValidationResult AddIssue(string code, string message)
    {
        Issues.Add(new CronValidationIssue(code, message));
        return this;
    }

    public string FirstMessage => Issues.Count == 0 ? string.Empty : Issues[0].Message;
}

/// <summary>任务写入操作的结果：要么带回落库后的任务，要么带回结构化失败原因。</summary>
public sealed class CronTaskMutationResult
{
    public bool Success { get; init; }

    public CronTask? Task { get; init; }

    public CronValidationResult Validation { get; init; } = new();

    public static CronTaskMutationResult Ok(CronTask task, CronValidationResult validation)
        => new() { Success = true, Task = task, Validation = validation };

    public static CronTaskMutationResult Invalid(CronValidationResult validation)
        => new() { Success = false, Validation = validation };

    public static CronTaskMutationResult NotFound(string taskId)
        => new()
        {
            Success = false,
            Validation = new CronValidationResult().AddIssue(
                "task_not_found",
                $"No cron task with id '{taskId}'.")
        };
}

/// <summary>存储层加载结果。损坏的单条记录被隔离，绝不阻止应用启动。</summary>
public sealed class CronTaskLoadResult
{
    public List<CronTask> Tasks { get; init; } = new();

    /// <summary>被跳过的损坏记录数。</summary>
    public int CorruptedCount { get; init; }

    /// <summary>整个文件不可解析（不是单条损坏）。</summary>
    public bool FileUnreadable { get; init; }

    public IEnumerable<string> TaskIds => Tasks.Select(task => task.Id);
}

/// <summary>任务集合变化的不可变通知。VM 收到后在 UI 线程重建投影。</summary>
public sealed class CronTaskListChangedEventArgs : EventArgs
{
    public required IReadOnlyList<CronTask> Tasks { get; init; }
}
