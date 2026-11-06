using Athena.UI.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Serilog;
using System;

namespace Athena.UI.Views;

public partial class GitBranchesWindow : Window
{
    public GitBranchesWindow() { InitializeComponent(); }

    public GitBranchesWindow(GitBranchManagerViewModel viewModel) : this()
    {
        DataContext = viewModel;
        Opened += OnOpened;
        Closed += (_, _) => viewModel.Dispose();
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        try
        {
            if (DataContext is GitBranchManagerViewModel vm) await vm.RefreshCommand.ExecuteAsync(null);
        }
        catch (Exception ex) { Log.Error(ex, "Could not open Git branches"); }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (DataContext is GitBranchManagerViewModel { IsBusy: true }) e.Cancel = true;
        base.OnClosing(e);
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();
}
