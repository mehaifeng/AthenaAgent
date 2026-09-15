using Athena.UI.Controls;
using Athena.UI.Models;
using Athena.UI.ViewModels;
using Athena.UI.Views;
using Athena.UI.Services.Interfaces;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SkiaSharp;
using System.Text.Json;
using System.Collections.ObjectModel;

internal static class OwlAnimationTests
{
    public static void Run(string outputPath)
    {
        CheckTimelines();
        CheckMotion();
        CheckNonBlockingPresentationTail();
        var total = 0;
        foreach (var (action, clip) in OwlAnimationClips.All)
        {
            var hashes = new HashSet<string>();
            for (var i = 0; i < clip.Durations.Count; i++)
            {
                using var stream = AssetLoader.Open(new Uri($"avares://Athena.UI/Assets/SubAgents/V2/{action.ToString().ToLowerInvariant()}/{i + 1:D2}.png"));
                using var bitmap = SKBitmap.Decode(stream);
                Require(bitmap.Width == 256 && bitmap.Height == 256, $"{action}/{i}: 画布不统一");
                var occupied = 0;
                for (var y = 0; y < 256; y++) for (var x = 0; x < 256; x++)
                {
                    var alpha = bitmap.GetPixel(x, y).Alpha;
                    if (x < 8 || y < 8 || x >= 248 || y >= 248)
                        Require(alpha == 0, $"{action}/{i}: 透明安全边距包含像素 ({x}, {y})");
                    if (alpha > 32) occupied++;
                }
                Require(occupied > 1000, $"{action}/{i}: 帧为空或主体过小");
                hashes.Add(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bitmap.Bytes)));
                Require(ReferenceEquals(OwlFrameLibrary.Get(action, i), OwlFrameLibrary.Get(action, i)), "素材未共享缓存");
                total++;
            }
            Require(hashes.Count == clip.Durations.Count, $"{action}: 存在复制充数的重复帧");
        }
        Require(total == 124, "完整动作库应为 14 组 / 124 帧");
        Capture(outputPath);
        CaptureVillage(outputPath);
        var folder = Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
        File.WriteAllText(Path.Combine(folder, "owl-clips.json"), JsonSerializer.Serialize(
            OwlAnimationClips.All.Values.Select(c => new { name = c.Action.ToString().ToLowerInvariant(), durations = c.Durations, loop = c.Loop })));
        Console.WriteLine("[PASS] owl animation: 14 clips / 124 unique transparent frames, timing, non-blocking presentation tail, flight priority, landing, retargeting and disposal");
    }

    private static void CheckTimelines()
    {
        foreach (var clip in OwlAnimationClips.All.Values)
        {
            Require(clip.FrameAt(-100) == 0, "负时间应钳制到首帧");
            var boundary = 0;
            for (var i = 0; i < clip.Durations.Count; i++)
            {
                Require(clip.FrameAt(boundary) == i, "帧边界错误");
                boundary += clip.Durations[i];
                Require(clip.FrameAt(boundary - .01) == i, "提前跳帧");
            }
            Require(clip.FrameAt(clip.Duration) == (clip.Loop ? 0 : clip.Durations.Count - 1), "单次动作不应回卷");
            Require(clip.FrameAt(clip.Duration * 1000.0) == (clip.Loop ? 0 : clip.Durations.Count - 1), "长时间暂停后的采样错误");
        }
    }

    private static void CheckMotion()
    {
        var owl = new OwlAnimationPlayer(0, 0, 0, 5);
        owl.SetActivity(SubAgentState.Running, SubAgentZone.Files, 0);
        owl.MoveTo(300, 160, true, 0);
        Require(owl.Action == OwlAction.Takeoff, "跨区必须起飞");
        owl.Sample(600);
        Require(owl.Action == OwlAction.Fly, "起飞后必须振翅");
        owl.MoveTo(10, 10, false, 600);
        Require(owl.Action == OwlAction.Fly && owl.IsTravelling, "游走打断了飞行");
        var beforeX = owl.X;
        var beforeY = owl.Y - owl.Lift;
        owl.MoveTo(400, 200, true, 600);
        Require(Math.Abs(owl.X - beforeX) < .01 && Math.Abs(owl.Y - owl.Lift - beforeY) < .01, "改道瞬移");
        var sawLanding = false;
        for (var t = 650; t < 5000; t += 25)
        {
            owl.Sample(t);
            sawLanding |= owl.Action == OwlAction.Land;
        }
        Require(sawLanding && !owl.IsTravelling && owl.X == 400 && owl.Y == 200, "非归巢区域没有落地");
        owl.SetActivity(SubAgentState.Cancelled, SubAgentZone.Files, 5000);
        owl.MoveTo(0, 0, true, 5100);
        owl.Sample(6000);
        Require(owl.Action == OwlAction.Cancel && !owl.IsTravelling, "取消后仍移动");
        owl.Vanish(6100);
        owl.SetActivity(SubAgentState.Running, SubAgentZone.Web, 6150);
        Require(owl.Action == OwlAction.Farewell, "业务变更覆盖谢幕");

        var skipped = new OwlAnimationPlayer(0, 0, 0, 9);
        skipped.MoveTo(350, 0, true, 0);
        skipped.Sample(100000);
        Require(!skipped.IsTravelling && skipped.X == 350, "恢复后补播过期飞行");
        var vm = new SubAgentViewModel { State = SubAgentState.Running };
        vm.RequestZone(SubAgentZone.Library);
        vm.RequestZone(SubAgentZone.Web); // queues the dwell timer
        vm.Dispose();
        vm.RequestZone(SubAgentZone.Perch);
        vm.AdvanceAnimation(SubAgentViewModel.AnimationNow + 10000);
        Require(vm.Zone == SubAgentZone.Library, "释放后仍接受区域请求");
    }

    private static void CheckNonBlockingPresentationTail()
    {
        var owl = new OwlAnimationPlayer(0, 0, 0, 17);
        owl.SetActivity(SubAgentState.Running, SubAgentZone.Meditation, 0);
        owl.MoveTo(300, 0, true, 0);
        owl.PresentActivity(OwlAction.Read, 0);

        // The real tool can finish immediately; presentation completion is a separate UI concern.
        owl.SetActivity(SubAgentState.Done, SubAgentZone.Files, 10);
        var sawLanding = false;
        var sawRead = false;
        for (var now = 10; now <= 4800; now += 25)
        {
            owl.Sample(now);
            sawLanding |= owl.Action == OwlAction.Land;
            sawRead |= owl.Action == OwlAction.Read;
        }
        Require(sawLanding, "快速工具完成后跳过了目的地区域的落地");
        Require(sawRead, "快速工具完成后没有播放完整的区域动作");
        owl.Sample(5000);
        Require(!owl.HasRequiredPresentation, "区域动作结束后展示门闩没有释放");

        owl.MoveTo(0, 0, true, 5000);
        owl.SetActivity(SubAgentState.Done, SubAgentZone.Perch, 5000);
        var sawSuccess = false;
        var successWasProtected = false;
        for (var now = 5025; now <= 9000; now += 25)
        {
            owl.Sample(now);
            if (owl.Action != OwlAction.Success) continue;
            sawSuccess = true;
            successWasProtected |= owl.HasRequiredPresentation;
        }
        Require(sawSuccess && successWasProtected, "归巢落地后没有保留成功动作的播放窗口");
        owl.Sample(10000);
        Require(!owl.HasRequiredPresentation, "成功动作结束后仍阻塞小镇谢幕");

        using var vm = new SubAgentViewModel { State = SubAgentState.Running };
        var startedAt = SubAgentViewModel.AnimationNow;
        vm.RequestZone(SubAgentZone.Files);
        vm.State = SubAgentState.Done;
        vm.RequestZone(SubAgentZone.Perch);
        Require(vm.Zone == SubAgentZone.Files && vm.HasPendingOwlPresentation,
            "快速完成直接改写了目的地区域");
        var vmSawRead = false;
        var vmSawReturn = false;
        var vmSawSuccess = false;
        for (var elapsed = 0; elapsed <= 12000; elapsed += 25)
        {
            vm.AdvanceAnimation(startedAt + elapsed);
            vmSawRead |= vm.CurrentOwlAction == OwlAction.Read;
            vmSawReturn |= vmSawRead && vm.Zone == SubAgentZone.Perch && vm.IsOwlTravelling;
            vmSawSuccess |= vmSawReturn && vm.CurrentOwlAction == OwlAction.Success;
        }
        Require(vmSawRead && vmSawReturn && vmSawSuccess,
            "ViewModel 没有按目的地动作 → 归巢 → 成功动作完成非阻塞展示");
        Require(!vm.HasPendingOwlPresentation, "ViewModel 展示尾声没有在固定时长内结束");

        using var nextRoundVm = new SubAgentViewModel { State = SubAgentState.Running };
        var nextRoundStartedAt = SubAgentViewModel.AnimationNow;
        nextRoundVm.RequestZone(SubAgentZone.Files);
        nextRoundVm.RequestZone(SubAgentZone.Meditation); // exact request made at the next model iteration
        Require(nextRoundVm.Zone == SubAgentZone.Files,
            "下一轮推理请求在目的地动作前改写了工具区域");
        var nextRoundSawRead = false;
        var nextRoundReturnedTooEarly = false;
        var nextRoundSawThink = false;
        for (var elapsed = 0; elapsed <= 10000; elapsed += 25)
        {
            nextRoundVm.AdvanceAnimation(nextRoundStartedAt + elapsed);
            nextRoundSawRead |= nextRoundVm.CurrentOwlAction == OwlAction.Read;
            nextRoundReturnedTooEarly |= !nextRoundSawRead && nextRoundVm.Zone == SubAgentZone.Meditation;
            nextRoundSawThink |= nextRoundSawRead && nextRoundVm.CurrentOwlAction == OwlAction.Think;
        }
        Require(nextRoundSawRead && !nextRoundReturnedTooEarly && nextRoundSawThink,
            "工具区域没有完成 Read 就返回了下一轮推理");
    }

    private static void Capture(string outputPath)
    {
        var root = new StackPanel { Spacing = 5, Margin = new Thickness(12) };
        foreach (var (action, clip) in OwlAnimationClips.All)
        {
            var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 4 };
            row.Children.Add(new TextBlock { Text = action.ToString(), Width = 74, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
            for (var i = 0; i < clip.Durations.Count; i++)
                row.Children.Add(new Image { Source = OwlFrameLibrary.Get(action, i), Width = 56, Height = 56 });
            root.Children.Add(row);
        }
        var window = new Window { Width = 820, Height = 900, Content = root, Background = Brushes.White };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("猫头鹰素材预览未渲染");
            var file = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outputPath))!, "owl-animation-contact.png");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            using var target = File.Create(file);
            frame.Save(target, PngBitmapEncoderOptions.Default);
        }
        finally { window.Close(); }
    }

    private sealed class PreviewOrchestrator : ISubAgentOrchestrator
    {
        public ObservableCollection<ISubAgentProgress> ActiveAgents { get; } = new();
        public Task<string> DispatchBatchAsync(SubAgentTaskInput[] tasks, CancellationToken cancellationToken)
            => throw new NotSupportedException("Visual fixture never calls providers.");
        public void ClearCompleted() { }
    }

    private static void CaptureVillage(string outputPath)
    {
        var orchestrator = new PreviewOrchestrator();
        using var chat = new MainConversationViewModel(null, null, null, null, null, null, null,
            subAgentOrchestrator: orchestrator);
        foreach (var zone in Enum.GetValues<SubAgentZone>())
        {
            var owl = new SubAgentViewModel { Title = zone.ToString(), Zone = zone, State = SubAgentState.Running };
            // Direct Zone assignment is used only to prepare the test scene; production uses RequestZone.
            owl.SetWander(0, 0);
            orchestrator.ActiveAgents.Add(owl);
        }
        var view = new OwlVillageView { DataContext = chat };
        var window = new Window { Width = 580, Height = 850, Content = view, Background = Brushes.White };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Require(view.GetVisualDescendants().OfType<Image>().Count(i => i.Source is Bitmap) == 6,
                "小镇实际模板中的猫头鹰未显示");
            var active = (SubAgentViewModel)orchestrator.ActiveAgents[0];
            var old = active.OwlFrame;
            // Run the actual platform timer loop. RunJobs only drains queued operations;
            // it does not itself promote native timer deadlines in this headless host.
            using (var clockProbe = new CancellationTokenSource(TimeSpan.FromMilliseconds(2300)))
                Dispatcher.UIThread.MainLoop(clockProbe.Token);
            var timer = (DispatcherTimer)typeof(OwlVillageView).GetField("_spriteTimer",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(view)!;
            Require(!ReferenceEquals(active.OwlFrame, old), $"小镇计时器没有推进实际绑定帧: enabled={timer.IsEnabled}, action={active.CurrentOwlAction}, travel={active.IsOwlTravelling}, context={ReferenceEquals(view.DataContext, chat)}, x={active.OwlX}");
            foreach (var theme in new[] { Avalonia.Styling.ThemeVariant.Light, Avalonia.Styling.ThemeVariant.Dark })
            {
                window.RequestedThemeVariant = theme;
                window.Background = theme == Avalonia.Styling.ThemeVariant.Dark ? Brushes.Black : Brushes.White;
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("小镇未渲染");
                var folder = Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
                using var file = File.Create(Path.Combine(folder, $"owl-village-{theme.Key}.png"));
                frame.Save(file, PngBitmapEncoderOptions.Default);
            }
        }
        finally { window.Close(); }
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
