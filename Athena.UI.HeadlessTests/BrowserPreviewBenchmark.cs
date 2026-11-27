using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Athena.UI.Controls;
using Athena.UI.Models;
using Athena.UI.Services;
using Athena.UI.Services.ComputerUse;
using Athena.UI.ViewModels;
using Athena.UI.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml.Styling;
using Serilog;

internal sealed class BrowserPreviewBenchmark : Application
{
    private static string _mode = "";

    public static int Run(string mode)
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("This benchmark needs a Linux or macOS desktop.");
        Log.Logger = new LoggerConfiguration().MinimumLevel.Warning().WriteTo.Console().CreateLogger();
        _mode = mode;
        var builder = AppBuilder.Configure<BrowserPreviewBenchmark>().UsePlatformDetect().WithInterFont();
        if (OperatingSystem.IsLinux())
        {
            builder.With(mode switch
            {
                "bitmap-fallback" => new X11PlatformOptions { RenderingMode = [X11RenderingMode.Software] },
                "bitmap-egl" => new X11PlatformOptions { RenderingMode = [X11RenderingMode.Egl] },
                _ => BrowserGpuPreview.X11Options
            });
        }
        else
            builder.With(new AvaloniaNativePlatformOptions { RenderingMode = [mode == "bitmap-opengl" ? AvaloniaNativeRenderingMode.OpenGl : AvaloniaNativeRenderingMode.Metal] });
        return builder.StartWithClassicDesktopLifetime([]);
    }

    public override void Initialize()
    {
        var app = new Uri("avares://Athena.UI/");
        foreach (var file in new[] { "CoreIcons", "AppIcons" })
            Resources.MergedDictionaries.Add(new ResourceInclude(app) { Source = new Uri(app, $"Styles/{file}.axaml") });
        Styles.Add(new Semi.Avalonia.SemiTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
        var window = new Window { Title = $"Athena browser benchmark · {_mode}", Width = 540, Height = 780, CanResize = false };
        window.Opened += async (_, _) =>
        {
            try
            {
                await MeasureAsync(window);
                desktop.Shutdown(0);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                desktop.Shutdown(1);
            }
        };
        desktop.MainWindow = window;
        base.OnFrameworkInitializationCompleted();
    }

    private static async Task MeasureAsync(Window window)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(75));
        var token = timeout.Token;
        var address = new Uri(RequiredEnvironment("ATHENA_BROWSER_BENCHMARK_URL"));
        var directory = RequiredEnvironment("ATHENA_BROWSER_BENCHMARK_PROFILE");
        var runtime = RequiredEnvironment("ATHENA_BROWSER_RUNTIME_DIRECTORY");
        var useGpu = _mode is "iosurface-metal" or "dmabuf-egl";
        var config = new HeadlessConfigService(new AppConfig());
        await using var browser = new EmbeddedBrowser(runtime, directory, config);
        await using var task = new ComputerUseService(config, Log.Logger, browser, new ModelRoleProbeService());
        using var model = new EmbeddedBrowserViewModel(browser, task, new LocalizationService(), new BrowserTestUrlOpener());
        var view = new EmbeddedBrowserView { DataContext = model, PreviewEnabled = true };
        var probe = new FrameProbe(window);
        view.PreviewFramePresented += probe.OnPresented;
        window.Content = view;
        try
        {
            var testUfo = address.AbsoluteUri == "https://testufo.com/";
            await model.OpenLinkAsync(testUfo ? address : new Uri(address, "scroll"));
            await probe.WaitForPreviewAsync(useGpu || _mode == "dmabuf-loss", token);
            var page = browser.BrowsingPage!;
            if ((_mode is "bitmap-fallback" or "bitmap-no-device") && !page.Driver.SupportsSharedTextures)
                throw new InvalidOperationException("The fallback test needs a connected GPU bridge.");
            if (testUfo)
            {
                if (_mode == "dmabuf-loss") await CrashGpuAndWaitForBitmapAsync(page, view, token);
                var displayHz = X11DisplayRefreshRate.Read(window.Screens.ScreenFromWindow(window));
                await MeasureTestUfoAsync(address, page, probe, useGpu, displayHz, token);
                return;
            }
            if (OperatingSystem.IsLinux())
            {
                await CheckLinuxPreviewPixelsAsync(window, view, page, token);
                await CheckWindowRefreshAsync(window, token);
            }
            var seed = Environment.GetEnvironmentVariable("ATHENA_BROWSER_BENCHMARK_SEED") == "1";
            var profileCheck = "document.cookie.includes('athena_benchmark=1') && localStorage.getItem('athena_benchmark') === '1'";
            if (seed)
                await EvaluateAsync(page, "document.cookie='athena_benchmark=1; Max-Age=3600; Path=/'; localStorage.setItem('athena_benchmark','1')", token);
            if (!(await EvaluateAsync(page, profileCheck, token)).GetBoolean())
                throw new InvalidOperationException("The existing Chromium profile did not retain its cookie and site storage.");

            if (_mode == "dmabuf-loss")
            {
                await CrashGpuAndWaitForBitmapAsync(page, view, token);
                await CheckLinuxPreviewPixelsAsync(window, view, page, token);
            }

            await EvaluateAsync(page, "beginScroll()", token);
            var scroll = await probe.MeasureAsync(token);
            var (width, height) = (probe.Width, probe.Height);
            await EvaluateAsync(page, "endScroll()", token);

            await page.NavigateAsync(new Uri(address, "video"), token);
            await EvaluateAsync(page, "document.readyState === 'loading' ? new Promise(resolve => document.addEventListener('DOMContentLoaded', () => resolve(startVideo), {once:true})) : startVideo", token);
            var video = await probe.MeasureAsync(token);
            if (probe.Width != width || probe.Height != height)
                throw new InvalidOperationException("The preview viewport changed between scrolling and video.");
            var playback = await EvaluateAsync(page, "({ time: clip.currentTime, decoded: clip.getVideoPlaybackQuality().totalVideoFrames })", token);
            if (playback.GetProperty("decoded").GetInt32() < 180)
                throw new InvalidOperationException("The local video did not produce enough decoded frames.");
            if (scroll.TextureSubmissions != (useGpu ? scroll.Submissions : 0)
                || video.TextureSubmissions != (useGpu ? video.Submissions : 0))
                throw new InvalidOperationException("The timed workload used the wrong frame type.");

            await CheckTakeoverAsync(address, browser, task, model, probe, useGpu, token);
            Console.WriteLine("BROWSER_BENCHMARK " + JsonSerializer.Serialize(new { Mode = _mode, Width = width, Height = height, Scroll = scroll, Video = video, Playback = playback }));
        }
        finally
        {
            view.PreviewEnabled = false;
            view.PreviewFramePresented -= probe.OnPresented;
            window.Content = null;
        }
    }

    private static async Task CrashGpuAndWaitForBitmapAsync(ChromeBrowserPage page, EmbeddedBrowserView view, CancellationToken token)
    {
        var bitmap = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFrame(bool texture, BrowserViewportSize _) { if (!texture) bitmap.TrySetResult(); }
        view.PreviewFramePresented += OnFrame;
        try
        {
            await page.Driver.CallAsync("Browser.crashGpuProcess", new { }, token);
            await bitmap.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
        }
        finally { view.PreviewFramePresented -= OnFrame; }
    }

    private static async Task MeasureTestUfoAsync(Uri address, ChromeBrowserPage page, FrameProbe probe,
        bool useGpu, int? displayHz, CancellationToken token)
    {
        const string readStats = """
            ({ fps: parseFloat(document.querySelector('#display_fps')?.innerText) || null,
               hz: parseFloat(document.querySelector('#display_refresh')?.innerText) || null,
               status: document.querySelector('#ufo_status')?.innerText })
            """;
        var warmup = Stopwatch.StartNew();
        while (true)
        {
            var stats = await EvaluateAsync(page, readStats, token);
            if (stats.GetProperty("fps").ValueKind == JsonValueKind.Number) break;
            if (warmup.Elapsed.TotalSeconds > 25) throw new InvalidOperationException("TestUFO did not display a frame rate.");
            await Task.Delay(500, token);
        }
        await Task.Delay(TimeSpan.FromSeconds(6), token);
        var measure = probe.MeasureAsync(token);
        await Task.Delay(2000, token);
        var samples = new List<JsonElement>();
        for (var i = 0; i < 8; i++)
        {
            samples.Add(await EvaluateAsync(page, readStats, token));
            await Task.Delay(1000, token);
        }
        var windowMetrics = await measure;
        if (samples.All(s => s.GetProperty("fps").ValueKind != JsonValueKind.Number))
            throw new InvalidOperationException("TestUFO reported no frame rate during the timed interval.");
        if (windowMetrics.TextureSubmissions != (useGpu ? windowMetrics.Submissions : 0))
            throw new InvalidOperationException("The preview switched rendering paths during the live measurement.");
        Console.WriteLine("BROWSER_LIVE_FPS " + JsonSerializer.Serialize(new { Mode = _mode, Url = address,
            probe.Width, probe.Height, DisplayHz = displayHz, TestUfo = samples, Window = windowMetrics }));
    }

    private static async Task CheckTakeoverAsync(Uri address, EmbeddedBrowser browser, ComputerUseService service,
        EmbeddedBrowserViewModel model, FrameProbe probe, bool useGpu, CancellationToken token)
    {
        var firstObservation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishDecision = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observations = 0;
        var run = service.RunAsync(ComputerUseTarget.FromBrowserAddress(new Uri(address, "scroll").AbsoluteUri)!,
            async (frame, previous, _, cancellation) =>
            {
                if (Interlocked.Increment(ref observations) == 1)
                {
                    firstObservation.TrySetResult();
                    await finishDecision.Task.WaitAsync(token);
                    return new("finish", Success: false, Summary: "Stale decision");
                }
                var continued = previous.Contains("returned control", StringComparison.OrdinalIgnoreCase)
                    && frame.Tree.Contains("Takeover verified", StringComparison.Ordinal);
                return new("finish", Success: continued, Summary: "Takeover verified");
            }, token);
        try
        {
            await firstObservation.Task.WaitAsync(token);
            var page = browser.TaskPage!;
            probe.ClearFirstFrame();
            await model.WithManualInputAsync(page, () => EvaluateAsync(page,
                "document.body.style.backgroundColor = '#31a874'", token), token);
            await probe.WaitForPreviewAsync(useGpu, token);
            model.TakeOverCommand.Execute(null);
            if (!service.IsBrowserPaused) throw new InvalidOperationException("Explicit takeover did not pause the browser task.");
            await model.WithManualInputAsync(page, () => EvaluateAsync(page, "document.title='Takeover verified'", token), token);
            await service.ResumeBrowserAsync(token);
        }
        finally { finishDecision.TrySetResult(); }
        if (!(await run.WaitAsync(token)).Success)
            throw new InvalidOperationException("Resuming after takeover did not re-observe the live page.");
    }

    private static async Task CheckWindowRefreshAsync(Window window, CancellationToken token)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watch = Stopwatch.StartNew();
        var ticks = 0;
        void Tick(TimeSpan _)
        {
            ticks++;
            if (watch.Elapsed.TotalSeconds < 1) window.RequestAnimationFrame(Tick);
            else done.TrySetResult();
        }
        window.RequestAnimationFrame(Tick);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(4), token);
        if (ticks / watch.Elapsed.TotalSeconds < 20)
            throw new InvalidOperationException("The window refresh clock is throttled. Use an active desktop or a 60 Hz virtual monitor.");
    }

    // Read the composited X11 window. A CDP screenshot would pass even with an upside-down or empty Athena pane.
    private static async Task CheckLinuxPreviewPixelsAsync(Window window, EmbeddedBrowserView view, ChromeBrowserPage page, CancellationToken token)
    {
        var layer = view.FindControl<Control>("BrowserGpuLayer")!;
        var xid = window.TryGetPlatformHandle()!.Handle;
        var display = XOpenDisplay(IntPtr.Zero);
        if (display == IntPtr.Zero) throw new InvalidOperationException("Cannot sample the benchmark window on X11.");
        try
        {
            await EvaluateAsync(page, """
                (() => { const box = document.createElement('div'); box.id = 'pixel_test';
                  box.style = 'position:fixed;inset:0;z-index:2147483647;background:linear-gradient(red 50%,blue 50%)';
                  document.body.appendChild(box); })()
                """, token);
            uint Read(double y)
            {
                var point = layer.TranslatePoint(new Point(layer.Bounds.Width / 2, layer.Bounds.Height * y), window)!.Value;
                var p = PixelPoint.FromPoint(point, window.RenderScaling);
                var image = XGetImage(display, xid, p.X, p.Y, 1, 1, nuint.MaxValue, 2); // ZPixmap
                if (image == IntPtr.Zero) throw new InvalidOperationException("Cannot read the benchmark window.");
                try { return (uint)XGetPixel(image, 0, 0) & 0xffffff; }
                finally { XDestroyImage(image); }
            }
            var timeout = Stopwatch.StartNew();
            while (true)
            {
                var top = Read(.25);
                var bottom = Read(.75);
                if (top == 0xff0000 && bottom == 0x0000ff) break;
                if (timeout.Elapsed > TimeSpan.FromSeconds(4))
                    throw new InvalidOperationException($"Incorrect visible preview colors in {_mode} (top {top:x6}, bottom {bottom:x6}).");
                await Task.Delay(50, token);
            }
            await EvaluateAsync(page, "document.getElementById('pixel_test').remove()", token);
        }
        finally { XCloseDisplay(display); }
    }

    [DllImport("libX11.so.6")] private static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport("libX11.so.6")] private static extern int XCloseDisplay(IntPtr display);
    [DllImport("libX11.so.6")] private static extern IntPtr XGetImage(IntPtr display, IntPtr drawable, int x, int y, uint width, uint height, nuint planes, int format);
    [DllImport("libX11.so.6")] private static extern nuint XGetPixel(IntPtr image, int x, int y);
    [DllImport("libX11.so.6")] private static extern int XDestroyImage(IntPtr image);

    private static async Task<JsonElement> EvaluateAsync(ChromeBrowserPage page, string expression, CancellationToken token)
    {
        var result = await page.Driver.CallAsync("Runtime.evaluate", new { expression, awaitPromise = true, returnByValue = true }, token);
        if (result.TryGetProperty("exceptionDetails", out var error)) throw new InvalidOperationException(error.ToString());
        return result.GetProperty("result").TryGetProperty("value", out var value) ? value : default;
    }

    private static string RequiredEnvironment(string key) => Environment.GetEnvironmentVariable(key)
        ?? throw new InvalidOperationException($"Missing {key}");

    private sealed class FrameProbe(Window window)
    {
        private TaskCompletionSource<bool> _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private List<long>? _frames;
        private int _submissions;
        private int _textureSubmissions;
        private bool _tickPending;
        public int Width { get; private set; }
        public int Height { get; private set; }

        public void OnPresented(bool texture, BrowserViewportSize size)
        {
            Width = size.PixelWidth;
            Height = size.PixelHeight;
            _first.TrySetResult(texture);
            if (_frames is not { } frames) return;
            _submissions++;
            if (texture) _textureSubmissions++;
            if (_tickPending) return;
            _tickPending = true;
            window.RequestAnimationFrame(_ =>
            {
                _tickPending = false;
                if (ReferenceEquals(frames, _frames)) frames.Add(Stopwatch.GetTimestamp());
            });
        }

        public void ClearFirstFrame() => _first = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task WaitForPreviewAsync(bool texture, CancellationToken token)
        {
            if (await _first.Task.WaitAsync(TimeSpan.FromSeconds(10), token) != texture)
                throw new InvalidOperationException($"The {_mode} preview did not use its requested renderer.");
        }

        public async Task<FrameMetrics> MeasureAsync(CancellationToken token)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), token);
            using var current = Process.GetCurrentProcess();
            var treeStart = LinuxProcessTreeCpuSeconds();
            var cpuStart = current.TotalProcessorTime;
            var start = Stopwatch.GetTimestamp();
            var frames = _frames = [];
            _submissions = _textureSubmissions = 0;
            try { await Task.Delay(TimeSpan.FromSeconds(8), token); }
            finally { _frames = null; }
            var elapsed = Stopwatch.GetElapsedTime(start).TotalSeconds;
            current.Refresh();
            var cpu = (current.TotalProcessorTime - cpuStart).TotalSeconds / elapsed * 100;
            var treeCpu = (LinuxProcessTreeCpuSeconds() - treeStart) / elapsed * 100;
            var gaps = frames.Zip(frames.Skip(1), (before, after) => Stopwatch.GetElapsedTime(before, after).TotalMilliseconds).Order().ToArray();
            if (gaps.Length < 2) throw new InvalidOperationException("The window received too few preview frames.");
            var percentileIndex = (int)Math.Ceiling(gaps.Length * .95) - 1;
            return new(Math.Round(frames.Count / elapsed, 1), Math.Round(gaps[percentileIndex], 1),
                Math.Round(100.0 * gaps.Count(gap => gap > 25) / gaps.Length, 1), Math.Round(cpu, 1), _submissions, _textureSubmissions,
                treeCpu.HasValue ? Math.Round(treeCpu.Value, 1) : null);
        }
    }

    // Include each live descendant and already reaped children (utime, stime, cutime, cstime).
    // This captures Electron's renderer and GPU process as well as the cost on the Athena side.
    private static double? LinuxProcessTreeCpuSeconds()
    {
        if (!OperatingSystem.IsLinux()) return null;
        var children = new Dictionary<int, List<int>>();
        var ticks = new Dictionary<int, long>();
        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(directory), out var pid)) continue;
            string stat;
            try { stat = File.ReadAllText(Path.Combine(directory, "stat")); }
            catch (IOException) { continue; } // An unrelated process can exit between enumeration and read.
            catch (UnauthorizedAccessException) { continue; } // procfs may hide other users' processes.
            var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' '); // starts at field 3 (state)
            var parent = int.Parse(fields[1]);
            if (!children.TryGetValue(parent, out var list)) children[parent] = list = [];
            list.Add(pid);
            ticks[pid] = long.Parse(fields[11]) + long.Parse(fields[12]) + long.Parse(fields[13]) + long.Parse(fields[14]);
        }
        long Total(int pid) => ticks[pid] + (children.TryGetValue(pid, out var list) ? list.Sum(Total) : 0);
        return (double)Total(Environment.ProcessId) / sysconf(2); // Linux _SC_CLK_TCK
    }

    [DllImport("libc")] private static extern nint sysconf(int name);

    private sealed record FrameMetrics(double Fps, double P95Ms, double Over25MsPercent, double AthenaCpuPercent,
        int Submissions, int TextureSubmissions, double? ProcessTreeCpuPercent);
}
