using Athena.UI.ViewModels;
using Avalonia.Controls;

namespace Athena.UI.Views;

public sealed class TasksWindow : Window
{
    public TasksWindow(TasksViewModel viewModel)
    {
        Title = "定时消息";
        Width = 1160;
        Height = 780;
        MinWidth = 1000;
        MinHeight = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = new TasksView { DataContext = viewModel };
    }
}
