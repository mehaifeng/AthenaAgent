using Athena.UI.Services.ComputerUse;
using Avalonia;
using Avalonia.Media;
using Avalonia.OpenGL;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;
using Avalonia.Threading;
using SkiaSharp;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Controls;

internal sealed class BrowserGpuPreview : IDisposable
{
    internal static X11PlatformOptions X11Options => OperatingSystem.IsLinux() && BrowserSurfaces.Requested
        ? new X11PlatformOptions { RenderingMode = [X11RenderingMode.Egl, X11RenderingMode.Glx, X11RenderingMode.Software] }
        : new X11PlatformOptions();

    private readonly Visual _target;
    private readonly TextureVisual _handler;
    private readonly CompositionCustomVisual _visual;
    private static readonly object Upload = new();
    private static readonly object Close = new();

    private BrowserGpuPreview(Visual target, Compositor compositor, Action wake)
    {
        _target = target;
        _handler = new TextureVisual(wake);
        _visual = compositor.CreateCustomVisual(_handler);
    }

    public string? Failure => _handler.Failure;
    public bool IsVisible => ReferenceEquals(ElementComposition.GetElementChildVisual(_target), _visual);
    public void ReportFailure(Exception exception) => _handler.ReportFailure(exception);

    public static async Task<BrowserGpuPreview?> TryCreateAsync(Visual target, Action wake)
    {
        var compositor = ElementComposition.GetElementVisual(target)?.Compositor;
        if (compositor is null) return null;
        // Avalonia does not expose DMA-BUF as an interop handle. Import it with the render thread's EGL context.
        if (OperatingSystem.IsLinux())
        {
            if (await compositor.TryGetRenderInterfaceFeature(typeof(IOpenGlTextureSharingRenderInterfaceContextFeature)) is null)
                return null;
        }
        else if (await compositor.TryGetCompositionGpuInterop() is not { } interop
            || !interop.SupportedImageHandleTypes.Contains(KnownPlatformGraphicsExternalImageHandleTypes.IOSurfaceRef, StringComparer.Ordinal))
            return null;
        return new(target, compositor, wake);
    }

    public async Task UploadAsync(BrowserPreviewFrame frame, Action presented)
    {
        var texture = frame.TakeTexture();
        if (OperatingSystem.IsMacOS())
            texture = await Task.Run(() => CopyMetal(texture));
        if (_handler.Publish(new TextureFrame(texture, frame.Width, frame.Height, presented)))
            _visual.SendHandlerMessage(Upload);
    }

    private static BrowserTexture CopyMetal(BrowserTexture source)
    {
        using (source)
        {
            var copy = BrowserSurfaces.SnapshotMetal(source.DangerousGetHandle(), out var timeout);
            if (timeout != 0) source.KeepProducerLease();
            return copy != IntPtr.Zero ? new BrowserTexture(copy)
                : throw new InvalidOperationException("The browser IOSurface could not be copied on the GPU.");
        }
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

    private sealed class TextureFrame(BrowserTexture texture, int width, int height, Action presented) : IDisposable
    {
        private Action? _presented = presented;

        public void OnPresented()
        {
            if (Interlocked.Exchange(ref _presented, null) is { } callback)
                Dispatcher.UIThread.Post(callback);
        }

        public SKImage Import(GRContext context)
        {
            if (OperatingSystem.IsLinux())
            {
                using (texture)
                {
                    if (context.Backend != GRBackend.OpenGL)
                        throw new InvalidOperationException("DMA-BUF preview requires the EGL/OpenGL renderer.");
                    var name = BrowserSurfaces.SnapshotGl(texture.DangerousGetHandle(), out var keepLease);
                    if (keepLease != 0) texture.KeepProducerLease();
                    if (name == 0) throw new InvalidOperationException("Could not import and copy the browser DMA-BUF with EGL.");
                    try
                    {
                        using var backend = new GRBackendTexture(width, height, false, new GRGlTextureInfo(0x0DE1, name, 0x8058)); // GL_TEXTURE_2D, GL_RGBA8
                        return SKImage.FromAdoptedTexture(context, backend, GRSurfaceOrigin.TopLeft, SKColorType.Rgba8888)
                            ?? throw new InvalidOperationException("Skia could not adopt the browser's OpenGL texture.");
                    }
                    catch
                    {
                        BrowserSurfaces.DeleteGlTextures(1, in name);
                        throw;
                    }
                }
            }
            if (context.Backend != GRBackend.Metal)
                throw new InvalidOperationException("IOSurface preview requires the Metal renderer.");
            using var metal = new GRBackendTexture(width, height, false, new GRMtlTextureInfo(texture.DangerousGetHandle()));
            return SKImage.FromTexture(context, metal, GRSurfaceOrigin.TopLeft, SKColorType.Bgra8888)
                ?? throw new InvalidOperationException("Skia could not import the browser's Metal texture.");
        }

        public void Dispose() => texture.Dispose();
    }

    private sealed class TextureVisual(Action wake) : CompositionCustomVisualHandler
    {
        private TextureFrame? _pending;
        private TextureFrame? _current;
        private SKImage? _image;
        private IPlatformGraphicsContext? _glContext;
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
            // Handler messages run before Avalonia binds the target. Skia owns the adopted GL name.
            try
            {
                if (_image is not null)
                {
                    using var current = _glContext is { IsLost: false } gl ? gl.EnsureCurrent() : null;
                    _image.Dispose();
                }
            }
            catch (PlatformGraphicsContextLostException exception) { _image?.Dispose(); ReportFailure(exception); }
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
                if (context is null)
                    throw new InvalidOperationException("The Avalonia renderer has no GPU context.");
                if (_glContext?.IsLost == true)
                    throw new InvalidOperationException("The browser preview lost its OpenGL context.");
                if (OperatingSystem.IsLinux() && _glContext is null)
                {
                    using var platform = lease.TryLeasePlatformGraphicsApi();
                    _glContext = platform?.Context ?? throw new InvalidOperationException("The compositor has no OpenGL context for texture cleanup.");
                }
                _image ??= frame.Import(context);
                var size = EffectiveSize;
                lease.SkCanvas.DrawImage(_image, new SKRect(0, 0, (float)size.X, (float)size.Y),
                    new SKSamplingOptions(SKFilterMode.Linear));
                frame.OnPresented();
            }
            catch (Exception exception) { ReportFailure(exception); }
        }
    }
}
