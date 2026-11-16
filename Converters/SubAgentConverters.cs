using Athena.UI.Models;
using Avalonia.Data.Converters;
using Avalonia.Media;
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
