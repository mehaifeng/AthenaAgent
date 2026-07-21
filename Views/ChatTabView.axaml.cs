using Athena.UI.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Athena.UI.Views;

public partial class ChatTabView : UserControl
{
    private ScrollViewer? _chatScrollViewer;
    private TextBox? _messageInputTextBox;
    private ChatTabViewModel? _viewModel;
    private bool _isUserScrolling;
    private double _lastScrollOffset;

    // ===== 子代理派生动效 =====
    private INotifyCollectionChanged? _subAgentsCollection;
    // 在途猫头鹰数：既作编队序号（错开每只的轨迹），也用于最后一只落地时才触发按钮脉冲。
    private int _activeOwlFlights;
    private static readonly Random _flightRng = new();
    private static readonly Bitmap[] OwlFlightFrames = LoadOwlFlightFrames();

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
            _viewModel.HistoryConversationLoaded -= OnHistoryConversationLoaded;
        }
        if (_subAgentsCollection != null)
        {
            _subAgentsCollection.CollectionChanged -= OnActiveSubAgentsChanged;
            _subAgentsCollection = null;
        }

        if (DataContext is ChatTabViewModel viewModel)
        {
            _viewModel = viewModel;
            viewModel.Messages.CollectionChanged += OnMessagesCollectionChanged;
            viewModel.PropertyChanged += OnViewModelPropertyChanged;
            viewModel.HistoryConversationLoaded += OnHistoryConversationLoaded;

            _subAgentsCollection = viewModel.Orchestrator?.ActiveAgents;
            if (_subAgentsCollection != null)
            {
                _subAgentsCollection.CollectionChanged += OnActiveSubAgentsChanged;
            }
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

    private void OnHistoryConversationLoaded(object? sender, EventArgs e)
        => ScrollToBottomIfHasMessages();

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

    // 子代理弹窗遮罩：点击关闭。用 Border（无悬停态）避免遮罩随光标变色。
    private void OnSubAgentScrimPressed(object? sender, PointerPressedEventArgs e)
        => _viewModel?.CloseSubAgentPopupCommand.Execute(null);

    // ===== 猫头鹰派生飞行动画 =====
    // 编排器每派生一只猫头鹰就往 ActiveAgents 加一项（已在 UI 线程），
    // 正好一一对应"从助手气泡飞出一只猫头鹰"的时机。同批多只同时起飞、轨迹互相错开。
    private void OnActiveSubAgentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || e.NewItems == null) return;
        if (!IsEffectivelyVisible) return;

        for (var i = 0; i < e.NewItems.Count; i++)
        {
            _ = SpawnOwlFlightAsync();
        }
    }

    /// <summary>
    /// 从最后一条助手气泡处淡入一只猫头鹰，沿专属二次贝塞尔弧线飞向 Sub-Agents 按钮后淡出。
    /// 手动逐帧驱动（约 60fps），不依赖框架动画管线；同批编队按序号错开弧顶高度与横向偏移。
    /// </summary>
    private async Task SpawnOwlFlightAsync()
    {
        var overlay = this.FindControl<Canvas>("OwlFlightOverlay");
        var button = this.FindControl<Button>("SubAgentsButton");
        if (overlay == null || button == null) return;

        var variant = _activeOwlFlights++;
        Image? owl = null;
        try
        {
            // 同批编队几乎同时起飞，仅加一点随机差让振翅更自然。
            await Task.Delay(_flightRng.Next(0, 130));

            var end = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), overlay);
            if (end == null) return;
            var start = GetOwlFlightStartPoint(overlay)
                        ?? new Point(overlay.Bounds.Width / 2, overlay.Bounds.Height / 2);

            const double size = 26;
            var translate = new TranslateTransform(start.X - size / 2, start.Y - size / 2);
            var facesLeft = end.Value.X < start.X;
            owl = new Image
            {
                Source = OwlFlightFrames[0],
                Width = size,
                Height = size,
                Opacity = 0,
                IsHitTestVisible = false,
                RenderTransformOrigin = RelativePoint.Center,
                RenderTransform = new TransformGroup
                {
                    Children =
                    {
                        new ScaleTransform(facesLeft ? -1 : 1, 1),
                        translate
                    }
                }
            };
            overlay.Children.Add(owl);

            // 每只一条专属弧线：弧顶高度与横向偏移随编队序号左右错开，避免轨迹重叠。
            var lift = 50 + variant * 32 + _flightRng.Next(0, 14);
            var side = (variant % 2 == 0 ? 1 : -1) * (16 + variant * 15);
            var control = new Point(
                (start.X + end.Value.X) / 2 + side,
                Math.Max(10, Math.Min(start.Y, end.Value.Y) - lift));

            var durationMs = 1600.0 + variant * 120;
            var sw = Stopwatch.StartNew();
            while (true)
            {
                var t = Math.Min(1.0, sw.Elapsed.TotalMilliseconds / durationMs);
                var p = EaseInOutCubic(t);
                var inv = 1 - p;
                var x = inv * inv * start.X + 2 * inv * p * control.X + p * p * end.Value.X;
                var y = inv * inv * start.Y + 2 * inv * p * control.Y + p * p * end.Value.Y;
                translate.X = x - size / 2;
                translate.Y = y - size / 2;
                owl.Source = OwlFlightFrames[(int)(sw.Elapsed.TotalMilliseconds / 95) % OwlFlightFrames.Length];
                // 前 15% 淡入、末 18% 淡出。
                owl.Opacity = t < 0.15 ? t / 0.15 : t > 0.82 ? Math.Max(0, (1 - t) / 0.18) : 1.0;

                if (t >= 1) break;
                await Task.Delay(15);
            }
        }
        catch (Exception ex)
        {
            // 纯装饰性动画，任何布局竞态导致的异常都不应影响对话流程。
            Serilog.Log.Debug(ex, "Owl flight animation failed");
        }
        finally
        {
            if (owl != null) overlay.Children.Remove(owl);
            // 最后一只落地才弹跳一次，避免多只连续触发过于跳脱。
            if (--_activeOwlFlights == 0) _ = PulseSubAgentsEntryAsync();
        }
    }

    private static double EaseInOutCubic(double t)
        => t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;

    private static Bitmap[] LoadOwlFlightFrames()
    {
        var frames = new Bitmap[4];
        for (var i = 0; i < frames.Length; i++)
        {
            using var stream = AssetLoader.Open(new Uri($"avares://Athena.UI/Assets/SubAgents/owl-fly-{i + 1:D2}.png"));
            frames[i] = new Bitmap(stream);
        }
        return frames;
    }

    /// <summary>起点：最后一条助手气泡内靠左下（流式输出/工具调用的视觉位置附近）。</summary>
    private Point? GetOwlFlightStartPoint(Canvas overlay)
    {
        var itemsControl = this.FindControl<ItemsControl>("MessagesItemsControl");
        if (_viewModel == null || itemsControl == null) return null;

        var message = _viewModel.Messages.LastOrDefault(m => !m.IsUser);
        if (message == null) return null;

        var container = itemsControl.ContainerFromItem(message);
        var bubble = container?.GetVisualDescendants().OfType<Border>()
            .FirstOrDefault(b => b.Classes.Contains("assistant-bubble"));
        if (bubble == null || bubble.Bounds.Width <= 0) return null;

        var local = new Point(
            Math.Min(bubble.Bounds.Width * 0.5, 140),
            bubble.Bounds.Height - Math.Min(26, bubble.Bounds.Height / 2));
        return bubble.TranslatePoint(local, overlay);
    }

    /// <summary>猫头鹰落地反馈：Sub-Agents 入口（按钮 + 角标）整体轻弹一下（手动逐帧，正弦单峰）。</summary>
    private async Task PulseSubAgentsEntryAsync()
    {
        var entry = this.FindControl<Panel>("SubAgentsEntry");
        if (entry?.RenderTransform is not ScaleTransform scale) return;

        try
        {
            const double durationMs = 280;
            var sw = Stopwatch.StartNew();
            while (true)
            {
                var t = Math.Min(1.0, sw.Elapsed.TotalMilliseconds / durationMs);
                var s = 1 + 0.12 * Math.Sin(Math.PI * t);
                scale.ScaleX = s;
                scale.ScaleY = s;
                if (t >= 1) break;
                await Task.Delay(15);
            }
        }
        finally
        {
            scale.ScaleX = 1.0;
            scale.ScaleY = 1.0;
        }
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
