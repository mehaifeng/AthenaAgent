using System;
using System.Buffers;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Athena.UI.Services.ComputerUse;

internal sealed class BrowserPreview(BrowserHostConnection connection, string tab, int id) : IDisposable
{
    private readonly Channel<bool> _updates = Channel.CreateBounded<bool>(1);
    private readonly object _gate = new();
    private BrowserPreviewFrame? _frame;
    private bool _complete;

    public Task ConfigureAsync(int revision, CancellationToken token) =>
        connection.CallAsync("previewStart", new { id, revision }, token, tab);

    public Task AcknowledgeAsync(CancellationToken token) =>
        connection.CallAsync("previewAck", new { id }, token, tab);

    public Task StopAsync(CancellationToken token) =>
        connection.CallAsync("previewStop", new { id }, token, tab);

    public void Signal() => _updates.Writer.TryWrite(true);

    public ValueTask<bool> WaitAsync(CancellationToken token) => _updates.Reader.ReadAsync(token);

    public BrowserPreviewFrame? TakeFrame()
    {
        lock (_gate)
        {
            var frame = _frame;
            _frame = null;
            return frame;
        }
    }

    internal void Publish(BrowserPreviewFrame frame)
    {
        lock (_gate)
        {
            if (_complete)
            {
                frame.Dispose();
                return;
            }
            _frame?.Dispose();
            _frame = frame;
        }
        Signal();
    }

    internal void Complete(Exception? error = null)
    {
        lock (_gate)
        {
            _complete = true;
            _frame?.Dispose();
            _frame = null;
        }
        _updates.Writer.TryComplete(error);
    }

    public void Dispose()
    {
        connection.ClosePreview(id);
        Complete();
    }
}

internal sealed class BrowserPreviewFrame(int revision, int width, int height, byte[] pixels) : IDisposable
{
    private byte[]? _pixels = pixels;

    public int Revision { get; } = revision;
    public int Width { get; } = width;
    public int Height { get; } = height;
    public byte[] Pixels => _pixels ?? throw new ObjectDisposedException(nameof(BrowserPreviewFrame));

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _pixels, null) is { } buffer)
            ArrayPool<byte>.Shared.Return(buffer);
    }
}
