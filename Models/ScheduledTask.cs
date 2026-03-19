using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Text;

namespace Athena.UI.Models
{
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

        public string TriggerTimeDisplay => TriggerTime.ToString("yyyy-MM-dd HH:mm");

        public string RecurrenceDisplay => Recurrence switch
        {
            "none" => GetLocalizedString("Recurrence.NoneDisplay", "Once"),
            "daily" => GetLocalizedString("Recurrence.DailyDisplay", "Daily"),
            "weekly" => GetLocalizedString("Recurrence.WeeklyDisplay", "Weekly"),
            _ => Recurrence
        };

        private static string GetLocalizedString(string key, string defaultValue)
        {
            var localizationService = App.Services?.GetService(typeof(Services.Interfaces.ILocalizationService))
                as Services.Interfaces.ILocalizationService;
            return localizationService?.GetString(key, defaultValue) ?? defaultValue;
        }
    }
}
