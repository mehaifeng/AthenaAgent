using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using Avalonia.Data.Converters;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Globalization;

namespace Athena.UI.Converters;

/// <summary>
/// 知识库整理模型来源枚举 → 本地化显示文本。
/// 通过 App.Services 解析本地化服务，避免在 ComboBox 里暴露英文枚举名。
/// </summary>
public class MaintenanceModelSourceToDisplayConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not KnowledgeMaintenanceModelSource source)
            return value?.ToString() ?? string.Empty;

        var (key, fallback) = source switch
        {
            KnowledgeMaintenanceModelSource.InheritSecondary => ("Config.KbMaintenanceSourceSecondary", "后台任务模型"),
            KnowledgeMaintenanceModelSource.InheritMain => ("Config.KbMaintenanceSourceMain", "主模型"),
            KnowledgeMaintenanceModelSource.Custom => ("Config.KbMaintenanceSourceCustom", "自定义"),
            _ => (string.Empty, source.ToString())
        };

        if (string.IsNullOrEmpty(key)) return fallback;

        var localization = Athena.UI.App.Services?.GetService<ILocalizationService>();
        return localization?.GetString(key, fallback) ?? fallback;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
