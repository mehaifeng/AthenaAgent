using Avalonia.Controls;
using Avalonia.Input;
using Athena.UI.ViewModels;
using Athena.UI.Services.Interfaces;
using System;
using System.ComponentModel;

namespace Athena.UI.Views;

public partial class WorkspaceWorkbenchView : UserControl
{
    private const double ReviewPaneDefaultWidth = 280;
    private const double ReviewPaneMinWidth = 260;
    private const double EditorPaneMinWidth = 248;
    private const double SplitterWidth = 5;

    private ColumnDefinition? _reviewColumn;
    private ColumnDefinition? _reviewSplitterColumn;
    private ColumnDefinition? _editorColumn;
    private WorkspaceWorkbenchViewModel? _viewModel;
    private double _reviewWidth = ReviewPaneDefaultWidth;

    public WorkspaceWorkbenchView()
    {
        InitializeComponent();
        var grid = this.FindControl<Grid>("WorkbenchGrid");
        _reviewColumn = grid?.ColumnDefinitions[0];
        _reviewSplitterColumn = grid?.ColumnDefinitions[1];
        _editorColumn = grid?.ColumnDefinitions[2];
        var config = (App.Services?.GetService(typeof(IConfigService)) as IConfigService)?.Load();
        if (_editorColumn != null && config != null)
            _editorColumn.Width = new GridLength(Math.Max(EditorPaneMinWidth, config.MainLayout.EditorWidth));
        DataContextChanged += OnWorkbenchDataContextChanged;
        AttachedToVisualTree += (_, _) => AttachViewModel(DataContext as WorkspaceWorkbenchViewModel);
        DetachedFromVisualTree += (_, _) => AttachViewModel(null);
        AttachViewModel(DataContext as WorkspaceWorkbenchViewModel);
    }

    private void OnWorkbenchDataContextChanged(object? sender, EventArgs e) =>
        AttachViewModel(DataContext as WorkspaceWorkbenchViewModel);

    private void AttachViewModel(WorkspaceWorkbenchViewModel? viewModel)
    {
        if (ReferenceEquals(_viewModel, viewModel)) return;
        if (_viewModel != null) _viewModel.PropertyChanged -= OnWorkbenchPropertyChanged;
        _viewModel = viewModel;
        if (_viewModel != null) _viewModel.PropertyChanged += OnWorkbenchPropertyChanged;
        ApplyReviewPaneLayout();
    }

    private void OnWorkbenchPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WorkspaceWorkbenchViewModel.IsReviewVisible))
            ApplyReviewPaneLayout();
    }

    private void ApplyReviewPaneLayout()
    {
        if (_reviewColumn == null || _reviewSplitterColumn == null) return;

        if (_viewModel?.IsReviewVisible == true)
        {
            _reviewColumn.MinWidth = ReviewPaneMinWidth;
            _reviewColumn.Width = new GridLength(Math.Max(ReviewPaneMinWidth, _reviewWidth));
            _reviewSplitterColumn.MinWidth = SplitterWidth;
            _reviewSplitterColumn.Width = new GridLength(SplitterWidth);
            return;
        }

        if (_reviewColumn.ActualWidth >= ReviewPaneMinWidth)
            _reviewWidth = _reviewColumn.ActualWidth;
        _reviewColumn.MinWidth = 0;
        _reviewColumn.Width = new GridLength(0);
        _reviewSplitterColumn.MinWidth = 0;
        _reviewSplitterColumn.Width = new GridLength(0);
    }

    private void OnFileDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is WorkspaceWorkbenchViewModel viewModel && viewModel.SelectedFile is { IsDirectory: false } file)
        {
            viewModel.OpenFileCommand.Execute(file);
        }
    }

    private async void OnEditorSplitterDragCompleted(object? sender, VectorEventArgs e)
    {
        var configService = App.Services?.GetService(typeof(IConfigService)) as IConfigService;
        if (configService == null || _editorColumn == null) return;
        var config = configService.Load();
        config.MainLayout.EditorWidth = _editorColumn.ActualWidth;
        await configService.SaveAsync(config);
    }
}
