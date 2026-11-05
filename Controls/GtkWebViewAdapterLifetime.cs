using Avalonia.Controls;
using Avalonia.Platform;
using Serilog;
using System;
using System.Threading;

namespace Athena.UI.Controls;

/// <summary>
/// A borrowed WebKit pointer is valid only while its adapter is alive. Avalonia raises
/// AdapterDestroyed on the UI thread before it queues the GTK destruction on the GLib thread.
/// </summary>
internal sealed class GtkWebViewAdapterLifetime
{
    private readonly IGtkWebViewPlatformHandle _adapter;
    private readonly nint _webKit;
    private int _active = 1;

    private GtkWebViewAdapterLifetime(IGtkWebViewPlatformHandle adapter, nint webKit)
    {
        _adapter = adapter;
        _webKit = webKit;
    }

    internal string? HandleDescriptor => _adapter.HandleDescriptor;
    internal bool IsActive => Volatile.Read(ref _active) != 0;

    /// <summary>
    /// Only call this from the GLib thread. If destruction starts after the check, the
    /// adapter's queued destruction cannot run until this GLib callback has returned.
    /// </summary>
    internal bool Use(Func<nint, bool> action) => IsActive && action(_webKit);

    private void Invalidate() => Interlocked.Exchange(ref _active, 0);

    internal static void Observe(NativeWebView webView, Action<GtkWebViewAdapterLifetime> created,
        Action<GtkWebViewAdapterLifetime>? destroyed = null)
    {
        GtkWebViewAdapterLifetime? current = null;

        void EndCurrent()
        {
            var previous = current;
            if (previous == null) return;
            current = null;
            previous.Invalidate();
            destroyed?.Invoke(previous);
        }

        webView.AdapterCreated += (_, e) =>
        {
            var handle = e.TryGetPlatformHandle();
            if (current != null && ReferenceEquals(current._adapter, handle)) return;
            EndCurrent();
            if (handle is not IGtkWebViewPlatformHandle gtk) return;
            var webKit = gtk.WebKitWebView;
            if (webKit == IntPtr.Zero)
            {
                Log.Warning("The GTK web view adapter was created without a WebKit handle");
                return;
            }
            current = new GtkWebViewAdapterLifetime(gtk, webKit);
            created(current);
        };
        webView.AdapterDestroyed += (_, e) =>
        {
            // A late notification for an earlier adapter must not cancel its replacement.
            if (current != null && ReferenceEquals(current._adapter, e.TryGetPlatformHandle())) EndCurrent();
        };
    }
}
