using Athena.UI.Services;
using Athena.UI.Controls;
using Athena.UI.Models;
using Athena.UI.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.Composition.Animations;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Linq;
using System.Threading.Tasks;

namespace Athena.UI.Views;

public partial class MainConversationView : UserControl
{
    public static readonly StyledProperty<WorkspaceWorkbenchViewModel?> WorkbenchProperty =
        AvaloniaProperty.Register<MainConversationView, WorkspaceWorkbenchViewModel?>(nameof(Workbench));

    public WorkspaceWorkbenchViewModel? Workbench
    {
        get => GetValue(WorkbenchProperty);
        set => SetValue(WorkbenchProperty, value);
    }

    /// <summary>
    /// 会话切换幕布的开关，由外壳（MainWindow）驱动。
    ///
    /// 幕布之所以住在这个视图里而不是外壳里，是为了让骨架屏能**精确套用消息列表自己的几何**：
    /// 它就挂在 ScrollViewer 同一个 Grid.Row 上，宽高、12px 外边距、ContentMaxWidth 上限
    /// 全部与真实气泡同源，不需要在外壳里复刻一份「标题栏 40px、输入区多高」的魔法数字。
    /// 走 StyledProperty 而不是向上绑 MainWindowViewModel：与 <see cref="Workbench"/> 同一形状，
    /// 视图层不因此认识外壳的 VM。
    /// </summary>
    public static readonly StyledProperty<bool> IsSwitchingProperty =
        AvaloniaProperty.Register<MainConversationView, bool>(nameof(IsSwitching));

    public bool IsSwitching
    {
        get => GetValue(IsSwitchingProperty);
        set => SetValue(IsSwitchingProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != IsSwitchingProperty) return;
        if (change.GetNewValue<bool>()) StartVeilAnimations();
        else StopVeilAnimations();
    }

    /// <summary>
    /// 幕布加载动效：猫头鹰呼吸 + 三点依次亮灭，全部走 **合成器动画**（渲染线程）。
    ///
    /// 这一条是整个会话切换里唯一没有替代方案的地方。幕布存在的那 2~3 秒，UI 线程正冻在
    /// 气泡树的首次布局上；Avalonia 的 Animation / Transition 由 UI 线程的动画时钟推进，
    /// 写成 XAML 关键帧就会在那一刻定格在第一帧——一个不动的"加载中"比没有加载动画更像崩溃。
    /// ElementComposition 拿到的 CompositionVisual 上启动的动画由渲染线程求值，UI 线程冻着也照跑。
    ///
    /// 只在幕布升起时启动、落下时停掉：一个常驻的渲染线程动画会让合成器每帧都有活干，
    /// 而这块面板 99% 的时间是静止的。
    /// </summary>
    private void StartVeilAnimations()
    {
        // 幕布刚被设为可见，这一帧还没布局，合成视觉可能尚未建立；退一拍再试一次。
        // Render(7) 高于我们自己的一切后续调度，且换绑要等 RowSelectionSettle（380ms），时间绰绰有余。
        if (!TryStartVeilAnimations())
            Dispatcher.UIThread.Post(() => { if (IsSwitching) TryStartVeilAnimations(); }, DispatcherPriority.Loaded);
    }

    internal bool TryStartVeilAnimations()
    {
        var owl = this.FindControl<Panel>("ConversationSwitchVeilOwl");
        if (owl == null) return false;
        var owlVisual = ElementComposition.GetElementVisual(owl);
        if (owlVisual == null) return false;
        var compositor = owlVisual.Compositor;

        // 呼吸：缩放中心必须显式给到控件中点，否则围绕左上角缩放，读起来是"在抖"而不是"在呼吸"。
        owlVisual.CenterPoint = new Vector3((float)owl.Width / 2f, (float)owl.Height / 2f, 0f);
        var breath = compositor.CreateVector3KeyFrameAnimation();
        breath.InsertKeyFrame(0f, new Vector3(1f, 1f, 1f));
        breath.InsertKeyFrame(0.5f, new Vector3(VeilOwlBreathScale, VeilOwlBreathScale, 1f));
        breath.InsertKeyFrame(1f, new Vector3(1f, 1f, 1f));
        breath.Duration = VeilOwlBreathDuration;
        breath.IterationBehavior = AnimationIterationBehavior.Forever;
        owlVisual.StartAnimation("Scale", breath);

        for (var index = 0; index < VeilDotCount; index++)
        {
            var dot = this.FindControl<Border>("ConversationSwitchVeilDot" + index.ToString(CultureInfo.InvariantCulture));
            if (dot == null) continue;
            var dotVisual = ElementComposition.GetElementVisual(dot);
            if (dotVisual == null) continue;
            // 首帧是**最亮**的一端，不是最暗的。动画万一没跑起来（拿不到合成视觉、某个平台
            // 把合成器放在 UI 线程上），停在首帧的点就是"完全可见"而不是"几乎看不见"——
            // 与工作区窗格进场那条同一个道理：静态回退必须是可见态，坏掉只该丢动效、不该丢元素。
            var pulse = compositor.CreateScalarKeyFrameAnimation();
            pulse.InsertKeyFrame(0f, 1f);
            pulse.InsertKeyFrame(0.5f, VeilDotMinOpacity);
            pulse.InsertKeyFrame(1f, 1f);
            pulse.Duration = VeilDotPulseDuration;
            // 三点错开三分之一个周期，读起来是"一道光在走"，同相位就成了整排一起闪。
            pulse.DelayTime = VeilDotPulseDuration / VeilDotCount * index;
            // 延迟期间必须先把首帧（最亮）写进去。默认行为是延迟结束才写初值，在那之前
            // 合成属性停在 0——实测截帧里后两个点整个不见，只剩第一个亮着。
            pulse.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
            pulse.IterationBehavior = AnimationIterationBehavior.Forever;
            dotVisual.StartAnimation("Opacity", pulse);
        }
        return true;
    }

    private void StopVeilAnimations()
    {
        var owl = this.FindControl<Panel>("ConversationSwitchVeilOwl");
        if (owl != null && ElementComposition.GetElementVisual(owl) is { } owlVisual)
        {
            owlVisual.StopAnimation("Scale");
            owlVisual.Scale = new Vector3(1f, 1f, 1f);
        }
        for (var index = 0; index < VeilDotCount; index++)
        {
            var dot = this.FindControl<Border>("ConversationSwitchVeilDot" + index.ToString(CultureInfo.InvariantCulture));
            if (dot != null && ElementComposition.GetElementVisual(dot) is { } dotVisual)
            {
                dotVisual.StopAnimation("Opacity");
                dotVisual.Opacity = 1f;
            }
        }
    }

    internal const int VeilDotCount = 3;
    internal const float VeilOwlBreathScale = 1.07f;
    internal const float VeilDotMinOpacity = 0.2f;
    internal static readonly TimeSpan VeilOwlBreathDuration = TimeSpan.FromMilliseconds(2200);
    internal static readonly TimeSpan VeilDotPulseDuration = TimeSpan.FromMilliseconds(1050);

    private ScrollViewer? _chatScrollViewer;
    private TextBox? _messageInputTextBox;
    private MainConversationViewModel? _viewModel;
    private bool _isUserScrolling;
    private double _lastScrollOffset;

    // ===== 子代理派生动效 =====
    private INotifyCollectionChanged? _subAgentsCollection;
    // 在途猫头鹰数：既作编队序号（错开每只的轨迹），也用于最后一只落地时才触发按钮脉冲。
    private int _activeOwlFlights;
    private static readonly Random _flightRng = new();


    public MainConversationView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnContextInspectorKeyDown, RoutingStrategies.Tunnel);
        DataContextChanged += OnDataContextChanged;
    }

    private void OnContextInspectorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || _viewModel?.IsContextInspectorOpen != true) return;
        _viewModel.CloseContextInspectorCommand.Execute(null);
        e.Handled = true;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (_viewModel != null)
        {
            _viewModel.Messages.CollectionChanged -= OnMessagesCollectionChanged;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }
        if (_subAgentsCollection != null)
        {
            _subAgentsCollection.CollectionChanged -= OnActiveSubAgentsChanged;
            _subAgentsCollection = null;
        }

        if (DataContext is MainConversationViewModel viewModel)
        {
            _viewModel = viewModel;
            viewModel.Messages.CollectionChanged += OnMessagesCollectionChanged;
            viewModel.PropertyChanged += OnViewModelPropertyChanged;

            _subAgentsCollection = viewModel.Orchestrator?.ActiveAgents;
            if (_subAgentsCollection != null)
            {
                _subAgentsCollection.CollectionChanged += OnActiveSubAgentsChanged;
            }

            // 换会话就回到底部。换 DataContext 时 ItemsSource 是整体替换，不产生 Add 事件，
            // OnAttachedToVisualTree 也早就跑过了——两条既有的滚动触发路径一条都不会命中，
            // ScrollViewer 只会把旧 offset 夹到新内容的范围内。结果是切到更长的会话时
            // 停在中间某处，而这恰好被切换幕布盖着，看不出是怎么来的。
            ScrollToBottomIfHasMessages();
        }
        else
        {
            _viewModel = null;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainConversationViewModel.IsSending))
        {
            if (_viewModel?.IsSending == true) _isUserScrolling = false;
        }
    }

    private void OnMessagesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && !_isUserScrolling) ScrollToBottom();
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

    private void OnMessageInputPastingFromClipboard(object? sender, RoutedEventArgs e)
        => AsyncEventGuard.Run(() => OnMessageInputPastingFromClipboardAsync(sender, e), nameof(OnMessageInputPastingFromClipboard));

    private async Task OnMessageInputPastingFromClipboardAsync(object? sender, RoutedEventArgs e)
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

    private void OnFilesDragOver(object? sender, DragEventArgs e)
    {
        var hasFiles = e.DataTransfer.TryGetFiles()?.OfType<IStorageFile>().Any() == true;
        e.DragEffects = _viewModel?.CanAcceptAttachments == true && hasFiles
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnFilesDrop(object? sender, DragEventArgs e)
        => AsyncEventGuard.Run(() => OnFilesDropAsync(sender, e), nameof(OnFilesDrop));

    private async Task OnFilesDropAsync(object? sender, DragEventArgs e)
    {
        if (_viewModel?.CanAcceptAttachments != true)
        {
            e.DragEffects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var files = e.DataTransfer.TryGetFiles()?.OfType<IStorageFile>().ToList();
        if (files?.Count > 0)
        {
            e.DragEffects = DragDropEffects.Copy;
            e.Handled = true;
            await _viewModel.AddStorageFilesAsync(files);
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
                Source = OwlFrameLibrary.Get(OwlAction.Fly, 0),
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
                owl.Source = OwlFrameLibrary.Get(OwlAction.Fly, OwlAnimationClips.Get(OwlAction.Fly).FrameAt(sw.Elapsed.TotalMilliseconds));
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
