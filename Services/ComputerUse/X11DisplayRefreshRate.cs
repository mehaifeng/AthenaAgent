using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Platform;

namespace Athena.UI.Services.ComputerUse;

internal static class X11DisplayRefreshRate
{
    // Electron on native Wayland can report a displayFrequency of zero. The Athena window runs on
    // X11/XWayland, so use the active RandR mode of the output containing the selected monitor's center.
    public static int? Read(Screen? screen)
    {
        if (!OperatingSystem.IsLinux() || screen?.TryGetPlatformHandle()?.HandleDescriptor != "XRandRMonitorName")
            return null;
        try { return ReadAt(screen.Bounds.Center); }
        catch (DllNotFoundException) { return null; }
        catch (EntryPointNotFoundException) { return null; }
    }

    private static int? ReadAt(PixelPoint center)
    {
        var display = XOpenDisplay(0);
        if (display == 0) return null;
        try
        {
            var resources = XRRGetScreenResourcesCurrent(display, XDefaultRootWindow(display));
            if (resources == 0) return null;
            try
            {
                var modes = Marshal.PtrToStructure<ScreenResources>(resources);
                for (var i = 0; i < modes.CrtcCount; i++)
                {
                    var crtc = XRRGetCrtcInfo(display, resources, Marshal.ReadIntPtr(modes.Crtcs, i * IntPtr.Size));
                    if (crtc == 0) continue;
                    CrtcInfo info;
                    try { info = Marshal.PtrToStructure<CrtcInfo>(crtc); }
                    finally { XRRFreeCrtcInfo(crtc); }
                    if (info.Mode == 0 || !new PixelRect(info.X, info.Y, info.Width, info.Height).ContainsExclusive(center))
                        continue;
                    for (var j = 0; j < modes.ModeCount; j++)
                    {
                        var mode = Marshal.PtrToStructure<ModeInfo>(modes.Modes + j * Marshal.SizeOf<ModeInfo>());
                        if (mode.Id != info.Mode || mode.DotClock == 0 || mode.HTotal == 0 || mode.VTotal == 0) continue;
                        var hz = (double)mode.DotClock / mode.HTotal / mode.VTotal;
                        if ((mode.Flags & 0x10) != 0) hz *= 2; // RR_Interlace
                        if ((mode.Flags & 0x20) != 0) hz /= 2; // RR_DoubleScan
                        return (int)Math.Round(hz);
                    }
                }
            }
            finally { XRRFreeScreenResources(resources); }
        }
        finally { XCloseDisplay(display); }
        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScreenResources
    {
        public nuint Timestamp, ConfigTimestamp;
        public int CrtcCount;
        public nint Crtcs;
        public int OutputCount;
        public nint Outputs;
        public int ModeCount;
        public nint Modes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CrtcInfo
    {
        public nuint Timestamp;
        public int X, Y, Width, Height;
        public nuint Mode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ModeInfo
    {
        public nuint Id;
        public uint Width, Height;
        public nuint DotClock;
        public uint HSyncStart, HSyncEnd, HTotal, HSkew, VSyncStart, VSyncEnd, VTotal;
        public nint Name;
        public uint NameLength;
        public nuint Flags;
    }

    [DllImport("libX11.so.6")] private static extern nint XOpenDisplay(nint name);
    [DllImport("libX11.so.6")] private static extern int XCloseDisplay(nint display);
    [DllImport("libX11.so.6")] private static extern nuint XDefaultRootWindow(nint display);
    [DllImport("libXrandr.so.2")] private static extern nint XRRGetScreenResourcesCurrent(nint display, nuint root);
    [DllImport("libXrandr.so.2")] private static extern void XRRFreeScreenResources(nint resources);
    [DllImport("libXrandr.so.2")] private static extern nint XRRGetCrtcInfo(nint display, nint resources, nint crtc);
    [DllImport("libXrandr.so.2")] private static extern void XRRFreeCrtcInfo(nint crtc);
}
