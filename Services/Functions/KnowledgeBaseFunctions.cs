using Athena.UI.Services.Interfaces;
using Serilog;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Athena.UI.Services.Functions;

/// <summary>
/// 知识库管理相关的 Function Calling 实现
/// </summary>
public class KnowledgeBaseFunctions
{
    private readonly IKnowledgeBaseService _knowledgeBase;
    private readonly ILogger _logger;

    public KnowledgeBaseFunctions(IKnowledgeBaseService knowledgeBase, ILogger logger)
    {
        _knowledgeBase = knowledgeBase;
        _logger = logger.ForContext<KnowledgeBaseFunctions>();
    }

    /// <summary>
    /// 创建知识文件（仅用于创建新文件，如需修改现有文件请使用 UpdateKnowledgeFileDiff）
    /// </summary>
    /// <param name="filePath">相对路径，如 'Characters/user_profile.md'</param>
    /// <param name="content">Markdown 格式的文件内容</param>
    /// <param name="tags">可选标签，用于分类</param>
    /// <returns>操作结果</returns>
    public async Task<FunctionResult> CreateKnowledgeFile(
        string filePath,
        string content,
        string[]? tags = null)
    {
        try
        {
            // 确保文件扩展名
            if (!filePath.EndsWith(".md"))
            {
                filePath += ".md";
            }

            // 检查文件是否已存在
            if (await _knowledgeBase.FileExistsAsync(filePath))
            {
                return FunctionResult.FailureResult($"文件已存在: {filePath}。请使用 update_knowledge_file_diff 更新现有文件。");
            }

            await _knowledgeBase.CreateFileAsync(filePath, content, tags);

            _logger.Information("Function: 创建知识文件 {FilePath}", filePath);

            return FunctionResult.SuccessResult(
                $"已创建知识文件: {filePath}",
                new { filePath, created = true });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "创建知识文件失败");
            return FunctionResult.FailureResult($"创建失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 使用 SEARCH/REPLACE 模式更新知识文件（推荐的文件修改方式）
    /// </summary>
    /// <param name="filePath">文件相对路径，如 'Characters/user_profile.md'</param>
    /// <param name="diffContent">SEARCH/REPLACE 格式的修改内容</param>
    /// <param name="fuzzyMatch">是否启用模糊匹配（忽略空格差异），默认 true</param>
    /// <returns>操作结果</returns>
    public async Task<FunctionResult> UpdateKnowledgeFileDiff(
        string filePath,
        string diffContent,
        bool fuzzyMatch = true)
    {
        try
        {
            // 确保文件扩展名
            if (!filePath.EndsWith(".md"))
            {
                filePath += ".md";
            }

            var result = await _knowledgeBase.UpdateWithDiffAsync(filePath, diffContent, fuzzyMatch);

            if (result.Success)
            {
                _logger.Information("Function: SEARCH/REPLACE 更新知识文件 {FilePath}, 应用 {Count} 个修改",
                    filePath, result.AppliedBlocks);
                return FunctionResult.SuccessResult(result.Message, new
                {
                    filePath,
                    appliedBlocks = result.AppliedBlocks,
                    lineNumber = result.LineNumber
                });
            }
            else
            {
                _logger.Warning("Function: SEARCH/REPLACE 更新失败 {FilePath}: {Message}",
                    filePath, result.Message);

                // 如果有多处匹配，提供详细信息
                if (result.MultipleMatches != null && result.MultipleMatches.Count > 0)
                {
                    var matchInfo = string.Join("\n", result.MultipleMatches.Take(5).Select((m, i) => $"  [{i + 1}] {m}"));
                    return FunctionResult.FailureResult($"{result.Message}\n\n匹配位置:\n{matchInfo}");
                }

                return FunctionResult.FailureResult(result.Message);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "SEARCH/REPLACE 更新知识文件失败");
            return FunctionResult.FailureResult($"更新失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 更新知识文件（追加或替换模式，建议优先使用 UpdateKnowledgeFileDiff）
    /// </summary>
    /// <param name="filePath">文件相对路径</param>
    /// <param name="content">要写入的内容</param>
    /// <param name="mode">模式：append（追加）或 replace（替换整体内容）</param>
    /// <returns>操作结果</returns>
    public async Task<FunctionResult> UpdateKnowledgeFile(
        string filePath,
        string content,
        string mode = "append")
    {
        try
        {
            // 确保文件扩展名
            if (!filePath.EndsWith(".md"))
            {
                filePath += ".md";
            }

            // 检查文件是否存在
            if (!await _knowledgeBase.FileExistsAsync(filePath))
            {
                // 如果不存在，自动创建
                await _knowledgeBase.CreateFileAsync(filePath, content);
                _logger.Information("Function: 文件不存在，自动创建 {FilePath}", filePath);
                return FunctionResult.SuccessResult($"文件不存在，已自动创建: {filePath}");
            }

            if (mode.ToLower() == "append")
            {
                await _knowledgeBase.AppendToFileAsync(filePath, content);
                _logger.Information("Function: 追加内容到知识文件 {FilePath}", filePath);
                return FunctionResult.SuccessResult($"已追加内容到: {filePath}");
            }
            else if (mode.ToLower() == "replace")
            {
                await _knowledgeBase.ReplaceFileAsync(filePath, content);
                _logger.Information("Function: 替换知识文件内容 {FilePath}", filePath);
                return FunctionResult.SuccessResult($"已替换内容: {filePath}");
            }
            else
            {
                return FunctionResult.FailureResult("无效的 mode 参数，请使用 'append' 或 'replace'");
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "更新知识文件失败");
            return FunctionResult.FailureResult($"更新失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 搜索知识库（推荐用于获取相关上下文，使用向量语义检索）
    /// </summary>
    /// <param name="query">搜索关键词或自然语言问题</param>
    /// <param name="maxResults">最大返回结果数，默认 5</param>
    /// <returns>搜索结果</returns>
    public async Task<FunctionResult> SearchKnowledgeBase(
        string query,
        int maxResults = 5)
    {
        try
        {
            var results = await _knowledgeBase.SearchAsync(query, maxResults);

            if (results.Count == 0)
            {
                return FunctionResult.SuccessResult("未找到匹配的内容", Array.Empty<object>());
            }

            var formattedResults = results.Select(r => new
            {
                filePath = r.FilePath,
                snippet = r.Snippet,
                relevance = Math.Round(r.RelevanceScore, 2)
            }).ToList();

            _logger.Information("Function: 搜索知识库 '{Query}' 找到 {Count} 个结果",
                query, results.Count);

            return FunctionResult.SuccessResult(
                $"找到 {results.Count} 个匹配结果",
                formattedResults);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "搜索知识库失败");
            return FunctionResult.FailureResult($"搜索失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 读取知识文件（用于精确编辑已知文件，在更新前查看当前内容）
    /// </summary>
    /// <param name="filePath">文件相对路径，如 'Characters/user_profile.md'</param>
    /// <returns>文件完整内容</returns>
    public async Task<FunctionResult> ReadKnowledgeFile(string filePath)
    {
        try
        {
            // 确保文件扩展名
            if (!filePath.EndsWith(".md"))
            {
                filePath += ".md";
            }

            var content = await _knowledgeBase.ReadFileAsync(filePath);

            if (content == null)
            {
                return FunctionResult.FailureResult($"文件不存在: {filePath}");
            }

            _logger.Information("Function: 读取知识文件 {FilePath}", filePath);

            return FunctionResult.SuccessResult("读取成功", new { filePath, content });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "读取知识文件失败");
            return FunctionResult.FailureResult($"读取失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 删除知识文件（此操作不可撤销，请谨慎使用）
    /// </summary>
    /// <param name="filePath">要删除的文件相对路径</param>
    /// <returns>操作结果</returns>
    public async Task<FunctionResult> DeleteKnowledgeFile(string filePath)
    {
        try
        {
            // 确保文件扩展名
            if (!filePath.EndsWith(".md"))
            {
                filePath += ".md";
            }

            if (!await _knowledgeBase.FileExistsAsync(filePath))
            {
                return FunctionResult.FailureResult($"文件不存在: {filePath}");
            }

            await _knowledgeBase.DeleteFileAsync(filePath);

            _logger.Information("Function: 删除知识文件 {FilePath}", filePath);

            return FunctionResult.SuccessResult($"已删除: {filePath}");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "删除知识文件失败");
            return FunctionResult.FailureResult($"删除失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 列出所有知识文件
    /// </summary>
    /// <returns>文件列表</returns>
    public async Task<FunctionResult> ListKnowledgeFiles()
    {
        try
        {
            var files = await _knowledgeBase.ListFilesAsync();

            _logger.Information("Function: 列出 {Count} 个知识文件", files.Count);

            return FunctionResult.SuccessResult(
                $"共有 {files.Count} 个知识文件",
                files);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "列出知识文件失败");
            return FunctionResult.FailureResult($"查询失败: {ex.Message}");
        }
    }
}
