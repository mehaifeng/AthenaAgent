using Athena.UI.Models;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using System;
using System.Globalization;

namespace Athena.UI.Converters;

/// <summary>子代理状态 → 猫头鹰主体颜色。运行=琥珀、完成=青绿、出错=红、其余=灰。</summary>
public class SubAgentStateToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Pending = new(Color.Parse("#9E9A90"));
    private static readonly SolidColorBrush Running = new(Color.Parse("#BA7517"));
    private static readonly SolidColorBrush Done = new(Color.Parse("#1D9E75"));
    private static readonly SolidColorBrush Error = new(Color.Parse("#E24B4A"));
    private static readonly SolidColorBrush Cancelled = new(Color.Parse("#888780"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is SubAgentState state
            ? state switch
            {
                SubAgentState.Running => Running,
                SubAgentState.Done => Done,
                SubAgentState.Error => Error,
                SubAgentState.Cancelled => Cancelled,
                _ => Pending
            }
            : Pending;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>整数计数 &gt; 0 → true（用于侧边 Dock 的可见性）。</summary>
public class CountToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int count && count > 0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 把 "translate(x px y px)" 变换字符串解析成 TransformOperations 对象。
/// 绑定场景下 RenderTransform 不会自动套用字符串类型转换器，故显式转换，
/// 并配合 TransformOperationsTransition 产生平滑滑翔。
/// </summary>
public class StringToTransformConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string s && !string.IsNullOrWhiteSpace(s)
            ? TransformOperations.Parse(s)
            : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
