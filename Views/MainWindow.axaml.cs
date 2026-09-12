using Athena.UI.Services;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.VisualTree;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.ComponentModel;
using Athena.UI.ViewModels;

namespace Athena.UI.Views;

public partial class MainWindow : Window
{
    private const double WindowBaseMinWidth = 1180;
    private const double ShellHorizontalMargin = 10;
    private const double LeftPanelMinWidth = 260;
    private const double RightPanelMinWidth = 360;
    private const double ConversationMinWidth = 540;
    private const double ShellSplitterWidth = 5;
    // 右侧工作区不再包 shell 外框、无 Margin，左右只剩面板自身的 1px 边框
    private const double RightPanelHorizontalInset = 2;
    private const double RightTopMinHeight = 280;
    private const double RightLogMinHeight = 120;
    private const double RightRowSplitterHeight = 5;

    private Image? _themeSplashImage;
    private Image? _baseBackgroundImage;
    private Image? _themeTransitionImage;
    private Image? _glassBackdropImage;
    private TabControl? _utilityTabControl;
    private Grid? _mainShellGrid;
    private Grid? _rightPanelGrid;
    private ColumnDefinition? _leftShellColumn;
    private ColumnDefinition? _rightShellColumn;
    private RowDefinition? _rightTopRow;
    private RowDefinition? _rightLogRow;
    private WorkspaceWorkbenchView? _workspaceWorkbench;
    private PathIcon? _titleBarMaximizeIcon;
    private PathIcon? _titleBarRestoreIcon;
    private MainWindowViewModel? _viewModel;
    private readonly List<Border> _shellPanels = new();

    public MainWindow()
    {
        InitializeComponent();
        _themeSplashImage = this.FindControl<Image>("ThemeSplashImage");
        _baseBackgroundImage = this.FindControl<Image>("BaseBackgroundImage");
        _themeTransitionImage = this.FindControl<Image>("ThemeTransitionImage");
        _glassBackdropImage = this.FindControl<Image>("GlassBackdropImage");
        _titleBarMaximizeIcon = this.FindControl<PathIcon>("TitleBarMaximizeIcon");
        _titleBarRestoreIcon = this.FindControl<PathIcon>("TitleBarRestoreIcon");
        UpdateMaximizeRestoreIcons();
        _mainShellGrid = this.FindControl<Grid>("MainShellGrid");
        _leftShellColumn = _mainShellGrid?.ColumnDefinitions[0];
        _rightShellColumn = _mainShellGrid?.ColumnDefinitions[4];
        _rightPanelGrid = this.FindControl<Grid>("RightPanelGrid");
        _rightTopRow = _rightPanelGrid?.RowDefinitions[0];
        _rightLogRow = _rightPanelGrid?.RowDefinitions[2];
        _workspaceWorkbench = this.FindControl<WorkspaceWorkbenchView>("WorkspaceWorkbench");
        _utilityTabControl = this.FindControl<TabControl>("UtilityTabControl");
        if (_utilityTabControl != null)
            _utilityTabControl.SelectionChanged += OnUtilityTabSelectionChanged;
        if (_workspaceWorkbench != null)
            _workspaceWorkbench.MinimumRequiredWidthChanged += OnWorkbenchMinimumRequiredWidthChanged;
        // 收集三块 shell 面板（左/中/右），面板透明度只作用于其背景画笔。
        _shellPanels.AddRange(_mainShellGrid?.Children.OfType<Border>()
            .Where(b => b.Classes.Contains("shell-panel")) ?? Array.Empty<Border>());
        // 主题变体在运行时切换时，面板背景色要立刻跟随（App.ThemeChanged 在 1.2s 过渡后才广播，太晚）。
        if (Application.Current is INotifyPropertyChanged appNotify)
            appNotify.PropertyChanged += OnApplicationPropertyChanged;
        // 配色方案切换不改 RequestedThemeVariant，必须单独订阅重解析面板背景。
        App.ColorSchemeChanged += OnColorSchemeChanged;
        ApplyShellPanelMaterial();
        DataContextChanged += OnMainDataContextChanged;
        SizeChanged += (_, _) => ApplySavedLayout();
        // 窗口显示之前就设置好 Splash 图片的初始状态：
        // Opacity=1 让窗口打开时图片立即覆盖在 UI 上，
        // ShowThemeSplashAsync 根据 IsLoaded 决定是否需要从 0 渐入
        if (_themeSplashImage != null)
        {
            _themeSplashImage.ZIndex = 100;
            _themeSplashImage.IsHitTestVisible = false;
            _themeSplashImage.Opacity = 1;
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty) UpdateMaximizeRestoreIcons();
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        if (e.ClickCount == 2)
        {
            ToggleMaximizeRestore();
        }
        else
        {
            BeginMoveDrag(e);
        }

        e.Handled = true;
    }

    private void OnTitleBarMinimizeClick(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
        e.Handled = true;
    }

    private void OnTitleBarMaximizeRestoreClick(object? sender, RoutedEventArgs e)
    {
        ToggleMaximizeRestore();
        e.Handled = true;
    }

    private void OnTitleBarCloseClick(object? sender, RoutedEventArgs e)
    {
        Close();
        e.Handled = true;
    }

    private void ToggleMaximizeRestore()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void UpdateMaximizeRestoreIcons()
    {
        var isMaximized = WindowState == WindowState.Maximized;
        if (_titleBarMaximizeIcon != null) _titleBarMaximizeIcon.IsVisible = !isMaximized;
        if (_titleBarRestoreIcon != null) _titleBarRestoreIcon.IsVisible = isMaximized;
    }

    private void OnMainDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel != null) _viewModel.PropertyChanged -= OnMainViewModelPropertyChanged;
        _viewModel = DataContext as MainWindowViewModel;
        if (_viewModel != null)
        {
            _viewModel.PropertyChanged += OnMainViewModelPropertyChanged;
            ApplySavedLayout();
            ApplyShellPanelMaterial();
        }
    }

    private void OnMainViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.IsSidePanelsSwapped)) ApplySavedLayout();
        else if (e.PropertyName == nameof(MainWindowViewModel.ShellPanelOpacity)) ApplyShellPanelMaterial();
        else if (e.PropertyName == nameof(MainWindowViewModel.PanelGlassEnabled)) ApplyShellPanelMaterial();
    }

    private void OnApplicationPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 运行时切换主题：RequestedThemeVariant 一落地就要重解析背景色（此时主题字典已换新）。
        if (e.PropertyName == nameof(Application.RequestedThemeVariant))
            ApplyShellPanelMaterial();
    }

    private void OnColorSchemeChanged(string _) => ApplyShellPanelMaterial();

    /// <summary>日志 / 终端切页的淡入时长。比 shell 的悬停反馈长一点——这是换了一整块内容，不是状态反馈。</summary>
    private const int UtilityTabFadeMs = 220;

    /// <summary>切页时新内容从下方抬起的距离。纯透明度变化在 220ms 里几乎看不出来，位移才读得出"动"。</summary>
    private const double UtilityTabRiseY = 6d;

    /// <summary>
    /// 日志 / 终端切页淡入。Avalonia 的 TabControl 没有 transition 属性，内容由模板里
    /// 单个 ContentPresenter 承载；切页时旧内容确实会被摘下、新内容挂上（实测
    /// DetachedFromVisualTree / AttachedToVisualTree 各触发一次），但 **Style.Animations
    /// 不会因为重新挂载而重播**（同样实测：切回来那一刻 Opacity 就是 1，动画根本没起）。
    /// 所以这里只能由 SelectionChanged 驱动，而不是纯 XAML。
    ///
    /// 必须 Post 一拍再找元素：SelectionChanged 触发时 ContentPresenter 的 Content 已经换了，
    /// 但新内容的可视子树要等下一次布局才实例化，当场找只会找到旧的那个（或什么都找不到）。
    /// </summary>
    private void OnUtilityTabSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        // **同步启动，不要 Post。** 曾经这里 Post 了一拍，结果是肉眼可见的"闪两下"：
        // SelectionChanged 触发时新页已经在可视树里了（实测），延后一拍意味着它先按
        // 完全不透明画了一帧，然后才被按到 0 再爬回来——出现、闪掉、再淡入。
        // Animation.RunAsync 的 Cue 0 会立即接管属性、中间不留渲染帧，所以同步调用就没有那一帧。
        var page = _utilityTabControl?.GetVisualDescendants()
            .OfType<Control>()
            .FirstOrDefault(c => c.Classes.Contains("utility-tab-page"));
        // 找不到就什么都不做：宁可没有淡入，也不要先改属性再补动画（那又是那一帧的来源）。
        if (page != null) _ = FadeInUtilityTabAsync(page);
    }

    private static async Task FadeInUtilityTabAsync(Control page)
    {
        // 刻意不预设局部值：FillMode.None 下动画结束后属性回落到局部值，
        // 若预设成 0，页面就永远留在透明上（这是"局部值必须等于动画终值"那条规则的反面）。
        // 这里局部值保持未设（默认完全可见），动画只负责那 220ms 的斜坡。
        var animation = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(UtilityTabFadeMs),
            Easing = new CubicEaseOut(),
            FillMode = FillMode.None,
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0d),
                    Setters =
                    {
                        new Setter(Visual.OpacityProperty, 0d),
                        new Setter(TranslateTransform.YProperty, UtilityTabRiseY)
                    }
                },
                new KeyFrame
                {
                    Cue = new Cue(1d),
                    Setters =
                    {
                        new Setter(Visual.OpacityProperty, 1d),
                        new Setter(TranslateTransform.YProperty, 0d)
                    }
                }
            }
        };

        try
        {
            await animation.RunAsync(page);
        }
        catch (Exception ex)
        {
            // 淡入失败不影响内容本身：FillMode.None 下属性回落到局部值（完全可见），
            // 这里丢掉的只是那 220ms。
            Serilog.Log.Debug(ex, "Utility tab fade-in failed");
        }
    }

    /// <summary>
    /// 面板材质的唯一改写点。按 ShellPanelOpacity + PanelGlassEnabled 重建：
    /// 背景画笔（左中右 shell 面板 + 右侧工作区四块面板的 App.PanelBackgroundBrush
    /// + 主对话气泡 Chat.UserBubbleBg / Chat.AssistantBubbleBg）、
    /// 描边画笔（App.PanelBorderBrush）、投影（App.PanelShadow）、模糊底图层，
    /// 以及会话行选中态淡入的同色零透明度起点（App.SelectedBackgroundFaded）——
    /// 后者与材质无关，但它和上面几个一样是"随主题/配色重解析的窗口级画笔"，
    /// 触发条件完全相同，再拆一套解析管线只会多一处漏更新的地方。
    /// 只让背景变透明使雅典娜图像透出，文字/图标/文件等内容保持完全不透明
    /// （对整个 Border 设 Opacity 会让整个子树一起变淡）。
    ///
    /// 新增面板不要再自己挂类或加绑定：消费 App.PanelBackgroundBrush /
    /// App.PanelBorderBrush / App.PanelShadow 三个键就自动跟随材质。
    /// </summary>
    private void ApplyShellPanelMaterial()
    {
        var glass = _viewModel?.PanelGlassEnabled == true;
        var isLight = Application.Current?.RequestedThemeVariant == ThemeVariant.Light;
        var opacity = ShellMaterial.ResolveTintOpacity(_viewModel?.ShellPanelOpacity ?? 1.0, glass, isLight);
        var color = ResolveShellPanelBackgroundColor();
        var brush = new SolidColorBrush(color, opacity);
        foreach (var panel in _shellPanels)
        {
            if (panel != null) panel.Background = brush;
        }

        // 右侧工作区区域通过窗口级动态资源跟随同一透明度；
        // 气泡背景色定义在各主题字典里，需在 Application 作用域解析原始颜色后重建带透明度的画笔
        // （窗口级覆盖会遮蔽主题字典，因此不能从 this 作用域解析）。
        Resources["App.PanelBackgroundBrush"] = brush;
        Resources["Chat.UserBubbleBg"] = RebuildBrushWithOpacity("Chat.UserBubbleBg", opacity);
        Resources["Chat.AssistantBubbleBg"] = RebuildBrushWithOpacity("Chat.AssistantBubbleBg", opacity);
        // 会话行选中态淡入的起点色：与 App.SelectedBackground 同色、透明度 0。
        // 不能让 BrushTransition 从 Transparent 起步——直通 alpha 插值会中途经过
        // "半透明白"，深色主题下就是闪一下白光（AvaloniaUI/Avalonia#15419）。
        Resources["App.SelectedBackgroundFaded"] = RebuildBrushWithOpacity("App.SelectedBackground", 0.0);

        // 悬停/按下覆盖层不在这里构造：它们只依赖明暗变体，不依赖配色方案，也不依赖面板材质，
        // 所以是静态资源，定义在 Styles/Overlays.axaml（应用级合并字典）里。
        // 合并字典不受 App.SetColorScheme 整本替换 ThemeDictionaries 的影响——
        // 写进 App.axaml 的主题字典反而会在切配色方案时整个丢掉。

        // 玻璃质感的三件套里，模糊只占一件——真正让人认出"玻璃"的是描边高光和投影。
        // 关闭时这两个键回落到原来的 SemiGreyLowBrush / 无投影，观感与改动前完全一致。
        Resources["App.PanelBorderBrush"] = ResolvePanelBorderBrush(glass);
        Resources["App.PanelShadow"] = glass ? GlassPanelShadow : default(BoxShadows);

        ApplyGlassBackdrop(glass);
    }

    /// <summary>玻璃模式的面板投影。半径给得比位移大，读起来是"浮起来"而不是"下面垫了一条黑边"。</summary>
    private static readonly BoxShadows GlassPanelShadow = BoxShadows.Parse("0 6 22 0 #26000000");

    /// <summary>
    /// 面板描边：非玻璃沿用主题的 SemiGreyLowBrush；玻璃换成白色高光。
    /// 浅色主题下要给到 0.62 才看得见边，深色主题 0.16 就够——再高会变成一道亮框。
    /// </summary>
    private IBrush ResolvePanelBorderBrush(bool glass)
    {
        var variant = Application.Current?.RequestedThemeVariant;
        if (glass)
        {
            var alpha = variant == ThemeVariant.Light
                ? ShellMaterial.GlassBorderAlphaLight
                : ShellMaterial.GlassBorderAlphaDark;
            return new SolidColorBrush(Colors.White, alpha);
        }

        if (variant != null && this.TryFindResource("SemiGreyLowBrush", variant, out var value) && value is IBrush themed)
            return themed;
        return new SolidColorBrush(Color.Parse("#3A3A3A"));
    }

    /// <summary>
    /// 驱动模糊底图层：玻璃开启时把它抬到 GlassBackdropOpacity，同时把清晰底图压到
    /// GlassBaseImageOpacity（两层都留满会叠成双影）；关闭时反向。
    ///
    /// 这里只赋终值，260ms 的淡入淡出由两个 Image 在 XAML 里声明的 DoubleTransition 完成。
    /// 刻意不用 Animation.RunAsync 手写：那需要"先把局部值预置为动画终值"来躲收尾闪帧
    /// （主题过渡里那条注释），还要处理快速来回切换时前一次收尾抹掉后一次结果的竞态，
    /// 而声明式过渡两者都不存在——状态是同步落定的，动画只是它的视觉斜坡。
    /// </summary>
    private void ApplyGlassBackdrop(bool glass)
    {
        if (_glassBackdropImage == null || _baseBackgroundImage == null) return;

        var backdrop = glass ? LoadGlassBackdropBitmap() : null;
        if (glass && backdrop == null)
        {
            // 位图解码失败：玻璃退化为"tint + 描边 + 投影"，没有模糊底图。
            // 这是可见但可接受的降级——一个装饰性材质不该让窗口起不来，
            // 失败原因已由 LoadGlassBackdropBitmap 记进日志。
            _glassBackdropImage.Opacity = 0;
            _baseBackgroundImage.Opacity = ShellMaterial.BaseImageOpacity;
            return;
        }

        if (backdrop != null) _glassBackdropImage.Source = backdrop;
        _glassBackdropImage.Opacity = glass ? ShellMaterial.GlassBackdropOpacity : 0d;
        _baseBackgroundImage.Opacity = glass ? ShellMaterial.GlassBaseImageOpacity : ShellMaterial.BaseImageOpacity;
        // 关闭时故意不置 Source = null：位图本身是进程级缓存的（_glassBackdropCache），
        // 置空省不下内存，只会把"淡出还没结束就把图抹掉"变成一个要防的竞态。
    }

    /// <summary>已解码的模糊底图缓存（按主题变体）。96px 的位图极小，常驻比每次切换重解码划算。</summary>
    private static readonly ConcurrentDictionary<string, Bitmap> _glassBackdropCache = new();

    private static Bitmap? LoadGlassBackdropBitmap()
    {
        var key = Application.Current?.RequestedThemeVariant == ThemeVariant.Light ? "Light" : "Dark";
        try
        {
            return _glassBackdropCache.GetOrAdd(key, static k =>
            {
                using var stream = AssetLoader.Open(new Uri($"avares://Athena.UI/Assets/{k}.webp"));
                return Bitmap.DecodeToWidth(stream, ShellMaterial.GlassBackdropDecodeWidth);
            });
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Glass backdrop bitmap decode failed; panels fall back to tint-only glass");
            return null;
        }
    }

    private SolidColorBrush RebuildBrushWithOpacity(string resourceKey, double opacity)
    {
        var variant = Application.Current?.RequestedThemeVariant;
        if (variant != null
            && Application.Current?.TryFindResource(resourceKey, variant, out var value) == true
            && value is ISolidColorBrush solid)
        {
            return new SolidColorBrush(solid.Color, opacity);
        }
        return new SolidColorBrush(Color.Parse("#2E2E30"), opacity);
    }

    /// <summary>
    /// 解析当前主题下的面板背景色（SemiColorBackground0 是随主题切换的 SolidColorBrush）。
    /// 必须显式传入当前主题变体：两参 TryFindResource 会落到 ThemeVariant.Default，
    /// 而 Semi 把 "Default" 键映射到 Light（白色），深色模式下会解析出错误的白背景。
    /// </summary>
    private Color ResolveShellPanelBackgroundColor()
    {
        var variant = Application.Current?.RequestedThemeVariant;
        if (variant != null
            && this.TryFindResource("SemiColorBackground0", variant, out var value)
            && value is ISolidColorBrush solid)
        {
            return solid.Color;
        }
        var isDark = variant == ThemeVariant.Dark;
        return isDark ? Color.Parse("#16161A") : Color.Parse("#FFFFFF");
    }

    private void OnWorkbenchMinimumRequiredWidthChanged(object? sender, EventArgs e) =>
        ApplySavedLayout();

    private void OnOverflowMenuClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control control && control.ContextFlyout is FlyoutBase flyout)
        {
            flyout.ShowAt(control);
            e.Handled = true;
        }
    }

    private void ApplySavedLayout()
    {
        var layout = _viewModel?.Config?.MainLayout;
        if (layout == null || _leftShellColumn == null || _rightShellColumn == null) return;
        var semanticRightMinWidth = GetSemanticRightMinWidth();
        MinWidth = Math.Max(
            WindowBaseMinWidth,
            ShellHorizontalMargin
            + LeftPanelMinWidth
            + ConversationMinWidth
            + semanticRightMinWidth
            + (2 * ShellSplitterWidth));
        if (WindowState == WindowState.Normal
            && Bounds.Width > 0
            && Bounds.Width + 0.01 < MinWidth)
        {
            Width = MinWidth;
        }
        var leftSemanticWidth = Math.Max(LeftPanelMinWidth, layout.LeftWidth);
        var rightSemanticWidth = Math.Max(semanticRightMinWidth, layout.RightWidth);
        var physicalLeftMinWidth = layout.SidePanelsSwapped ? semanticRightMinWidth : LeftPanelMinWidth;
        var physicalRightMinWidth = layout.SidePanelsSwapped ? LeftPanelMinWidth : semanticRightMinWidth;
        var physicalLeftWidth = layout.SidePanelsSwapped ? rightSemanticWidth : leftSemanticWidth;
        var physicalRightWidth = layout.SidePanelsSwapped ? leftSemanticWidth : rightSemanticWidth;

        ConstrainSideWidths(
            ref physicalLeftWidth,
            ref physicalRightWidth,
            physicalLeftMinWidth,
            physicalRightMinWidth);
        _leftShellColumn.MinWidth = physicalLeftMinWidth;
        _rightShellColumn.MinWidth = physicalRightMinWidth;
        _leftShellColumn.Width = new GridLength(physicalLeftWidth);
        _rightShellColumn.Width = new GridLength(physicalRightWidth);
        ApplySavedLogHeight(layout.RightLogHeight);
    }

    private double GetSemanticRightMinWidth() =>
        Math.Max(
            RightPanelMinWidth,
            (_workspaceWorkbench?.MinimumRequiredWidth ?? 0) + RightPanelHorizontalInset);

    private void ApplySavedLogHeight(double preferredHeight)
    {
        if (_rightTopRow == null || _rightLogRow == null) return;
        var availableHeight = _rightPanelGrid?.Bounds.Height ?? 0;
        var maxLogHeight = availableHeight > 0
            ? Math.Max(
                RightLogMinHeight,
                availableHeight - RightTopMinHeight - RightRowSplitterHeight)
            : Math.Max(RightLogMinHeight, preferredHeight);
        var logHeight = Math.Clamp(
            preferredHeight > 0 ? preferredHeight : 190,
            RightLogMinHeight,
            maxLogHeight);

        _rightTopRow.Height = new GridLength(1, GridUnitType.Star);
        _rightLogRow.Height = new GridLength(logHeight);
    }

    private void ConstrainSideWidths(
        ref double leftWidth,
        ref double rightWidth,
        double leftMinWidth,
        double rightMinWidth)
    {
        var shellWidth = _mainShellGrid?.Bounds.Width ?? 0;
        if (shellWidth <= 0) shellWidth = Math.Max(0, ClientSize.Width - 10);
        if (shellWidth <= 0) return;

        var sideBudget = Math.Max(
            leftMinWidth + rightMinWidth,
            shellWidth - ConversationMinWidth - (2 * ShellSplitterWidth));
        var requestedWidth = leftWidth + rightWidth;
        if (requestedWidth <= sideBudget) return;

        var availableExtra = Math.Max(0, sideBudget - leftMinWidth - rightMinWidth);
        var leftExtra = Math.Max(0, leftWidth - leftMinWidth);
        var rightExtra = Math.Max(0, rightWidth - rightMinWidth);
        var requestedExtra = leftExtra + rightExtra;
        if (requestedExtra <= 0)
        {
            leftWidth = leftMinWidth;
            rightWidth = rightMinWidth;
            return;
        }

        var scale = availableExtra / requestedExtra;
        leftWidth = leftMinWidth + (leftExtra * scale);
        rightWidth = rightMinWidth + (rightExtra * scale);
    }

    private void OnSideSplitterDragCompleted(object? sender, Avalonia.Input.VectorEventArgs e)
        => AsyncEventGuard.Run(() => OnSideSplitterDragCompletedAsync(sender, e), nameof(OnSideSplitterDragCompleted));

    private async Task OnSideSplitterDragCompletedAsync(object? sender, Avalonia.Input.VectorEventArgs e)
    {
        var layout = _viewModel?.Config?.MainLayout;
        if (layout == null || _viewModel == null || _leftShellColumn == null || _rightShellColumn == null) return;
        if (layout.SidePanelsSwapped)
        {
            layout.RightWidth = _leftShellColumn.ActualWidth;
            layout.LeftWidth = _rightShellColumn.ActualWidth;
        }
        else
        {
            layout.LeftWidth = _leftShellColumn.ActualWidth;
            layout.RightWidth = _rightShellColumn.ActualWidth;
        }
        await _viewModel.SaveConfigurationNowAsync();
    }

    private void OnRightRowSplitterDragCompleted(object? sender, Avalonia.Input.VectorEventArgs e)
        => AsyncEventGuard.Run(() => OnRightRowSplitterDragCompletedAsync(sender, e), nameof(OnRightRowSplitterDragCompleted));

    private async Task OnRightRowSplitterDragCompletedAsync(object? sender, Avalonia.Input.VectorEventArgs e)
    {
        var layout = _viewModel?.Config?.MainLayout;
        if (layout == null || _viewModel == null || _rightLogRow == null) return;
        layout.RightLogHeight = Math.Max(RightLogMinHeight, _rightLogRow.ActualHeight);
        await _viewModel.SaveConfigurationNowAsync();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (Application.Current is App app && !app.IsQuitting)
        {
            e.Cancel = true;
            this.Hide();
        }
        base.OnClosing(e);
    }

    /// <summary>
    /// 引导交接：在窗口 Show() 之前把版画遮罩预置为完全覆盖状态，
    /// 使主窗口首帧就被与引导窗同一张雅典娜版画盖住，跨窗切换无感知。
    /// </summary>
    /// <param name="theme">"Dark" 或 "Light"</param>
    public void PrepareSplashCover(string theme)
    {
        if (_themeSplashImage == null) return;
        try
        {
            _themeSplashImage.Source = LoadSplashBitmap(theme);
            _themeSplashImage.ZIndex = 100;
            _themeSplashImage.IsHitTestVisible = true;
            _themeSplashImage.Opacity = 1;
        }
        catch (Exception)
        {
            // 加载失败则放弃覆盖，退化为普通打开
            _themeSplashImage.Opacity = 0;
        }
    }

    /// <summary>
    /// 显示主题过渡动画：
    /// 引导交接（force）播放满幕版画揭幕；运行期主题切换播放"景深聚焦"背景过渡。
    /// </summary>
    /// <param name="theme">"Dark" 或 "Light"</param>
    /// <param name="force">
    /// true = 强制播放（引导交接路径）：即使目标主题与当前一致也不跳过；
    /// 若 PrepareSplashCover 已预置满幕遮罩，则跳过渐入，直接停留后揭幕。
    /// </param>
    public async Task ShowThemeSplashAsync(string theme, bool force = false)
    {
        // 如果主题与当前 Avalonia 主题相同，跳过动画（避免配置自动保存重复触发）。
        // 引导交接（force）除外——该路径的意义正是同主题下的揭幕动画。
        var isTargetDark = theme?.ToLower() != "light";
        var currentVariant = Application.Current?.RequestedThemeVariant;
        var isCurrentDark = currentVariant == Avalonia.Styling.ThemeVariant.Dark;
        if (!force && isTargetDark == isCurrentDark) return;

        if (force)
        {
            await RunSplashHandoffAsync(theme, isTargetDark, isCurrentDark);
        }
        else
        {
            await RunFocusTransitionAsync(isTargetDark);
        }
    }

    /// <summary>
    /// 引导交接的满幕版画揭幕（仅 force 路径）：遮罩已定格则停留后揭幕，否则渐入→切主题→揭幕。
    /// </summary>
    private async Task RunSplashHandoffAsync(string? theme, bool isTargetDark, bool isCurrentDark)
    {
        if (_themeSplashImage == null) return;

        try
        {
            var coveredAlready = _themeSplashImage.Source != null && _themeSplashImage.Opacity >= 1;

            if (!coveredAlready)
            {
                _themeSplashImage.Source = LoadSplashBitmap(theme);
                _themeSplashImage.ZIndex = 100;
                _themeSplashImage.IsHitTestVisible = false;
                _themeSplashImage.Opacity = 0;
                //初始化时，不使用渐入动画
                if (IsLoaded)
                {
                    // 渐入动画 300ms（此时界面还是旧主题，覆盖层渐入）
                    await FadeInAsync(_themeSplashImage, 300);
                }
            }

            // 渐入完成后切换主题（覆盖层已完全遮住界面下方，切换无感知）。
            // 注意：此处直接落地 RequestedThemeVariant，绝不能回调 App.SetTheme——
            // App.SetTheme(desktop 分支) 正是通过本方法应用主题的，若再回调会形成
            // “SetTheme ↔ ShowThemeSplashAsync” 的 async void 无限递归（主题反复切换、画面闪烁）。
            // ThemeChanged 广播由 App.SetTheme 在 await 本方法之后统一发出，此处无需重复广播。
            if (isTargetDark != isCurrentDark && Application.Current != null)
            {
                Application.Current.RequestedThemeVariant = isTargetDark
                    ? Avalonia.Styling.ThemeVariant.Dark
                    : Avalonia.Styling.ThemeVariant.Light;
            }

            // 保持显示（覆盖层与下方新主题界面一致；交接路径在引导窗侧已满幕定格过，
            // 这里只留一小段让换窗尘埃落定即揭幕）
            await Task.Delay(coveredAlready ? 250 : 800);

            // 渐出动画 400ms（揭示已切换完毕的新主题界面）
            _themeSplashImage.IsHitTestVisible = false;
            await FadeOutAsync(_themeSplashImage, 400);
        }
        catch (Exception)
        {
            // 静默处理，避免动画失败影响主流程
            if (_themeSplashImage != null)
            {
                _themeSplashImage.Opacity = 0;
            }
        }
    }

    // 景深聚焦过渡时长
    private const int FocusTransitionMs = 1200;

    // 启动入场的景深聚焦时长（比主题切换短，避免拖沓）
    private const int StartupFocusEntranceMs = 700;

    // 景深聚焦：旧背景失焦的模糊终值 / 新背景落定的缩放起点
    private const double FocusBlurRadius = 26d;
    private const double FocusSettleScale = 1.06;

    // 过渡进行中的重入保护：连点切换时后到者直接落地变体，不叠加第二场动画
    private bool _focusTransitionRunning;

    /// <summary>
    /// 运行期主题切换的"景深聚焦"过渡：旧背景在原位模糊失焦并淡出，
    /// 新背景带轻微缩放落定，像相机重新对焦到另一幅版画。
    /// 过渡只发生在底层背景图上（蒙版之下）；蒙版与 UI 控件颜色随主题变体即时切换。
    /// 与版画揭幕同理，此处直接落地 RequestedThemeVariant，绝不能回调 App.SetTheme。
    /// </summary>
    private async Task RunFocusTransitionAsync(bool isTargetDark)
    {
        if (Application.Current == null) return;
        var targetVariant = isTargetDark ? ThemeVariant.Dark : ThemeVariant.Light;

        // 控件缺失、窗口尚未加载（启动路径）或已有过渡在播时直接落地主题，不做动画
        // （在播场景下，正在进行的动画会自然揭示换新后的底图，收尾状态仍一致）
        if (_themeTransitionImage == null || _baseBackgroundImage == null || !IsLoaded || _focusTransitionRunning)
        {
            Application.Current.RequestedThemeVariant = targetVariant;
            return;
        }

        _focusTransitionRunning = true;
        try
        {
            // 过渡层先顶上旧主题背景（与底图像素一致，接管瞬间无感知），
            // 再落地主题变体：底图 DynamicResource 换成新背景，此刻被过渡层盖住
            _themeTransitionImage.Effect = null;
            _themeTransitionImage.Source = LoadSplashBitmap(isTargetDark ? "Light" : "Dark");
            _themeTransitionImage.Opacity = 1;
            Application.Current.RequestedThemeVariant = targetVariant;

            // 旧背景模糊失焦并淡出（透明度前 35% 定格，让"失焦"先于消失被看见），
            // 同时新背景从 1.06 缩放回 1.0 落定。
            // 陷阱：Animation.RunAsync 完成、订阅解除的瞬间，属性会回落到局部值，
            // 之后才轮到代码补设终值——若局部值 ≠ 动画终值，收尾就会闪出一帧突变
            // （缩放弹回 1.06 / 旧图闪现 / 模糊骤清）。因此局部值一律预置为动画终值，
            // 且置值与挂动画在同一同步块内完成（Cue 0 立即接管，中间无渲染帧，起始不闪）。
            var blur = new BlurEffect { Radius = FocusBlurRadius };
            var settle = new ScaleTransform(1.0, 1.0);
            _themeTransitionImage.Opacity = 0;
            _themeTransitionImage.Effect = blur;
            _baseBackgroundImage.RenderTransform = settle;

            var fadeOut = new Animation
            {
                Duration = TimeSpan.FromMilliseconds(FocusTransitionMs),
                Easing = new LinearEasing(),
                FillMode = FillMode.Forward,
                Children =
                {
                    new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(Visual.OpacityProperty, 1d) } },
                    new KeyFrame { Cue = new Cue(0.35), Setters = { new Setter(Visual.OpacityProperty, 1d) } },
                    new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(Visual.OpacityProperty, 0d) } }
                }
            };

            await Task.WhenAll(
                fadeOut.RunAsync(_themeTransitionImage),
                AnimateAsync(blur, BlurEffect.RadiusProperty, 0d, FocusBlurRadius, FocusTransitionMs, new CubicEaseOut()),
                AnimateAsync(settle, ScaleTransform.ScaleXProperty, FocusSettleScale, 1.0, FocusTransitionMs, new CubicEaseOut()),
                AnimateAsync(settle, ScaleTransform.ScaleYProperty, FocusSettleScale, 1.0, FocusTransitionMs, new CubicEaseOut()));
        }
        catch (Exception)
        {
            // 静默处理：主题已在动画前落地，动画失败不影响主流程
        }
        finally
        {
            // 清理过渡状态，释放模糊效果、位图引用与缩放变换
            _themeTransitionImage.Opacity = 0;
            _themeTransitionImage.Effect = null;
            _themeTransitionImage.Source = null;
            _baseBackgroundImage.RenderTransform = null;
            _focusTransitionRunning = false;
        }
    }

    /// <summary>
    /// 普通启动的极简"景深聚焦"入场：背景从失焦+微缩放落定为清晰，
    /// 与运行期主题切换的聚焦过渡同一视觉语言，但不切换主题、不用过渡层，
    /// 用于盖住首帧后会话树异步填充的瞬间。
    /// </summary>
    public async Task PlayStartupFocusEntranceAsync()
    {
        if (_baseBackgroundImage == null || !IsLoaded || _focusTransitionRunning) return;

        _focusTransitionRunning = true;
        try
        {
            // 与 RunFocusTransitionAsync 同一陷阱：局部值必须预置为动画终值，
            // 且置值与挂动画在同一同步块内完成，避免收尾闪出一帧突变。
            var blur = new BlurEffect { Radius = FocusBlurRadius };
            var settle = new ScaleTransform(1.0, 1.0);
            _baseBackgroundImage.Effect = blur;
            _baseBackgroundImage.RenderTransform = settle;

            await Task.WhenAll(
                AnimateAsync(blur, BlurEffect.RadiusProperty, FocusBlurRadius, 0d, StartupFocusEntranceMs, new CubicEaseOut()),
                AnimateAsync(settle, ScaleTransform.ScaleXProperty, FocusSettleScale, 1.0, StartupFocusEntranceMs, new CubicEaseOut()),
                AnimateAsync(settle, ScaleTransform.ScaleYProperty, FocusSettleScale, 1.0, StartupFocusEntranceMs, new CubicEaseOut()));
        }
        catch (Exception)
        {
            // 静默处理：入场动画失败不影响主流程
        }
        finally
        {
            _baseBackgroundImage.Effect = null;
            _baseBackgroundImage.RenderTransform = null;
            _focusTransitionRunning = false;
        }
    }

    // 版画位图进程内缓存：每主题只解码一次
    private static readonly ConcurrentDictionary<string, Bitmap> _splashBitmapCache = new();

    // 覆盖层按窗口尺寸展示，1600 宽在 2K 屏上已足够清晰；降分辨率同时压低解码耗时与常驻内存
    private const int SplashDecodeWidth = 1600;

    private static Bitmap LoadSplashBitmap(string? theme)
    {
        var key = theme == "Light" ? "Light" : "Dark";
        return _splashBitmapCache.GetOrAdd(key, static k =>
        {
            var assetPath = $"avares://Athena.UI/Assets/{k}.webp";
            using var stream = AssetLoader.Open(new Uri(assetPath));
            return Bitmap.DecodeToWidth(stream, SplashDecodeWidth);
        });
    }

    /// <summary>
    /// 合成器时钟驱动的单属性动画（Animatable 通用：透明度、模糊半径、缩放等），
    /// 交由渲染管线插值，帧率平滑不受 UI 线程抖动影响。
    /// </summary>
    private static async Task AnimateAsync(Animatable target, AvaloniaProperty property, object from, object to, int durationMs, Easing? easing = null)
    {
        var animation = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(durationMs),
            Easing = easing ?? new LinearEasing(),
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0d),
                    Setters = { new Setter(property, from) }
                },
                new KeyFrame
                {
                    Cue = new Cue(1d),
                    Setters = { new Setter(property, to) }
                }
            }
        };

        await animation.RunAsync(target);
        target.SetValue(property, to); // 动画结束固化终值
    }

    private static Task AnimateOpacityAsync(Visual target, double from, double to, int durationMs)
        => AnimateAsync(target, Visual.OpacityProperty, from, to, durationMs);

    private Task FadeInAsync(Image image, int durationMs) => AnimateOpacityAsync(image, 0, 1, durationMs);

    private Task FadeOutAsync(Image image, int durationMs) => AnimateOpacityAsync(image, image.Opacity, 0, durationMs);
}
