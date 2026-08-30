using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.Linq;

namespace Athena.UI.ViewModels;

/// <summary>
/// 任务卡片的显示投影。持有的 <see cref="CronTask"/> 是服务给出的不可变快照：
/// 这里绝不就地修改它，任何变更都必须经由 <see cref="ICronTaskService"/> 走一圈再回来。
/// </summary>
public sealed partial class CronTaskItemViewModel : ObservableObject
{
    private readonly ICronScheduleService _scheduleService;
    private readonly ILocalizationService? _localizationService;

    public CronTaskItemViewModel(
        CronTask task,
        ICronScheduleService scheduleService,
        ILocalizationService? localizationService)
    {
        Task = task;
        _scheduleService = scheduleService;
        _localizationService = localizationService;
        Runs = new ObservableCollection<CronTaskRunItemViewModel>(
            task.RecentRuns.Select(run => new CronTaskRunItemViewModel(run, task.TimeZoneId, scheduleService, localizationService)));
    }

    /// <summary>
    /// 卡片上的四个动作走事件而不是 XAML 里的 <c>$parent</c> 反查：
    /// item 模板里跨 DataContext 强转父级视图模型既脆弱又难读，本仓库既有的
    /// <see cref="ConversationSessionItemViewModel"/> 就是用事件解决同一问题的。
    /// </summary>
    public event EventHandler? EditRequested;

    public event EventHandler? ToggleEnabledRequested;

    public event EventHandler? RunNowRequested;

    public event EventHandler? DeleteRequested;

    public CronTask Task { get; }

    [RelayCommand]
    private void Edit() => EditRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void ToggleEnabled() => ToggleEnabledRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void RunNow() => RunNowRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void Delete() => DeleteRequested?.Invoke(this, EventArgs.Empty);

    public string Id => Task.Id;

    public string Name => Task.Name;

    public string Instruction => Task.Instruction;

    public string CronExpression => Task.CronExpression;

    public string TimeZoneId => Task.TimeZoneId;

    public bool IsEnabled => Task.IsEnabled;

    public bool RunOnce => Task.RunOnce;

    public bool NotifyOnCompletion => Task.NotifyOnCompletion;

    public string? WorkspaceId => Task.WorkspaceId;

    public ObservableCollection<CronTaskRunItemViewModel> Runs { get; }

    public bool HasRuns => Runs.Count > 0;

    public string ScheduleDescription => _scheduleService.Describe(Task.CronExpression);

    public string NextRunText => Task.NextOccurrence == null
        ? (Task.IsEnabled
            ? L("Cron.NextRun.None", "No upcoming run")
            : L("Cron.NextRun.Paused", "Paused"))
        : _scheduleService.FormatInZone(Task.NextOccurrence.Value, Task.TimeZoneId);

    public string StatusText => !Task.IsEnabled
        ? L("Cron.Status.Paused", "Paused")
        : Task.HasPendingRun
            ? L("Cron.Status.Running", "Running")
            : L("Cron.Status.Active", "Active");

    public bool HasRecentFailure =>
        Runs.FirstOrDefault()?.IsFailure == true;

    public string RunOnceText => Task.RunOnce
        ? L("Cron.RunOnce.Badge", "Once")
        : string.Empty;

    public bool ShowRunOnceBadge => Task.RunOnce;

    public string ToggleEnabledText => Task.IsEnabled
        ? L("Cron.Action.Pause", "Pause")
        : L("Cron.Action.Resume", "Resume");

    public string ToggleEnabledIconKey => Task.IsEnabled ? "AthenaIconTaskPause" : "AthenaIconTaskResume";

    private string L(string key, string fallback)
        => _localizationService?.GetString(key, fallback) ?? fallback;
}
