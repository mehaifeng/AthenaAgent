using Athena.UI.ViewModels;
using Avalonia.Controls;

namespace Athena.UI.Views;

public sealed class KnowledgeBaseWindow : Window
{
    public KnowledgeBaseWindow(KnowledgeBaseViewModel viewModel)
    {
        Title = "知识库";
        Width = 1120;
        Height = 780;
        MinWidth = 900;
        MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = new KnowledgeBaseView { DataContext = viewModel };
    }
}
