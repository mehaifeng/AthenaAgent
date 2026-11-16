using Athena.UI.Markup;
using Athena.UI.Services.Interfaces;
using Athena.UI.ViewModels;
using Avalonia.Controls;

namespace Athena.UI.Views;

public sealed class KnowledgeBaseWindow : Window
{
    public KnowledgeBaseWindow(KnowledgeBaseViewModel viewModel, ILocalizationService? localization = null)
    {
        Width = 1120;
        Height = 780;
        MinWidth = 900;
        MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = new KnowledgeBaseView { DataContext = viewModel };
        const string title = "Knowledge base";
        Title = title;
        if (localization != null)
        {
            var binding = this.Bind(TitleProperty, LocExtension.CreateBinding(localization, "Window.KnowledgeBase.Title", title));
            Closed += (_, _) => binding.Dispose();
        }
    }
}
