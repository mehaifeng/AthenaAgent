using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Athena.UI.Services;

/// <summary>
/// 本地系统文件服务实现类
/// </summary>
public class FileSystemService : IFileSystemService
{
    private readonly IConfigService _configService;
    private readonly IPlatformPathService _pathService;
    private readonly ILogger _logger;

    private const int ChunkSizeBytes = 50 * 1024; // 50KB per chunk for reading large files

    public FileSystemService(IConfigService configService, IPlatformPathService pathService, ILogger logger)
    {
        _configService = configService;
        _pathService = pathService;
        _logger = logger.ForContext<FileSystemService>();
    }

    private string ExpandPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        var expanded = Environment.ExpandEnvironmentVariables(path);
        if (expanded.StartsWith("~"))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home))
            {
                expanded = expanded.Length == 1 ? home : Path.Combine(home, expanded.Substring(2));
            }
        }
        else if (!Path.IsPathRooted(expanded) && !expanded.StartsWith("%"))
        {
            var kbDir = _pathService.GetKnowledgeBaseDirectory();
            var kbPath = Path.GetFullPath(Path.Combine(kbDir, expanded));
            if (File.Exists(kbPath) || Directory.Exists(kbPath)) return kbPath;

            var appDataDir = _pathService.GetAppDataDirectory();
            var appDataPath = Path.GetFullPath(Path.Combine(appDataDir, expanded));
            if (File.Exists(appDataPath) || Directory.Exists(appDataPath)) return appDataPath;

            return kbPath;
        }
        return expanded;
    }

    private void ValidatePathAndSecurity(string absolutePath, bool isWriteOperation, bool isDirectoryOperation = false, long dataSize = 0, bool enforceSizeLimit = true)
    {
        if (string.IsNullOrWhiteSpace(absolutePath)) throw new ArgumentException("File path cannot be empty");
        var policy = _configService.Load().FileSystemPolicy;
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var fullPath = Path.GetFullPath(ExpandPath(absolutePath));

        _logger.Debug(
            "FileSystem validation started: Path={Path}, IsWrite={IsWrite}, IsDir={IsDir}, DataSize={DataSize}",
            fullPath, isWriteOperation, isDirectoryOperation, dataSize);

        // 不跟随符号链接时，把路径解析到真实目标，阻止「沙箱内软链指向 /etc、~/.ssh」这类越界逃逸。
        // 字面路径与真实路径都要过黑名单：软链本身所在位置、以及它指向的目标，任一命中即拒绝。
        var pathsToCheck = new List<string> { fullPath };
        if (!policy.Global.FollowSymlinks)
        {
            var realPath = ResolveRealPath(fullPath);
            if (!realPath.Equals(fullPath, comparison))
                pathsToCheck.Add(realPath);
        }

        if (isWriteOperation)
        {
            var configPath = Path.GetFullPath(_pathService.GetConfigFilePath());
            if (pathsToCheck.Any(p => p.Equals(configPath, comparison)))
            {
                _logger.Warning("FileSystem config-file write protection rejected: Path={Path}", fullPath);
                throw new UnauthorizedAccessException("Self-protection: the application configuration file cannot be modified via filesystem tools.");
            }
        }

        var platform = OperatingSystem.IsWindows() ? policy.Platforms.Windows : (OperatingSystem.IsMacOS() ? policy.Platforms.MacOS : policy.Platforms.Linux);
        var accessRules = isWriteOperation ? platform.WriteAccess : platform.ReadAccess;

        var blockedDirs = accessRules.BlockedDirectories.Select(dir => Path.GetFullPath(ExpandPath(dir))).ToList();
        if (pathsToCheck.Any(p => blockedDirs.Any(dir => p.StartsWith(dir, comparison))))
        {
            _logger.Warning(
                "FileSystem blocked-directory denied: Path={Path}, BlockedDirs={BlockedDirs}",
                fullPath, string.Join(";", blockedDirs));
            throw new UnauthorizedAccessException("System critical directory is protected by security policy; access denied.");
        }

        // 附件库为“受信读取区”：解析后的大文档/大文本是有意落盘供模型按需分块读取的，
        // 整体大小限制对它们没有意义（实际返回的是 50KB 切片）。因此对该目录下的读取
        // 豁免整文件大小护栏，仅保留目录边界与分块约束。
        bool trustedRead = !isWriteOperation && IsWithinAttachmentRoot(fullPath);

        long targetLimit = isWriteOperation ? policy.Global.MaxWriteSizeBytes : policy.Global.MaxReadSizeBytes;
        if (enforceSizeLimit && (dataSize > targetLimit || (!isWriteOperation && !isDirectoryOperation && !trustedRead && File.Exists(fullPath) && new FileInfo(fullPath).Length > targetLimit)))
        {
            _logger.Warning(
                "FileSystem size quota exceeded: Path={Path}, DataSize={DataSize}, Limit={Limit}",
                fullPath, dataSize, targetLimit);
            throw new InvalidOperationException($"Operation exceeds size limit ({targetLimit} bytes).");
        }
    }

    /// <summary>
    /// 把路径解析到真实目标：沿最近的已存在祖先解析全部符号链接组件，再拼回尚不存在的尾部
    /// （写/建新文件时叶子可能还不存在）。覆盖两类逃逸：叶子本身是软链，或路径中某级目录是软链。
    /// 解析失败时回退原路径（校验方仍会用字面路径兜底）。
    /// </summary>
    private static string ResolveRealPath(string fullPath)
    {
        try
        {
            var tail = new List<string>();
            var current = fullPath;

            while (!File.Exists(current) && !Directory.Exists(current))
            {
                var parent = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(parent) || parent.Equals(current, StringComparison.Ordinal))
                    return fullPath; // 到根仍不存在，无从解析
                tail.Add(Path.GetFileName(current));
                current = parent;
            }

            var real = CanonicalizeExisting(current);

            tail.Reverse();
            foreach (var seg in tail)
                real = Path.Combine(real, seg);

            return Path.GetFullPath(real);
        }
        catch
        {
            return fullPath;
        }
    }

    /// <summary>
    /// 逐级解析一个已存在路径的所有符号链接组件（含中间目录软链），返回规范化后的真实路径。
    /// </summary>
    private static string CanonicalizeExisting(string existingPath)
    {
        var root = Path.GetPathRoot(existingPath) ?? string.Empty;
        var segments = existingPath.Substring(root.Length)
            .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);

        var accumulated = string.IsNullOrEmpty(root) ? Path.DirectorySeparatorChar.ToString() : root;
        foreach (var seg in segments)
        {
            accumulated = Path.Combine(accumulated, seg);

            FileSystemInfo? info = Directory.Exists(accumulated)
                ? new DirectoryInfo(accumulated)
                : File.Exists(accumulated) ? new FileInfo(accumulated) : null;
            if (info == null) break;

            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            if (target != null)
            {
                // 软链目标可能是相对路径：相对链接所在目录解析为绝对路径。
                accumulated = Path.IsPathRooted(target.FullName)
                    ? target.FullName
                    : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(accumulated) ?? accumulated, target.FullName));
            }
        }
        return Path.GetFullPath(accumulated);
    }

    private bool IsWithinAttachmentRoot(string fullPath)
    {
        try
        {
            var root = Path.GetFullPath(_pathService.GetAttachmentDirectory());
            var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var normalizedRoot = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(normalizedRoot, comparison);
        }
        catch
        {
            return false;
        }
    }

    public async Task<string?> ReadFileAsync(string absolutePath, int? startLine = null, int? endLine = null, string? sectionTitle = null, int? chunkIndex = null, bool includeLineNumbers = false)
    {
        var fullPath = Path.GetFullPath(ExpandPath(absolutePath));
        _logger.Debug("FileSystem ReadFile: Path={Path}", fullPath);
        ValidatePathAndSecurity(fullPath, false);

        if (!File.Exists(fullPath))
        {
            _logger.Debug("FileSystem ReadFile target missing: Path={Path}", fullPath);
            return null;
        }

        var fileInfo = new FileInfo(fullPath);

        // Handle a Markdown ATX section, including nested subsections until the next
        // heading at the same or a higher level.
        if (!string.IsNullOrEmpty(sectionTitle))
        {
            var lines = await File.ReadAllLinesAsync(fullPath);
            var sectionLines = new List<string>();
            int sectionStart = -1;
            int sectionLevel = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var heading = Regex.Match(line, @"^\s{0,3}(#{1,6})\s+(.+?)\s*#*\s*$");
                if (sectionStart < 0 && heading.Success
                    && string.Equals(heading.Groups[2].Value.Trim(), sectionTitle.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    sectionStart = i;
                    sectionLevel = heading.Groups[1].Value.Length;
                    sectionLines.Add(line);
                    continue;
                }
                if (sectionStart >= 0 && heading.Success && heading.Groups[1].Value.Length <= sectionLevel) break;
                if (sectionStart >= 0) sectionLines.Add(line);
            }
            return includeLineNumbers
                ? NumberLines(sectionLines, Math.Max(0, sectionStart) + 1)
                : string.Join(Environment.NewLine, sectionLines);
        }

        // Handle Line Ranges
        if (startLine.HasValue)
        {
            var lines = await File.ReadAllLinesAsync(fullPath);
            int start = Math.Max(0, startLine.Value - 1);
            int end = endLine.HasValue ? Math.Min(lines.Length, endLine.Value) : lines.Length;
            if (start >= lines.Length) return string.Empty;
            var range = lines.Skip(start).Take(end - start).ToList();
            return includeLineNumbers
                ? NumberLines(range, start + 1)
                : string.Join(Environment.NewLine, range);
        }

        // Handle Chunking for large files
        if (fileInfo.Length > 50 * 1024 || chunkIndex.HasValue)
        {
            int idx = chunkIndex ?? 0;
            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read);

            // 块边界必须落在 UTF-8 字符起始字节上，否则跨边界的多字节字符会被解码成 U+FFFD——
            // 两侧各留下一段乱码，而模型看不出那是截断产物。一律向前对齐到字符首字节：
            // 边界处的半个字符完整归属后一块，块与块严丝合缝，既不丢内容也不重复。
            long start = await AlignToCharBoundaryAsync(stream, (long)idx * ChunkSizeBytes);
            long end = await AlignToCharBoundaryAsync(stream, (long)(idx + 1) * ChunkSizeBytes);
            if (start >= stream.Length) return string.Empty;

            stream.Seek(start, SeekOrigin.Begin);
            byte[] buffer = new byte[Math.Max(0, end - start)];
            int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length));
            var text = Encoding.UTF8.GetString(buffer, 0, read);
            if (!includeLineNumbers) return text;

            // 分块按字节偏移，需先统计块起点之前的换行数，才能给出行号。
            var prefix = await ScanPrefixAsync(fullPath, start);
            var chunkLines = text.Split('\n').ToList();
            bool endsMidLine = chunkLines.Count > 0 && chunkLines[^1].Length > 0 && start + read < stream.Length;
            if (chunkLines.Count > 0 && chunkLines[^1].Length == 0) chunkLines.RemoveAt(chunkLines.Count - 1); // 块以换行结尾时的空尾元素

            long firstLineNo = prefix.Newlines + 1;
            var body = NumberLines(chunkLines, (int)firstLineNo);

            // 块边界落在行中部时必须说明，否则模型会把半行当成完整行去构造 SEARCH。
            var notes = new StringBuilder();
            if (prefix.CharsSinceNewline > 0)
                notes.Append($"[本块自第 {firstLineNo} 行第 {prefix.CharsSinceNewline + 1} 个字符处开始，该行前半部分在上一块]\n");
            notes.Append(body);
            if (endsMidLine)
                notes.Append($"\n[第 {firstLineNo + chunkLines.Count - 1} 行在此截断，后续内容在下一块]");
            return notes.ToString();
        }

        if (includeLineNumbers)
        {
            var lines = await File.ReadAllLinesAsync(fullPath);
            return NumberLines(lines, 1);
        }

        return await File.ReadAllTextAsync(fullPath, Encoding.UTF8);
    }

    /// <summary>
    /// 为每行加 1-based 行号前缀（格式 "N | 内容"），供模型精确定位后再编辑。
    /// </summary>
    private static string NumberLines(IEnumerable<string> lines, int firstLineNumber)
    {
        var sb = new StringBuilder();
        int n = firstLineNumber;
        foreach (var line in lines)
        {
            sb.Append(n).Append(" | ").Append(line).Append('\n');
            n++;
        }
        if (sb.Length > 0) sb.Length--; // 去掉末尾换行，与原有输出保持一致
        return sb.ToString();
    }

    /// <summary>
    /// 单趟扫描文件前 offset 字节，得到换行符数量（用于行号）与最后一个换行符之后的字符数
    /// （用于说明块起点落在某行的第几个字符）。字符数按 UTF-8 首字节计数，不需要解码。
    /// </summary>
    private static async Task<(long Newlines, long CharsSinceNewline)> ScanPrefixAsync(string fullPath, long offset)
    {
        long newlines = 0, charsSinceNewline = 0;
        var buffer = new byte[64 * 1024];
        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read);
        long remaining = Math.Min(offset, stream.Length);
        while (remaining > 0)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)));
            if (read <= 0) break;
            for (int i = 0; i < read; i++)
            {
                if (buffer[i] == (byte)'\n') { newlines++; charsSinceNewline = 0; }
                else if ((buffer[i] & 0xC0) != 0x80) charsSinceNewline++; // 跳过续字节，只数字符首字节
            }
            remaining -= read;
        }
        return (newlines, charsSinceNewline);
    }

    /// <summary>
    /// 把字节位置向前对齐到 UTF-8 字符的首字节，保证分块边界不会切开多字节字符。
    /// </summary>
    private static async Task<long> AlignToCharBoundaryAsync(FileStream stream, long position)
    {
        long pos = Math.Clamp(position, 0, stream.Length);
        if (pos <= 0 || pos >= stream.Length) return pos;

        var probe = new byte[1];
        // UTF-8 字符最长 4 字节，最多回退 3 次即可落到首字节。
        for (int i = 0; i < 3 && pos > 0; i++)
        {
            stream.Seek(pos, SeekOrigin.Begin);
            if (await stream.ReadAsync(probe.AsMemory(0, 1)) != 1) break;
            if ((probe[0] & 0xC0) != 0x80) break; // 已是首字节
            pos--;
        }
        return pos;
    }

    public async Task<bool> WriteFileAsync(string absolutePath, string content)
    {
        var fullPath = Path.GetFullPath(ExpandPath(absolutePath));
        var sizeBytes = Encoding.UTF8.GetByteCount(content);
        _logger.Information(
            "FileSystem WriteFile started: Path={Path}, Bytes={Bytes}",
            fullPath, sizeBytes);
        ValidatePathAndSecurity(fullPath, true, false, sizeBytes);
        // 无 BOM 写入 + 原子替换，避免污染文件并防止写入中崩溃损坏数据。
        await AtomicWriteAsync(fullPath, EncodeUtf8(content, withBom: false));
        _logger.Information("FileSystem WriteFile succeeded: Path={Path}", fullPath);
        return true;
    }

    public async Task<FileUpdateResult> ModifyFileWithDiffAsync(string absolutePath, string diffContent, bool fuzzyMatch = true, bool replaceAll = false)
    {
        var fullPath = Path.GetFullPath(ExpandPath(absolutePath));
        _logger.Information("FileSystem ModifyFile started: Path={Path}", fullPath);
        ValidatePathAndSecurity(fullPath, true);
        if (!File.Exists(fullPath)) return new FileUpdateResult { Success = false, Message = "文件不存在。" };

        var parse = DiffApplier.Parse(diffContent);
        if (parse.Error != null) return new FileUpdateResult { Success = false, Message = parse.Error };

        // 读原始字节 → 探测编码/换行风格 → 在按 \n 规范化的字符空间内匹配。
        byte[] raw = await File.ReadAllBytesAsync(fullPath);
        string text = DecodeUtf8(raw);
        var profile = FileEncodingProfile.Detect(raw, text);

        var normalized = text.Replace("\r\n", "\n").Replace("\r", "\n");

        var applyResult = DiffApplier.Apply(normalized, parse.Blocks, fuzzyMatch, replaceAll);
        if (!applyResult.Success) return applyResult;

        // 按原换行风格与 BOM 还原落盘，避免污染文件。
        string newText = applyResult.ModifiedContent ?? normalized;
        if (profile.DominantEol != "\n") newText = newText.Replace("\n", profile.DominantEol);
        byte[] outBytes = EncodeUtf8(newText, profile.HasUtf8Bom);

        // 以最终字节数复核写入配额。
        ValidatePathAndSecurity(fullPath, true, false, outBytes.Length);

        await AtomicWriteAsync(fullPath, outBytes);
        return applyResult;
    }

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private static string DecodeUtf8(byte[] raw)
    {
        int offset = (raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF) ? 3 : 0;
        return Utf8NoBom.GetString(raw, offset, raw.Length - offset);
    }

    private static byte[] EncodeUtf8(string text, bool withBom)
    {
        byte[] body = Utf8NoBom.GetBytes(text);
        if (!withBom) return body;
        var result = new byte[3 + body.Length];
        result[0] = 0xEF; result[1] = 0xBB; result[2] = 0xBF;
        Buffer.BlockCopy(body, 0, result, 3, body.Length);
        return result;
    }

    /// <summary>先写同目录临时文件，再原子替换，避免写入中崩溃损坏原文件。</summary>
    private static async Task AtomicWriteAsync(string fullPath, byte[] bytes)
    {
        var dir = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        await File.WriteAllBytesAsync(tmp, bytes);
        try
        {
            File.Replace(tmp, fullPath, null);
        }
        catch
        {
            // 跨卷/网络盘等 File.Replace 不支持的场景退化为覆盖移动。
            File.Move(tmp, fullPath, overwrite: true);
        }
    }

    public Task<bool> DeleteFileAsync(string absolutePath, bool recursive = false)
    {
        var fullPath = Path.GetFullPath(ExpandPath(absolutePath));
        ValidatePathAndSecurity(fullPath, true);
        if (File.Exists(fullPath)) { File.Delete(fullPath); _logger.Information("FileSystem DeleteFile succeeded: Path={Path}", fullPath); return Task.FromResult(true); }
        if (Directory.Exists(fullPath))
        {
            if (!recursive)
            {
                _logger.Warning("FileSystem Delete rejected (directory without recursive): Path={Path}", fullPath);
                throw new InvalidOperationException(
                    $"Target is a directory not a file; recursive delete would remove all its contents. Pass recursive=true to confirm. ({absolutePath})");
            }
            Directory.Delete(fullPath, true);
            _logger.Information("FileSystem DeleteDirectory succeeded: Path={Path}, Recursive={Recursive}", fullPath, recursive);
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    public Task<bool> MoveFileAsync(string sourcePath, string destinationPath)
    {
        var src = Path.GetFullPath(ExpandPath(sourcePath));
        var dest = Path.GetFullPath(ExpandPath(destinationPath));
        ValidatePathAndSecurity(src, true);
        ValidatePathAndSecurity(dest, true);

        if (File.Exists(src))
        {
            var destDir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
            File.Move(src, dest);
            _logger.Information("FileSystem MoveFile succeeded: {Src} -> {Dest}", src, dest);
            return Task.FromResult(true);
        }
        if (Directory.Exists(src))
        {
            Directory.Move(src, dest);
            _logger.Information("FileSystem MoveDirectory succeeded: {Src} -> {Dest}", src, dest);
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    public Task<bool> CopyFileAsync(string sourcePath, string destinationPath, bool overwrite = false)
    {
        var src = Path.GetFullPath(ExpandPath(sourcePath));
        var dest = Path.GetFullPath(ExpandPath(destinationPath));
        ValidatePathAndSecurity(src, false);
        ValidatePathAndSecurity(dest, true);

        if (File.Exists(src))
        {
            var destDir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
            File.Copy(src, dest, overwrite);
            _logger.Information("FileSystem CopyFile succeeded: {Src} -> {Dest}, Overwrite={Overwrite}", src, dest, overwrite);
            return Task.FromResult(true);
        }
        // Directory copy logic could be added if needed, but keeping it simple for now
        return Task.FromResult(false);
    }

    public Task<bool> CreateDirectoryAsync(string absolutePath)
    {
        var fullPath = Path.GetFullPath(ExpandPath(absolutePath));
        ValidatePathAndSecurity(fullPath, true, true);
        if (!Directory.Exists(fullPath))
        {
            Directory.CreateDirectory(fullPath);
            _logger.Information("FileSystem CreateDirectory succeeded: Path={Path}", fullPath);
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    public Task<List<FileSystemEntry>> ListDirectoryAsync(string absolutePath, bool recursive = false, string? filter = null)
    {
        var fullPath = Path.GetFullPath(ExpandPath(absolutePath));
        _logger.Debug("FileSystem ListDirectory: Path={Path}, Recursive={Recursive}", fullPath, recursive);
        ValidatePathAndSecurity(fullPath, false, true);
        if (!Directory.Exists(fullPath)) return Task.FromResult(new List<FileSystemEntry>());

        var pattern = string.IsNullOrEmpty(filter) ? "*" : filter;
        var opt = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        var allEntries = new List<FileSystemEntry>();
        var dirInfo = new DirectoryInfo(fullPath);

        const int maxEntries = 1000;

        foreach (var d in dirInfo.GetDirectories(pattern, opt))
        {
            allEntries.Add(new FileSystemEntry { Name = d.Name, FullPath = d.FullName, Type = "Directory", LastModified = d.LastWriteTime });
            if (allEntries.Count >= maxEntries) break;
        }

        if (allEntries.Count < maxEntries)
        {
            foreach (var f in dirInfo.GetFiles(pattern, opt))
            {
                allEntries.Add(new FileSystemEntry { Name = f.Name, FullPath = f.FullName, Type = "File", SizeBytes = f.Length, LastModified = f.LastWriteTime });
                if (allEntries.Count >= maxEntries) break;
            }
        }

        if (allEntries.Count >= maxEntries)
        {
            _logger.Information("FileSystem ListDirectory truncated: Path={Path}, MaxEntries={Max}", fullPath, maxEntries);
        }
        return Task.FromResult(allEntries);
    }

    public async Task<FileMetadata?> GetFileInfoAsync(string absolutePath, bool includeTextStatistics = false)
    {
        var fullPath = Path.GetFullPath(ExpandPath(absolutePath));
        _logger.Debug("FileSystem GetFileInfo: Path={Path}", fullPath);
        ValidatePathAndSecurity(fullPath, false);
        if (!File.Exists(fullPath)) return null;

        var info = new FileInfo(fullPath);
        var metadata = new FileMetadata
        {
            SizeBytes = info.Length,
            LastModified = info.LastWriteTime,
            ChunkCount = Math.Max(1, (int)Math.Ceiling((double)info.Length / ChunkSizeBytes)),
            MimeType = GetMimeType(fullPath),
            Encoding = await DetectEncodingFromBomAsync(fullPath)
        };

        if (includeTextStatistics)
        {
            (metadata.CharCount, metadata.LineCount, metadata.Encoding) = await ScanTextStatisticsAsync(fullPath);
        }

        return metadata;
    }

    public async Task<FileSearchResult> SearchInFileAsync(string absolutePath, string pattern, int contextLines = 3, int maxMatches = 10)
    {
        var fullPath = Path.GetFullPath(ExpandPath(absolutePath));
        _logger.Debug("FileSystem SearchInFile: Path={Path}, Pattern={Pattern}", fullPath, pattern);
        ValidatePathAndSecurity(fullPath, false);
        var result = new FileSearchResult();
        if (!File.Exists(fullPath)) return result;

        var lines = await File.ReadAllLinesAsync(fullPath);
        var regex = new Regex(pattern, RegexOptions.IgnoreCase);

        for (int i = 0; i < lines.Length; i++)
        {
            if (regex.IsMatch(lines[i]))
            {
                var match = new FileSearchMatch
                {
                    LineNumber = i + 1,
                    Content = lines[i],
                    ContextBefore = string.Join("\n", lines.Skip(Math.Max(0, i - contextLines)).Take(Math.Min(i, contextLines))),
                    ContextAfter = string.Join("\n", lines.Skip(i + 1).Take(contextLines))
                };
                result.TotalMatches++;
                if (result.Matches.Count < maxMatches) result.Matches.Add(match);
            }
        }
        if (result.TotalMatches > result.Matches.Count)
        {
            _logger.Information("FileSystem SearchInFile truncated: Path={Path}, MaxMatches={Max}", fullPath, maxMatches);
        }
        return result;
    }

    public async Task<DocumentOutline> GetDocumentOutlineAsync(string absolutePath)
    {
        var fullPath = Path.GetFullPath(ExpandPath(absolutePath));
        _logger.Debug("FileSystem GetDocumentOutline: Path={Path}", fullPath);
        ValidatePathAndSecurity(fullPath, false);
        if (!File.Exists(fullPath)) return new DocumentOutline();
        return await DocumentOutlineExtractor.ExtractLocalAsync(fullPath);
    }

    private string GetMimeType(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".md" or ".markdown" or ".mdx" => "text/markdown",
            ".cs" => "text/x-csharp",
            ".json" or ".jsonc" => "application/json",
            ".yaml" or ".yml" => "application/yaml",
            ".py" or ".pyi" => "text/x-python",
            ".js" or ".jsx" or ".mjs" or ".cjs" => "text/javascript",
            ".ts" or ".tsx" => "text/typescript",
            ".html" or ".htm" => "text/html",
            ".xml" or ".xaml" or ".axaml" => "application/xml",
            ".pdf" => "application/pdf",
            ".doc" => "application/msword",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".ppt" => "application/vnd.ms-powerpoint",
            ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            ".xls" => "application/vnd.ms-excel",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            _ => "text/plain"
        };
    }

    public string GetAbsoluteSecurePath(string path, bool enforceReadSizeLimit = true)
    {
        var fullPath = Path.GetFullPath(ExpandPath(path));
        ValidatePathAndSecurity(fullPath, false, enforceSizeLimit: enforceReadSizeLimit);
        return fullPath;
    }

    public string GetAbsoluteSecureWritePath(string path, long estimatedSizeBytes = 0)
    {
        var fullPath = Path.GetFullPath(ExpandPath(path));
        ValidatePathAndSecurity(fullPath, true, dataSize: estimatedSizeBytes);
        return fullPath;
    }

    private static async Task<(long CharCount, long LineCount, string Encoding)> ScanTextStatisticsAsync(string fullPath)
    {
        await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 16 * 1024, useAsync: true);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true,
            bufferSize: 16 * 1024, leaveOpen: false);
        var buffer = new char[16 * 1024];
        long charCount = 0;
        long newlineCount = 0;
        var sawAny = false;
        var lastWasCarriageReturn = false;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length))) > 0)
        {
            sawAny = true;
            charCount += read;
            for (var i = 0; i < read; i++)
            {
                var ch = buffer[i];
                if (ch == '\n')
                {
                    if (!lastWasCarriageReturn) newlineCount++;
                    lastWasCarriageReturn = false;
                }
                else if (ch == '\r')
                {
                    newlineCount++;
                    lastWasCarriageReturn = true;
                }
                else
                {
                    lastWasCarriageReturn = false;
                }
            }
        }

        return (charCount, sawAny ? newlineCount + 1 : 0, reader.CurrentEncoding.WebName);
    }

    private static async Task<string> DetectEncodingFromBomAsync(string fullPath)
    {
        await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 4, useAsync: true);
        var bytes = new byte[4];
        var read = await stream.ReadAsync(bytes.AsMemory(0, bytes.Length));
        if (read >= 4 && bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xFE && bytes[3] == 0xFF) return "utf-32BE";
        if (read >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00) return "utf-32";
        if (read >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return "utf-8";
        if (read >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return "utf-16BE";
        if (read >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return "utf-16";
        return "utf-8";
    }
}
