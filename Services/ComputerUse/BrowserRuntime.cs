using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.ComputerUse;

internal sealed class BrowserRuntime(string archiveDirectory, string dataDirectory)
{
    private const string Resources = "Athena.UI.ComputerUse.";
    private static readonly Lazy<JsonElement> Manifest = new(() =>
    {
        using var stream = Resource("BrowserRuntime.json");
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.Clone();
    });

    public static string Version => Manifest.Value.GetProperty("version").GetString()!;
    private static string Rid => (OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" :
        OperatingSystem.IsLinux() ? "linux" : "unknown") + "-" + RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
    internal static string DefaultDirectory => Path.Combine(AppContext.BaseDirectory, "BrowserRuntime");
    internal static string DefaultDataDirectory => Path.Combine(
        OperatingSystem.IsMacOS()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support")
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AthenaAgent", "Browser");

    private static JsonElement Package => Manifest.Value.GetProperty("packages").TryGetProperty(Rid, out var value)
        ? value : throw new PlatformNotSupportedException("The browser runtime is unavailable for this platform.");
    private string Archive => Path.Combine(archiveDirectory, "runtime.zip");
    private static string Executable => OperatingSystem.IsWindows() ? "electron.exe"
        : OperatingSystem.IsMacOS() ? "Electron.app/Contents/MacOS/Electron" : "electron";

    public bool IsBundled
    {
        get
        {
            try { _ = Package; return File.Exists(Archive); }
            catch (PlatformNotSupportedException) { return false; }
        }
    }

    public Task<(string Executable, string Script)> PrepareAsync(CancellationToken token) => Task.Run(() =>
    {
        if (!IsBundled) throw new InvalidOperationException("The bundled browser runtime is missing. Reinstall Athena from its platform package.");
        var directory = Path.Combine(dataDirectory, "Runtime", $"{Version}-{Rid}");
        var checksum = Package.GetProperty("sha256").GetString()!;
        var executable = Path.Combine(directory, Executable);
        var stamp = Path.Combine(directory, ".sha256");
        if (!File.Exists(executable) || !File.Exists(stamp) || File.ReadAllText(stamp) != checksum)
        {
            using var input = File.OpenRead(Archive);
            if (!Convert.ToHexString(SHA256.HashData(input)).Equals(checksum, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The bundled browser runtime failed its checksum verification.");
            input.Position = 0;
            var stage = directory + "." + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(stage);
            try
            {
                Extract(input, stage, token);
                if (!File.Exists(Path.Combine(stage, Executable)))
                    throw new InvalidDataException("The browser archive did not contain its executable.");
                File.WriteAllText(Path.Combine(stage, ".sha256"), checksum);
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
                Directory.Move(stage, directory);
            }
            finally
            {
                if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
            }
        }
        foreach (var name in new[] { "BrowserHost.cjs", "BrowserPrompt.cjs" })
        {
            using var scriptResource = Resource(name);
            var script = Path.Combine(directory, name);
            var stagingScript = script + ".tmp";
            using (var output = File.Create(stagingScript)) scriptResource.CopyTo(output);
            File.Move(stagingScript, script, overwrite: true);
        }
        return (executable, Path.Combine(directory, "BrowserHost.cjs"));
    }, token);

    private static Stream Resource(string name) => typeof(BrowserRuntime).Assembly.GetManifestResourceStream(Resources + name)
        ?? throw new InvalidOperationException("The embedded browser resources are missing.");

    private static void Extract(Stream stream, string destination, CancellationToken token)
    {
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var links = new List<(string Path, string Target)>();
        foreach (var entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested();
            var path = Resolve(root, root, entry.FullName);
            var unixMode = entry.ExternalAttributes >> 16;
            if ((unixMode & 0xf000) == 0xa000)
            {
                using var reader = new StreamReader(entry.Open());
                var target = reader.ReadToEnd().TrimEnd('\r', '\n');
                _ = Resolve(root, Path.GetDirectoryName(path)!, target);
                links.Add((path, target));
                continue;
            }
            if (entry.Name.Length == 0)
            {
                Directory.CreateDirectory(path);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path, false);
            if (!OperatingSystem.IsWindows() && (unixMode & 0x1ff) != 0)
                File.SetUnixFileMode(path, (UnixFileMode)(unixMode & 0x1ff));
        }

        foreach (var (path, target) in links)
        {
            var actual = Path.Combine(Path.GetDirectoryName(path)!, target);
            if (Directory.Exists(actual))
                Directory.CreateSymbolicLink(path, target);
            else
                File.CreateSymbolicLink(path, target);
        }
    }

    private static string Resolve(string root, string parent, string path)
    {
        if (Path.IsPathRooted(path) || path.Contains('\\'))
            throw new InvalidDataException("The browser archive contains an invalid path.");
        var resolved = Path.GetFullPath(Path.Combine(parent, path));
        if (!resolved.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("The browser archive contains a path outside its destination.");
        return resolved;
    }
}
