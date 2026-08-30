using Athena.UI.Services;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Athena.UI.ViewModels;

namespace Athena.UI.Views;

public partial class KnowledgeBaseView : UserControl
{
    public KnowledgeBaseView() { InitializeComponent(); }

    private void OnLoaded(object? sender, RoutedEventArgs e)
        => AsyncEventGuard.Run(() => OnLoadedAsync(sender, e), nameof(OnLoaded));

    private async Task OnLoadedAsync(object? sender, RoutedEventArgs e)
    {
        if (DataContext is KnowledgeBaseViewModel vm)
        {
            await vm.InitializeAsync();
        }
    }
}
