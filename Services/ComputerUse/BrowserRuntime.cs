using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.ComputerUse;

internal sealed record CefRuntimePaths(
    string RuntimeDir,
    string BridgeLibrary,
    string FrameworkDir,
    string MainBundleDir,
    string SubprocessPath,
    string ResourcesDir,
    string LocalesDir);

internal sealed class BrowserRuntime(string runtimeDirectory, string dataDirectory)
{
    private const string Resources = "Athena.UI.ComputerUse.";
    private static readonly Lazy<JsonElement> Manifest = new(() =>
    {
        using var stream = Resource("BrowserRuntime.json");
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.Clone();
    });

    public static string Version => Manifest.Value.GetProperty("version").GetString()!;
    public static string ChromiumVersion => Manifest.Value.GetProperty("chromiumVersion").GetString()!;
    internal static string Rid => (OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" :
        OperatingSystem.IsLinux() ? "linux" : "unknown") + "-" + RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
    internal static string DefaultDirectory => Path.Combine(AppContext.BaseDirectory, "BrowserRuntime");
    internal static string DefaultDataDirectory => Path.Combine(
        OperatingSystem.IsMacOS()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support")
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AthenaAgent", "Browser");

    private static JsonElement Package => Manifest.Value.GetProperty("packages").TryGetProperty(Rid, out var value)
        ? value : throw new PlatformNotSupportedException($"The bundled browser runtime is unavailable for {Rid}.");

    internal string RuntimeDirectory => runtimeDirectory;
    internal string DataDirectory => dataDirectory;

    private static string BridgeFileName => OperatingSystem.IsWindows() ? "athena_cef_bridge.dll"
        : OperatingSystem.IsMacOS() ? "libathena_cef_bridge.dylib" : "libathena_cef_bridge.so";

    public bool IsBundled
    {
        get
        {
            try
            {
                _ = Package;
                return TryResolvePaths(runtimeDirectory, out _);
            }
            catch (PlatformNotSupportedException)
            {
                return false;
            }
        }
    }

    internal static bool TryResolveBridgeLibrary(string directory, out string bridgePath)
    {
        bridgePath = Path.Combine(directory, BridgeFileName);
        return File.Exists(bridgePath);
    }

    internal static bool TryResolvePaths(string directory, out CefRuntimePaths paths)
    {
        var fullRuntime = Path.GetFullPath(directory);
        var bridge = Path.Combine(fullRuntime, BridgeFileName);
        if (OperatingSystem.IsMacOS())
        {
            // Inside a packaged Athena.app bundle, Frameworks live at Contents/Frameworks.
            // During source builds (`dotnet run`), stage-browser-runtime.py puts Frameworks inside BrowserRuntime/.
            var bundledFrameworks = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Frameworks"));
            var localFrameworks = Path.Combine(fullRuntime, "Frameworks");
            var useAppBundle = PathsEqual(fullRuntime, Path.GetFullPath(DefaultDirectory))
                && Directory.Exists(Path.Combine(bundledFrameworks, "Chromium Embedded Framework.framework"));
            var frameworksDir = useAppBundle ? bundledFrameworks : localFrameworks;
            var frameworkDir = Path.Combine(frameworksDir, "Chromium Embedded Framework.framework");
            var frameworkBinary = Path.Combine(frameworkDir, "Chromium Embedded Framework");
            var subprocess = Path.Combine(frameworksDir, "Athena Helper.app", "Contents", "MacOS", "Athena Helper");
            var mainBundle = useAppBundle
                ? Path.GetFullPath(Path.Combine(bundledFrameworks, "..", ".."))
                : fullRuntime;
            paths = new CefRuntimePaths(fullRuntime, bridge, frameworkDir, mainBundle, subprocess, "", "");
            return File.Exists(bridge) && File.Exists(frameworkBinary) && File.Exists(subprocess);
        }

        if (OperatingSystem.IsWindows())
        {
            var subprocess = Path.Combine(fullRuntime, "athena_cef_helper.exe");
            var libcef = Path.Combine(fullRuntime, "libcef.dll");
            var locales = Path.Combine(fullRuntime, "locales");
            paths = new CefRuntimePaths(fullRuntime, bridge, "", "", subprocess, fullRuntime, locales);
            return File.Exists(bridge) && File.Exists(libcef) && File.Exists(subprocess);
        }

        var linuxSubprocess = Path.Combine(fullRuntime, "athena_cef_helper");
        var linuxLibcef = Path.Combine(fullRuntime, "libcef.so");
        var linuxLocales = Path.Combine(fullRuntime, "locales");
        paths = new CefRuntimePaths(fullRuntime, bridge, "", "", linuxSubprocess, fullRuntime, linuxLocales);
        return File.Exists(bridge) && File.Exists(linuxLibcef) && File.Exists(linuxSubprocess);
    }

    public Task<CefRuntimePaths> PrepareAsync(CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        _ = Package;
        if (!TryResolvePaths(runtimeDirectory, out var paths))
        {
            var expected = Path.Combine(runtimeDirectory, BridgeFileName);
            throw new FileNotFoundException(
                $"The bundled CEF browser runtime for {Rid} is missing or incomplete at '{expected}'. "
                + "For a source build, run 'dotnet run --project Athena.UI.csproj' or "
                + $"'python3 Scripts/stage-browser-runtime.py {Rid} bin/Debug/net10.0' with Python 3 and a C++ compiler available. "
                + "For an installed build, reinstall Athena from its platform package.",
                expected);
        }

        Directory.CreateDirectory(dataDirectory);
        return paths;
    }, token);

    private static bool PathsEqual(string a, string b) =>
        string.Equals(
            a.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            b.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static Stream Resource(string name) => typeof(BrowserRuntime).Assembly.GetManifestResourceStream(Resources + name)
        ?? throw new InvalidOperationException("The embedded browser resources are missing.");
}
