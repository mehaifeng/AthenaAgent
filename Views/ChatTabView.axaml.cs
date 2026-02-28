using Athena.UI.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Athena.UI.Views;

public partial class ChatTabView : UserControl
{
    private ScrollViewer? _chatScrollViewer;
    private ChatTabViewModel? _viewModel;
    private bool _isUserScrolling;
    private double _lastScrollOffset;

    public ChatTabView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (DataContext is ChatTabViewModel viewModel)
        {
            _viewModel = viewModel;
            viewModel.Messages.CollectionChanged += OnMessagesCollectionChanged;
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatTabViewModel.IsSending))
        {
            if (_viewModel?.IsSending == true) _isUserScrolling = false;
        }
    }

    private void OnMessagesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && !_isUserScrolling) ScrollToBottom();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _chatScrollViewer = this.FindControl<ScrollViewer>("ChatScrollViewer");
        if (_chatScrollViewer != null)
        {
            _chatScrollViewer.ScrollChanged += OnScrollChanged;
            _chatScrollViewer.PointerPressed += OnPointerPressed;
            _chatScrollViewer.PointerReleased += OnPointerReleased;
            _chatScrollViewer.PointerWheelChanged += OnPointerWheelChanged;
        }
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_chatScrollViewer == null) return;
        var currentOffset = _chatScrollViewer.Offset.Y;
        var maxOffset = _chatScrollViewer.Extent.Height - _chatScrollViewer.Viewport.Height;
        if (currentOffset >= maxOffset - 5) _isUserScrolling = false;
        else if (currentOffset < _lastScrollOffset - 5) _isUserScrolling = true;
        _lastScrollOffset = currentOffset;
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e) => _isUserScrolling = true;
    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e) => CheckIfAtBottom();
    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (e.Delta.Y > 0) _isUserScrolling = true;
        Avalonia.Threading.Dispatcher.UIThread.Post(CheckIfAtBottom, Avalonia.Threading.DispatcherPriority.Background);
    }

    private void CheckIfAtBottom()
    {
        if (_chatScrollViewer == null) return;
        if (_chatScrollViewer.Offset.Y >= (_chatScrollViewer.Extent.Height - _chatScrollViewer.Viewport.Height) - 5) _isUserScrolling = false;
    }

    private void ScrollToBottom()
    {
        if (_chatScrollViewer != null)
            Avalonia.Threading.Dispatcher.UIThread.Post(() => _chatScrollViewer.ScrollToEnd(), Avalonia.Threading.DispatcherPriority.Background);
    }
}
