using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services;

/// <summary>
/// Dedicated web-search adapters. Provider credentials are intentionally not
/// borrowed from the chat-model connection.
/// </summary>
public sealed class WebSearchService : IWebSearchService, IDisposable
{
    private readonly IConfigService _configService;
    private readonly ILogger _logger;
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly ILocalizationService? _localizationService;

    public WebSearchService(IConfigService configService, ILogger logger, ILocalizationService? localizationService = null)
    {
        _configService = configService;
        _logger = logger.ForContext<WebSearchService>();
        _localizationService = localizationService;
    }

    public bool IsConfigured
    {
        get
        {
            var config = _configService.Load();
            ApplyActiveProviderSettings(config);
            return HasRequiredConfiguration(config);
        }
    }

    public void RefreshConfig()
    {
    }

    public async Task<List<WebSearchResult>> SearchAsync(
        string query,
        int maxResults = 5,
        CancellationToken cancellationToken = default)
    {
        var config = _configService.Load();
        ApplyActiveProviderSettings(config);
        if (!HasRequiredConfiguration(config))
        {
            _logger.Warning("Web Search provider configuration is incomplete");
            return [];
        }

        maxResults = Math.Clamp(maxResults, 1, 20);
        try
        {
            return config.WebSearchProvider.ToLowerInvariant() switch
            {
                "brave" => await SearchBraveAsync(query, maxResults, config, cancellationToken),
                "duckduckgo" => await SearchDuckDuckGoAsync(query, maxResults, cancellationToken),
                "exa" => await SearchExaAsync(query, maxResults, config, cancellationToken),
                "firecrawl" or "firecrawlselfhosted" => await SearchFirecrawlAsync(query, maxResults, config, cancellationToken),
                "parallel" => await SearchParallelAsync(query, maxResults, config, cancellationToken),
                "searxng" => await SearchSearXngAsync(query, maxResults, config, cancellationToken),
                "tavily" => await SearchTavilyAsync(query, maxResults, config, cancellationToken),
                "xai" => await SearchXaiAsync(query, maxResults, config, cancellationToken),
                "websearchapi" => await SearchWebSearchApiAsync(query, maxResults, config, cancellationToken),
                "zhipu" => await SearchZhipuAsync(query, maxResults, config, cancellationToken),
                "baidu" => await SearchBaiduAsync(query, maxResults, config, cancellationToken),
                _ => throw new NotSupportedException($"Unsupported Web Search provider: {config.WebSearchProvider}")
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Web Search failed. Provider={Provider}, Query={Query}", config.WebSearchProvider, query);
            throw;
        }
    }

    public async Task<(bool Success, string Message)> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        var config = _configService.Load();
        ApplyActiveProviderSettings(config);
        if (!config.WebSearchEnabled)
            return (false, Localize("WebSearch.NotEnabled", "Web Search is not enabled"));
        if (!HasRequiredConfiguration(config))
            return (false, Localize("WebSearch.ApiKeyMissing", "Web Search provider configuration is incomplete"));

        try
        {
            var results = await SearchAsync("Athena Agent", 1, cancellationToken);
            return (true, $"Connection succeeded — {results.Count} result(s)");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (false, $"Connection failed: {ex.Message}");
        }
    }

    private string Localize(string key, string fallback)
        => _localizationService?.GetString(key, fallback) ?? fallback;

    public void Dispose() => _httpClient.Dispose();

    private static bool HasRequiredConfiguration(AppConfig config)
    {
        if (!config.WebSearchEnabled) return false;
        return config.WebSearchProvider.ToLowerInvariant() switch
        {
            "duckduckgo" => true,
            "searxng" or "firecrawlselfhosted" => !string.IsNullOrWhiteSpace(config.WebSearchBaseUrl),
            _ => !string.IsNullOrWhiteSpace(config.WebSearchApiKey)
        };
    }

    private static void ApplyActiveProviderSettings(AppConfig config)
    {
        var settings = config.WebSearchProviderSettings.FirstOrDefault(
            item => item.ProviderId.Equals(config.WebSearchProvider, StringComparison.OrdinalIgnoreCase));
        if (settings == null) return;
        config.WebSearchBaseUrl = settings.BaseUrl;
        config.WebSearchApiKey = settings.ApiKey;
        config.WebSearchModel = settings.Model;
        config.WebSearchAppId = settings.AppId;
        config.WebSearchMode = settings.Mode;
    }

    private static HttpRequestMessage JsonRequest(HttpMethod method, string url, object? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (body != null) request.Content = JsonContent.Create(body);
        return request;
    }

    private async Task<List<WebSearchResult>> SearchBraveAsync(
        string query, int limit, AppConfig config, CancellationToken token)
    {
        var endpoint = string.IsNullOrWhiteSpace(config.WebSearchBaseUrl)
            ? "https://api.search.brave.com/res/v1/web/search"
            : config.WebSearchBaseUrl.TrimEnd('/');
        using var request = JsonRequest(HttpMethod.Get,
            $"{endpoint}?q={Uri.EscapeDataString(query)}&count={limit}");
        request.Headers.Add("X-Subscription-Token", config.WebSearchApiKey);
        using var json = await SendJsonAsync(request, token);
        return ReadResultArray(json.RootElement.GetProperty("web").GetProperty("results"), "Brave", limit);
    }

    private async Task<List<WebSearchResult>> SearchDuckDuckGoAsync(
        string query, int limit, CancellationToken token)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://html.duckduckgo.com/html/?q={Uri.EscapeDataString(query)}");
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 AthenaAgent/1.0");
        using var response = await _httpClient.SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(token);
        var links = Regex.Matches(
            html,
            "<a[^>]+class=\"result__a\"[^>]+href=\"(?<url>[^\"]+)\"[^>]*>(?<title>.*?)</a>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var snippets = Regex.Matches(
            html,
            "<a[^>]+class=\"result__snippet\"[^>]*>(?<snippet>.*?)</a>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        var results = new List<WebSearchResult>();
        for (var index = 0; index < Math.Min(limit, links.Count); index++)
        {
            var url = WebUtility.HtmlDecode(links[index].Groups["url"].Value);
            var redirectMatch = Regex.Match(url, @"[?&]uddg=([^&]+)", RegexOptions.IgnoreCase);
            if (redirectMatch.Success) url = Uri.UnescapeDataString(redirectMatch.Groups[1].Value);
            results.Add(new WebSearchResult
            {
                Title = StripHtml(links[index].Groups["title"].Value),
                Url = url,
                Snippet = index < snippets.Count ? StripHtml(snippets[index].Groups["snippet"].Value) : string.Empty,
                Source = "DuckDuckGo"
            });
        }
        return results;
    }

    private async Task<List<WebSearchResult>> SearchExaAsync(
        string query, int limit, AppConfig config, CancellationToken token)
    {
        using var request = JsonRequest(HttpMethod.Post, "https://api.exa.ai/search", new
        {
            query,
            numResults = limit,
            contents = new { highlights = new { numSentences = 3 } }
        });
        request.Headers.Add("x-api-key", config.WebSearchApiKey);
        using var json = await SendJsonAsync(request, token);
        return ReadResultArray(json.RootElement.GetProperty("results"), "Exa", limit);
    }

    private async Task<List<WebSearchResult>> SearchFirecrawlAsync(
        string query, int limit, AppConfig config, CancellationToken token)
    {
        var root = string.IsNullOrWhiteSpace(config.WebSearchBaseUrl)
            ? "https://api.firecrawl.dev/v1"
            : config.WebSearchBaseUrl.TrimEnd('/');
        using var request = JsonRequest(HttpMethod.Post, $"{root}/search", new { query, limit });
        if (!string.IsNullOrWhiteSpace(config.WebSearchApiKey))
            request.Headers.Authorization = new("Bearer", config.WebSearchApiKey);
        using var json = await SendJsonAsync(request, token);
        return ReadResultArray(json.RootElement.GetProperty("data"), "Firecrawl", limit);
    }

    private async Task<List<WebSearchResult>> SearchParallelAsync(
        string query, int limit, AppConfig config, CancellationToken token)
    {
        using var request = JsonRequest(HttpMethod.Post, "https://api.parallel.ai/v1beta/search", new
        {
            search_queries = new[] { query },
            objective = query,
            mode = string.IsNullOrWhiteSpace(config.WebSearchMode) ? "fast" : config.WebSearchMode,
            max_results = limit
        });
        request.Headers.Add("x-api-key", config.WebSearchApiKey);
        using var json = await SendJsonAsync(request, token);
        return ReadResultArray(json.RootElement.GetProperty("results"), "Parallel", limit);
    }

    private async Task<List<WebSearchResult>> SearchSearXngAsync(
        string query, int limit, AppConfig config, CancellationToken token)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{config.WebSearchBaseUrl.TrimEnd('/')}/search?q={Uri.EscapeDataString(query)}&format=json");
        using var json = await SendJsonAsync(request, token);
        return ReadResultArray(json.RootElement.GetProperty("results"), "SearXNG", limit);
    }

    private async Task<List<WebSearchResult>> SearchTavilyAsync(
        string query, int limit, AppConfig config, CancellationToken token)
    {
        var root = string.IsNullOrWhiteSpace(config.WebSearchBaseUrl)
            ? "https://api.tavily.com"
            : config.WebSearchBaseUrl.TrimEnd('/');
        using var request = JsonRequest(HttpMethod.Post, $"{root}/search", new
        {
            api_key = config.WebSearchApiKey,
            query,
            max_results = limit,
            search_depth = "basic",
            include_answer = false,
            include_raw_content = false
        });
        using var json = await SendJsonAsync(request, token);
        return ReadResultArray(json.RootElement.GetProperty("results"), "Tavily", limit);
    }

    private async Task<List<WebSearchResult>> SearchXaiAsync(
        string query, int limit, AppConfig config, CancellationToken token)
    {
        var root = string.IsNullOrWhiteSpace(config.WebSearchBaseUrl)
            ? "https://api.x.ai/v1"
            : config.WebSearchBaseUrl.TrimEnd('/');
        using var request = JsonRequest(HttpMethod.Post, $"{root}/responses", new
        {
            model = string.IsNullOrWhiteSpace(config.WebSearchModel) ? "grok-4-fast" : config.WebSearchModel,
            input = new[]
            {
                new
                {
                    role = "user",
                    content = $"Search the web for: {query}. Return only a JSON array of at most {limit} objects with title, url, and snippet."
                }
            },
            tools = new[] { new { type = "web_search" } },
            include = new[] { "no_inline_citations" }
        });
        request.Headers.Authorization = new("Bearer", config.WebSearchApiKey);
        using var json = await SendJsonAsync(request, token);
        var text = FindString(json.RootElement, "output_text") ?? FindString(json.RootElement, "text") ?? string.Empty;
        var first = text.IndexOf('[');
        var last = text.LastIndexOf(']');
        if (first < 0 || last <= first) return [];
        using var output = JsonDocument.Parse(text[first..(last + 1)]);
        return ReadResultArray(output.RootElement, "xAI", limit);
    }

    private async Task<List<WebSearchResult>> SearchWebSearchApiAsync(
        string query, int limit, AppConfig config, CancellationToken token)
    {
        var root = string.IsNullOrWhiteSpace(config.WebSearchBaseUrl)
            ? "https://api.websearchapi.ai"
            : config.WebSearchBaseUrl.TrimEnd('/');
        using var request = JsonRequest(HttpMethod.Post, $"{root}/ai-search", new
        {
            query,
            maxResults = limit,
            includeContent = true,
            includeAnswer = false
        });
        request.Headers.Authorization = new("Bearer", config.WebSearchApiKey);
        using var json = await SendJsonAsync(request, token);
        var array = json.RootElement.TryGetProperty("organic", out var organic) ? organic : default;
        return ReadResultArray(array, "WebSearchAPI", limit);
    }

    private async Task<List<WebSearchResult>> SearchZhipuAsync(
        string query, int limit, AppConfig config, CancellationToken token)
    {
        var root = string.IsNullOrWhiteSpace(config.WebSearchBaseUrl)
            ? "https://open.bigmodel.cn/api/paas/v4"
            : config.WebSearchBaseUrl.TrimEnd('/');
        using var request = JsonRequest(HttpMethod.Post, $"{root}/web_search", new
        {
            search_query = query,
            search_result_count = limit
        });
        request.Headers.Authorization = new("Bearer", config.WebSearchApiKey);
        using var json = await SendJsonAsync(request, token);
        return ReadResultArray(json.RootElement.GetProperty("data"), "Zhipu", limit);
    }

    private async Task<List<WebSearchResult>> SearchBaiduAsync(
        string query, int limit, AppConfig config, CancellationToken token)
    {
        var endpoint = string.IsNullOrWhiteSpace(config.WebSearchBaseUrl)
            ? "https://qianfan.baidubce.com/v2/ai_search/web_search"
            : config.WebSearchBaseUrl;
        using var request = JsonRequest(HttpMethod.Post, endpoint, new
        {
            app_id = config.WebSearchAppId,
            messages = new[] { new { role = "user", content = query } },
            edition = "standard",
            search_source = "baidu_search_v2"
        });
        request.Headers.Authorization = new("Bearer", config.WebSearchApiKey);
        using var json = await SendJsonAsync(request, token);
        return ReadResultArray(json.RootElement.GetProperty("references"), "Baidu", limit);
    }

    private async Task<JsonDocument> SendJsonAsync(HttpRequestMessage request, CancellationToken token)
    {
        using var response = await _httpClient.SendAsync(request, token);
        var body = await response.Content.ReadAsStringAsync(token);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body);
    }

    private static List<WebSearchResult> ReadResultArray(JsonElement array, string source, int limit)
    {
        var results = new List<WebSearchResult>();
        if (array.ValueKind != JsonValueKind.Array) return results;
        foreach (var item in array.EnumerateArray().Take(limit))
        {
            var snippet = FindString(item, "description")
                ?? FindString(item, "snippet")
                ?? FindString(item, "content")
                ?? FindString(item, "text")
                ?? JoinStringArray(item, "highlights")
                ?? JoinStringArray(item, "excerpts")
                ?? string.Empty;
            results.Add(new WebSearchResult
            {
                Title = FindString(item, "title") ?? string.Empty,
                Url = FindString(item, "url") ?? FindString(item, "link") ?? string.Empty,
                Snippet = snippet,
                Source = source,
                Score = FindDouble(item, "score")
            });
        }
        return results;
    }

    private static string? FindString(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals(name) && property.Value.ValueKind == JsonValueKind.String)
                    return property.Value.GetString();
                var nested = FindString(property.Value, name);
                if (nested != null) return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindString(item, name);
                if (nested != null) return nested;
            }
        }
        return null;
    }

    private static double? FindDouble(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out var number))
            return number;
        return null;
    }

    private static string? JoinStringArray(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(name, out var array)
            || array.ValueKind != JsonValueKind.Array)
            return null;
        return string.Join(" ", array.EnumerateArray().Select(item => item.ToString()));
    }

    private static string StripHtml(string value)
        => WebUtility.HtmlDecode(Regex.Replace(value, "<[^>]+>", " ")).Trim();
}
