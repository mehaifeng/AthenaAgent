using System.Diagnostics;
using System.Text.Json;
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
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("This benchmark needs a macOS desktop.");
        Log.Logger = new LoggerConfiguration().MinimumLevel.Warning().WriteTo.Console().CreateLogger();
        _mode = mode;
        var backend = mode == "bitmap-opengl" ? AvaloniaNativeRenderingMode.OpenGl : AvaloniaNativeRenderingMode.Metal;
        return AppBuilder.Configure<BrowserPreviewBenchmark>().UsePlatformDetect().WithInterFont()
            .With(new AvaloniaNativePlatformOptions { RenderingMode = [backend] })
            .StartWithClassicDesktopLifetime([]);
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
        var useGpu = _mode == "iosurface-metal";
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
            await model.OpenLinkAsync(new Uri(address, "scroll"));
            await probe.WaitForPreviewAsync(useGpu, token);
            var page = browser.BrowsingPage!;
            var seed = Environment.GetEnvironmentVariable("ATHENA_BROWSER_BENCHMARK_SEED") == "1";
            var profileCheck = "document.cookie.includes('athena_benchmark=1') && localStorage.getItem('athena_benchmark') === '1'";
            if (seed)
                await EvaluateAsync(page, "document.cookie='athena_benchmark=1; Max-Age=3600; Path=/'; localStorage.setItem('athena_benchmark','1')", token);
            if (!(await EvaluateAsync(page, profileCheck, token)).GetBoolean())
                throw new InvalidOperationException("The existing Chromium profile did not retain its cookie and site storage.");

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
            if (useGpu && (scroll.Submissions != scroll.TextureSubmissions || video.Submissions != video.TextureSubmissions))
                throw new InvalidOperationException("The timed workload fell back to bitmap frames.");

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
            var cpuStart = current.TotalProcessorTime;
            var start = Stopwatch.GetTimestamp();
            var frames = _frames = [];
            _submissions = _textureSubmissions = 0;
            try { await Task.Delay(TimeSpan.FromSeconds(8), token); }
            finally { _frames = null; }
            var elapsed = Stopwatch.GetElapsedTime(start).TotalSeconds;
            current.Refresh();
            var cpu = (current.TotalProcessorTime - cpuStart).TotalSeconds / elapsed * 100;
            var gaps = frames.Zip(frames.Skip(1), (before, after) => Stopwatch.GetElapsedTime(before, after).TotalMilliseconds).Order().ToArray();
            if (gaps.Length < 2) throw new InvalidOperationException("The window received too few preview frames.");
            var percentileIndex = (int)Math.Ceiling(gaps.Length * .95) - 1;
            return new(Math.Round(frames.Count / elapsed, 1), Math.Round(gaps[percentileIndex], 1),
                Math.Round(100.0 * gaps.Count(gap => gap > 25) / gaps.Length, 1), Math.Round(cpu, 1), _submissions, _textureSubmissions);
        }
    }

    private sealed record FrameMetrics(double Fps, double P95Ms, double Over25MsPercent, double AthenaCpuPercent,
        int Submissions, int TextureSubmissions);
}
