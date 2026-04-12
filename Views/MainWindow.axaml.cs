using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using System;
using System.Threading.Tasks;

namespace Athena.UI.Views;

public partial class MainWindow : Window
{
    private Image? _themeSplashImage;

    public MainWindow()
    {
        InitializeComponent();
        _themeSplashImage = this.FindControl<Image>("ThemeSplashImage");
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
    /// 显示主题过渡动画
    /// </summary>
    /// <param name="theme">"Dark" 或 "Light"</param>
    public async Task ShowThemeSplashAsync(string theme)
    {
        if (_themeSplashImage == null) return;

        try
        {
            // 根据主题选择对应的图片
            var assetPath = theme == "Light"
                ? "avares://Athena.UI/Assets/Light.PNG"
                : "avares://Athena.UI/Assets/Dark.PNG";

            var uri = new Uri(assetPath);
            using var stream = AssetLoader.Open(uri);
            var bitmap = new Bitmap(stream);

            _themeSplashImage.Source = bitmap;
            _themeSplashImage.ZIndex = 100;
            _themeSplashImage.IsHitTestVisible = false;
            _themeSplashImage.Opacity = 0;

            // 渐入动画 300ms
            await FadeInAsync(_themeSplashImage, 300);

            // 保持显示 800ms
            await Task.Delay(800);

            // 渐出动画 400ms
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
