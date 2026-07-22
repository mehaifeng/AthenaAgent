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

    private void ValidatePathAndSecurity(string absolutePath, bool isWriteOperation, bool isDirectoryOperation = false, long dataSize = 0)
    {
        if (string.IsNullOrWhiteSpace(absolutePath)) throw new ArgumentException("文件路径不能为空");
        var policy = _configService.Load().FileSystemPolicy;
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var fullPath = Path.GetFullPath(ExpandPath(absolutePath));

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
                throw new UnauthorizedAccessException("系统自我保护机制：禁止通过文件系统工具修改应用配置文件！");
        }

        var platform = OperatingSystem.IsWindows() ? policy.Platforms.Windows : (OperatingSystem.IsMacOS() ? policy.Platforms.MacOS : policy.Platforms.Linux);
        var accessRules = isWriteOperation ? platform.WriteAccess : platform.ReadAccess;

        var blockedDirs = accessRules.BlockedDirectories.Select(dir => Path.GetFullPath(ExpandPath(dir))).ToList();
        if (pathsToCheck.Any(p => blockedDirs.Any(dir => p.StartsWith(dir, comparison))))
            throw new UnauthorizedAccessException($"由于安全策略，系统关键目录受到保护，访问被拒绝。");

        // 附件库为“受信读取区”：解析后的大文档/大文本是有意落盘供模型按需分块读取的，
        // 整体大小限制对它们没有意义（实际返回的是 50KB 切片）。因此对该目录下的读取
        // 豁免整文件大小护栏，仅保留目录边界与分块约束。
        bool trustedRead = !isWriteOperation && IsWithinAttachmentRoot(fullPath);

        long targetLimit = isWriteOperation ? policy.Global.MaxWriteSizeBytes : policy.Global.MaxReadSizeBytes;
        if (dataSize > targetLimit || (!isWriteOperation && !isDirectoryOperation && !trustedRead && File.Exists(fullPath) && new FileInfo(fullPath).Length > targetLimit))
            throw new InvalidOperationException($"操作超出大小限制 ({targetLimit} bytes)。");
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

    public async Task<string?> ReadFileAsync(string absolutePath, int? startLine = null, int? endLine = null, string? sectionTitle = null, int? chunkIndex = null)
    {
        var fullPath = Path.GetFullPath(ExpandPath(absolutePath));
        ValidatePathAndSecurity(fullPath, false);

        if (!File.Exists(fullPath)) return null;

        var fileInfo = new FileInfo(fullPath);
        
        // Handle Section Title (Simple Markdown implementation)
        if (!string.IsNullOrEmpty(sectionTitle))
        {
            var lines = await File.ReadAllLinesAsync(fullPath);
            var sectionLines = new List<string>();
            bool inSection = false;
            foreach (var line in lines)
            {
                if (line.Trim().StartsWith("#") && line.Contains(sectionTitle, StringComparison.OrdinalIgnoreCase))
                {
                    inSection = true;
                    sectionLines.Add(line);
                    continue;
                }
                if (inSection && line.Trim().StartsWith("#")) break;
                if (inSection) sectionLines.Add(line);
            }
            return string.Join(Environment.NewLine, sectionLines);
        }

        // Handle Line Ranges
        if (startLine.HasValue)
        {
            var lines = await File.ReadAllLinesAsync(fullPath);
            int start = Math.Max(0, startLine.Value - 1);
            int end = endLine.HasValue ? Math.Min(lines.Length, endLine.Value) : lines.Length;
            if (start >= lines.Length) return string.Empty;
            return string.Join(Environment.NewLine, lines.Skip(start).Take(end - start));
        }

        // Handle Chunking for large files
        if (fileInfo.Length > 50 * 1024 || chunkIndex.HasValue)
        {
            int idx = chunkIndex ?? 0;
            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read);
            stream.Seek(idx * ChunkSizeBytes, SeekOrigin.Begin);
            byte[] buffer = new byte[ChunkSizeBytes];
            int read = await stream.ReadAsync(buffer, 0, ChunkSizeBytes);
            return Encoding.UTF8.GetString(buffer, 0, read);
        }

        return await File.ReadAllTextAsync(fullPath, Encoding.UTF8);
    }

    public async Task<bool> WriteFileAsync(string absolutePath, string content)
    {
        var fullPath = Path.GetFullPath(ExpandPath(absolutePath));
        ValidatePathAndSecurity(fullPath, true, false, Encoding.UTF8.GetByteCount(content));
        // 无 BOM 写入 + 原子替换，避免污染文件并防止写入中崩溃损坏数据。
        await AtomicWriteAsync(fullPath, EncodeUtf8(content, withBom: false));
        return true;
    }

    public async Task<FileUpdateResult> ModifyFileWithDiffAsync(string absolutePath, string diffContent, bool fuzzyMatch = true, bool replaceAll = false)
    {
        var fullPath = Path.GetFullPath(ExpandPath(absolutePath));
        ValidatePathAndSecurity(fullPath, true);
        if (!File.Exists(fullPath)) return new FileUpdateResult { Success = false, Message = "文件不存在。" };

        var parse = DiffApplier.Parse(diffContent);
        if (parse.Error != null) return new FileUpdateResult { Success = false, Message = parse.Error };

        // 读原始字节 → 探测编码/换行风格 → 在按 \n 规范化的行空间内匹配。
        byte[] raw = await File.ReadAllBytesAsync(fullPath);
        string text = DecodeUtf8(raw);
        var profile = FileEncodingProfile.Detect(raw, text);

        var lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n').ToList();

        var applyResult = DiffApplier.Apply(lines, parse.Blocks, fuzzyMatch, replaceAll);
        if (!applyResult.Success) return applyResult;

        // 按原换行风格与 BOM 还原落盘，避免污染文件。
        string newText = string.Join(profile.DominantEol, lines);
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
        if (File.Exists(fullPath)) { File.Delete(fullPath); return Task.FromResult(true); }
        if (Directory.Exists(fullPath))
        {
            if (!recursive)
                throw new InvalidOperationException(
                    $"目标是目录而非文件，删除将递归移除其全部内容。如确需删除，请显式传入 recursive=true。({absolutePath})");
            Directory.Delete(fullPath, true);
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
            return Task.FromResult(true);
        }
        if (Directory.Exists(src))
        {
            Directory.Move(src, dest);
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    public Task<bool> CopyFileAsync(string sourcePath, string destinationPath)
    {
        var src = Path.GetFullPath(ExpandPath(sourcePath));
        var dest = Path.GetFullPath(ExpandPath(destinationPath));
        ValidatePathAndSecurity(src, false);
        ValidatePathAndSecurity(dest, true);

        if (File.Exists(src))
        {
            var destDir = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
            File.Copy(src, dest, true);
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
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    public Task<List<FileSystemEntry>> ListDirectoryAsync(string absolutePath, bool recursive = false, string? filter = null)
    {
        var fullPath = Path.GetFullPath(ExpandPath(absolutePath));
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

        return Task.FromResult(allEntries);
    }

    public async Task<FileMetadata?> GetFileInfoAsync(string absolutePath)
    {
        var fullPath = Path.GetFullPath(ExpandPath(absolutePath));
        ValidatePathAndSecurity(fullPath, false);
        if (!File.Exists(fullPath)) return null;

        var info = new FileInfo(fullPath);
        var content = await File.ReadAllTextAsync(fullPath);
        return new FileMetadata
        {
            SizeBytes = info.Length,
            CharCount = content.Length,
            LineCount = content.Split('\n').Length,
            LastModified = info.LastWriteTime,
            ChunkCount = (int)Math.Ceiling((double)info.Length / ChunkSizeBytes),
            MimeType = GetMimeType(fullPath)
        };
    }

    public async Task<FileSearchResult> SearchInFileAsync(string absolutePath, string pattern, int contextLines = 3, int maxMatches = 10)
    {
        var fullPath = Path.GetFullPath(ExpandPath(absolutePath));
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
                result.Matches.Add(match);
                if (result.Matches.Count >= maxMatches) break;
            }
        }
        result.TotalMatches = result.Matches.Count;
        return result;
    }

    public async Task<DocumentOutline> GetDocumentOutlineAsync(string absolutePath)
    {
        var fullPath = Path.GetFullPath(ExpandPath(absolutePath));
        ValidatePathAndSecurity(fullPath, false);
        var outline = new DocumentOutline();
        if (!File.Exists(fullPath)) return outline;

        var ext = Path.GetExtension(fullPath).ToLowerInvariant();
        var lines = await File.ReadAllLinesAsync(fullPath);

        if (ext == ".md" || ext == ".markdown")
        {
            outline.OutlineType = "Markdown";
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].TrimStart().StartsWith("#"))
                    outline.Entries.Add(new OutlineEntry { Title = lines[i].Trim(), LineNumber = i + 1 });
            }
        }
        else if (ext == ".cs" || ext == ".java" || ext == ".py")
        {
            outline.OutlineType = "Code";
            // Basic regex for methods/classes
            var regex = new Regex(@"(class|void|public|private|def|async|task)\s+([\w<>]+)\s*\(", RegexOptions.IgnoreCase);
            for (int i = 0; i < lines.Length; i++)
            {
                if (regex.IsMatch(lines[i]))
                    outline.Entries.Add(new OutlineEntry { Title = lines[i].Trim(), LineNumber = i + 1 });
            }
        }
        return outline;
    }

    private string GetMimeType(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch { ".md" => "text/markdown", ".cs" => "text/x-csharp", ".json" => "application/json", ".py" => "text/x-python", _ => "text/plain" };
    }

    public string GetAbsoluteSecurePath(string path)
    {
        return Path.GetFullPath(ExpandPath(path));
    }
}
