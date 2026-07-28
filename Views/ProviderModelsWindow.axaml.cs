using Avalonia.Controls;

namespace Athena.UI.Views;

public partial class ProviderModelsWindow : Window
{
    public ProviderModelsWindow() => InitializeComponent();

    protected override void OnClosed(System.EventArgs e)
    {
        if (DataContext is System.IDisposable disposable) disposable.Dispose();
        base.OnClosed(e);
    }
}
