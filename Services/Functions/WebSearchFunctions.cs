using Athena.UI.Services.Interfaces;
using Athena.UI.Services.SubAgents;
using Serilog;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Athena.UI.Services.Functions;

/// <summary>
/// 网络搜索相关的 Function Calling 实现
/// </summary>
public class WebSearchFunctions
{
    private readonly IWebSearchService _webSearchService;
    private readonly ILogger _logger;

    public WebSearchFunctions(IWebSearchService webSearchService, ILogger logger)
    {
        _webSearchService = webSearchService;
        _logger = logger.ForContext<WebSearchFunctions>();
    }

    /// <summary>
    /// 执行网络搜索
    /// </summary>
    /// <param name="query">搜索关键词或自然语言问题</param>
    /// <param name="maxResults">最大返回结果数，默认 5</param>
    /// <returns>搜索结果</returns>
    public async Task<FunctionResult> WebSearchAsync(string query, int maxResults = 5)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return FunctionResult.FailureResult("Search query cannot be empty.");
            }

            if (!_webSearchService.IsConfigured)
            {
                return FunctionResult.FailureResult("Web Search service is not configured or enabled; please configure it in Settings before using.");
            }

            // 读取主对话循环经 AsyncLocal 透传的取消令牌，使用户点"停止"能中断网络搜索请求。
            var cancellationToken = ToolExecutionContext.CurrentCancellationToken;
            var results = await _webSearchService.SearchAsync(query, maxResults, cancellationToken);

            if (results.Count == 0)
            {
                return FunctionResult.SuccessResult("未找到相关搜索结果", Array.Empty<object>());
            }

            var formattedResults = results.Select(r => new
            {
                title = r.Title,
                url = r.Url,
                snippet = r.Snippet,
                source = r.Source,
                score = r.Score
            }).ToList();

            _logger.Information("Function: Web Search '{Query}' returned {Count} result(s)", query, results.Count);

            return FunctionResult.SuccessResult(
                $"网络搜索找到 {results.Count} 条相关结果",
                formattedResults);
        }
        catch (OperationCanceledException)
        {
            _logger.Information("Web Search execution cancelled by user stop request.");
            return FunctionResult.FailureResult("Web search was cancelled by the user.");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Web Search execution failed");
            return FunctionResult.FailureResult($"搜索失败: {ex.Message}");
        }
    }
}
