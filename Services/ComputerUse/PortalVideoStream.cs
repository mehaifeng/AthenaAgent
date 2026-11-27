using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Athena.UI.Services.ComputerUse;

// GStreamer consumes only the PipeWire node on the portal-provided connection. No global PipeWire client.
internal sealed class PortalVideoStream : IDisposable
{
    private const string Gst = "libgstreamer-1.0.so.0", GObject = "libgobject-2.0.so.0", GLib = "libglib-2.0.so.0";
    private nint _pipeline, _sink, _bus, _sourcePad;

    public PortalVideoStream(SafeFileHandle remote, uint node, int logicalWidth, int logicalHeight)
    {
        try
        {
            if (!gst_init_check(0, 0, out var error)) throw NativeError(error, "GStreamer initialization failed");
            double scale = Math.Min(1d, 1440d / Math.Max(logicalWidth, logicalHeight));
            int width = Math.Max(1, (int)Math.Round(logicalWidth * scale)), height = Math.Max(1, (int)Math.Round(logicalHeight * scale));
            string pipeline = $"pipewiresrc name=monitor fd={remote.DangerousGetHandle().ToInt32()} path={node} keepalive-time=160 ! "
                + "queue max-size-buffers=1 leaky=downstream ! videorate drop-only=true max-rate=6 ! videoconvert ! "
                + $"videoscale add-borders=false ! video/x-raw,format=I420,width={width},height={height},pixel-aspect-ratio=1/1 ! "
                + "jpegenc quality=90 ! appsink name=frames max-buffers=1 drop=true sync=false";
            _pipeline = gst_parse_launch(pipeline, out error);
            if (error != 0 || _pipeline == 0) throw NativeError(error, "The GNOME PipeWire/GStreamer capture pipeline is unavailable");
            _sink = gst_bin_get_by_name(_pipeline, "frames");
            var source = gst_bin_get_by_name(_pipeline, "monitor");
            if (source != 0)
            {
                _sourcePad = gst_element_get_static_pad(source, "src");
                gst_object_unref(source);
            }
            _bus = gst_element_get_bus(_pipeline);
            if (_sourcePad == 0 || _sink == 0 || gst_element_set_state(_pipeline, 4) == 0)
                throw new InvalidOperationException("Could not start capture of the shared monitor.");
        }
        catch { Dispose(); throw; }
    }

    internal (int Width, int Height) GetSourceSize()
    {
        nint caps = gst_pad_get_current_caps(_sourcePad);
        try
        {
            if (caps != 0)
            {
                nint video = gst_caps_get_structure(caps, 0);
                if (gst_structure_get_int(video, "width", out int width) && width > 0
                    && gst_structure_get_int(video, "height", out int height) && height > 0) return (width, height);
            }
            throw new InvalidOperationException("The portal did not negotiate a monitor size for pointer input.");
        }
        finally { if (caps != 0) gst_mini_object_unref(caps); }
    }

    internal async Task WaitForNextBufferAsync(CancellationToken token)
    {
        // A cached JPEG may still contain the cursor window that the compositor just unmapped.
        // Retain the sample so the buffer address cannot be reused while comparing frames.
        g_object_get(_sink, "last-sample", out var old, 0);
        if (old == 0) return;
        try
        {
            var previous = gst_sample_get_buffer(old);
            for (int i = 0; i < 120; i++)
            {
                await Task.Delay(16, token);
                g_object_get(_sink, "last-sample", out var current, 0);
                if (current == 0) continue;
                try { if (gst_sample_get_buffer(current) != previous) return; }
                finally { gst_mini_object_unref(current); }
            }
            throw new TimeoutException("The shared monitor did not deliver a fresh frame after hiding the Athena cursor.");
        }
        finally { gst_mini_object_unref(old); }
    }

    public async Task<ComputerUseFrame> ReadAsync(CancellationToken token)
    {
        for (int attempt = 0; attempt < 160; attempt++)
        {
            token.ThrowIfCancellationRequested();
            nint error = gst_bus_pop_filtered(_bus, 2); // GST_MESSAGE_ERROR
            if (error != 0)
            {
                gst_message_parse_error(error, out var reason, out var debug);
                if (debug != 0) g_free(debug);
                gst_mini_object_unref(error);
                throw NativeError(reason, "The shared monitor stream failed");
            }
            // GstBaseSink retains the latest sample even when an idle compositor produces no new frame.
            g_object_get(_sink, "last-sample", out var sample, 0);
            if (sample == 0) { await Task.Delay(60, token); continue; }
            try
            {
                var buffer = gst_sample_get_buffer(sample);
                var caps = gst_caps_get_structure(gst_sample_get_caps(sample), 0);
                var length = gst_buffer_get_size(buffer);
                if (length is 0 or > 12 * 1024 * 1024 || !gst_structure_get_int(caps, "width", out int width)
                    || !gst_structure_get_int(caps, "height", out int height) || width < 1 || height < 1)
                    throw new InvalidOperationException("The shared monitor returned an unusable frame.");
                var bytes = new byte[(int)length];
                if (gst_buffer_extract(buffer, 0, bytes, length) != length)
                    throw new InvalidOperationException("Incomplete shared monitor frame.");
                return new(bytes, "image/jpeg", width, height, "GNOME desktop. Keyboard input goes to the application with focus.",
                    null, [], IsDesktop: true);
            }
            finally { gst_mini_object_unref(sample); }
        }
        throw new TimeoutException("The shared monitor did not deliver a PipeWire frame.");
    }

    private static InvalidOperationException NativeError(nint error, string context)
    {
        if (error == 0) return new(context);
        try { return new($"{context}: {Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(error, 8))}"); }
        finally { g_error_free(error); }
    }

    public void Dispose()
    {
        if (_pipeline != 0) gst_element_set_state(_pipeline, 1); // GST_STATE_NULL
        if (_sink != 0) gst_object_unref(_sink);
        if (_bus != 0) gst_object_unref(_bus);
        if (_sourcePad != 0) gst_object_unref(_sourcePad);
        if (_pipeline != 0) gst_object_unref(_pipeline);
        _pipeline = _sink = _bus = _sourcePad = 0;
    }

    [DllImport(Gst)] private static extern bool gst_init_check(nint argc, nint argv, out nint error);
    [DllImport(Gst)] private static extern nint gst_parse_launch(string description, out nint error);
    [DllImport(Gst)] private static extern nint gst_bin_get_by_name(nint bin, string name);
    [DllImport(Gst)] private static extern nint gst_element_get_static_pad(nint element, string name);
    [DllImport(Gst)] private static extern nint gst_pad_get_current_caps(nint pad);
    [DllImport(Gst)] private static extern nint gst_element_get_bus(nint element);
    [DllImport(Gst)] private static extern int gst_element_set_state(nint element, int state);
    [DllImport(Gst)] private static extern nint gst_bus_pop_filtered(nint bus, uint types);
    [DllImport(Gst)] private static extern void gst_message_parse_error(nint message, out nint error, out nint debug);
    [DllImport(GObject)] private static extern void g_object_get(nint obj, string property, out nint value, nint end);
    [DllImport(Gst)] private static extern nint gst_sample_get_buffer(nint sample);
    [DllImport(Gst)] private static extern nint gst_sample_get_caps(nint sample);
    [DllImport(Gst)] private static extern nint gst_caps_get_structure(nint caps, uint index);
    [DllImport(Gst)] private static extern bool gst_structure_get_int(nint structure, string field, out int value);
    [DllImport(Gst)] private static extern nuint gst_buffer_get_size(nint buffer);
    [DllImport(Gst)] private static extern nuint gst_buffer_extract(nint buffer, nuint offset, [Out] byte[] data, nuint length);
    [DllImport(Gst)] private static extern void gst_mini_object_unref(nint obj);
    [DllImport(Gst)] private static extern void gst_object_unref(nint obj);
    [DllImport(GLib)] private static extern void g_error_free(nint error);
    [DllImport(GLib)] private static extern void g_free(nint ptr);
}
