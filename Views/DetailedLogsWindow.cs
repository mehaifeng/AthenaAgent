using Athena.UI.Services.Interfaces;
using Athena.UI.ViewModels;
using Avalonia.Controls;

namespace Athena.UI.Views;

public sealed class DetailedLogsWindow : Window
{
    private readonly ILocalizationService? _localization;

    public DetailedLogsWindow(LogsViewModel viewModel, ILocalizationService? localization = null)
    {
        _localization = localization;
        Width = 1080;
        Height = 720;
        MinWidth = 800;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = new LogsView { DataContext = viewModel };
    }

    protected override void OnOpened(System.EventArgs e)
    {
        base.OnOpened(e);
        RefreshTitle();
        if (_localization != null)
        {
            _localization.LanguageChanged += (_, _) => RefreshTitle();
        }
    }

    private void RefreshTitle()
    {
        Title = _localization?.GetString("Window.DetailedLogs.Title", "Detailed logs") ?? "Detailed logs";
    }
}