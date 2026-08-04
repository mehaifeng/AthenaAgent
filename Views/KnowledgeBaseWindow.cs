using Athena.UI.Services.Interfaces;
using Athena.UI.ViewModels;
using Avalonia.Controls;

namespace Athena.UI.Views;

public sealed class KnowledgeBaseWindow : Window
{
    private readonly ILocalizationService? _localization;

    public KnowledgeBaseWindow(KnowledgeBaseViewModel viewModel, ILocalizationService? localization = null)
    {
        _localization = localization;
        Width = 1120;
        Height = 780;
        MinWidth = 900;
        MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = new KnowledgeBaseView { DataContext = viewModel };
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
        Title = _localization?.GetString("Window.KnowledgeBase.Title", "Knowledge base") ?? "Knowledge base";
    }
}