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
/// 折叠状态（bool）→ 展开箭头图标。展开取 AthenaIconChevronDown，折叠取 AthenaIconChevronRight。
/// 直接返回 Geometry 以便绑到 PathIcon.Data —— 用字形文本会随字体缺字回退成豆腐块，
/// 而且粗细和周围的图标对不上。
/// </summary>
public class BoolToExpandIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => IconResources.Resolve(value is true ? "AthenaIconChevronDown" : "AthenaIconChevronRight");

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 播放状态（bool）→ 停止/播放图标。播放中显示停止，未播放显示播放。
/// </summary>
public class BoolToPlaybackIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => IconResources.Resolve(value is true ? "AthenaIconStop" : "AthenaIconPlay");

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 按图标契约 key（Styles/AppIcons.axaml 中的 AthenaIcon* 名称）取 Geometry。
/// key 拼错或资源缺失时返回 null —— PathIcon 画空白，不至于让整个模板崩掉。
/// </summary>
internal static class IconResources
{
    public static Avalonia.Media.Geometry? Resolve(string key)
        => Avalonia.Application.Current?.TryGetResource(key, null, out var resource) == true
            ? resource as Avalonia.Media.Geometry
            : null;
}

/// <summary>
/// 工具审批模式枚举 → 本地化显示文本。
/// </summary>
/// <summary>
/// 压缩强度在下拉框里必须显示成人话。原始枚举名（Conservative/Balanced/Aggressive）
/// 出现在中文界面里，就又变回了一个「看不懂的参数」。
/// </summary>
public class CompressionStrengthToDisplayConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not CompressionStrength strength)
            return value?.ToString() ?? string.Empty;

        var (key, fallback) = strength switch
        {
            CompressionStrength.Conservative => ("Settings.Context.Strength.Conservative", "保守（4:1，细节优先）"),
            CompressionStrength.Aggressive => ("Settings.Context.Strength.Aggressive", "激进（16:1，少打断）"),
            _ => ("Settings.Context.Strength.Balanced", "平衡（8:1，推荐）")
        };

        var localization = Athena.UI.App.Services?.GetService<ILocalizationService>();
        return localization?.GetString(key, fallback) ?? fallback;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

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
            ToolApprovalMode.Automatic => ("Config.ToolApproval.Mode.Automatic", "自动（由审批模型裁决）"),
            _ => (string.Empty, mode.ToString())
        };

        if (string.IsNullOrEmpty(key)) return fallback;

        var localization = Athena.UI.App.Services?.GetService<ILocalizationService>();
        return localization?.GetString(key, fallback) ?? fallback;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
