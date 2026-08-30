using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Athena.UI.Models;

/// <summary>一次运行是被计划触发的还是用户手动发起的。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CronRunTrigger>))]
public enum CronRunTrigger
{
    [JsonStringEnumMemberName("scheduled")]
    Scheduled,

    [JsonStringEnumMemberName("manual")]
    Manual
}

/// <summary>
/// 一次运行的生命周期状态。Queued/Running 是运行中状态，其余为终态。
/// 进程退出时仍处于 Running 的记录，在下次启动时收敛为 Interrupted，绝不自动重放。
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<CronRunState>))]
public enum CronRunState
{
    [JsonStringEnumMemberName("queued")]
    Queued,

    [JsonStringEnumMemberName("running")]
    Running,

    [JsonStringEnumMemberName("succeeded")]
    Succeeded,

    [JsonStringEnumMemberName("failed")]
    Failed,

    [JsonStringEnumMemberName("interrupted")]
    Interrupted,

    [JsonStringEnumMemberName("skipped")]
    Skipped
}

/// <summary>
/// 单次运行记录。触发的产物是一个全新会话，因此必须保留 ConversationId / HistoryId，
/// 否则任务页只能显示"成功"两个字而无法回溯到那次实际发生了什么。
/// </summary>
public sealed class CronTaskRunRecord
{
    public string RunId { get; set; } = Guid.NewGuid().ToString("N");

    public CronRunTrigger Trigger { get; set; } = CronRunTrigger.Scheduled;

    /// <summary>计划触发时刻（UTC）。手动运行为 null。</summary>
    public DateTimeOffset? ScheduledFor { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public CronRunState State { get; set; } = CronRunState.Queued;

    /// <summary>该次运行创建的会话 ID；跳过的运行没有会话。</summary>
    public string? ConversationId { get; set; }

    /// <summary>该次运行创建的历史条目 ID；用于从任务页跳转回会话。</summary>
    public string? HistoryId { get; set; }

    public string? Note { get; set; }

    public string? Error { get; set; }

    [JsonIgnore]
    public bool IsTerminal => State is not (CronRunState.Queued or CronRunState.Running);

    public CronTaskRunRecord Clone() => new()
    {
        RunId = RunId,
        Trigger = Trigger,
        ScheduledFor = ScheduledFor,
        StartedAt = StartedAt,
        CompletedAt = CompletedAt,
        State = State,
        ConversationId = ConversationId,
        HistoryId = HistoryId,
        Note = Note,
        Error = Error
    };
}

/// <summary>
/// cron 定时任务。纯领域对象：不继承 ObservableObject，不做本地化，不触碰 App.Services。
/// 所有时间以 UTC 的 <see cref="DateTimeOffset"/> 持久化；<see cref="TimeZoneId"/> 决定 cron
/// 表达式在哪个时区求值，同时也是 UI 显示所用的时区。
/// </summary>
public sealed class CronTask
{
    /// <summary>每个任务最多保留的运行记录条数。</summary>
    public const int MaxRunRecords = 20;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = string.Empty;

    /// <summary>触发时注入新会话的指令文本。</summary>
    public string Instruction { get; set; } = string.Empty;

    /// <summary>标准五段 cron 表达式（分 时 日 月 周）。</summary>
    public string CronExpression { get; set; } = string.Empty;

    public string TimeZoneId { get; set; } = TimeZoneInfo.Local.Id;

    /// <summary>只执行第一个计划 occurrence；被领取后立即停用，失败也不自动重试。</summary>
    public bool RunOnce { get; set; }

    /// <summary>成功完成后是否提示。失败始终提示，与此开关无关。</summary>
    public bool NotifyOnCompletion { get; set; } = true;

    /// <summary>新会话落入的工作区；null 或找不到时落入全局分组。</summary>
    public string? WorkspaceId { get; set; }

    public bool IsEnabled { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>下一次计划触发时刻（UTC）。这是缓存，真相是 cron 表达式本身。</summary>
    public DateTimeOffset? NextOccurrence { get; set; }

    public List<CronTaskRunRecord> RecentRuns { get; set; } = new();

    [JsonIgnore]
    public bool HasPendingRun => RecentRuns.Any(run => !run.IsTerminal);

    /// <summary>把新记录插到最前，并把总数裁到 <see cref="MaxRunRecords"/>。</summary>
    public void AddRun(CronTaskRunRecord record)
    {
        RecentRuns.Insert(0, record);
        TrimRuns();
    }

    public void TrimRuns()
    {
        if (RecentRuns.Count <= MaxRunRecords) return;
        RecentRuns.RemoveRange(MaxRunRecords, RecentRuns.Count - MaxRunRecords);
    }

    public CronTask Clone() => new()
    {
        Id = Id,
        Name = Name,
        Instruction = Instruction,
        CronExpression = CronExpression,
        TimeZoneId = TimeZoneId,
        RunOnce = RunOnce,
        NotifyOnCompletion = NotifyOnCompletion,
        WorkspaceId = WorkspaceId,
        IsEnabled = IsEnabled,
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
        NextOccurrence = NextOccurrence,
        RecentRuns = RecentRuns.Select(run => run.Clone()).ToList()
    };
}

/// <summary>创建/更新任务的输入。与 <see cref="CronTask"/> 分开，避免调用方顺手改运行记录。</summary>
public sealed class CronTaskDraft
{
    public string Name { get; set; } = string.Empty;

    public string Instruction { get; set; } = string.Empty;

    public string CronExpression { get; set; } = string.Empty;

    public string? TimeZoneId { get; set; }

    public bool RunOnce { get; set; }

    public bool NotifyOnCompletion { get; set; } = true;

    public string? WorkspaceId { get; set; }

    public bool IsEnabled { get; set; } = true;
}

/// <summary>调度器领取到的一次待执行运行。</summary>
public sealed class CronTaskClaim
{
    public required CronTask Task { get; init; }

    public required CronTaskRunRecord Run { get; init; }
}
