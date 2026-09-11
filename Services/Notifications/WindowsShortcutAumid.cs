using Serilog;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Athena.UI.Services.Notifications;

/// <summary>
/// 给开始菜单快捷方式打上 <c>System.AppUserModel.ID</c>，好让 toast 以 Athena 的名字和图标出现。
///
/// 为什么非做不可：未打包的 Win32 应用的通知身份不来自进程，而来自
/// **某个开始菜单快捷方式上的 AUMID 属性**。属性不在，`CreateToastNotifier`
/// 就没有可用身份，只能退到 PowerShell 自己的 AUMID——通知照弹，但署名是
/// 「Windows PowerShell」。安装包侧已经在 [Icons] 上写了 AppUserModelID，
/// 但那只对**重装之后**的快捷方式有效；已装好的那份、以及 zip 便携版
/// 根本没有这个属性，只能由应用自己补。
///
/// 写属性走的是 IShellLink + IPropertyStore，没有别的入口：WScript.Shell
/// 那套 COM 根本不暴露属性存储，PowerShell 也一样——所以这段 interop
/// 无法用「shell 出去执行一条命令」代替。
///
/// 三条行为约束：
/// - **优先补属性，而不是重建快捷方式**。安装包装的那份带着图标、工作目录等信息，
///   重建等于把它们悄悄换掉；这里只加一个属性，别的原样不动。
/// - **已经有 AUMID 就直接用它**，哪怕值和我们想写的不一样——那是安装包
///   或更早版本注册过的身份，覆盖它只会让此前发出的通知失去归属。
/// - **全机范围的那份只读不写**：ProgramData 下的快捷方式要管理员权限，
///   而本应用是每用户安装（见 windows-installer.iss 的 PrivilegesRequired=lowest）。
/// </summary>
internal static class WindowsShortcutAumid
{
    private const string ShellLinkClsid = "00021401-0000-0000-C000-000000000046";

    /// <summary>PKEY_AppUserModel_ID。</summary>
    private static readonly Guid AppUserModelIdFormatId = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
    private const uint AppUserModelIdPropertyId = 5;

    private const ushort VT_EMPTY = 0;
    private const ushort VT_LPWSTR = 31;
    private const uint STGM_READ = 0;
    private const uint STGM_READWRITE = 2;

    /// <summary>
    /// 确保存在一个带 AUMID 的开始菜单快捷方式，返回可用于 CreateToastNotifier 的身份。
    /// 任何一步失败都返回 null——调用方会退回到通用身份，通知不能因为品牌化失败就发不出去。
    /// </summary>
    /// <param name="preferredAumid">本应用希望注册的 AUMID。</param>
    /// <param name="shortcutName">快捷方式显示名，同时决定通知上显示的名字。</param>
    internal static string? TryEnsure(string preferredAumid, string shortcutName, ILogger logger)
    {
        if (!OperatingSystem.IsWindows()) return null;

        try
        {
            // 全机安装的那份优先：它先于每用户快捷方式被 shell 认定为应用身份。
            var machineWide = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
                shortcutName,
                shortcutName + ".lnk");
            var existingMachineAumid = RunInSta(() => ReadAumid(machineWide), logger);
            if (!string.IsNullOrEmpty(existingMachineAumid)) return existingMachineAumid;

            var perUser = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                shortcutName,
                shortcutName + ".lnk");
            return RunInSta(() => EnsurePerUser(perUser, preferredAumid, logger), logger);
        }
        catch (Exception ex)
        {
            logger.Debug(ex, "Could not provision a Start Menu identity for notifications");
            return null;
        }
    }

    private static string? EnsurePerUser(string shortcutPath, string preferredAumid, ILogger logger)
    {
        var existing = ReadAumid(shortcutPath);
        if (!string.IsNullOrEmpty(existing)) return existing;

        if (File.Exists(shortcutPath))
        {
            // 安装包装的快捷方式没带这个属性（本次改动之前的安装包都是如此）。
            // 只补属性，其余原样保留。
            if (!WriteAumid(shortcutPath, preferredAumid, createTargetPath: null)) return null;
            logger.Information("Added a notification identity to the existing Start Menu shortcut {Path}", shortcutPath);
            return preferredAumid;
        }

        // zip 便携版：没有任何快捷方式，自己建一个指向当前可执行文件的。
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
        {
            logger.Debug("No executable path available; skipping Start Menu shortcut creation");
            return null;
        }

        var directory = Path.GetDirectoryName(shortcutPath);
        if (string.IsNullOrEmpty(directory)) return null;
        Directory.CreateDirectory(directory);

        if (!WriteAumid(shortcutPath, preferredAumid, exePath)) return null;
        logger.Information("Created a Start Menu shortcut at {Path} so notifications carry the app's own identity", shortcutPath);
        return preferredAumid;
    }

    private static string? ReadAumid(string shortcutPath)
    {
        if (!File.Exists(shortcutPath)) return null;

        object? shellLink = null;
        try
        {
            shellLink = CreateShellLink();
            if (shellLink == null) return null;

            if (((IPersistFile)shellLink).Load(shortcutPath, STGM_READ) != 0) return null;

            var store = (IPropertyStore)shellLink;
            var key = new PropertyKey { FormatId = AppUserModelIdFormatId, PropertyId = AppUserModelIdPropertyId };
            var value = new PropVariant();
            try
            {
                if (store.GetValue(ref key, out value) != 0) return null;
                return value.Vt == VT_LPWSTR && value.Pointer != IntPtr.Zero
                    ? Marshal.PtrToStringUni(value.Pointer)
                    : null;
            }
            finally
            {
                if (value.Vt != VT_EMPTY) PropVariantClear(ref value);
            }
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (shellLink != null) Marshal.FinalReleaseComObject(shellLink);
        }
    }

    /// <param name="createTargetPath">非空表示这是一个新建的快捷方式，需要先设定目标。</param>
    private static bool WriteAumid(string shortcutPath, string aumid, string? createTargetPath)
    {
        object? shellLink = null;
        var stringPointer = IntPtr.Zero;
        try
        {
            shellLink = CreateShellLink();
            if (shellLink == null) return false;

            var link = (IShellLinkW)shellLink;
            var persist = (IPersistFile)shellLink;

            if (createTargetPath == null)
            {
                if (persist.Load(shortcutPath, STGM_READWRITE) != 0) return false;
            }
            else
            {
                if (link.SetPath(createTargetPath) != 0) return false;
                var workingDirectory = Path.GetDirectoryName(createTargetPath);
                if (!string.IsNullOrEmpty(workingDirectory)) link.SetWorkingDirectory(workingDirectory);
            }

            var store = (IPropertyStore)shellLink;
            var key = new PropertyKey { FormatId = AppUserModelIdFormatId, PropertyId = AppUserModelIdPropertyId };
            stringPointer = Marshal.StringToCoTaskMemUni(aumid);
            var value = new PropVariant { Vt = VT_LPWSTR, Pointer = stringPointer };

            if (store.SetValue(ref key, ref value) != 0) return false;
            if (store.Commit() != 0) return false;

            // Save(null, true) 表示存回加载时的那个文件。
            return persist.Save(createTargetPath == null ? null : shortcutPath, true) == 0;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            // SetValue 会自己复制一份，这里释放的是我们分配的那块。
            if (stringPointer != IntPtr.Zero) Marshal.FreeCoTaskMem(stringPointer);
            if (shellLink != null) Marshal.FinalReleaseComObject(shellLink);
        }
    }

    private static object? CreateShellLink()
    {
        var type = Type.GetTypeFromCLSID(new Guid(ShellLinkClsid));
        return type == null ? null : Activator.CreateInstance(type);
    }

    /// <summary>
    /// Shell 的链接对象是套间线程模型（STA）的，而通知路径跑在任意线程上。
    /// 从 MTA 调用会走跨套间代理，行为随宿主状态而变；起一条一次性的 STA 线程
    /// 把整段调用关在里面，是这里唯一稳定的做法。
    /// </summary>
    private static string? RunInSta(Func<string?> action, ILogger logger)
    {
        string? result = null;
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        // 纯本地的文件与 shell 调用，不该超过一两百毫秒；超时就当作不可用。
        if (!thread.Join(TimeSpan.FromSeconds(10)))
        {
            logger.Debug("Timed out while reading or writing the Start Menu shortcut identity");
            return null;
        }

        if (failure != null) logger.Debug(failure, "Start Menu shortcut identity access failed");
        return result;
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pvar);

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    /// <summary>
    /// PROPVARIANT 的最小可用布局：2+2+2+2 字节的头，后面跟联合体。
    /// 两个指针大小的尾巴让它在 x86（16 字节）和 x64（24 字节）上都是正确大小。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort Vt;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public IntPtr Pointer;
        public IntPtr Padding;
    }

    [ComImport]
    [Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [ComImport]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        [PreserveSig] int GetClassID(out Guid classId);
        [PreserveSig] int IsDirty();
        [PreserveSig] int Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);
        [PreserveSig] int Save([MarshalAs(UnmanagedType.LPWStr)] string? fileName, [MarshalAs(UnmanagedType.Bool)] bool remember);
        [PreserveSig] int SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);
        [PreserveSig] int GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
    }

    /// <summary>
    /// 整张 vtable 必须按原顺序声明齐全，即使只调用其中两个方法——
    /// 少一个槽位后面所有调用就都错位了。
    /// </summary>
    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        [PreserveSig] int GetPath([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder file, int maxPath, IntPtr findData, uint flags);
        [PreserveSig] int GetIDList(out IntPtr idList);
        [PreserveSig] int SetIDList(IntPtr idList);
        [PreserveSig] int GetDescription([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder name, int maxName);
        [PreserveSig] int SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int GetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder directory, int maxPath);
        [PreserveSig] int SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        [PreserveSig] int GetArguments([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder arguments, int maxArguments);
        [PreserveSig] int SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        [PreserveSig] int GetHotkey(out ushort hotkey);
        [PreserveSig] int SetHotkey(ushort hotkey);
        [PreserveSig] int GetShowCmd(out int showCmd);
        [PreserveSig] int SetShowCmd(int showCmd);
        [PreserveSig] int GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder iconPath, int iconPathLength, out int iconIndex);
        [PreserveSig] int SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        [PreserveSig] int SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relativePath, uint reserved);
        [PreserveSig] int Resolve(IntPtr window, uint flags);
        [PreserveSig] int SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }
}
