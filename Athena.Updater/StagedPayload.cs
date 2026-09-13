using System;
using System.Diagnostics;
using System.IO;

namespace Athena.Updater;

/// <summary>
/// Reads facts about the update payload the updater is about to install.
/// </summary>
internal static class StagedPayload
{
    /// <summary>
    /// Reads the version off the staged app's managed assembly, for sessions that do not
    /// carry one. Returns an empty string when it cannot be determined.
    /// </summary>
    /// <remarks>
    /// The session is written by the <em>old</em> app while the updater comes from the
    /// <em>newly downloaded</em> package, so a new updater always meets old sessions that
    /// predate <see cref="Athena.Updater.Program"/> knowing about versions at all. Without
    /// this fallback the bundle's Info.plist would only start tracking reality one release
    /// after every install already has the field — the payload itself knows its version,
    /// so there is no reason to wait a cycle.
    /// </remarks>
    public static string ResolveVersion(string stagingDirectory, string entryExecutable)
    {
        if (string.IsNullOrWhiteSpace(stagingDirectory) || string.IsNullOrWhiteSpace(entryExecutable))
        {
            return string.Empty;
        }

        // The entry is the apphost (Athena.UI / Athena.UI.exe); the version lives on the
        // managed assembly beside it. Note that Path.ChangeExtension is wrong here — it
        // would read "Athena.UI" as having extension ".UI" and produce "Athena.dll".
        var managedName = entryExecutable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? entryExecutable[..^4] + ".dll"
            : entryExecutable + ".dll";

        var managedPath = Path.Combine(stagingDirectory, managedName);
        if (!File.Exists(managedPath))
        {
            return string.Empty;
        }

        try
        {
            // ProductVersion is the informational version release.sh stamps, which is the
            // same value create_app_bundle writes into Info.plist.
            var product = FileVersionInfo.GetVersionInfo(managedPath).ProductVersion;
            if (string.IsNullOrWhiteSpace(product))
            {
                return string.Empty;
            }

            // SourceLink appends "+<commit>" unless the build overrides it.
            var plusIndex = product.IndexOf('+');
            return (plusIndex > 0 ? product[..plusIndex] : product).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Only costs the Info.plist refresh; the update itself does not depend on it.
            Console.Error.WriteLine($"Could not read a version from {managedPath}: {ex.Message}");
            return string.Empty;
        }
    }
}
