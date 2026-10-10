using System;
using System.IO;

namespace Athena.UI.Services.GameMode;

/// <summary>
/// 城邦里的路径：一律是工作区相对路径，'/' 分隔。网页只见得到这种写法（设计稿 12.4），
/// C# 在这里把它和磁盘上的绝对路径互相换算，并且只认"干净"的写法——不允许 '..'、根路径、反斜杠。
/// </summary>
public static class PolisPaths
{
    public const int MaxRelativePathLength = 1024;

    /// <summary>
    /// 是不是一条干净的相对路径：非空、不以 '/' 开头、不含反斜杠与盘符、没有空段 / '.' / '..'、没有控制字符。
    /// 不接受"差不多"的写法：网页发来的路径要么一字不差地落在工作区里，要么拒绝。
    /// </summary>
    public static bool IsCleanRelativePath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > MaxRelativePathLength) return false;
        if (path[0] == '/' || path.Contains('\\') || path.Contains(':')) return false;
        foreach (var ch in path)
        {
            if (char.IsControl(ch)) return false;
        }
        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment == "." || segment == ".." || segment.Length > 255) return false;
        }
        return true;
    }

    /// <summary>所属的建筑（顶层文件夹名）；根目录下的文件返回空串（广场）。</summary>
    public static string TopLevelOf(string relativePath)
    {
        var slash = relativePath.IndexOf('/');
        return slash < 0 ? string.Empty : relativePath[..slash];
    }

    /// <summary>路径里有没有被城邦扫描跳过的段（隐藏项、构建 / 依赖目录）：这些地方的改动不改变城邦。</summary>
    public static bool IsOutsideSurvey(string relativePath)
    {
        var segments = relativePath.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            if (segment.Length == 0) continue;
            var isLast = i == segments.Length - 1;
            // 最后一段可能是文件：隐藏文件跳过，生成目录的名字只在目录段上算数（一个叫 build 的文件照样进城）
            if (isLast ? PolisScanner.IsSkippedFile(segment) : PolisScanner.IsSkippedDirectory(segment)) return true;
        }
        return false;
    }

    /// <summary>
    /// 绝对路径 → 工作区相对路径（字面换算，不解析软链）；不在工作区里返回 null，工作区根本身返回空串。
    /// </summary>
    public static string? ToRelative(string workspaceRoot, string fullPath, StringComparison comparison)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(fullPath));
        if (full.Equals(root, comparison)) return string.Empty;
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, comparison)) return null;
        return full[prefix.Length..].Replace(Path.DirectorySeparatorChar, '/');
    }

    /// <summary>
    /// 工作区相对路径 → 绝对路径。只接受 <see cref="IsCleanRelativePath"/> 的写法；拼出来的结果再核对一次
    /// 仍在根之内（字面）。软链指到哪里由调用方另行核对（<see cref="PolisIntents"/> 用真实路径再比一遍）。
    /// </summary>
    public static string? ToFull(string workspaceRoot, string relativePath, StringComparison comparison)
    {
        if (!IsCleanRelativePath(relativePath)) return null;
        var root = Path.GetFullPath(workspaceRoot);
        var full = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        return ToRelative(root, full, comparison) is { Length: > 0 } ? full : null;
    }

    /// <summary>当前平台文件系统的默认比较方式：Linux 区分大小写，macOS / Windows 默认不区分。</summary>
    public static StringComparison PlatformComparison
        => OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
}
