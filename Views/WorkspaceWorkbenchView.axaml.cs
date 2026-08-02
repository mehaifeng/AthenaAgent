using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Athena.UI.Services.Interfaces;
using Athena.UI.ViewModels;
using System;
using System.ComponentModel;
using System.Threading.Tasks;

namespace Athena.UI.Views;

public partial class WorkspaceWorkbenchView : UserControl
{
    private const double ReviewPaneDefaultWidth = 280;
    private const double ReviewPaneMinWidth = 260;
    private const double EditorPaneDefaultWidth = 280;
    private const double EditorPaneMinWidth = 248;
    private const double FileTreePaneDefaultWidth = 180;
    private const double FileTreePaneMinWidth = 100;
    private const double SplitterWidth = 5;

    private ColumnDefinition? _reviewColumn;
    private ColumnDefinition? _reviewSplitterColumn;
    private ColumnDefinition? _editorColumn;
    private ColumnDefinition? _editorSplitterColumn;
    private ColumnDefinition? _fileTreeColumn;
    private WorkspaceWorkbenchViewModel? _viewModel;
    private double _preferredReviewWidth = ReviewPaneDefaultWidth;
    private double _preferredEditorWidth = EditorPaneDefaultWidth;
    private double _preferredFileTreeWidth = FileTreePaneDefaultWidth;
    private double _actualReviewWidth;
    private double _actualEditorWidth;
    private double _actualFileTreeWidth;
    private double _lastWorkbenchWidth;
    private double _lastMinimumRequiredWidth;
    private double _internalDragFirstStartWidth;
    private double _internalDragSecondStartWidth;
    private double _internalDragCumulativeDelta;
    private bool _isApplyingLayout;
    private bool _isPaneLayoutQueued;

    public WorkspaceWorkbenchView()
    {
        InitializeComponent();
        var grid = this.FindControl<Grid>("WorkbenchGrid");
        _reviewColumn = grid?.ColumnDefinitions[0];
        _reviewSplitterColumn = grid?.ColumnDefinitions[1];
        _editorColumn = grid?.ColumnDefinitions[2];
        _editorSplitterColumn = grid?.ColumnDefinitions[3];
        _fileTreeColumn = grid?.ColumnDefinitions[4];

        var config = GetConfigService()?.Load();
        if (config != null)
        {
            _preferredReviewWidth = Math.Max(ReviewPaneMinWidth, config.MainLayout.ReviewWidth);
            _preferredEditorWidth = Math.Max(EditorPaneMinWidth, config.MainLayout.EditorWidth);
            _preferredFileTreeWidth = Math.Max(FileTreePaneMinWidth, config.MainLayout.FileTreeWidth);
        }

        DataContextChanged += OnWorkbenchDataContextChanged;
        SizeChanged += OnWorkbenchSizeChanged;
        AttachedToVisualTree += (_, _) =>
        {
            AttachViewModel(DataContext as WorkspaceWorkbenchViewModel);
            ApplyPaneLayoutFromPreferences();
        };
        DetachedFromVisualTree += (_, _) => AttachViewModel(null);
        AttachViewModel(DataContext as WorkspaceWorkbenchViewModel);
    }

    public event EventHandler? MinimumRequiredWidthChanged;

    public double MinimumRequiredWidth
    {
        get
        {
            var width = FileTreePaneMinWidth;
            if (IsEditorVisible) width += SplitterWidth + EditorPaneMinWidth;
            if (IsReviewVisible) width += SplitterWidth + ReviewPaneMinWidth;
            return width;
        }
    }

    private bool IsReviewVisible => _viewModel?.IsReviewVisible == true;
    private bool IsEditorVisible => _viewModel?.IsEditorVisible == true;

    private static IConfigService? GetConfigService() =>
        App.Services?.GetService(typeof(IConfigService)) as IConfigService;

    private void OnWorkbenchDataContextChanged(object? sender, EventArgs e) =>
        AttachViewModel(DataContext as WorkspaceWorkbenchViewModel);

    private void AttachViewModel(WorkspaceWorkbenchViewModel? viewModel)
    {
        if (ReferenceEquals(_viewModel, viewModel)) return;
        if (_viewModel != null) _viewModel.PropertyChanged -= OnWorkbenchPropertyChanged;
        _viewModel = viewModel;
        if (_viewModel != null) _viewModel.PropertyChanged += OnWorkbenchPropertyChanged;
        NotifyMinimumRequiredWidthChanged();
        ApplyPaneLayoutFromPreferences();
    }

    private void OnWorkbenchPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (
            nameof(WorkspaceWorkbenchViewModel.IsReviewVisible)
            or nameof(WorkspaceWorkbenchViewModel.IsEditorVisible)))
        {
            return;
        }

        NotifyMinimumRequiredWidthChanged();
        QueuePaneLayoutFromPreferences();
    }

    private void QueuePaneLayoutFromPreferences()
    {
        if (_isPaneLayoutQueued) return;
        _isPaneLayoutQueued = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                _isPaneLayoutQueued = false;
                ApplyPaneLayoutFromPreferences();
            },
            DispatcherPriority.Loaded);
    }

    private void OnWorkbenchSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (_isApplyingLayout || e.NewSize.Width <= 0) return;
        if (_lastWorkbenchWidth <= 0)
        {
            ApplyPaneLayoutFromPreferences();
            return;
        }

        var delta = e.NewSize.Width - _lastWorkbenchWidth;
        if (Math.Abs(delta) < 0.01) return;
        ResizeForOuterDelta(delta);
        ApplyColumns();
        _lastWorkbenchWidth = e.NewSize.Width;
    }

    private void ApplyPaneLayoutFromPreferences()
    {
        if (_reviewColumn == null
            || _reviewSplitterColumn == null
            || _editorColumn == null
            || _editorSplitterColumn == null
            || _fileTreeColumn == null)
        {
            return;
        }

        var paneSpace = Math.Max(
            GetMinimumPaneSpace(),
            Math.Max(0, Bounds.Width - GetVisibleSplitterWidth()));

        _actualReviewWidth = IsReviewVisible ? _preferredReviewWidth : 0;
        _actualEditorWidth = IsEditorVisible ? _preferredEditorWidth : 0;
        _actualFileTreeWidth = _preferredFileTreeWidth;

        var requested = GetActualPaneWidth();
        if (requested > paneSpace)
            ShrinkOuterToInner(requested - paneSpace);
        else if (requested < paneSpace)
            GrowInnerToOuter(paneSpace - requested);

        ApplyColumns();
        // When a newly opened pane temporarily needs more room than the host
        // currently owns, the columns can be wider than Bounds until the shell
        // completes its own resize. Track the allocated total, otherwise that
        // later host growth would be counted a second time.
        _lastWorkbenchWidth = GetActualPaneWidth() + GetVisibleSplitterWidth();
    }

    private void ResizeForOuterDelta(double delta)
    {
        if (delta < 0)
            ShrinkOuterToInner(-delta);
        else
            GrowInnerToOuter(delta);
    }

    private void ShrinkOuterToInner(double amount)
    {
        if (IsReviewVisible)
            amount = Reduce(ref _actualReviewWidth, ReviewPaneMinWidth, amount);
        if (amount > 0 && IsEditorVisible)
            amount = Reduce(ref _actualEditorWidth, EditorPaneMinWidth, amount);
        if (amount > 0)
            Reduce(ref _actualFileTreeWidth, FileTreePaneMinWidth, amount);
    }

    private void GrowInnerToOuter(double amount)
    {
        amount = Restore(ref _actualFileTreeWidth, _preferredFileTreeWidth, amount);
        if (amount > 0 && IsEditorVisible)
            amount = Restore(ref _actualEditorWidth, _preferredEditorWidth, amount);
        if (amount > 0 && IsReviewVisible)
            amount = Restore(ref _actualReviewWidth, _preferredReviewWidth, amount);

        if (amount <= 0) return;
        if (IsReviewVisible)
            _actualReviewWidth += amount;
        else if (IsEditorVisible)
            _actualEditorWidth += amount;
        else
            _actualFileTreeWidth += amount;
    }

    private void ApplyColumns()
    {
        if (_reviewColumn == null
            || _reviewSplitterColumn == null
            || _editorColumn == null
            || _editorSplitterColumn == null
            || _fileTreeColumn == null)
        {
            return;
        }

        _isApplyingLayout = true;
        try
        {
            SetColumn(_reviewColumn, IsReviewVisible ? _actualReviewWidth : 0, IsReviewVisible ? ReviewPaneMinWidth : 0);
            SetColumn(_reviewSplitterColumn, IsReviewVisible ? SplitterWidth : 0, IsReviewVisible ? SplitterWidth : 0);

            if (IsEditorVisible)
            {
                SetColumn(_editorColumn, _actualEditorWidth, EditorPaneMinWidth);
                SetColumn(_editorSplitterColumn, SplitterWidth, SplitterWidth);
                SetColumn(_fileTreeColumn, _actualFileTreeWidth, FileTreePaneMinWidth);
            }
            else
            {
                // The browser spans columns 2–4 while the editor is hidden. Keeping
                // its width in column 2 also makes the review splitter resize the
                // review and file tree as true adjacent panes.
                SetColumn(_editorColumn, _actualFileTreeWidth, FileTreePaneMinWidth);
                SetColumn(_editorSplitterColumn, 0, 0);
                SetColumn(_fileTreeColumn, 0, 0);
            }
        }
        finally
        {
            _isApplyingLayout = false;
        }
    }

    private static void SetColumn(ColumnDefinition column, double width, double minWidth)
    {
        column.MinWidth = minWidth;
        column.Width = new GridLength(Math.Max(minWidth, width));
    }

    private static double Reduce(ref double width, double minWidth, double amount)
    {
        var reduction = Math.Min(Math.Max(0, width - minWidth), amount);
        width -= reduction;
        return amount - reduction;
    }

    private static double Restore(ref double width, double preferredWidth, double amount)
    {
        var increase = Math.Min(Math.Max(0, preferredWidth - width), amount);
        width += increase;
        return amount - increase;
    }

    private double GetActualPaneWidth() =>
        _actualFileTreeWidth
        + (IsEditorVisible ? _actualEditorWidth : 0)
        + (IsReviewVisible ? _actualReviewWidth : 0);

    private double GetMinimumPaneSpace() =>
        FileTreePaneMinWidth
        + (IsEditorVisible ? EditorPaneMinWidth : 0)
        + (IsReviewVisible ? ReviewPaneMinWidth : 0);

    private double GetVisibleSplitterWidth() =>
        (IsEditorVisible ? SplitterWidth : 0)
        + (IsReviewVisible ? SplitterWidth : 0);

    private void SyncActualWidthsFromColumns()
    {
        if (_reviewColumn == null || _editorColumn == null || _fileTreeColumn == null) return;
        _actualReviewWidth = IsReviewVisible ? _reviewColumn.ActualWidth : 0;
        if (IsEditorVisible)
        {
            _actualEditorWidth = _editorColumn.ActualWidth;
            _actualFileTreeWidth = _fileTreeColumn.ActualWidth;
        }
        else
        {
            _actualEditorWidth = 0;
            _actualFileTreeWidth = _editorColumn.ActualWidth;
        }
    }

    private void NotifyMinimumRequiredWidthChanged()
    {
        var minimum = MinimumRequiredWidth;
        if (Math.Abs(minimum - _lastMinimumRequiredWidth) < 0.01) return;
        _lastMinimumRequiredWidth = minimum;
        MinimumRequiredWidthChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnFileDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is WorkspaceWorkbenchViewModel viewModel
            && viewModel.SelectedFile is { IsDirectory: false, IsRenaming: false } file)
        {
            viewModel.OpenFileCommand.Execute(file);
        }
    }

    private void OnGitChangeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not WorkspaceWorkbenchViewModel viewModel
            || e.Source is not StyledElement source)
        {
            return;
        }

        // 差异文件改为双击才在文件编辑区切换 diff 视图；
        // 单击仅更新选中高亮。双击行内操作按钮时不触发切换。
        for (var element = source; element != null; element = element.Parent as StyledElement)
        {
            if (element is Button) return;
            if (element is ListBoxItem { DataContext: GitChangeFileViewModel change })
            {
                viewModel.OpenGitChangeCommand.Execute(change);
                return;
            }
        }
    }

    private void OnCommitMessageKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter
            || (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) == 0)
        {
            return;
        }

        if (DataContext is not WorkspaceWorkbenchViewModel viewModel) return;
        e.Handled = true;
        if (viewModel.CommitCommand.CanExecute(null))
            viewModel.CommitCommand.Execute(null);
    }

    private void OnRenameTextBoxPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Visual.IsVisibleProperty && sender is TextBox { IsVisible: true } textBox)
            QueueRenameTextBoxFocus(textBox, selectAll: true);
    }

    private async void OnRenameTextBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox textBox
            || textBox.DataContext is not WorkspaceFileNodeViewModel node
            || DataContext is not WorkspaceWorkbenchViewModel viewModel)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            viewModel.CancelRenameFileCommand.Execute(node);
            return;
        }

        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await viewModel.CommitRenameFileCommand.ExecuteAsync(node);
        if (node.IsRenaming) QueueRenameTextBoxFocus(textBox, selectAll: false);
    }

    private async void OnRenameTextBoxLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox textBox
            || textBox.DataContext is not WorkspaceFileNodeViewModel { IsRenaming: true } node
            || DataContext is not WorkspaceWorkbenchViewModel viewModel)
        {
            return;
        }

        await viewModel.CommitRenameFileCommand.ExecuteAsync(node);
        if (node.IsRenaming) QueueRenameTextBoxFocus(textBox, selectAll: false);
    }

    private static void QueueRenameTextBoxFocus(TextBox textBox, bool selectAll)
    {
        Dispatcher.UIThread.Post(
            () =>
            {
                if (!textBox.IsVisible
                    || textBox.DataContext is not WorkspaceFileNodeViewModel { IsRenaming: true })
                {
                    return;
                }

                textBox.Focus();
                if (selectAll) textBox.SelectAll();
            },
            DispatcherPriority.Background);
    }

    private void OnReviewSplitterDragStarted(object? sender, VectorEventArgs e)
    {
        SyncActualWidthsFromColumns();
        _internalDragFirstStartWidth = _actualReviewWidth;
        _internalDragSecondStartWidth = IsEditorVisible
            ? _actualEditorWidth
            : _actualFileTreeWidth;
        _internalDragCumulativeDelta = 0;
    }

    private void OnReviewSplitterDragDelta(object? sender, VectorEventArgs e)
    {
        _internalDragCumulativeDelta += e.Vector.X;
        var pairWidth = _internalDragFirstStartWidth + _internalDragSecondStartWidth;
        var secondMinWidth = IsEditorVisible ? EditorPaneMinWidth : FileTreePaneMinWidth;
        var reviewWidth = Math.Clamp(
            _internalDragFirstStartWidth + _internalDragCumulativeDelta,
            ReviewPaneMinWidth,
            pairWidth - secondMinWidth);

        _actualReviewWidth = reviewWidth;
        if (IsEditorVisible)
            _actualEditorWidth = pairWidth - reviewWidth;
        else
            _actualFileTreeWidth = pairWidth - reviewWidth;
        ApplyColumns();
        e.Handled = true;
    }

    private async void OnReviewSplitterDragCompleted(object? sender, VectorEventArgs e)
    {
        SyncActualWidthsFromColumns();
        _preferredReviewWidth = Math.Max(ReviewPaneMinWidth, _actualReviewWidth);
        if (IsEditorVisible)
            _preferredEditorWidth = Math.Max(EditorPaneMinWidth, _actualEditorWidth);
        else
            _preferredFileTreeWidth = Math.Max(FileTreePaneMinWidth, _actualFileTreeWidth);
        await SavePreferredWidthsAsync();
    }

    private void OnEditorSplitterDragStarted(object? sender, VectorEventArgs e)
    {
        SyncActualWidthsFromColumns();
        _internalDragFirstStartWidth = _actualEditorWidth;
        _internalDragSecondStartWidth = _actualFileTreeWidth;
        _internalDragCumulativeDelta = 0;
    }

    private void OnEditorSplitterDragDelta(object? sender, VectorEventArgs e)
    {
        _internalDragCumulativeDelta += e.Vector.X;
        var pairWidth = _internalDragFirstStartWidth + _internalDragSecondStartWidth;
        var editorWidth = Math.Clamp(
            _internalDragFirstStartWidth + _internalDragCumulativeDelta,
            EditorPaneMinWidth,
            pairWidth - FileTreePaneMinWidth);

        _actualEditorWidth = editorWidth;
        _actualFileTreeWidth = pairWidth - editorWidth;
        ApplyColumns();
        e.Handled = true;
    }

    private async void OnEditorSplitterDragCompleted(object? sender, VectorEventArgs e)
    {
        SyncActualWidthsFromColumns();
        _preferredEditorWidth = Math.Max(EditorPaneMinWidth, _actualEditorWidth);
        _preferredFileTreeWidth = Math.Max(FileTreePaneMinWidth, _actualFileTreeWidth);
        await SavePreferredWidthsAsync();
    }

    private async Task SavePreferredWidthsAsync()
    {
        var configService = GetConfigService();
        if (configService == null) return;
        var config = configService.Load();
        config.MainLayout.ReviewWidth = _preferredReviewWidth;
        config.MainLayout.EditorWidth = _preferredEditorWidth;
        config.MainLayout.FileTreeWidth = _preferredFileTreeWidth;
        await configService.SaveAsync(config);
    }
}
