using Athena.UI.Services.Interfaces;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.ComputerUse;

public sealed record ComputerUseTarget(int Pid, long WindowId, string Title, string App)
{
    public string DisplayName => string.IsNullOrWhiteSpace(App) ? Title : $"{Title} · {App}";
}

public sealed record ComputerUseFrame(byte[] Image, string MimeType, int Width, int Height, string Tree,
    string? CaptureId, IReadOnlyList<ComputerUseElement> Elements);

public sealed record ComputerUseElement(int Index, string Token, string Role, string Label);

public sealed record ComputerUseAction(string Action, double? X = null, double? Y = null,
    int? Element = null, string? Text = null, string? Key = null, string? Direction = null,
    bool Success = false, string? Summary = null);

public enum ComputerUsePhase { Observing, Moving, Clicking, Typing, OtherAction, Completed, Stopped, Failed }

public sealed record ComputerUseProgress(ComputerUsePhase Phase, ComputerUseFrame? Frame = null,
    double? X = null, double? Y = null, string? Detail = null);

public sealed class ComputerUseService(IConfigService config, OpenAiModelRuntimeFactory models, ILogger logger) : IAsyncDisposable
{
    private const int MaxSteps = 16;
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
    private bool _disposed;
    private ComputerUseTarget? _selectedTarget;

    public event Action<ComputerUseProgress>? ProgressChanged;
    public bool IsAvailable => File.Exists(CuaDriverNative.LibraryPath);
    public ComputerUseTarget? SelectedTarget
    {
        get => Volatile.Read(ref _selectedTarget);
        set => Volatile.Write(ref _selectedTarget, value);
    }

    public async Task<IReadOnlyList<ComputerUseTarget>> ListWindowsAsync(
        CancellationToken cancellationToken, int? pid = null)
    {
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
        var planner = new ComputerUsePlanner(config.Load(), models, goal);
        return RunAsync(target, planner.DecideAsync, cancellationToken);
    }

    internal async Task<FunctionResult> RunAsync(ComputerUseTarget target,
        Func<ComputerUseFrame, string, int, CancellationToken, Task<ComputerUseAction>> decide,
        CancellationToken cancellationToken)
    {
        using var run = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_activeRun is not null)
                return FunctionResult.FailureResult("Another Computer Use task is running.");
            _activeRun = run;
            _runEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        run.CancelAfter(TimeSpan.FromMinutes(3));
        var token = run.Token;
        var session = $"athena-{Guid.NewGuid():N}";
        ComputerUseFrame? frame = null;
        var started = false;
        try
        {
            Publish(new(ComputerUsePhase.Observing, Detail: target.Title));
            await CallAsync("start_session", new { session }, token).ConfigureAwait(false);
            started = true;
            var previousResult = "";
            for (var step = 0; step <= MaxSteps; step++)
            {
                frame = await ReadFrameAsync(target, session, token).ConfigureAwait(false);
                Publish(new(ComputerUsePhase.Observing, frame, Detail: target.Title));
                var action = await decide(frame, previousResult, step, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (action.Action == "finish")
                {
                    var summary = string.IsNullOrWhiteSpace(action.Summary) ? "Computer Use finished." : action.Summary;
                    Publish(new(action.Success ? ComputerUsePhase.Completed : ComputerUsePhase.Failed, frame, Detail: summary));
                    return action.Success ? FunctionResult.SuccessResult(summary) : FunctionResult.FailureResult(summary);
                }
                if (step == MaxSteps)
                    throw new InvalidOperationException("Computer Use reached its action limit.");
                previousResult = await ExecuteAsync(target, session, frame, action, token).ConfigureAwait(false);
                await Task.Delay(160, token).ConfigureAwait(false);
            }
            throw new InvalidOperationException("Computer Use reached its action limit.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Publish(new(ComputerUsePhase.Stopped, frame));
            return FunctionResult.FailureResult("Computer Use stopped.");
        }
        catch (Exception exception)
        {
            logger.Warning("Computer Use task failed: {Error}", exception.Message);
            Publish(new(ComputerUsePhase.Failed, frame, Detail: exception.Message));
            return FunctionResult.FailureResult(exception.Message);
        }
        finally
        {
            if (started)
            {
                try
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await CallAsync("end_session", new { session }, cleanup.Token).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    logger.Debug("Cua Driver session cleanup ended: {Error}", exception.Message);
                }
            }
            lock (_gate)
            {
                _activeRun = null;
                _runEnded?.TrySetResult();
                _runEnded = null;
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
            _activeRun?.Cancel();
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

    private async Task<string> ExecuteAsync(ComputerUseTarget target, string session, ComputerUseFrame frame,
        ComputerUseAction action, CancellationToken token)
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
        }

        if (action.X is not double x || action.Y is not double y || !double.IsFinite(x) || !double.IsFinite(y)
            || x < 0 || y < 0 || x >= frame.Width || y >= frame.Height)
            throw new InvalidOperationException("The action must point to a valid pixel in the current window observation.");

        if (action.Element is null)
        {
            arguments["x"] = x;
            arguments["y"] = y;
            if (action.Action == "click" && !string.IsNullOrWhiteSpace(frame.CaptureId))
                arguments["capture_id"] = frame.CaptureId;
        }

        switch (action.Action)
        {
            case "click":
                break;
            case "type_text" when action.Text is { Length: > 0 and <= 4000 } text:
                arguments["text"] = text;
                break;
            case "press_key" when action.Key is { } key && NavigationKeys.Contains(key):
                arguments["key"] = key;
                break;
            case "scroll" when action.Direction is "up" or "down" or "left" or "right":
                arguments["direction"] = action.Direction;
                break;
            default:
                throw new InvalidOperationException("The model returned an unsupported Computer Use action.");
        }

        var px = x / frame.Width;
        var py = y / frame.Height;
        Publish(new(ComputerUsePhase.Moving, frame, px, py));
        await Task.Delay(270, token).ConfigureAwait(false);
        if (action.Action == "type_text")
            Publish(new(ComputerUsePhase.Typing, frame, px, py));
        var result = await CallAsync(action.Action, arguments, token).ConfigureAwait(false);
        Publish(new(action.Action == "click" ? ComputerUsePhase.Clicking
            : action.Action == "type_text" ? ComputerUsePhase.Typing : ComputerUsePhase.OtherAction, frame, px, py));
        return ReadText(result);
    }

    private async Task<JsonElement> CallAsync(string tool, object arguments, CancellationToken token)
    {
        if (!IsAvailable)
            throw new InvalidOperationException("Cua Driver SDK is missing. Run Scripts/run-computer-use-demo.sh or set ATHENA_CUA_DRIVER_LIBRARY.");
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

    private void Publish(ComputerUseProgress progress) => ProgressChanged?.Invoke(progress);

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
