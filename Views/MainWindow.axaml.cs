using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using System;
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
    /// 显示主题过渡动画
    /// </summary>
    /// <param name="theme">"Dark" 或 "Light"</param>
    public async Task ShowThemeSplashAsync(string theme)
    {
        if (_themeSplashImage == null) return;

        // 如果主题与当前 Avalonia 主题相同，跳过动画（避免 ConfigTabView auto-save 重复触发）
        var isTargetDark = theme?.ToLower() != "light";
        var currentVariant = Application.Current?.RequestedThemeVariant;
        var isCurrentDark = currentVariant == Avalonia.Styling.ThemeVariant.Dark;
        if (isTargetDark == isCurrentDark) return;

        try
        {
            // 根据主题选择对应的图片
            var assetPath = theme == "Light"
                ? "avares://Athena.UI/Assets/Light.webp"
                : "avares://Athena.UI/Assets/Dark.webp";

            var uri = new Uri(assetPath);
            using var stream = AssetLoader.Open(uri);
            var bitmap = new Bitmap(stream);

            _themeSplashImage.Source = bitmap;
            _themeSplashImage.ZIndex = 100;
            _themeSplashImage.IsHitTestVisible = false;
            _themeSplashImage.Opacity = 0;
            //初始化时，不使用渐入动画
            if (IsLoaded)
            {
                // 渐入动画 300ms（此时界面还是旧主题，覆盖层渐入）
                await FadeInAsync(_themeSplashImage, 300);
            }
            // 渐入完成后切换主题（覆盖层已完全遮住界面下方，切换无感知）
            if (Application.Current != null)
            {
                var isDark = theme?.ToLower() != "light";
                Application.Current.RequestedThemeVariant = isDark ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light;
            }

            // 保持显示 800ms（覆盖层已切换为新主题图片，与下方新主题界面一致）
            await Task.Delay(800);

            // 渐出动画 400ms（揭示已切换完毕的新主题界面）
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

    private async Task FadeInAsync(Image image, int durationMs)
    {
        var steps = 20;
        var stepDuration = durationMs / steps;
        var targetOpacity = 1.0;

        for (int i = 1; i <= steps; i++)
        {
            image.Opacity = targetOpacity * (i / (double)steps);
            await Task.Delay(stepDuration);
        }
        image.Opacity = targetOpacity;
    }

    private async Task FadeOutAsync(Image image, int durationMs)
    {
        var steps = 20;
        var stepDuration = durationMs / steps;
        var startOpacity = image.Opacity;

        for (int i = 1; i <= steps; i++)
        {
            image.Opacity = startOpacity * (1 - i / (double)steps);
            await Task.Delay(stepDuration);
        }
        image.Opacity = 0;
    }
}
