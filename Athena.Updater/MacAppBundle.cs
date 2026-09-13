using System;
using System.IO;

namespace Athena.Updater;

/// <summary>
/// macOS .app layout awareness for the updater.
/// </summary>
/// <remarks>
/// An update package is the flat <c>dotnet publish</c> output, so it is shaped like a
/// tar.gz install, not like the .app bundle release.sh builds for the DMG. Everything
/// create_app_bundle moves out of Contents/MacOS has to be moved again after an update,
/// or the bundle drifts away from the layout a fresh install has.
/// </remarks>
internal static class MacAppBundle
{
    private const string DriverDirectoryName = ".playwright";

    /// <summary>
    /// Returns the enclosing <c>.app</c> directory when <paramref name="installDirectory"/>
    /// is a bundle's <c>Contents/MacOS</c>, or null for a flat install.
    /// </summary>
    public static string? ResolveBundleRoot(string installDirectory)
    {
        if (string.IsNullOrWhiteSpace(installDirectory))
        {
            return null;
        }

        // AppDomain.CurrentDomain.BaseDirectory — which is what the session's
        // InstallDirectory is built from — always carries a trailing separator, and
        // Path.GetFileName returns "" for a path that ends in one.
        var macOsDir = Path.GetFullPath(installDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.Equals(Path.GetFileName(macOsDir), "MacOS", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var contentsDir = Path.GetDirectoryName(macOsDir);
        if (contentsDir == null ||
            !string.Equals(Path.GetFileName(contentsDir), "Contents", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var bundleRoot = Path.GetDirectoryName(contentsDir);
        if (bundleRoot == null ||
            !Path.GetFileName(bundleRoot).EndsWith(".app", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // The name shape alone is a coincidence waiting to happen. Info.plist is what
        // actually makes a directory a bundle, so require it before moving anything.
        return File.Exists(Path.Combine(contentsDir, "Info.plist")) ? bundleRoot : null;
    }

    /// <summary>
    /// Moves the driver an update just wrote into <c>Contents/MacOS</c> over to
    /// <c>Contents/Resources</c>, replacing the copy that is already there. Returns false
    /// when there is nothing to do — a flat install, or a package without a driver.
    /// </summary>
    /// <remarks>
    /// codesign reads the driver's Mach-O node binary as a nested code object and fails
    /// with "bundle format unrecognized" when it sits under Contents/MacOS, which is why
    /// create_app_bundle ships it under Contents/Resources. Leaving the updated copy in
    /// Contents/MacOS also stores the 128 MB driver twice.
    /// </remarks>
    public static bool RelocatePlaywrightDriver(string installDirectory)
    {
        var bundleRoot = ResolveBundleRoot(installDirectory);
        if (bundleRoot == null)
        {
            return false;
        }

        var staged = Path.Combine(installDirectory, DriverDirectoryName);
        if (!Directory.Exists(staged))
        {
            return false;
        }

        var resourcesDir = Path.Combine(bundleRoot, "Contents", "Resources");
        Directory.CreateDirectory(resourcesDir);
        var target = Path.Combine(resourcesDir, DriverDirectoryName);

        // Both moves are renames on one volume. Retire the old copy instead of deleting
        // it up front: if the second rename fails we can put it back, and the app starts
        // either way — Athena.UI/Program.cs points the driver search at Resources only
        // when Contents/MacOS has no driver of its own.
        var retired = target + $".retired-{Guid.NewGuid():N}";
        var retiredOldCopy = false;
        if (Directory.Exists(target))
        {
            Directory.Move(target, retired);
            retiredOldCopy = true;
        }

        try
        {
            Directory.Move(staged, target);
        }
        catch
        {
            if (retiredOldCopy)
            {
                Directory.Move(retired, target);
            }

            throw;
        }

        if (retiredOldCopy)
        {
            try
            {
                Directory.Delete(retired, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The relocation itself is already done and the bundle is in the right
                // shape. A leftover retired copy wastes disk but breaks nothing, so it
                // must not turn a successful relocation into a reported failure.
                Console.Error.WriteLine(
                    $"Could not remove the retired driver copy at {retired}: {ex.Message}");
            }
        }

        return true;
    }
}
