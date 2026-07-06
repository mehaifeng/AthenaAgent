using Athena.UI.ViewModels;
using Avalonia.Controls;

namespace Athena.UI.Views;

public partial class ToolApprovalDialog : Window
{
    public ToolApprovalDialog()
    {
        InitializeComponent();
    }

    public ToolApprovalDialog(ToolApprovalDialogViewModel viewModel) : this()
    {
        DataContext = viewModel;
        viewModel.RequestClose = Close;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (DataContext is ToolApprovalDialogViewModel vm)
        {
            vm.RequestClose = null;
        }
        base.OnClosing(e);
    }
}
