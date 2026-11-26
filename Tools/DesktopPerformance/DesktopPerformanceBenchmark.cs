using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Athena.UI.ViewModels;
using Athena.UI.Views;
using Athena.UI.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Raw;
using Avalonia.Input;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Avalonia.VisualTree;

// DOTNET_STARTUP_HOOKS attaches this observer only to the disposable benchmark process.
// The binary under test still enters Athena.UI.Program.Main and launches its normal window.
internal static class StartupHook
{
    public static void Initialize()
    {
        var directory = Environment.GetEnvironmentVariable("ATHENA_DESKTOP_BENCHMARK");
        if (Environment.GetEnvironmentVariable("ATHENA_BENCHMARK_UPDATER") is { } updater
            && Environment.ProcessPath == updater) return;
        if (Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory) != directory ||
            !File.Exists(Path.Combine(directory, "AthenaData", "conversations.db")))
            throw new InvalidOperationException("Use the isolated launcher Scripts/benchmark-desktop.py.");
        // Wait for Athena itself to load its themes. Pulling Avalonia or the app into the
        // startup hook before Program.Main would change the startup path being measured.
        void AssemblyLoaded(object? sender, AssemblyLoadEventArgs e)
        {
            if (e.LoadedAssembly.GetName().Name != "Semi.Avalonia") return;
            AppDomain.CurrentDomain.AssemblyLoad -= AssemblyLoaded;
            ObserveWindow(directory);
        }
        AppDomain.CurrentDomain.AssemblyLoad += AssemblyLoaded;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ObserveWindow(string directory) =>
        Window.WindowOpenedEvent.AddClassHandler<MainWindow>((window, args) =>
            Dispatcher.UIThread.Post(() => _ = MeasureAsync(directory), DispatcherPriority.Loaded));

    private static async Task MeasureAsync(string directory)
    {
        try
        {
            // The parent timestamps the apphost launch, including OS and runtime initialization.
            var launch = Environment.GetEnvironmentVariable("ATHENA_BENCHMARK_UPDATER") != null
                ? new DateTimeOffset(Process.GetCurrentProcess().StartTime.ToUniversalTime()).ToUnixTimeMilliseconds()
                : long.Parse(Environment.GetEnvironmentVariable("ATHENA_BENCHMARK_LAUNCH_MS")!);
            long SinceLaunch() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - launch;
            var openedMs = SinceLaunch();
            var lifetime = (Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)
                Application.Current!.ApplicationLifetime!;
            var window = (MainWindow)lifetime.MainWindow!;
            var shell = (MainWindowViewModel)window.DataContext!;
            var view = window.FindControl<MainConversationView>("MainConversationView")!;
            var list = view.FindControl<ItemsControl>("MessagesItemsControl")!;
            var scroll = view.FindControl<ScrollViewer>("ChatScrollViewer")!;
            var compositor = ElementComposition.GetElementVisual(window)!.Compositor;
            Task Render() => compositor.RequestCompositionBatchCommitAsync().Rendered;
            async Task Ready(string title)
            {
                var wait = Stopwatch.StartNew();
                while (true)
                {
                    var messages = shell.DisplayedConversation.Messages;
                    if (!shell.IsConversationSwitching && shell.SelectedConversation?.Title == title
                        && ReferenceEquals(shell.SelectedConversation.Chat, shell.DisplayedConversation)
                        && messages.Count > 0 && messages[^1].Content.StartsWith("Answer 160", StringComparison.Ordinal)
                        && messages[^1].Attachments.Any(a => a.IsImage && a.PreviewImage != null)
                        && list.ContainerFromItem(messages[^1]) is { Bounds.Height: > 0, Opacity: > 0, IsEffectivelyVisible: true, IsHitTestVisible: true } tail
                        && tail.TranslatePoint(default, scroll) is { } top && top.Y < scroll.Bounds.Height
                        && top.Y + tail.Bounds.Height > 0
                        && scroll.Extent.Height - scroll.Viewport.Height - scroll.Offset.Y < 3
                        && RichReplyLaidOut(tail))
                    {
                        await Render();
                        return;
                    }
                    if (wait.Elapsed.TotalSeconds > 90)
                        throw new TimeoutException($"Waiting for {title}: {messages.Count} messages, scroll {scroll.Offset.Y}/{scroll.Extent.Height - scroll.Viewport.Height}");
                    await Task.Delay(12);
                }
            }

            await Ready("Benchmark 000");
            var startupMs = SinceLaunch();
            Console.WriteLine($"DESKTOP startup {startupMs:F0} ms; rows {list.GetRealizedContainers().Count()}");
            if (Environment.GetEnvironmentVariable("ATHENA_BENCHMARK_STARTUP_ONLY") == "1")
            {
                File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(new { openedMs, startupMs }));
                Environment.Exit(0);
            }
            var switchTimer = Stopwatch.StartNew();
            shell.SelectedConversation = shell.ConversationGroups.SelectMany(g => g.Conversations)
                .Single(s => s.Title == "Benchmark 001");
            await Ready("Benchmark 001");
            var switchMs = switchTimer.Elapsed.TotalMilliseconds;
            var rows = list.GetRealizedContainers().Count();
            Console.WriteLine($"DESKTOP switch {switchMs:F0} ms; rows {rows}");

            using var mouse = new MouseDevice();
            var input = new WindowInput(window, mouse);
            var scrollPoint = scroll.TranslatePoint(new Point(scroll.Bounds.Width / 2, scroll.Bounds.Height / 2), window)!.Value;
            async Task<object> Scroll()
            {
                input.Move(scrollPoint);
                var nextTrace = 0d;
                var trace = Environment.GetEnvironmentVariable("ATHENA_PROFILE_FRAMES") != null;
                return await FramesAsync(window, scroll, seconds =>
                {
                    if (trace && seconds >= nextTrace)
                    {
                        var hit = window.InputHitTest(scrollPoint) as Visual;
                        var path = hit?.GetSelfAndVisualAncestors().Take(18).Select(v => v is ScrollViewer s
                            ? $"{v.GetType().Name}[{s.Offset.Y:F0}/{s.Extent.Height:F0}]" : v.GetType().Name);
                        Console.WriteLine($"DESKTOP wheel {seconds:F1}s {scroll.Offset.Y:F0}/{scroll.Extent.Height:F0} {string.Join("/", path ?? [])}");
                        nextTrace += .4;
                    }
                    input.Wheel(scrollPoint, seconds < 1.5 ? 2 : -2);
                },
                    () => scroll.Offset.Y, minTravel: scroll.Viewport.Height * 2);
            }
            var scrollFrames = await Scroll();
            // Jump once to the middle, then cross previously unvisited mixed-height bubbles.
            input.Wheel(scrollPoint, 1);
            var jump = Stopwatch.StartNew();
            var middle = shell.DisplayedConversation.Messages.Single(m => m.IsUser && m.Content.StartsWith("Question 81 —", StringComparison.Ordinal));
            list.ScrollIntoView(middle);
            list.ContainerFromItem(middle)?.BringIntoView();
            while (true)
            {
                window.UpdateLayout();
                if (list.ContainerFromItem(middle) is { Opacity: > 0, Bounds.Height: > 0 } control &&
                    control.TranslatePoint(default, scroll) is { } p)
                {
                    if (p.Y < scroll.Bounds.Height && p.Y + control.Bounds.Height > 0) break;
                    // The original nonvirtualizing panel can return a laid-out container
                    // without bringing it into view. Move the scrollbar to its measured top.
                    scroll.Offset = scroll.Offset.WithY(scroll.Offset.Y + p.Y);
                }
                if (jump.Elapsed.TotalSeconds > 10)
                    throw new TimeoutException($"The conversation did not reach question 81. Offset = {scroll.Offset.Y}.");
                await Task.Delay(8);
            }
            await Render();
            var middleJumpMs = jump.Elapsed.TotalMilliseconds;
            var middleFrames = await Scroll();

            var panel = window.FindControl<Border>("ConversationPanel")!;
            async Task<object> Resize(string name, int sign)
            {
                var splitter = window.FindControl<GridSplitter>(name)!;
                if (Environment.GetEnvironmentVariable("ATHENA_PROFILE_FRAMES") != null)
                    Console.WriteLine($"DESKTOP {name} {string.Join(", ", window.GetVisualDescendants().OfType<ItemsControl>().Where(c => c.ItemCount >= 50).Select(c => $"{c.Name ?? c.GetType().Name}:{c.GetRealizedContainers().Count()}/{c.ItemCount}"))}");
                if (splitter.ShowsPreview) throw new InvalidOperationException("Live resize required.");
                var point = splitter.TranslatePoint(new Point(splitter.Bounds.Width / 2, splitter.Bounds.Height / 2), window)!.Value;
                input.Move(point);
                input.Pointer(RawPointerEventType.LeftButtonDown, point, RawInputModifiers.LeftMouseButton);
                var origin = point;
                var columns = ((Grid)splitter.Parent!).ColumnDefinitions;
                var column = columns[sign < 0 ? 0 : 4];
                var sideSpace = column.ActualWidth - column.MinWidth;
                var chatSpace = columns[2].ActualWidth - columns[2].MinWidth;
                var inward = sideSpace >= chatSpace;
                var direction = inward ? sign : -sign;
                var range = Math.Min(96, Math.Floor(Math.Max(sideSpace, chatSpace)) - 2);
                if (range < 20) throw new InvalidOperationException("Insufficient native splitter clearance.");
                var metrics = await FramesAsync(window, panel, seconds =>
                {
                    // Keep the pointer within the legal range and move at 150 logical pixels/s.
                    // A held splitter at its minimum width has no new layout to render.
                    var excursion = range - Math.Abs(range - (seconds * 150) % (2 * range));
                    point = origin.WithX(origin.X + direction * excursion);
                    input.Pointer(RawPointerEventType.Move, point, RawInputModifiers.LeftMouseButton);
                }, () => panel.Bounds.Width, minTravel: 16);
                input.Pointer(RawPointerEventType.LeftButtonUp, point, RawInputModifiers.None);
                return metrics;
            }
            var resize = await Resize("LeftSideSplitter", -1);
            var rightResize = await Resize("RightSideSplitter", 1);

            var pageTimer = Stopwatch.StartNew();
            shell.OpenProviderModelsCommand.Execute(null);
            window.UpdateLayout();
            var picker = window.GetVisualDescendants().OfType<ComboBox>().First(c => c.Name == "ModelPicker");
            var pageMs = pageTimer.Elapsed.TotalMilliseconds;
            if (Athena.UI.App.Services!.GetRequiredService<IOpenRouterModelMetadataCatalog>().Current.Models.Count != 400)
                throw new InvalidOperationException("The local 400-model reference catalogue did not load.");
            var open = Stopwatch.StartNew();
            picker.IsDropDownOpen = true;
            await Task.Delay(1);
            var option = picker.GetRealizedContainers().First();
            var popupScroll = option.FindAncestorOfType<ScrollViewer>()!;
            var popupWindow = TopLevel.GetTopLevel(option)!;
            await ElementComposition.GetElementVisual(popupWindow)!.Compositor.RequestCompositionBatchCommitAsync().Rendered;
            var openMs = open.Elapsed.TotalMilliseconds;
            var options = picker.GetRealizedContainers().Count();
            if (picker.ItemCount != 2400) throw new InvalidOperationException($"Only {picker.ItemCount} models.");
            var popupInput = new WindowInput(popupWindow, mouse);
            var popupPoint = popupScroll.TranslatePoint(new Point(popupScroll.Bounds.Width / 2, popupScroll.Bounds.Height / 2), popupWindow)!.Value;
            popupInput.Move(popupPoint);
            var modelFrames = await FramesAsync(popupWindow, popupScroll,
                seconds => popupInput.Wheel(popupPoint, seconds < 1.5 ? -1 : 1), () => popupScroll.Offset.Y, minTravel: 32);
            var result = new { openedMs, startupMs, switchMs, rows, scrollFrames, middleJumpMs, middleFrames, resize, rightResize, pageMs, openMs, options, modelFrames };
            Console.WriteLine("DESKTOP " + JsonSerializer.Serialize(result));
            File.WriteAllText(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(result));
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            Environment.Exit(1);
        }
    }

    private static bool RichReplyLaidOut(Control tail)
    {
        bool code = false, diagram = false, process = false, audio = false;
        foreach (var visual in tail.GetVisualDescendants())
        {
            if (visual.Bounds.Width <= 0 || visual.Bounds.Height <= 0) continue;
            code |= visual is LiveMarkdown.Avalonia.CodeBlock;
            diagram |= visual is LiveMarkdown.Avalonia.PanAndZoom;
            process |= visual is Athena.UI.Controls.ProcessRunView;
            audio |= visual is TextBlock { Text: "sample.wav" };
            if (code && diagram && process && audio) return true;
        }
        return false;
    }

    // Feed the same raw mouse/wheel events as the native backend to the real hit tester,
    // including capture, pointer-over styles, nested scrollers and the actual splitter.
    private sealed class WindowInput(TopLevel window, MouseDevice mouse)
    {
        private readonly IInputRoot _root = (IInputRoot)typeof(TopLevel)
            .GetProperty("InputRoot", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        public void Pointer(RawPointerEventType type, Point point, RawInputModifiers modifiers) =>
            window.PlatformImpl!.Input!(new RawPointerEventArgs(mouse, (ulong)Environment.TickCount64, _root, type, point, modifiers));
        public void Move(Point point) => Pointer(RawPointerEventType.Move, point, RawInputModifiers.None);
        public void Wheel(Point point, double y) => window.PlatformImpl!.Input!(new RawMouseWheelEventArgs(mouse,
            (ulong)Environment.TickCount64, _root, point, new Vector(0, y), RawInputModifiers.None));
    }

    // Count distinct render-thread completions that carried changed scroll/layout geometry.
    // A UI animation callback alone is not evidence that the frame was rendered.
    private static async Task<object> FramesAsync(TopLevel window, Control control, Action<double> move, Func<double> coordinate, double minTravel)
    {
        var compositor = ElementComposition.GetElementVisual(control)!.Compositor;
        var samples = new List<(Task Batch, double Position, Task<long> Timestamp)>();
        var done = new TaskCompletionSource();
        var start = Stopwatch.GetTimestamp();
        var profile = Environment.GetEnvironmentVariable("ATHENA_PROFILE_FRAMES") != null;
        var costs = new List<(double Input, double Layout, long Bytes)>();
        void Tick(TimeSpan _)
        {
            var seconds = Stopwatch.GetElapsedTime(start).TotalSeconds;
            var before = profile ? Stopwatch.GetTimestamp() : 0;
            var bytes = profile ? GC.GetAllocatedBytesForCurrentThread() : 0;
            move(seconds);
            var inputDone = profile ? Stopwatch.GetTimestamp() : 0;
            window.UpdateLayout();
            if (profile) costs.Add((Stopwatch.GetElapsedTime(before, inputDone).TotalMilliseconds,
                Stopwatch.GetElapsedTime(inputDone).TotalMilliseconds, GC.GetAllocatedBytesForCurrentThread() - bytes));
            var batch = compositor.RequestCompositionBatchCommitAsync().Rendered;
            var stamp = batch.ContinueWith(_ => Stopwatch.GetTimestamp(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            samples.Add((batch, coordinate(), stamp));
            if (seconds < 3) window.RequestAnimationFrame(Tick);
            else done.SetResult();
        }
        window.RequestAnimationFrame(Tick);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(75));
        await Task.WhenAll(samples.Select(s => s.Timestamp));
        if (profile)
            Console.WriteLine($"DESKTOP timing {control.Name} {string.Join(" ", costs.OrderByDescending(c => c.Input + c.Layout).Take(8).Select(c => $"{c.Input:F1}+{c.Layout:F1}ms/{c.Bytes/1024}KiB"))}");
        var last = double.NaN;
        var times = new List<long>();
        foreach (var sample in samples.GroupBy(s => s.Batch).Select(batch => batch.Last()))
        {
            if (sample.Position == last) continue;
            times.Add(await sample.Timestamp);
            last = sample.Position;
        }
        var travel = samples.Max(s => s.Position) - samples.Min(s => s.Position);
        var intervals = times.Zip(times.Skip(1), (a,b) => Stopwatch.GetElapsedTime(a, b).TotalMilliseconds).Order().ToList();
        var end = await samples[^1].Timestamp;
        var elapsed = Stopwatch.GetElapsedTime(start, end).TotalSeconds;
        var boundaryGap = Math.Max(Stopwatch.GetElapsedTime(start, times[0]).TotalMilliseconds,
            Stopwatch.GetElapsedTime(times[^1], end).TotalMilliseconds);
        return new { frames = times.Count, fps = times.Count / elapsed, travel,
            sufficientMovement = times.Count >= 2 && travel >= minTravel,
            p95Ms = intervals.Count == 0 ? boundaryGap : intervals[(int)(intervals.Count * .95)],
            maxMs = Math.Max(boundaryGap, intervals.LastOrDefault()) };
    }
}
