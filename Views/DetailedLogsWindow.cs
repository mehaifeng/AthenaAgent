using Athena.UI.Markup;
using Athena.UI.Services.Interfaces;
using Athena.UI.ViewModels;
using Avalonia.Controls;

namespace Athena.UI.Views;

public sealed class DetailedLogsWindow : Window
{
    public DetailedLogsWindow(LogsViewModel viewModel, ILocalizationService? localization = null)
    {
        Width = 1080;
        Height = 720;
        MinWidth = 800;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = new LogsView { DataContext = viewModel };
        const string title = "Detailed logs";
        Title = title;
        if (localization != null)
        {
            var binding = this.Bind(TitleProperty, LocExtension.CreateBinding(localization, "Window.DetailedLogs.Title", title));
            Closed += (_, _) => binding.Dispose();
        }
    }
}
