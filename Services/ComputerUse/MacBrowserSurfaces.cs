using Serilog;
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Athena.UI.Services.ComputerUse;

internal sealed class MacBrowserSurfaces(uint port, string addon, string service) : IDisposable
{
    internal const string DirectoryVariable = "ATHENA_BROWSER_GPU_DIRECTORY";
    private static readonly Lazy<NativeApi?> Api = new(Load);
    internal static bool Requested => OperatingSystem.IsMacOSVersionAtLeast(12)
        && Environment.GetEnvironmentVariable(DirectoryVariable) is { Length: > 0 };
    public string Addon { get; } = addon;
    public string Service { get; } = service;

    public static MacBrowserSurfaces? TryOpen()
    {
        if (Api.Value is not { } api) return null;
        var name = $"com.athena.browser.surface.{Guid.NewGuid():N}";
        var receive = api.Open(name);
        if (receive != 0) return new(receive, api.Addon, name);
        Log.Warning("Could not open the Mach port for the experimental browser preview");
        return null;
    }

    public IntPtr Read(int pid, uint sequence, int width, int height) => Api.Value!.Read(port, pid, sequence, width, height);
    public void Dispose() => Api.Value!.Close(port);

    internal static IntPtr Snapshot(IntPtr surface, out int timedOut) => Api.Value!.Snapshot(surface, out timedOut);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation", EntryPoint = "CFRelease")]
    internal static extern void Release(IntPtr reference);

    private static NativeApi? Load()
    {
        if (!Requested || Environment.GetEnvironmentVariable(DirectoryVariable) is not { Length: > 0 } directory)
            return null;
        var addon = Path.GetFullPath(Path.Combine(directory, "athena-browser-surface.node"));
        var library = Path.Combine(directory, "libathena-browser-surface.dylib");
        if (!File.Exists(addon) || !NativeLibrary.TryLoad(library, out var handle))
        {
            Log.Warning("The experimental browser preview bridge is unavailable in {Directory}", directory);
            return null;
        }
        try { return new NativeApi(handle, addon); }
        catch (EntryPointNotFoundException exception)
        {
            NativeLibrary.Free(handle);
            Log.Warning("The experimental browser preview bridge could not be loaded: {Error}", exception.Message);
            return null;
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint OpenPort([MarshalAs(UnmanagedType.LPUTF8Str)] string service);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ClosePort(uint port);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr ReadSurface(uint port, int pid, uint sequence, int width, int height);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr SnapshotSurface(IntPtr surface, out int timedOut);

    private sealed class NativeApi(IntPtr library, string addon)
    {
        // The successful load lives for the process. Both window presenters and host connections use these entry points.
        public readonly OpenPort Open = Get<OpenPort>(library, "athena_browser_surface_open");
        public readonly ClosePort Close = Get<ClosePort>(library, "athena_browser_surface_close");
        public readonly ReadSurface Read = Get<ReadSurface>(library, "athena_browser_surface_read");
        public readonly SnapshotSurface Snapshot = Get<SnapshotSurface>(library, "athena_browser_surface_snapshot");
        public string Addon { get; } = addon;

        private static T Get<T>(IntPtr library, string symbol) where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, symbol));
    }
}
