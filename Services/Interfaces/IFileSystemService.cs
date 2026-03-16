using Athena.UI.Models;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Athena.UI.Services.Interfaces;

/// <summary>
/// 本地系统文件服务，提供面向本机文件系统的读写能力（受白名单保护）
/// </summary>
public interface IFileSystemService
{
    Task<string?> ReadFileAsync(string absolutePath, int? startLine = null, int? endLine = null, string? sectionTitle = null, int? chunkIndex = null);
    Task<bool> WriteFileAsync(string absolutePath, string content);
    Task<FileUpdateResult> ModifyFileWithDiffAsync(string absolutePath, string diffContent, bool fuzzyMatch = true);
    Task<bool> DeleteFileAsync(string absolutePath);
    Task<bool> MoveFileAsync(string sourcePath, string destinationPath);
    Task<bool> CopyFileAsync(string sourcePath, string destinationPath);
    Task<bool> CreateDirectoryAsync(string absolutePath);
    Task<List<FileSystemEntry>> ListDirectoryAsync(string absolutePath, bool recursive = false, string? filter = null);
    Task<FileMetadata?> GetFileInfoAsync(string absolutePath);
    Task<FileSearchResult> SearchInFileAsync(string absolutePath, string pattern, int contextLines = 3, int maxMatches = 10);
    Task<DocumentOutline> GetDocumentOutlineAsync(string absolutePath);
    string GetAbsoluteSecurePath(string path);
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
    public int LineCount { get; set; }
    public long CharCount { get; set; }
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

public class DocumentOutline
{
    public string OutlineType { get; set; } = "Unknown";
    public List<OutlineEntry> Entries { get; set; } = new();
}

public class OutlineEntry
{
    public string Title { get; set; } = string.Empty;
    public int LineNumber { get; set; }
    public long CharOffset { get; set; }
}
