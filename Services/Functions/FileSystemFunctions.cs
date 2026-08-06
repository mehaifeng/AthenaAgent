using Athena.UI.Services.Interfaces;
using Serilog;
using System;
using System.Linq;
using System.Text;
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
                _logger.Information("Knowledge base file change detected, vector sync flag triggered: {Path}", absolutePath);
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
            _logger.Warning(ex, "Post-write knowledge base sync or workspace knowledge budget check failed");
        }
    }

    public async Task<FunctionResult> GetFileInfoAsync(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return FunctionResult.FailureResult("Error: the 'path' parameter is required.");
            var info = await _fileSystemService.GetFileInfoAsync(path);
            if (info == null) return FunctionResult.FailureResult($"Error: file not found ({path})");
            return FunctionResult.SuccessResult("File metadata retrieved successfully.", info);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Function GetFileInfo failed: {Stage}", "fetch metadata");
            return FunctionResult.FailureResult($"Failed to fetch file metadata: {ex.Message}");
        }
    }

    public async Task<FunctionResult> SearchInFileAsync(string path, string pattern, int contextLines = 3, int maxMatches = 10)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(pattern))
                return FunctionResult.FailureResult("Error: both 'path' and 'pattern' parameters are required.");
            var result = await _fileSystemService.SearchInFileAsync(path, pattern, contextLines, maxMatches);
            return FunctionResult.SuccessResult("Search completed.", result);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Function SearchInFile failed: {Stage}", "search file content");
            return FunctionResult.FailureResult($"Search failed: {ex.Message}");
        }
    }

    public async Task<FunctionResult> GetDocumentOutlineAsync(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return FunctionResult.FailureResult("Error: the 'path' parameter is required.");
            var outline = await _fileSystemService.GetDocumentOutlineAsync(path);
            return FunctionResult.SuccessResult("Document outline retrieved successfully.", outline);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Function GetDocumentOutline failed: {Stage}", "parse outline");
            return FunctionResult.FailureResult($"Failed to get document outline: {ex.Message}");
        }
    }

    public async Task<FunctionResult> ReadSystemFileAsync(string path, int? startLine = null, int? endLine = null, string? sectionTitle = null, int? chunkIndex = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return FunctionResult.FailureResult("Error: the 'path' parameter is required.");
            var content = await _fileSystemService.ReadFileAsync(path, startLine, endLine, sectionTitle, chunkIndex, includeLineNumbers: true);
            if (content == null) return FunctionResult.FailureResult($"Error: file not found ({path})");

            var info = await _fileSystemService.GetFileInfoAsync(path);
            return FunctionResult.SuccessResult("Read succeeded.", new
            {
                content,
                startLine,
                endLine,
                chunkIndex,
                totalChunks = info?.ChunkCount ?? 1,
                truncated = (info?.ChunkCount ?? 1) > 1
            });
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.Warning("Function ReadSystemFile denied: {Message}", ex.Message);
            return FunctionResult.FailureResult($"Security blocked: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            _logger.Warning("Function ReadSystemFile quota/limit: {Message}", ex.Message);
            return FunctionResult.FailureResult($"Operation denied: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Function ReadSystemFile failed: {Stage}", "read file");
            return FunctionResult.FailureResult($"Failed to read file: {ex.Message}");
        }
    }
    public async Task<FunctionResult> WriteSystemFileAsync(string path, string content)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return FunctionResult.FailureResult("Error: the 'path' parameter is required.");
            var success = await _fileSystemService.WriteFileAsync(path, content ?? string.Empty);
            if (!success) return FunctionResult.FailureResult($"Write failed: {path}");

            await TryUpdateKnowledgeBaseVectorsAsync(path);

            return FunctionResult.SuccessResult("File written successfully.");
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.Warning("Function WriteSystemFile denied: {Message}", ex.Message);
            return FunctionResult.FailureResult($"Security blocked: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            _logger.Warning("Function WriteSystemFile quota/limit: {Message}", ex.Message);
            return FunctionResult.FailureResult($"Operation denied: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Function WriteSystemFile failed: {Stage}", "write file");
            return FunctionResult.FailureResult($"Failed to write file: {ex.Message}");
        }
    }

    public async Task<FunctionResult> ModifySystemFileAsync(string path, string diffContent, bool fuzzyMatch = true, bool replaceAll = false)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(diffContent))
                return FunctionResult.FailureResult("Error: both 'path' and 'diffContent' parameters are required.");

            var result = await _fileSystemService.ModifyFileWithDiffAsync(path, diffContent, fuzzyMatch, replaceAll);
            if (result.Success)
            {
                await TryUpdateKnowledgeBaseVectorsAsync(path);
                return FunctionResult.SuccessResult(result.Message,
                    new { path, appliedBlocks = result.AppliedBlocks, matchTier = result.MatchTier.ToString(), editedRegions = result.RegionPreview });
            }

            var sb = new StringBuilder(result.Message);
            if (result.FailedBlockIndex is int blockIndex)
            {
                sb.Append($"\nFailed block: #{blockIndex}");
                // 多块编辑全有全无：失败块之前的块已全部匹配成功，只需重发失败块本身，勿整份重发。
                sb.Append(blockIndex > 1
                    ? $"\nRetry hint: blocks #1-#{blockIndex - 1} already matched — resend ONLY block #{blockIndex} with a corrected SEARCH (fix the text or add surrounding context). Do not resend the whole diff."
                    : $"\nRetry hint: resend ONLY block #{blockIndex} with a corrected SEARCH (fix the text or add surrounding context). Do not resend the whole diff.");
            }
            if (!string.IsNullOrEmpty(result.NearestHint)) sb.Append('\n').Append(result.NearestHint);
            if (result.MultipleMatches != null && result.MultipleMatches.Any())
                sb.Append("\nConflicting matches (add more context to make SEARCH unique, or set replaceAll=true):\n")
                  .Append(string.Join("\n", result.MultipleMatches.Select(m => $"- {m}")));

            return FunctionResult.FailureResult(sb.ToString());
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.Warning("Function ModifySystemFile denied: {Message}", ex.Message);
            return FunctionResult.FailureResult($"Security blocked: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            _logger.Warning("Function ModifySystemFile quota/limit: {Message}", ex.Message);
            return FunctionResult.FailureResult($"Operation denied: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Function ModifySystemFile failed: {Stage}", "apply diff");
            return FunctionResult.FailureResult($"Failed to apply diff: {ex.Message}");
        }
    }

    public async Task<FunctionResult> DeleteSystemFileAsync(string path, bool recursive = false)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return FunctionResult.FailureResult("Error: the 'path' parameter is required.");
            var success = await _fileSystemService.DeleteFileAsync(path, recursive);
            if (success)
            {
                await TryUpdateKnowledgeBaseVectorsAsync(path);
                return FunctionResult.SuccessResult($"Success: file deleted ({path})");
            }
            return FunctionResult.FailureResult($"Error: file not found ({path})");
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.Warning("Function DeleteSystemFile denied: {Message}", ex.Message);
            return FunctionResult.FailureResult($"Security blocked: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            _logger.Warning("Function DeleteSystemFile quota/limit: {Message}", ex.Message);
            return FunctionResult.FailureResult($"Operation denied: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Function DeleteSystemFile failed: {Stage}", "delete file");
            return FunctionResult.FailureResult($"Failed to delete: {ex.Message}");
        }
    }

    public async Task<FunctionResult> ListSystemDirectoryAsync(string path, bool recursive = false, string? filter = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return FunctionResult.FailureResult("Error: the 'path' parameter is required.");
            var entries = await _fileSystemService.ListDirectoryAsync(path, recursive, filter);
            var formatted = entries.Select(e => new { e.Name, e.Type, e.SizeBytes, lastModified = e.LastModified.ToString("yyyy-MM-dd HH:mm:ss") }).ToList();

            var message = $"Directory contents ({path})";
            if (formatted.Count >= 1000)
            {
                message += " [Note: results truncated to the first 1000 entries; use a more specific path or filter]";
            }

            return FunctionResult.SuccessResult(message, new { path, entries = formatted });
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.Warning("Function ListSystemDirectory denied: {Message}", ex.Message);
            return FunctionResult.FailureResult($"Security blocked: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            _logger.Warning("Function ListSystemDirectory quota/limit: {Message}", ex.Message);
            return FunctionResult.FailureResult($"Operation denied: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Function ListSystemDirectory failed: {Stage}", "list directory");
            return FunctionResult.FailureResult($"Failed to list directory: {ex.Message}");
        }
    }

    public async Task<FunctionResult> CreateDirectoryAsync(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return FunctionResult.FailureResult("Error: the 'path' parameter is required.");
            var success = await _fileSystemService.CreateDirectoryAsync(path);
            if (success) return FunctionResult.SuccessResult($"Directory created: {path}");
            return FunctionResult.FailureResult($"Directory already exists or creation failed: {path}");
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.Warning("Function CreateDirectory denied: {Message}", ex.Message);
            return FunctionResult.FailureResult($"Security blocked: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            _logger.Warning("Function CreateDirectory quota/limit: {Message}", ex.Message);
            return FunctionResult.FailureResult($"Operation denied: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Function CreateDirectory failed: {Stage}", "create directory");
            return FunctionResult.FailureResult($"Failed to create directory: {ex.Message}");
        }
    }

    public async Task<FunctionResult> MoveSystemFileAsync(string sourcePath, string destinationPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(destinationPath))
                return FunctionResult.FailureResult("Error: both 'sourcePath' and 'destinationPath' parameters are required.");
            var success = await _fileSystemService.MoveFileAsync(sourcePath, destinationPath);
            if (success)
            {
                await TryUpdateKnowledgeBaseVectorsAsync(sourcePath);
                await TryUpdateKnowledgeBaseVectorsAsync(destinationPath);
                return FunctionResult.SuccessResult($"Move succeeded: {sourcePath} -> {destinationPath}");
            }
            return FunctionResult.FailureResult($"Move failed: source path does not exist ({sourcePath})");
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.Warning("Function MoveSystemFile denied: {Message}", ex.Message);
            return FunctionResult.FailureResult($"Security blocked: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            _logger.Warning("Function MoveSystemFile quota/limit: {Message}", ex.Message);
            return FunctionResult.FailureResult($"Operation denied: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Function MoveSystemFile failed: {Stage}", "move file");
            return FunctionResult.FailureResult($"Failed to move: {ex.Message}");
        }
    }

    public async Task<FunctionResult> CopySystemFileAsync(string sourcePath, string destinationPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(destinationPath))
                return FunctionResult.FailureResult("Error: both 'sourcePath' and 'destinationPath' parameters are required.");
            var success = await _fileSystemService.CopyFileAsync(sourcePath, destinationPath);
            if (success)
            {
                await TryUpdateKnowledgeBaseVectorsAsync(destinationPath);
                return FunctionResult.SuccessResult($"Copy succeeded: {sourcePath} -> {destinationPath}");
            }
            return FunctionResult.FailureResult($"Copy failed: source path does not exist ({sourcePath})");
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.Warning("Function CopySystemFile denied: {Message}", ex.Message);
            return FunctionResult.FailureResult($"Security blocked: {ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            _logger.Warning("Function CopySystemFile quota/limit: {Message}", ex.Message);
            return FunctionResult.FailureResult($"Operation denied: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Function CopySystemFile failed: {Stage}", "copy file");
            return FunctionResult.FailureResult($"Failed to copy: {ex.Message}");
        }
    }
}
