using Athena.UI.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;

namespace Athena.UI.Views;

public partial class ChatTabView : UserControl
{
    private ScrollViewer? _chatScrollViewer;
    private TextBox? _messageInputTextBox;
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
        if (_viewModel != null)
        {
            _viewModel.Messages.CollectionChanged -= OnMessagesCollectionChanged;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        if (DataContext is ChatTabViewModel viewModel)
        {
            _viewModel = viewModel;
            viewModel.Messages.CollectionChanged += OnMessagesCollectionChanged;
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
        else
        {
            _viewModel = null;
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

    private void OnIsVisibleChanged(bool isVisible)
    {
        if (isVisible) ScrollToBottomIfHasMessages();
    }

    public void ScrollToBottomIfHasMessages()
    {
        if (_viewModel?.Messages.Count > 0)
        {
            _isUserScrolling = false;
            ScrollToBottom();
        }
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

        _messageInputTextBox = this.FindControl<TextBox>("MessageInputTextBox");
        if (_messageInputTextBox != null)
        {
            _messageInputTextBox.PastingFromClipboard += OnMessageInputPastingFromClipboard;
        }

        ScrollToBottomIfHasMessages();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsVisibleProperty && change.GetNewValue<bool>())
        {
            OnIsVisibleChanged(true);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_chatScrollViewer != null)
        {
            _chatScrollViewer.ScrollChanged -= OnScrollChanged;
            _chatScrollViewer.PointerPressed -= OnPointerPressed;
            _chatScrollViewer.PointerReleased -= OnPointerReleased;
            _chatScrollViewer.PointerWheelChanged -= OnPointerWheelChanged;
        }
        _chatScrollViewer = null;
        if (_messageInputTextBox != null)
        {
            _messageInputTextBox.PastingFromClipboard -= OnMessageInputPastingFromClipboard;
        }
        _messageInputTextBox = null;
        base.OnDetachedFromVisualTree(e);
    }

    private async void OnMessageInputPastingFromClipboard(object? sender, RoutedEventArgs e)
    {
        if (_viewModel == null) return;

        var textBox = sender as TextBox;
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard == null) return;
        e.Handled = true;

        var bitmap = await clipboard.TryGetBitmapAsync();
        if (bitmap != null)
        {
            e.Handled = true;
            await _viewModel.AddClipboardBitmapAsync(bitmap);
            return;
        }

        var files = await clipboard.TryGetFilesAsync();
        var imageFiles = files?
            .OfType<IStorageFile>()
            .Where(file => IsSupportedImageName(file.Name))
            .ToList();

        if (imageFiles?.Count > 0)
        {
            await _viewModel.AddStorageFilesAsync(imageFiles);
            return;
        }

        var text = await clipboard.TryGetTextAsync();
        if (!string.IsNullOrEmpty(text) && textBox != null)
        {
            InsertText(textBox, text);
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
        var scrollViewer = _chatScrollViewer;
        if (scrollViewer != null)
        {
            Dispatcher.UIThread.Post(() => scrollViewer.ScrollToEnd(), DispatcherPriority.Loaded);
            Dispatcher.UIThread.Post(() => scrollViewer.ScrollToEnd(), DispatcherPriority.Background);
        }
    }

    private static bool IsSupportedImageName(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".webp", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".gif", StringComparison.OrdinalIgnoreCase);
    }

    private static void InsertText(TextBox textBox, string text)
    {
        var current = textBox.Text ?? string.Empty;
        var start = Math.Min(textBox.SelectionStart, textBox.SelectionEnd);
        var end = Math.Max(textBox.SelectionStart, textBox.SelectionEnd);
        textBox.Text = current.Remove(start, end - start).Insert(start, text);
        textBox.CaretIndex = start + text.Length;
        textBox.SelectionStart = textBox.CaretIndex;
        textBox.SelectionEnd = textBox.CaretIndex;
    }
}
