using Athena.UI.Services.ComputerUse;
using Avalonia.Controls;
using Avalonia.Platform;
using System;

namespace Athena.UI.Controls;

/// <summary>
/// Hosts a real native child container (<c>NSView</c> on macOS, <c>HWND</c> on Windows, <c>XID</c> on Linux)
/// for CEF windowed rendering, and degrades cleanly to a zero-handle placeholder under <c>Avalonia.Headless</c>.
/// </summary>
public sealed class CefBrowserHostControl : NativeControlHost
{
    private nint _nativeHandle;
    private bool _ownsNativeHandle;
    private string? _runtimeDirectory;

    internal event Action<nint>? NativeHandleChanged;

    internal nint NativeHandle => _nativeHandle;

    internal void SetRuntimeDirectory(string? directory) => _runtimeDirectory = directory;

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        if (CefNativeBridge.IsHeadlessRuntime)
        {
            _nativeHandle = 0;
            _ownsNativeHandle = false;
            NativeHandleChanged?.Invoke(0);
            return new PlatformHandle(IntPtr.Zero, "HeadlessCefHost");
        }

        var descriptor = OperatingSystem.IsMacOS() ? "NSView"
            : OperatingSystem.IsWindows() ? "HWND"
            : "XID";

        var created = CefNativeBridge.CreateHostView(parent.Handle, _runtimeDirectory);
        if (created != 0)
        {
            _nativeHandle = created;
            _ownsNativeHandle = true;
            NativeHandleChanged?.Invoke(_nativeHandle);
            return new PlatformHandle(_nativeHandle, descriptor);
        }

        var fallback = base.CreateNativeControlCore(parent);
        _nativeHandle = fallback.Handle;
        _ownsNativeHandle = false;
        NativeHandleChanged?.Invoke(_nativeHandle);
        return fallback;
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        var handle = _nativeHandle;
        var owned = _ownsNativeHandle;
        _nativeHandle = 0;
        _ownsNativeHandle = false;
        NativeHandleChanged?.Invoke(0);

        if (CefNativeBridge.IsHeadlessRuntime)
            return;

        if (owned && handle != 0)
            CefNativeBridge.DestroyHostView(handle);
        else
            base.DestroyNativeControlCore(control);
    }
}
