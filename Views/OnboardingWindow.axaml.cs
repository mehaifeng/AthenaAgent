using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Athena.UI.ViewModels;
using System;
using System.Threading.Tasks;

namespace Athena.UI.Views;

public partial class OnboardingWindow : Window
{
    /// <summary>
    /// 引导完成时的交接回调（由 App 注入）：负责创建已被同图遮罩覆盖的主窗口并关闭本窗。
    /// 为空时退化为直接 Close（等价旧行为）。
    /// </summary>
    public Func<Task>? HandoffRequested { get; set; }

    private bool _handoffStarted;

    public OnboardingWindow()
    {
        InitializeComponent();
    }

    public OnboardingWindow(OnboardingViewModel viewModel) : this()
    {
        DataContext = viewModel;
        viewModel.RequestClose = OnRequestClose;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (DataContext is OnboardingViewModel vm)
        {
            vm.RequestClose = null;
        }
        base.OnClosing(e);
    }

    /// <summary>完成/跳过按钮的关窗请求：有交接回调则走版画揭幕流程（App 全程编排），否则直接关窗。</summary>
    private void OnRequestClose()
    {
        if (_handoffStarted) return;

        if (HandoffRequested is { } handoff)
        {
            _handoffStarted = true;
            _ = handoff();
        }
        else
        {
            Close();
        }
    }

    /// <summary>
    /// 交接出场动画：窗口保持中心不动，平缓拉伸到主窗口的目标尺寸（约 420ms，三次缓动），
    /// 拉伸期间当前主题的雅典娜版画同步渐入（先于拉伸完成达到满幕），
    /// 满幕后停留约 300ms 再返回。调用方随后在本窗 Position 处显示同图覆盖的主窗口并关闭本窗。
    /// 期间吞掉所有点击，避免用户在交接中再次触发按钮。
    /// </summary>
    public async Task PlayHandoffExitAsync(double targetWidth, double targetHeight)
    {
        try
        {
            var veil = this.FindControl<Image>("HandoffVeilImage");
            if (veil != null)
            {
                var theme = (DataContext as OnboardingViewModel)?.Config.Theme;
                var assetPath = theme == "Light"
                    ? "avares://Athena.UI/Assets/Light.webp"
                    : "avares://Athena.UI/Assets/Dark.webp";
                using var stream = AssetLoader.Open(new Uri(assetPath));
                veil.Source = new Bitmap(stream);
                veil.IsHitTestVisible = true;
            }

            var startWidth = Width;
            var startHeight = Height;
            var startPos = Position;
            var scale = DesktopScaling;

            const int durationMs = 420;
            const int stepMs = 14;
            const int steps = durationMs / stepMs;
            for (int i = 1; i <= steps; i++)
            {
                var t = i / (double)steps;
                // 三次缓入缓出
                var eased = t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;

                Width = startWidth + (targetWidth - startWidth) * eased;
                Height = startHeight + (targetHeight - startHeight) * eased;
                // 中心锚定：尺寸每长一分，位置反向挪半分（Position 为物理像素，需乘缩放）
                Position = new Avalonia.PixelPoint(
                    startPos.X - (int)Math.Round((Width - startWidth) / 2 * scale),
                    startPos.Y - (int)Math.Round((Height - startHeight) / 2 * scale));

                if (veil != null)
                {
                    // 版画在拉伸约 2/3 处即达满幕，收尾阶段只见画布生长
                    veil.Opacity = Math.Min(1.0, t * 1.6);
                }
                await Task.Delay(stepMs);
            }

            Width = targetWidth;
            Height = targetHeight;
            if (veil != null) veil.Opacity = 1;

            // 满幕定格，让观者看清版画后再交棒
            await Task.Delay(300);
        }
        catch (Exception)
        {
            // 出场动画失败不阻断交接，最差退化为无动画切换
        }
    }

    /// <summary>Provider 选择变化时带出默认 BaseUrl（避免用户手抄端点）。</summary>
    private void OnProviderSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is OnboardingViewModel vm && vm.ApplyProviderDefaultUrlCommand.CanExecute(null))
        {
            vm.ApplyProviderDefaultUrlCommand.Execute(null);
        }
    }

    /// <summary>Web Search 供应商选择变化：带出默认 BaseUrl 并刷新 Custom / Baidu 附加字段可见性。</summary>
    private void OnWebSearchProviderSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is OnboardingViewModel vm && vm.ApplyWebSearchProviderDefaultUrlCommand.CanExecute(null))
        {
            vm.ApplyWebSearchProviderDefaultUrlCommand.Execute(null);
        }
    }
}
