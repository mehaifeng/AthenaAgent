using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using Serilog;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Athena.UI.Services.Functions;

/// <summary>
/// 知识库管理相关的 Function Calling 实现
/// 仅保留核心的向量检索和专用知识创建，通用文件操作由 FileSystemFunctions 处理
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
    /// 创建知识文件（仅用于创建新记录，如需修改请使用通用系统文件修改工具）
    /// </summary>
    /// <param name="filePath">相对路径，如 'user_preferences/coding_style.md'</param>
    /// <param name="content">Markdown 格式的文件内容</param>
    /// <returns>操作结果</returns>
    public async Task<FunctionResult> CreateKnowledgeFile(
        string filePath,
        string content)
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
                return FunctionResult.FailureResult($"记录已存在: {filePath}。如果需要修改，请使用 modify_system_file 工具。");
            }

            await _knowledgeBase.CreateFileAsync(filePath, content);

            _logger.Information("Function: 创建知识记录 {FilePath}", filePath);

            return FunctionResult.SuccessResult(
                $"已成功存入知识库: {filePath}",
                new { filePath, created = true });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "创建知识记录失败");
            return FunctionResult.FailureResult($"创建失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 搜索知识库（使用向量语义检索获取相关背景）
    /// </summary>
    /// <param name="query">搜索关键词或自然语言问题</param>
    /// <param name="maxResults">最大返回结果数，默认 3</param>
    /// <returns>搜索结果</returns>
    public async Task<FunctionResult> SearchKnowledgeBase(
        string query,
        int maxResults = 3)
    {
        try
        {
            var results = await _knowledgeBase.SearchAsync(query, maxResults);

            if (results.Count == 0)
            {
                return FunctionResult.SuccessResult("未在知识库中找到相关背景信息", Array.Empty<object>());
            }

            var formattedResults = results.Select(r => new
            {
                filePath = r.FilePath,
                snippet = r.Snippet,
                relevance = Math.Round(r.RelevanceScore, 2)
            }).ToList();

            _logger.Information("Function: 语义检索 '{Query}' 找到 {Count} 个结果",
                query, results.Count);

            return FunctionResult.SuccessResult(
                $"从知识库检索到 {results.Count} 条相关背景",
                formattedResults);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "搜索知识库失败");
            return FunctionResult.FailureResult($"检索失败: {ex.Message}");
        }
    }
}
