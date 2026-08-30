using System.Text.Json.Serialization;

namespace Athena.UI.Models;

/// <summary>
/// 一次会话级执行的结果。Busy 已随"往当前会话插主动消息"的旧设计一并移除：
/// cron 每次触发都开新会话，结构上不存在"目标会话正忙"这种状态。
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<TaskExecutionOutcome>))]
public enum TaskExecutionOutcome
{
    [JsonStringEnumMemberName("succeeded")]
    Succeeded,

    [JsonStringEnumMemberName("failed")]
    Failed,

    [JsonStringEnumMemberName("interrupted")]
    Interrupted
}

public class TaskExecutionResult
{
    public TaskExecutionOutcome Outcome { get; init; }
    public string? Note { get; init; }

    public static TaskExecutionResult Succeeded(string? note = null) => new() { Outcome = TaskExecutionOutcome.Succeeded, Note = note };
    public static TaskExecutionResult Failed(string? note = null) => new() { Outcome = TaskExecutionOutcome.Failed, Note = note };
    public static TaskExecutionResult Interrupted(string? note = null) => new() { Outcome = TaskExecutionOutcome.Interrupted, Note = note };
}
