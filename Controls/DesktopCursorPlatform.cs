using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace Athena.UI.Controls;

// Hit testing has to pass through to windows in other processes, including native Wayland clients.
internal static class DesktopCursorPlatform
{
    internal static void MakeClickThrough(Window window, bool desktopWide = true)
    {
        var handle = window.TryGetPlatformHandle() ?? throw new InvalidOperationException("No native cursor window.");
        if (OperatingSystem.IsWindows() && handle.HandleDescriptor == "HWND")
        {
            const uint layered = 0x80000, transparent = 0x20, noActivate = 0x08000000, toolWindow = 0x80;
            Win32Properties.AddWindowStylesCallback(window, (style, extended) =>
                (style, extended | layered | transparent | noActivate | toolWindow));
        }
        else if (OperatingSystem.IsMacOS() && handle.HandleDescriptor is "NSWindow" or "NSView")
        {
            var nsWindow = handle.HandleDescriptor == "NSWindow" ? handle.Handle : Send(handle.Handle, Selector("window"));
            if (nsWindow == 0) throw new InvalidOperationException("No Cocoa cursor window.");
            SendBool(nsWindow, Selector("setIgnoresMouseEvents:"), 1);
            SendBool(nsWindow, Selector("setHasShadow:"), 0);
            if (desktopWide)
            {
                // Join the active Space, including full-screen apps, and skip window cycling.
                SendNumber(nsWindow, Selector("setCollectionBehavior:"), 1 | (1u << 3) | (1u << 6) | (1u << 8));
            }
            else
            {
                // Owned child window for the embedded browser: stay at normal window level
                // (never float above unrelated third-party apps) and skip Cmd+` cycling.
                SendNumber(nsWindow, Selector("setCollectionBehavior:"), 1u << 6);
                SendSigned(nsWindow, Selector("setLevel:"), 0); // NSNormalWindowLevel
            }
        }
        else if (OperatingSystem.IsLinux() && handle.HandleDescriptor == "XID")
        {
            nint display = XOpenDisplay(0);
            if (display == 0) throw new InvalidOperationException("No X11 display for cursor presentation.");
            try
            {
                if (XShapeQueryExtension(display, out _, out _) == 0)
                    throw new InvalidOperationException("A click-through cursor requires the X Shape extension.");
                if (desktopWide)
                {
                    var attributes = new XWindowAttributes { OverrideRedirect = 1 };
                    XChangeWindowAttributes(display, handle.Handle, 1u << 9, ref attributes);
                }
                XShapeCombineRectangles(display, handle.Handle, 2, 0, 0, 0, 0, 0, 0);
                XSync(display, 0);
            }
            finally { XCloseDisplay(display); }
        }
        else throw new NotSupportedException("No click-through cursor surface is available on this window backend.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XWindowAttributes
    {
        internal nuint BackgroundPixmap, BackgroundPixel, BorderPixmap, BorderPixel;
        internal int BitGravity, WinGravity, BackingStore;
        internal nuint BackingPlanes, BackingPixel;
        internal int SaveUnder;
        internal nint EventMask, DoNotPropagateMask;
        internal int OverrideRedirect;
        internal nuint Colormap, Cursor;
    }

    [DllImport("libX11.so.6")] private static extern nint XOpenDisplay(nint name);
    [DllImport("libX11.so.6")] private static extern int XCloseDisplay(nint display);
    [DllImport("libX11.so.6")] private static extern int XSync(nint display, int discard);
    [DllImport("libX11.so.6")] private static extern int XChangeWindowAttributes(nint display, nint window, nuint mask, ref XWindowAttributes attributes);
    [DllImport("libXext.so.6")] private static extern int XShapeQueryExtension(nint display, out int eventBase, out int errorBase);
    [DllImport("libXext.so.6")] private static extern void XShapeCombineRectangles(nint display, nint window, int kind, int x, int y, nint rectangles, int count, int operation, int ordering);
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    [DllImport(ObjC, EntryPoint = "sel_registerName")] private static extern nint Selector(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint Send(nint obj, nint selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendBool(nint obj, nint selector, byte value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendNumber(nint obj, nint selector, nuint value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendSigned(nint obj, nint selector, nint value);
}
