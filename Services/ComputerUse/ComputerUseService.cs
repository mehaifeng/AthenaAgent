using Athena.UI.Services.Interfaces;
using Athena.UI.Models;
using Serilog;
using System;
using System.ClientModel.Primitives;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.ComputerUse;

public sealed record ComputerUseTarget(int Pid, long WindowId, string Title, string App, Uri? BrowserUri = null)
{
    public static ComputerUseTarget Desktop { get; } = new(0, 0, "Current desktop", "");
    public bool IsDesktop => BrowserUri is null && Pid == 0 && WindowId == 0;
    public string DisplayName => string.IsNullOrWhiteSpace(App) ? Title : $"{Title} · {App}";

    public static ComputerUseTarget? FromBrowserAddress(string address) =>
        Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri) && EmbeddedBrowser.IsPageUri(uri)
            ? new ComputerUseTarget(0, 0, uri.Host, "Chrome", uri) : null;
}

public sealed record ComputerUseFrame(byte[] Image, string MimeType, int Width, int Height, string Tree,
    string? CaptureId, IReadOnlyList<ComputerUseElement> Elements, Uri? BrowserUri = null, bool IsDesktop = false)
{
    internal Avalonia.PixelRect? PortalMonitor { get; init; }
    internal bool FromPortal { get; init; }
}

public sealed record ComputerUseElement(int Index, string Token, string Role, string Label, double? X = null, double? Y = null);

public sealed record ComputerUseAction(string Action, double? X = null, double? Y = null,
    int? Element = null, string? Text = null, string? Key = null, string? Direction = null,
    bool Success = false, string? Summary = null, string? Url = null);

public enum ComputerUsePhase { Observing, Moving, Clicking, Typing, Navigating, OtherAction, Paused, Resumed, Completed, Stopped, Failed, TimedOut }

public sealed record ComputerUseProgress(ComputerUsePhase Phase, ComputerUseFrame? Frame = null,
    double? X = null, double? Y = null, string? Detail = null, bool InBrowser = false,
    Uri? Address = null)
{
    internal ComputerUseCursor? DesktopCursor { get; init; }
}

internal interface IDesktopCursorDisplay
{
    Task ShowAsync(ComputerUseFrame frame, ComputerUseCursor cursor);
    Task<bool> HideForCaptureAsync();
    Task CloseAsync();
}

internal interface IComputerUseSession : IAsyncDisposable
{
    Task<ComputerUseFrame> ObserveAsync(CancellationToken token);
    Task<string> ActAsync(ComputerUseFrame frame, ComputerUseAction action, double x, double y, CancellationToken token);
    Task EndAsync(CancellationToken token);
    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await EndAsync(cleanup.Token).ConfigureAwait(false); }
        catch (Exception exception) { Log.Debug(exception, "Computer Use session cleanup ended"); }
    }
}

public sealed class ComputerUseService(IConfigService config, ILogger logger, EmbeddedBrowser browser,
    ModelRoleProbeService roleProbes, PipelineTransport? modelTransport = null) : IAsyncDisposable
{
    private const int MaxWindowSteps = 16;
    private const int MaxImageDimension = 1440;
    private static readonly HashSet<string> NavigationKeys = new(StringComparer.Ordinal)
    {
        "Return", "Tab", "Escape", "Left", "Right", "Up", "Down",
        "Home", "End", "Page_Up", "Page_Down", "BackSpace", "Delete", "space"
    };

    private readonly Lazy<CuaDriverNative> _driver = new(() => new CuaDriverNative());
    private readonly object _gate = new();
    private CancellationTokenSource? _activeRun;
    private TaskCompletionSource? _runEnded;
    private bool _activeRunInBrowser;
    private BrowserTaskControl? _browserControl;
    private bool _disposed;
    private ComputerUseTarget? _selectedTarget;
    internal IDesktopCursorDisplay? CursorDisplay { get; init; }
    private ComputerUseCursor? _desktopCursor;

    public event Action<ComputerUseProgress>? ProgressChanged;
    public bool HasWindowDriver => File.Exists(CuaDriverNative.LibraryPath);
    public bool IsDesktopAvailable => PortalDesktopSession.IsWayland || HasWindowDriver;
    public bool IsAvailable => IsDesktopAvailable || browser.IsAvailable;
    internal bool HasBrowserTask => Volatile.Read(ref _browserControl) != null;
    internal bool IsBrowserPaused => Volatile.Read(ref _browserControl)?.IsPaused == true;
    internal bool IsBrowserTaskRunning => Volatile.Read(ref _browserControl) is { IsPaused: false };
    internal void RequestBrowserTakeover(string? reason = null) => Volatile.Read(ref _browserControl)?.Pause(reason);
    internal void BrowserTaskPageChanged() => Volatile.Read(ref _browserControl)?.PageChanged();
    internal Task ResumeBrowserAsync(CancellationToken token) =>
        Volatile.Read(ref _browserControl)?.ResumeAsync(token) ?? Task.CompletedTask;
    internal Task WithManualBrowserInputAsync(ChromeBrowserPage page, Func<Task> send, CancellationToken token, bool changesPage = true) =>
        page.IsTask && Volatile.Read(ref _browserControl) is { } control
            ? control.WithHumanInputAsync(send, token, changesPage) : send();
    public ComputerUseTarget? SelectedTarget
    {
        get => Volatile.Read(ref _selectedTarget);
        set => Volatile.Write(ref _selectedTarget, value);
    }

    internal ComputerUseTarget? CurrentBrowserTarget =>
        (browser.BrowsingPage ?? browser.TaskPage)?.Address is { } address
            ? ComputerUseTarget.FromBrowserAddress(address.AbsoluteUri)
            : null;

    public async Task<IReadOnlyList<ComputerUseTarget>> ListWindowsAsync(
        CancellationToken cancellationToken, int? pid = null)
    {
        if (!HasWindowDriver)
            return [];
        var arguments = new Dictionary<string, object> { ["on_screen_only"] = true };
        if (pid is not null)
            arguments["pid"] = pid.Value;
        var result = await CallAsync("list_windows", arguments, cancellationToken).ConfigureAwait(false);
        return result.GetProperty("structuredContent").GetProperty("windows").EnumerateArray()
            .Select(window => new ComputerUseTarget(window.GetProperty("pid").GetInt32(),
                window.GetProperty("window_id").GetInt64(), ReadString(window, "title"), ReadString(window, "app_name")))
            .Where(window => window.Pid > 0 && window.WindowId > 0 && !string.IsNullOrWhiteSpace(window.Title))
            .Where(window => pid == Environment.ProcessId || window.Pid != Environment.ProcessId)
            .ToArray();
    }

    public Task<FunctionResult> RunAsync(ComputerUseTarget target, string goal, CancellationToken cancellationToken)
    {
        var current = config.Load();
        var browserSettings = current.EmbeddedBrowser;
        var model = roleProbes.Resolve(current, AiModelRole.ComputerUse);
        var needsProof = roleProbes.NeedsVisionProof(model);
        var planner = new ComputerUsePlanner(model, goal, target.BrowserUri is not null, modelTransport, browserSettings.ActionRetryCount, target.IsDesktop);
        return RunAsync(target, planner.DecideAsync, cancellationToken, planner.RememberProgress,
            TimeSpan.FromMinutes(browserSettings.TaskTimeoutMinutes),
            needsProof ? token => roleProbes.TestAsync(model, token, modelTransport) : null);
    }

    internal async Task<FunctionResult> RunAsync(ComputerUseTarget target,
        Func<ComputerUseFrame, string, int, CancellationToken, Task<ComputerUseAction>> decide,
        CancellationToken cancellationToken, Action<ComputerUseFrame, ComputerUseAction>? remember = null,
        TimeSpan? browserTimeLimit = null, Func<CancellationToken, Task>? prepare = null)
    {
        var inBrowser = target.BrowserUri is not null;
        var timeLimit = inBrowser
            ? browserTimeLimit ?? TimeSpan.FromMinutes(config.Load().EmbeddedBrowser.TaskTimeoutMinutes)
            : TimeSpan.FromMinutes(target.IsDesktop ? 10 : 3);
        using var deadline = new CancellationTokenSource();
        using var run = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        BrowserTaskControl? control = null;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_activeRun is not null)
                return FunctionResult.FailureResult("Another Computer Use task is running.");
            _activeRun = run;
            _activeRunInBrowser = inBrowser;
            _runEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (inBrowser)
                _browserControl = control = new BrowserTaskControl(run.Token, deadline, timeLimit,
                    reason => Publish(new(ComputerUsePhase.Paused, Detail: reason, InBrowser: true)),
                    () => Publish(new(ComputerUsePhase.Resumed, InBrowser: true)));
        }

        if (control == null) deadline.CancelAfter(timeLimit);
        var token = run.Token;
        ComputerUseFrame? frame = null;
        try
        {
            Publish(new(ComputerUsePhase.Observing, Detail: target.Title, InBrowser: inBrowser, Address: target.BrowserUri));
            if (prepare is not null) await prepare(token).ConfigureAwait(false);
            await using var session = await OpenSessionAsync(target, token).ConfigureAwait(false);
            var previousResult = "";
            var generation = 0;
            for (var step = 0; ;)
            {
                var (observedGeneration, observationToken, afterTakeover) = control != null
                    ? await control.NextObservationAsync(token).ConfigureAwait(false) : (0, token, false);
                if (observedGeneration != generation)
                {
                    generation = observedGeneration;
                    previousResult = afterTakeover
                        ? "The user returned control. Observe the current page before acting."
                        : "The user interacted with the browser. Observe the current page before acting.";
                }
                ComputerUseAction action;
                try
                {
                    frame = await ObserveAsync(session, target.IsDesktop, observationToken).ConfigureAwait(false);
                    observationToken.ThrowIfCancellationRequested();
                    Publish(new(ComputerUsePhase.Observing, frame, Detail: target.Title, InBrowser: inBrowser));
                    action = await decide(frame, previousResult, step, observationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested && observationToken.IsCancellationRequested)
                {
                    continue;
                }
                token.ThrowIfCancellationRequested();
                using (var input = control == null ? null : await control.BeginAgentInputAsync(generation, token).ConfigureAwait(false))
                {
                    if (control != null && input == null) continue;
                    if (control != null && action.Action == "handoff")
                    {
                        control.Pause(action.Summary);
                        continue;
                    }
                    if (action.Action == "finish")
                    {
                        var summary = string.IsNullOrWhiteSpace(action.Summary) ? "Computer Use finished." : action.Summary;
                        Publish(new(action.Success ? ComputerUsePhase.Completed : ComputerUsePhase.Failed, frame,
                            Detail: summary, InBrowser: inBrowser));
                        return action.Success ? FunctionResult.SuccessResult(summary) : FunctionResult.FailureResult(summary);
                    }
                    if (!inBrowser && !target.IsDesktop && step == MaxWindowSteps)
                        throw new InvalidOperationException("Computer Use reached its action limit.");
                    remember?.Invoke(frame, action);
                    previousResult = await ExecuteAsync(session, frame, action, token).ConfigureAwait(false);
                }
                step++;
                // A desktop compositor can still show the outgoing window during a focus or overview animation.
                await Task.Delay(target.IsDesktop ? 500 : 160, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if ((inBrowser || target.IsDesktop) && deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                Publish(new(ComputerUsePhase.TimedOut, frame, InBrowser: inBrowser));
                return FunctionResult.FailureResult(inBrowser ? "The browser task reached its active time limit." : "The desktop task reached its time limit.");
            }
            Publish(new(ComputerUsePhase.Stopped, frame, InBrowser: inBrowser));
            return FunctionResult.FailureResult("Computer Use stopped.");
        }
        catch (Exception exception)
        {
            logger.Warning("Computer Use task failed: {Error}", exception.Message);
            Publish(new(ComputerUsePhase.Failed, frame, Detail: exception.Message, InBrowser: inBrowser));
            return FunctionResult.FailureResult(exception.Message);
        }
        finally
        {
            if (target.IsDesktop)
            {
                _desktopCursor = null;
                if (CursorDisplay is { } display)
                {
                    try { await display.CloseAsync().ConfigureAwait(false); }
                    catch (Exception e) { logger.Debug(e, "Desktop cursor presentation ended during shutdown"); }
                }
            }
            lock (_gate)
            {
                _browserControl = null;
                control?.Dispose();
                _activeRun = null;
                _runEnded?.TrySetResult();
                _runEnded = null;
            }
        }
    }

    public void StopDesktop()
    {
        lock (_gate)
        {
            if (!_activeRunInBrowser)
                _activeRun?.Cancel();
        }
    }

    private async Task<IComputerUseSession> OpenSessionAsync(ComputerUseTarget target, CancellationToken token)
    {
        if (target.BrowserUri is { } address) return await OpenBrowserAsync(address, token).ConfigureAwait(false);
        if (target.IsDesktop && PortalDesktopSession.IsWayland) return await PortalDesktopSession.OpenAsync(token).ConfigureAwait(false);
        return await OpenWindowAsync(target, token).ConfigureAwait(false);
    }

    private async Task<IComputerUseSession> OpenWindowAsync(ComputerUseTarget target, CancellationToken token)
    {
        var session = $"athena-{Guid.NewGuid():N}";
        await CallAsync("start_session", new { session, capture_scope = target.IsDesktop ? "desktop" : "window" }, token).ConfigureAwait(false);
        if (!target.IsDesktop) return new WindowSession(this, target, session);
        var desktop = new NativeDesktopSession(session, CallAsync);
        try
        {
            if (CursorDisplay is not null)
                await CallAsync("set_agent_cursor_enabled", new { session, enabled = false }, token).ConfigureAwait(false);
            return desktop;
        }
        catch
        {
            await ((IAsyncDisposable)desktop).DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<IComputerUseSession> OpenBrowserAsync(Uri address, CancellationToken token) =>
        new BrowserComputerUseSession(browser, await browser.OpenTaskAsync(address, token).ConfigureAwait(false));

    private sealed class WindowSession(ComputerUseService owner, ComputerUseTarget target, string id) : IComputerUseSession
    {
        public Task<ComputerUseFrame> ObserveAsync(CancellationToken token) => owner.ReadFrameAsync(target, id, token);

        public Task<string> ActAsync(ComputerUseFrame frame, ComputerUseAction action, double x, double y, CancellationToken token) =>
            owner.ActOnWindowAsync(target, id, frame, action, x, y, token);

        public Task EndAsync(CancellationToken token) => owner.CallAsync("end_session", new { session = id }, token);
    }

    private async Task<ComputerUseFrame> ReadFrameAsync(ComputerUseTarget target, string session, CancellationToken token)
    {
        var result = await CallAsync("get_window_state", new
        {
            pid = target.Pid,
            window_id = target.WindowId,
            session,
            tree_format = "both",
            max_elements = 60,
            max_image_dimension = MaxImageDimension
        }, token).ConfigureAwait(false);
        var state = result.GetProperty("structuredContent");
        var image = result.GetProperty("content").EnumerateArray()
            .FirstOrDefault(part => ReadString(part, "type") == "image");
        if (image.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"Cua Driver could not capture '{target.Title}'. Select a visible capture-capable window.");

        return new(image.GetProperty("data").GetBytesFromBase64(), ReadString(image, "mimeType"),
            state.GetProperty("screenshot_width").GetInt32(), state.GetProperty("screenshot_height").GetInt32(),
            ReadString(state, "tree_markdown"), ReadString(state, "capture_id"), ReadElements(state));
    }

    private async Task<string> ExecuteAsync(IComputerUseSession session, ComputerUseFrame frame, ComputerUseAction action,
        CancellationToken token)
    {
        if (session is BrowserComputerUseSession page && action.Action == "navigate")
        {
            if (!Uri.TryCreate(action.Url?.Trim(), UriKind.Absolute, out var address) || !EmbeddedBrowser.IsPageUri(address))
                return "No navigation occurred. Use a complete HTTP or HTTPS URL without embedded credentials.";

            Publish(new(ComputerUsePhase.Navigating, frame, InBrowser: true, Address: address));
            return await page.NavigateAsync(address, token).ConfigureAwait(false);
        }

        var validAction = action.Action switch
        {
            "click" => true,
            "double_click" or "right_click" => frame.IsDesktop,
            "type_text" => action.Text is { Length: > 0 and <= 4000 },
            "press_key" => action.Key is { } key && (NavigationKeys.Contains(key) || frame.IsDesktop && DesktopKeys.TryParse(key, out _)),
            "scroll" => action.Direction is "up" or "down" or "left" or "right",
            _ => false
        };
        var inBrowser = frame.BrowserUri is not null;
        if (!validAction && (inBrowser || frame.IsDesktop))
            return "No input was sent. Choose one supported action in the requested JSON format.";
        if (!validAction)
            throw new InvalidOperationException("The model returned an unsupported Computer Use action.");

        double x, y;
        try
        {
            (x, y) = ResolveActionPoint(frame, action);
        }
        catch (InvalidOperationException) when (inBrowser || frame.IsDesktop)
        {
            return "No input was sent. Pick a numbered control from the new observation or give x and y in screenshot pixels.";
        }
        var px = x / frame.Width;
        var py = y / frame.Height;
        bool hasDesktopTypePoint = frame.IsDesktop && action.Action == "type_text" && action.X is not null && action.Y is not null;
        bool usesPointer = !frame.IsDesktop || action.Action is "click" or "double_click" or "right_click" or "scroll" || hasDesktopTypePoint;
        if (usesPointer && frame.IsDesktop)
            await ShowCursorAsync(frame, ComputerUseCursor.Move(_desktopCursor, px, py)).ConfigureAwait(false);
        var cursorX = usesPointer ? px : _desktopCursor?.X;
        var cursorY = usesPointer ? py : _desktopCursor?.Y;
        Publish(new(ComputerUsePhase.Moving, frame, cursorX, cursorY, InBrowser: inBrowser));
        if (usesPointer && !inBrowser) await Task.Delay(ComputerUseCursor.MoveMilliseconds, token).ConfigureAwait(false);
        if (hasDesktopTypePoint)
        {
            if (_desktopCursor is { } focusPointer)
                await ShowCursorAsync(frame, focusPointer.Click()).ConfigureAwait(false);
            Publish(new(ComputerUsePhase.Clicking, frame, cursorX, cursorY, InBrowser: inBrowser));
        }
        if (action.Action == "type_text")
        {
            if (frame.IsDesktop && _desktopCursor is { } cursor)
                await ShowCursorAsync(frame, cursor with { IsTyping = true }).ConfigureAwait(false);
            Publish(new(ComputerUsePhase.Typing, frame, cursorX, cursorY, InBrowser: inBrowser));
        }
        var result = await session.ActAsync(frame, action, x, y, token).ConfigureAwait(false);
        if (frame.IsDesktop && _desktopCursor is { } pointer && action.Action is "click" or "double_click" or "right_click")
            await ShowCursorAsync(frame, pointer.Click()).ConfigureAwait(false);
        Publish(new(action.Action is "click" or "double_click" or "right_click" ? ComputerUsePhase.Clicking
            : action.Action == "type_text" ? ComputerUsePhase.Typing : ComputerUsePhase.OtherAction,
            frame, cursorX, cursorY, Detail: frame.IsDesktop ? ComputerUsePlanner.DescribeDesktopInput(action) : null, InBrowser: inBrowser));
        return result;
    }

    private async Task<ComputerUseFrame> ObserveAsync(IComputerUseSession session, bool desktop, CancellationToken token)
    {
        if (!desktop)
            return await session.ObserveAsync(token).ConfigureAwait(false);
        var cursor = _desktopCursor ?? ComputerUseCursor.Resting;
        bool hidden = _desktopCursor != null && CursorDisplay is { } display && await display.HideForCaptureAsync().ConfigureAwait(false);
        if (hidden) await Task.Delay(40, token).ConfigureAwait(false);
        var frame = hidden && session is PortalDesktopSession portal
            ? await portal.ObserveAsync(token, fresh: true).ConfigureAwait(false)
            : await session.ObserveAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        await ShowCursorAsync(frame, cursor.IsTyping ? cursor with { IsTyping = false } : cursor).ConfigureAwait(false);
        return frame;
    }

    private Task ShowCursorAsync(ComputerUseFrame frame, ComputerUseCursor cursor)
    {
        _desktopCursor = cursor;
        return CursorDisplay?.ShowAsync(frame, cursor) ?? Task.CompletedTask;
    }

    internal static (double X, double Y) ResolveActionPoint(ComputerUseFrame frame, ComputerUseAction action)
    {
        var inBrowser = frame.BrowserUri is not null;
        if (frame.IsDesktop && action.Element is not null)
            throw new InvalidOperationException("Desktop actions use the latest screenshot and keyboard focus.");
        if (frame.IsDesktop && (action.Action == "press_key" || action.Action == "type_text" && action.X is null && action.Y is null))
            return (frame.Width / 2.0, frame.Height / 2.0);
        if (inBrowser && action is { Action: "scroll" or "press_key", Element: null, X: null, Y: null })
            return (frame.Width / 2.0, frame.Height / 2.0);

        var element = action.Element is { } index && inBrowser
            ? frame.Elements.FirstOrDefault(item => item.Index == index)
                ?? throw new InvalidOperationException("The selected control is absent from the current observation.")
            : null;
        if ((element?.X ?? action.X) is not double x || (element?.Y ?? action.Y) is not double y
            || !double.IsFinite(x) || !double.IsFinite(y) || x < 0 || y < 0 || x >= frame.Width || y >= frame.Height)
            throw new InvalidOperationException("The action must point to a valid pixel in the current observation.");
        return (x, y);
    }

    private async Task<string> ActOnWindowAsync(ComputerUseTarget target, string session, ComputerUseFrame frame,
        ComputerUseAction action, double x, double y, CancellationToken token)
    {
        var arguments = new Dictionary<string, object>
        {
            ["pid"] = target.Pid, ["window_id"] = target.WindowId, ["session"] = session
        };
        if (action.Element is { } index)
        {
            var element = frame.Elements.FirstOrDefault(item => item.Index == index)
                ?? throw new InvalidOperationException("The selected control is absent from the current observation.");
            arguments["element_token"] = element.Token;
            arguments["delivery_mode"] = "background";
        }
        else
        {
            arguments["delivery_mode"] = "foreground";
            arguments["x"] = x;
            arguments["y"] = y;
            if (action.Action == "click" && !string.IsNullOrWhiteSpace(frame.CaptureId))
                arguments["capture_id"] = frame.CaptureId;
        }

        switch (action.Action)
        {
            case "type_text":
                arguments["text"] = action.Text!;
                break;
            case "press_key":
                arguments["key"] = action.Key!;
                break;
            case "scroll":
                arguments["direction"] = action.Direction!;
                break;
        }
        return ReadText(await CallAsync(action.Action, arguments, token).ConfigureAwait(false));
    }

    private async Task<JsonElement> CallAsync(string tool, object arguments, CancellationToken token)
    {
        if (!HasWindowDriver)
            throw new InvalidOperationException("The native desktop driver is missing from this Athena installation.");
        var result = await _driver.Value.CallAsync(tool, arguments, token).ConfigureAwait(false);
        if (result.TryGetProperty("isError", out var error) && error.ValueKind == JsonValueKind.True)
            throw new InvalidOperationException(ReadText(result));
        return result;
    }

    private static ComputerUseElement[] ReadElements(JsonElement state)
    {
        if (!state.TryGetProperty("elements", out var elements) || elements.ValueKind != JsonValueKind.Array)
            return [];
        return elements.EnumerateArray()
            .Select(element => new ComputerUseElement(element.GetProperty("element_index").GetInt32(),
                ReadString(element, "element_token"), ReadString(element, "role"), ReadString(element, "label")))
            .Where(element => element.Token.Length != 0)
            .ToArray();
    }

    private static string ReadText(JsonElement result) => result.TryGetProperty("content", out var content)
        ? string.Join("\n", content.EnumerateArray().Where(part => ReadString(part, "type") == "text")
            .Select(part => ReadString(part, "text")))
        : "Cua Driver returned no text.";

    private static string ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "" : "";

    private void Publish(ComputerUseProgress progress) => ProgressChanged?.Invoke(
        progress.Frame?.IsDesktop == true ? progress with { DesktopCursor = _desktopCursor } : progress);

    public async ValueTask DisposeAsync()
    {
        Task? wait;
        lock (_gate)
        {
            _disposed = true;
            _activeRun?.Cancel();
            wait = _runEnded?.Task;
        }
        if (wait is not null)
            await wait.ConfigureAwait(false);
        if (_driver.IsValueCreated)
            await _driver.Value.DisposeAsync().ConfigureAwait(false);
    }
}
