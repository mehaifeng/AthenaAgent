using System;
using System.IO;
using System.Text;
using Serilog;

namespace Athena.UI.Services.ComputerUse;

// The portal binds this opaque credential to the application ID and the user's chosen monitor/devices.
internal sealed class PortalRestoreToken(string directory)
{
    internal static PortalRestoreToken ForUser()
    {
        var state = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        if (string.IsNullOrEmpty(state) || !Path.IsPathFullyQualified(state))
            state = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state");
        return new(Path.Combine(state, "AthenaAgent", "ComputerUse"));
    }

    private string TokenPath => Path.Combine(directory, "remote-desktop.token");
    internal bool IsAvailable => File.Exists(TokenPath);

    // Atomically claim a single-use token so concurrent app instances cannot submit it twice.
    internal string? Take()
    {
        var claimed = Path.Combine(directory, Guid.NewGuid().ToString("N"));
        try
        {
            if (!IsAvailable) return null;
            File.Move(TokenPath, claimed);
            if (new FileInfo(claimed).Length is <= 0 or > 4096) return null;
            var value = File.ReadAllText(claimed);
            return string.IsNullOrWhiteSpace(value) || value.Contains('\0') ? null : value;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally { DeleteTemporary(claimed); }
    }

    internal void Save(string value)
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrWhiteSpace(value)) return;
        var temp = Path.Combine(directory, Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            using (var stream = new FileStream(temp, new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
            }))
            {
                stream.Write(Encoding.UTF8.GetBytes(value));
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, TokenPath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warning("Could not remember the desktop portal authorization for the next task");
        }
        finally { DeleteTemporary(temp); }
    }

    private static void DeleteTemporary(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
