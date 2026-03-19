using Athena.UI.ViewModels;
using Avalonia.Controls;

namespace Athena.UI.Views;

public partial class ConfirmDialog : Window
{
    public ConfirmDialog()
    {
        InitializeComponent();
    }

    public ConfirmDialog(ConfirmDialogViewModel viewModel) : this()
    {
        DataContext = viewModel;
        viewModel.RequestClose = Close;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (DataContext is ConfirmDialogViewModel vm)
        {
            vm.RequestClose = null;
        }
        base.OnClosing(e);
    }
}
