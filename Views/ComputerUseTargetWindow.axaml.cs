using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System;
using System.Globalization;

namespace Athena.UI.Views;

public sealed partial class ComputerUseTargetWindow : Window
{
    internal string ConfirmationLabel { get; set; } = "Confirmed";

    public ComputerUseTargetWindow()
    {
        InitializeComponent();
        Opened += (_, _) => Dispatcher.UIThread.Post(ReportLayout, DispatcherPriority.Loaded);
    }

    private void ReportLayout()
    {
        var field = CenterOf(DemoField);
        var confirm = CenterOf(DemoConfirm);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"ATHENA_CUA_LAYOUT {field.X / ClientSize.Width} {field.Y / ClientSize.Height} {confirm.X / ClientSize.Width} {confirm.Y / ClientSize.Height}"));
    }

    private Point CenterOf(Control control) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), this) ?? default;

    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        ResultText.Text = $"{ConfirmationLabel} · {DemoField.Text}";
        Console.WriteLine($"ATHENA_CUA_CONFIRMED {DemoField.Text}");
    }
}
