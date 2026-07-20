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
    private readonly IConversationSessionAccessor? _sessionAccessor;
    private readonly IWorkspaceService? _workspaceService;

    /// <summary>
    /// 语义查重门槛：新内容与某已有文件的最大分块相似度 ≥ 此值即视为"同类记录已存在"，
    /// 拦截新建并引导改用 modify。针对 text-embedding-3-small 的经验值，可随模型校准（日志会打印命中相似度）。
    /// </summary>
    private const double DuplicateGuardThreshold = 0.72;

    public KnowledgeBaseFunctions(
        IKnowledgeBaseService knowledgeBase,
        ILogger logger,
        IConversationSessionAccessor? sessionAccessor = null,
        IWorkspaceService? workspaceService = null)
    {
        _knowledgeBase = knowledgeBase;
        _logger = logger.ForContext<KnowledgeBaseFunctions>();
        _sessionAccessor = sessionAccessor;
        _workspaceService = workspaceService;
    }

    /// <summary>
    /// 创建知识文件。带路径查重 + 语义查重双重护栏：命中已有同类记录时拒绝新建、引导改用修改，
    /// 从根本上避免同类信息散落到多个不同文件。
    /// </summary>
    /// <param name="filePath">相对路径，如 'user_preferences/coding_style.md'</param>
    /// <param name="content">Markdown 格式的文件内容</param>
    /// <param name="allowDuplicate">确认这是独立新主题、需绕过语义查重时置 true</param>
    /// <param name="workspaceScoped">工作区受管知识文件不支持新建；false 时写入全局知识库</param>
    /// <returns>操作结果</returns>
    public async Task<FunctionResult> CreateKnowledgeFile(
        string filePath,
        string content,
        bool allowDuplicate = false,
        bool workspaceScoped = false)
    {
        try
        {
            // 确保文件扩展名
            if (!filePath.EndsWith(".md"))
            {
                filePath += ".md";
            }

            // 判断写入目标：工作区知识库 or 全局知识库
            var activeWorkspaceId = _sessionAccessor?.CurrentWorkspaceId;
            if (workspaceScoped && !string.IsNullOrEmpty(activeWorkspaceId))
            {
                var managedPath = _workspaceService == null
                    ? null
                    : await _workspaceService.GetKnowledgeFilePathAsync(activeWorkspaceId);
                return FunctionResult.FailureResult(
                    "工作区知识文件由系统创建和管理，不能用 create_new_memory 新建。请使用 modify_system_file 修改返回的绝对路径。",
                    new { scope = "workspace", workspaceId = activeWorkspaceId, fullPath = managedPath });
            }

            if (workspaceScoped)
            {
                return FunctionResult.FailureResult("未选择工作区，无法新建工作区知识文件。请选择工作区后，使用其 system prompt 中提供的知识文件路径进行修改。");
            }

            // --- 全局知识库路径（现有逻辑） ---

            // 路径查重：同名文件已存在
            if (await _knowledgeBase.FileExistsAsync(filePath))
            {
                return FunctionResult.FailureResult(
                    $"记录已存在: {filePath}。如需补充/更新，请用 modify_system_file 修改该文件（可直接使用此相对路径）。",
                    new { filePath, scope = "global" });
            }

            // 语义查重护栏
            if (!allowDuplicate)
            {
                var similar = await _knowledgeBase.FindSimilarFilesAsync(content, DuplicateGuardThreshold, 3);
                if (similar.Count > 0)
                {
                    var listText = string.Join("; ", similar.Select(s => $"{s.FilePath} (相似度 {s.Similarity:F2})"));
                    _logger.Information("Function: create_new_memory 命中疑似重复并拦截。最相近: {File} ({Sim:F2})",
                        similar[0].FilePath, similar[0].Similarity);

                    return FunctionResult.FailureResult(
                        $"知识库中已有高度相似的记录，未新建文件以避免同类信息碎片化。相近文件: {listText}。" +
                        $"请优先用 modify_system_file 把新信息合并进最相关的那个文件" +
                        $"（先用 read_system_file 读取其当前内容，路径可直接用上面的相对路径）。" +
                        $"仅当你确认这是一条真正独立的新主题时，才用更具体的 filePath 并将 allowDuplicate 设为 true 重新调用。",
                        new { blocked = true, reason = "semantic_duplicate", similarFiles = similar, scope = "global" });
                }
            }

            await _knowledgeBase.CreateFileAsync(filePath, content);

            _logger.Information("Function: 创建知识记录 {FilePath} (global)", filePath);

            return FunctionResult.SuccessResult(
                $"已成功存入知识库: {filePath}",
                new { filePath, created = true, scope = "global" });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "创建知识记录失败");
            return FunctionResult.FailureResult($"创建失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 搜索知识库（Embedding 可用时混合检索，否则使用本地 FTS/BM25）
    /// </summary>
    /// <param name="query">搜索关键词或自然语言问题</param>
    /// <param name="maxResults">最大返回结果数，默认 3</param>
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
                return FunctionResult.SuccessResult("未在知识库中找到相关背景信息", Array.Empty<object>());
            }

            var formattedResults = results.Select(r => new
            {
                filePath = r.FilePath,
                headingPath = r.HeadingPath,
                matchCount = r.MatchCount,
                snippet = r.Snippet,
                retrievalMode = r.RetrievalMode,
                // RRF 的绝对值不是概率或可信度；仅在混合检索时输出可解释的余弦相似度。
                semanticSimilarity = r.RetrievalMode == "hybrid"
                    ? Math.Round(r.RelevanceScore, 2)
                    : (double?)null
            }).ToList();

            _logger.Information("Function: 知识库检索 '{Query}' 找到 {Count} 个结果",
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
