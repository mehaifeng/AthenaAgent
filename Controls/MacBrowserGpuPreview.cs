using Athena.UI.Services.ComputerUse;
using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;
using Avalonia.Threading;
using Microsoft.Win32.SafeHandles;
using SkiaSharp;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Controls;

internal sealed class MacBrowserGpuPreview : IDisposable
{
    private readonly Visual _target;
    private readonly TextureVisual _handler;
    private readonly CompositionCustomVisual _visual;
    private static readonly object Upload = new();
    private static readonly object Close = new();

    private MacBrowserGpuPreview(Visual target, Compositor compositor, Action wake)
    {
        _target = target;
        _handler = new TextureVisual(wake);
        _visual = compositor.CreateCustomVisual(_handler);
    }

    public string? Failure => _handler.Failure;
    public bool IsVisible => ReferenceEquals(ElementComposition.GetElementChildVisual(_target), _visual);
    public void ReportFailure(Exception exception) => _handler.ReportFailure(exception);

    public static async Task<MacBrowserGpuPreview?> TryCreateAsync(Visual target, Action wake)
    {
        var compositor = ElementComposition.GetElementVisual(target)?.Compositor;
        if (compositor is null || await compositor.TryGetCompositionGpuInterop() is not { } interop
            || !interop.SupportedImageHandleTypes.Contains(KnownPlatformGraphicsExternalImageHandleTypes.IOSurfaceRef, StringComparer.Ordinal))
            return null;
        return new(target, compositor, wake);
    }

    public async Task UploadAsync(BrowserPreviewFrame frame, Action presented)
    {
        var copy = await Task.Run(() =>
        {
            try
            {
                var snapshot = MacBrowserSurfaces.Snapshot(frame.Surface, out var timeout);
                if (timeout != 0) frame.KeepProducerLease();
                return snapshot;
            }
            // The renderer reads the independent copy. Let Chromium send the next frame in parallel.
            finally { frame.Dispose(); }
        });
        if (copy == IntPtr.Zero) throw new InvalidOperationException("The browser IOSurface could not be copied on the GPU.");
        if (_handler.Publish(new TextureFrame(copy, frame.Width, frame.Height, presented)))
            _visual.SendHandlerMessage(Upload);
    }

    public void Show()
    {
        _visual.Size = new Vector(_target.Bounds.Width, _target.Bounds.Height);
        if (!IsVisible)
            ElementComposition.SetElementChildVisual(_target, _visual);
    }

    public void Hide()
    {
        if (IsVisible)
            ElementComposition.SetElementChildVisual(_target, null);
    }

    public void Dispose()
    {
        Hide();
        _visual.SendHandlerMessage(Close);
    }

    private sealed class TextureFrame : SafeHandleZeroOrMinusOneIsInvalid
    {
        private Action? _presented;
        public int Width { get; }
        public int Height { get; }

        public TextureFrame(IntPtr texture, int width, int height, Action presented) : base(true)
        {
            SetHandle(texture);
            Width = width;
            Height = height;
            _presented = presented;
        }

        public void OnPresented()
        {
            if (Interlocked.Exchange(ref _presented, null) is { } callback)
                Dispatcher.UIThread.Post(callback);
        }

        protected override bool ReleaseHandle()
        {
            MacBrowserSurfaces.Release(handle);
            return true;
        }
    }

    private sealed class TextureVisual(Action wake) : CompositionCustomVisualHandler
    {
        private TextureFrame? _pending;
        private TextureFrame? _current;
        private SKImage? _image;
        private string? _failure;

        public string? Failure => Volatile.Read(ref _failure);

        public void ReportFailure(Exception exception)
        {
            if (Interlocked.CompareExchange(ref _failure, exception.Message, null) is null) wake();
        }

        public bool Publish(TextureFrame frame)
        {
            var previous = Interlocked.Exchange(ref _pending, frame);
            previous?.Dispose();
            return previous is null;
        }

        public override void OnMessage(object message)
        {
            _image?.Dispose();
            _image = null;
            _current?.Dispose();
            _current = Interlocked.Exchange(ref _pending, null);
            if (ReferenceEquals(message, Close))
            {
                _current?.Dispose();
                _current = null;
            }
            Invalidate();
        }

        public override void OnRender(ImmediateDrawingContext drawingContext)
        {
            if (_current is not { } frame || Failure is not null) return;
            try
            {
                var feature = drawingContext.TryGetFeature<ISkiaSharpApiLeaseFeature>()
                    ?? throw new InvalidOperationException("The Avalonia composition renderer does not expose its Skia context.");
                using var lease = feature.Lease();
                var context = lease.GrContext;
                if (context is null || context.Backend != GRBackend.Metal)
                    throw new InvalidOperationException("The browser GPU preview requires a Metal Skia context.");
                if (_image is null)
                {
                    using var texture = new GRBackendTexture(frame.Width, frame.Height, false, new GRMtlTextureInfo(frame.DangerousGetHandle()));
                    _image = SKImage.FromTexture(context, texture, GRSurfaceOrigin.TopLeft, SKColorType.Bgra8888)
                        ?? throw new InvalidOperationException("Skia could not import the browser's Metal texture.");
                }
                var size = EffectiveSize;
                lease.SkCanvas.DrawImage(_image, new SKRect(0, 0, (float)size.X, (float)size.Y),
                    new SKSamplingOptions(SKFilterMode.Linear));
                frame.OnPresented();
            }
            catch (Exception exception) { ReportFailure(exception); }
        }
    }
}
