using Avalonia.Controls;
using Avalonia.Interactivity;
using Athena.UI.ViewModels;

namespace Athena.UI.Views;

public partial class KnowledgeBaseTabView : UserControl
{
    public KnowledgeBaseTabView() { InitializeComponent(); }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (DataContext is KnowledgeBaseTabViewModel vm)
        {
            await vm.InitializeAsync();
        }
    }
}
