using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.Interfaces;

/// <summary>
/// 网络搜索服务接口
/// </summary>
public interface IWebSearchService
{
    /// <summary>
    /// 是否已配置（启用且有 API Key）
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>
    /// 执行网络搜索
    /// </summary>
    /// <param name="query">搜索关键词</param>
    /// <param name="maxResults">最大返回结果数</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>搜索结果列表</returns>
    Task<List<WebSearchResult>> SearchAsync(string query, int maxResults = 5, CancellationToken cancellationToken = default);

    /// <summary>
    /// 测试连接
    /// </summary>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>测试结果</returns>
    Task<(bool Success, string Message)> TestConnectionAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 网络搜索结果
/// </summary>
public class WebSearchResult
{
    /// <summary>
    /// 标题
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// URL
    /// </summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// 摘要内容
    /// </summary>
    public string Snippet { get; set; } = string.Empty;

    /// <summary>
    /// 来源（搜索引擎/提供商）
    /// </summary>
    public string? Source { get; set; }

    /// <summary>
    /// 相关度分数（0-1）
    /// </summary>
    public double? Score { get; set; }
}
