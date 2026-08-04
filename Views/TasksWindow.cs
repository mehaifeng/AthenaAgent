using Athena.UI.Services.Interfaces;
using Athena.UI.ViewModels;
using Avalonia.Controls;

namespace Athena.UI.Views;

public sealed class TasksWindow : Window
{
    private readonly ILocalizationService? _localization;

    public TasksWindow(TasksViewModel viewModel, ILocalizationService? localization = null)
    {
        _localization = localization;
        Width = 1160;
        Height = 780;
        MinWidth = 1000;
        MinHeight = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = new TasksView { DataContext = viewModel };
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
        Title = _localization?.GetString("Window.Tasks.Title", "Scheduled tasks") ?? "Scheduled tasks";
    }
}