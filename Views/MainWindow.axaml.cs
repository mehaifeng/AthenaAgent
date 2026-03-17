using Avalonia;
using Avalonia.Controls;

namespace Athena.UI.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (Application.Current is App app && !app.IsQuitting)
        {
            e.Cancel = true;
            this.Hide();
        }
        base.OnClosing(e);
    }
}
