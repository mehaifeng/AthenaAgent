using Athena.UI.Services.Interfaces;
using Serilog;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Athena.UI.Services.Functions;

/// <summary>
/// 系统文件控制相关工具函数
/// </summary>
public class FileSystemFunctions
{
    private readonly IFileSystemService _fileSystemService;
    private readonly ILogger _logger;

    public FileSystemFunctions(IFileSystemService fileSystemService, ILogger logger)
    {
        _fileSystemService = fileSystemService;
        _logger = logger.ForContext<FileSystemFunctions>();
    }

    public async Task<FunctionResult> ReadSystemFileAsync(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return FunctionResult.FailureResult("错误: 必须提供 path 参数。");

            var content = await _fileSystemService.ReadFileAsync(path);
            if (content == null) return FunctionResult.FailureResult($"错误: 文件不存在 ({path})");

            _logger.Information("Function: 读取系统文件 {Path}", path);
            return FunctionResult.SuccessResult("读取成功", new { path, content });
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
            _logger.Information("Function: 写入系统文件 {Path}", path);
            return success ? FunctionResult.SuccessResult($"成功: 文件已写入 ({path})") : FunctionResult.FailureResult("写入失败");
        }
        catch (UnauthorizedAccessException ex) { return FunctionResult.FailureResult($"安全拦截: {ex.Message}"); }
        catch (InvalidOperationException ex) { return FunctionResult.FailureResult($"操作限制: {ex.Message}"); }
        catch (Exception ex) { return FunctionResult.FailureResult($"写入失败: {ex.Message}"); }
    }

    public async Task<FunctionResult> ModifySystemFileAsync(string path, string diffContent, bool fuzzyMatch = true)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(diffContent))
                return FunctionResult.FailureResult("错误: 必须提供 path 和 diffContent 参数。");

            var result = await _fileSystemService.ModifyFileWithDiffAsync(path, diffContent, fuzzyMatch);

            if (result.Success)
            {
                _logger.Information("Function: 修改系统文件 {Path}", path);
                return FunctionResult.SuccessResult(result.Message, new { path, appliedBlocks = result.AppliedBlocks });
            }

            var errorMessage = result.Message;
            if (result.MultipleMatches != null && result.MultipleMatches.Any())
            {
                errorMessage += "\n\n冲突的上下文:\n" + string.Join("\n", result.MultipleMatches.Select(m => $"- {m}"));
                errorMessage += "\n请提供更多上下文以唯一标识要修改的位置。";
            }

            return FunctionResult.FailureResult(errorMessage);
        }
        catch (UnauthorizedAccessException ex) { return FunctionResult.FailureResult($"安全拦截: {ex.Message}"); }
        catch (InvalidOperationException ex) { return FunctionResult.FailureResult($"操作限制: {ex.Message}"); }
        catch (Exception ex) { return FunctionResult.FailureResult($"修改失败: {ex.Message}"); }
    }

    public async Task<FunctionResult> DeleteSystemFileAsync(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return FunctionResult.FailureResult("错误: 必须提供 path 参数。");

            var success = await _fileSystemService.DeleteFileAsync(path);
            _logger.Information("Function: 删除系统文件 {Path}", path);
            return success ? FunctionResult.SuccessResult($"成功: 文件已删除 ({path})") : FunctionResult.FailureResult($"错误: 文件不存在 ({path})");
        }
        catch (UnauthorizedAccessException ex) { return FunctionResult.FailureResult($"安全拦截: {ex.Message}"); }
        catch (InvalidOperationException ex) { return FunctionResult.FailureResult($"操作限制: {ex.Message}"); }
        catch (Exception ex) { return FunctionResult.FailureResult($"删除失败: {ex.Message}"); }
    }

    public async Task<FunctionResult> ListSystemDirectoryAsync(string path, bool recursive = false)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return FunctionResult.FailureResult("错误: 必须提供 path 参数。");

            var entries = await _fileSystemService.ListDirectoryAsync(path, recursive);
            _logger.Information("Function: 列出系统目录 {Path}", path);
            return FunctionResult.SuccessResult($"目录内容 ({path})", new { path, entries });
        }
        catch (UnauthorizedAccessException ex) { return FunctionResult.FailureResult($"安全拦截: {ex.Message}"); }
        catch (InvalidOperationException ex) { return FunctionResult.FailureResult($"操作限制: {ex.Message}"); }
        catch (Exception ex) { return FunctionResult.FailureResult($"列出目录失败: {ex.Message}"); }
    }
}
