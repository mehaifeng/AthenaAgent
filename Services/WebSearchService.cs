using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using Serilog;

namespace Athena.UI.Services;

/// <summary>
/// 网络搜索服务实现
/// 支持 Tavily、智谱 AI、百度三个供应商
/// </summary>
public class WebSearchService : IWebSearchService
{
    private readonly IConfigService _configService;
    private readonly ILogger _logger;
    private readonly HttpClient _httpClient;

    public bool IsConfigured
    {
        get
        {
            var config = GetConfig();
            return config.WebSearchEnabled && !string.IsNullOrWhiteSpace(config.WebSearchApiKey);
        }
    }

    public WebSearchService(IConfigService configService, ILogger logger)
    {
        _configService = configService;
        _logger = logger.ForContext<WebSearchService>();
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    /// <summary>
    /// 刷新配置（保留接口兼容性，现每次调用都会重新读取配置）
    /// </summary>
    public void RefreshConfig()
    {
        // 不再需要缓存清除，每次 GetConfig() 都会重新加载
    }

    private AppConfig GetConfig()
    {
        // 每次都重新加载，确保获取最新配置
        return _configService.Load();
    }

    public async Task<List<WebSearchResult>> SearchAsync(string query, int maxResults = 5)
    {
        var config = GetConfig();

        if (!config.WebSearchEnabled)
        {
            _logger.Warning("Web Search 未启用");
            return new List<WebSearchResult>();
        }

        if (string.IsNullOrWhiteSpace(config.WebSearchApiKey))
        {
            _logger.Warning("Web Search API Key 未配置");
            return new List<WebSearchResult>();
        }

        try
        {
            return config.WebSearchProvider.ToLower() switch
            {
                "tavily" => await SearchTavilyAsync(query, maxResults, config),
                "zhipu" or "智谱" => await SearchZhipuAsync(query, maxResults, config),
                "baidu" or "百度" => await SearchBaiduAsync(query, maxResults, config),
                _ => throw new NotSupportedException($"不支持的 Web Search 供应商: {config.WebSearchProvider}")
            };
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Web Search 执行失败: {Query}", query);
            throw;
        }
    }

    #region Tavily

    /// <summary>
    /// Tavily API 搜索
    /// 文档: https://docs.tavily.com/documentation/api-reference/endpoint/search
    /// </summary>
    private async Task<List<WebSearchResult>> SearchTavilyAsync(string query, int maxResults, AppConfig config)
    {
        var baseUrl = string.IsNullOrWhiteSpace(config.WebSearchBaseUrl)
            ? "https://api.tavily.com"
            : config.WebSearchBaseUrl;

        var request = new
        {
            api_key = config.WebSearchApiKey,
            query = query,
            max_results = maxResults,
            search_depth = "basic",
            include_answer = false,
            include_raw_content = false
        };

        var response = await _httpClient.PostAsJsonAsync($"{baseUrl}/search", request);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        var tavilyResponse = JsonSerializer.Deserialize<TavilyResponse>(json);

        var results = new List<WebSearchResult>();
        if (tavilyResponse?.Results != null)
        {
            foreach (var item in tavilyResponse.Results)
            {
                results.Add(new WebSearchResult
                {
                    Title = item.Title ?? string.Empty,
                    Url = item.Url ?? string.Empty,
                    Snippet = item.Content ?? string.Empty,
                    Score = item.Score,
                    Source = "Tavily"
                });
            }
        }

        _logger.Information("Tavily 搜索 '{Query}' 返回 {Count} 条结果", query, results.Count);
        return results;
    }

    private class TavilyResponse
    {
        [JsonPropertyName("results")]
        public List<TavilyResult>? Results { get; set; }
    }

    private class TavilyResult
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("content")]
        public string? Content { get; set; }

        [JsonPropertyName("score")]
        public double? Score { get; set; }
    }

    #endregion

    #region 智谱 AI

    /// <summary>
    /// 智谱 AI Web Search
    /// 文档: https://bigmodel.cn/dev/api/search-tool/web-search
    /// </summary>
    private async Task<List<WebSearchResult>> SearchZhipuAsync(string query, int maxResults, AppConfig config)
    {
        var baseUrl = string.IsNullOrWhiteSpace(config.WebSearchBaseUrl)
            ? "https://open.bigmodel.cn/api/paas/v4"
            : config.WebSearchBaseUrl;

        _httpClient.DefaultRequestHeaders.Clear();
        _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {config.WebSearchApiKey}");

        var request = new
        {
            search_query = query,
            search_result_count = maxResults
        };

        var response = await _httpClient.PostAsJsonAsync($"{baseUrl}/web_search", request);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        var zhipuResponse = JsonSerializer.Deserialize<ZhipuWebResponse>(json);

        var results = new List<WebSearchResult>();
        if (zhipuResponse?.Data != null)
        {
            foreach (var item in zhipuResponse.Data)
            {
                results.Add(new WebSearchResult
                {
                    Title = item.Title ?? string.Empty,
                    Url = item.Link ?? string.Empty,
                    Snippet = item.Content ?? item.Snippet ?? string.Empty,
                    Source = "智谱AI"
                });
            }
        }

        _logger.Information("智谱 AI 搜索 '{Query}' 返回 {Count} 条结果", query, results.Count);
        return results;
    }

    private class ZhipuWebResponse
    {
        [JsonPropertyName("data")]
        public List<ZhipuWebItem>? Data { get; set; }
    }

    private class ZhipuWebItem
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("link")]
        public string? Link { get; set; }

        [JsonPropertyName("content")]
        public string? Content { get; set; }

        [JsonPropertyName("snippet")]
        public string? Snippet { get; set; }
    }

    #endregion

    #region 百度

    /// <summary>
    /// 百度智能云 Web Search
    /// 文档: https://ai.baidu.com/ai-doc/AppBuilder/pmaxd1hvy
    /// </summary>
    private async Task<List<WebSearchResult>> SearchBaiduAsync(string query, int maxResults, AppConfig config)
    {
        var baseUrl = string.IsNullOrWhiteSpace(config.WebSearchBaseUrl)
            ? "https://qianfan.baidubce.com/v2/app/conversation/runs"
            : config.WebSearchBaseUrl;

        _httpClient.DefaultRequestHeaders.Clear();
        _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {config.WebSearchApiKey}");

        var request = new
        {
            query = query,
            stream = false,
            conversation_id = Guid.NewGuid().ToString(),
            tools = new[]
            {
                new
                {
                    type = "web_search",
                    web_search = new
                    {
                        enable = true,
                        max_search_results = maxResults
                    }
                }
            }
        };

        var content = new StringContent(
            JsonSerializer.Serialize(request),
            Encoding.UTF8,
            "application/json");

        var response = await _httpClient.PostAsync(baseUrl, content);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        var baiduResponse = JsonSerializer.Deserialize<BaiduWebResponse>(json);

        var results = new List<WebSearchResult>();
        if (baiduResponse?.ToolCalls != null)
        {
            foreach (var toolCall in baiduResponse.ToolCalls)
            {
                if (toolCall.Type == "web_search" && toolCall.SearchResult != null)
                {
                    foreach (var item in toolCall.SearchResult)
                    {
                        results.Add(new WebSearchResult
                        {
                            Title = item.Title ?? string.Empty,
                            Url = item.Url ?? string.Empty,
                            Snippet = item.Content ?? item.Snippet ?? string.Empty,
                            Source = "百度"
                        });
                    }
                }
            }
        }

        _logger.Information("百度搜索 '{Query}' 返回 {Count} 条结果", query, results.Count);
        return results;
    }

    private class BaiduWebResponse
    {
        [JsonPropertyName("tool_calls")]
        public List<BaiduToolCall>? ToolCalls { get; set; }
    }

    private class BaiduToolCall
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("search_result")]
        public List<BaiduSearchItem>? SearchResult { get; set; }
    }

    private class BaiduSearchItem
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("content")]
        public string? Content { get; set; }

        [JsonPropertyName("snippet")]
        public string? Snippet { get; set; }
    }

    #endregion

    /// <summary>
    /// 测试连接
    /// </summary>
    public async Task<(bool Success, string Message)> TestConnectionAsync()
    {
        var config = GetConfig();

        if (!config.WebSearchEnabled)
        {
            return (false, "Web Search 未启用");
        }

        if (string.IsNullOrWhiteSpace(config.WebSearchApiKey))
        {
            return (false, "API Key 未配置");
        }

        try
        {
            var results = await SearchAsync("test", 1);
            return (true, $"连接成功，返回 {results.Count} 条结果");
        }
        catch (Exception ex)
        {
            return (false, $"连接失败: {ex.Message}");
        }
    }
}
