using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.Generic;

namespace Athena.UI.Models;

public partial class FileSystemGlobalConfig : ObservableObject
{
    [ObservableProperty]
    private List<string> _blockedExtensions = new()
    {
        ".exe", ".dll", ".so", ".dylib", ".sys", ".drv", ".ocx",
        ".bat", ".cmd", ".com", ".msi", ".msix", ".deb", ".rpm", ".pkg", ".dmg", ".iso",
        ".env", ".env.local", ".env.production", ".env.development",
        ".pem", ".key", ".p12", ".pfx", ".cer", ".crt", ".csr",
        ".keystore", ".jks", ".gpg", ".asc",
        ".zip", ".tar", ".gz", ".bz2", ".xz", ".7z", ".rar",
        ".db", ".sqlite", ".sqlite3", ".mdf", ".ldf",
        ".bin", ".dat", ".img", ".vmdk", ".vhd"
    };

    [ObservableProperty]
    private long _maxReadSizeBytes = 10485760; // 10MB

    [ObservableProperty]
    private long _maxWriteSizeBytes = 5242880; // 5MB

    [ObservableProperty]
    private bool _allowDelete = false;

    [ObservableProperty]
    private bool _allowDirectoryCreation = true;

    [ObservableProperty]
    private bool _followSymlinks = false;

    [ObservableProperty]
    private bool _allowHiddenFiles = false;
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