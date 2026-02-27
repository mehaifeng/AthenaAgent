using Athena.UI.Models;
using Athena.UI.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using System.Collections.Specialized;

namespace Athena.UI.Views;

public partial class MainWindow : Window
{
    private ScrollViewer? _chatScrollViewer;
    private MainWindowViewModel? _viewModel;
    private bool _isUserScrolling;
    private double _lastScrollOffset;

    public MainWindow()
    {
        InitializeComponent();

        // 监听 DataContext 变化
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            _viewModel = viewModel;
            // 监听消息集合变化
            viewModel.Messages.CollectionChanged += OnMessagesCollectionChanged;
            // 监听属性变化
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.IsUserScrolling))
        {
            // 当 ViewModel 重置滚动状态时，同步重置
            if (_viewModel != null && !_viewModel.IsUserScrolling)
            {
                _isUserScrolling = false;
            }
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.IsSending))
        {
            // 当开始发送消息时，重置滚动状态
            if (_viewModel?.IsSending == true)
            {
                _isUserScrolling = false;
            }
        }
    }

    private void OnMessagesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // 当有新消息添加时，只有在用户没有手动滚动时才自动滚动
        if (e.Action == NotifyCollectionChangedAction.Add)
        {
            if (!_isUserScrolling)
            {
                ScrollToBottom();
            }
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // 获取 ScrollViewer 引用
        _chatScrollViewer = this.FindControl<ScrollViewer>("ChatScrollViewer");

        if (_chatScrollViewer != null)
        {
            // 监听滚动事件
            _chatScrollViewer.ScrollChanged += OnScrollChanged;

            // 监听指针事件（检测用户主动滚动）
            _chatScrollViewer.PointerPressed += OnPointerPressed;
            _chatScrollViewer.PointerReleased += OnPointerReleased;
            _chatScrollViewer.PointerWheelChanged += OnPointerWheelChanged;
        }
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_chatScrollViewer == null || _viewModel == null)
            return;

        var currentOffset = _chatScrollViewer.Offset.Y;
        var extentHeight = _chatScrollViewer.Extent.Height;
        var viewportHeight = _chatScrollViewer.Viewport.Height;
        var maxOffset = extentHeight - viewportHeight;

        // 检查是否滚动到底部（允许 5 像素的误差）
        var isAtBottom = currentOffset >= maxOffset - 5;

        if (isAtBottom)
        {
            // 用户滚动到底部，重置状态
            _isUserScrolling = false;
            _viewModel.IsUserScrolling = false;
        }
        else if (currentOffset < _lastScrollOffset - 5)
        {
            // 用户向上滚动
            _isUserScrolling = true;
            _viewModel.IsUserScrolling = true;
        }

        _lastScrollOffset = currentOffset;
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // 用户开始触摸/点击滚动条
        _isUserScrolling = true;
        if (_viewModel != null)
        {
            _viewModel.IsUserScrolling = true;
        }
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        // 检查释放时是否在底部
        CheckIfAtBottom();
    }

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        // 鼠标滚轮滚动
        if (_chatScrollViewer == null)
            return;

        // 向上滚动时标记用户正在滚动
        if (e.Delta.Y > 0)
        {
            _isUserScrolling = true;
            if (_viewModel != null)
            {
                _viewModel.IsUserScrolling = true;
            }
        }

        // 延迟检查是否在底部
        Avalonia.Threading.Dispatcher.UIThread.Post(CheckIfAtBottom, Avalonia.Threading.DispatcherPriority.Background);
    }

    private void CheckIfAtBottom()
    {
        if (_chatScrollViewer == null)
            return;

        var currentOffset = _chatScrollViewer.Offset.Y;
        var extentHeight = _chatScrollViewer.Extent.Height;
        var viewportHeight = _chatScrollViewer.Viewport.Height;
        var maxOffset = extentHeight - viewportHeight;

        // 检查是否滚动到底部
        var isAtBottom = currentOffset >= maxOffset - 5;

        if (isAtBottom)
        {
            _isUserScrolling = false;
            if (_viewModel != null)
            {
                _viewModel.IsUserScrolling = false;
            }
        }
    }

    private void ScrollToBottom()
    {
        if (_chatScrollViewer != null)
        {
            // 使用 Dispatcher 确保在 UI 更新后滚动
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _chatScrollViewer.ScrollToEnd();
            }, Avalonia.Threading.DispatcherPriority.Background);
        }
    }
}
