using System;
using System.Buffers;
using System.Buffers.Binary;
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

    public BrowserPreview OpenPreview() => connection.OpenPreview(tab);
}

/// <summary>Private process pipes for commands, browser events and compositor frames.</summary>
internal sealed class BrowserHostConnection : IAsyncDisposable
{
    private const int MaxMetadataBytes = 32 * 1024 * 1024;
    private const int MaxPixelBytes = 128 * 1024 * 1024;
    private readonly Process _process;
    private readonly Action<JsonElement> _event;
    private readonly SemaphoreSlim _writer = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _requests = new();
    private readonly ConcurrentDictionary<int, BrowserPreview> _previews = new();
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

    public BrowserPreview OpenPreview(string tab)
    {
        var id = Interlocked.Increment(ref _nextId);
        var preview = new BrowserPreview(this, tab, id);
        _previews[id] = preview;
        return preview;
    }

    internal void ClosePreview(int id) => _previews.TryRemove(id, out _);

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
            var stream = _process.StandardOutput.BaseStream;
            var header = new byte[8];
            var token = _closed.Token;
            while (true)
            {
                var count = await stream.ReadAtLeastAsync(header, header.Length, false, token).ConfigureAwait(false);
                if (count == 0) break;
                if (count != header.Length) throw new IOException("The browser returned an incomplete message.");
                var metadataLength = BinaryPrimitives.ReadInt32LittleEndian(header);
                var pixelLength = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
                if (metadataLength is <= 0 or > MaxMetadataBytes || pixelLength is < 0 or > MaxPixelBytes)
                    throw new IOException("The browser returned an invalid message size.");
                var metadata = new byte[metadataLength];
                await stream.ReadExactlyAsync(metadata, token).ConfigureAwait(false);
                using var document = JsonDocument.Parse(metadata);
                var message = document.RootElement;
                if (pixelLength != 0)
                {
                    await ReadPreviewAsync(stream, message, pixelLength, token).ConfigureAwait(false);
                    continue;
                }
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
            foreach (var preview in _previews.Values) preview.Complete(failure);
            using var disconnected = JsonDocument.Parse("""{"event":"disconnected"}""");
            _event(disconnected.RootElement);
        }
    }

    private async Task ReadPreviewAsync(Stream stream, JsonElement message, int length, CancellationToken token)
    {
        var width = message.GetProperty("width").GetInt32();
        var height = message.GetProperty("height").GetInt32();
        if (message.GetProperty("event").GetString() != "preview" || width <= 0 || height <= 0
            || (long)width * height != length / 4 || length % 4 != 0)
            throw new IOException("The browser returned an invalid preview frame.");
        var id = message.GetProperty("preview").GetInt32();
        var revision = message.GetProperty("revision").GetInt32();
        var pixels = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            await stream.ReadExactlyAsync(pixels.AsMemory(0, length), token).ConfigureAwait(false);
            if (_previews.TryGetValue(id, out var preview))
            {
                preview.Publish(new BrowserPreviewFrame(revision, width, height, pixels));
                return;
            }
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(pixels);
            throw;
        }
        ArrayPool<byte>.Shared.Return(pixels);
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
