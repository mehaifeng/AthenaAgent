using Athena.UI.Services.Interfaces;
using Serilog;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.ComputerUse;

public sealed class EmbeddedBrowser : IAsyncDisposable
{
    internal static readonly Uri Blank = new("about:blank");
    private readonly SemaphoreSlim _lifetime = new(1, 1);
    private readonly object _pagesGate = new();
    private readonly object _historyQueueGate = new();
    private Task _pendingHistoryOp = Task.CompletedTask;
    private readonly Dictionary<string, ChromeBrowserPage> _pages = [];
    private readonly List<string> _tabOrder = [];
    private readonly BrowserRuntime _runtime;
    private readonly BrowserHistory _history;
    private readonly IConfigService? _config;
    private readonly string _directory;
    private FileStream? _profileLease;
    private CefNativeSession? _session;
    private ChromeBrowserPage? _browsingPage;
    private ChromeBrowserPage? _taskPage;
    private ChromeBrowserPage? _activePage;
    private ChromeBrowserPage? _displayedPage;
    private BrowserViewportSize _preferredViewport = new(800, 720);
    private nint _surfaceParent;
    private BrowserViewportSize _surfaceSize = new(800, 720);
    private bool _surfaceVisible;
    private int _nextBlankTabId;
    private int _pendingOpens;
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
    public bool IsRunning => _session?.IsOpen == true;
    public string RuntimeVersion => BrowserRuntime.Version;
    public string ChromiumVersion => BrowserRuntime.ChromiumVersion;
    public string? RuntimeError { get; private set; }
    internal string RuntimeDirectory => _runtime.RuntimeDirectory;
    internal Uri? HomePage => TryNormalizeUserAddress(_config?.Load().EmbeddedBrowser.HomePage, out var uri) ? uri : null;
    internal string? DownloadDirectory => _config?.Load().EmbeddedBrowser.DownloadDirectory;
    internal ChromeBrowserPage? BrowsingPage => Volatile.Read(ref _browsingPage);
    internal ChromeBrowserPage? TaskPage => Volatile.Read(ref _taskPage);
    internal ChromeBrowserPage? ActivePage => Volatile.Read(ref _activePage);
    internal bool HasPendingOpen => Volatile.Read(ref _pendingOpens) > 0;

    internal event Action? PagesChanged;
    internal event Action? RuntimeChanged;
    internal event Action? HistoryChanged;
    internal event Action<BrowserInteraction>? InteractionRequested;
    internal event Action<string, bool, bool>? DownloadCompleted;
    internal event Action<ChromeBrowserPage>? UserInteracted;

    internal void SetPreferredViewport(BrowserViewportSize size) => Volatile.Write(ref _preferredViewport, size);

    internal static bool IsPageUri(Uri address) => address.IsAbsoluteUri
        && address.Scheme is "https" or "http" && address.Host.Length != 0 && address.UserInfo.Length == 0;

    /// <summary>
    /// Normalizes a user-entered address bar / home-page string into an HTTP(S) <see cref="Uri"/>.
    /// Allows omitting the scheme (defaulting to <c>https://</c>, or <c>http://</c> for localhost,
    /// loopback, and private IPv4 literals), while rejecting non-web schemes such as <c>file:</c>,
    /// <c>javascript:</c>, <c>data:</c>, <c>mailto:</c>, and <c>ftp://</c>.
    /// </summary>
    internal static bool TryNormalizeUserAddress(string? raw, [NotNullWhen(true)] out Uri? address)
    {
        address = null;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var text = raw.Trim();
        if (text.Any(char.IsWhiteSpace)) return false;

        var schemeSep = text.IndexOf("://", StringComparison.Ordinal);
        if (schemeSep >= 0)
        {
            if (Uri.TryCreate(text, UriKind.Absolute, out var explicitUri) && IsPageUri(explicitUri))
            {
                address = explicitUri;
                return true;
            }
            return false;
        }

        if (text.StartsWith("//", StringComparison.Ordinal))
            text = text[2..];
        if (text.Length == 0) return false;

        if (!text.StartsWith('['))
        {
            var authorityEnd = text.IndexOfAny(['/', '?', '#']);
            var authority = authorityEnd >= 0 ? text[..authorityEnd] : text;
            var colon = authority.IndexOf(':');
            if (colon >= 0)
            {
                var afterColon = authority[(colon + 1)..];
                if (afterColon.Length == 0 || !afterColon.All(char.IsAsciiDigit))
                    return false;
            }
        }

        if (!Uri.TryCreate("https://" + text, UriKind.Absolute, out var candidate) || !IsPageUri(candidate))
            return false;

        var host = candidate.IdnHost;
        var isValidHost = candidate.HostNameType switch
        {
            UriHostNameType.IPv4 or UriHostNameType.IPv6 => true,
            UriHostNameType.Dns => IsLocalhostHost(host) || HasValidDomainDot(host),
            _ => false
        };
        if (!isValidHost) return false;

        var scheme = IsDefaultHttpHost(candidate) ? "http://" : "https://";
        if (!Uri.TryCreate(scheme + text, UriKind.Absolute, out var normalized) || !IsPageUri(normalized))
            return false;

        address = normalized;
        return true;
    }

    private static bool IsLocalhostHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);

    private static bool HasValidDomainDot(string host)
    {
        if (host.Length < 3 || host.StartsWith('.') || host.EndsWith('.') || host.Contains("..", StringComparison.Ordinal))
            return false;
        var parts = host.Split('.');
        return parts.Length >= 2
            && parts.All(p => p.Length > 0)
            && !parts[^1].All(char.IsAsciiDigit);
    }

    private static bool IsDefaultHttpHost(Uri uri)
    {
        if (IsLocalhostHost(uri.Host)) return true;
        if (!IPAddress.TryParse(uri.Host, out var ip)) return false;
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168);
        }
        return false;
    }

    internal IReadOnlyList<ChromeBrowserPage> AllTabs()
    {
        lock (_pagesGate)
        {
            var result = new List<ChromeBrowserPage>(_tabOrder.Count);
            foreach (var id in _tabOrder)
                if (_pages.TryGetValue(id, out var page)) result.Add(page);
            return result;
        }
    }

    internal IReadOnlyList<ChromeBrowserPage> Tabs(bool task)
    {
        lock (_pagesGate)
        {
            var result = new List<ChromeBrowserPage>();
            foreach (var id in _tabOrder)
                if (_pages.TryGetValue(id, out var page) && page.IsTask == task) result.Add(page);
            return result;
        }
    }

    internal void Select(ChromeBrowserPage page)
    {
        lock (_pagesGate)
        {
            if (!_pages.ContainsKey(page.Id)) return;
            _activePage = page;
            if (page.IsTask) _taskPage = page;
            else _browsingPage = page;
        }
        SyncSurfaceToPages();
        PagesChanged?.Invoke();
    }

    internal bool MoveTab(string tabId, int targetIndex)
    {
        lock (_pagesGate)
        {
            var current = _tabOrder.IndexOf(tabId);
            if (current < 0) return false;
            var clamped = Math.Clamp(targetIndex, 0, _tabOrder.Count - 1);
            if (current == clamped) return false;
            _tabOrder.RemoveAt(current);
            _tabOrder.Insert(clamped, tabId);
        }
        PagesChanged?.Invoke();
        return true;
    }

    internal async Task SetTabMutedAsync(ChromeBrowserPage page, bool muted, CancellationToken token)
    {
        await page.SetMutedAsync(muted, token).ConfigureAwait(false);
        PagesChanged?.Invoke();
    }

    internal async Task CloseTabsAsync(IEnumerable<ChromeBrowserPage> pages, CancellationToken token)
    {
        var snapshot = pages.ToArray();
        foreach (var page in snapshot)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                await CloseTabAsync(page, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ChromeDevToolsException or InvalidOperationException)
            {
                Log.Debug("Skipping already-closed browser tab {TabId}: {Error}", page.Id, ex.Message);
            }
        }
    }

    internal void AttachSurface(nint parentHandle, BrowserViewportSize size, bool visible, ChromeBrowserPage? displayedPage = null)
    {
        lock (_pagesGate)
        {
            _surfaceParent = parentHandle;
            _surfaceSize = size;
            _surfaceVisible = visible;
            _displayedPage = displayedPage;
        }
        SetPreferredViewport(size);
        SyncSurfaceToPages();
    }

    internal void UpdateSurface(nint parentHandle, BrowserViewportSize size, bool visible, ChromeBrowserPage? displayedPage = null)
    {
        lock (_pagesGate)
        {
            _surfaceParent = parentHandle;
            _surfaceSize = size;
            _surfaceVisible = visible;
            _displayedPage = displayedPage;
        }
        SetPreferredViewport(size);
        SyncSurfaceToPages();
    }

    internal void DetachSurface()
    {
        lock (_pagesGate)
        {
            _surfaceParent = 0;
            _surfaceVisible = false;
            _displayedPage = null;
        }
        SyncSurfaceToPages();
    }

    internal void SetSurfaceFocus(bool focused)
    {
        ChromeBrowserPage? target;
        lock (_pagesGate)
            target = _displayedPage ?? _activePage;
        if (target is not null && _session is { IsOpen: true })
            _session.SetTabFocus(target.TabId, focused);
    }

    private void SyncSurfaceToPages()
    {
        ChromeBrowserPage[] all;
        ChromeBrowserPage? target;
        nint parent;
        BrowserViewportSize size;
        bool visible;
        CefNativeSession? session;
        lock (_pagesGate)
        {
            session = _session;
            if (session is not { IsOpen: true }) return;
            all = _pages.Values.ToArray();
            target = _displayedPage ?? _activePage;
            parent = _surfaceParent;
            size = _surfaceSize;
            visible = _surfaceVisible;
        }

        foreach (var page in all)
        {
            if (page.IsPlaceholder) continue;
            var showHere = visible && ReferenceEquals(page, target);
            if (CefNativeBridge.IsHeadlessRuntime)
            {
                // Headless viewport metrics (SetTabBounds + Emulation.setDeviceMetricsOverride + ViewportSize)
                // are applied atomically through ChromeBrowserPage.ResizeAsync so CDP never observes stale metrics.
                continue;
            }
            if (showHere && parent != 0)
            {
                page.UpdateNativeViewportSize(size);
                session.SetTabParent(page.TabId, parent, size.Width, size.Height, size.Scale, visible: true);
            }
            else
            {
                var parkedSize = page.ViewportSize ?? size;
                session.SetTabParent(page.TabId, 0, parkedSize.Width, parkedSize.Height, parkedSize.Scale, visible: false);
            }
        }
    }

    internal ChromeBrowserPage OpenBlankTab(Uri? initialAddress = null)
    {
        ChromeBrowserPage page;
        lock (_pagesGate)
        {
            var id = $"blank-{Interlocked.Increment(ref _nextBlankTabId)}";
            var driver = new ChromeDevTools((_, _, _) => Task.FromResult(JsonDocument.Parse("{}").RootElement.Clone()));
            page = new ChromeBrowserPage(id, tabId: 0, task: false, opener: null, driver, session: null, isPlaceholder: true);
            if (initialAddress is not null && IsPageUri(initialAddress))
                page.UpdateAddress(initialAddress);
            _pages[id] = page;
            _tabOrder.Add(id);
            _browsingPage = page;
            _activePage = page;
        }
        SyncSurfaceToPages();
        PagesChanged?.Invoke();
        return page;
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
        Interlocked.Increment(ref _pendingOpens);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            string? promoteId = null;
            var promoteMuted = false;
            if (!task && !newTab && BrowsingPage is { IsPlaceholder: true } placeholder)
            {
                promoteId = placeholder.Id;
                promoteMuted = placeholder.IsMuted;
            }

            await EnsureHostAsync(token).ConfigureAwait(false);
            if (!task && !newTab && BrowsingPage is { IsPlaceholder: false } existing)
            {
                Select(existing);
                await existing.NavigateAsync(address, token).ConfigureAwait(false);
                return existing;
            }
            if (task)
            {
                foreach (var prior in Tabs(true))
                    await CloseTabAsync(prior, token).ConfigureAwait(false);
            }

            var size = Volatile.Read(ref _preferredViewport);
            nint parent;
            bool visible;
            lock (_pagesGate)
            {
                parent = _surfaceParent;
                visible = _surfaceVisible;
            }

            var tabId = await _session!.CreateTabAsync(
                CefNativeBridge.IsHeadlessRuntime ? 0 : parent,
                size.Width, size.Height, size.Scale,
                task ? "task" : "browse",
                Blank.AbsoluteUri,
                visible && !CefNativeBridge.IsHeadlessRuntime && parent != 0,
                token).ConfigureAwait(false);

            var id = tabId.ToString(CultureInfo.InvariantCulture);
            ChromeBrowserPage page;
            var promoted = false;
            lock (_pagesGate)
            {
                page = _pages[id];
                if (promoteId is not null && _pages.Remove(promoteId))
                {
                    var promoteIndex = _tabOrder.IndexOf(promoteId);
                    if (promoteIndex >= 0)
                    {
                        _tabOrder.Remove(id);
                        _tabOrder[promoteIndex] = id;
                    }
                    _browsingPage = page;
                    _activePage = page;
                    promoted = true;
                }
            }
            if (promoted) PagesChanged?.Invoke();
            try
            {
                if (promoteMuted)
                    await page.SetMutedAsync(true, token).ConfigureAwait(false);
                await page.InitializeViewportAsync(size, token).ConfigureAwait(false);
                if (address != Blank) await page.NavigateAsync(address, token).ConfigureAwait(false);
                SyncSurfaceToPages();
                return page;
            }
            catch
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await CloseTabAsync(page, cleanup.Token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or OperationCanceledException)
                {
                    // Best-effort cleanup of the newly created tab after navigation failure.
                }
                throw;
            }
        }
        finally
        {
            Interlocked.Decrement(ref _pendingOpens);
            _lifetime.Release();
        }
    }

    internal Task CloseTabAsync(ChromeBrowserPage page, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (page.IsPlaceholder || _session is not { IsOpen: true } session)
        {
            var removed = false;
            lock (_pagesGate)
                removed = RemoveTabLocked(page.Id);
            if (removed)
            {
                SyncSurfaceToPages();
                PagesChanged?.Invoke();
            }
            return Task.CompletedTask;
        }
        return session.CloseTabAsync(page.TabId, forceClose: true, token);
    }

    /// <summary>
    /// Adds an in-memory tab without starting CEF, for headless UI tests covering tab strip,
    /// context menu, drag reorder, mute state, and full-view layout transitions.
    /// </summary>
    internal ChromeBrowserPage AddTestTab(
        string id,
        Uri address,
        string title,
        bool task = false,
        bool muted = false,
        string? opener = null)
    {
        ChromeBrowserPage page;
        lock (_pagesGate)
        {
            var tabNum = int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : _pages.Count + 1;
            var driver = new ChromeDevTools((_, _, _) => Task.FromResult(JsonDocument.Parse("{}").RootElement.Clone()));
            page = new ChromeBrowserPage(id, tabNum, task, opener, driver, session: null);
            if (IsPageUri(address) || address == Blank)
                page.UpdateAddress(address);
            if (!string.IsNullOrEmpty(title))
                page.UpdateTitle(title);
            page.UpdateMuted(muted);

            _pages[id] = page;
            var openerIndex = opener is not null ? _tabOrder.IndexOf(opener) : -1;
            if (openerIndex >= 0) _tabOrder.Insert(openerIndex + 1, id);
            else _tabOrder.Add(id);

            var activeInGroup = task ? _taskPage : _browsingPage;
            if (opener == null || activeInGroup?.Id == opener)
            {
                if (task) _taskPage = page;
                else _browsingPage = page;
            }
            if (opener == null || _activePage?.Id == opener || _activePage is null)
                _activePage = page;
        }
        PagesChanged?.Invoke();
        return page;
    }

    private bool RemoveTabLocked(string id)
    {
        var closedIndex = _tabOrder.IndexOf(id);
        _tabOrder.Remove(id);
        if (!_pages.Remove(id, out var closed))
            return false;

        var isTask = closed.IsTask;
        var activeInGroup = isTask ? _taskPage : _browsingPage;
        if (activeInGroup?.Id == id)
        {
            var fallback = closed.Opener is { } opener && _pages.TryGetValue(opener, out var parent) && parent.IsTask == isTask
                ? parent
                : _tabOrder.Select(tid => _pages.TryGetValue(tid, out var p) ? p : null)
                    .LastOrDefault(candidate => candidate is not null && candidate.IsTask == isTask);
            if (isTask) _taskPage = fallback;
            else _browsingPage = fallback;
        }
        if (_activePage?.Id == id)
        {
            _activePage = closed.Opener is { } opener && _pages.TryGetValue(opener, out var parent)
                ? parent
                : _tabOrder.Count > 0 && closedIndex >= 0
                    ? _pages[_tabOrder[Math.Min(closedIndex, _tabOrder.Count - 1)]]
                    : _tabOrder.Count > 0 ? _pages[_tabOrder[^1]] : null;
        }
        if (_displayedPage?.Id == id) _displayedPage = null;
        return true;
    }

    internal async Task<IReadOnlyList<BrowserHistoryEntry>> SearchHistoryAsync(string query, CancellationToken token)
    {
        await DrainHistoryWritesAsync(token).ConfigureAwait(false);
        return await Task.Run(() => _history.Search(query), token).ConfigureAwait(false);
    }

    internal async Task ClearHistoryAsync(CancellationToken token)
    {
        Task clearTask;
        lock (_historyQueueGate)
        {
            var previous = _pendingHistoryOp;
            clearTask = ClearHistoryAfterAsync(previous, token);
            _pendingHistoryOp = clearTask;
        }
        await clearTask.ConfigureAwait(false);
        HistoryChanged?.Invoke();
    }

    private void EnqueueHistoryRecord(Uri url, string title, bool isVisit)
    {
        lock (_historyQueueGate)
        {
            var previous = _pendingHistoryOp;
            _pendingHistoryOp = RecordHistoryAfterAsync(previous, url, title, isVisit);
        }
    }

    private async Task RecordHistoryAfterAsync(Task previous, Uri url, string title, bool isVisit)
    {
        try { await previous.ConfigureAwait(false); }
        catch { /* Prior queued history write or cancelled clear already reported its own result; keep FIFO queue draining. */ }

        await Task.Run(() =>
        {
            try
            {
                _history.Record(url, title, isVisit);
                HistoryChanged?.Invoke();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TypeInitializationException
                or Microsoft.Data.Sqlite.SqliteException)
            {
                Log.Warning("Could not save embedded browser history: {ErrorType}", ex.GetType().Name);
            }
        }).ConfigureAwait(false);
    }

    private async Task ClearHistoryAfterAsync(Task previous, CancellationToken token)
    {
        try { await previous.ConfigureAwait(false); }
        catch { /* Prior queued history write already handled its own failure; proceed with ClearHistoryAsync. */ }

        token.ThrowIfCancellationRequested();
        await Task.Run(_history.Clear, token).ConfigureAwait(false);
    }

    private Task DrainHistoryWritesAsync(CancellationToken token)
    {
        Task pending;
        lock (_historyQueueGate) pending = _pendingHistoryOp;
        return pending.WaitAsync(token);
    }

    internal async Task ClearSiteDataAsync(CancellationToken token)
    {
        await _lifetime.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await EnsureHostAsync(token).ConfigureAwait(false);
            var origins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var page in AllTabs())
            {
                if (IsPageUri(page.Address))
                    origins.Add(page.Address.GetLeftPart(UriPartial.Authority));
            }
            foreach (var entry in await SearchHistoryAsync("", token).ConfigureAwait(false))
            {
                if (Uri.TryCreate(entry.Url, UriKind.Absolute, out var uri) && IsPageUri(uri))
                    origins.Add(uri.GetLeftPart(UriPartial.Authority));
            }

            ChromeBrowserPage? helper = AllTabs().FirstOrDefault(p => !p.IsPlaceholder);
            var createdHelper = false;
            if (helper is null)
            {
                var tabId = await _session!.CreateTabAsync(0, 800, 720, 1.0, "browse", Blank.AbsoluteUri, false, token).ConfigureAwait(false);
                lock (_pagesGate) _pages.TryGetValue(tabId.ToString(CultureInfo.InvariantCulture), out helper);
                createdHelper = helper is not null;
            }

            if (helper is not null)
            {
                async Task TryClearAsync(string method, object parameters)
                {
                    try { await helper.Driver.CallAsync(method, parameters, token).ConfigureAwait(false); }
                    catch (ChromeDevToolsException ex)
                    {
                        Log.Debug("CDP {Method} completed with notice: {Error}", method, ex.Message);
                    }
                }

                await TryClearAsync("Network.clearBrowserCookies", new { }).ConfigureAwait(false);
                await TryClearAsync("Network.clearBrowserCache", new { }).ConfigureAwait(false);
                foreach (var origin in origins)
                    await TryClearAsync("Storage.clearDataForOrigin", new { origin, storageTypes = "all" }).ConfigureAwait(false);
                await TryClearAsync("Storage.clearDataForOrigin", new { origin = "*", storageTypes = "all" }).ConfigureAwait(false);
            }

            foreach (var page in AllTabs())
                await CloseTabAsync(page, token).ConfigureAwait(false);
            if (createdHelper && helper is not null && _pages.ContainsKey(helper.Id))
                await CloseTabAsync(helper, token).ConfigureAwait(false);

            await _session!.ClearSiteDataAsync(token).ConfigureAwait(false);
        }
        finally { _lifetime.Release(); }
    }

    private async Task EnsureHostAsync(CancellationToken token)
    {
        if (_session?.IsOpen == true) return;
        if (_session is not null || _profileLease is not null)
            await CloseHostAsync(preservePlaceholders: true).ConfigureAwait(false);
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

            var paths = await _runtime.PrepareAsync(token).ConfigureAwait(false);
            var profileDir = Path.Combine(_directory, "Profile");
            _session = await CefNativeBridge.AcquireAsync(paths, profileDir, OnCefEvent, token).ConfigureAwait(false);
            RuntimeError = null;
        }
        catch (Exception ex)
        {
            await CloseHostAsync(preservePlaceholders: true).ConfigureAwait(false);
            RuntimeError = ex is OperationCanceledException && !token.IsCancellationRequested
                ? "The bundled browser did not start in time." : ex.Message;
            if (ex is OperationCanceledException && !token.IsCancellationRequested)
                throw new InvalidOperationException(RuntimeError, ex);
            throw;
        }
        finally { RuntimeChanged?.Invoke(); }
    }

    private void OnCefEvent(CefBridgeEvent evt)
    {
        if (evt.Kind == CefBridgeEventKind.Disconnected)
        {
            ResetPages();
            RuntimeChanged?.Invoke();
            return;
        }

        var id = evt.TabId.ToString(CultureInfo.InvariantCulture);

        if (evt.Kind == CefBridgeEventKind.UserInteraction)
        {
            ChromeBrowserPage? page;
            lock (_pagesGate) _pages.TryGetValue(id, out page);
            if (page is not null) UserInteracted?.Invoke(page);
            return;
        }

        if (evt.Kind == CefBridgeEventKind.Interaction)
        {
            bool isTask;
            lock (_pagesGate) isTask = _pages.TryGetValue(id, out var p) && p.IsTask;
            var payload = ParseJsonOrEmpty(evt.PayloadJson);
            var interaction = new BrowserInteraction(_session!, evt.InteractionId, id, isTask, evt.InteractionKind, payload);
            if (InteractionRequested is { } handler) handler(interaction);
            else _ = interaction.RespondAsync(new { }, CancellationToken.None);
            return;
        }

        if (evt.Kind == CefBridgeEventKind.DownloadCompleted)
        {
            bool isTask;
            lock (_pagesGate) isTask = _pages.TryGetValue(id, out var p) && p.IsTask;
            DownloadCompleted?.Invoke(evt.FileName, evt.DownloadOk, isTask);
            return;
        }

        Uri? visited = null;
        string? title = null;
        var isVisit = false;
        var syncSurface = false;

        lock (_pagesGate)
        {
            if (evt.Kind == CefBridgeEventKind.TabOpened)
            {
                var isTask = string.Equals(evt.Group, "task", StringComparison.Ordinal);
                var opener = evt.OpenerId != 0 ? evt.OpenerId.ToString(CultureInfo.InvariantCulture) : null;
                var page = new ChromeBrowserPage(id, evt.TabId, isTask, opener, new ChromeDevTools(_session!, evt.TabId), _session!);
                page.UpdateMuted(evt.Muted);
                if (Uri.TryCreate(evt.Url, UriKind.Absolute, out var initialUri) && IsPageUri(initialUri))
                    page.UpdateAddress(initialUri);
                if (!string.IsNullOrEmpty(evt.Title))
                    page.UpdateTitle(evt.Title);

                _pages[id] = page;
                var openerIndex = opener is not null ? _tabOrder.IndexOf(opener) : -1;
                if (openerIndex >= 0) _tabOrder.Insert(openerIndex + 1, id);
                else _tabOrder.Add(id);

                var activeInGroup = isTask ? _taskPage : _browsingPage;
                if (opener == null || activeInGroup?.Id == opener)
                {
                    if (isTask) _taskPage = page;
                    else _browsingPage = page;
                }
                if (opener == null || _activePage?.Id == opener || _activePage is null)
                    _activePage = page;
                syncSurface = true;
            }
            else if (evt.Kind == CefBridgeEventKind.TabClosed)
            {
                if (RemoveTabLocked(id))
                    syncSurface = true;
            }
            else if (evt.Kind == CefBridgeEventKind.MutedChanged)
            {
                if (_pages.TryGetValue(id, out var page))
                    page.UpdateMuted(evt.Muted);
            }
            else if (evt.Kind == CefBridgeEventKind.Navigation && _pages.TryGetValue(id, out var page))
            {
                page.UpdateNavigationState(evt.CanGoBack, evt.CanGoForward);
                if (Uri.TryCreate(evt.Url, UriKind.Absolute, out var address) && IsPageUri(address))
                {
                    page.UpdateAddress(address);
                    title = evt.Title;
                    page.UpdateTitle(title);
                    if (evt.NavigationKind is "visit" or "title")
                    {
                        visited = address;
                        isVisit = evt.NavigationKind == "visit";
                    }
                }
            }
        }

        if (syncSurface)
            SyncSurfaceToPages();

        if (visited != null)
            EnqueueHistoryRecord(visited, title!, isVisit);
        PagesChanged?.Invoke();
    }

    private static JsonElement ParseJsonOrEmpty(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(raw) ? "{}" : raw);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            using var doc = JsonDocument.Parse("{}");
            return doc.RootElement.Clone();
        }
    }

    private void ResetPages()
    {
        lock (_pagesGate)
        {
            _pages.Clear();
            _tabOrder.Clear();
            _browsingPage = null;
            _taskPage = null;
            _activePage = null;
            _displayedPage = null;
        }
        PagesChanged?.Invoke();
    }

    private async Task CloseHostAsync(bool preservePlaceholders = false)
    {
        try
        {
            if (_session is { IsOpen: true } session)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                foreach (var page in AllTabs())
                {
                    if (page.IsPlaceholder) continue;
                    try { await session.CloseTabAsync(page.TabId, forceClose: true, cleanup.Token).ConfigureAwait(false); }
                    catch (Exception ex) { Log.Debug("Closing browser tab during shutdown: {Error}", ex.Message); }
                }
                await session.DisposeAsync().ConfigureAwait(false);
            }
            else if (_session is not null)
            {
                await _session.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _session = null;
            _profileLease?.Dispose();
            _profileLease = null;
            if (!preservePlaceholders)
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
            try
            {
                using var flush = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await DrainHistoryWritesAsync(flush.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Best-effort history flush during disposal; do not hang application shutdown.
            }
        }
        finally { _lifetime.Release(); }
    }
}

internal sealed class BrowserInteraction(
    CefNativeSession session,
    int interactionId,
    string tab,
    bool inTask,
    string kind,
    JsonElement request)
{
    public string Tab => tab;
    public bool InTask => inTask;
    public JsonElement Request => request;
    public string Kind => kind;

    public string Text(string key) => request.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
        ? value.GetString() ?? "" : "";

    public Task RespondAsync(object result, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var json = JsonSerializer.SerializeToElement(result);
        var accept = false;
        string? text1 = null;
        string? text2 = null;

        if (kind == "file")
        {
            if (json.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
            {
                var list = files.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString())
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .ToArray();
                if (list.Length > 0)
                {
                    accept = true;
                    text1 = string.Join('\x1f', list);
                }
            }
        }
        else if (kind == "download")
        {
            if (json.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(path.GetString()))
            {
                accept = true;
                text1 = path.GetString();
            }
        }
        else if (kind == "dialog")
        {
            accept = json.TryGetProperty("accept", out var a) && a.ValueKind == JsonValueKind.True;
            text1 = json.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : "";
        }
        else if (kind == "authentication")
        {
            accept = json.TryGetProperty("accept", out var a) && a.ValueKind == JsonValueKind.True;
            text1 = json.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : "";
            text2 = json.TryGetProperty("password", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : "";
        }
        else if (kind == "permission")
        {
            accept = json.TryGetProperty("accept", out var a) && a.ValueKind == JsonValueKind.True;
        }

        session.ReplyInteraction(interactionId, accept, text1, text2);
        return Task.CompletedTask;
    }
}

internal sealed record BrowserViewportSize(int Width, int Height, double Scale = 1)
{
    public int PixelWidth => Math.Max(1, (int)Math.Round(Width * Scale, MidpointRounding.AwayFromZero));
    public int PixelHeight => Math.Max(1, (int)Math.Round(Height * Scale, MidpointRounding.AwayFromZero));
}

[SuppressMessage("Design", "CA1001", Justification = "No wait handle is created and a viewport lease may complete after the tab closes.")]
internal sealed class ChromeBrowserPage
{
    private readonly SemaphoreSlim _viewportGate = new(1, 1);
    private readonly CefNativeSession? _session;
    private Uri _address = EmbeddedBrowser.Blank;
    private string _title = "";
    private int _isMuted;
    private int _canGoBack;
    private int _canGoForward;
    private ComputerUseCursor? _cursor;

    public ChromeBrowserPage(string id, int tabId, bool task, string? opener, ChromeDevTools driver,
        CefNativeSession? session = null, bool isPlaceholder = false)
    {
        Id = id;
        TabId = tabId;
        IsTask = task;
        IsPlaceholder = isPlaceholder;
        Opener = opener;
        Driver = driver;
        _session = session;
    }

    public string Id { get; }
    public int TabId { get; }
    public bool IsTask { get; }
    public bool IsPlaceholder { get; }
    public string? Opener { get; }
    public ChromeDevTools Driver { get; }
    public Uri Address => Volatile.Read(ref _address);
    public string Title => Volatile.Read(ref _title);
    public bool IsMuted => Volatile.Read(ref _isMuted) != 0;
    public bool CanGoBack => Volatile.Read(ref _canGoBack) != 0;
    public bool CanGoForward => Volatile.Read(ref _canGoForward) != 0;
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
    public void UpdateMuted(bool muted) => Volatile.Write(ref _isMuted, muted ? 1 : 0);
    public void UpdateNavigationState(bool canGoBack, bool canGoForward)
    {
        Volatile.Write(ref _canGoBack, canGoBack ? 1 : 0);
        Volatile.Write(ref _canGoForward, canGoForward ? 1 : 0);
    }

    internal void UpdateNativeViewportSize(BrowserViewportSize size)
    {
        if (ViewportSize == size) return;
        Cursor = null;
        ViewportSize = size;
        ViewportRevision++;
    }

    public void SetAgentInputActive(bool active) => _session?.SetAgentInputActive(TabId, active);

    public Task SetMutedAsync(bool muted, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_session is { IsOpen: true })
            _session.SetMuted(TabId, muted);
        UpdateMuted(muted);
        return Task.CompletedTask;
    }

    public async Task NavigateAsync(Uri target, CancellationToken token)
    {
        var result = await Driver.CallAsync("Page.navigate", new { url = target.AbsoluteUri }, token).ConfigureAwait(false);
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

    private async Task ApplyViewportAsync(BrowserViewportSize size, CancellationToken token)
    {
        _session?.SetTabBounds(TabId, size.Width, size.Height, size.Scale, visible: !CefNativeBridge.IsHeadlessRuntime);
        if (CefNativeBridge.IsHeadlessRuntime)
        {
            await Driver.CallAsync("Emulation.setDeviceMetricsOverride", new
            {
                width = size.Width,
                height = size.Height,
                deviceScaleFactor = size.Scale,
                mobile = false
            }, token).ConfigureAwait(false);
        }
        Cursor = null;
        ViewportSize = size;
        ViewportRevision++;
    }

    public async ValueTask<ViewportLease> LockViewportAsync(CancellationToken token)
    {
        await _viewportGate.WaitAsync(token).ConfigureAwait(false);
        return new ViewportLease(_viewportGate);
    }

    public async Task GoAsync(int offset, CancellationToken token)
    {
        var history = await Driver.CallAsync("Page.getNavigationHistory", new { }, token).ConfigureAwait(false);
        var entries = history.GetProperty("entries");
        var index = history.GetProperty("currentIndex").GetInt32() + offset;
        if (index >= 0 && index < entries.GetArrayLength())
        {
            var entryId = entries[index].GetProperty("id").GetInt32();
            await Driver.CallAsync("Page.navigateToHistoryEntry", new { entryId }, token).ConfigureAwait(false);
        }
    }

    public Task ReloadAsync(CancellationToken token) =>
        Driver.CallAsync("Page.reload", new { }, token);

    internal readonly struct ViewportLease(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }
}
