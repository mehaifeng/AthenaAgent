using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using Serilog;

namespace Athena.UI.Services;

/// <summary>
/// 网络搜索服务实现
/// 支持 Tavily、WebSearchAPI、智谱 AI、百度四个供应商
/// </summary>
public class WebSearchService : IWebSearchService
{
    private readonly IConfigService _configService;
    private readonly ILogger _logger;
    private readonly HttpClient _httpClient;
    private readonly ILocalizationService? _localizationService;

    private string GetLocalized(string key, string defaultValue)
        => _localizationService?.GetString(key, defaultValue) ?? defaultValue;

    public bool IsConfigured
    {
        get
        {
            var config = GetConfig();
            return config.WebSearchEnabled && !string.IsNullOrWhiteSpace(config.WebSearchApiKey);
        }
    }

    public WebSearchService(IConfigService configService, ILogger logger, ILocalizationService? localizationService = null)
    {
        _configService = configService;
        _logger = logger.ForContext<WebSearchService>();
        _localizationService = localizationService;
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

    public async Task<List<WebSearchResult>> SearchAsync(string query, int maxResults = 5, CancellationToken cancellationToken = default)
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
                "tavily" => await SearchTavilyAsync(query, maxResults, config, cancellationToken),
                "websearchapi" => await SearchWebSearchApiAsync(query, maxResults, config, cancellationToken),
                "zhipu" or "智谱" => await SearchZhipuAsync(query, maxResults, config, cancellationToken),
                "baidu" or "百度" => await SearchBaiduAsync(query, maxResults, config, cancellationToken),
                _ => throw new NotSupportedException($"不支持的 Web Search 供应商: {config.WebSearchProvider}")
            };
        }
        catch (OperationCanceledException)
        {
            throw;
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
    private async Task<List<WebSearchResult>> SearchTavilyAsync(string query, int maxResults, AppConfig config, CancellationToken cancellationToken)
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

        var response = await _httpClient.PostAsJsonAsync($"{baseUrl}/search", request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
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

    #region WebSearchAPI

    /// <summary>
    /// WebSearchAPI 搜索
    /// 文档: https://websearchapi.ai
    /// </summary>
    private async Task<List<WebSearchResult>> SearchWebSearchApiAsync(string query, int maxResults, AppConfig config, CancellationToken cancellationToken)
    {
        var baseUrl = string.IsNullOrWhiteSpace(config.WebSearchBaseUrl)
            ? "https://api.websearchapi.ai"
            : config.WebSearchBaseUrl.TrimEnd('/');

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/ai-search")
        {
            Content = JsonContent.Create(new
            {
                query,
                maxResults,
                includeContent = true,
                contentLength = "medium",
                includeAnswer = false,
                safeSearch = true
            })
        };
        request.Headers.Add("Authorization", $"Bearer {config.WebSearchApiKey}");

        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var parsed = JsonSerializer.Deserialize<WebSearchApiResponse>(json);

        var results = new List<WebSearchResult>();
        if (parsed?.Organic != null)
        {
            foreach (var item in parsed.Organic)
            {
                results.Add(new WebSearchResult
                {
                    Title = item.Title ?? string.Empty,
                    Url = item.Url ?? string.Empty,
                    Snippet = !string.IsNullOrWhiteSpace(item.Content) ? item.Content! : (item.Description ?? string.Empty),
                    Score = item.Score,
                    Source = "WebSearchAPI"
                });
            }
        }

        _logger.Information("WebSearchAPI 搜索 '{Query}' 返回 {Count} 条结果", query, results.Count);
        return results;
    }

    private class WebSearchApiResponse
    {
        [JsonPropertyName("answer")]
        public string? Answer { get; set; }

        [JsonPropertyName("organic")]
        public List<WebSearchApiItem>? Organic { get; set; }
    }

    private class WebSearchApiItem
    {
        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("content")]
        public string? Content { get; set; }

        [JsonPropertyName("position")]
        public int? Position { get; set; }

        [JsonPropertyName("score")]
        public double? Score { get; set; }
    }

    #endregion

    #region 智谱 AI

    /// <summary>
    /// 智谱 AI Web Search
    /// 文档: https://bigmodel.cn/dev/api/search-tool/web-search
    /// </summary>
    private async Task<List<WebSearchResult>> SearchZhipuAsync(string query, int maxResults, AppConfig config, CancellationToken cancellationToken)
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

        var response = await _httpClient.PostAsJsonAsync($"{baseUrl}/web_search", request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
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
                    Source = "Zhipu"
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
    /// API 示例: https://qianfan.baidubce.com/v2/ai_search/web_search
    /// </summary>
    private async Task<List<WebSearchResult>> SearchBaiduAsync(string query, int maxResults, AppConfig config, CancellationToken cancellationToken)
    {
        var baseUrl = string.IsNullOrWhiteSpace(config.WebSearchBaseUrl)
            ? "https://qianfan.baidubce.com/v2/ai_search/web_search"
            : config.WebSearchBaseUrl;

        _httpClient.DefaultRequestHeaders.Clear();
        _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {config.WebSearchApiKey}");

        var request = new
        {
            app_id = config.WebSearchAppId,
            messages = new[]
            {
                new { role = "user", content = query }
            },
            edition = "standard",
            search_source = "baidu_search_v2"
        };

        var requestJson = JsonSerializer.Serialize(request);
        _logger.Debug("百度搜索请求: {RequestJson}", requestJson);

        var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
        var response = await _httpClient.PostAsync(baseUrl, content, cancellationToken);

        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        _logger.Debug("百度搜索响应: {ResponseJson}", responseJson);

        if (!response.IsSuccessStatusCode)
        {
            _logger.Error("百度搜索失败: Status={Status}, Response={Response}", response.StatusCode, responseJson);
            throw new HttpRequestException($"百度搜索请求失败: {response.StatusCode}, {responseJson}");
        }

        var baiduResponse = JsonSerializer.Deserialize<BaiduWebResponse>(responseJson);

        var results = new List<WebSearchResult>();
        if (baiduResponse?.References != null)
        {
            foreach (var reference in baiduResponse.References.Take(maxResults))
            {
                results.Add(new WebSearchResult
                {
                    Title = reference.Title ?? string.Empty,
                    Url = reference.Url ?? string.Empty,
                    Snippet = reference.Snippet ?? reference.Content ?? string.Empty,
                    Source = "Baidu"
                });
            }
        }

        _logger.Information("百度搜索 '{Query}' 返回 {Count} 条结果", query, results.Count);
        return results;
    }

    private class BaiduWebResponse
    {
        [JsonPropertyName("request_id")]
        public string? RequestId { get; set; }

        [JsonPropertyName("references")]
        public List<BaiduReference>? References { get; set; }
    }

    private class BaiduReference
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

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
            return (false, GetLocalized("WebSearch.NotEnabled", "Web Search is not enabled"));
        }

        if (string.IsNullOrWhiteSpace(config.WebSearchApiKey))
        {
            return (false, GetLocalized("WebSearch.ApiKeyMissing", "Web Search API Key is not configured"));
        }

        try
        {
            var results = await SearchAsync("test", 1);
            return (true, string.Format(GetLocalized("WebSearch.TestSuccess", "Connection succeeded — {0} result(s)"), results.Count));
        }
        catch (Exception ex)
        {
            return (false, string.Format(GetLocalized("Service.ConnectionFailed", "Connection failed: {0}"), ex.Message));
        }
    }
}
