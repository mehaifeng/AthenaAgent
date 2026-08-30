using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;

namespace Athena.UI.ViewModels;

/// <summary>
/// 一条运行记录的显示投影。领域对象 <see cref="CronTaskRunRecord"/> 不做本地化也不发通知，
/// 所有面向界面的措辞都在这一层完成。
/// </summary>
public sealed partial class CronTaskRunItemViewModel : ObservableObject
{
    private readonly ILocalizationService? _localizationService;
    private readonly ICronScheduleService _scheduleService;
    private readonly string _timeZoneId;

    public CronTaskRunItemViewModel(
        CronTaskRunRecord record,
        string timeZoneId,
        ICronScheduleService scheduleService,
        ILocalizationService? localizationService)
    {
        Record = record;
        _timeZoneId = timeZoneId;
        _scheduleService = scheduleService;
        _localizationService = localizationService;
    }

    /// <summary>请求打开这次运行创建的会话。由 <see cref="TasksViewModel"/> 订阅并转交给导航边界。</summary>
    public event EventHandler? OpenConversationRequested;

    public CronTaskRunRecord Record { get; }

    [RelayCommand]
    private void OpenConversation()
    {
        if (CanOpenConversation) OpenConversationRequested?.Invoke(this, EventArgs.Empty);
    }

    public string RunId => Record.RunId;

    public string? ConversationId => Record.ConversationId;

    public string? HistoryId => Record.HistoryId;

    /// <summary>只有真正创建了会话的运行才能跳转；Skipped 的运行没有会话可去。</summary>
    public bool CanOpenConversation =>
        !string.IsNullOrWhiteSpace(Record.HistoryId) || !string.IsNullOrWhiteSpace(Record.ConversationId);

    public string StateText => Record.State switch
    {
        CronRunState.Queued => L("Cron.RunState.Queued", "Queued"),
        CronRunState.Running => L("Cron.RunState.Running", "Running"),
        CronRunState.Succeeded => L("Cron.RunState.Succeeded", "Succeeded"),
        CronRunState.Failed => L("Cron.RunState.Failed", "Failed"),
        CronRunState.Interrupted => L("Cron.RunState.Interrupted", "Interrupted"),
        CronRunState.Skipped => L("Cron.RunState.Skipped", "Skipped"),
        _ => Record.State.ToString()
    };

    public string StateIconKey => Record.State switch
    {
        CronRunState.Succeeded => "AthenaIconRunSucceeded",
        CronRunState.Failed or CronRunState.Interrupted => "AthenaIconRunFailed",
        CronRunState.Skipped => "AthenaIconRunSkipped",
        _ => "AthenaIconScheduledRun"
    };

    public bool IsFailure => Record.State is CronRunState.Failed or CronRunState.Interrupted;

    public string TriggerText => Record.Trigger == CronRunTrigger.Manual
        ? L("Cron.Trigger.Manual", "Manual")
        : L("Cron.Trigger.Scheduled", "Scheduled");

    /// <summary>展示时刻优先用"实际开始"，没有开始过就退回"计划时刻"，都没有则用完成时刻。</summary>
    public string TimestampText
    {
        get
        {
            var instant = Record.StartedAt ?? Record.ScheduledFor ?? Record.CompletedAt;
            return instant == null
                ? L("Cron.Run.NoTimestamp", "—")
                : _scheduleService.FormatInZone(instant.Value, _timeZoneId);
        }
    }

    public string DurationText
    {
        get
        {
            if (Record.StartedAt == null || Record.CompletedAt == null) return string.Empty;
            var duration = Record.CompletedAt.Value - Record.StartedAt.Value;
            if (duration < TimeSpan.Zero) return string.Empty;
            return duration.TotalMinutes >= 1
                ? string.Format(L("Cron.Run.DurationMinutes", "{0:0.0} min"), duration.TotalMinutes)
                : string.Format(L("Cron.Run.DurationSeconds", "{0:0.0} s"), duration.TotalSeconds);
        }
    }

    public string DetailText => !string.IsNullOrWhiteSpace(Record.Error)
        ? Record.Error!
        : Record.Note ?? string.Empty;

    public bool HasDetail => !string.IsNullOrWhiteSpace(DetailText);

    private string L(string key, string fallback)
        => _localizationService?.GetString(key, fallback) ?? fallback;
}
