using Athena.UI.Services.Interfaces;
using Serilog;
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
        var path = paths.GetRequestFingerprintKeyPath();
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
        DeleteLegacyCalibrationFile(paths.GetLegacyTokenCalibrationFilePath());
    }

    /// <summary>
    /// token 估算器及其校准数据已经删除：只认供应商回报的 usage。旧版落下的校准文件不会再被读取，
    /// 在这里顺手清掉；失败无所谓（文件只是占点磁盘），只记 Debug。
    /// </summary>
    private static void DeleteLegacyCalibrationFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Debug(ex, "Could not remove the legacy token calibration file {Path}", path);
        }
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
