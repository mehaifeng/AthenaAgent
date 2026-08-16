using Athena.UI.Models;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.Interfaces;

/// <summary>
/// 本地系统文件服务，提供面向本机文件系统的读写能力（受白名单保护）
/// </summary>
public interface IFileSystemService
{
    Task<string?> ReadFileAsync(string absolutePath, int? startLine = null, int? endLine = null, string? sectionTitle = null, int? chunkIndex = null, bool includeLineNumbers = false);
    Task<bool> WriteFileAsync(string absolutePath, string content);
    Task<FileUpdateResult> ModifyFileWithDiffAsync(string absolutePath, string diffContent, bool fuzzyMatch = true, bool replaceAll = false);
    Task<bool> DeleteFileAsync(string absolutePath, bool recursive = false);
    Task<bool> MoveFileAsync(string sourcePath, string destinationPath);
    Task<bool> CopyFileAsync(string sourcePath, string destinationPath, bool overwrite = false);
    Task<DirectoryCreateOutcome> CreateDirectoryAsync(string absolutePath);
    Task<List<FileSystemEntry>> ListDirectoryAsync(string absolutePath, bool recursive = false, string? filter = null);
    Task<FileMetadata?> GetFileInfoAsync(string absolutePath, bool includeTextStatistics = false);
    Task<FileSearchResult> SearchInFileAsync(string absolutePath, string pattern, int contextLines = 3, int maxMatches = 10);
    Task<DirectorySearchResult> SearchInDirectoryAsync(string absolutePath, string pattern, DirectorySearchOptions options, CancellationToken cancellationToken = default);
    Task<DocumentOutline> GetDocumentOutlineAsync(string absolutePath);
    string GetAbsoluteSecurePath(string path, bool enforceReadSizeLimit = true);
    string GetAbsoluteSecureWritePath(string path, long estimatedSizeBytes = 0);
}

public class FileSystemEntry
{
    public string Name { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public string Type { get; set; } = "File"; // "File" or "Directory"
    public long SizeBytes { get; set; }
    public DateTime LastModified { get; set; }
}

public class FileMetadata
{
    public long? LineCount { get; set; }
    public long? CharCount { get; set; }
    public long SizeBytes { get; set; }
    public string Encoding { get; set; } = "utf-8";
    public DateTime LastModified { get; set; }
    public string MimeType { get; set; } = "text/plain";
    public int ChunkCount { get; set; }
}

public class FileSearchResult
{
    public List<FileSearchMatch> Matches { get; set; } = new();
    public int TotalMatches { get; set; }
}

public class FileSearchMatch
{
    public int LineNumber { get; set; }
    public string Content { get; set; } = string.Empty;
    public string ContextBefore { get; set; } = string.Empty;
    public string ContextAfter { get; set; } = string.Empty;
}

/// <summary>
/// 建目录的结果。刻意不用 bool：目录已存在既不是「成功创建」也不是「失败」，
/// 而把两者压进一个 bool 正是让模型陷入无限重试的原因——它拿到一个失败，
/// 却无从判断该改参数还是该继续往下走。
/// 真正的失败（权限、路径非法）由 Directory.CreateDirectory 抛异常表达。
/// </summary>
public enum DirectoryCreateOutcome
{
    Created,
    AlreadyExisted
}

/// <summary>跨文件搜索的边界参数。每一项都是硬上限，用来把一次搜索的代价钉死。</summary>
public class DirectorySearchOptions
{
    /// <summary>逗号分隔的文件名通配（如 "*.cs,*.axaml"）。为空表示全部文本文件。</summary>
    public string? FilePattern { get; set; }
    public bool Recursive { get; set; } = true;
    public int ContextLines { get; set; } = 2;
    public int MaxMatchesPerFile { get; set; } = 5;
    public int MaxTotalMatches { get; set; } = 100;
    public int MaxFilesScanned { get; set; } = 2000;

    /// <summary>是否搜索 bin/obj/node_modules/.git 这类构建与依赖产物目录。</summary>
    public bool IncludeGeneratedDirectories { get; set; }
}

public class DirectorySearchResult
{
    public List<FileSearchGroup> Files { get; set; } = new();
    public int TotalMatches { get; set; }
    public int FilesScanned { get; set; }
    public int FilesSkipped { get; set; }
    public bool Truncated { get; set; }
    public List<string> Warnings { get; set; } = new();
}

/// <summary>某一个文件内的命中集合。按文件聚合，避免上千条散装命中淹没结果。</summary>
public class FileSearchGroup
{
    public string Path { get; set; } = string.Empty;
    public int TotalMatches { get; set; }
    public List<FileSearchMatch> Matches { get; set; } = new();
}

public class DocumentOutline
{
    public string OutlineType { get; set; } = "Unknown";
    public string Format { get; set; } = string.Empty;
    public string Source { get; set; } = "local";
    public int? PageCount { get; set; }
    public bool IsPartial { get; set; }
    public List<string> Warnings { get; set; } = new();
    public List<OutlineEntry> Entries { get; set; } = new();
}

public class OutlineEntry
{
    public string Title { get; set; } = string.Empty;
    public string Kind { get; set; } = "section";
    public int Level { get; set; } = 1;
    public int? LineNumber { get; set; }
    public int? PageNumber { get; set; }
    public long CharOffset { get; set; }
}
