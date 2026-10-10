using System;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Serilog;

namespace Athena.UI.Services.Platform;

/// <summary>
/// 让 Linux 上的系统标题栏跟随应用的浅/深色主题。
///
/// Avalonia 在 <c>ActualThemeVariant</c> 变化时会调 <c>IWindowImpl.SetFrameThemeVariant</c>，
/// Win32 与 macOS 据此切换系统标题栏；但 X11 后端的实现是空函数（Avalonia 12.1.1 的
/// <c>X11Window.SetFrameThemeVariant</c>），于是 GNOME 上的标题栏永远跟着桌面主题走。
/// GNOME 的窗口管理器（Mutter）对 X11 窗口读取 <c>_GTK_THEME_VARIANT</c> 属性
/// （UTF8_STRING，"dark" / "light"）决定服务端装饰用哪套配色，并监听它的变化——
/// 这里就是替 Avalonia 补上这一步。
///
/// 只作用于 X11 句柄（<c>HandleDescriptor == "XID"</c>）；其他平台或拿不到句柄时什么都不做。
/// libX11 加载失败只记一次 Warning：标题栏配色是装饰，不能因此影响窗口本身。
/// </summary>
internal static class LinuxWindowFrameTheme
{
    private static IntPtr _display;
    private static bool _unavailable;

    /// <summary>在应用启动时调用一次：之后每个打开的窗口都会被跟踪。</summary>
    public static void Register()
    {
        if (!OperatingSystem.IsLinux()) return;
        Window.WindowOpenedEvent.AddClassHandler<Window>(OnWindowOpened);
    }

    /// <summary>Avalonia 主题 → <c>_GTK_THEME_VARIANT</c> 取值。纯函数，便于断言。</summary>
    internal static string ToGtkThemeVariant(ThemeVariant? variant) =>
        variant == ThemeVariant.Dark ? "dark" : "light";

    private static void OnWindowOpened(Window window, RoutedEventArgs e)
    {
        Apply(window);
        window.ActualThemeVariantChanged += (_, _) => Apply(window);
    }

    private static void Apply(Window window)
    {
        if (_unavailable) return;
        var handle = window.TryGetPlatformHandle();
        if (handle is not { HandleDescriptor: "XID" } || handle.Handle == IntPtr.Zero) return;
        try
        {
            if (_display == IntPtr.Zero)
            {
                _display = XOpenDisplay(IntPtr.Zero);
                if (_display == IntPtr.Zero)
                {
                    _unavailable = true;
                    Log.Warning("LinuxWindowFrameTheme: XOpenDisplay failed; the system title bar will not follow the app theme");
                    return;
                }
            }

            var value = Encoding.UTF8.GetBytes(ToGtkThemeVariant(window.ActualThemeVariant));
            var property = XInternAtom(_display, "_GTK_THEME_VARIANT", false);
            var utf8String = XInternAtom(_display, "UTF8_STRING", false);
            XChangeProperty(_display, handle.Handle, property, utf8String, 8, PropModeReplace, value, value.Length);
            XFlush(_display);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _unavailable = true;
            Log.Warning(ex, "LinuxWindowFrameTheme: libX11 is unavailable; the system title bar will not follow the app theme");
        }
    }

    private const int PropModeReplace = 0;

    [DllImport("libX11.so.6")]
    private static extern IntPtr XOpenDisplay(IntPtr displayName);

    [DllImport("libX11.so.6")]
    private static extern IntPtr XInternAtom(IntPtr display, string atomName, bool onlyIfExists);

    [DllImport("libX11.so.6")]
    private static extern int XChangeProperty(IntPtr display, IntPtr window, IntPtr property, IntPtr type,
        int format, int mode, byte[] data, int elementCount);

    [DllImport("libX11.so.6")]
    private static extern int XFlush(IntPtr display);
}
