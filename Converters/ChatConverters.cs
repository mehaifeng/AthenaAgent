using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;
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

public class RoleToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool isUser = value is bool b && b;
        // 用户使用高亮色，系统使用标准前景色
        return isUser ? Application.Current!.FindResource("DosHighlightBrush")! : Application.Current!.FindResource("DosBorderBrush")!;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class RoleToBgConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool isUser = value is bool b && b;
        // 用户消息使用轻微的背景填充增强区分度
        return isUser ? Application.Current!.FindResource("DosHoverBackgroundBrush")! : Brushes.Transparent;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class RoleToTitleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return (value is bool isUser && isUser) ? "TRANSMIT_DATA_PKT" : "RECEIVE_DATA_STREAM";
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class RoleToTextBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool isUser = value is bool b && b;
        return isUser ? Application.Current!.FindResource("DosHighlightBrush")! : Application.Current!.FindResource("DosForegroundBrush")!;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}
