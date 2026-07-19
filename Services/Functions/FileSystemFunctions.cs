using Athena.UI.Services.Interfaces;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Athena.UI.Services.Functions;

/// <summary>
/// 系统文件控制相关工具函数
/// </summary>
public class FileSystemFunctions
{
    private readonly IFileSystemService _fileSystemService;
    private readonly IKnowledgeBaseService _knowledgeBaseService;
    private readonly IWorkspaceService? _workspaceService;
    private readonly ILogger _logger;

    public FileSystemFunctions(
        IFileSystemService fileSystemService,
        IKnowledgeBaseService knowledgeBaseService,
        ILogger logger,
        IWorkspaceService? workspaceService = null)
    {
        _fileSystemService = fileSystemService;
        _knowledgeBaseService = knowledgeBaseService;
        _logger = logger.ForContext<FileSystemFunctions>();
        _workspaceService = workspaceService;
    }

    private async Task TryUpdateKnowledgeBaseVectorsAsync(string path)
    {
        try
        {
            // 使用文件系统的路径解析引擎，将相对路径或模糊路径还原为物理全路径
            var absolutePath = _fileSystemService.GetAbsoluteSecurePath(path);
            var kbRoot = _knowledgeBaseService.KnowledgeBasePath;

            // 严谨判断：解析后的路径是否以知识库根目录开头
            if (absolutePath.StartsWith(kbRoot, StringComparison.OrdinalIgnoreCase))
            {
                await _knowledgeBaseService.RefreshVectorCacheAsync();
                _logger.Information("检测到知识库文件被修改，已触发向量同步标志: {Path}", absolutePath);
            }

            // 工作区知识可由 modify_system_file / write_system_file 直接更新，
            // 因此在所有成功写入路径后统一检查目标文件预算。
            if (_workspaceService != null)
            {
                await _workspaceService.EnforceKnowledgeFileBudgetAsync(absolutePath);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "写入后同步知识库或检查工作区知识预算失败");
        }
    }

    public async Task<FunctionResult> GetFileInfoAsync(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return FunctionResult.FailureResult("错误: 必须提供 path 参数。");
            var info = await _fileSystemService.GetFileInfoAsync(path);
            if (info == null) return FunctionResult.FailureResult($"错误: 文件不存在 ({path})");
            return FunctionResult.SuccessResult("获取文件信息成功", info);
        }
        catch (Exception ex) { return FunctionResult.FailureResult($"获取失败: {ex.Message}"); }
    }

    public async Task<FunctionResult> SearchInFileAsync(string path, string pattern, int contextLines = 3, int maxMatches = 10)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(pattern))
                return FunctionResult.FailureResult("错误: 必须提供 path 和 pattern 参数。");
            var result = await _fileSystemService.SearchInFileAsync(path, pattern, contextLines, maxMatches);
            return FunctionResult.SuccessResult("搜索完成", result);
        }
        catch (Exception ex) { return FunctionResult.FailureResult($"搜索失败: {ex.Message}"); }
    }

    public async Task<FunctionResult> GetDocumentOutlineAsync(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return FunctionResult.FailureResult("错误: 必须提供 path 参数。");
            var outline = await _fileSystemService.GetDocumentOutlineAsync(path);
            return FunctionResult.SuccessResult("获取大纲成功", outline);
        }
        catch (Exception ex) { return FunctionResult.FailureResult($"获取大纲失败: {ex.Message}"); }
    }

    public async Task<FunctionResult> ReadSystemFileAsync(string path, int? startLine = null, int? endLine = null, string? sectionTitle = null, int? chunkIndex = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return FunctionResult.FailureResult("错误: 必须提供 path 参数。");
            var content = await _fileSystemService.ReadFileAsync(path, startLine, endLine, sectionTitle, chunkIndex);
            if (content == null) return FunctionResult.FailureResult($"错误: 文件不存在 ({path})");

            var info = await _fileSystemService.GetFileInfoAsync(path);
            return FunctionResult.SuccessResult("读取成功", new 
            { 
                content, 
                startLine, 
                endLine, 
                chunkIndex, 
                totalChunks = info?.ChunkCount ?? 1,
                truncated = !string.IsNullOrEmpty(content) && content.Length >= 50 * 1024 
            });
        }
        catch (UnauthorizedAccessException ex) { return FunctionResult.FailureResult($"安全拦截: {ex.Message}"); }
        catch (InvalidOperationException ex) { return FunctionResult.FailureResult($"操作限制: {ex.Message}"); }
        catch (Exception ex) { return FunctionResult.FailureResult($"读取失败: {ex.Message}"); }
    }
public async Task<FunctionResult> WriteSystemFileAsync(string path, string content)
{
    try
    {
        if (string.IsNullOrWhiteSpace(path)) return FunctionResult.FailureResult("错误: 必须提供 path 参数。");
        var success = await _fileSystemService.WriteFileAsync(path, content ?? string.Empty);
        if (!success) return FunctionResult.FailureResult($"文件写入失败: {path}");

        await TryUpdateKnowledgeBaseVectorsAsync(path);

        return FunctionResult.SuccessResult("文件写入成功。");
    }
    catch (UnauthorizedAccessException ex) { return FunctionResult.FailureResult($"安全拦截: {ex.Message}"); }
    catch (InvalidOperationException ex) { return FunctionResult.FailureResult($"操作限制: {ex.Message}"); }
    catch (Exception ex) { return FunctionResult.FailureResult($"写入失败: {ex.Message}"); }
}

    public async Task<FunctionResult> ModifySystemFileAsync(string path, string diffContent, bool fuzzyMatch = true, bool replaceAll = false)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(diffContent))
                return FunctionResult.FailureResult("错误: 必须提供 path 和 diffContent 参数。");

            var result = await _fileSystemService.ModifyFileWithDiffAsync(path, diffContent, fuzzyMatch, replaceAll);
            if (result.Success)
            {
                await TryUpdateKnowledgeBaseVectorsAsync(path);
                return FunctionResult.SuccessResult(result.Message,
                    new { path, appliedBlocks = result.AppliedBlocks, matchTier = result.MatchTier.ToString() });
            }

            var sb = new StringBuilder(result.Message);
            if (result.FailedBlockIndex is int blockIndex) sb.Append($"\n失败块: #{blockIndex}");
            if (!string.IsNullOrEmpty(result.NearestHint)) sb.Append('\n').Append(result.NearestHint);
            if (result.MultipleMatches != null && result.MultipleMatches.Any())
                sb.Append("\n冲突上下文（请补充上下文使 SEARCH 唯一，或设置 replaceAll=true）:\n")
                  .Append(string.Join("\n", result.MultipleMatches.Select(m => $"- {m}")));

            return FunctionResult.FailureResult(sb.ToString());
        }
        catch (UnauthorizedAccessException ex) { return FunctionResult.FailureResult($"安全拦截: {ex.Message}"); }
        catch (InvalidOperationException ex) { return FunctionResult.FailureResult($"操作限制: {ex.Message}"); }
        catch (Exception ex) { return FunctionResult.FailureResult($"修改失败: {ex.Message}"); }
    }

    public async Task<FunctionResult> DeleteSystemFileAsync(string path, bool recursive = false)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return FunctionResult.FailureResult("错误: 必须提供 path 参数。");
            var success = await _fileSystemService.DeleteFileAsync(path, recursive);
            if (success)
            {
                await TryUpdateKnowledgeBaseVectorsAsync(path);
                return FunctionResult.SuccessResult($"成功: 文件已删除 ({path})");
            }
            return FunctionResult.FailureResult($"错误: 文件不存在 ({path})");
        }
        catch (UnauthorizedAccessException ex) { return FunctionResult.FailureResult($"安全拦截: {ex.Message}"); }
        catch (InvalidOperationException ex) { return FunctionResult.FailureResult($"操作限制: {ex.Message}"); }
        catch (Exception ex) { return FunctionResult.FailureResult($"删除失败: {ex.Message}"); }
    }

    public async Task<FunctionResult> ListSystemDirectoryAsync(string path, bool recursive = false, string? filter = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return FunctionResult.FailureResult("错误: 必须提供 path 参数。");
            var entries = await _fileSystemService.ListDirectoryAsync(path, recursive, filter);
            var formatted = entries.Select(e => new { e.Name, e.Type, e.SizeBytes, lastModified = e.LastModified.ToString("yyyy-MM-dd HH:mm:ss") }).ToList();
            
            var message = $"目录内容 ({path})";
            if (formatted.Count >= 1000)
            {
                message += " [注意：结果已截断为前 1000 个条目，请使用更具体的路径或过滤器]";
            }

            return FunctionResult.SuccessResult(message, new { path, entries = formatted });
        }
        catch (UnauthorizedAccessException ex) { return FunctionResult.FailureResult($"安全拦截: {ex.Message}"); }
        catch (InvalidOperationException ex) { return FunctionResult.FailureResult($"操作限制: {ex.Message}"); }
        catch (Exception ex) { return FunctionResult.FailureResult($"列出目录失败: {ex.Message}"); }
    }

    public async Task<FunctionResult> CreateDirectoryAsync(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return FunctionResult.FailureResult("错误: 必须提供 path 参数。");
            var success = await _fileSystemService.CreateDirectoryAsync(path);
            if (success) return FunctionResult.SuccessResult($"目录创建成功: {path}");
            return FunctionResult.FailureResult($"目录已存在或创建失败: {path}");
        }
        catch (UnauthorizedAccessException ex) { return FunctionResult.FailureResult($"安全拦截: {ex.Message}"); }
        catch (InvalidOperationException ex) { return FunctionResult.FailureResult($"操作限制: {ex.Message}"); }
        catch (Exception ex) { return FunctionResult.FailureResult($"创建目录失败: {ex.Message}"); }
    }

    public async Task<FunctionResult> MoveSystemFileAsync(string sourcePath, string destinationPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(destinationPath))
                return FunctionResult.FailureResult("错误: 必须提供 sourcePath 和 destinationPath 参数。");
            var success = await _fileSystemService.MoveFileAsync(sourcePath, destinationPath);
            if (success)
            {
                await TryUpdateKnowledgeBaseVectorsAsync(sourcePath);
                await TryUpdateKnowledgeBaseVectorsAsync(destinationPath);
                return FunctionResult.SuccessResult($"移动成功: {sourcePath} -> {destinationPath}");
            }
            return FunctionResult.FailureResult($"移动失败: 源路径不存在 ({sourcePath})");
        }
        catch (UnauthorizedAccessException ex) { return FunctionResult.FailureResult($"安全拦截: {ex.Message}"); }
        catch (InvalidOperationException ex) { return FunctionResult.FailureResult($"操作限制: {ex.Message}"); }
        catch (Exception ex) { return FunctionResult.FailureResult($"移动操作失败: {ex.Message}"); }
    }

    public async Task<FunctionResult> CopySystemFileAsync(string sourcePath, string destinationPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(destinationPath))
                return FunctionResult.FailureResult("错误: 必须提供 sourcePath 和 destinationPath 参数。");
            var success = await _fileSystemService.CopyFileAsync(sourcePath, destinationPath);
            if (success)
            {
                await TryUpdateKnowledgeBaseVectorsAsync(destinationPath);
                return FunctionResult.SuccessResult($"复制成功: {sourcePath} -> {destinationPath}");
            }
            return FunctionResult.FailureResult($"复制失败: 源路径不存在 ({sourcePath})");
        }
        catch (UnauthorizedAccessException ex) { return FunctionResult.FailureResult($"安全拦截: {ex.Message}"); }
        catch (InvalidOperationException ex) { return FunctionResult.FailureResult($"操作限制: {ex.Message}"); }
        catch (Exception ex) { return FunctionResult.FailureResult($"复制操作失败: {ex.Message}"); }
    }
}
