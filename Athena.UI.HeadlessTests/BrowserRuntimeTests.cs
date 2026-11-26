using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Athena.UI.Services;
using Athena.UI.Services.ComputerUse;
using Athena.UI.Models;
using Serilog;

internal static class BrowserRuntimeTests
{
    public static async Task RunAsync(string archiveDirectory)
    {
        var root = Directory.CreateTempSubdirectory("athena-browser-persistence-");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(75));
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
                throw new InvalidOperationException("Browser startup must diagnose a missing archive.");
            }
            catch (FileNotFoundException exception)
            {
                Assert(exception.FileName == Path.Combine(unstagedDirectory, "runtime.zip")
                    && exception.Message.Contains("dotnet run", StringComparison.Ordinal),
                    "The missing-runtime error must give the archive path and the source launch command.");
            }

            await using (var browser = new EmbeddedBrowser(archiveDirectory, root.FullName))
            {
                var login = await browser.OpenAsync(new Uri(site, "login"), token);
                await WaitForAsync(() => IsTrue(login, "document.title === 'Signed in' && localStorage.getItem('fixture') === 'ready'", token), token);

                var agent = await browser.OpenTaskAsync(new Uri(site, "session"), token);
                await WaitForAsync(() => IsTrue(agent, "document.body?.textContent.includes('Logged in')", token), token);
                Assert(agent.Id != login.Id, "The task must have its own tab while sharing the signed-in browser profile.");
                await ReplayInteractionsAsync(browser, agent, root.FullName, token);
                await VerifyConcurrentBrowserInputAsync(browser, site, token);
                await VerifyTakeoverAsync(browser, site, token);
                await VerifyActiveTimeLimitAsync(browser, site, token);
                await VerifyShadowDialogInputAsync(browser, site, token);
                await VerifyHorizontalScrollingAsync(browser, site, token);
            }
            // Recreate the host with a new installation location but the same user browser data.
            var movedArchiveDirectory = Path.Combine(root.FullName, "UpdatedInstall");
            Directory.CreateDirectory(movedArchiveDirectory);
            File.Copy(Path.Combine(archiveDirectory, "runtime.zip"), Path.Combine(movedArchiveDirectory, "runtime.zip"));
            await using (var restarted = new EmbeddedBrowser(movedArchiveDirectory, root.FullName))
            {
                var page = await restarted.OpenAsync(new Uri(site, "session"), token);
                await WaitForAsync(() => IsTrue(page,
                    "document.body?.textContent.includes('Logged in') && localStorage.getItem('fixture') === 'ready'", token), token);
                var loginHistory = await restarted.SearchHistoryAsync("Signed in", token);
                var taskHistory = await restarted.SearchHistoryAsync("/session", token);
                Assert(loginHistory.Any(entry => entry.Url.EndsWith("/login", StringComparison.Ordinal))
                    && taskHistory.Any(entry => entry.Url.EndsWith("/session", StringComparison.Ordinal)),
                    "Committed visits and titles must survive a host restart and an install-directory change.");
                await restarted.ClearHistoryAsync(token);
                Assert((await restarted.SearchHistoryAsync("", token)).Count == 0
                    && await IsTrue(page, "document.body?.textContent.includes('Logged in')", token),
                    "Clearing history must preserve the current login.");
                await restarted.ClearSiteDataAsync(token);
                var cleared = await restarted.OpenAsync(new Uri(site, "session"), token);
                await WaitForAsync(() => IsTrue(cleared,
                    "document.body?.textContent.includes('Logged out') && localStorage.getItem('fixture') === null", token), token);
            }
            Console.WriteLine("[PASS] Bundled runtime uses shared persistent cookies and site data with searchable, separately clearable history");
        }
        finally
        {
            listener.Stop();
            await server;
            root.Delete(recursive: true);
        }
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
        var session = new BrowserComputerUseSession(browser, page);
        try
        {
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
        finally { await session.EndAsync(token); }
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
