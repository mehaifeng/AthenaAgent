using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Athena.UI.Services.GameMode;

/// <summary>目录里的一项。路径由调用方掌握，这里只有名字。</summary>
public sealed record PolisFsEntry(string Name, bool IsDirectory, long Size, DateTimeOffset LastWriteUtc);

/// <summary>扫描到的一个文件。<see cref="RelativePath"/> 一律用 '/' 分隔，相对于工作区根目录。</summary>
public sealed record PolisScannedFile(string RelativePath, long Size, DateTimeOffset LastWriteUtc);

/// <summary>根目录下的一个文件夹：将来的一座建筑。</summary>
public sealed record PolisTopLevelDirectory(string Name, DateTimeOffset LastWriteUtc);

public sealed record PolisScanOptions
{
    /// <summary>最多访问多少个条目（文件 + 目录）。超出的部分不扫，所在建筑标成"未完全测绘"。</summary>
    public int MaxEntries { get; init; } = 20_000;

    /// <summary>最深扫到第几层（根目录的子项是第 1 层）。更深的目录同样算"未完全测绘"。</summary>
    public int MaxDepth { get; init; } = 16;

    public static PolisScanOptions Default { get; } = new();
}

public sealed record PolisScanResult(
    IReadOnlyList<PolisTopLevelDirectory> TopLevelDirectories,
    IReadOnlyList<PolisScannedFile> Files,
    IReadOnlySet<string> IncompleteTopLevel,
    bool RootIncomplete,
    IReadOnlyList<string> UnreadableDirectories,
    int SkippedDirectories,
    int VisitedEntries)
{
    /// <summary>有没有任何部分没扫完（条目上限、深度上限或读不了的目录）。</summary>
    public bool Truncated => RootIncomplete || IncompleteTopLevel.Count > 0;
}

/// <summary>
/// 把一个文件夹扫描成城邦的原材料。遍历本身是纯逻辑：目录内容由 <c>enumerate</c> 提供（相对路径 → 子项），
/// 生产环境接 <see cref="PolisFileSystem.Enumerator"/>，测试接内存里的树。
/// </summary>
public static class PolisScanner
{
    /// <summary>
    /// 跳过的目录：构建/依赖产物（与目录搜索共用 <see cref="GeneratedDirectories"/>），以及以 '.' 开头的隐藏目录
    /// （.git、.claude、.DS_Store 一类，对"我的文件"毫无意义）。
    /// </summary>
    public static bool IsSkippedDirectory(string name)
        => name.StartsWith('.') || GeneratedDirectories.IsGenerated(name);

    /// <summary>以 '.' 开头的隐藏文件同样不进城邦。</summary>
    public static bool IsSkippedFile(string name) => name.StartsWith('.');

    /// <summary>
    /// 按层遍历（先扫完第 1 层再扫第 2 层），所以条目上限先砍掉的是最深处：只要根目录本身的子项没超限，
    /// 每个顶层文件夹都一定出现在结果里，只是可能"未完全测绘"。同一目录内按名字的序数顺序，结果确定。
    /// <c>enumerate</c> 抛出的异常记为读不了的目录，不中断整次扫描；取消则照常抛出。
    /// </summary>
    public static PolisScanResult Scan(
        Func<string, IReadOnlyList<PolisFsEntry>> enumerate,
        PolisScanOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(enumerate);
        options ??= PolisScanOptions.Default;
        if (options.MaxEntries < 1) throw new ArgumentOutOfRangeException(nameof(options), "MaxEntries must be at least 1.");
        if (options.MaxDepth < 1) throw new ArgumentOutOfRangeException(nameof(options), "MaxDepth must be at least 1.");

        var topLevel = new List<PolisTopLevelDirectory>();
        var files = new List<PolisScannedFile>();
        var incomplete = new HashSet<string>(StringComparer.Ordinal);
        var unreadable = new List<string>();
        var rootIncomplete = false;
        var skipped = 0;
        var visited = 0;

        // (相对路径, 深度)；根目录深度 0，它的子项深度 1
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((string.Empty, 0));

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (dir, depth) = queue.Dequeue();

            IReadOnlyList<PolisFsEntry> children;
            try
            {
                children = enumerate(dir);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                unreadable.Add(dir);
                MarkIncomplete(dir, isDirectory: true);
                continue;
            }

            foreach (var entry in children.OrderBy(e => e.Name, StringComparer.Ordinal))
            {
                var relative = dir.Length == 0 ? entry.Name : dir + "/" + entry.Name;
                if (entry.IsDirectory ? IsSkippedDirectory(entry.Name) : IsSkippedFile(entry.Name))
                {
                    if (entry.IsDirectory) skipped++;
                    continue;
                }

                // 每座建筑都要出现，哪怕已经超出预算：顶层文件夹只是一个名字，代价可以忽略
                if (entry.IsDirectory && depth == 0) topLevel.Add(new PolisTopLevelDirectory(entry.Name, entry.LastWriteUtc));

                if (visited >= options.MaxEntries)
                {
                    MarkIncomplete(relative, entry.IsDirectory);
                    continue;
                }

                visited++;
                if (!entry.IsDirectory)
                {
                    files.Add(new PolisScannedFile(relative, Math.Max(0, entry.Size), entry.LastWriteUtc));
                }
                else if (depth + 1 < options.MaxDepth)
                {
                    queue.Enqueue((relative, depth + 1));
                }
                else
                {
                    MarkIncomplete(relative, isDirectory: true);
                }
            }

            if (visited >= options.MaxEntries && queue.Count > 0)
            {
                // 预算用完：剩下的目录一个都不再列，直接记成"未完全测绘"
                foreach (var (pending, _) in queue) MarkIncomplete(pending, isDirectory: true);
                break;
            }
        }

        return new PolisScanResult(topLevel, files, incomplete, rootIncomplete, unreadable, skipped, visited);

        void MarkIncomplete(string relativePath, bool isDirectory)
        {
            var slash = relativePath.IndexOf('/');
            // 根目录本身、或根目录下的文件没扫完：算广场的；其余记到所属的顶层文件夹上
            if (relativePath.Length == 0 || (slash < 0 && !isDirectory)) rootIncomplete = true;
            else incomplete.Add(slash < 0 ? relativePath : relativePath[..slash]);
        }
    }
}

/// <summary>真实文件系统的 <see cref="PolisScanner"/> 适配器。</summary>
public static class PolisFileSystem
{
    /// <summary>
    /// 返回一个按相对路径列目录的函数。软链接/重解析点目录不进入：顺着它走可能走出工作区，也可能成环。
    /// </summary>
    public static Func<string, IReadOnlyList<PolisFsEntry>> Enumerator(string root)
    {
        var fullRoot = Path.GetFullPath(root);
        return relative =>
        {
            var path = relative.Length == 0 ? fullRoot : Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            var info = new DirectoryInfo(path);
            var result = new List<PolisFsEntry>();
            foreach (var item in info.EnumerateFileSystemInfos())
            {
                var isDirectory = (item.Attributes & FileAttributes.Directory) != 0;
                if (isDirectory && (item.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                var size = item is FileInfo file ? file.Length : 0;
                result.Add(new PolisFsEntry(item.Name, isDirectory, size, new DateTimeOffset(item.LastWriteTimeUtc, TimeSpan.Zero)));
            }
            return result;
        };
    }
}
