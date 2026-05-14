using System;
using System.Text.Json.Serialization;

namespace Athena.UI.Models;

[JsonConverter(typeof(JsonStringEnumConverter<TaskExecutionOutcome>))]
public enum TaskExecutionOutcome
{
    [JsonStringEnumMemberName("succeeded")]
    Succeeded,

    [JsonStringEnumMemberName("failed")]
    Failed,

    [JsonStringEnumMemberName("interrupted")]
    Interrupted,

    [JsonStringEnumMemberName("busy")]
    Busy
}

public class TaskExecutionResult
{
    public TaskExecutionOutcome Outcome { get; init; }
    public string? Note { get; init; }

    public static TaskExecutionResult Succeeded(string? note = null) => new() { Outcome = TaskExecutionOutcome.Succeeded, Note = note };
    public static TaskExecutionResult Failed(string? note = null) => new() { Outcome = TaskExecutionOutcome.Failed, Note = note };
    public static TaskExecutionResult Interrupted(string? note = null) => new() { Outcome = TaskExecutionOutcome.Interrupted, Note = note };
    public static TaskExecutionResult Busy(string? note = null) => new() { Outcome = TaskExecutionOutcome.Busy, Note = note };
}
