using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;
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

// 移除 RoleToBrushConverter 和 RoleToBgConverter 的复杂逻辑，改为简单的占位符或删除（如果不再使用）
public class RoleToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Brushes.Transparent;
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

public class ThemeToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // Dark -> True (Checked), Light -> False (Unchecked)
        return value?.ToString() == "Dark";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // True -> Dark, False -> Light
        return (bool)(value ?? false) ? "Dark" : "Light";
    }
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
