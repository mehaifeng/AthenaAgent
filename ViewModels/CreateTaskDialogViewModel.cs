using Athena.UI.Models;
using Athena.UI.Services;
using Athena.UI.Services.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;

namespace Athena.UI.ViewModels;

public partial class CreateTaskDialogViewModel : ViewModelBase
{
    private readonly ILocalizationService? _localizationService;
    private readonly IRecurrenceService _recurrenceService;
    private RecurrenceRule _currentRule = RecurrenceRule.None();
    private DateTime? _previewTriggerTime;
    private bool _disposed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FullTriggerTime))]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    private DateTime _triggerDate = DateTime.Today.AddDays(1);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FullTriggerTime))]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    private TimeSpan _triggerTime = new(9, 0, 0);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    private string _intent = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TaskTypeDescription))]
    private TaskType _taskType = TaskType.Proactive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomRecurrenceVisible))]
    [NotifyPropertyChangedFor(nameof(IsIntervalEditorVisible))]
    [NotifyPropertyChangedFor(nameof(IsWeeklyDaysEditorVisible))]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    private int _selectedRecurrencePresetIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIntervalEditorVisible))]
    [NotifyPropertyChangedFor(nameof(IsWeeklyDaysEditorVisible))]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    private int _selectedCustomModeIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    private int _customInterval = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    private int _selectedIntervalUnitIndex = 2;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasValidationError))]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    private string _validationError = string.Empty;

    public ObservableCollection<string> RecurrencePresetDisplayNames { get; }
    public ObservableCollection<string> CustomModeDisplayNames { get; }
    public ObservableCollection<string> IntervalUnitDisplayNames { get; }
    public ObservableCollection<string> TaskTypeDisplayNames { get; }
    public ObservableCollection<WeekdaySelectionItem> WeekdaySelections { get; }

    public CreateTaskDialogViewModel() : this(null, null) { }

    public CreateTaskDialogViewModel(ILocalizationService? localizationService, IRecurrenceService? recurrenceService = null)
    {
        _localizationService = localizationService;
        _recurrenceService = recurrenceService
            ?? App.Services?.GetService(typeof(IRecurrenceService)) as IRecurrenceService
            ?? new RecurrenceService(localizationService);

        RecurrencePresetDisplayNames = [];
        CustomModeDisplayNames = [];
        IntervalUnitDisplayNames = [];
        TaskTypeDisplayNames = [];

        WeekdaySelections =
        [
            CreateWeekday(DayOfWeek.Monday),
            CreateWeekday(DayOfWeek.Tuesday),
            CreateWeekday(DayOfWeek.Wednesday),
            CreateWeekday(DayOfWeek.Thursday),
            CreateWeekday(DayOfWeek.Friday),
            CreateWeekday(DayOfWeek.Saturday),
            CreateWeekday(DayOfWeek.Sunday)
        ];

        foreach (var item in WeekdaySelections)
        {
            item.PropertyChanged += OnWeekdaySelectionChanged;
        }

        RebuildLocalizedNames();

        if (_localizationService != null)
        {
            _localizationService.LanguageChanged += OnLanguageChanged;
        }

        RecalculatePreview();
    }

    /// <summary>
    /// 重建全部本地化显示名称（语言切换时调用）。
    /// </summary>
    private void RebuildLocalizedNames()
    {
        RecurrencePresetDisplayNames.Clear();
        RecurrencePresetDisplayNames.Add(GetLocalizedString("Recurrence.NoneDisplay", "Once"));
        RecurrencePresetDisplayNames.Add(GetLocalizedString("Recurrence.DailyDisplay", "Every day"));
        RecurrencePresetDisplayNames.Add(GetLocalizedString("Recurrence.WorkdaysDisplay", "Weekdays"));
        RecurrencePresetDisplayNames.Add(GetLocalizedString("Recurrence.WeeklyPresetDisplay", "Every week"));
        RecurrencePresetDisplayNames.Add(GetLocalizedString("Recurrence.CustomDisplay", "Custom"));

        CustomModeDisplayNames.Clear();
        CustomModeDisplayNames.Add(GetLocalizedString("TaskDialog.Custom.IntervalMode", "Interval"));
        CustomModeDisplayNames.Add(GetLocalizedString("TaskDialog.Custom.WeeklyDaysMode", "Specific weekdays"));

        IntervalUnitDisplayNames.Clear();
        IntervalUnitDisplayNames.Add(GetLocalizedString("Recurrence.Unit.Minutes", "minutes"));
        IntervalUnitDisplayNames.Add(GetLocalizedString("Recurrence.Unit.Hours", "hours"));
        IntervalUnitDisplayNames.Add(GetLocalizedString("Recurrence.Unit.Days", "days"));
        IntervalUnitDisplayNames.Add(GetLocalizedString("Recurrence.Unit.Weeks", "weeks"));

        TaskTypeDisplayNames.Clear();
        TaskTypeDisplayNames.Add(GetLocalizedString("TaskType.Proactive", "Foreground (Proactive Message)"));
        TaskTypeDisplayNames.Add(GetLocalizedString("TaskType.Background", "Background (Silent)"));

        foreach (var item in WeekdaySelections)
        {
            item.DisplayName = GetWeekdayLabel(item.DayOfWeek);
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        RebuildLocalizedNames();
        RecalculatePreview();
        OnPropertyChanged(nameof(TaskTypeDescription));
        OnPropertyChanged(nameof(MinuteSchedulingHint));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_localizationService != null)
        {
            _localizationService.LanguageChanged -= OnLanguageChanged;
        }
        foreach (var item in WeekdaySelections)
        {
            item.PropertyChanged -= OnWeekdaySelectionChanged;
        }
    }

    public bool IsCustomRecurrenceVisible => SelectedRecurrencePresetIndex == 4;

    public bool IsIntervalEditorVisible => IsCustomRecurrenceVisible && SelectedCustomModeIndex == 0;

    public bool IsWeeklyDaysEditorVisible => IsCustomRecurrenceVisible && SelectedCustomModeIndex == 1;

    public bool ShowMinuteSchedulingHint =>
        _currentRule.Mode == RecurrenceMode.Interval
        && _currentRule.Unit == RecurrenceUnit.Minute;

    public bool HasValidationError => !string.IsNullOrWhiteSpace(ValidationError);

    public string TaskTypeDescription => TaskType switch
    {
        TaskType.Proactive => GetLocalizedString("TaskDialog.ProactiveHint",
            "Triggers at the scheduled time and sends a proactive message to the chat interface."),
        TaskType.Background => GetLocalizedString("TaskDialog.BackgroundHint",
            "Triggers at the scheduled time and executes silently in the background without disturbing you."),
        _ => string.Empty
    };

    public string MinuteSchedulingHint => GetLocalizedString(
        "TaskDialog.MinuteHint",
        "High-frequency foreground tasks run serially. One-time tasks wait. Recurring tasks skip collisions.");

    public DateTime FullTriggerTime => TriggerDate.Date + TriggerTime;

    public string RecurrenceSummaryPreview => _recurrenceService.GetSummary(_currentRule);

    public string FirstTriggerPreview => _previewTriggerTime?.ToString("yyyy-MM-dd HH:mm")
        ?? GetLocalizedString("TaskDialog.FirstTriggerUnavailable", "Unavailable");

    public ScheduledTask? Result { get; private set; }

    public bool IsConfirmed { get; private set; }

    public bool CanConfirm =>
        !string.IsNullOrWhiteSpace(Intent)
        && !HasValidationError
        && _previewTriggerTime.HasValue;

    public int SelectedTaskTypeIndex
    {
        get => TaskType == TaskType.Background ? 1 : 0;
        set
        {
            TaskType = value == 1 ? TaskType.Background : TaskType.Proactive;
        }
    }

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void Confirm()
    {
        if (!TryCreateResult(out var result))
        {
            return;
        }

        Result = result;
        IsConfirmed = true;
        RequestClose?.Invoke();
    }

    public bool TryCreateResult(out ScheduledTask? result)
    {
        RecalculatePreview();
        if (!CanConfirm || !_previewTriggerTime.HasValue)
        {
            result = null;
            return false;
        }

        result = new ScheduledTask
        {
            Id = Guid.NewGuid().ToString(),
            TriggerTime = _previewTriggerTime.Value,
            ScheduleBoundary = FullTriggerTime,
            Intent = Intent.Trim(),
            RecurrenceRule = _currentRule.Clone(),
            CreatedAt = DateTime.Now,
            TaskType = TaskType
        };
        return true;
    }

    [RelayCommand]
    private void Cancel()
    {
        IsConfirmed = false;
        Result = null;
        RequestClose?.Invoke();
    }

    public Action? RequestClose { get; set; }

    partial void OnTriggerDateChanged(DateTime value) => RecalculatePreview();
    partial void OnTriggerTimeChanged(TimeSpan value) => RecalculatePreview();
    partial void OnIntentChanged(string value)
    {
        OnPropertyChanged(nameof(CanConfirm));
        ConfirmCommand.NotifyCanExecuteChanged();
    }
    partial void OnTaskTypeChanged(TaskType value) => OnPropertyChanged(nameof(SelectedTaskTypeIndex));
    partial void OnSelectedRecurrencePresetIndexChanged(int value) => RecalculatePreview();
    partial void OnSelectedCustomModeIndexChanged(int value) => RecalculatePreview();
    partial void OnCustomIntervalChanged(int value) => RecalculatePreview();
    partial void OnSelectedIntervalUnitIndexChanged(int value) => RecalculatePreview();

    private void OnWeekdaySelectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WeekdaySelectionItem.IsSelected))
        {
            RecalculatePreview();
        }
    }

    private void RecalculatePreview()
    {
        _currentRule = BuildCurrentRule();
        var validation = _recurrenceService.Validate(_currentRule);

        if (!validation.IsValid)
        {
            _previewTriggerTime = null;
            ValidationError = validation.Issues.First().Message;
        }
        else
        {
            _previewTriggerTime = _recurrenceService.GetFirstTriggerTime(FullTriggerTime, _currentRule, DateTime.Now);
            ValidationError = _previewTriggerTime.HasValue
                ? string.Empty
                : GetLocalizedString("TaskDialog.Validation.FutureTrigger", "The first actual trigger time must be in the future.");
        }

        OnPropertyChanged(nameof(RecurrenceSummaryPreview));
        OnPropertyChanged(nameof(FirstTriggerPreview));
        OnPropertyChanged(nameof(ShowMinuteSchedulingHint));
        OnPropertyChanged(nameof(CanConfirm));
        ConfirmCommand.NotifyCanExecuteChanged();
    }

    private RecurrenceRule BuildCurrentRule()
    {
        return SelectedRecurrencePresetIndex switch
        {
            0 => RecurrenceRule.None(),
            1 => new RecurrenceRule
            {
                Mode = RecurrenceMode.Interval,
                Interval = 1,
                Unit = RecurrenceUnit.Day
            },
            2 => new RecurrenceRule
            {
                Mode = RecurrenceMode.WeeklyDays,
                Interval = 1,
                DaysOfWeek =
                [
                    DayOfWeek.Monday,
                    DayOfWeek.Tuesday,
                    DayOfWeek.Wednesday,
                    DayOfWeek.Thursday,
                    DayOfWeek.Friday
                ]
            },
            3 => new RecurrenceRule
            {
                Mode = RecurrenceMode.WeeklyDays,
                Interval = 1,
                DaysOfWeek = [TriggerDate.DayOfWeek]
            },
            4 when SelectedCustomModeIndex == 0 => new RecurrenceRule
            {
                Mode = RecurrenceMode.Interval,
                Interval = CustomInterval,
                Unit = SelectedIntervalUnitIndex switch
                {
                    0 => RecurrenceUnit.Minute,
                    1 => RecurrenceUnit.Hour,
                    2 => RecurrenceUnit.Day,
                    3 => RecurrenceUnit.Week,
                    _ => RecurrenceUnit.Day
                }
            },
            4 => new RecurrenceRule
            {
                Mode = RecurrenceMode.WeeklyDays,
                Interval = CustomInterval,
                DaysOfWeek = WeekdaySelections
                    .Where(x => x.IsSelected)
                    .Select(x => x.DayOfWeek)
                    .ToList()
            },
            _ => RecurrenceRule.None()
        };
    }

    private WeekdaySelectionItem CreateWeekday(DayOfWeek day)
    {
        return new WeekdaySelectionItem(day, GetWeekdayLabel(day));
    }

    private string GetWeekdayLabel(DayOfWeek day)
    {
        return day switch
        {
            DayOfWeek.Monday => GetLocalizedString("Day.MondayShort", "Mon"),
            DayOfWeek.Tuesday => GetLocalizedString("Day.TuesdayShort", "Tue"),
            DayOfWeek.Wednesday => GetLocalizedString("Day.WednesdayShort", "Wed"),
            DayOfWeek.Thursday => GetLocalizedString("Day.ThursdayShort", "Thu"),
            DayOfWeek.Friday => GetLocalizedString("Day.FridayShort", "Fri"),
            DayOfWeek.Saturday => GetLocalizedString("Day.SaturdayShort", "Sat"),
            DayOfWeek.Sunday => GetLocalizedString("Day.SundayShort", "Sun"),
            _ => day.ToString()
        };
    }

    private string GetLocalizedString(string key, string defaultValue)
    {
        return _localizationService?.GetString(key, defaultValue) ?? defaultValue;
    }
}

public partial class WeekdaySelectionItem : ObservableObject
{
    public WeekdaySelectionItem(DayOfWeek dayOfWeek, string displayName)
    {
        DayOfWeek = dayOfWeek;
        DisplayName = displayName;
    }

    public DayOfWeek DayOfWeek { get; }

    [ObservableProperty]
    private string _displayName;

    [ObservableProperty]
    private bool _isSelected;
}
