using System;
using System.IO;

namespace Athena.Updater;

/// <summary>
/// macOS .app layout awareness for the updater.
/// </summary>
internal static class MacAppBundle
{
    // create_app_bundle writes the release version into both of these.
    private static readonly string[] VersionKeys = ["CFBundleVersion", "CFBundleShortVersionString"];

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
    /// Writes <paramref name="version"/> into the bundle's <c>CFBundleVersion</c> and
    /// <c>CFBundleShortVersionString</c>. Returns false when there is nothing to do — a
    /// flat install, an unusable version, or a plist that already reads that version.
    /// </summary>
    /// <remarks>
    /// ApplyUpdate only writes inside Contents/MacOS, so without this the bundle keeps
    /// advertising whatever version its DMG shipped: Finder, Gatekeeper and anything else
    /// reading Info.plist see a version the app stopped being several updates ago.
    /// </remarks>
    public static bool TryUpdateBundleVersion(string installDirectory, string version)
    {
        var bundleRoot = ResolveBundleRoot(installDirectory);
        if (bundleRoot == null || !IsWritableVersion(version))
        {
            return false;
        }

        var plistPath = Path.Combine(bundleRoot, "Contents", "Info.plist");
        var original = File.ReadAllText(plistPath);
        var updated = original;
        foreach (var key in VersionKeys)
        {
            TrySetPlistString(ref updated, key, version);
        }

        if (string.Equals(updated, original, StringComparison.Ordinal))
        {
            return false;
        }

        // Write through a temp file: a half-written Info.plist is an app macOS refuses to
        // launch at all, which is far worse than a stale version string.
        var temp = plistPath + $".tmp-{Guid.NewGuid():N}";
        File.WriteAllText(temp, updated);
        File.Move(temp, plistPath, true);
        return true;
    }

    /// <summary>
    /// Replaces the text of the <c>&lt;string&gt;</c> that follows <c>&lt;key&gt;</c>
    /// <paramref name="key"/><c>&lt;/key&gt;</c>, leaving every other byte alone.
    /// </summary>
    /// <remarks>
    /// Deliberately not an XDocument round-trip. XmlWriter re-emits the plist DOCTYPE with
    /// an empty internal subset — <c>...PropertyList-1.0.dtd"[]&gt;</c> — and Apple's own
    /// parser rejects that outright ("unexpected character [ while parsing DTD"), so the
    /// tidier-looking version produces a bundle nothing on the system can read.
    /// </remarks>
    private static bool TrySetPlistString(ref string text, string key, string value)
    {
        var keyMarker = $"<key>{key}</key>";
        var keyIndex = text.IndexOf(keyMarker, StringComparison.Ordinal);
        if (keyIndex < 0)
        {
            return false;
        }

        var cursor = keyIndex + keyMarker.Length;
        while (cursor < text.Length && char.IsWhiteSpace(text[cursor]))
        {
            cursor++;
        }

        const string Open = "<string>";
        const string Close = "</string>";
        if (string.CompareOrdinal(text, cursor, Open, 0, Open.Length) != 0)
        {
            // The key holds something other than a string; rewriting it would change the
            // entry's type.
            return false;
        }

        var valueStart = cursor + Open.Length;
        var valueEnd = text.IndexOf(Close, valueStart, StringComparison.Ordinal);
        if (valueEnd < 0)
        {
            return false;
        }

        text = string.Concat(text.AsSpan(0, valueStart), value, text.AsSpan(valueEnd));
        return true;
    }

    /// <summary>
    /// Accepts only the shape a release version actually has. Anything else is rejected
    /// rather than escaped: a version needing XML escaping is bad input, not a value worth
    /// writing into a plist.
    /// </summary>
    private static bool IsWritableVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return false;
        }

        foreach (var c in version)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_'))
            {
                return false;
            }
        }

        return char.IsAsciiDigit(version[0]);
    }
}
