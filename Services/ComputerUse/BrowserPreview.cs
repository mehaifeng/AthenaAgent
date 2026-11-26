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
    private int _viewportChanged;

    public Task ConfigureAsync(int revision, CancellationToken token, bool sharedTextures = false) =>
        connection.CallAsync("previewStart", new { id, revision, sharedTextures }, token, tab);

    public Task AcknowledgeAsync(CancellationToken token) =>
        connection.CallAsync("previewAck", new { id }, token, tab);

    public Task StopAsync(CancellationToken token) =>
        connection.CallAsync("previewStop", new { id }, token, tab);

    public void Signal() => _updates.Writer.TryWrite(true);

    internal void ReportViewportChange()
    {
        Interlocked.Exchange(ref _viewportChanged, 1);
        Signal();
    }

    public bool TakeViewportChange() => Interlocked.Exchange(ref _viewportChanged, 0) != 0;

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

internal sealed class BrowserPreviewFrame(int revision, int width, int height, byte[]? pixels,
    IntPtr surface = default, Action? releaseTexture = null) : IDisposable
{
    private byte[]? _pixels = pixels;
    private IntPtr _surface = surface;
    private Action? _releaseTexture = releaseTexture;

    public int Revision { get; } = revision;
    public int Width { get; } = width;
    public int Height { get; } = height;
    public IntPtr Surface => _surface;
    public byte[] Pixels => _pixels ?? throw new ObjectDisposedException(nameof(BrowserPreviewFrame));

    // If a GPU blit never completes, keep Chromium's lease rather than let it overwrite a surface in use.
    public void KeepProducerLease() => Interlocked.Exchange(ref _releaseTexture, null);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _pixels, null) is { } buffer)
            ArrayPool<byte>.Shared.Return(buffer);
        if (Interlocked.Exchange(ref _surface, IntPtr.Zero) is var reference && reference != IntPtr.Zero)
            MacBrowserSurfaces.Release(reference);
        Interlocked.Exchange(ref _releaseTexture, null)?.Invoke();
    }
}
