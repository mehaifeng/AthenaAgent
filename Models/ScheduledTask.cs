using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Text;

namespace Athena.UI.Models
{
    /// <summary>
    /// 任务类型
    /// </summary>
    public enum TaskType
    {
        /// <summary>
        /// 前台任务：触发时以主动消息形式出现在聊天界面
        /// </summary>
        Proactive,

        /// <summary>
        /// 后台任务：触发时静默执行，不干扰用户
        /// </summary>
        Background
    }

    /// <summary>
    /// 计划任务模型
    /// </summary>
    public partial class ScheduledTask : ObservableObject
    {
        [ObservableProperty]
        private string _id = Guid.NewGuid().ToString();

        [ObservableProperty]
        private DateTime _triggerTime;

        [ObservableProperty]
        private string _intent = string.Empty;

        [ObservableProperty]
        private string _recurrence = "none";

        [ObservableProperty]
        private bool _isExecuted;

        [ObservableProperty]
        private DateTime _createdAt = DateTime.Now;

        /// <summary>
        /// 任务类型：前台（主动消息）或后台（静默执行）
        /// </summary>
        [ObservableProperty]
        private TaskType _taskType = TaskType.Proactive;

        public string TriggerTimeDisplay => TriggerTime.ToString("yyyy-MM-dd HH:mm");

        public string RecurrenceDisplay => Recurrence switch
        {
            "none" => GetLocalizedString("Recurrence.NoneDisplay", "Once"),
            "daily" => GetLocalizedString("Recurrence.DailyDisplay", "Daily"),
            "weekly" => GetLocalizedString("Recurrence.WeeklyDisplay", "Weekly"),
            _ => Recurrence
        };

        public string TaskTypeDisplay => TaskType switch
        {
            TaskType.Proactive => GetLocalizedString("TaskType.Proactive", "Foreground"),
            TaskType.Background => GetLocalizedString("TaskType.Background", "Background"),
            _ => TaskType.ToString()
        };

        private static string GetLocalizedString(string key, string defaultValue)
        {
            var localizationService = App.Services?.GetService(typeof(Services.Interfaces.ILocalizationService))
                as Services.Interfaces.ILocalizationService;
            return localizationService?.GetString(key, defaultValue) ?? defaultValue;
        }
    }
}
