using System.Diagnostics;
using Athena.UI.Models;
using Athena.UI.Services;
using Athena.UI.ViewModels;
using Athena.UI.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;

internal static class OnboardingEntranceTests
{
    public static void Run(string outputPath)
    {
        var directory = Path.GetDirectoryName(outputPath)!;
        Directory.CreateDirectory(directory);
        var initialTheme = Application.Current!.RequestedThemeVariant;
        foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
        {
            VerifyCardExit(directory, theme);
            foreach (var (swapped, collapsed) in new[] { (false, false), (true, false), (false, true), (true, true) })
                VerifyEntrance(directory, theme, swapped, collapsed);
        }
        VerifyInterruptedEntrance(beforeFirstFrame: true);
        VerifyInterruptedEntrance(beforeFirstFrame: false);
        VerifyClosedOnboarding();
        Check(Application.Current.RequestedThemeVariant == initialTheme, "初始化交接不得修改全局主题。");
        Console.WriteLine("[PASS] onboarding: fixed window bounds, first frame, staggered panels, swapped/collapsed layouts, themes and tray recovery");
    }

    private static void VerifyCardExit(string directory, ThemeVariant theme)
    {
        using var viewModel = new OnboardingViewModel();
        var onboarding = new OnboardingWindow(viewModel) { RequestedThemeVariant = theme };
        var main = new MainWindow();
        Check(onboarding.Width == main.Width && onboarding.Height == main.Height,
            "初始化与主窗体必须从一开始使用相同的尺寸。");
        main.Close();
        onboarding.Show();
        Tick();
        var bounds = onboarding.ClientSize;
        var position = onboarding.Position;
        var card = onboarding.FindControl<Border>("OnboardingCard")!;
        Capture(onboarding, Path.Combine(directory, $"onboarding-{theme.Key}.png"));
        Check(card.Bounds.Width <= 720 && card.Bounds.Height <= 600, "设置表单应居中保留紧凑布局。");
        var exit = onboarding.PlayHandoffExitAsync();
        PumpUntil(() =>
        {
            Check(onboarding.ClientSize == bounds && onboarding.Position == position,
                "设置退场不能缩放或移动窗口。");
            return exit.IsCompleted;
        });
        exit.GetAwaiter().GetResult();
        Check(card.Opacity == 0 && !card.IsHitTestVisible, "设置卡片退场后不能继续显示或响应点击。");
        Check(onboarding.ActualThemeVariant == theme, "退场不能重设主题。");
        onboarding.Close();
    }

    private static void VerifyEntrance(string directory, ThemeVariant theme, bool swapped, bool collapsed)
    {
        var config = new AppConfig();
        config.MainLayout.SidePanelsSwapped = swapped;
        config.MainLayout.RightPanelCollapsed = collapsed;
        using var session = new AppConfigurationSession(new HeadlessConfigService(config));
        using var vm = new MainWindowViewModel(null, null, null, null, null, null, null, null,
            null, null, null, null, null, null, configurationSession: session);
        var window = new MainWindow { RequestedThemeVariant = theme, DataContext = vm, Width = 1440, Height = 900 };
        var shell = window.FindControl<Grid>("MainShellGrid")!;
        var center = window.FindControl<Border>("ConversationPanel")!;
        var pet = window.FindControl<VirtualPetView>("VirtualPet")!;
        var petTransform = pet.RenderTransform;
        pet.IsSuppressed = true;
        window.PrepareOnboardingEntrance();
        Check(shell.Children.Where(panel => Grid.GetColumnSpan(panel) == 1).All(panel => panel.Opacity == 0) && !shell.IsHitTestVisible,
            "主窗体 Show 前三栏必须不可见，避免第一帧闪亮或点击穿透。");
        window.Show();
        var size = window.ClientSize;
        var position = window.Position;
        var left = window.FindControl<Control>(swapped ? "RightPanelGrid" : "LeftPanel")!;
        var right = window.FindControl<Control>(swapped ? "LeftPanel" : "RightPanelGrid")!;
        var centerLed = false;
        var leftMovedInward = !left.IsVisible;
        var rightMovedInward = !right.IsVisible;
        PumpUntil(() =>
        {
            Check(window.ClientSize == size && window.Position == position, "三栏入场期间窗口边界必须稳定。");
            Check(window.FindControl<Grid>("RightPanelGrid")!.IsVisible == !collapsed,
                "入场不能展开已收起的工作台。");
            if (center.Opacity > 0.01 && center.Opacity < 1)
            {
                centerLed |= (!left.IsVisible || center.Opacity > left.Opacity)
                    && (!right.IsVisible || center.Opacity > right.Opacity);
            }
            leftMovedInward |= left.RenderTransform?.Value.M31 < 0;
            rightMovedInward |= right.RenderTransform?.Value.M31 > 0;
            return shell.IsHitTestVisible;
        });
        Check(centerLed, "中间对话区必须先于可见的两侧栏入场。");
        Check(leftMovedInward && rightMovedInward, "交换布局后，左右侧栏仍须从物理外侧向内进入。");
        Check(shell.Children.Where(panel => Grid.GetColumnSpan(panel) == 1).All(panel => panel.Opacity == 1 && panel.RenderTransform == null),
            "入场结束必须清理透明度和位移，不能留下模糊、隐藏或偏移的内容。");
        Check(ReferenceEquals(pet.RenderTransform, petTransform) && pet.Opacity == 0,
            "入场不能覆盖宠物自己的移动变换或隐藏状态。");
        Check(window.ActualThemeVariant == theme, "入场必须保留当前主题。");
        if (!swapped && !collapsed) Capture(window, Path.Combine(directory, $"entrance-complete-{theme.Key}.png"));

        window.Hide();
        var nextTheme = theme == ThemeVariant.Dark ? ThemeVariant.Light : ThemeVariant.Dark;
        window.RequestedThemeVariant = nextTheme;
        window.Show();
        Tick();
        Check(shell.IsHitTestVisible && shell.Children.Where(panel => Grid.GetColumnSpan(panel) == 1).All(panel => panel.Opacity == 1 && panel.RenderTransform == null),
            "从托盘恢复不能重复入场或隐藏内容。");
        Check(window.ActualThemeVariant == nextTheme, "从托盘恢复不能回滚主题。");
        window.Close();
    }

    private static void VerifyInterruptedEntrance(bool beforeFirstFrame)
    {
        var window = new MainWindow();
        var shell = window.FindControl<Grid>("MainShellGrid")!;
        window.PrepareOnboardingEntrance();
        window.Show();
        if (!beforeFirstFrame) PumpUntil(() => shell.Children.Where(panel => Grid.GetColumnSpan(panel) == 1).Any(panel => panel.RenderTransform != null));
        window.Hide();
        PumpUntil(() => shell.IsHitTestVisible);
        window.Show();
        Tick();
        Check(shell.Children.Where(panel => Grid.GetColumnSpan(panel) == 1).All(panel => panel.Opacity == 1 && panel.RenderTransform == null),
            "入场开始前或播放中隐藏窗口，恢复时都必须显示完整界面。");
        window.Close();
    }

    private static void VerifyClosedOnboarding()
    {
        using var viewModel = new OnboardingViewModel();
        var window = new OnboardingWindow(viewModel);
        window.Show();
        Tick();
        var exit = window.PlayHandoffExitAsync();
        window.Close();
        PumpUntil(() => exit.IsCompleted);
        Check(exit.IsCanceled, "交接中关闭初始化窗口必须取消退场，阻止后续打开主窗体。");
    }

    private static void Tick()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static void PumpUntil(Func<bool> ready)
    {
        var timer = Stopwatch.StartNew();
        while (true)
        {
            Tick();
            if (ready()) return;
            if (timer.ElapsedMilliseconds > 5000) throw new InvalidOperationException("初始化动效未在预期时间内完成。");
            Thread.Sleep(4);
        }
    }

    private static void Capture(Window window, string path)
    {
        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("窗口没有可用渲染帧。");
        using var stream = File.Create(path);
        frame.Save(stream, PngBitmapEncoderOptions.Default);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
