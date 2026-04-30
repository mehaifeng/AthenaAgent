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
    public List<string> PreservePaths { get; init; } = new();
}
