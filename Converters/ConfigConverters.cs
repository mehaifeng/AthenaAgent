using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using Avalonia.Data.Converters;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Globalization;

namespace Athena.UI.Converters;

/// <summary>
/// 工具审批模式枚举 → 本地化显示文本。
/// </summary>
public class ToolApprovalModeToDisplayConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not ToolApprovalMode mode)
            return value?.ToString() ?? string.Empty;

        var (key, fallback) = mode switch
        {
            ToolApprovalMode.Off => ("Config.ToolApproval.Mode.Off", "关闭（全部自动放行）"),
            ToolApprovalMode.Balanced => ("Config.ToolApproval.Mode.Balanced", "均衡（写/删/终端需确认，推荐）"),
            ToolApprovalMode.Strict => ("Config.ToolApproval.Mode.Strict", "严格（所有工具都确认）"),
            _ => (string.Empty, mode.ToString())
        };

        if (string.IsNullOrEmpty(key)) return fallback;

        var localization = Athena.UI.App.Services?.GetService<ILocalizationService>();
        return localization?.GetString(key, fallback) ?? fallback;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

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
