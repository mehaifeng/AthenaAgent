using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.Generic;

namespace Athena.UI.Models;

public partial class FileSystemGlobalConfig : ObservableObject
{
    [ObservableProperty]
    private long _maxReadSizeBytes = 10485760; // 10MB

    [ObservableProperty]
    private long _maxWriteSizeBytes = 5242880; // 5MB

    // 是否跟随符号链接。false（默认）时，文件工具会把路径解析到真实目标后再跑目录/扩展名黑名单，
    // 阻止「沙箱内软链指向 /etc、~/.ssh」这类越界逃逸。true 时按字面路径校验（不解析软链）。
    [ObservableProperty]
    private bool _followSymlinks = false;
}

public partial class PlatformAccessRule : ObservableObject
{
    [ObservableProperty]
    private List<string> _blockedDirectories = new();
}

public partial class PlatformFileSystemConfig : ObservableObject
{
    [ObservableProperty]
    private PlatformAccessRule _readAccess = new();

    [ObservableProperty]
    private PlatformAccessRule _writeAccess = new();
}

public partial class FileSystemPlatformsConfig : ObservableObject
{
    [ObservableProperty]
    private PlatformFileSystemConfig _windows = new()
    {
        ReadAccess = new PlatformAccessRule
        {
            BlockedDirectories = new() { "%SystemRoot%", "%SystemRoot%\\System32", "%SystemRoot%\\SysWOW64", "%ProgramFiles%", "%ProgramFiles(x86)%", "%ProgramData%\\Microsoft", "%APPDATA%\\Microsoft", "%LOCALAPPDATA%\\Microsoft\\Credentials", "%USERPROFILE%\\.ssh", "%USERPROFILE%\\.gnupg", "%USERPROFILE%\\AppData\\Roaming\\Microsoft\\Protect" }
        },
        WriteAccess = new PlatformAccessRule
        {
            BlockedDirectories = new() { "%SystemRoot%", "%ProgramFiles%", "%ProgramFiles(x86)%", "%ProgramData%", "%USERPROFILE%\\.ssh", "%USERPROFILE%\\.gnupg", "%USERPROFILE%\\AppData\\Roaming\\Microsoft\\Protect", "%APPDATA%\\Athena\\config.json" }
        }
    };

    [ObservableProperty]
    private PlatformFileSystemConfig _macOS = new()
    {
        ReadAccess = new PlatformAccessRule
        {
            BlockedDirectories = new() { "/System", "/Library", "/usr", "/bin", "/sbin", "/etc", "/private/etc", "/private/var", "~/Library/Keychains", "~/Library/Passwords", "~/.ssh", "~/.gnupg", "~/Library/Application Support/Microsoft", "~/Library/Application Support/Google/Chrome/Default" }
        },
        WriteAccess = new PlatformAccessRule
        {
            BlockedDirectories = new() { "/System", "/Library", "/usr", "/bin", "/sbin", "/etc", "/private", "~/Library", "~/.ssh", "~/.gnupg", "~/.local/share/Athena/config.json" }
        }
    };

    [ObservableProperty]
    private PlatformFileSystemConfig _linux = new()
    {
        ReadAccess = new PlatformAccessRule
        {
            BlockedDirectories = new() { "/etc", "/etc/shadow", "/etc/sudoers", "/root", "/boot", "/sys", "/proc", "/dev", "/run", "/var/log", "/usr/lib", "/usr/bin", "/bin", "/sbin", "~/.ssh", "~/.gnupg", "~/.config/google-chrome", "~/.config/mozilla", "~/.local/share/keyrings" }
        },
        WriteAccess = new PlatformAccessRule
        {
            BlockedDirectories = new() { "/etc", "/root", "/boot", "/sys", "/proc", "/dev", "/var", "/usr", "/bin", "/sbin", "~/.ssh", "~/.gnupg", "~/.config", "~/.local/share/Athena/config.json" }
        }
    };
}

public partial class FileSystemPolicyConfig : ObservableObject
{
    [ObservableProperty]
    private FileSystemGlobalConfig _global = new();

    [ObservableProperty]
    private FileSystemPlatformsConfig _platforms = new();
}
