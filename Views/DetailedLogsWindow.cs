using Athena.UI.ViewModels;
using Avalonia.Controls;

namespace Athena.UI.Views;

public sealed class DetailedLogsWindow : Window
{
    public DetailedLogsWindow(LogsViewModel viewModel)
    {
        Title = "详细日志";
        Width = 1080;
        Height = 720;
        MinWidth = 800;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = new LogsView { DataContext = viewModel };
    }
}
