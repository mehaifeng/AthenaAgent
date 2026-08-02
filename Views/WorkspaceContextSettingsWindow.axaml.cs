using Athena.UI.ViewModels;
using Avalonia.Controls;
using System;

namespace Athena.UI.Views;

public partial class WorkspaceContextSettingsWindow : Window
{
    private WorkspaceContextSettingsViewModel? _viewModel;

    public WorkspaceContextSettingsWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel != null) _viewModel.CloseRequested -= OnCloseRequested;
        _viewModel = DataContext as WorkspaceContextSettingsViewModel;
        if (_viewModel != null) _viewModel.CloseRequested += OnCloseRequested;
    }

    private void OnCloseRequested(object? sender, EventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        if (_viewModel != null)
        {
            _viewModel.CloseRequested -= OnCloseRequested;
            _viewModel.Dispose();
            _viewModel = null;
        }
        base.OnClosed(e);
    }
}
