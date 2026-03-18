using Avalonia.Controls;
using Athena.UI.ViewModels;

namespace Athena.UI.Views;

public partial class ConfigTabView : UserControl
{
    public ConfigTabView() { InitializeComponent(); }

    private void WebSearchProvider_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is ConfigTabViewModel vm && sender is ComboBox comboBox)
        {
            var provider = comboBox.SelectedItem?.ToString();
            vm.UpdateWebSearchBaseUrl(provider);
        }
    }
}
