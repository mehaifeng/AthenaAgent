using System;
using System.Buffers;
using Microsoft.Win32.SafeHandles;
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

    public Task ConfigureAsync(int revision, CancellationToken token, bool sharedTextures = false, int? frameRate = null) =>
        connection.CallAsync("previewStart", new { id, revision, sharedTextures, frameRate }, token, tab);

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
    private BrowserTexture? _texture = surface != IntPtr.Zero ? new BrowserTexture(surface, releaseTexture) : null;

    public int Revision { get; } = revision;
    public int Width { get; } = width;
    public int Height { get; } = height;
    public bool HasTexture => _texture is not null;
    public byte[] Pixels => _pixels ?? throw new ObjectDisposedException(nameof(BrowserPreviewFrame));

    public BrowserTexture TakeTexture() => Interlocked.Exchange(ref _texture, null)
        ?? throw new InvalidOperationException("The preview frame has no shared texture.");

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _pixels, null) is { } buffer)
            ArrayPool<byte>.Shared.Return(buffer);
        Interlocked.Exchange(ref _texture, null)?.Dispose();
    }
}

internal sealed class BrowserTexture : SafeHandleZeroOrMinusOneIsInvalid
{
    private Action? _acknowledge;

    public BrowserTexture(IntPtr reference, Action? acknowledge = null) : base(true)
    {
        SetHandle(reference);
        _acknowledge = acknowledge;
    }

    // Do not let Chromium overwrite the source if a submitted GPU copy has not finished.
    public void KeepProducerLease() => Interlocked.Exchange(ref _acknowledge, null);

    protected override bool ReleaseHandle()
    {
        BrowserSurfaces.Release(handle);
        Interlocked.Exchange(ref _acknowledge, null)?.Invoke();
        return true;
    }
}
