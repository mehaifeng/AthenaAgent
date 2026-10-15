using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Athena.UI.Services.GameMode;

/// <summary>
/// 一个人住的那座楼（文件夹）的详情：名字、类型、文件数与体积、按类型的分布、最近改动、第一层的条目（子文件夹在前，最多
/// <see cref="PolisBriefs.MaxEntries"/> 个，<see cref="MoreEntries"/> 是没列出来的），README 的开头（有的话）。村舍与摊位没有文件夹详情。
/// </summary>
public sealed record PolisBuildingBrief(
    string Key,
    PolisBuildingKind Kind,
    int Files,
    long Bytes,
    IReadOnlyDictionary<string, int> Kinds,
    DateTimeOffset LastModified,
    IReadOnlyList<PolisFsEntry> Entries,
    int MoreEntries,
    string? Readme);

/// <summary>整个项目的大概：工作区名、最多 <see cref="PolisBriefs.MaxProjectBuildings"/> 座楼（名字、类型、文件数）、市集的摊位、根目录 README 的开头。</summary>
public sealed record PolisProjectBrief(
    string Name,
    IReadOnlyList<(string Key, PolisBuildingKind Kind, int Files)> Buildings,
    int MoreBuildings,
    IReadOnlyList<string> Stalls,
    string? Readme);

/// <summary>
/// NPC 的上下文里那座楼（详）与整个项目（略）（第四轮需求 3，2026-10-15，用户：注入他所住建筑（目录）信息（详）和整个项目（略）上下文，
/// 支持闲谈和项目谈论）。读法有界：第一层最多 <see cref="MaxEntries"/> 个条目、README 只读开头（楼 <see cref="ReadmeChars"/> 字，项目
/// <see cref="ProjectReadmeChars"/> 字，文件最多读 <see cref="MaxReadmeBytes"/> 字节），隐藏条目与生成目录不列（<see cref="PolisScanner.IsSkippedDirectory"/>），
/// 读文件只在工作区里（字面上、以及解析软链之后，<see cref="TextReader"/>）。列目录与读文件都由调用方给（测试给内存里的树），这里是纯函数。
/// 只在你开口和他说话时进提示词；两个 NPC 自己闲聊（没有人按回车）只带楼名与类型，不带任何文件内容（<see cref="PolisTalk"/>）。
/// </summary>
public static class PolisBriefs
{
    public const int MaxEntries = 40;
    public const int ReadmeChars = 1500;
    public const int ProjectReadmeChars = 600;
    public const int MaxProjectBuildings = 15;
    public const int MaxReadmeBytes = 64 * 1024;

    /// <summary>README 的名字（不分大小写）：README、README.md、README.txt、README.markdown、README.rst，以及中文的"说明"。</summary>
    public static bool IsReadme(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return (string.Equals(stem, "readme", StringComparison.OrdinalIgnoreCase) || string.Equals(stem, "说明", StringComparison.Ordinal))
            && ext is "" or ".md" or ".txt" or ".markdown" or ".rst";
    }

    /// <summary>第一层的条目：跳过隐藏条目与生成目录，子文件夹在前、同类按名字的序数；README 排在文件的最前面。</summary>
    public static IReadOnlyList<PolisFsEntry> Listing(IReadOnlyList<PolisFsEntry> children)
    {
        ArgumentNullException.ThrowIfNull(children);
        return children
            .Where(e => e.IsDirectory ? !PolisScanner.IsSkippedDirectory(e.Name) : !PolisScanner.IsSkippedFile(e.Name))
            .OrderByDescending(e => e.IsDirectory)
            .ThenByDescending(e => !e.IsDirectory && IsReadme(e.Name))
            .ThenBy(e => e.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// 一座楼的详情。<paramref name="enumerate"/> 按工作区相对路径列目录、<paramref name="readText"/> 按相对路径读开头的若干字；
    /// 任何一个为 null（文件夹不见了、神殿岛）或读不出来，就只有夹具里的那些数。
    /// </summary>
    public static PolisBuildingBrief Building(
        PolisFixtureBuilding building,
        Func<string, IReadOnlyList<PolisFsEntry>>? enumerate,
        Func<string, int, string?>? readText)
    {
        ArgumentNullException.ThrowIfNull(building);
        IReadOnlyList<PolisFsEntry> listing = Array.Empty<PolisFsEntry>();
        string? readme = null;
        if (enumerate != null && !PolisHouseholds.IsHouseholdKey(building.Key))
        {
            try
            {
                listing = Listing(enumerate(building.Key));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // 列不出来（权限、刚被删掉）：只说夹具里的那些数，这一句照样能聊
                listing = Array.Empty<PolisFsEntry>();
            }
            var readmeEntry = listing.FirstOrDefault(e => !e.IsDirectory && IsReadme(e.Name));
            if (readmeEntry != null && readText != null) readme = readText(building.Key + "/" + readmeEntry.Name, ReadmeChars);
        }
        return new PolisBuildingBrief(
            building.Key, building.Kind, building.Files, building.Bytes, building.Kinds, building.LastModified,
            listing.Take(MaxEntries).ToList(), Math.Max(0, listing.Count - MaxEntries), readme);
    }

    /// <summary>整个项目的大概（文件夹的建筑按文件数从多到少，最多 <see cref="MaxProjectBuildings"/> 座；村舍不算）。</summary>
    public static PolisProjectBrief Project(
        string name,
        IReadOnlyList<PolisFixtureBuilding> buildings,
        IReadOnlyList<string>? stalls,
        Func<string, IReadOnlyList<PolisFsEntry>>? enumerate,
        Func<string, int, string?>? readText)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(buildings);
        var folders = buildings.Where(b => !PolisHouseholds.IsHouseholdKey(b.Key))
            .OrderByDescending(b => b.Files).ThenBy(b => b.Key, StringComparer.Ordinal)
            .ToList();
        string? readme = null;
        if (enumerate != null && readText != null)
        {
            try
            {
                var top = enumerate(string.Empty).FirstOrDefault(e => !e.IsDirectory && IsReadme(e.Name));
                if (top != null) readme = readText(top.Name, ProjectReadmeChars);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                readme = null;
            }
        }
        return new PolisProjectBrief(
            name,
            folders.Take(MaxProjectBuildings).Select(b => (b.Key, b.Kind, b.Files)).ToList(),
            Math.Max(0, folders.Count - MaxProjectBuildings),
            (stalls ?? Array.Empty<string>()).Take(MaxProjectBuildings).ToList(),
            readme);
    }

    /// <summary>
    /// 按工作区相对路径读一个文本文件的开头：路径必须字面上、以及解析软链之后都在工作区里，文件最多读 <see cref="MaxReadmeBytes"/> 字节，
    /// 按 UTF-8 解码（坏字节换成替换符），去掉控制字符（留换行），最多 <c>maxChars</c> 字。读不了就是 null。
    /// </summary>
    public static Func<string, int, string?> TextReader(string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var fullRoot = Path.GetFullPath(root);
        return (relative, maxChars) =>
        {
            try
            {
                var path = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!IsInside(fullRoot, path)) return null;
                if (!FileSystemService.TryResolveFully(fullRoot, out var realRoot) || !FileSystemService.TryResolveFully(path, out var realPath)) return null;
                if (!IsInside(realRoot, realPath)) return null;
                var info = new FileInfo(realPath);
                if (!info.Exists) return null;
                var length = (int)Math.Min(info.Length, MaxReadmeBytes);
                var buffer = new byte[length];
                using (var stream = new FileStream(realPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    var read = 0;
                    while (read < length)
                    {
                        var n = stream.Read(buffer, read, length - read);
                        if (n == 0) break;
                        read += n;
                    }
                    length = read;
                }
                return Clean(Encoding.UTF8.GetString(buffer, 0, length), maxChars);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException)
            {
                return null;
            }
        };
    }

    /// <summary>去掉控制字符（留换行与制表）、把连着的空行收成一个，最多 <paramref name="maxChars"/> 字（超出加"…"）。空的算没有。</summary>
    public static string? Clean(string? text, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var sb = new StringBuilder(Math.Min(text.Length, maxChars + 8));
        var blank = 0;
        foreach (var raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            var line = new string(raw.Where(c => c == '\t' || !char.IsControl(c)).ToArray()).TrimEnd();
            if (line.Length == 0) { if (++blank > 1) continue; }
            else blank = 0;
            sb.Append(line).Append('\n');
            if (sb.Length > maxChars) break;
        }
        var s = sb.ToString().Trim();
        if (s.Length == 0) return null;
        if (s.Length <= maxChars) return s;
        var cut = maxChars;
        if (char.IsHighSurrogate(s[cut - 1])) cut--;
        return s[..cut] + "…";
    }

    private static bool IsInside(string root, string path)
    {
        var r = Path.TrimEndingDirectorySeparator(root);
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        return path.Length > r.Length && path.StartsWith(r, comparison) && (path[r.Length] == Path.DirectorySeparatorChar || path[r.Length] == Path.AltDirectorySeparatorChar);
    }
}
