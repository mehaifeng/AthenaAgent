using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.ComputerUse;

internal sealed class ChromeDevTools(BrowserHostConnection connection, string tab)
{
    public bool IsOpen => connection.IsOpen;

    public Task<JsonElement> CallAsync(string method, object parameters, CancellationToken token) =>
        connection.CallAsync(method, parameters, token, tab);
}

/// <summary>One private, request-correlated pipe. A cancelled caller does not interrupt other tabs or the preview.</summary>
internal sealed class BrowserHostConnection : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Action<JsonElement> _event;
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _requests = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _closed = new();
    private readonly Task _reader;
    private readonly Task _errors;
    private int _nextId;
    private string? _lastStartupDiagnostic;

    public BrowserHostConnection(string executable, string script, string profile, Action<JsonElement> onEvent)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true, StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add(script);
        start.ArgumentList.Add(profile);
        start.Environment.Remove("ELECTRON_RUN_AS_NODE");
        start.Environment.Remove("NODE_OPTIONS");
        start.Environment.Remove("NODE_PATH");
        _event = onEvent;
        _process = Process.Start(start) ?? throw new InvalidOperationException("Could not launch the bundled browser.");
        _reader = ReadAsync();
        _errors = ReadErrorsAsync();
    }

    public bool IsOpen => !_closed.IsCancellationRequested;
    public Task WaitUntilReadyAsync(CancellationToken token) => _ready.Task.WaitAsync(token);

    public async Task<JsonElement> CallAsync(string method, object parameters, CancellationToken token, string? tab = null)
    {
        if (!IsOpen) throw new ChromeDevToolsException("The browser runtime disconnected.");
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var id = Interlocked.Increment(ref _nextId);
        _requests[id] = completion;
        try
        {
            var line = JsonSerializer.Serialize(new { id, tab, method, parameters });
            await _writer.WaitAsync(token).ConfigureAwait(false);
            try
            {
                token.ThrowIfCancellationRequested();
                // Finish an entire pipe message once writing starts, even if its caller stops waiting.
                await _process.StandardInput.WriteLineAsync(line.AsMemory(), _closed.Token).ConfigureAwait(false);
                await _process.StandardInput.FlushAsync(_closed.Token).ConfigureAwait(false);
            }
            finally { _writer.Release(); }
            return await completion.Task.WaitAsync(token).ConfigureAwait(false);
        }
        finally { _requests.TryRemove(id, out _); }
    }

    private async Task ReadAsync()
    {
        Exception? error = null;
        try
        {
            while (await _process.StandardOutput.ReadLineAsync(_closed.Token).ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0) continue;
                if (line.Length > 32 * 1024 * 1024)
                    throw new IOException("The browser returned an oversized response.");
                using var document = JsonDocument.Parse(line);
                var message = document.RootElement;
                if (message.TryGetProperty("id", out var id))
                {
                    if (!_requests.TryGetValue(id.GetInt32(), out var completion)) continue;
                    if (message.TryGetProperty("error", out var failure))
                        completion.TrySetException(new ChromeDevToolsException(failure.GetString() ?? "Browser command failed."));
                    else
                        completion.TrySetResult(message.GetProperty("result").Clone());
                    continue;
                }
                switch (message.GetProperty("event").GetString())
                {
                    case "ready":
                        if (message.GetProperty("version").GetString() != BrowserRuntime.Version)
                            throw new IOException("The browser runtime returned an unexpected version.");
                        _ready.TrySetResult();
                        break;
                    case "startup-error":
                        throw new IOException(message.GetProperty("error").GetString());
                    default:
                        _event(message);
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or OperationCanceledException)
        {
            error = ex;
        }
        finally
        {
            _closed.Cancel();
            var failure = new ChromeDevToolsException(error?.Message ?? (_lastStartupDiagnostic is { } detail
                ? $"The browser runtime disconnected. {detail}" : "The browser runtime disconnected."));
            _ready.TrySetException(failure);
            foreach (var request in _requests.Values) request.TrySetException(failure);
            using var disconnected = JsonDocument.Parse("""{"event":"disconnected"}""");
            _event(disconnected.RootElement);
        }
    }

    private async Task ReadErrorsAsync()
    {
        while (await _process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            if (!_ready.Task.IsCompleted && (line.Contains("FATAL", StringComparison.OrdinalIgnoreCase)
                || line.Contains("error while loading shared libraries", StringComparison.OrdinalIgnoreCase)))
                _lastStartupDiagnostic = line.Length <= 400 ? line : line[..400];
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.StandardInput.Close();
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                await _process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException)
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _closed.Cancel();
            await Task.WhenAll(_reader, _errors).ConfigureAwait(false);
            _process.Dispose();
        }
    }
}

internal sealed class ChromeDevToolsException(string message) : InvalidOperationException(message)
{
    public bool IsNavigation =>
        Message.Contains("Cannot find context", StringComparison.OrdinalIgnoreCase)
        || Message.Contains("context was destroyed", StringComparison.OrdinalIgnoreCase)
        || Message.Contains("Inspected target navigated", StringComparison.OrdinalIgnoreCase)
        || Message.Contains("No frame with given id", StringComparison.OrdinalIgnoreCase)
        || Message.Contains("Not attached to an active page", StringComparison.OrdinalIgnoreCase)
        || Message.Contains("target closed while handling command", StringComparison.OrdinalIgnoreCase);
}
