using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;

namespace Athena.UI.ViewModels;

public partial class CreateTaskDialogViewModel : ViewModelBase
{
    private readonly ILocalizationService? _localizationService;

    /// <summary>
    /// 触发时间
    /// </summary>
    [ObservableProperty]
    private DateTime _triggerDate = DateTime.Today.AddDays(1);

    [ObservableProperty]
    private TimeSpan _triggerTime = new TimeSpan(9, 0, 0);

    /// <summary>
    /// 任务意图（LLM 对自己的提醒）
    /// </summary>
    [ObservableProperty]
    private string _intent = string.Empty;

    /// <summary>
    /// 循环模式
    /// </summary>
    [ObservableProperty]
    private string _recurrence = "none";

    /// <summary>
    /// 任务类型
    /// </summary>
    [ObservableProperty]
    private TaskType _taskType = TaskType.Proactive;

    /// <summary>
    /// 可用的循环模式
    /// </summary>
    public ObservableCollection<string> RecurrenceOptions { get; } = new()
    {
        "none", "daily", "weekly", "every 3 days", "every 2 weeks"
    };

    /// <summary>
    /// 循环模式显示名称
    /// </summary>
    public ObservableCollection<string> RecurrenceDisplayNames { get; }

    /// <summary>
    /// 任务类型显示名称
    /// </summary>
    public ObservableCollection<string> TaskTypeDisplayNames { get; }

    public CreateTaskDialogViewModel() : this(null) { }

    public CreateTaskDialogViewModel(ILocalizationService? localizationService)
    {
        _localizationService = localizationService;
        RecurrenceDisplayNames = new ObservableCollection<string>
        {
            GetLocalizedString("Recurrence.NoneDisplay", "Once"),
            GetLocalizedString("Recurrence.DailyDisplay", "Daily"),
            GetLocalizedString("Recurrence.WeeklyDisplay", "Weekly"),
            GetLocalizedString("Recurrence.Every3Days", "Every 3 days"),
            GetLocalizedString("Recurrence.Every2Weeks", "Every 2 weeks")
        };
        TaskTypeDisplayNames = new ObservableCollection<string>
        {
            GetLocalizedString("TaskType.Proactive", "Foreground (Proactive Message)"),
            GetLocalizedString("TaskType.Background", "Background (Silent)")
        };
    }

    private string GetLocalizedString(string key, string defaultValue)
    {
        return _localizationService?.GetString(key, defaultValue) ?? defaultValue;
    }

    private int _selectedRecurrenceIndex;
    public int SelectedRecurrenceIndex
    {
        get => _selectedRecurrenceIndex;
        set
        {
            if (SetProperty(ref _selectedRecurrenceIndex, value) && value >= 0 && value < RecurrenceOptions.Count)
            {
                Recurrence = RecurrenceOptions[value];
            }
        }
    }

    private int _selectedTaskTypeIndex;
    public int SelectedTaskTypeIndex
    {
        get => _selectedTaskTypeIndex;
        set
        {
            if (SetProperty(ref _selectedTaskTypeIndex, value))
            {
                TaskType = value == 1 ? TaskType.Background : TaskType.Proactive;
            }
        }
    }

    /// <summary>
    /// 任务类型说明
    /// </summary>
    public string TaskTypeDescription => TaskType switch
    {
        TaskType.Proactive => GetLocalizedString("TaskDialog.ProactiveHint",
            "Triggers at the scheduled time and sends a proactive message to the chat interface."),
        TaskType.Background => GetLocalizedString("TaskDialog.BackgroundHint",
            "Triggers at the scheduled time and executes silently in the background without disturbing you."),
        _ => string.Empty
    };

    /// <summary>
    /// 完整的触发时间
    /// </summary>
    public DateTime FullTriggerTime => TriggerDate.Date + TriggerTime;

    /// <summary>
    /// 对话结果
    /// </summary>
    public ScheduledTask? Result { get; private set; }

    /// <summary>
    /// 是否确认
    /// </summary>
    public bool IsConfirmed { get; private set; }

    /// <summary>
    /// 确认命令
    /// </summary>
    [RelayCommand]
    private void Confirm()
    {
        if (string.IsNullOrWhiteSpace(Intent))
            return;

        Result = new ScheduledTask
        {
            Id = Guid.NewGuid().ToString(),
            TriggerTime = FullTriggerTime,
            Intent = Intent.Trim(),
            Recurrence = Recurrence,
            CreatedAt = DateTime.Now,
            TaskType = TaskType
        };

        IsConfirmed = true;
        RequestClose?.Invoke();
    }

    /// <summary>
    /// 取消命令
    /// </summary>
    [RelayCommand]
    private void Cancel()
    {
        IsConfirmed = false;
        Result = null;
        RequestClose?.Invoke();
    }

    /// <summary>
    /// 请求关闭窗口
    /// </summary>
    public Action? RequestClose { get; set; }
}
