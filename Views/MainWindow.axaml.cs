using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace Athena.UI.Views;

public partial class MainWindow : Window
{
    private Image? _themeSplashImage;
    private Image? _baseBackgroundImage;
    private Image? _themeTransitionImage;
    private TabControl? _mainTabControl;
    private ChatTabView? _chatTabView;

    public MainWindow()
    {
        InitializeComponent();
        _themeSplashImage = this.FindControl<Image>("ThemeSplashImage");
        _baseBackgroundImage = this.FindControl<Image>("BaseBackgroundImage");
        _themeTransitionImage = this.FindControl<Image>("ThemeTransitionImage");
        _mainTabControl = this.FindControl<TabControl>("MainTabControl");
        _chatTabView = this.FindControl<ChatTabView>("ChatTabView");
        if (_mainTabControl != null)
        {
            _mainTabControl.SelectionChanged += OnMainTabSelectionChanged;
        }
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

    private void OnMainTabSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        ScrollChatToBottomIfVisible();
    }

    public void ScrollChatToBottomIfVisible()
    {
        if (_mainTabControl?.SelectedIndex == 0 && _chatTabView != null)
        {
            Dispatcher.UIThread.Post(_chatTabView.ScrollToBottomIfHasMessages, DispatcherPriority.Loaded);
            Dispatcher.UIThread.Post(_chatTabView.ScrollToBottomIfHasMessages, DispatcherPriority.Background);
        }
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

    protected override void OnClosed(EventArgs e)
    {
        if (_mainTabControl != null)
        {
            _mainTabControl.SelectionChanged -= OnMainTabSelectionChanged;
        }
        base.OnClosed(e);
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
        // 如果主题与当前 Avalonia 主题相同，跳过动画（避免 ConfigTabView auto-save 重复触发）。
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
