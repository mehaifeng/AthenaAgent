using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
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
    private TabControl? _mainTabControl;
    private ChatTabView? _chatTabView;

    public MainWindow()
    {
        InitializeComponent();
        _themeSplashImage = this.FindControl<Image>("ThemeSplashImage");
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
    /// 显示主题过渡动画
    /// </summary>
    /// <param name="theme">"Dark" 或 "Light"</param>
    /// <param name="force">
    /// true = 强制播放（引导交接路径）：即使目标主题与当前一致也不跳过；
    /// 若 PrepareSplashCover 已预置满幕遮罩，则跳过渐入，直接停留后揭幕。
    /// </param>
    public async Task ShowThemeSplashAsync(string theme, bool force = false)
    {
        if (_themeSplashImage == null) return;

        // 如果主题与当前 Avalonia 主题相同，跳过动画（避免 ConfigTabView auto-save 重复触发）。
        // 引导交接（force）除外——该路径的意义正是同主题下的揭幕动画。
        var isTargetDark = theme?.ToLower() != "light";
        var currentVariant = Application.Current?.RequestedThemeVariant;
        var isCurrentDark = currentVariant == Avalonia.Styling.ThemeVariant.Dark;
        if (!force && isTargetDark == isCurrentDark) return;

        try
        {
            var coveredAlready = force && _themeSplashImage.Source != null && _themeSplashImage.Opacity >= 1;

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
    /// 合成器时钟驱动的透明度动画，交由渲染管线插值，帧率平滑不受 UI 线程抖动影响。
    /// </summary>
    private static async Task AnimateOpacityAsync(Visual target, double from, double to, int durationMs)
    {
        var animation = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(durationMs),
            Easing = new LinearEasing(),
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0d),
                    Setters = { new Setter(Visual.OpacityProperty, from) }
                },
                new KeyFrame
                {
                    Cue = new Cue(1d),
                    Setters = { new Setter(Visual.OpacityProperty, to) }
                }
            }
        };

        await animation.RunAsync(target);
        target.Opacity = to; // 动画结束固化终值
    }

    private Task FadeInAsync(Image image, int durationMs) => AnimateOpacityAsync(image, 0, 1, durationMs);

    private Task FadeOutAsync(Image image, int durationMs) => AnimateOpacityAsync(image, image.Opacity, 0, durationMs);
}
