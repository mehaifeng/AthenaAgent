using Serilog;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.ComputerUse;

/// <summary>Owns the browser process and page lifetimes independently of the right panel.</summary>
public sealed class EmbeddedBrowser : IAsyncDisposable
{
    private static readonly Lazy<string?> ChromePath = new(FindChrome);
    private readonly SemaphoreSlim _lifetime = new(1, 1);
    private DirectoryInfo? _profile;
    private Process? _process;
    private ChromeDevTools? _host;
    private ushort _port;
    private ChromeBrowserPage? _browsingPage;
    private ChromeBrowserPage? _taskPage;
    private bool _disposed;

    public static bool IsAvailable => ChromePath.Value is not null;
    internal ChromeBrowserPage? BrowsingPage => Volatile.Read(ref _browsingPage);
    internal ChromeBrowserPage? TaskPage => Volatile.Read(ref _taskPage);
    internal event Action? PagesChanged;

    internal static bool IsPageUri(Uri address) => address.IsAbsoluteUri
        && address.Scheme is "https" or "http" && address.Host.Length != 0 && address.UserInfo.Length == 0;

    internal Task<ChromeBrowserPage> OpenAsync(Uri address, CancellationToken token) => OpenPageAsync(address, false, token);

    internal Task<ChromeBrowserPage> OpenTaskAsync(Uri address, CancellationToken token) => OpenPageAsync(address, true, token);

    private async Task<ChromeBrowserPage> OpenPageAsync(Uri address, bool task, CancellationToken token)
    {
        if (!IsPageUri(address))
            throw new InvalidOperationException("Enter an HTTP or HTTPS page address.");

        await _lifetime.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await EnsureHostAsync(token).ConfigureAwait(false);
            if (!task && BrowsingPage is { Driver.IsOpen: true } existing)
            {
                await existing.NavigateAsync(address, token).ConfigureAwait(false);
                return existing;
            }

            var page = await CreatePageAsync(address, task, token).ConfigureAwait(false);
            var previous = task
                ? Interlocked.Exchange(ref _taskPage, page)
                : Interlocked.Exchange(ref _browsingPage, page);
            PagesChanged?.Invoke();
            if (previous is not null)
                await ClosePageAsync(previous).ConfigureAwait(false);
            return page;
        }
        finally
        {
            _lifetime.Release();
        }
    }

    private async Task EnsureHostAsync(CancellationToken token)
    {
        if (_process is { HasExited: false } && _host is { IsOpen: true })
            return;
        await CloseHostAsync().ConfigureAwait(false);

        var executable = ChromePath.Value ?? throw new InvalidOperationException("Chrome or Chromium is required for the built-in browser.");
        _profile = Directory.CreateTempSubdirectory("athena-browser-");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false };
        foreach (var argument in new[]
        {
            "--headless=new", "--remote-debugging-port=0", "--remote-debugging-address=127.0.0.1",
            "--user-data-dir=" + _profile.FullName, "--no-first-run", "--no-default-browser-check", "--disable-extensions",
            "--window-size=800,720", "--force-device-scale-factor=1", "about:blank"
        })
            start.ArgumentList.Add(argument);

        try
        {
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(token);
            startup.CancelAfter(TimeSpan.FromSeconds(12));
            _process = Process.Start(start) ?? throw new InvalidOperationException("Could not launch Chrome.");
            var endpoint = await FindHostSocketAsync(_process, _profile.FullName, startup.Token).ConfigureAwait(false);
            _port = (ushort)endpoint.Port;
            _host = new ChromeDevTools();
            await _host.ConnectAsync(endpoint, startup.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await CloseHostAsync().ConfigureAwait(false);
            if (exception is OperationCanceledException && !token.IsCancellationRequested)
                throw new InvalidOperationException("Chrome did not start in time.", exception);
            throw;
        }
    }

    [SuppressMessage("Reliability", "CA2000",
        Justification = "ChromeBrowserPage owns the connected driver. Startup failures dispose it before closing the context.")]
    private async Task<ChromeBrowserPage> CreatePageAsync(Uri address, bool task, CancellationToken token)
    {
        // Distinct browser contexts keep the user's browsing cookies out of an automation task.
        var host = _host ?? throw new InvalidOperationException("Chrome is disconnected.");
        var context = await host.CallAsync("Target.createBrowserContext", new { }, token).ConfigureAwait(false);
        var contextId = context.GetProperty("browserContextId").GetString()!;
        var driver = new ChromeDevTools();
        try
        {
            var target = await host.CallAsync("Target.createTarget", new { url = "about:blank", browserContextId = contextId }, token)
                .ConfigureAwait(false);
            var targetId = target.GetProperty("targetId").GetString();
            var socket = new Uri($"ws://127.0.0.1:{_port}/devtools/page/{targetId}");
            await driver.ConnectAsync(socket, token).ConfigureAwait(false);
            var page = new ChromeBrowserPage(contextId, socket, driver, address);
            await host.CallAsync("Browser.setDownloadBehavior", new
            {
                behavior = "deny", browserContextId = contextId
            }, token).ConfigureAwait(false);
            await page.ResizeAsync(task ? 800 : 720, 720, token).ConfigureAwait(false);
            if (!task)
            {
                await driver.CallAsync("Page.enable", new { }, token).ConfigureAwait(false);
                await driver.CallAsync("Page.addScriptToEvaluateOnNewDocument", new
                {
                    worldName = "Athena Browser",
                    source = """
                        addEventListener('click', e => {
                            const link = e.composedPath().find(node => node instanceof HTMLAnchorElement);
                            if (link?.target === '_blank' && !e.ctrlKey && !e.metaKey && !e.shiftKey)
                                link.target = '_self';
                        }, true);
                        """
                }, token).ConfigureAwait(false);
            }
            await page.NavigateAsync(address, token).ConfigureAwait(false);
            return page;
        }
        catch
        {
            driver.Dispose();
            await DisposeContextAsync(contextId).ConfigureAwait(false);
            throw;
        }
    }

    private async Task ClosePageAsync(ChromeBrowserPage page)
    {
        page.Dispose();
        await DisposeContextAsync(page.ContextId).ConfigureAwait(false);
    }

    private async Task DisposeContextAsync(string contextId)
    {
        if (_host is not { IsOpen: true } host) return;
        try
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await host.CallAsync("Target.disposeBrowserContext", new { browserContextId = contextId }, cleanup.Token)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or InvalidOperationException or WebSocketException)
        {
            Log.Debug("Embedded browser context cleanup ended: {Error}", exception.Message);
        }
    }

    private static async Task<Uri> FindHostSocketAsync(Process process, string profile, CancellationToken token)
    {
        var portFile = Path.Combine(profile, "DevToolsActivePort");
        while (!process.HasExited)
        {
            token.ThrowIfCancellationRequested();
            if (File.Exists(portFile))
            {
                var lines = await File.ReadAllLinesAsync(portFile, token).ConfigureAwait(false);
                if (lines.Length >= 2 && ushort.TryParse(lines[0], NumberStyles.None, CultureInfo.InvariantCulture, out var port)
                    && port != 0 && lines[1].StartsWith("/devtools/browser/", StringComparison.Ordinal))
                    return new Uri($"ws://127.0.0.1:{port}{lines[1]}");
            }
            await Task.Delay(100, token).ConfigureAwait(false);
        }
        throw new InvalidOperationException("Chrome exited before its browser started.");
    }

    private async Task CloseHostAsync()
    {
        var browsing = Interlocked.Exchange(ref _browsingPage, null);
        var task = Interlocked.Exchange(ref _taskPage, null);
        browsing?.Dispose();
        task?.Dispose();
        if (browsing is not null || task is not null)
            PagesChanged?.Invoke();
        _host?.Dispose();
        _host = null;
        try
        {
            if (_process is { HasExited: false } running)
            {
                running.Kill(entireProcessTree: true);
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await running.WaitForExitAsync(cleanup.Token).ConfigureAwait(false);
            }
            _profile?.Delete(recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception
                                         or InvalidOperationException or OperationCanceledException)
        {
            Log.Debug("Embedded Chrome cleanup ended: {Error}", exception.Message);
        }
        finally
        {
            _process?.Dispose();
            _process = null;
            _profile = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            await CloseHostAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifetime.Release();
        }
    }

    private static string? FindChrome()
    {
        IEnumerable<string> candidates;
        if (OperatingSystem.IsWindows())
        {
            candidates = new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.LocalApplicationData }
                .Select(folder => Path.Combine(Environment.GetFolderPath(folder), "Google", "Chrome", "Application", "chrome.exe"));
        }
        else if (OperatingSystem.IsMacOS())
        {
            candidates = ["/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
                "/Applications/Chromium.app/Contents/MacOS/Chromium"];
        }
        else if (OperatingSystem.IsLinux())
        {
            candidates = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Where(Path.IsPathFullyQualified)
                .SelectMany(directory => new[] { "google-chrome", "google-chrome-stable", "chromium", "chromium-browser" }
                    .Select(name => Path.Combine(directory, name)));
        }
        else
        {
            return null;
        }
        return candidates.FirstOrDefault(File.Exists);
    }
}

internal sealed class ChromeBrowserPage(string contextId, Uri socket, ChromeDevTools driver, Uri address) : IDisposable
{
    private Uri _address = address;

    public string ContextId => contextId;
    public Uri Socket => socket;
    public ChromeDevTools Driver => driver;
    public Uri Address => Volatile.Read(ref _address);

    public void UpdateAddress(Uri target) => Volatile.Write(ref _address, target);

    public async Task NavigateAsync(Uri target, CancellationToken token)
    {
        var result = await driver.CallAsync("Page.navigate", new { url = target.AbsoluteUri }, token).ConfigureAwait(false);
        if (result.TryGetProperty("errorText", out var error) && !string.IsNullOrEmpty(error.GetString()))
            throw new InvalidOperationException($"Chrome could not open the page ({error.GetString()}).");
        UpdateAddress(target);
    }

    public Task ResizeAsync(int width, int height, CancellationToken token) => driver.CallAsync(
        "Emulation.setDeviceMetricsOverride", new { width, height, deviceScaleFactor = 1, mobile = false }, token);

    public async Task GoAsync(int offset, CancellationToken token)
    {
        var history = await driver.CallAsync("Page.getNavigationHistory", new { }, token).ConfigureAwait(false);
        var entries = history.GetProperty("entries");
        var index = history.GetProperty("currentIndex").GetInt32() + offset;
        if (index >= 0 && index < entries.GetArrayLength())
        {
            var entryId = entries[index].GetProperty("id").GetInt32();
            await driver.CallAsync("Page.navigateToHistoryEntry", new { entryId }, token).ConfigureAwait(false);
        }
    }

    public void Dispose() => driver.Dispose();
}
