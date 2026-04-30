using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Athena.Updater;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            var sessionPath = ParseSessionPath(args);
            if (string.IsNullOrWhiteSpace(sessionPath) || !File.Exists(sessionPath))
            {
                Console.Error.WriteLine("Missing --session path.");
                return 2;
            }

            var sessionJson = await File.ReadAllTextAsync(sessionPath);
            var session = JsonSerializer.Deserialize<UpdateSession>(sessionJson, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            if (session == null)
            {
                Console.Error.WriteLine("Invalid session payload.");
                return 3;
            }

            await WaitForProcessExitAsync(session.MainProcessId, timeoutSeconds: 60);
            ApplyUpdate(session);
            StartMainApp(session);
            return 0;
        }
        catch (UnauthorizedAccessException ex)
        {
            Console.Error.WriteLine($"Permission denied: {ex.Message}");
            return 5;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static string ParseSessionPath(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--session", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                return args[i + 1];
            }
        }

        return string.Empty;
    }

    private static async Task WaitForProcessExitAsync(int pid, int timeoutSeconds)
    {
        try
        {
            var process = Process.GetProcessById(pid);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));
            while (!process.HasExited && !cts.IsCancellationRequested)
            {
                await Task.Delay(200, cts.Token);
            }
        }
        catch (ArgumentException)
        {
            // Process already exited.
        }
        catch (TaskCanceledException)
        {
            // Timeout reached; proceed anyway.
        }
    }

    private static void ApplyUpdate(UpdateSession session)
    {
        Directory.CreateDirectory(session.InstallDirectory);
        var preserveRoots = new HashSet<string>(
            (session.PreservePaths ?? new List<string>())
            .Select(NormalizeRelativePath)
            .Where(p => !string.IsNullOrWhiteSpace(p)),
            StringComparer.OrdinalIgnoreCase);

        foreach (var sourceDir in Directory.GetDirectories(session.StagingDirectory))
        {
            var relative = Path.GetRelativePath(session.StagingDirectory, sourceDir);
            if (ShouldPreserve(relative, preserveRoots))
            {
                continue;
            }

            var destinationDir = Path.Combine(session.InstallDirectory, relative);
            CopyDirectory(sourceDir, destinationDir);
        }

        foreach (var sourceFile in Directory.GetFiles(session.StagingDirectory))
        {
            var relative = Path.GetRelativePath(session.StagingDirectory, sourceFile);
            if (ShouldPreserve(relative, preserveRoots))
            {
                continue;
            }

            var destinationFile = Path.Combine(session.InstallDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile) ?? session.InstallDirectory);
            File.Copy(sourceFile, destinationFile, true);
        }
    }

    private static void CopyDirectory(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var destinationFile = Path.Combine(destinationDir, Path.GetFileName(file));
            File.Copy(file, destinationFile, true);
        }

        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            var childDestination = Path.Combine(destinationDir, Path.GetFileName(dir));
            CopyDirectory(dir, childDestination);
        }
    }

    private static bool ShouldPreserve(string relativePath, HashSet<string> preserveRoots)
    {
        var normalized = NormalizeRelativePath(relativePath);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        return preserveRoots.Any(root =>
            normalized.Equals(root, StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith($"{root}/", StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeRelativePath(string value)
    {
        return value.Replace('\\', '/').Trim('/');
    }

    private static void StartMainApp(UpdateSession session)
    {
        var executablePath = Path.Combine(session.InstallDirectory, session.EntryExecutable);
        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException("Updated entry executable not found.", executablePath);
        }

        if (!OperatingSystem.IsWindows())
        {
            TryEnsureExecutable(executablePath);
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            WorkingDirectory = session.InstallDirectory
        });
    }

    private static void TryEnsureExecutable(string executablePath)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "chmod",
                Arguments = $"+x \"{executablePath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            })?.WaitForExit(5000);
        }
        catch
        {
            // Best effort.
        }
    }

    private sealed class UpdateSession
    {
        public int MainProcessId { get; init; }
        public string InstallDirectory { get; init; } = string.Empty;
        public string StagingDirectory { get; init; } = string.Empty;
        public string EntryExecutable { get; init; } = string.Empty;
        public List<string>? PreservePaths { get; init; }
    }
}
