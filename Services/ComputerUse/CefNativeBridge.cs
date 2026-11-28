using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace Athena.UI.Services.ComputerUse;

internal enum CefBridgeEventKind
{
    TabOpened,
    TabClosed,
    Navigation,
    MutedChanged,
    Interaction,
    DownloadCompleted,
    UserInteraction,
    Disconnected
}

internal sealed record CefBridgeEvent(
    CefBridgeEventKind Kind,
    int TabId,
    int RequestId = 0,
    int OpenerId = 0,
    string Group = "",
    string NavigationKind = "",
    string Url = "",
    string Title = "",
    bool CanGoBack = false,
    bool CanGoForward = false,
    bool Muted = false,
    int InteractionId = 0,
    string InteractionKind = "",
    string PayloadJson = "",
    string FileName = "",
    bool DownloadOk = false);

/// <summary>
/// Process-wide native P/Invoke wrapper for <c>libathena_cef_bridge</c>.
/// CEF strictly allows one initialization per OS process; individual <see cref="EmbeddedBrowser"/>
/// instances acquire a lightweight session and release their tabs on disposal while CEF shuts down
/// cleanly on process exit via <see cref="ShutdownProcessAsync"/>.
/// </summary>
internal static class CefNativeBridge
{
    [StructLayout(LayoutKind.Sequential)]
    private struct AthenaCefConfig
    {
        public int StructSize;
        public nint RuntimeDir;
        public nint FrameworkDir;
        public nint MainBundleDir;
        public nint SubprocessPath;
        public nint CachePath;
        public nint RootCachePath;
        public nint ResourcesDir;
        public nint LocalesDir;
        public nint LogFile;
        public int HeadlessMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AthenaCefCallbacks
    {
        public int StructSize;
        public nint UserData;
        public nint OnContextInitialized;
        public nint OnTabOpened;
        public nint OnTabClosed;
        public nint OnNavigation;
        public nint OnMutedChanged;
        public nint OnDevToolsResult;
        public nint OnDevToolsEvent;
        public nint OnInteraction;
        public nint OnDownloadCompleted;
        public nint OnUserInteraction;
        public nint OnSiteDataCleared;
        public nint OnSchedulePumpWork;
    }

    // Native exports
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int PreloadLibraryFn(nint frameworkDir);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int InitializeFn(ref AthenaCefConfig config, ref AthenaCefCallbacks callbacks, nint errorBuf, int errorLen);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ShutdownFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DoMessageLoopWorkFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CreateTabFn(int requestId, long parentHandle, int width, int height, double scale, nint group, nint initialUrl, int visible);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetTabParentFn(int tabId, long parentHandle, int width, int height, double scale, int visible);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetTabBoundsFn(int tabId, int width, int height, double scale, int visible);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetTabFocusFn(int tabId, int focused);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetAgentInputActiveFn(int tabId, int active);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void CloseTabFn(int tabId, int forceClose);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void NavigateFn(int tabId, nint url);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void TabActionFn(int tabId);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SetMutedFn(int tabId, int muted);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SendDevToolsFn(int tabId, int messageId, nint jsonUtf8, int jsonLen);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ReplyInteractionFn(int interactionId, int accept, nint text1, nint text2);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ClearSiteDataFn(int requestId);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate long CreateHostViewFn(long parentHandle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DestroyHostViewFn(long viewHandle);

    // Native callbacks (held in static fields so GC never collects their thunks)
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ContextInitializedCb(nint userData);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void TabOpenedCb(nint userData, int requestId, int tabId, int openerId, nint group, nint url, nint title, int muted);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void TabClosedCb(nint userData, int tabId);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void NavigationCb(nint userData, int tabId, nint kind, nint url, nint title, int canGoBack, int canGoForward);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void MutedChangedCb(nint userData, int tabId, int muted);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DevToolsResultCb(nint userData, int tabId, int messageId, int success, nint json, int jsonLen);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DevToolsEventCb(nint userData, int tabId, nint method, nint paramsJson, int paramsLen);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void InteractionCb(nint userData, int interactionId, int tabId, nint kind, nint payloadJson);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DownloadCompletedCb(nint userData, int tabId, nint fileName, int ok);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void UserInteractionCb(nint userData, int tabId);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SiteDataClearedCb(nint userData, int requestId, int ok);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SchedulePumpWorkCb(nint userData, long delayMs);

    private static readonly ContextInitializedCb ContextInitializedDelegate = OnContextInitialized;
    private static readonly TabOpenedCb TabOpenedDelegate = OnTabOpened;
    private static readonly TabClosedCb TabClosedDelegate = OnTabClosed;
    private static readonly NavigationCb NavigationDelegate = OnNavigation;
    private static readonly MutedChangedCb MutedChangedDelegate = OnMutedChanged;
    private static readonly DevToolsResultCb DevToolsResultDelegate = OnDevToolsResult;
    private static readonly DevToolsEventCb DevToolsEventDelegate = OnDevToolsEvent;
    private static readonly InteractionCb InteractionDelegate = OnInteraction;
    private static readonly DownloadCompletedCb DownloadCompletedDelegate = OnDownloadCompleted;
    private static readonly UserInteractionCb UserInteractionDelegate = OnUserInteraction;
    private static readonly SiteDataClearedCb SiteDataClearedDelegate = OnSiteDataCleared;
    private static readonly SchedulePumpWorkCb SchedulePumpWorkDelegate = OnSchedulePumpWork;

    private static readonly object LibraryGate = new();
    private static readonly SemaphoreSlim LifecycleGate = new(1, 1);
    private static readonly ConcurrentDictionary<int, TaskCompletionSource<int>> PendingOpens = new();
    private static readonly ConcurrentDictionary<int, TaskCompletionSource> PendingCloses = new();
    private static readonly ConcurrentDictionary<int, TaskCompletionSource<bool>> PendingClearData = new();
    private static readonly ConcurrentDictionary<(int TabId, int MessageId), TaskCompletionSource<JsonElement>> PendingDevTools = new();

    private static nint _libraryHandle;
    private static PreloadLibraryFn? _preloadLibrary;
    private static InitializeFn? _initialize;
    private static ShutdownFn? _shutdown;
    private static DoMessageLoopWorkFn? _doMessageLoopWork;
    private static CreateTabFn? _createTab;
    private static SetTabParentFn? _setTabParent;
    private static SetTabBoundsFn? _setTabBounds;
    private static SetTabFocusFn? _setTabFocus;
    private static SetAgentInputActiveFn? _setAgentInputActive;
    private static CloseTabFn? _closeTab;
    private static NavigateFn? _navigate;
    private static TabActionFn? _goBack;
    private static TabActionFn? _goForward;
    private static TabActionFn? _reload;
    private static SetMutedFn? _setMuted;
    private static SendDevToolsFn? _sendDevTools;
    private static ReplyInteractionFn? _replyInteraction;
    private static ClearSiteDataFn? _clearSiteData;
    private static CreateHostViewFn? _createHostView;
    private static DestroyHostViewFn? _destroyHostView;

    private static volatile bool _initialized;
    private static volatile bool _shutDown;
    private static volatile bool _frameworkPreloaded;
    private static TaskCompletionSource _contextInitialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Action<CefBridgeEvent>? _eventHandler;
    private static int _nextRequestId;
    private static int _nextDevToolsId;
    private static Timer? _headlessPumpTimer;
    private static int _pumpPosted;

    /// <summary>Registered by App / HeadlessTests so macOS CEF init/pump/shutdown run on the OS main thread without referencing Avalonia here.</summary>
    internal static Func<Action, Task>? RunOnMainThreadAsync { get; set; }
    internal static Action<Action>? PostToMainThread { get; set; }
    internal static bool IsHeadlessRuntime { get; set; }

    internal static bool IsInitialized => _initialized && !_shutDown;
    internal static bool IsFrameworkPreloaded => _frameworkPreloaded;

    /// <summary>
    /// Preloads <c>Chromium Embedded Framework</c> on macOS before worker threads or SQLite start.
    /// Chromium's macOS PartitionAlloc static initializer temporarily unregisters the system
    /// <c>DefaultMallocZone</c> to promote PartitionAlloc to <c>zones[0]</c>; if another thread
    /// frees a pre-existing allocation during that window (e.g. <c>libsqlite3mc</c> holding its
    /// global allocator mutex), <c>TryFreeDefaultFallbackToFindZoneAndFree</c> traps on <c>brk #0</c>
    /// and deadlocks the process under CoreCLR's Mach exception handler.
    /// </summary>
    internal static bool TryPreloadFramework(string? runtimeDirectory = null)
    {
        if (!OperatingSystem.IsMacOS()) return false;
        if (_frameworkPreloaded) return true;
        if (!BrowserRuntime.TryResolvePaths(runtimeDirectory ?? BrowserRuntime.DefaultDirectory, out var paths))
            return false;
        try
        {
            EnsureLibraryLoaded(paths.BridgeLibrary);
            if (_preloadLibrary is not { } preload)
                return false;
            var fwPtr = ToUtf8(paths.FrameworkDir);
            try
            {
                if (preload(fwPtr) != 0)
                {
                    _frameworkPreloaded = true;
                    return true;
                }
                return false;
            }
            finally
            {
                if (fwPtr != 0) Marshal.FreeCoTaskMem(fwPtr);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException
            or BadImageFormatException or IOException or UnauthorizedAccessException)
        {
            // Startup preload is best-effort; if the runtime is missing or corrupt, normal browser
            // launch will surface the user-facing runtime diagnostic later.
            return false;
        }
    }

    internal static bool TryEnsureLibraryLoaded(string runtimeDirectory)
    {
        if (!BrowserRuntime.TryResolveBridgeLibrary(runtimeDirectory, out var bridgePath))
            return false;
        try
        {
            EnsureLibraryLoaded(bridgePath);
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not preload CEF bridge library from {Path}", bridgePath);
            return false;
        }
    }

    internal static nint CreateHostView(nint parentHandle, string? runtimeDirectory = null)
    {
        if (_createHostView is null && !TryEnsureLibraryLoaded(runtimeDirectory ?? BrowserRuntime.DefaultDirectory))
            return 0;
        return _createHostView is { } fn ? (nint)fn((long)parentHandle) : 0;
    }

    internal static void DestroyHostView(nint viewHandle)
    {
        if (viewHandle == 0 || _destroyHostView is null) return;
        _destroyHostView((long)viewHandle);
    }

    internal static async Task<CefNativeSession> AcquireAsync(
        CefRuntimePaths paths,
        string profileDirectory,
        Action<CefBridgeEvent> onEvent,
        CancellationToken token)
    {
        await LifecycleGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_shutDown)
                throw new InvalidOperationException("The CEF browser runtime has already shut down for this process.");

            EnsureLibraryLoaded(paths.BridgeLibrary);
            Volatile.Write(ref _eventHandler, onEvent);

            if (!_initialized)
            {
                Directory.CreateDirectory(profileDirectory);
                _contextInitialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
                var logFile = Path.Combine(profileDirectory, "cef.log");
                var headless = IsHeadlessRuntime;

                string? initError = null;
                void InitCore()
                {
                    var allocs = new nint[9];
                    try
                    {
                        var config = new AthenaCefConfig
                        {
                            StructSize = Marshal.SizeOf<AthenaCefConfig>(),
                            RuntimeDir = allocs[0] = ToUtf8(paths.RuntimeDir),
                            FrameworkDir = allocs[1] = ToUtf8(paths.FrameworkDir),
                            MainBundleDir = allocs[2] = ToUtf8(paths.MainBundleDir),
                            SubprocessPath = allocs[3] = ToUtf8(paths.SubprocessPath),
                            CachePath = allocs[4] = ToUtf8(profileDirectory),
                            RootCachePath = allocs[5] = ToUtf8(profileDirectory),
                            ResourcesDir = allocs[6] = ToUtf8(paths.ResourcesDir),
                            LocalesDir = allocs[7] = ToUtf8(paths.LocalesDir),
                            LogFile = allocs[8] = ToUtf8(logFile),
                            HeadlessMode = headless ? 1 : 0
                        };
                        var callbacks = CreateCallbacks();
                        var errorBuf = Marshal.AllocHGlobal(1024);
                        try
                        {
                            Marshal.WriteByte(errorBuf, 0);
                            var ok = _initialize!(ref config, ref callbacks, errorBuf, 1024);
                            if (ok == 0)
                                initError = Marshal.PtrToStringUTF8(errorBuf) ?? "Failed to initialize CEF.";
                        }
                        finally
                        {
                            Marshal.FreeHGlobal(errorBuf);
                        }
                    }
                    finally
                    {
                        foreach (var ptr in allocs)
                            if (ptr != 0) Marshal.FreeCoTaskMem(ptr);
                    }
                }

                if (OperatingSystem.IsMacOS() && RunOnMainThreadAsync is { } runOnMain)
                    await runOnMain(InitCore).ConfigureAwait(false);
                else
                    InitCore();

                if (initError is not null)
                    throw new InvalidOperationException(initError);

                _initialized = true;
                if (OperatingSystem.IsMacOS() && headless)
                    StartHeadlessPump();

                using var startup = CancellationTokenSource.CreateLinkedTokenSource(token);
                startup.CancelAfter(TimeSpan.FromSeconds(15));
                await _contextInitialized.Task.WaitAsync(startup.Token).ConfigureAwait(false);
            }

            return new CefNativeSession(onEvent);
        }
        finally
        {
            LifecycleGate.Release();
        }
    }

    internal static async Task ShutdownProcessAsync()
    {
        await LifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_initialized || _shutDown) return;
            _shutDown = true;
            _initialized = false;
            StopHeadlessPump();
            Volatile.Write(ref _eventHandler, null);
            FailAllPending("The CEF browser runtime shut down.");

            void ShutdownCore() => _shutdown?.Invoke();
            if (OperatingSystem.IsMacOS() && RunOnMainThreadAsync is { } runOnMain)
                await runOnMain(ShutdownCore).ConfigureAwait(false);
            else
                ShutdownCore();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "CEF shutdown encountered an error");
        }
        finally
        {
            LifecycleGate.Release();
        }
    }

    internal static void DetachHandler(Action<CefBridgeEvent> handler)
    {
        Interlocked.CompareExchange(ref _eventHandler, null, handler);
    }

    internal static async Task<int> CreateTabAsync(
        nint parentHandle,
        int width,
        int height,
        double scale,
        string group,
        string initialUrl,
        bool visible,
        CancellationToken token)
    {
        EnsureActive();
        var requestId = Interlocked.Increment(ref _nextRequestId);
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        PendingOpens[requestId] = tcs;
        try
        {
            var groupPtr = ToUtf8(group);
            var urlPtr = ToUtf8(initialUrl);
            try
            {
                if (_createTab!(requestId, (long)parentHandle, Math.Max(1, width), Math.Max(1, height),
                        scale > 0 ? scale : 1.0, groupPtr, urlPtr, visible ? 1 : 0) == 0)
                    throw new ChromeDevToolsException("CEF could not create a new browser tab.");
            }
            finally
            {
                if (groupPtr != 0) Marshal.FreeCoTaskMem(groupPtr);
                if (urlPtr != 0) Marshal.FreeCoTaskMem(urlPtr);
            }
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(15));
            return await tcs.Task.WaitAsync(limit.Token).ConfigureAwait(false);
        }
        finally
        {
            PendingOpens.TryRemove(requestId, out _);
        }
    }

    internal static void SetTabParent(int tabId, nint parentHandle, int width, int height, double scale, bool visible)
    {
        if (!IsInitialized) return;
        _setTabParent?.Invoke(tabId, (long)parentHandle, Math.Max(1, width), Math.Max(1, height), scale > 0 ? scale : 1.0, visible ? 1 : 0);
    }

    internal static void SetTabBounds(int tabId, int width, int height, double scale, bool visible)
    {
        if (!IsInitialized) return;
        _setTabBounds?.Invoke(tabId, Math.Max(1, width), Math.Max(1, height), scale > 0 ? scale : 1.0, visible ? 1 : 0);
    }

    internal static void SetTabFocus(int tabId, bool focused)
    {
        if (!IsInitialized) return;
        _setTabFocus?.Invoke(tabId, focused ? 1 : 0);
    }

    internal static void SetAgentInputActive(int tabId, bool active)
    {
        if (!IsInitialized) return;
        _setAgentInputActive?.Invoke(tabId, active ? 1 : 0);
    }

    internal static async Task CloseTabAsync(int tabId, bool forceClose, CancellationToken token)
    {
        if (!IsInitialized) return;
        var tcs = PendingCloses.GetOrAdd(tabId, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
        _closeTab?.Invoke(tabId, forceClose ? 1 : 0);
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(5));
            await tcs.Task.WaitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            Log.Debug("Timed out waiting for CEF tab {TabId} close confirmation", tabId);
        }
        finally
        {
            PendingCloses.TryRemove(tabId, out _);
        }
    }

    internal static void Navigate(int tabId, string url)
    {
        if (!IsInitialized) return;
        var ptr = ToUtf8(url);
        try { _navigate?.Invoke(tabId, ptr); }
        finally { if (ptr != 0) Marshal.FreeCoTaskMem(ptr); }
    }

    internal static void GoBack(int tabId) { if (IsInitialized) _goBack?.Invoke(tabId); }
    internal static void GoForward(int tabId) { if (IsInitialized) _goForward?.Invoke(tabId); }
    internal static void Reload(int tabId) { if (IsInitialized) _reload?.Invoke(tabId); }

    internal static bool SetMuted(int tabId, bool muted)
    {
        if (!IsInitialized || _setMuted is null) return false;
        return _setMuted(tabId, muted ? 1 : 0) != 0;
    }

    internal static async Task<JsonElement> SendDevToolsAsync(int tabId, string method, object parameters, CancellationToken token)
    {
        EnsureActive();
        var messageId = Interlocked.Increment(ref _nextDevToolsId);
        var key = (tabId, messageId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        PendingDevTools[key] = tcs;
        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(new { id = messageId, method, @params = parameters });
            var buf = Marshal.AllocHGlobal(payload.Length);
            try
            {
                Marshal.Copy(payload, 0, buf, payload.Length);
                if (_sendDevTools!(tabId, messageId, buf, payload.Length) == 0)
                    throw new ChromeDevToolsException("Not attached to an active page.");
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
            return await tcs.Task.WaitAsync(token).ConfigureAwait(false);
        }
        finally
        {
            PendingDevTools.TryRemove(key, out _);
        }
    }

    internal static void ReplyInteraction(int interactionId, bool accept, string? text1 = null, string? text2 = null)
    {
        if (!IsInitialized || _replyInteraction is null) return;
        var p1 = ToUtf8(text1);
        var p2 = ToUtf8(text2);
        try { _replyInteraction(interactionId, accept ? 1 : 0, p1, p2); }
        finally
        {
            if (p1 != 0) Marshal.FreeCoTaskMem(p1);
            if (p2 != 0) Marshal.FreeCoTaskMem(p2);
        }
    }

    internal static async Task ClearSiteDataAsync(CancellationToken token)
    {
        EnsureActive();
        var requestId = Interlocked.Increment(ref _nextRequestId);
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        PendingClearData[requestId] = tcs;
        try
        {
            _clearSiteData!(requestId);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(10));
            await tcs.Task.WaitAsync(limit.Token).ConfigureAwait(false);
        }
        finally
        {
            PendingClearData.TryRemove(requestId, out _);
        }
    }

    private static void EnsureActive()
    {
        if (!IsInitialized)
            throw new ChromeDevToolsException("The browser runtime disconnected.");
    }

    private static void EnsureLibraryLoaded(string bridgePath)
    {
        if (_libraryHandle != 0) return;
        lock (LibraryGate)
        {
            if (_libraryHandle != 0) return;
            var handle = NativeLibrary.Load(bridgePath);
            if (NativeLibrary.TryGetExport(handle, "athena_cef_preload_library", out var preloadAddr))
                _preloadLibrary = Marshal.GetDelegateForFunctionPointer<PreloadLibraryFn>(preloadAddr);
            _initialize = Bind<InitializeFn>(handle, "athena_cef_initialize");
            _shutdown = Bind<ShutdownFn>(handle, "athena_cef_shutdown");
            _doMessageLoopWork = Bind<DoMessageLoopWorkFn>(handle, "athena_cef_do_message_loop_work");
            _createTab = Bind<CreateTabFn>(handle, "athena_cef_create_tab");
            _setTabParent = Bind<SetTabParentFn>(handle, "athena_cef_set_tab_parent");
            _setTabBounds = Bind<SetTabBoundsFn>(handle, "athena_cef_set_tab_bounds");
            _setTabFocus = Bind<SetTabFocusFn>(handle, "athena_cef_set_tab_focus");
            _setAgentInputActive = Bind<SetAgentInputActiveFn>(handle, "athena_cef_set_agent_input_active");
            _closeTab = Bind<CloseTabFn>(handle, "athena_cef_close_tab");
            _navigate = Bind<NavigateFn>(handle, "athena_cef_navigate");
            _goBack = Bind<TabActionFn>(handle, "athena_cef_go_back");
            _goForward = Bind<TabActionFn>(handle, "athena_cef_go_forward");
            _reload = Bind<TabActionFn>(handle, "athena_cef_reload");
            _setMuted = Bind<SetMutedFn>(handle, "athena_cef_set_muted");
            _sendDevTools = Bind<SendDevToolsFn>(handle, "athena_cef_send_devtools");
            _replyInteraction = Bind<ReplyInteractionFn>(handle, "athena_cef_reply_interaction");
            _clearSiteData = Bind<ClearSiteDataFn>(handle, "athena_cef_clear_site_data");
            _createHostView = Bind<CreateHostViewFn>(handle, "athena_cef_create_host_view");
            _destroyHostView = Bind<DestroyHostViewFn>(handle, "athena_cef_destroy_host_view");
            _libraryHandle = handle;
        }
    }

    private static T Bind<T>(nint handle, string export) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(handle, export));

    private static AthenaCefCallbacks CreateCallbacks() => new()
    {
        StructSize = Marshal.SizeOf<AthenaCefCallbacks>(),
        UserData = 0,
        OnContextInitialized = Marshal.GetFunctionPointerForDelegate(ContextInitializedDelegate),
        OnTabOpened = Marshal.GetFunctionPointerForDelegate(TabOpenedDelegate),
        OnTabClosed = Marshal.GetFunctionPointerForDelegate(TabClosedDelegate),
        OnNavigation = Marshal.GetFunctionPointerForDelegate(NavigationDelegate),
        OnMutedChanged = Marshal.GetFunctionPointerForDelegate(MutedChangedDelegate),
        OnDevToolsResult = Marshal.GetFunctionPointerForDelegate(DevToolsResultDelegate),
        OnDevToolsEvent = Marshal.GetFunctionPointerForDelegate(DevToolsEventDelegate),
        OnInteraction = Marshal.GetFunctionPointerForDelegate(InteractionDelegate),
        OnDownloadCompleted = Marshal.GetFunctionPointerForDelegate(DownloadCompletedDelegate),
        OnUserInteraction = Marshal.GetFunctionPointerForDelegate(UserInteractionDelegate),
        OnSiteDataCleared = Marshal.GetFunctionPointerForDelegate(SiteDataClearedDelegate),
        OnSchedulePumpWork = Marshal.GetFunctionPointerForDelegate(SchedulePumpWorkDelegate)
    };

    private static void StartHeadlessPump()
    {
        _headlessPumpTimer?.Dispose();
        _headlessPumpTimer = new Timer(_ => RequestMainThreadPump(), null, 0, 15);
    }

    private static void StopHeadlessPump()
    {
        _headlessPumpTimer?.Dispose();
        _headlessPumpTimer = null;
    }

    private static void RequestMainThreadPump()
    {
        if (!IsInitialized) return;
        if (Interlocked.Exchange(ref _pumpPosted, 1) != 0) return;
        if (PostToMainThread is { } post)
        {
            post(() =>
            {
                Interlocked.Exchange(ref _pumpPosted, 0);
                if (IsInitialized) _doMessageLoopWork?.Invoke();
            });
        }
        else
        {
            Interlocked.Exchange(ref _pumpPosted, 0);
        }
    }

    private static void FailAllPending(string message)
    {
        var ex = new ChromeDevToolsException(message);
        foreach (var kv in PendingOpens)
            if (PendingOpens.TryRemove(kv.Key, out var tcs)) tcs.TrySetException(ex);
        foreach (var kv in PendingCloses)
            if (PendingCloses.TryRemove(kv.Key, out var tcs)) tcs.TrySetResult();
        foreach (var kv in PendingClearData)
            if (PendingClearData.TryRemove(kv.Key, out var tcs)) tcs.TrySetException(ex);
        foreach (var kv in PendingDevTools)
            if (PendingDevTools.TryRemove(kv.Key, out var tcs)) tcs.TrySetException(ex);
    }

    private static void FailTabDevTools(int tabId)
    {
        var ex = new ChromeDevToolsException("target closed while handling command");
        foreach (var kv in PendingDevTools)
        {
            if (kv.Key.TabId == tabId && PendingDevTools.TryRemove(kv.Key, out var tcs))
                tcs.TrySetException(ex);
        }
    }

    private static nint ToUtf8(string? value) =>
        string.IsNullOrEmpty(value) ? 0 : Marshal.StringToCoTaskMemUTF8(value);

    private static string FromUtf8(nint ptr) =>
        ptr == 0 ? "" : Marshal.PtrToStringUTF8(ptr) ?? "";

    private static string FromUtf8(nint ptr, int len) =>
        ptr == 0 || len <= 0 ? "" : Marshal.PtrToStringUTF8(ptr, len) ?? "";

    private static void DispatchEvent(CefBridgeEvent evt)
    {
        try { Volatile.Read(ref _eventHandler)?.Invoke(evt); }
        catch (Exception ex) { Log.Warning(ex, "Unhandled error in CEF event handler ({Kind})", evt.Kind); }
    }

    private static void OnContextInitialized(nint userData)
    {
        try { _contextInitialized.TrySetResult(); }
        catch (Exception ex) { Log.Warning(ex, "CEF OnContextInitialized callback failed"); }
    }

    private static void OnTabOpened(nint userData, int requestId, int tabId, int openerId, nint group, nint url, nint title, int muted)
    {
        try
        {
            var evt = new CefBridgeEvent(
                CefBridgeEventKind.TabOpened,
                tabId,
                RequestId: requestId,
                OpenerId: openerId,
                Group: FromUtf8(group),
                Url: FromUtf8(url),
                Title: FromUtf8(title),
                Muted: muted != 0);
            DispatchEvent(evt);
            if (requestId != 0 && PendingOpens.TryRemove(requestId, out var tcs))
                tcs.TrySetResult(tabId);
        }
        catch (Exception ex) { Log.Warning(ex, "CEF OnTabOpened callback failed"); }
    }

    private static void OnTabClosed(nint userData, int tabId)
    {
        try
        {
            FailTabDevTools(tabId);
            DispatchEvent(new CefBridgeEvent(CefBridgeEventKind.TabClosed, tabId));
            if (PendingCloses.TryRemove(tabId, out var tcs))
                tcs.TrySetResult();
        }
        catch (Exception ex) { Log.Warning(ex, "CEF OnTabClosed callback failed"); }
    }

    private static void OnNavigation(nint userData, int tabId, nint kind, nint url, nint title, int canGoBack, int canGoForward)
    {
        try
        {
            DispatchEvent(new CefBridgeEvent(
                CefBridgeEventKind.Navigation,
                tabId,
                NavigationKind: FromUtf8(kind),
                Url: FromUtf8(url),
                Title: FromUtf8(title),
                CanGoBack: canGoBack != 0,
                CanGoForward: canGoForward != 0));
        }
        catch (Exception ex) { Log.Warning(ex, "CEF OnNavigation callback failed"); }
    }

    private static void OnMutedChanged(nint userData, int tabId, int muted)
    {
        try
        {
            DispatchEvent(new CefBridgeEvent(CefBridgeEventKind.MutedChanged, tabId, Muted: muted != 0));
        }
        catch (Exception ex) { Log.Warning(ex, "CEF OnMutedChanged callback failed"); }
    }

    private static void OnDevToolsResult(nint userData, int tabId, int messageId, int success, nint json, int jsonLen)
    {
        try
        {
            if (!PendingDevTools.TryRemove((tabId, messageId), out var tcs)) return;
            var raw = FromUtf8(json, jsonLen);
            if (success != 0)
            {
                if (string.IsNullOrWhiteSpace(raw))
                {
                    using var empty = JsonDocument.Parse("{}");
                    tcs.TrySetResult(empty.RootElement.Clone());
                }
                else
                {
                    using var doc = JsonDocument.Parse(raw);
                    tcs.TrySetResult(doc.RootElement.Clone());
                }
            }
            else
            {
                var errorMessage = "Browser command failed.";
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(raw);
                        if (doc.RootElement.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String)
                            errorMessage = msg.GetString() ?? errorMessage;
                        else
                            errorMessage = raw;
                    }
                    catch (JsonException)
                    {
                        errorMessage = raw;
                    }
                }
                tcs.TrySetException(new ChromeDevToolsException(errorMessage));
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "CEF OnDevToolsResult callback failed");
        }
    }

    private static void OnDevToolsEvent(nint userData, int tabId, nint method, nint paramsJson, int paramsLen)
    {
        // Reserved for future CDP event subscriptions.
    }

    private static void OnInteraction(nint userData, int interactionId, int tabId, nint kind, nint payloadJson)
    {
        try
        {
            DispatchEvent(new CefBridgeEvent(
                CefBridgeEventKind.Interaction,
                tabId,
                InteractionId: interactionId,
                InteractionKind: FromUtf8(kind),
                PayloadJson: FromUtf8(payloadJson)));
        }
        catch (Exception ex) { Log.Warning(ex, "CEF OnInteraction callback failed"); }
    }

    private static void OnDownloadCompleted(nint userData, int tabId, nint fileName, int ok)
    {
        try
        {
            DispatchEvent(new CefBridgeEvent(
                CefBridgeEventKind.DownloadCompleted,
                tabId,
                FileName: FromUtf8(fileName),
                DownloadOk: ok != 0));
        }
        catch (Exception ex) { Log.Warning(ex, "CEF OnDownloadCompleted callback failed"); }
    }

    private static void OnUserInteraction(nint userData, int tabId)
    {
        try
        {
            DispatchEvent(new CefBridgeEvent(CefBridgeEventKind.UserInteraction, tabId));
        }
        catch (Exception ex) { Log.Warning(ex, "CEF OnUserInteraction callback failed"); }
    }

    private static void OnSiteDataCleared(nint userData, int requestId, int ok)
    {
        try
        {
            if (PendingClearData.TryRemove(requestId, out var tcs))
                tcs.TrySetResult(ok != 0);
        }
        catch (Exception ex) { Log.Warning(ex, "CEF OnSiteDataCleared callback failed"); }
    }

    private static void OnSchedulePumpWork(nint userData, long delayMs)
    {
        try
        {
            if (!IsInitialized) return;
            if (delayMs <= 0)
            {
                RequestMainThreadPump();
            }
            else
            {
                var clamped = (int)Math.Clamp(delayMs, 1, 33);
                _ = Task.Delay(clamped).ContinueWith(_ => RequestMainThreadPump(), TaskScheduler.Default);
            }
        }
        catch (Exception ex) { Log.Warning(ex, "CEF OnSchedulePumpWork callback failed"); }
    }
}

internal sealed class CefNativeSession(Action<CefBridgeEvent> handler) : IAsyncDisposable
{
    private volatile bool _disposed;

    public bool IsOpen => !_disposed && CefNativeBridge.IsInitialized;

    public Task<int> CreateTabAsync(nint parentHandle, int width, int height, double scale, string group, string initialUrl, bool visible, CancellationToken token) =>
        CefNativeBridge.CreateTabAsync(parentHandle, width, height, scale, group, initialUrl, visible, token);

    public void SetTabParent(int tabId, nint parentHandle, int width, int height, double scale, bool visible) =>
        CefNativeBridge.SetTabParent(tabId, parentHandle, width, height, scale, visible);

    public void SetTabBounds(int tabId, int width, int height, double scale, bool visible) =>
        CefNativeBridge.SetTabBounds(tabId, width, height, scale, visible);

    public void SetTabFocus(int tabId, bool focused) =>
        CefNativeBridge.SetTabFocus(tabId, focused);

    public void SetAgentInputActive(int tabId, bool active) =>
        CefNativeBridge.SetAgentInputActive(tabId, active);

    public Task CloseTabAsync(int tabId, bool forceClose, CancellationToken token) =>
        CefNativeBridge.CloseTabAsync(tabId, forceClose, token);

    public void Navigate(int tabId, string url) => CefNativeBridge.Navigate(tabId, url);
    public void GoBack(int tabId) => CefNativeBridge.GoBack(tabId);
    public void GoForward(int tabId) => CefNativeBridge.GoForward(tabId);
    public void Reload(int tabId) => CefNativeBridge.Reload(tabId);
    public bool SetMuted(int tabId, bool muted) => CefNativeBridge.SetMuted(tabId, muted);

    public Task<JsonElement> SendDevToolsAsync(int tabId, string method, object parameters, CancellationToken token) =>
        CefNativeBridge.SendDevToolsAsync(tabId, method, parameters, token);

    public void ReplyInteraction(int interactionId, bool accept, string? text1 = null, string? text2 = null) =>
        CefNativeBridge.ReplyInteraction(interactionId, accept, text1, text2);

    public Task ClearSiteDataAsync(CancellationToken token) => CefNativeBridge.ClearSiteDataAsync(token);

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        CefNativeBridge.DetachHandler(handler);
        return ValueTask.CompletedTask;
    }
}
