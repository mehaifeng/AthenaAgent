using CommunityToolkit.Mvvm.ComponentModel;
using System;

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
        private DateTime _scheduleBoundary;

        [ObservableProperty]
        private RecurrenceRule _recurrenceRule = RecurrenceRule.None();

        [ObservableProperty]
        private bool _isExecuted;

        [ObservableProperty]
        private DateTime _createdAt = DateTime.Now;

        /// <summary>
        /// 任务类型：前台（主动消息）或后台（静默执行）
        /// </summary>
        [ObservableProperty]
        private TaskType _taskType = TaskType.Proactive;

        [ObservableProperty]
        private DateTime? _lastExecutionAt;

        [ObservableProperty]
        private string? _lastExecutionOutcome;

        [ObservableProperty]
        private string? _lastExecutionNote;

        public string TriggerTimeDisplay => TriggerTime.ToString("yyyy-MM-dd HH:mm");

        public string RecurrenceDisplay
        {
            get
            {
                var recurrenceService = App.Services?.GetService(typeof(Services.Interfaces.IRecurrenceService))
                    as Services.Interfaces.IRecurrenceService;

                return recurrenceService?.GetSummary(RecurrenceRule)
                    ?? GetLocalizedString("Recurrence.NoneDisplay", "Once");
            }
        }

        public string TaskTypeDisplay => TaskType switch
        {
            TaskType.Proactive => GetLocalizedString("TaskType.Proactive", "Foreground"),
            TaskType.Background => GetLocalizedString("TaskType.Background", "Background"),
            _ => TaskType.ToString()
        };

        /// <summary>
        /// 语言切换后刷新本地化显示字符串（由 TasksViewModel 在 LanguageChanged 时调用）。
        /// </summary>
        public void RefreshLocalizedDisplays()
        {
            OnPropertyChanged(nameof(RecurrenceDisplay));
            OnPropertyChanged(nameof(TaskTypeDisplay));
        }

        private static string GetLocalizedString(string key, string defaultValue)
        {
            var localizationService = App.Services?.GetService(typeof(Services.Interfaces.ILocalizationService))
                as Services.Interfaces.ILocalizationService;
            return localizationService?.GetString(key, defaultValue) ?? defaultValue;
        }
    }
}
