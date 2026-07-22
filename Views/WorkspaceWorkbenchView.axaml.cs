using Avalonia.Controls;
using Avalonia.Input;
using Athena.UI.ViewModels;
using Athena.UI.Services.Interfaces;
using System;

namespace Athena.UI.Views;

public partial class WorkspaceWorkbenchView : UserControl
{
    private ColumnDefinition? _editorColumn;

    public WorkspaceWorkbenchView()
    {
        InitializeComponent();
        var grid = this.FindControl<Grid>("WorkbenchGrid");
        _editorColumn = grid?.ColumnDefinitions[0];
        var config = (App.Services?.GetService(typeof(IConfigService)) as IConfigService)?.Load();
        if (_editorColumn != null && config != null)
            _editorColumn.Width = new GridLength(Math.Max(180, config.MainLayout.EditorWidth));
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
