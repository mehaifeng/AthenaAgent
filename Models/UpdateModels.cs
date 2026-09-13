using System;
using System.Collections.Generic;

namespace Athena.UI.Models;

public sealed class UpdateCheckResult
{
    public bool IsSuccess { get; init; }
    public bool IsUpdateAvailable { get; init; }
    public string CurrentVersion { get; init; } = string.Empty;
    public string LatestVersion { get; init; } = string.Empty;
    public string ReleaseTag { get; init; } = string.Empty;
    public DateTimeOffset? PublishedAt { get; init; }
    public string ReleaseNotes { get; init; } = string.Empty;
    public string ReleaseNotesUrl { get; init; } = string.Empty;
    public string ManifestDownloadUrl { get; init; } = string.Empty;
    public string ErrorMessage { get; init; } = string.Empty;
}

public sealed class UpdateApplyResult
{
    public bool IsSuccess { get; init; }
    public bool IsPermissionDenied { get; init; }
    public string ErrorMessage { get; init; } = string.Empty;
}

public enum UpdateProgressStage
{
    Preparing,
    DownloadingManifest,
    DownloadingPackage,
    VerifyingPackage,
    ExtractingPackage,
    LaunchingUpdater
}

public sealed class UpdateProgressInfo
{
    public UpdateProgressStage Stage { get; init; }
    public double? Progress { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public long? BytesReceived { get; init; }
    public long? TotalBytes { get; init; }
}

public sealed class UpdateManifest
{
    public string Version { get; init; } = string.Empty;
    public string ReleaseNotesUrl { get; init; } = string.Empty;
    public Dictionary<string, UpdatePackageEntry> Packages { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> PreservePaths { get; init; } = new();
}

public sealed class UpdatePackageEntry
{
    public string Url { get; init; } = string.Empty;
    public string Sha256 { get; init; } = string.Empty;
    public string ArchiveType { get; init; } = string.Empty;
    public string EntryExecutable { get; init; } = string.Empty;
}

public sealed class UpdateSession
{
    public int MainProcessId { get; init; }
    public string InstallDirectory { get; init; } = string.Empty;
    public string StagingDirectory { get; init; } = string.Empty;
    public string EntryExecutable { get; init; } = string.Empty;

    /// <summary>
    /// 本次要装上的版本号，供更新器同步写回 macOS .app 的 Info.plist。
    /// </summary>
    /// <remarks>
    /// 会话由<em>旧</em>版应用写出，更新器却来自<em>新</em>下载的包，所以新更新器一定会读到
    /// 不带本字段的旧会话。更新器必须能从缺省值继续，不能因此中断更新。
    /// </remarks>
    public string Version { get; init; } = string.Empty;

    public List<string> PreservePaths { get; init; } = new();
}
