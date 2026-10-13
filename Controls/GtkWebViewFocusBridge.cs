using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.X11.Interop;
using Serilog;
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Athena.UI.Controls;

/// <summary>
/// Linux 上让内嵌的 WebKitGTK 用得了输入法。
/// <para>
/// Avalonia 的 GTK 适配器（<c>GtkX11WebViewAdapter</c>）新建一个 GTK 顶层窗口放 WebKit，再用 XReparentWindow 塞进
/// Avalonia 自己的 X11 窗口。窗口管理器把焦点给的是 Avalonia 的顶层，GDK 不知道那是它的父窗口，于是这个 GTK 窗口
/// 永远不是"活动窗口"：WebKit 认为自己没焦点（页面里 <c>document.hasFocus()</c> 恒为 false），从不调用输入法上下文的
/// focus_in，ibus 也就不接这个上下文的按键——按键原样落进输入框，英文能打，拼音只出字母。macOS 的 WKWebView 是
/// 原生子视图，没有这一层，所以只有 Linux 坏。
/// </para>
/// <para>
/// 修法是 XEmbed 里 GtkPlug 做的那件事：由宿主告诉嵌入窗口它有没有顶层焦点——往这个 GTK 窗口投一个合成的
/// GDK_FOCUS_CHANGE。获得：用户在网页里按下鼠标或按键（X 把按键发给指针下、焦点窗口里的那个子窗口，所以这正是
/// 按键实际到达网页的时刻）、或 Avalonia 窗口重新激活而焦点仍在网页上；失去：Avalonia 的焦点移到别的控件、或窗口
/// 失活。不在 Avalonia 窗口一激活就给，因为 ibus 同时只服务一个上下文，那样会抢走 Avalonia 自己文本框的输入法。
/// 复现与验证（同一套重挂方式，注入 GDK 按键 n-i-h-a-o-空格，Rime 引擎）：修前输入框得到 "nihao "，修后得到「你好」。
/// </para>
/// </summary>
internal static class GtkWebViewFocusBridge
{
    private static readonly ILogger Logger = Log.ForContext(typeof(GtkWebViewFocusBridge));

    private const int GdkFocusChange = 12;   // GDK_FOCUS_CHANGE

    public static void Attach(NativeWebView webView)
    {
        if (!OperatingSystem.IsLinux()) return;
        webView.AdapterCreated += (_, _) => _ = HookAsync(webView);
    }

    private static async Task HookAsync(NativeWebView webView)
    {
        try
        {
            // WPE 或离屏适配器走 Avalonia 自己的输入法路径，没有这个问题，也没有 GTK 顶层可给焦点
            if (webView.TryGetPlatformHandle() is not IGtkWebViewPlatformHandle gtk || gtk.HandleDescriptor != "XID") return;
            var webKit = gtk.WebKitWebView;
            var hooked = await GtkInteropHelper.RunOnGlibThread(() =>
            {
                var toplevel = gtk_widget_get_toplevel(webKit);
                if (toplevel == IntPtr.Zero) return false;
                g_signal_connect_data(webKit, "button-press-event", OnButtonPressPtr, toplevel, IntPtr.Zero, 0);
                // 接在顶层窗口上：GtkWindow 的默认处理才把按键交给焦点控件和输入法，连接的处理器先于它运行
                g_signal_connect_data(toplevel, "key-press-event", OnKeyPressPtr, toplevel, IntPtr.Zero, 0);
                return true;
            });
            if (!hooked)
            {
                Logger.Warning("The embedded web view has no GTK toplevel; input methods will not reach the page");
                return;
            }

            Logger.Information("Embedded WebKitGTK focus bridge attached (input methods follow the host's focus)");
            var topLevel = TopLevel.GetTopLevel(webView) as WindowBase;
            webView.LostFocus += (_, _) => SetFocusAsync(webKit, false);
            if (topLevel != null)
            {
                topLevel.Deactivated += (_, _) => SetFocusAsync(webKit, false);
                topLevel.Activated += (_, _) => { if (webView.IsFocused) SetFocusAsync(webKit, true); };
            }
        }
        catch (Exception ex)
        {
            // 只是输入法补丁：失败了页面照常能用（英文能打），记下来
            Logger.Warning(ex, "Hooking GTK focus for the embedded web view failed; input methods will not reach the page");
        }
    }

    private static async void SetFocusAsync(nint webKit, bool focused)
    {
        try
        {
            await GtkInteropHelper.RunOnGlibThread(() =>
            {
                var toplevel = gtk_widget_get_toplevel(webKit);
                if (toplevel != IntPtr.Zero && gtk_window_has_toplevel_focus(toplevel) != focused) PutFocusChange(toplevel, focused);
                return true;
            });
        }
        catch (Exception ex)
        {
            // WebView 已拆掉、GTK 已退出：没有焦点可给
            Logger.Debug(ex, "Setting GTK focus on the embedded web view failed");
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GtkEventHandler(nint widget, nint gdkEvent, nint data);

    // 静态字段持有委托：GTK 拿着它的函数指针直到进程结束，委托不能被回收
    private static readonly GtkEventHandler OnButtonPressDelegate = OnButtonPress;
    private static readonly GtkEventHandler OnKeyPressDelegate = OnKeyPress;
    private static readonly nint OnButtonPressPtr = Marshal.GetFunctionPointerForDelegate(OnButtonPressDelegate);
    private static readonly nint OnKeyPressPtr = Marshal.GetFunctionPointerForDelegate(OnKeyPressDelegate);

    /// <summary>GLib 线程：在网页里按下鼠标，而顶层还不是活动窗口——补一个获得焦点。返回 0：点击照常往下走。</summary>
    private static int OnButtonPress(nint widget, nint gdkEvent, nint toplevel)
    {
        try
        {
            if (!gtk_window_has_toplevel_focus(toplevel)) PutFocusChange(toplevel, true);
        }
        catch (Exception ex)
        {
            // 原生回调里的异常会直接终止进程：吞下并记录，点击照常往下走
            Logger.Warning(ex, "GTK focus bridge failed on a button press");
        }
        return 0;
    }

    /// <summary>
    /// GLib 线程：没点过网页就直接按键（比如鼠标停在城里按 T 开对话栏）。这一下若照常往下走，它会绕过输入法直接
    /// 落进输入框——拼音的第一个字母就漏了。所以先补焦点，再把这下按键复制一份排在后面重投，原事件到此为止（返回 1）。
    /// 复制出来的那份到达时顶层已是活动窗口，这里不再拦。
    /// </summary>
    private static int OnKeyPress(nint widget, nint gdkEvent, nint toplevel)
    {
        try
        {
            // 补不上焦点（窗口还没 realize）就放行：否则重投的那份又会被拦下，无限循环
            if (gtk_window_has_toplevel_focus(toplevel) || !PutFocusChange(toplevel, true)) return 0;
            var copy = gdk_event_copy(gdkEvent);
            gdk_event_put(copy);
            gdk_event_free(copy);
            return 1;
        }
        catch (Exception ex)
        {
            // 同上：不能让异常穿过原生回调；按键照常往下走（只是没有输入法）
            Logger.Warning(ex, "GTK focus bridge failed on a key press");
            return 0;
        }
    }

    private static bool PutFocusChange(nint toplevel, bool focusIn)
    {
        var window = gtk_widget_get_window(toplevel);
        if (window == IntPtr.Zero) return false;
        // GdkEventFocus：type(int) | window(GdkWindow*) | send_event(gint8) | in(gint16)；gdk_event_free 会 unref window
        var ev = gdk_event_new(GdkFocusChange);
        Marshal.WriteIntPtr(ev, IntPtr.Size, g_object_ref(window));
        Marshal.WriteByte(ev, 2 * IntPtr.Size, 1);
        Marshal.WriteInt16(ev, 2 * IntPtr.Size + 2, (short)(focusIn ? 1 : 0));
        gdk_event_put(ev);
        gdk_event_free(ev);
        return true;
    }

    [DllImport("libgtk-3.so.0")] private static extern nint gtk_widget_get_toplevel(nint widget);
    [DllImport("libgtk-3.so.0")] private static extern nint gtk_widget_get_window(nint widget);
    [DllImport("libgtk-3.so.0")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool gtk_window_has_toplevel_focus(nint window);
    [DllImport("libgdk-3.so.0")] private static extern nint gdk_event_new(int type);
    [DllImport("libgdk-3.so.0")] private static extern nint gdk_event_copy(nint gdkEvent);
    [DllImport("libgdk-3.so.0")] private static extern void gdk_event_put(nint gdkEvent);
    [DllImport("libgdk-3.so.0")] private static extern void gdk_event_free(nint gdkEvent);
    [DllImport("libgobject-2.0.so.0")] private static extern nint g_object_ref(nint obj);
    [DllImport("libgobject-2.0.so.0")] private static extern ulong g_signal_connect_data(nint instance, string signal, nint handler, nint data, nint destroyData, int flags);
}
