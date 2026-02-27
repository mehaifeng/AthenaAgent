using Athena.UI.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Athena.UI.Views;

public partial class CreateTaskDialog : Window
{
    public CreateTaskDialog()
    {
        InitializeComponent();
    }

    public CreateTaskDialog(CreateTaskDialogViewModel viewModel) : this()
    {
        DataContext = viewModel;
        viewModel.RequestClose = Close;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // 确保在关闭时取消绑定
        if (DataContext is CreateTaskDialogViewModel vm)
        {
            vm.RequestClose = null;
        }
        base.OnClosing(e);
    }
}
