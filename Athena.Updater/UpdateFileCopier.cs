using System;
using System.IO;

namespace Athena.Updater;

internal sealed class UpdateFileCopier
{
    private readonly byte[] _staged = new byte[64 * 1024];
    private readonly byte[] _installed = new byte[64 * 1024];

    public void Copy(string source, string destination)
    {
        if (SameInstalledFile(source, destination)) return;
        File.Copy(source, destination, overwrite: true);
    }

    // Preserve the installed identity of identical code so macOS can reuse its launch
    // caches. Timestamps alone cannot distinguish same-size changes in release archives.
    private bool SameInstalledFile(string source, string destination)
    {
        if (!OperatingSystem.IsMacOS()) return false;
        try
        {
            var from = new FileInfo(source);
            var to = new FileInfo(destination);
            if (!to.Exists || from.LinkTarget != null || to.LinkTarget != null || from.Length != to.Length
                || from.UnixFileMode != to.UnixFileMode || from.Attributes != to.Attributes) return false;
            using var input = from.OpenRead();
            using var existing = to.OpenRead();
            int count;
            while ((count = input.Read(_staged)) > 0)
            {
                if (existing.ReadAtLeast(_installed.AsSpan(0, count), count, throwOnEndOfStream: false) != count
                    || !_staged.AsSpan(0, count).SequenceEqual(_installed.AsSpan(0, count))) return false;
            }
            return existing.ReadByte() < 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A writable destination need not be readable. Let the usual copy decide.
            return false;
        }
    }
}
