using Athena.UI.Services.Interfaces;
using Serilog;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.ComputerUse;

public sealed class EmbeddedBrowser : IAsyncDisposable
{
    internal static readonly Uri Blank = new("about:blank");
    private readonly SemaphoreSlim _lifetime = new(1, 1);
    private readonly object _pagesGate = new();
    private readonly Dictionary<string, ChromeBrowserPage> _pages = [];
    private readonly BrowserRuntime _runtime;
    private readonly BrowserHistory _history;
    private readonly IConfigService? _config;
    private readonly string _directory;
    private FileStream? _profileLease;
    private BrowserHostConnection? _host;
    private ChromeBrowserPage? _browsingPage;
    private ChromeBrowserPage? _taskPage;
    private BrowserViewportSize _preferredViewport = new(800, 720);
    private bool _disposed;

    public EmbeddedBrowser(IConfigService? config = null)
        : this(BrowserRuntime.DefaultDirectory, BrowserRuntime.DefaultDataDirectory, config) { }

    internal EmbeddedBrowser(string runtimeDirectory, string dataDirectory, IConfigService? config = null)
    {
        _runtime = new(runtimeDirectory, dataDirectory);
        _history = new(dataDirectory);
        _directory = dataDirectory;
        _config = config;
    }

    public bool IsAvailable => _runtime.IsBundled;
    public bool IsRunning => _host?.IsOpen == true;
    public string RuntimeVersion => BrowserRuntime.Version;
    public string? RuntimeError { get; private set; }
    internal Uri? HomePage => Uri.TryCreate(_config?.Load().EmbeddedBrowser.HomePage, UriKind.Absolute, out var uri)
        && IsPageUri(uri) ? uri : null;
    internal string? DownloadDirectory => _config?.Load().EmbeddedBrowser.DownloadDirectory;
    internal ChromeBrowserPage? BrowsingPage => Volatile.Read(ref _browsingPage);
    internal ChromeBrowserPage? TaskPage => Volatile.Read(ref _taskPage);

    internal event Action? PagesChanged;
    internal event Action? RuntimeChanged;
    internal event Action? HistoryChanged;
    internal event Action<BrowserInteraction>? InteractionRequested;
    internal event Action<string, bool, bool>? DownloadCompleted;

    internal void SetPreferredViewport(BrowserViewportSize size) => Volatile.Write(ref _preferredViewport, size);

    internal static bool IsPageUri(Uri address) => address.IsAbsoluteUri
        && address.Scheme is "https" or "http" && address.Host.Length != 0 && address.UserInfo.Length == 0;

    internal IReadOnlyList<ChromeBrowserPage> Tabs(bool task)
    {
        lock (_pagesGate) return _pages.Values.Where(page => page.IsTask == task).ToArray();
    }

    internal void Select(ChromeBrowserPage page)
    {
        lock (_pagesGate)
        {
            if (!_pages.ContainsKey(page.Id)) return;
            if (page.IsTask) _taskPage = page;
            else _browsingPage = page;
        }
        PagesChanged?.Invoke();
    }

    internal Task<ChromeBrowserPage> OpenAsync(Uri address, CancellationToken token) => OpenPageAsync(address, false, false, token);
    internal Task<ChromeBrowserPage> NewTabAsync(Uri address, CancellationToken token) => OpenPageAsync(address, false, true, token);
    internal Task<ChromeBrowserPage> OpenTaskAsync(Uri address, CancellationToken token) => OpenPageAsync(address, true, true, token);

    [SuppressMessage("Reliability", "CA2000", Justification = "The scoped cancellation source bounds cleanup and is disposed after it.")]
    private async Task<ChromeBrowserPage> OpenPageAsync(Uri address, bool task, bool newTab, CancellationToken token)
    {
        if (!IsPageUri(address) && (task || address != Blank))
            throw new InvalidOperationException("Enter an HTTP or HTTPS page address.");
        await _lifetime.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await EnsureHostAsync(token).ConfigureAwait(false);
            if (!task && !newTab && BrowsingPage is { } existing)
            {
                await existing.NavigateAsync(address, token).ConfigureAwait(false);
                return existing;
            }
            if (task)
            {
                foreach (var prior in Tabs(true))
                    await CloseTabAsync(prior, token).ConfigureAwait(false);
            }

            var size = Volatile.Read(ref _preferredViewport);
            var response = await _host!.CallAsync("open", new { group = task ? "task" : "browse", width = size.Width, height = size.Height }, token)
                .ConfigureAwait(false);
            var id = response.GetProperty("tab").GetString()!;
            ChromeBrowserPage page;
            lock (_pagesGate) page = _pages[id];
            try
            {
                await page.InitializeViewportAsync(size, token).ConfigureAwait(false);
                if (address != Blank) await page.NavigateAsync(address, token).ConfigureAwait(false);
                return page;
            }
            catch
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await CloseTabAsync(page, cleanup.Token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or OperationCanceledException) { }
                throw;
            }
        }
        finally { _lifetime.Release(); }
    }

    internal Task CloseTabAsync(ChromeBrowserPage page, CancellationToken token) => page.Driver.CallAsync("close", new { }, token);

    internal Task<IReadOnlyList<BrowserHistoryEntry>> SearchHistoryAsync(string query, CancellationToken token) =>
        Task.Run(() => _history.Search(query), token);

    internal async Task ClearHistoryAsync(CancellationToken token)
    {
        await Task.Run(_history.Clear, token).ConfigureAwait(false);
        HistoryChanged?.Invoke();
    }

    internal async Task ClearSiteDataAsync(CancellationToken token)
    {
        await _lifetime.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await EnsureHostAsync(token).ConfigureAwait(false);
            foreach (var page in Tabs(false).Concat(Tabs(true)))
                await CloseTabAsync(page, token).ConfigureAwait(false);
            await _host!.CallAsync("clearData", new { }, token).ConfigureAwait(false);
        }
        finally { _lifetime.Release(); }
    }

    private async Task EnsureHostAsync(CancellationToken token)
    {
        if (_host?.IsOpen == true) return;
        await CloseHostAsync().ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_directory);
            try
            {
                _profileLease = new FileStream(Path.Combine(_directory, "Profile.lock"), FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex)
            {
                throw new InvalidOperationException("The Athena browser profile is in use by another Athena instance.", ex);
            }
            var (executable, script) = await _runtime.PrepareAsync(token).ConfigureAwait(false);
            _host = new BrowserHostConnection(executable, script, Path.Combine(_directory, "Profile"), OnHostEvent);
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(token);
            startup.CancelAfter(TimeSpan.FromSeconds(15));
            await _host.WaitUntilReadyAsync(startup.Token).ConfigureAwait(false);
            RuntimeError = null;
        }
        catch (Exception ex)
        {
            await CloseHostAsync().ConfigureAwait(false);
            RuntimeError = ex is OperationCanceledException && !token.IsCancellationRequested
                ? "The bundled browser did not start in time." : ex.Message;
            if (ex is OperationCanceledException && !token.IsCancellationRequested)
                throw new InvalidOperationException(RuntimeError, ex);
            throw;
        }
        finally { RuntimeChanged?.Invoke(); }
    }

    private void OnHostEvent(JsonElement message)
    {
        var kind = message.GetProperty("event").GetString();
        if (kind == "disconnected")
        {
            ResetPages();
            RuntimeChanged?.Invoke();
            return;
        }

        var id = message.GetProperty("tab").GetString()!;
        var isTask = message.GetProperty("group").GetString() == "task";
        if (kind == "interaction")
        {
            var interaction = new BrowserInteraction(_host!, id, isTask, message.Clone());
            if (InteractionRequested is { } handler) handler(interaction);
            else _ = interaction.RespondAsync(new { }, CancellationToken.None);
            return;
        }
        if (kind == "download")
        {
            DownloadCompleted?.Invoke(message.GetProperty("name").GetString() ?? "", message.GetProperty("ok").GetBoolean(), isTask);
            return;
        }

        Uri? visited = null;
        string? title = null;
        lock (_pagesGate)
        {
            var active = isTask ? _taskPage : _browsingPage;
            if (kind == "opened")
            {
                var opener = message.GetProperty("opener").GetString();
                var page = new ChromeBrowserPage(id, isTask, opener, new ChromeDevTools(_host!, id));
                _pages[id] = page;
                if (opener == null || active?.Id == opener)
                {
                    if (isTask) _taskPage = page;
                    else _browsingPage = page;
                }
            }
            else if (kind == "closed")
            {
                _pages.Remove(id, out var closed);
                if (active?.Id == id)
                {
                    var fallback = closed?.Opener is { } opener && _pages.TryGetValue(opener, out var parent)
                        ? parent : _pages.Values.LastOrDefault(candidate => candidate.IsTask == isTask);
                    if (isTask) _taskPage = fallback;
                    else _browsingPage = fallback;
                }
            }
            else if (_pages.TryGetValue(id, out var page) && Uri.TryCreate(message.GetProperty("url").GetString(),
                UriKind.Absolute, out var address) && IsPageUri(address))
            {
                page.UpdateAddress(address);
                title = message.GetProperty("title").GetString() ?? "";
                page.UpdateTitle(title);
                if (kind is "visit" or "title") visited = address;
            }
        }

        if (visited != null)
        {
            try
            {
                _history.Record(visited, title!, kind == "visit");
                HistoryChanged?.Invoke();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TypeInitializationException
                or Microsoft.Data.Sqlite.SqliteException)
            {
                Log.Warning("Could not save embedded browser history: {ErrorType}", ex.GetType().Name);
            }
        }
        PagesChanged?.Invoke();
    }

    private void ResetPages()
    {
        lock (_pagesGate)
        {
            _pages.Clear();
            _browsingPage = null;
            _taskPage = null;
        }
        PagesChanged?.Invoke();
    }

    private async Task CloseHostAsync()
    {
        try { if (_host != null) await _host.DisposeAsync().ConfigureAwait(false); }
        finally
        {
            _host = null;
            _profileLease?.Dispose();
            _profileLease = null;
            ResetPages();
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
        finally { _lifetime.Release(); }
    }
}

internal sealed class BrowserInteraction(BrowserHostConnection connection, string tab, bool inTask, JsonElement request)
{
    public string Tab => tab;
    public bool InTask => inTask;
    public JsonElement Request => request;
    public string Kind => request.GetProperty("kind").GetString()!;
    public string Text(string key) => request.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
        ? value.GetString() ?? "" : "";
    public Task RespondAsync(object result, CancellationToken token) => connection.CallAsync("reply",
        new { interaction = request.GetProperty("interaction").GetInt32(), result }, token);
}

internal sealed record BrowserViewportSize(int Width, int Height, double Scale = 1)
{
    public int PixelWidth => Math.Max(1, (int)Math.Round(Width * Scale, MidpointRounding.AwayFromZero));
    public int PixelHeight => Math.Max(1, (int)Math.Round(Height * Scale, MidpointRounding.AwayFromZero));
}

[SuppressMessage("Design", "CA1001", Justification = "No wait handle is created and a viewport lease may complete after the tab closes.")]
internal sealed class ChromeBrowserPage(string id, bool task, string? opener, ChromeDevTools driver)
{
    private readonly SemaphoreSlim _viewportGate = new(1, 1);
    private Uri _address = EmbeddedBrowser.Blank;
    private string _title = "";
    private ComputerUseCursor? _cursor;

    public string Id => id;
    public bool IsTask => task;
    public string? Opener => opener;
    public ChromeDevTools Driver => driver;
    public Uri Address => Volatile.Read(ref _address);
    public string Title => Volatile.Read(ref _title);
    public BrowserViewportSize? ViewportSize { get; private set; }
    public int ViewportRevision { get; private set; }
    public event Action<ChromeBrowserPage>? CursorChanged;
    public ComputerUseCursor? Cursor
    {
        get => Volatile.Read(ref _cursor);
        set
        {
            if (!ReferenceEquals(Interlocked.Exchange(ref _cursor, value), value))
                CursorChanged?.Invoke(this);
        }
    }

    public void UpdateAddress(Uri target) => Volatile.Write(ref _address, target);
    public void UpdateTitle(string title) => Volatile.Write(ref _title, title);

    public async Task NavigateAsync(Uri target, CancellationToken token)
    {
        var result = await driver.CallAsync("Page.navigate", new { url = target.AbsoluteUri }, token).ConfigureAwait(false);
        if (result.TryGetProperty("errorText", out var error) && !string.IsNullOrEmpty(error.GetString()))
            throw new InvalidOperationException($"Chrome could not open the page ({error.GetString()}).");
    }

    public Task InitializeViewportAsync(BrowserViewportSize size, CancellationToken token) =>
        ResizeAsync(size, token, onlyIfUnset: true);

    public Task ResizeAsync(BrowserViewportSize size, CancellationToken token) =>
        ResizeAsync(size, token, onlyIfUnset: false);

    private async Task ResizeAsync(BrowserViewportSize size, CancellationToken token, bool onlyIfUnset)
    {
        using var viewport = await LockViewportAsync(token).ConfigureAwait(false);
        if (size == ViewportSize || (onlyIfUnset && ViewportSize is not null)) return;
        token.ThrowIfCancellationRequested();
        using var resize = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await ApplyViewportAsync(size, resize.Token).ConfigureAwait(false);
    }

    public async Task<bool> RepairPreviewViewportAsync(BrowserViewportSize size, CancellationToken token)
    {
        using var viewport = await LockViewportAsync(token).ConfigureAwait(false);
        if (size != ViewportSize) return false;
        using var repair = CancellationTokenSource.CreateLinkedTokenSource(token);
        repair.CancelAfter(TimeSpan.FromSeconds(3));
        var response = await driver.CallAsync("Runtime.evaluate",
            new { expression = "[innerWidth, innerHeight, devicePixelRatio]", returnByValue = true }, repair.Token).ConfigureAwait(false);
        // An old compositor surface can arrive during navigation even when the CSS viewport is already correct.
        if (!response.GetProperty("result").TryGetProperty("value", out var metrics)
            || metrics[0].GetInt32() == size.Width && metrics[1].GetInt32() == size.Height
                && Math.Abs(metrics[2].GetDouble() - size.Scale) < 0.0001)
        {
            await SetSurfaceSizeAsync(size, repair.Token).ConfigureAwait(false);
            return false;
        }
        repair.Token.ThrowIfCancellationRequested();
        using var resize = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await ApplyViewportAsync(size, resize.Token).ConfigureAwait(false);
        return true;
    }

    private async Task ApplyViewportAsync(BrowserViewportSize size, CancellationToken token)
    {
        await driver.CallAsync("setViewport", new
        {
            scale = size.Scale, pixelWidth = size.PixelWidth, pixelHeight = size.PixelHeight,
            cssWidth = size.Width, cssHeight = size.Height
        }, token).ConfigureAwait(false);
        Cursor = null;
        ViewportSize = size;
        ViewportRevision++;
    }

    private Task SetSurfaceSizeAsync(BrowserViewportSize size, CancellationToken token) =>
        driver.CallAsync("Emulation.setVisibleSize", new { width = size.PixelWidth, height = size.PixelHeight }, token);

    public async ValueTask<ViewportLease> LockViewportAsync(CancellationToken token)
    {
        await _viewportGate.WaitAsync(token).ConfigureAwait(false);
        return new ViewportLease(_viewportGate);
    }

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

    internal readonly struct ViewportLease(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }
}
