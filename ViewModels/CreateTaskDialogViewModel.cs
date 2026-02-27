using Athena.UI.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;

namespace Athena.UI.ViewModels;

public partial class CreateTaskDialogViewModel : ViewModelBase
{
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
    /// 可用的循环模式
    /// </summary>
    public ObservableCollection<string> RecurrenceOptions { get; } = new()
    {
        "none", "daily", "weekly", "every 3 days", "every 2 weeks"
    };

    /// <summary>
    /// 循环模式显示名称
    /// </summary>
    public ObservableCollection<string> RecurrenceDisplayNames { get; } = new()
    {
        "一次性 (None)", "每天 (Daily)", "每周 (Weekly)", "每 3 天", "每 2 周"
    };

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
            CreatedAt = DateTime.Now
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
