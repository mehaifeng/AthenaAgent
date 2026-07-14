using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using Avalonia.Data.Converters;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Globalization;

namespace Athena.UI.Converters;

/// <summary>
/// MCP 传输方式枚举 ↔ 布尔（Http=true）。用于 ToggleSwitch 双向绑定与段落显隐。
/// ConverterParameter="Stdio" 时 Convert 取反（供 Stdio 段 IsVisible）。
/// </summary>
public class McpTransportIsHttpConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isHttp = value is McpTransportKind.Http;
        if (parameter is string p && p.Equals("Stdio", StringComparison.OrdinalIgnoreCase))
            return !isHttp;
        return isHttp;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? McpTransportKind.Http : McpTransportKind.Stdio;
}

/// <summary>
/// MCP 连接状态枚举 → 状态灯颜色。灰=空闲 蓝=连接中 绿=已连接 红=失败。
/// </summary>
public class McpStatusToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var color = value switch
        {
            McpConnectionStatus.Connecting => Avalonia.Media.Colors.DodgerBlue,
            McpConnectionStatus.Connected => Avalonia.Media.Colors.MediumSeaGreen,
            McpConnectionStatus.Failed => Avalonia.Media.Colors.IndianRed,
            _ => Avalonia.Media.Colors.Gray
        };
        return new Avalonia.Media.SolidColorBrush(color);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// MCP 连接状态 → 是否失败（bool）。用于「重连」按钮仅在失败时显示。
/// </summary>
public class McpStatusIsFailedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is McpConnectionStatus.Failed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 折叠状态（bool）→ 展开箭头字形。展开为 ▾，折叠为 ▸。
/// </summary>
public class BoolToExpandGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "▾" : "▸";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

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
