using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Athena.UI.Services.Interfaces;
using System;
using System.Globalization;

namespace Athena.UI.Converters;

public class BoolToAlignmentConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isUser && isUser) return HorizontalAlignment.Right;
        return HorizontalAlignment.Left;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class IntToColumnConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool isUser = value is bool b && b;
        if (parameter?.ToString() == "UserAlign") return isUser ? 1 : 0;
        return 0;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

/// <summary>主导航页缓存：仅切换页面可见性，不从视觉树移除页面。</summary>
public class IntEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int actual
           && int.TryParse(parameter?.ToString(), out var expected)
           && actual == expected;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

// 移除 RoleToBrushConverter 和 RoleToBgConverter 的复杂逻辑，改为简单的占位符或删除（如果不再使用）
public class RoleToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Brushes.Transparent;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

/// <summary>调试 RAW 视图：按角色类别为标题着色。</summary>
public class RawRoleToBrushConverter : IValueConverter
{
    private static readonly IBrush System = new ImmutableSolidColorBrush(Color.Parse("#E6A23C"));
    private static readonly IBrush User = new ImmutableSolidColorBrush(Color.Parse("#4C8BF5"));
    private static readonly IBrush Assistant = new ImmutableSolidColorBrush(Color.Parse("#67C23A"));
    private static readonly IBrush Tool = new ImmutableSolidColorBrush(Color.Parse("#B57EDC"));
    private static readonly IBrush Error = new ImmutableSolidColorBrush(Color.Parse("#F56C6C"));
    private static readonly IBrush Default = new ImmutableSolidColorBrush(Color.Parse("#909399"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value?.ToString() switch
        {
            "system" => System,
            "user" => User,
            "assistant" => Assistant,
            "tool" => Tool,
            "error" => Error,
            _ => Default
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class RoleToBgConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Brushes.Transparent;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class RoleToTitleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string key = (value is bool isUser && isUser) ? "Chat.RoleUser" : "Chat.RoleAssistant";
        var localizationService = App.Services?.GetService(typeof(ILocalizationService)) as ILocalizationService;
        return localizationService?.GetString(key, key) ?? key;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class OpacityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool isCompressed = value is bool b && b;
        return isCompressed ? 0.4 : 1.0;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

/// <summary>
/// Compares the tag value against the parameter; returns true for IsVisible binding.
/// Used to show either Moon or Sun icon based on the current ThemeIcon tag.
/// </summary>
public class ThemeTagToVisibilityConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value?.ToString() == parameter?.ToString();

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>
/// 把 Semi 图标资源 key（字符串）解析为 PathIcon 可用的 Geometry。
/// 用于按工具名动态选择图标（无法用 {StaticResource {Binding}} 实现）。
/// </summary>
public class ToolIconKeyToGeometryConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string key
            && !string.IsNullOrEmpty(key)
            && Application.Current?.TryGetResource(key, null, out var resource) == true
            && resource is Geometry geometry)
        {
            return geometry;
        }

        return null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class LocConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string key && !string.IsNullOrEmpty(key))
        {
            var localizationService = App.Services?.GetService(typeof(ILocalizationService)) as ILocalizationService;
            return localizationService?.GetString(key, key) ?? key;
        }
        return value;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}
