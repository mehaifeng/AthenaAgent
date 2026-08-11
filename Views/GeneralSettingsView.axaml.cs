using Avalonia.Controls;
using Athena.UI.ViewModels;

namespace Athena.UI.Views;

public partial class GeneralSettingsView : UserControl
{
    public GeneralSettingsView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) =>
        {
            if (DataContext is GeneralSettingsViewModel viewModel)
                _ = viewModel.LoadPetCatalogAsync();
        };
    }
}
