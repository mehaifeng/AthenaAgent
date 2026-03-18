using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
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
                return FunctionResult.FailureResult("搜索关键词不能为空");
            }

            if (!_webSearchService.IsConfigured)
            {
                return FunctionResult.FailureResult("Web Search 服务未配置或未启用，请在设置中配置后再使用");
            }

            var results = await _webSearchService.SearchAsync(query, maxResults);

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

            _logger.Information("Function: Web Search '{Query}' 找到 {Count} 个结果", query, results.Count);

            return FunctionResult.SuccessResult(
                $"网络搜索找到 {results.Count} 条相关结果",
                formattedResults);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Web Search 执行失败");
            return FunctionResult.FailureResult($"搜索失败: {ex.Message}");
        }
    }
}
