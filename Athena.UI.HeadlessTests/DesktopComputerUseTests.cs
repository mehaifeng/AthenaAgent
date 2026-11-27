using System.Text.Json;
using System.Security.Cryptography;
using System.Diagnostics;
using Athena.UI.Models;
using Athena.UI.Services;
using Athena.UI.Services.ComputerUse;
using Athena.UI.Services.Functions;
using Serilog;
using Athena.UI.Controls;
using Avalonia;
using Avalonia.Threading;

internal static class DesktopComputerUseTests
{
    internal static async Task VerifyNativeContractAsync()
    {
        if (!DesktopCursorDisplay.MatchesPortalMonitor(new(2048, 0, 1920, 1080), new(4096, 0, 3840, 2160))
            || DesktopCursorDisplay.MatchesPortalMonitor(new(2048, 0, 1920, 1080), new(0, 0, 3840, 2160)))
            throw new InvalidOperationException("A shared secondary monitor must show the cursor only on its own XWayland output.");
        var desktop = ComputerUseTarget.Desktop;
        var scoped = new ComputerUseTarget(123, 456, "Scoped document", "Editor");
        var browser = ComputerUseTarget.FromBrowserAddress("https://example.org/")!;
        if (ComputerUseFunctions.ResolveTarget(null, null, null, browser) != desktop
            || ComputerUseFunctions.ResolveTarget(null, null, scoped, browser) != scoped
            || ComputerUseFunctions.ResolveTarget(null, "desktop", scoped, browser) != desktop
            || ComputerUseFunctions.ResolveTarget(null, "browser", scoped, browser) != browser
            || ComputerUseFunctions.ResolveTarget("https://example.org/", null, scoped, null) != browser)
            throw new InvalidOperationException("Desktop, explicit window and browser requests must reach the intended surface.");
        var shortcut = ComputerUsePlanner.ReadScreenAction("""{"action":"press_key","key":"Ctrl+End"}""", desktop: true);
        var typing = ComputerUsePlanner.ReadScreenAction("""{"action":"type_text","text":"桌面"}""", desktop: true);
        var targetedTyping = ComputerUsePlanner.ReadScreenAction("""{"action":"type_text","x":312.5,"y":488,"text":"Hi！文件传输助手"}""", desktop: true);
        if (shortcut.Key != "Ctrl+End" || typing.Text != "桌面" || typing.X is not null || typing.Y is not null)
            throw new InvalidOperationException("Desktop keyboard input must use the current focus without requiring a click point.");
        if (targetedTyping.X != 312.5 || targetedTyping.Y != 488 || targetedTyping.Text != "Hi！文件传输助手")
            throw new InvalidOperationException("Desktop type_text must preserve screenshot coordinates when provided.");
        bool rejectedPartial = false;
        try { ComputerUsePlanner.ReadScreenAction("""{"action":"type_text","x":312,"y":null,"text":"partial"}""", desktop: true); }
        catch (JsonException) { rejectedPartial = true; }
        bool rejectedElement = false;
        try { ComputerUsePlanner.ReadScreenAction("""{"action":"type_text","element":2,"text":"wrong"}""", desktop: true); }
        catch (JsonException) { rejectedElement = true; }
        if (!rejectedPartial || !rejectedElement)
            throw new InvalidOperationException("Desktop type_text must reject half-specified coordinates or browser element indices.");
        var dummyFrame = new ComputerUseFrame([1, 2, 3, 4], "image/jpeg", 1280, 800, "", "snap", [], IsDesktop: true);
        if (ComputerUseService.ResolveActionPoint(dummyFrame, targetedTyping) != (312.5, 488)
            || ComputerUseService.ResolveActionPoint(dummyFrame, typing) != (640, 400))
            throw new InvalidOperationException("Desktop type_text point resolution must honor explicit coordinates and default to center when omitted.");
        var planner = new ComputerUsePlanner(new RoleModelRequest(AiModelRole.ComputerUse, "test",
            new EffectiveOpenAiModel("custom", "test", "https://api.example.invalid/v1", "k", "gpt-test", null, 4096),
            30, CapabilitySupport.Supported, true, false), "Type text", browser: false, desktop: true);
        planner.RememberProgress(dummyFrame, typing);
        var unchangedReport = planner.BuildObservationReport(dummyFrame, "ok", 1);
        if (unchangedReport.Contains("already be ready for keyboard input", StringComparison.Ordinal)
            || !unchangedReport.Contains("may not have keyboard focus", StringComparison.Ordinal))
            throw new InvalidOperationException("Unchanged screenshot after desktop typing must warn about missing focus instead of encouraging blind retries.");
        if (OperatingSystem.IsLinux())
        {
            foreach (var (preview, logical, source) in new[] { (1440, 2048, 3072), (900, 1280, 1920), (1440, 1920, 1920) })
            {
                foreach (double pixel in new[] { 0d, preview * .33, preview * .75, preview - .5 })
                {
                    var (absolute, relative) = PortalDesktopSession.MapPointerAxis(pixel, preview, logical, source);
                    var landed = absolute * logical / source + relative;
                    if (absolute < 0 || absolute >= logical || Math.Abs(landed - pixel * logical / preview) > .00001)
                        throw new InvalidOperationException("The complete shared monitor must be reachable within the portal's absolute bounds.");
                }
            }
            var directory = Directory.CreateTempSubdirectory("athena-portal-test-");
            try
            {
                var cache = new PortalRestoreToken(directory.FullName);
                cache.Save("first-test-token");
                cache.Save("replacement-test-token");
                if (File.GetUnixFileMode(directory.GetFiles().Single().FullName) != (UnixFileMode.UserRead | UnixFileMode.UserWrite)
                    || cache.Take() != "replacement-test-token" || cache.Take() is not null)
                    throw new InvalidOperationException("Only the owner can read a remembered portal token, and only one caller may claim it.");
            }
            finally { directory.Delete(true); }
        }
        var calls = new List<(string Tool, JsonElement Args)>();
        Task<JsonElement> Driver(string tool, object args, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            calls.Add((tool, JsonSerializer.SerializeToElement(args)));
            return Task.FromResult(tool == "get_desktop_state"
                ? JsonSerializer.SerializeToElement(new
                {
                    structuredContent = new { screen_width = 2560, screen_height = 1600, screenshot_width = 1280, screenshot_height = 800, capture_id = "snap" },
                    content = new[] { new { type = "image", mimeType = "image/png", data = "AA==" } }
                })
                : JsonSerializer.SerializeToElement(new { content = new[] { new { type = "text", text = "OK" } } }));
        }

        await using IComputerUseSession session = new NativeDesktopSession("test", Driver);
        var frame = await session.ObserveAsync(CancellationToken.None);
        if (!frame.IsDesktop || frame.Width != 1280 || frame.Height != 800 || calls[^1].Tool != "get_desktop_state")
            throw new InvalidOperationException("The desktop must observe the display at its actual screenshot size.");
        await session.ActAsync(frame, new("click", 17, 29), 17, 29, CancellationToken.None);
        var click = calls[^1].Args;
        if (click.GetProperty("scope").GetString() != "desktop" || click.GetProperty("capture_id").GetString() != "snap" ||
            click.GetProperty("x").GetInt32() != 17 || click.TryGetProperty("pid", out _))
            throw new InvalidOperationException("Pixels in a scaled desktop screenshot must be bound to that capture.");
        bool rejected = false;
        try { await session.ActAsync(frame, new("click", 17, 29), 17, 29, CancellationToken.None); }
        catch (InvalidOperationException) { rejected = true; }
        if (!rejected) throw new InvalidOperationException("A desktop frame must expire after an action.");
        frame = await session.ObserveAsync(CancellationToken.None);
        await session.ActAsync(frame, new("press_key", Key: "Alt+Tab"), 0, 0, CancellationToken.None);
        var hotkey = calls[^1];
        if (hotkey.Tool != "hotkey" || hotkey.Args.GetProperty("keys")[0].GetString() != "alt" ||
            hotkey.Args.GetProperty("keys")[1].GetString() != "tab" || hotkey.Args.TryGetProperty("x", out _))
            throw new InvalidOperationException("A desktop switch must go to the foreground as one modifier chord.");
        frame = await session.ObserveAsync(CancellationToken.None);
        int beforeUnfocusedType = calls.Count;
        await session.ActAsync(frame, new("type_text", Text: "桌面"), 0, 0, CancellationToken.None);
        if (calls.Count != beforeUnfocusedType + 1 || calls[^1].Tool != "type_text" || calls[^1].Args.TryGetProperty("window_id", out _) ||
            calls[^1].Args.GetProperty("text").GetString() != "桌面" || DesktopKeys.TryParse("A+Tab", out _))
            throw new InvalidOperationException("Desktop typing must use current focus and chords must have valid modifiers.");
        frame = await session.ObserveAsync(CancellationToken.None);
        int beforeTargetedType = calls.Count;
        await session.ActAsync(frame, new("type_text", X: 42, Y: 88, Text: "Hi！文件传输助手"), 42, 88, CancellationToken.None);
        if (calls.Count != beforeTargetedType + 2
            || calls[^2].Tool != "click" || calls[^2].Args.GetProperty("x").GetInt32() != 42 || calls[^2].Args.GetProperty("y").GetInt32() != 88
            || calls[^2].Args.GetProperty("capture_id").GetString() != "snap"
            || calls[^1].Tool != "type_text" || calls[^1].Args.GetProperty("text").GetString() != "Hi！文件传输助手")
            throw new InvalidOperationException("Targeted desktop type_text must click the requested field before entering text.");
        if (!PortalClipboard.IsSupportedTextMime("text/plain;charset=utf-8")
            || !PortalClipboard.IsSupportedTextMime("UTF8_STRING")
            || PortalClipboard.IsSupportedTextMime("image/png"))
            throw new InvalidOperationException("Portal clipboard MIME filtering must allow UTF-8 text formats and reject binary formats.");
        if (OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("ATHENA_PORTAL_CLIPBOARD_TEST_SERVICE") is { Length: > 0 } testService)
            await VerifyPortalClipboardWireAsync(testService);
        Console.WriteLine("[PASS] Desktop capture binding, whole-display input, focus switching, targeted typing and stale frame rejection");
    }

    private static async Task VerifyPortalClipboardWireAsync(string service)
    {
        var address = Tmds.DBus.Protocol.DBusAddress.Session ?? throw new InvalidOperationException("No test D-Bus session.");
        using var bus = new Tmds.DBus.Protocol.DBusConnection(address);
        await bus.ConnectAsync();
        const string sessionPath = "/org/freedesktop/portal/desktop/session/test/athena";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var clipboard = await PortalClipboard.AttachAsync(bus, sessionPath, _ => { }, cts.Token, service);
        if (!await PortalClipboard.RequestAsync(bus, sessionPath, cts.Token, service))
            throw new InvalidOperationException("PortalClipboard.RequestAsync failed against test portal.");
        await Task.Delay(120, cts.Token);
        bool shortcutSent = false;
        bool pasted = await clipboard.PasteTextAsync("Hi！文件传输助手", async ct =>
        {
            shortcutSent = true;
            var w = bus.GetMessageWriter();
            Tmds.DBus.Protocol.MessageBuffer msg;
            try
            {
                w.WriteMethodCallHeader(service, "/org/freedesktop/portal/desktop", "athena.test.Trigger", "SimulatePaste", "s");
                w.WriteString("UTF8_STRING");
                msg = w.CreateMessage();
            }
            finally { w.Dispose(); }
            await bus.CallMethodAsync(msg).WaitAsync(ct);
        }, cts.Token);
        if (!shortcutSent || !pasted)
            throw new InvalidOperationException("PortalClipboard.PasteTextAsync did not complete transfer on simulated paste.");
        // Allow the test portal to read back the restored clipboard before starting a second paste.
        await Task.Delay(80, cts.Token);
        // Second call without any SelectionTransfer must report false instead of succeeding silently.
        bool silentPasted = await clipboard.PasteTextAsync("ignored-when-unfocused", _ => Task.CompletedTask, cts.Token);
        if (silentPasted)
            throw new InvalidOperationException("PortalClipboard.PasteTextAsync must report false when no control reads the clipboard.");
        Console.WriteLine("[PASS] PortalClipboard D-Bus wire transfer, UTF-8 FD write/EOF, restore and no-reader detection");
    }

    // Explicit opt-in only. Uses the same interactive tool, configured vision role, native
    // permission picker and capture/input implementation as chat. Never run as part of unattended CI.
    internal static int RunLive(string goal)
    {
        AppBuilder.Configure<Application>().UsePlatformDetect().SetupWithoutStarting();
        using var stop = new CancellationTokenSource();
        var run = Task.Run(async () =>
        {
            try { return await RunLiveAsync(goal); }
            finally { stop.Cancel(); }
        });
        Dispatcher.UIThread.MainLoop(stop.Token);
        return run.GetAwaiter().GetResult();
    }

    private static async Task<int> RunLiveAsync(string goal)
    {
        var path = Environment.GetEnvironmentVariable("ATHENA_DESKTOP_SMOKE_CONFIG")
            ?? throw new InvalidOperationException("Specify a Computer Use model configuration for the live desktop smoke.");
        var config = JsonSerializer.Deserialize<AppConfig>(await File.ReadAllTextAsync(path),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
            ?? throw new InvalidDataException("Missing smoke model configuration.");
        var settings = new HeadlessConfigService(config);
        await using var browser = new EmbeddedBrowser(settings);
        var probes = new ModelRoleProbeService();
        await using var service = new ComputerUseService(settings, Log.Logger, browser, probes) { CursorDisplay = new DesktopCursorDisplay() };
        string? previewPath = Environment.GetEnvironmentVariable("ATHENA_DESKTOP_SMOKE_PREVIEW");
        string? fixtureCheck = Environment.GetEnvironmentVariable("ATHENA_DESKTOP_SMOKE_CHECK");
        if (fixtureCheck is not null)
        {
            // Resolve the capability probe before the fixture asks the desktop user to yield the keyboard.
            var model = probes.Resolve(config, AiModelRole.ComputerUse);
            if (probes.NeedsVisionProof(model))
                await probes.TestAsync(model, CancellationToken.None);
            CheckFixture(fixtureCheck, "begin", 90000);
        }
        service.ProgressChanged += p =>
        {
            // A live fixture can assert its workspace and foreground window before an image leaves the process.
            if (fixtureCheck is not null && p.Frame is not null && p.Phase is ComputerUsePhase.Observing or ComputerUsePhase.Moving)
                CheckFixture(fixtureCheck, "check", 3000);
            var digest = p.Phase == ComputerUsePhase.Observing && p.Frame is { } f
                ? Convert.ToHexString(SHA256.HashData(f.Image))[..12] : "";
            if (previewPath is not null && p.Phase == ComputerUsePhase.Observing && p.Frame is { } frame)
            {
                File.WriteAllBytes(previewPath + ".new", frame.Image);
                File.Move(previewPath + ".new", previewPath, true);
            }
            Console.WriteLine($"Desktop {p.Phase} {p.Frame?.Width}x{p.Frame?.Height} {digest} {p.Detail}");
        };
        using var interactive = ToolApprovalContext.EnterInteractive(goal);
        var result = await new ComputerUseFunctions(service).RunComputerTaskAsync(goal);
        Console.WriteLine(result.ToJson());
        if (PortalDesktopSession.IsWayland && Environment.GetEnvironmentVariable("ATHENA_DESKTOP_SMOKE_VERIFY_RESTORE") == "1")
        {
            if (!PortalRestoreToken.ForUser().IsAvailable) throw new InvalidOperationException("GNOME did not grant persistent desktop access.");
            using var restoreLimit = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            var timer = Stopwatch.StartNew();
            await using IComputerUseSession resumed = await PortalDesktopSession.OpenAsync(restoreLimit.Token);
            var frame = await resumed.ObserveAsync(restoreLimit.Token);
            Console.WriteLine($"[PASS] Fresh portal connection restored the authorized {frame.Width}x{frame.Height} monitor in {timer.ElapsedMilliseconds} ms.");
        }
        return result.Success ? 0 : 1;
    }

    private static void CheckFixture(string executable, string phase, int timeoutMs)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, ArgumentList = { phase } };
        using var check = Process.Start(start) ?? throw new InvalidOperationException("The desktop fixture check could not start.");
        if (!check.WaitForExit(timeoutMs))
        {
            check.Kill(entireProcessTree: true);
            throw new TimeoutException("The desktop fixture check timed out.");
        }
        if (check.ExitCode != 0) throw new InvalidOperationException("The desktop left the expected test workspace or window.");
    }
}
