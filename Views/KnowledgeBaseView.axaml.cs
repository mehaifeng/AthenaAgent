using Avalonia.Controls;
using Avalonia.Interactivity;
using Athena.UI.ViewModels;

namespace Athena.UI.Views;

public partial class KnowledgeBaseView : UserControl
{
    public KnowledgeBaseView() { InitializeComponent(); }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (DataContext is KnowledgeBaseViewModel vm)
        {
            await vm.InitializeAsync();
        }
    }
}
