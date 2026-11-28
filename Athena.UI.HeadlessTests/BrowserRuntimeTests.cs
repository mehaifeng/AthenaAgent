using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Athena.UI.Services;
using Athena.UI.Services.ComputerUse;
using Athena.UI.Models;
using Microsoft.Data.Sqlite;
using Serilog;

internal static class BrowserRuntimeTests
{
    public static async Task RunAsync(string runtimeDirectory)
    {
        var root = Directory.CreateTempSubdirectory("athena-browser-persistence-");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var token = timeout.Token;
        using var port = new TcpListener(IPAddress.Loopback, 0);
        port.Start();
        var site = new Uri($"http://127.0.0.1:{((IPEndPoint)port.LocalEndpoint).Port}/");
        port.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add(site.AbsoluteUri);
        listener.Start();
        var server = ServeAsync(listener, token);
        try
        {
            var unstagedDirectory = Path.Combine(root.FullName, "UnstagedRuntime");
            var unstaged = new BrowserRuntime(unstagedDirectory, root.FullName);
            try
            {
                await unstaged.PrepareAsync(token);
                throw new InvalidOperationException("Browser startup must diagnose a missing runtime.");
            }
            catch (FileNotFoundException exception)
            {
                var expectedBridge = Path.Combine(unstagedDirectory, OperatingSystem.IsWindows() ? "athena_cef_bridge.dll"
                    : OperatingSystem.IsMacOS() ? "libathena_cef_bridge.dylib" : "libathena_cef_bridge.so");
                Assert(exception.FileName == expectedBridge
                    && exception.Message.Contains("dotnet run", StringComparison.Ordinal),
                    "The missing-runtime error must give the bridge path and the source launch command.");
            }

            var dataDirectory = Path.Combine(root.FullName, "UserBrowserData");
            // Phase 1 runs in an isolated child process so CEF initializes cleanly, exercises all
            // real Chromium 158 capabilities, and flushes cookies + localStorage on real cef_shutdown().
            await RunWorkerAsync("phase1", runtimeDirectory, dataDirectory, site, token);

            // Relocate the runtime installation directory while preserving the same user data directory.
            var movedRuntimeDirectory = Path.Combine(root.FullName, "UpdatedInstall", "BrowserRuntime");
            CopyDirectoryPreservingSymlinks(runtimeDirectory, movedRuntimeDirectory);

            // Phase 2 runs in a fresh OS process to verify real restart persistence and clearing.
            await RunWorkerAsync("phase2", movedRuntimeDirectory, dataDirectory, site, token);

            Console.WriteLine("[PASS] Bundled CEF runtime uses shared persistent cookies and site data with searchable, separately clearable history");
        }
        finally
        {
            listener.Stop();
            await server;
            try { root.Delete(recursive: true); }
            catch (IOException) { /* Best-effort cleanup of temp CEF profile files on Windows. */ }
        }
    }

    internal static async Task RunWorkerPhaseAsync(string phase)
    {
        var runtimeDirectory = Environment.GetEnvironmentVariable("ATHENA_BROWSER_WORKER_RUNTIME")
            ?? throw new InvalidOperationException("Missing ATHENA_BROWSER_WORKER_RUNTIME.");
        var dataDirectory = Environment.GetEnvironmentVariable("ATHENA_BROWSER_WORKER_DATA")
            ?? throw new InvalidOperationException("Missing ATHENA_BROWSER_WORKER_DATA.");
        var site = new Uri(Environment.GetEnvironmentVariable("ATHENA_BROWSER_WORKER_SITE")
            ?? throw new InvalidOperationException("Missing ATHENA_BROWSER_WORKER_SITE."));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = timeout.Token;
        try
        {
            if (phase == "phase1")
            {
                await using var browser = new EmbeddedBrowser(runtimeDirectory, dataDirectory);
                var login = await browser.OpenAsync(new Uri(site, "login"), token);
                await WaitForAsync(() => IsTrue(login, "document.title === 'Signed in' && localStorage.getItem('fixture') === 'ready'", token), token);

                var agent = await browser.OpenTaskAsync(new Uri(site, "session"), token);
                await WaitForAsync(() => IsTrue(agent, "document.body?.textContent.includes('Logged in')", token), token);
                Assert(agent.Id != login.Id, "The task must have its own tab while sharing the signed-in browser profile.");

                await VerifyTabDomainOperationsAsync(browser, login, agent, site, token);
                await VerifyConcurrentSqliteAndAsyncHistoryAsync(browser, agent, dataDirectory, site, token);
                await ReplayInteractionsAsync(browser, agent, dataDirectory, token);
                await VerifyConcurrentBrowserInputAsync(browser, site, token);
                await VerifyTakeoverAsync(browser, site, token);
                await VerifyActiveTimeLimitAsync(browser, site, token);
                await VerifyShadowDialogInputAsync(browser, site, token);
                await VerifyHorizontalScrollingAsync(browser, site, token);
            }
            else if (phase == "phase2")
            {
                await using var restarted = new EmbeddedBrowser(runtimeDirectory, dataDirectory);
                var page = await restarted.OpenAsync(new Uri(site, "session"), token);
                await WaitForAsync(() => IsTrue(page,
                    "document.body?.textContent.includes('Logged in') && localStorage.getItem('fixture') === 'ready'", token), token);
                var loginHistory = await restarted.SearchHistoryAsync("Signed in", token);
                var taskHistory = await restarted.SearchHistoryAsync("/session", token);
                Assert(loginHistory.Any(entry => entry.Url.EndsWith("/login", StringComparison.Ordinal))
                    && taskHistory.Any(entry => entry.Url.EndsWith("/session", StringComparison.Ordinal)),
                    "Committed visits and titles must survive a real process restart and an install-directory change.");
                await restarted.ClearHistoryAsync(token);
                Assert((await restarted.SearchHistoryAsync("", token)).Count == 0
                    && await IsTrue(page, "document.body?.textContent.includes('Logged in')", token),
                    "Clearing history must preserve the current login.");
                await restarted.ClearSiteDataAsync(token);
                var cleared = await restarted.OpenAsync(new Uri(site, "session"), token);
                await WaitForAsync(() => IsTrue(cleared,
                    "document.body?.textContent.includes('Logged out') && localStorage.getItem('fixture') === null", token), token);
            }
            else
            {
                throw new InvalidOperationException($"Unknown browser persistence worker phase: {phase}");
            }
        }
        finally
        {
            await CefNativeBridge.ShutdownProcessAsync();
        }
    }

    private static async Task RunWorkerAsync(string phase, string runtimeDirectory, string dataDirectory, Uri site, CancellationToken token)
    {
        var assembly = Assembly.GetExecutingAssembly().Location;
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(assembly);
        start.Environment.Remove("ATHENA_HEADLESS_ONLY");
        start.Environment.Remove("ATHENA_BROWSER_SMOKE");
        start.Environment["ATHENA_BROWSER_PERSISTENCE_WORKER"] = phase;
        start.Environment["ATHENA_BROWSER_WORKER_RUNTIME"] = runtimeDirectory;
        start.Environment["ATHENA_BROWSER_WORKER_DATA"] = dataDirectory;
        start.Environment["ATHENA_BROWSER_WORKER_SITE"] = site.AbsoluteUri;

        using var proc = Process.Start(start) ?? throw new InvalidOperationException($"Failed to launch CEF worker ({phase}).");
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(token);
        var stderrTask = proc.StandardError.ReadToEndAsync(token);
        await proc.WaitForExitAsync(token);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (!string.IsNullOrWhiteSpace(stdout))
            Console.Write(stdout);
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"CEF persistence worker ({phase}) failed with exit code {proc.ExitCode}.\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
    }

    private static void CopyDirectoryPreservingSymlinks(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var entry in new DirectoryInfo(sourceDir).EnumerateFileSystemInfos())
        {
            var targetPath = Path.Combine(destDir, entry.Name);
            if (entry.LinkTarget is { } linkTarget)
            {
                if (entry is DirectoryInfo)
                    Directory.CreateSymbolicLink(targetPath, linkTarget);
                else
                    File.CreateSymbolicLink(targetPath, linkTarget);
            }
            else if (entry is DirectoryInfo subDir)
            {
                CopyDirectoryPreservingSymlinks(subDir.FullName, targetPath);
            }
            else if (entry is FileInfo file)
            {
                file.CopyTo(targetPath, overwrite: true);
            }
        }
    }

    private static async Task VerifyTabDomainOperationsAsync(
        EmbeddedBrowser browser, ChromeBrowserPage login, ChromeBrowserPage agent, Uri site, CancellationToken token)
    {
        var extra = await browser.NewTabAsync(new Uri(site, "session"), token);
        await WaitForAsync(() => IsTrue(extra, "document.body?.textContent.includes('Logged in')", token), token);
        Assert(browser.AllTabs().Count >= 3, "AllTabs must include both browsing and task tabs in order.");

        // Mute / unmute round-trip on real CEF browser host
        await browser.SetTabMutedAsync(extra, true, token);
        Assert(extra.IsMuted, "SetTabMutedAsync(true) must mute the tab.");
        await browser.SetTabMutedAsync(extra, false, token);
        Assert(!extra.IsMuted, "SetTabMutedAsync(false) must unmute the tab.");

        // MoveTab ordering
        Assert(browser.MoveTab(extra.Id, 0) && browser.AllTabs()[0].Id == extra.Id,
            "MoveTab must reorder tabs deterministically.");

        // Batch close
        var extra2 = await browser.NewTabAsync(EmbeddedBrowser.Blank, token);
        await browser.CloseTabsAsync([extra, extra2], token);
        var remainingIds = browser.AllTabs().Select(t => t.Id).ToHashSet();
        Assert(!remainingIds.Contains(extra.Id) && !remainingIds.Contains(extra2.Id)
            && remainingIds.Contains(login.Id) && remainingIds.Contains(agent.Id),
            "CloseTabsAsync must close the targeted tabs while preserving others.");
        browser.Select(agent);
        Console.WriteLine("[PASS] CEF tabs support ordered listing, drag reordering, audio mute toggling, and batch close");
    }

    private static async Task VerifyConcurrentSqliteAndAsyncHistoryAsync(
        EmbeddedBrowser browser, ChromeBrowserPage agent, string dataDirectory, Uri site, CancellationToken token)
    {
        if (OperatingSystem.IsMacOS())
            Assert(CefNativeBridge.IsFrameworkPreloaded,
                "macOS must preload Chromium Embedded Framework before worker threads/SQLite start to avoid PartitionAlloc malloc_zone unregister races.");

        var stressDb = Path.Combine(dataDirectory, "sqlite-cef-stress.db");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var workers = Enumerable.Range(0, 6).Select(workerId => Task.Run(() =>
        {
            var connStr = new SqliteConnectionStringBuilder
            {
                DataSource = stressDb,
                Pooling = false,
                DefaultTimeout = 5
            }.ConnectionString;
            for (var round = 0; round < 20 && !stop.IsCancellationRequested; round++)
            {
                using var conn = new SqliteConnection(connStr);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE IF NOT EXISTS StressUpsert(Key TEXT PRIMARY KEY, Val TEXT NOT NULL, Ts INTEGER NOT NULL);
                    INSERT INTO StressUpsert(Key, Val, Ts) VALUES ($k, $v, $t)
                    ON CONFLICT(Key) DO UPDATE SET Val = CASE WHEN excluded.Val = '' THEN StressUpsert.Val ELSE excluded.Val END, Ts = excluded.Ts;
                    """;
                cmd.Parameters.AddWithValue("$k", $"worker-{workerId}-key-{round % 4}");
                cmd.Parameters.AddWithValue("$v", $"payload-{workerId}-{round}");
                cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                try { cmd.ExecuteNonQuery(); }
                catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6)
                {
                    // SQLITE_BUSY / SQLITE_LOCKED under intentional 6-thread contention is expected;
                    // the invariant under test is zero allocator traps / deadlocks in libsqlite3mc.
                }
            }
        }, token)).ToArray();

        // Concurrently navigate and update document.title so OnCefEvent queues async BrowserHistory writes
        // while the 6 SQLite workers hammer libsqlite3mc upserts.
        var markerTitle = $"AsyncHistory-{Guid.NewGuid():N}";
        await agent.NavigateAsync(new Uri(site, "session?stress=1"), token);
        await WaitForAsync(() => IsTrue(agent, "document.body?.textContent.includes('Logged in')", token), token);
        await Evaluate(agent, $"document.title = '{markerTitle}'; true", token);
        await WaitForAsync(async () => (await browser.SearchHistoryAsync(markerTitle, token)).Count > 0, token);
        await Task.WhenAll(workers);
        await agent.NavigateAsync(new Uri(site, "session"), token);
        await WaitForAsync(() => IsTrue(agent, "document.body?.textContent.includes('Logged in')", token), token);
        Console.WriteLine("[PASS] Preloaded CEF framework and async history queue stay free of SQLite allocator traps and main-thread deadlocks");
    }

    private static async Task ReplayInteractionsAsync(EmbeddedBrowser browser, ChromeBrowserPage page, string directory, CancellationToken token)
    {
        var source = Path.Combine(directory, "upload.txt");
        await File.WriteAllTextAsync(source, "upload fixture", token);
        var destination = Path.Combine(directory, "download.txt");
        var downloadDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        browser.DownloadCompleted += (_, success, _) => downloadDone.TrySetResult(success);
        void Respond(BrowserInteraction request)
        {
            var answer = request.Kind switch
            {
                "file" => (object)new { files = new[] { source } },
                "download" => new { path = destination },
                "dialog" => new { accept = true, text = "accepted fixture" },
                "permission" => new { accept = false },
                _ => new { }
            };
            _ = request.RespondAsync(answer, token);
        }
        browser.InteractionRequested += Respond;
        try
        {
            await Evaluate(page, "document.getElementById('uploaded').click(); true", token);
            await WaitForAsync(() => IsTrue(page, "document.getElementById('upload-state').textContent === 'upload fixture'", token), token);
            await Evaluate(page, "document.getElementById('download').click(); true", token);
            Assert(await downloadDone.Task.WaitAsync(token) && await File.ReadAllTextAsync(destination, token) == "download fixture",
                "The chosen path must receive the real Chromium download.");
            await Evaluate(page, "setTimeout(() => document.title = prompt('Enter text'), 0); true", token);
            await WaitForAsync(() => IsTrue(page, "document.title === 'accepted fixture'", token), token);
            await Evaluate(page, "navigator.geolocation.getCurrentPosition(() => window.permission = 'granted', () => window.permission = 'denied'); true", token);
            await WaitForAsync(() => IsTrue(page, "window.permission === 'denied'", token), token);
        }
        finally { browser.InteractionRequested -= Respond; }
        Console.WriteLine("[PASS] The browser forwards file selection, downloads, JavaScript prompts and site permissions");
    }

    private static async Task VerifyTakeoverAsync(EmbeddedBrowser browser, Uri site, CancellationToken token)
    {
        var config = new HeadlessConfigService(new AppConfig());
        await using var service = new ComputerUseService(config, Log.Logger, browser, new ModelRoleProbeService());
        var observing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var returnOldClick = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var passes = 0;
        var run = service.RunAsync(ComputerUseTarget.FromBrowserAddress(new Uri(site, "session").AbsoluteUri)!,
            async (frame, previous, _, cancellation) =>
            {
                if (Interlocked.Increment(ref passes) == 1)
                {
                    observing.TrySetResult();
                    await returnOldClick.Task; // Simulate an endpoint that answers after cancellation.
                    return new ComputerUseAction("click", X: 20, Y: 20);
                }
                Assert(previous.Contains("user returned control", StringComparison.OrdinalIgnoreCase)
                    && frame.Tree.Contains("Human updated page", StringComparison.Ordinal), "Resume must observe the actual user-edited page.");
                return new ComputerUseAction("finish", Success: true, Summary: "Resumed");
            }, token);
        await observing.Task.WaitAsync(token);
        var page = browser.TaskPage!;
        service.RequestBrowserTakeover();
        await service.WithManualBrowserInputAsync(page, async () =>
        {
            await Evaluate(page, "document.title = 'Human updated page'; true", token);
        }, token);
        returnOldClick.TrySetResult();
        Assert(service.IsBrowserPaused && Volatile.Read(ref passes) == 1, "Pausing must prevent a re-observation before resume.");
        await service.ResumeBrowserAsync(token);
        var result = await run.WaitAsync(token);
        Assert(result.Success && result.Message == "Resumed"
            && await IsTrue(page, "!window.staleClick", token), "The stale agent action must remain discarded after resume.");
        Console.WriteLine("[PASS] Manual takeover invalidates an in-flight model decision and resumes with a fresh observation");
    }

    private static async Task VerifyConcurrentBrowserInputAsync(EmbeddedBrowser browser, Uri site, CancellationToken token)
    {
        var config = new HeadlessConfigService(new AppConfig());
        await using var service = new ComputerUseService(config, Log.Logger, browser, new ModelRoleProbeService());
        var localization = new LocalizationService();
        using var model = new Athena.UI.ViewModels.EmbeddedBrowserViewModel(browser, service, localization, new BrowserTestUrlOpener());
        var observing = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishDecision = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var passes = 0;
        var run = service.RunAsync(ComputerUseTarget.FromBrowserAddress(new Uri(site, "session").AbsoluteUri)!,
            async (frame, previous, _, cancellation) =>
            {
                if (Interlocked.Increment(ref passes) == 1)
                {
                    observing.TrySetResult(cancellation);
                    await finishDecision.Task.WaitAsync(token);
                    return new ComputerUseAction("click", X: 20, Y: 20);
                }
                Assert(previous.Contains("user interacted", StringComparison.OrdinalIgnoreCase)
                    && frame.Tree.Contains("Manual input during task", StringComparison.Ordinal),
                    "After manual input, the next model observation must see the changed page.");
                return new("finish", Success: true, Summary: "Continued");
            }, token);
        try
        {
            var decisionToken = await observing.Task.WaitAsync(token);
            var page = browser.TaskPage!;
            await Evaluate(page, "navigator.geolocation.getCurrentPosition(() => window.permission = 'granted', () => window.permission = 'denied'); true", token);
            await WaitForAsync(() => IsTrue(page, "window.permission === 'denied'", token), token);
            Assert(service.IsBrowserTaskRunning && !decisionToken.IsCancellationRequested && model.Prompt is null,
                "An ungranted website permission must return denied without blocking or pausing the task.");
            await model.WithManualInputAsync(page, async () =>
            {
                await Evaluate(page, "document.title = 'Manual input during task'; true", token);
            }, token);
            Assert(service.IsBrowserTaskRunning && !decisionToken.IsCancellationRequested && model.Notice == localization.GetString("Browser.ManualInput"),
                "Manual input must send a notice while leaving the in-flight model request and task running.");
            finishDecision.TrySetResult();
            var result = await run.WaitAsync(token);
            Assert(result.Success && result.Message == "Continued" && passes == 2
                && await IsTrue(page, "!window.staleClick", token),
                "The task must advance automatically without applying the stale click.");
        }
        finally { finishDecision.TrySetResult(); }
        Console.WriteLine("[PASS] Website permissions resolve without a pause, and manual input re-observes the page while the task continues");
    }

    private static async Task VerifyActiveTimeLimitAsync(EmbeddedBrowser browser, Uri site, CancellationToken token)
    {
        var config = new HeadlessConfigService(new AppConfig());
        await using var service = new ComputerUseService(config, Log.Logger, browser, new ModelRoleProbeService());
        var observing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ComputerUsePhase? lastPhase = null;
        service.ProgressChanged += progress => lastPhase = progress.Phase;
        var limit = TimeSpan.FromSeconds(3);
        var run = service.RunAsync(ComputerUseTarget.FromBrowserAddress(new Uri(site, "session").AbsoluteUri)!,
            async (_, _, _, cancellation) =>
            {
                observing.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
                throw new InvalidOperationException("The waiting model must be cancelled.");
            }, token, browserTimeLimit: limit);
        await observing.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
        service.RequestBrowserTakeover();
        await Task.Delay(limit + TimeSpan.FromMilliseconds(250), token);
        Assert(service.IsBrowserPaused && !run.IsCompleted, "Time spent in manual takeover must not consume the task time limit.");
        await service.ResumeBrowserAsync(token);
        var result = await run.WaitAsync(limit + TimeSpan.FromSeconds(2), token);
        Assert(lastPhase == ComputerUsePhase.TimedOut && !result.Success
            && result.Message.Contains("active time limit", StringComparison.Ordinal), "Expiry must be reported separately from a user stop.");
        Console.WriteLine("[PASS] Browser task time limits pause during takeover and report expiry after resume");
    }

    private static async Task VerifyShadowDialogInputAsync(EmbeddedBrowser browser, Uri site, CancellationToken token)
    {
        var page = await browser.OpenTaskAsync(new Uri(site, "shadow-search"), token);
        var viewport = new BrowserViewportSize(469, 761, 2);
        await page.ResizeAsync(viewport, token);
        await using var session = new BrowserComputerUseSession(browser, page);
        var frame = await session.ObserveAsync(token);
        Assert(frame.Width == viewport.PixelWidth && frame.Height == viewport.PixelHeight,
            "The model must observe the physical-pixel viewport.");
        await session.ActAsync(frame, new ComputerUseAction("type_text", Text: "Array.map"), 120, 64, token);
        Assert(await IsTrue(page, """
            document.activeElement.id === 'search' &&
            document.activeElement.shadowRoot.activeElement.value === 'Array.map'
            """, token), "2× model coordinates must map to the visible field inside the shadow-root dialog.");
        Console.WriteLine("[PASS] A 2× hidden browser observation maps model pixels to the editable field inside a shadow-root dialog");
    }

    private static async Task VerifyHorizontalScrollingAsync(EmbeddedBrowser browser, Uri site, CancellationToken token)
    {
        var viewport = new BrowserViewportSize(320, 220, 2);
        browser.SetPreferredViewport(viewport);
        var config = new HeadlessConfigService(new AppConfig());
        await using var service = new ComputerUseService(config, Log.Logger, browser, new ModelRoleProbeService());
        var target = ComputerUseTarget.FromBrowserAddress(new Uri(site, "horizontal-scroll").AbsoluteUri)!;
        var result = await service.RunAsync(target, async (frame, _, step, cancellation) =>
        {
            var page = browser.TaskPage!;
            Assert(frame.Width == viewport.PixelWidth && frame.Height == viewport.PixelHeight,
                "A wider document must still produce a screenshot of just the narrow viewport.");
            switch (step)
            {
                case 0:
                    Assert(frame.Tree.Contains("Horizontal page scroll available: right.", StringComparison.Ordinal)
                        && !frame.Tree.Contains("Middle column", StringComparison.Ordinal)
                        && frame.Elements.All(element => element.Label is not "Page target" and not "Panel target"),
                        "The initial observation must report rightward overflow and omit the offscreen text and controls.");
                    return ComputerUsePlanner.ReadBrowserAction("""{"action":"scroll","direction":"right"}""");
                case 1:
                    Assert(await IsTrue(page, "scrollX > innerWidth * 0.7 && scrollX < innerWidth", cancellation)
                        && frame.Tree.Contains("Middle column", StringComparison.Ordinal)
                        && frame.Tree.Contains("Horizontal page scroll available: left, right.", StringComparison.Ordinal),
                        "A rightward step must overlap the previous viewport and reveal the middle column.");
                    return new("click", Element: frame.Elements.Single(element => element.Label == "Page target").Index);
                case 2:
                    Assert(await IsTrue(page, "window.pageClicked === true", cancellation),
                        "The next screenshot must provide clickable coordinates after the page scrolls.");
                    return ComputerUsePlanner.ReadBrowserAction("""{"action":"scroll","direction":"left"}""");
                case 3:
                    Assert(await IsTrue(page, "scrollX === 0", cancellation), "A leftward scroll must return to the starting position.");
                    return ComputerUsePlanner.ReadBrowserAction("""{"action":"scroll","direction":"right","x":100,"y":330}""");
                case 4:
                    Assert(await IsTrue(page, "scrollX === 0 && rail.scrollLeft > rail.clientWidth * 0.7 && rail.scrollLeft < rail.clientWidth", cancellation),
                        "A targeted scroll must overlap the nested panel's previous viewport and leave the page in place.");
                    return new("click", Element: frame.Elements.Single(element => element.Label == "Panel target").Index);
                case 5:
                    Assert(await IsTrue(page, "window.panelClicked === true", cancellation),
                        "The newly visible control inside the panel must be clickable.");
                    return new("finish", Success: true, Summary: "Both targets clicked");
                default:
                    throw new InvalidOperationException("Unexpected horizontal scrolling step.");
            }
        }, token);
        Assert(result.Success && result.Message == "Both targets clicked", result.Message);
        Console.WriteLine("[PASS] Narrow 2× viewport scrolls the page and nested panel horizontally with overlap, then clicks newly visible controls");
    }

    private static async Task<JsonElement> Evaluate(ChromeBrowserPage page, string expression, CancellationToken token)
    {
        var result = await page.Driver.CallAsync("Runtime.evaluate", new { expression, returnByValue = true, userGesture = true }, token);
        return result.GetProperty("result").TryGetProperty("value", out var value) ? value : default;
    }

    private static async Task<bool> IsTrue(ChromeBrowserPage page, string expr, CancellationToken token)
    {
        try { return (await Evaluate(page, expr, token)).ValueKind == JsonValueKind.True; }
        catch (ChromeDevToolsException ex) when (ex.IsNavigation) { return false; }
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition, CancellationToken token)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (await condition()) return;
            await Task.Delay(100, token);
        }
        throw new InvalidOperationException("The browser did not reach the expected test state.");
    }

    private static void Assert(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static async Task ServeAsync(HttpListener listener, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var request = await listener.GetContextAsync().WaitAsync(token);
                using var response = request.Response;
                var path = request.Request.Url?.AbsolutePath;
                var html = "";
                if (path == "/login")
                {
                    response.AppendHeader("Set-Cookie", "authentication=fixture; Path=/; Max-Age=600; HttpOnly; SameSite=Lax");
                    html = "<title>Signed in</title><script>localStorage.setItem('fixture','ready')</script><p>Signed in";
                }
                else if (path == "/download")
                {
                    response.AppendHeader("Content-Disposition", "attachment; filename=fixture.txt");
                    response.ContentType = "text/plain";
                    html = "download fixture";
                }
                else if (path == "/shadow-search")
                {
                    html = """
                        <!doctype html><title>Shadow search</title><div id="search"></div>
                        <script>
                          const root = document.getElementById('search').attachShadow({mode: 'open'});
                          root.innerHTML = '<dialog><input aria-label="Search" style="position:fixed;left:20px;top:20px;width:180px"></dialog>';
                          root.querySelector('dialog').showModal();
                        </script>
                        """;
                }
                else if (path == "/horizontal-scroll")
                {
                    html = """
                        <!doctype html><title>Horizontal scroll</title>
                        <style>
                          body { margin: 0 }
                          main { position: relative; width: 880px; height: 200px; background: white }
                          #rail { position: fixed; left: 16px; top: 140px; width: 160px; height: 62px;
                                  overflow-x: auto; overflow-y: hidden; background: #eef }
                        </style>
                        <main>
                          <p style="position:absolute;left:385px;top:0">Middle column</p>
                          <button style="position:absolute;left:430px;top:56px" onclick="window.pageClicked=true">Page target</button>
                        </main>
                        <section id="rail">
                          <div style="width:600px;height:42px;position:relative">
                            <button style="position:absolute;left:230px;top:8px" onclick="window.panelClicked=true">Panel target</button>
                          </div>
                        </section>
                        """;
                }
                else
                {
                    var login = request.Request.Cookies["authentication"]?.Value == "fixture" ? "Logged in" : "Logged out";
                    html = """
                        <!doctype html><meta charset=utf-8><title>Session page</title>
                        <button style="position:absolute;left:4px;top:4px" onclick="window.staleClick = true">Stale</button>
                        <input id="uploaded" type="file" style="margin:50px 0 0">
                        <output id="upload-state"></output>
                        <a id="download" href="/download">Download</a>
                        <script>
                          uploaded.onchange = async () => document.getElementById('upload-state').textContent = await uploaded.files[0].text();
                        </script>
                        <p>
                        """ + login;
                }
                response.ContentType ??= "text/html; charset=utf-8";
                await response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(html), token);
            }
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or OperationCanceledException) { }
    }
}
