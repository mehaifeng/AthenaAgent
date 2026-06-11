using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Formats.Tar;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using Serilog;

namespace Athena.UI.Services;

public sealed class GitHubUpdateService : IUpdateService
{
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/mehaifeng/AthenaAgent/releases/latest";
    private const string ManifestAssetName = "update-manifest.json";

    private readonly ILogger _logger;
    private readonly HttpClient _httpClient;

    public GitHubUpdateService(ILogger logger)
    {
        _logger = logger.ForContext<GitHubUpdateService>();
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "AthenaAgent-Updater");
        _httpClient.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
    }

    public string GetCurrentVersion()
    {
        var entryAssembly = System.Reflection.Assembly.GetEntryAssembly();
        var infoVersion = entryAssembly?
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(infoVersion))
        {
            var idx = infoVersion.IndexOf('+');
            return idx > 0 ? infoVersion[..idx] : infoVersion;
        }

        var version = entryAssembly?.GetName().Version;
        return version == null ? "0.0.0" : $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        var currentVersion = GetCurrentVersion();
        try
        {
            using var response = await _httpClient.GetAsync(LatestReleaseApiUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new UpdateCheckResult
                {
                    IsSuccess = false,
                    CurrentVersion = currentVersion,
                    ErrorMessage = $"GitHub API returned {(int)response.StatusCode}."
                };
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var release = await JsonSerializer.DeserializeAsync<GitHubReleaseDto>(stream, cancellationToken: cancellationToken);
            if (release == null || string.IsNullOrWhiteSpace(release.TagName))
            {
                return new UpdateCheckResult
                {
                    IsSuccess = false,
                    CurrentVersion = currentVersion,
                    ErrorMessage = "Failed to parse latest release."
                };
            }

            var latestVersion = NormalizeVersion(release.TagName);
            var isUpdateAvailable = IsLatestNewer(currentVersion, latestVersion);
            var releaseNotes = release.Body ?? string.Empty;
            var releaseUrl = release.HtmlUrl ?? string.Empty;

            if (!isUpdateAvailable)
            {
                return new UpdateCheckResult
                {
                    IsSuccess = true,
                    IsUpdateAvailable = false,
                    CurrentVersion = currentVersion,
                    LatestVersion = latestVersion,
                    ReleaseTag = release.TagName,
                    PublishedAt = release.PublishedAt,
                    ReleaseNotes = releaseNotes,
                    ReleaseNotesUrl = releaseUrl
                };
            }

            var manifestAsset = release.Assets?.FirstOrDefault(a =>
                string.Equals(a.Name, ManifestAssetName, StringComparison.OrdinalIgnoreCase));
            if (manifestAsset == null || string.IsNullOrWhiteSpace(manifestAsset.BrowserDownloadUrl))
            {
                return new UpdateCheckResult
                {
                    IsSuccess = false,
                    IsUpdateAvailable = true,
                    CurrentVersion = currentVersion,
                    LatestVersion = latestVersion,
                    ReleaseTag = release.TagName,
                    PublishedAt = release.PublishedAt,
                    ReleaseNotes = releaseNotes,
                    ReleaseNotesUrl = releaseUrl,
                    ErrorMessage = "Release is missing update-manifest.json."
                };
            }

            return new UpdateCheckResult
            {
                IsSuccess = true,
                IsUpdateAvailable = true,
                CurrentVersion = currentVersion,
                LatestVersion = latestVersion,
                ReleaseTag = release.TagName,
                PublishedAt = release.PublishedAt,
                ReleaseNotes = releaseNotes,
                ReleaseNotesUrl = releaseUrl,
                ManifestDownloadUrl = manifestAsset.BrowserDownloadUrl
            };
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Check for updates failed.");
            return new UpdateCheckResult
            {
                IsSuccess = false,
                CurrentVersion = currentVersion,
                ErrorMessage = ex.Message
            };
        }
    }

    public async Task<UpdateApplyResult> PrepareAndLaunchUpdateAsync(
        UpdateCheckResult checkResult,
        IProgress<UpdateProgressInfo>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!checkResult.IsSuccess || !checkResult.IsUpdateAvailable || string.IsNullOrWhiteSpace(checkResult.ManifestDownloadUrl))
        {
            return new UpdateApplyResult
            {
                IsSuccess = false,
                ErrorMessage = "Invalid update state."
            };
        }

        var installDir = AppDomain.CurrentDomain.BaseDirectory;
        if (!CanWriteToInstallDirectory(installDir, out var writeError))
        {
            return new UpdateApplyResult
            {
                IsSuccess = false,
                IsPermissionDenied = true,
                ErrorMessage = writeError
            };
        }

        var workRoot = Path.Combine(Path.GetTempPath(), "AthenaAgentUpdater", $"{checkResult.LatestVersion}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workRoot);

        try
        {
            ReportProgress(progress, UpdateProgressStage.Preparing);

            var manifestPath = Path.Combine(workRoot, ManifestAssetName);
            await DownloadFileAsync(
                checkResult.ManifestDownloadUrl,
                manifestPath,
                UpdateProgressStage.DownloadingManifest,
                progress,
                cancellationToken);

            var manifest = await ReadManifestAsync(manifestPath, cancellationToken);
            if (manifest == null)
            {
                return new UpdateApplyResult
                {
                    IsSuccess = false,
                    ErrorMessage = "Failed to parse update manifest."
                };
            }

            var packageKey = GetCurrentPackageKey();
            if (!manifest.Packages.TryGetValue(packageKey, out var package) || string.IsNullOrWhiteSpace(package.Url))
            {
                return new UpdateApplyResult
                {
                    IsSuccess = false,
                    ErrorMessage = $"No update package for current platform ({packageKey})."
                };
            }

            var packageFileName = Path.GetFileName(new Uri(package.Url).AbsolutePath);
            if (string.IsNullOrWhiteSpace(packageFileName))
            {
                packageFileName = $"{packageKey}.pkg";
            }

            var packagePath = Path.Combine(workRoot, packageFileName);
            await DownloadFileAsync(
                package.Url,
                packagePath,
                UpdateProgressStage.DownloadingPackage,
                progress,
                cancellationToken);

            if (!string.IsNullOrWhiteSpace(package.Sha256))
            {
                ReportProgress(progress, UpdateProgressStage.VerifyingPackage, 1, packageFileName);
                var checksum = await ComputeSha256Async(packagePath, cancellationToken);
                if (!string.Equals(checksum, package.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    return new UpdateApplyResult
                    {
                        IsSuccess = false,
                        ErrorMessage = $"Checksum mismatch. Expected {package.Sha256}, got {checksum}."
                    };
                }
            }
            else
            {
                _logger.Warning("Manifest package has no SHA256: {PackageKey}", packageKey);
            }

            var stagingDir = Path.Combine(workRoot, "staging");
            Directory.CreateDirectory(stagingDir);
            ReportProgress(progress, UpdateProgressStage.ExtractingPackage, null, packageFileName);
            await ExtractPackageAsync(packagePath, package.ArchiveType, stagingDir, cancellationToken);
            var payloadRoot = ResolvePayloadRoot(stagingDir);

            var updaterFileName = OperatingSystem.IsWindows() ? "Athena.Updater.exe" : "Athena.Updater";
            var updaterPath = Path.Combine(payloadRoot, "updater", updaterFileName);
            if (!File.Exists(updaterPath))
            {
                return new UpdateApplyResult
                {
                    IsSuccess = false,
                    ErrorMessage = $"Updater binary is missing in package: {updaterFileName}."
                };
            }

            var entryExecutable = string.IsNullOrWhiteSpace(package.EntryExecutable)
                ? (OperatingSystem.IsWindows() ? "Athena.UI.exe" : "Athena.UI")
                : package.EntryExecutable;

            var session = new UpdateSession
            {
                MainProcessId = Environment.ProcessId,
                InstallDirectory = installDir,
                StagingDirectory = payloadRoot,
                EntryExecutable = entryExecutable,
                PreservePaths = manifest.PreservePaths?.Count > 0 ? manifest.PreservePaths : new List<string> { "AthenaData", "updater" }
            };

            var sessionFile = Path.Combine(workRoot, "update-session.json");
            var sessionJson = JsonSerializer.Serialize(session, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            await File.WriteAllTextAsync(sessionFile, sessionJson, cancellationToken);

            var startInfo = new ProcessStartInfo
            {
                FileName = updaterPath,
                Arguments = $"--session \"{sessionFile}\"",
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(updaterPath) ?? workRoot
            };
            ReportProgress(progress, UpdateProgressStage.LaunchingUpdater, 1, packageFileName);
            Process.Start(startInfo);

            _logger.Information("Updater launched. Version={Version}, Session={SessionFile}", checkResult.LatestVersion, sessionFile);
            return new UpdateApplyResult { IsSuccess = true };
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.Warning(ex, "Permission denied while preparing update.");
            return new UpdateApplyResult
            {
                IsSuccess = false,
                IsPermissionDenied = true,
                ErrorMessage = ex.Message
            };
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Prepare and launch update failed.");
            return new UpdateApplyResult
            {
                IsSuccess = false,
                ErrorMessage = ex.Message
            };
        }
    }

    private static string NormalizeVersion(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "0.0.0";
        }

        var trimmed = value.Trim();
        if (trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[1..];
        }

        var plusIndex = trimmed.IndexOf('+');
        if (plusIndex > 0)
        {
            trimmed = trimmed[..plusIndex];
        }

        return trimmed;
    }

    private static bool IsLatestNewer(string currentVersion, string latestVersion)
    {
        var currentParsed = TryParseVersion(currentVersion, out var current);
        var latestParsed = TryParseVersion(latestVersion, out var latest);
        if (currentParsed && latestParsed)
        {
            for (var i = 0; i < Math.Max(current.Length, latest.Length); i++)
            {
                var cur = i < current.Length ? current[i] : 0;
                var lat = i < latest.Length ? latest[i] : 0;
                if (lat > cur) return true;
                if (lat < cur) return false;
            }

            return false;
        }

        return !string.Equals(currentVersion, latestVersion, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseVersion(string version, out int[] parts)
    {
        parts = Array.Empty<int>();
        var normalized = NormalizeVersion(version);
        var numericPart = normalized.Split('-', 2)[0];
        var segments = numericPart.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return false;
        }

        var values = new List<int>(segments.Length);
        foreach (var seg in segments)
        {
            if (!int.TryParse(seg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var part))
            {
                return false;
            }

            values.Add(part);
        }

        parts = values.ToArray();
        return true;
    }

    private static bool CanWriteToInstallDirectory(string installDir, out string error)
    {
        error = string.Empty;
        try
        {
            var marker = Path.Combine(installDir, $".athena_write_test_{Guid.NewGuid():N}.tmp");
            File.WriteAllText(marker, "ok");
            File.Delete(marker);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private async Task<UpdateManifest?> ReadManifestAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;

        var version = root.TryGetProperty("version", out var versionElement) ? versionElement.GetString() ?? string.Empty : string.Empty;
        var releaseNotesUrl = root.TryGetProperty("releaseNotesUrl", out var notesElement) ? notesElement.GetString() ?? string.Empty : string.Empty;
        var preservePaths = new List<string>();
        if (root.TryGetProperty("preservePaths", out var preserveElement) && preserveElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in preserveElement.EnumerateArray())
            {
                var pathItem = item.GetString();
                if (!string.IsNullOrWhiteSpace(pathItem))
                {
                    preservePaths.Add(pathItem);
                }
            }
        }

        var packages = new Dictionary<string, UpdatePackageEntry>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("packages", out var packagesElement) && packagesElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in packagesElement.EnumerateObject())
            {
                var value = property.Value;
                var url = value.TryGetProperty("url", out var urlElement) ? urlElement.GetString() ?? string.Empty : string.Empty;
                var sha256 = value.TryGetProperty("sha256", out var shaElement) ? shaElement.GetString() ?? string.Empty : string.Empty;
                var archiveType = value.TryGetProperty("archiveType", out var archiveTypeElement) ? archiveTypeElement.GetString() ?? string.Empty : string.Empty;
                var entryExecutable = value.TryGetProperty("entryExecutable", out var entryElement) ? entryElement.GetString() ?? string.Empty : string.Empty;

                packages[property.Name] = new UpdatePackageEntry
                {
                    Url = url,
                    Sha256 = sha256,
                    ArchiveType = archiveType,
                    EntryExecutable = entryExecutable
                };
            }
        }

        return new UpdateManifest
        {
            Version = version,
            ReleaseNotesUrl = releaseNotesUrl,
            Packages = packages,
            PreservePaths = preservePaths
        };
    }

    private async Task DownloadFileAsync(
        string url,
        string destinationPath,
        UpdateProgressStage stage,
        IProgress<UpdateProgressInfo>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var totalBytes = response.Content.Headers.ContentLength;
        var itemName = Path.GetFileName(destinationPath);
        await using var sourceStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destinationStream = File.Create(destinationPath);
        var buffer = new byte[81920];
        long bytesReceived = 0;
        ReportProgress(progress, stage, totalBytes.HasValue && totalBytes.Value > 0 ? 0 : null, itemName, bytesReceived, totalBytes);

        while (true)
        {
            var bytesRead = await sourceStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (bytesRead == 0)
            {
                break;
            }

            await destinationStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            bytesReceived += bytesRead;
            double? progressValue = totalBytes.HasValue && totalBytes.Value > 0
                ? Math.Min(1d, (double)bytesReceived / totalBytes.Value)
                : null;
            ReportProgress(progress, stage, progressValue, itemName, bytesReceived, totalBytes);
        }
    }

    private static void ReportProgress(
        IProgress<UpdateProgressInfo>? progress,
        UpdateProgressStage stage,
        double? progressValue = null,
        string itemName = "",
        long? bytesReceived = null,
        long? totalBytes = null)
    {
        progress?.Report(new UpdateProgressInfo
        {
            Stage = stage,
            Progress = progressValue,
            ItemName = itemName,
            BytesReceived = bytesReceived,
            TotalBytes = totalBytes
        });
    }

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(filePath);
        using var sha256 = SHA256.Create();
        var hash = await sha256.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }

    private static async Task ExtractPackageAsync(string packagePath, string archiveType, string destinationDirectory, CancellationToken cancellationToken)
    {
        var type = (archiveType ?? string.Empty).Trim().ToLowerInvariant();
        if (type == "zip" || packagePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ZipFile.ExtractToDirectory(packagePath, destinationDirectory, true);
            return;
        }

        if (type is "tar.gz" or "tgz" || packagePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) || packagePath.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase))
        {
            await using var file = File.OpenRead(packagePath);
            await using var gzip = new GZipStream(file, CompressionMode.Decompress);
            TarFile.ExtractToDirectory(gzip, destinationDirectory, true);
            return;
        }

        throw new NotSupportedException($"Unsupported package format: {archiveType}");
    }

    private static string ResolvePayloadRoot(string stagingDirectory)
    {
        var subDirs = Directory.GetDirectories(stagingDirectory);
        var files = Directory.GetFiles(stagingDirectory);
        if (subDirs.Length == 1 && files.Length == 0)
        {
            return subDirs[0];
        }

        return stagingDirectory;
    }

    private static string GetCurrentPackageKey()
    {
        var os = OperatingSystem.IsWindows()
            ? "win"
            : OperatingSystem.IsMacOS()
                ? "osx"
                : "linux";
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X64 => "x64",
            _ => "x64"
        };
        return $"{os}-{arch}";
    }

    private sealed class GitHubReleaseDto
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; init; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; init; }

        [JsonPropertyName("body")]
        public string? Body { get; init; }

        [JsonPropertyName("published_at")]
        public DateTimeOffset? PublishedAt { get; init; }

        [JsonPropertyName("assets")]
        public List<GitHubReleaseAssetDto>? Assets { get; init; }
    }

    private sealed class GitHubReleaseAssetDto
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; init; }
    }
}
