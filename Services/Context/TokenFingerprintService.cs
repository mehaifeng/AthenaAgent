using Athena.UI.Services.Interfaces;
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Athena.UI.Services.Context;

public sealed class TokenFingerprintService
{
    private readonly byte[] _key;
    public string KeyId { get; }

    public TokenFingerprintService(IPlatformPathService paths)
    {
        var path = paths.GetTokenCalibrationKeyPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            _key = File.ReadAllBytes(path);
            if (_key.Length != 32) _key = ReplaceKey(path);
            else RestrictPermissions(path);
        }
        else
        {
            _key = ReplaceKey(path);
        }
        KeyId = Convert.ToHexString(SHA256.HashData(_key))[..16].ToLowerInvariant();
    }

    public string Compute(string value)
        => Convert.ToHexString(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static byte[] ReplaceKey(string path)
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(key);
            stream.Flush(flushToDisk: true);
        }
        if (!OperatingSystem.IsWindows())
            RestrictPermissions(temp);
        File.Move(temp, path, overwrite: true);
        return key;
    }

    private static void RestrictPermissions(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
