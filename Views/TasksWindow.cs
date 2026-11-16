using Athena.UI.Markup;
using Athena.UI.Services.Interfaces;
using Athena.UI.ViewModels;
using Avalonia.Controls;

namespace Athena.UI.Views;

public sealed class TasksWindow : Window
{
    public TasksWindow(TasksViewModel viewModel, ILocalizationService? localization = null)
    {
        Width = 1160;
        Height = 780;
        MinWidth = 1000;
        MinHeight = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = new TasksView { DataContext = viewModel };
        const string title = "Scheduled tasks";
        Title = title;
        if (localization != null)
        {
            var binding = this.Bind(TitleProperty, LocExtension.CreateBinding(localization, "Window.Tasks.Title", title));
            Closed += (_, _) => binding.Dispose();
        }
    }
}
