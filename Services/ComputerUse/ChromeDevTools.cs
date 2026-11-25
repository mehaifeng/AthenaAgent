using System;
using System.IO;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.ComputerUse;

internal sealed class ChromeDevTools : IDisposable
{
    private const int MaxReplyBytes = 16 * 1024 * 1024;
    private readonly ClientWebSocket _socket = new();
    private readonly SemaphoreSlim _commands = new(1, 1);
    private readonly byte[] _buffer = new byte[32 * 1024];
    private int _requestId;

    public bool IsOpen => _socket.State == WebSocketState.Open;

    public Task ConnectAsync(Uri address, CancellationToken token) => _socket.ConnectAsync(address, token);

    public async Task<JsonElement> CallAsync(string method, object parameters, CancellationToken token)
    {
        await _commands.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await SendAndReadAsync(method, parameters, token).ConfigureAwait(false);
        }
        finally
        {
            _commands.Release();
        }
    }

    private async Task<JsonElement> SendAndReadAsync(string method, object parameters, CancellationToken token)
    {
        var id = ++_requestId;
        var request = JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = parameters });
        await _socket.SendAsync(request.AsMemory(), WebSocketMessageType.Text, true, token).ConfigureAwait(false);
        using var message = new MemoryStream();
        while (true)
        {
            var read = await _socket.ReceiveAsync(_buffer.AsMemory(), token).ConfigureAwait(false);
            if (read.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException("Chrome closed the controlled tab.");
            if (message.Length + read.Count > MaxReplyBytes)
                throw new InvalidOperationException("Chrome returned an oversized page observation.");
            message.Write(_buffer, 0, read.Count);
            if (!read.EndOfMessage)
                continue;

            using var data = JsonDocument.Parse(message.GetBuffer().AsMemory(0, (int)message.Length));
            if (data.RootElement.TryGetProperty("id", out var replyId) && replyId.GetInt32() == id)
            {
                if (data.RootElement.TryGetProperty("error", out var error))
                    throw new ChromeDevToolsException(error.GetProperty("message").GetString() ?? "Unknown browser error.");
                return data.RootElement.GetProperty("result").Clone();
            }
            message.SetLength(0);
        }
    }

    public void Dispose() => _socket.Dispose();
}

internal sealed class ChromeDevToolsException(string message) : InvalidOperationException(message)
{
    public bool IsNavigation =>
        Message.Contains("Cannot find context", StringComparison.OrdinalIgnoreCase)
        || Message.Contains("context was destroyed", StringComparison.OrdinalIgnoreCase)
        || Message.Contains("Inspected target navigated", StringComparison.OrdinalIgnoreCase)
        || Message.Contains("No frame with given id", StringComparison.OrdinalIgnoreCase);
}
