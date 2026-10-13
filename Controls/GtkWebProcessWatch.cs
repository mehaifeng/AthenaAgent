using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.X11.Interop;
using Serilog;
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Athena.UI.Controls;

/// <summary>
/// Linux 上盯住 WebKitGTK 的网页进程。<see cref="NativeWebView"/> 在任何平台都不报"浏览器进程没了"：
/// WebKit 发 <c>web-process-terminated</c>（崩溃，或超出内存上限被它自己终止），画面变白，此后每次
/// <c>InvokeScript</c> 都以 "Unsupported result type" 失败——而宿主只当推送失败记一条 Debug，游戏区就这么永远白着，
/// 切换工作区也救不回来（2026-10-13 实际发生过，系统里既无核心转储也无内核 OOM）。这里直接接那个 GTK 信号。
/// </summary>
internal static class GtkWebProcessWatch
{
    private static readonly ILogger Logger = Log.ForContext(typeof(GtkWebProcessWatch));

    /// <summary>
    /// 接上信号。返回的对象由调用方持有到页面释放：GTK 那边只拿着它的弱引用句柄，页面没了回调自然作废，
    /// 不会因为一个信号把整个 WebView 控件钉在内存里。
    /// </summary>
    public static object? Attach(NativeWebView webView, Action<string> onTerminated)
    {
        if (!OperatingSystem.IsLinux()) return null;
        var state = new State(onTerminated);
        webView.AdapterCreated += (_, _) => _ = HookAsync(webView, state);
        return state;
    }

    private sealed class State(Action<string> onTerminated)
    {
        public Action<string> OnTerminated { get; } = onTerminated;
    }

    private static async Task HookAsync(NativeWebView webView, State state)
    {
        try
        {
            if (webView.TryGetPlatformHandle() is not IGtkWebViewPlatformHandle gtk) return;
            var webKit = gtk.WebKitWebView;
            var handle = GCHandle.ToIntPtr(GCHandle.Alloc(state, GCHandleType.Weak));
            await GtkInteropHelper.RunOnGlibThread(() => g_signal_connect_data(webKit, "web-process-terminated", OnTerminatedPtr, handle, IntPtr.Zero, 0));
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Watching the embedded web view's process failed; a crashed page will not be reported");
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void TerminatedHandler(nint webView, int reason, nint data);

    // 静态字段持有委托：GTK 拿着它的函数指针直到进程结束
    private static readonly TerminatedHandler OnTerminatedDelegate = OnTerminated;
    private static readonly nint OnTerminatedPtr = Marshal.GetFunctionPointerForDelegate(OnTerminatedDelegate);

    /// <summary>GLib 线程。原生回调里的异常会直接终止进程，所以全部接住。</summary>
    private static void OnTerminated(nint webView, int reason, nint data)
    {
        try
        {
            // WebKitWebProcessTerminationReason
            var text = reason switch
            {
                0 => "crashed",
                1 => "exceeded-memory-limit",
                2 => "terminated-by-api",
                _ => $"reason {reason}"
            };
            Logger.Warning("The embedded WebKit web process terminated: {Reason}", text);
            if (GCHandle.FromIntPtr(data).Target is State state) state.OnTerminated($"WebKit web process terminated: {text}");
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Reporting a terminated WebKit web process failed");
        }
    }

    [DllImport("libgobject-2.0.so.0")] private static extern ulong g_signal_connect_data(nint instance, string signal, nint handler, nint data, nint destroyData, int flags);
}
