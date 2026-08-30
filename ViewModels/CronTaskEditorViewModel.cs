using Athena.UI.Models;
using Athena.UI.Services.Cron;
using Athena.UI.Services.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Athena.UI.ViewModels;

/// <summary>编辑器里的一个常用预设：选中即把表达式填进输入框。</summary>
public sealed class CronPresetOption
{
    public required string DisplayName { get; init; }

    /// <summary>null 表示"自定义"，此时表达式输入框保持用户当前内容。</summary>
    public string? Expression { get; init; }

    public override string ToString() => DisplayName;
}

/// <summary>
/// 创建与编辑共用的 cron 任务编辑器。
///
/// 编辑器的价值几乎全在"未来 5 次触发时间"那一栏：cron 表达式本身不可读，
/// 这份预览是用户唯一能确认自己没写错的手段，因此它必须随每一次输入实时重算。
/// </summary>
public sealed partial class CronTaskEditorViewModel : ViewModelBase, IDisposable
{
    public const int PreviewCount = 5;

    private readonly ICronScheduleService _scheduleService;
    private readonly ILocalizationService? _localizationService;
    private bool _disposed;
    private bool _suppressPresetSync;

    /// <summary>非 null 时是编辑模式，保存会走 UpdateAsync。</summary>
    public string? EditingTaskId { get; private set; }

    public bool IsEditing => !string.IsNullOrEmpty(EditingTaskId);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string _name = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string _instruction = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string _cronExpression = "0 9 * * *";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string _timeZoneId = TimeZoneInfo.Local.Id;

    [ObservableProperty]
    private bool _runOnce;

    [ObservableProperty]
    private bool _notifyOnCompletion = true;

    [ObservableProperty]
    private bool _isEnabled = true;

    [ObservableProperty]
    private WorkspaceProfile? _selectedWorkspace;

    [ObservableProperty]
    private CronPresetOption? _selectedPreset;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasValidationError))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string _validationError = string.Empty;

    [ObservableProperty]
    private string _scheduleDescription = string.Empty;

    [ObservableProperty]
    private bool _isAdvancedExpanded;

    public ObservableCollection<CronPresetOption> Presets { get; } = new();

    public ObservableCollection<string> UpcomingRuns { get; } = new();

    public ObservableCollection<WorkspaceProfile> AvailableWorkspaces { get; } = new();

    public ObservableCollection<string> AvailableTimeZoneIds { get; } = new();

    public bool HasValidationError => !string.IsNullOrWhiteSpace(ValidationError);

    public bool HasUpcomingRuns => UpcomingRuns.Count > 0;

    public bool CanSave =>
        !HasValidationError
        && !string.IsNullOrWhiteSpace(Instruction)
        && !string.IsNullOrWhiteSpace(CronExpression);

    public string SaveButtonText => IsEditing
        ? L("Cron.Editor.Save", "Save changes")
        : L("Cron.Editor.Create", "Create task");

    public CronTaskEditorViewModel() : this(new CronScheduleService(), null) { }

    public CronTaskEditorViewModel(ICronScheduleService scheduleService, ILocalizationService? localizationService)
    {
        _scheduleService = scheduleService;
        _localizationService = localizationService;

        RebuildPresets();
        LoadTimeZones();

        if (_localizationService != null)
        {
            _localizationService.LanguageChanged += OnLanguageChanged;
        }

        Recalculate();
    }

    /// <summary>装载一个既有任务进入编辑模式。</summary>
    public void LoadForEdit(CronTask task)
    {
        EditingTaskId = task.Id;
        _suppressPresetSync = true;
        Name = task.Name;
        Instruction = task.Instruction;
        CronExpression = task.CronExpression;
        TimeZoneId = task.TimeZoneId;
        RunOnce = task.RunOnce;
        NotifyOnCompletion = task.NotifyOnCompletion;
        IsEnabled = task.IsEnabled;
        SelectedWorkspace = AvailableWorkspaces.FirstOrDefault(workspace => workspace.Id == task.WorkspaceId);
        _suppressPresetSync = false;
        SyncPresetToExpression();
        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(SaveButtonText));
        Recalculate();
    }

    /// <summary>清空成"新建"状态。</summary>
    public void ResetToNew()
    {
        EditingTaskId = null;
        _suppressPresetSync = true;
        Name = string.Empty;
        Instruction = string.Empty;
        CronExpression = "0 9 * * *";
        TimeZoneId = TimeZoneInfo.Local.Id;
        RunOnce = false;
        NotifyOnCompletion = true;
        IsEnabled = true;
        SelectedWorkspace = null;
        _suppressPresetSync = false;
        SyncPresetToExpression();
        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(SaveButtonText));
        Recalculate();
    }

    public void SetAvailableWorkspaces(IEnumerable<WorkspaceProfile> workspaces, string? selectedWorkspaceId = null)
    {
        AvailableWorkspaces.Clear();
        foreach (var workspace in workspaces) AvailableWorkspaces.Add(workspace);
        SelectedWorkspace = string.IsNullOrWhiteSpace(selectedWorkspaceId)
            ? null
            : AvailableWorkspaces.FirstOrDefault(workspace => workspace.Id == selectedWorkspaceId);
    }

    public CronTaskDraft BuildDraft() => new()
    {
        Name = Name,
        Instruction = Instruction,
        CronExpression = CronExpression,
        TimeZoneId = TimeZoneId,
        RunOnce = RunOnce,
        NotifyOnCompletion = NotifyOnCompletion,
        WorkspaceId = SelectedWorkspace?.Id,
        IsEnabled = IsEnabled
    };

    /// <summary>把服务端返回的结构化校验结果显示出来（本地校验之外的兜底）。</summary>
    public void ApplyValidation(CronValidationResult validation)
    {
        ValidationError = validation.IsValid ? string.Empty : validation.FirstMessage;
    }

    partial void OnCronExpressionChanged(string value)
    {
        SyncPresetToExpression();
        Recalculate();
    }

    partial void OnTimeZoneIdChanged(string value) => Recalculate();

    partial void OnInstructionChanged(string value) => Recalculate();

    partial void OnSelectedPresetChanged(CronPresetOption? value)
    {
        if (_suppressPresetSync || value?.Expression == null) return;
        if (string.Equals(CronExpression, value.Expression, StringComparison.Ordinal)) return;
        CronExpression = value.Expression;
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        RebuildPresets();
        SyncPresetToExpression();
        Recalculate();
        OnPropertyChanged(nameof(SaveButtonText));
    }

    /// <summary>实时重算：校验 → 人话说明 → 未来 5 次触发。三者必须一起更新，否则会互相矛盾。</summary>
    private void Recalculate()
    {
        var validation = _scheduleService.Validate(CronExpression, TimeZoneId);
        UpcomingRuns.Clear();

        if (!validation.IsValid)
        {
            ValidationError = validation.FirstMessage;
            ScheduleDescription = string.Empty;
            OnPropertyChanged(nameof(HasUpcomingRuns));
            OnPropertyChanged(nameof(CanSave));
            return;
        }

        ScheduleDescription = _scheduleService.Describe(validation.NormalizedExpression);

        var occurrences = _scheduleService.Preview(
            validation.NormalizedExpression!,
            validation.NormalizedTimeZoneId!,
            DateTimeOffset.UtcNow,
            PreviewCount);

        foreach (var occurrence in occurrences)
        {
            UpcomingRuns.Add(_scheduleService.FormatInZone(occurrence, validation.NormalizedTimeZoneId));
        }

        ValidationError = occurrences.Count == 0
            ? L("Cron.Editor.NoUpcoming", "This expression has no future occurrence.")
            : string.IsNullOrWhiteSpace(Instruction)
                ? L("Cron.Editor.InstructionRequired", "An instruction is required: it is what the new session is asked to do.")
                : string.Empty;

        OnPropertyChanged(nameof(HasUpcomingRuns));
        OnPropertyChanged(nameof(CanSave));
    }

    private void RebuildPresets()
    {
        var previous = SelectedPreset?.Expression;
        Presets.Clear();
        Presets.Add(new CronPresetOption { DisplayName = L("Cron.Preset.DailyMorning", "Every day at 09:00"), Expression = "0 9 * * *" });
        Presets.Add(new CronPresetOption { DisplayName = L("Cron.Preset.Weekdays", "Weekdays at 09:00"), Expression = "0 9 * * 1-5" });
        Presets.Add(new CronPresetOption { DisplayName = L("Cron.Preset.WeeklyMonday", "Every Monday at 09:00"), Expression = "0 9 * * 1" });
        Presets.Add(new CronPresetOption { DisplayName = L("Cron.Preset.Hourly", "Every hour"), Expression = "0 * * * *" });
        Presets.Add(new CronPresetOption { DisplayName = L("Cron.Preset.Every30Minutes", "Every 30 minutes"), Expression = "*/30 * * * *" });
        Presets.Add(new CronPresetOption { DisplayName = L("Cron.Preset.MonthlyFirst", "1st of the month at 09:00"), Expression = "0 9 1 * *" });
        Presets.Add(new CronPresetOption { DisplayName = L("Cron.Preset.Custom", "Custom expression"), Expression = null });

        _suppressPresetSync = true;
        SelectedPreset = Presets.FirstOrDefault(preset => preset.Expression == previous) ?? Presets[^1];
        _suppressPresetSync = false;
    }

    private void SyncPresetToExpression()
    {
        var normalized = CronScheduleService.NormalizeExpression(CronExpression);
        var match = Presets.FirstOrDefault(preset =>
            preset.Expression != null && string.Equals(preset.Expression, normalized, StringComparison.Ordinal));

        _suppressPresetSync = true;
        SelectedPreset = match ?? Presets.LastOrDefault();
        _suppressPresetSync = false;
    }

    private void LoadTimeZones()
    {
        AvailableTimeZoneIds.Clear();
        try
        {
            foreach (var zone in TimeZoneInfo.GetSystemTimeZones().Select(zone => zone.Id).OrderBy(id => id, StringComparer.Ordinal))
            {
                AvailableTimeZoneIds.Add(zone);
            }
        }
        catch (Exception)
        {
            // 极少数裁剪过的运行时读不到时区库；至少保证本机时区可选，编辑器仍然可用。
        }

        if (!AvailableTimeZoneIds.Contains(TimeZoneInfo.Local.Id))
        {
            AvailableTimeZoneIds.Insert(0, TimeZoneInfo.Local.Id);
        }
    }

    private string L(string key, string fallback)
        => _localizationService?.GetString(key, fallback) ?? fallback;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_localizationService != null)
        {
            _localizationService.LanguageChanged -= OnLanguageChanged;
        }
    }
}
