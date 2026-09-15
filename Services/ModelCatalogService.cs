using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using Athena.UI.Services.ModelMetadata;
using OpenAI.Models;
using Serilog;
using System;
using System.ClientModel;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services;

/// <summary>
/// 查询某个 OpenAI 协议端点的可用模型列表。
///
/// 主路径直接读原始 JSON，而不是用 SDK 的 <see cref="OpenAIModelClient"/>：
/// SDK 的模型对象只保留 OpenAI 协议规定的那几个字段，把端点多报的
/// context_length / architecture / supported_endpoint_types 全部丢掉，
/// 而那些字段正是 <see cref="MetadataValueSource.ProviderReported"/> 这一层唯一的来源
/// （见 <see cref="ProviderReportedMetadataParser"/>）。
///
/// SDK 路径保留为回退：它带着 <see cref="OpenAiClientOptionsFactory"/> 的重试策略，
/// 所以传输层抖动仍然走它；代价只是那一轮拿不到自报元数据，库存本身不受影响。
/// </summary>
public sealed class ModelCatalogService : IModelCatalogService
{
    // 防止个别代理端点长时间挂起拖住 UI 上的拉取动作。
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);
    private static readonly HttpClient SharedHttpClient = new();
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger = Log.ForContext<ModelCatalogService>();

    public ModelCatalogService() : this(SharedHttpClient) { }

    public ModelCatalogService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<ModelCatalogResult> GetModelsAsync(string? baseUrl, string? apiKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return ModelCatalogResult.Fail("API Key is empty");
        }

        var raw = await TryFetchRawAsync(baseUrl, apiKey, outputModality: null, cancellationToken).ConfigureAwait(false);
        if (raw != null)
        {
            return raw;
        }

        _logger.Debug(
            "Raw model listing unavailable (endpoint={Endpoint}); falling back to the SDK path without provider-reported metadata",
            baseUrl ?? "default");
        return await GetModelsViaSdkAsync(baseUrl, apiKey, cancellationToken).ConfigureAwait(false);
    }

    public Task<ModelCatalogResult> GetTextModelsAsync(string? baseUrl, string? apiKey, CancellationToken cancellationToken = default)
        => IsOpenRouter(baseUrl)
            ? GetOpenRouterModelsAsync(baseUrl!, apiKey, "text", cancellationToken)
            : GetModelsAsync(baseUrl, apiKey, cancellationToken);

    public async Task<ModelCatalogResult> GetEmbeddingModelsAsync(string? baseUrl, string? apiKey, CancellationToken cancellationToken = default)
    {
        // 只有 OpenRouter 支持 output_modalities 服务端过滤；其它端点回退到全量拉取，由上层按 ID 关键字筛选。
        if (!IsOpenRouter(baseUrl))
        {
            return await GetModelsAsync(baseUrl, apiKey, cancellationToken).ConfigureAwait(false);
        }

        return await GetOpenRouterModelsAsync(baseUrl!, apiKey, "embeddings", cancellationToken).ConfigureAwait(false);
    }

    private async Task<ModelCatalogResult> GetOpenRouterModelsAsync(
        string baseUrl,
        string? apiKey,
        string outputModality,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return ModelCatalogResult.Fail("API Key is empty");
        }

        // 模态过滤是 OpenRouter 私有的查询参数，SDK 回退路径发不出这个请求，
        // 回退过去只会拿到一份没过滤的全量列表——那比失败更难排查，所以这里不回退。
        var raw = await TryFetchRawAsync(baseUrl, apiKey, outputModality, cancellationToken).ConfigureAwait(false);
        if (raw == null)
        {
            return ModelCatalogResult.Fail($"Failed to fetch OpenRouter {outputModality} model list");
        }

        if (raw.Success)
        {
            _logger.Information("OpenRouter {Modality} model list fetched successfully, {Count} total", outputModality, raw.Models.Count);
        }

        return raw;
    }

    /// <summary>
    /// 读原始 <c>/models</c> 响应。返回 null 表示「这次没能解析成 JSON 目录」——
    /// 传输异常、响应体不是预期形状都算，调用方据此决定是否回退。
    /// 拿到了明确的 HTTP 错误状态则不返回 null：那是端点给出的确定答案，
    /// 再换一条路径重试只会多打一次必然失败的请求。
    /// </summary>
    private async Task<ModelCatalogResult?> TryFetchRawAsync(
        string? baseUrl,
        string apiKey,
        string? outputModality,
        CancellationToken cancellationToken)
    {
        Uri requestUri;
        try
        {
            var url = AppendModelsPath(baseUrl);
            if (!string.IsNullOrWhiteSpace(outputModality))
            {
                url += $"?output_modalities={Uri.EscapeDataString(outputModality)}";
            }

            requestUri = new Uri(url);
        }
        catch (UriFormatException)
        {
            return ModelCatalogResult.Fail($"Invalid Base URL: {baseUrl}");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(RequestTimeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());

            using var response = await _httpClient.SendAsync(request, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return ModelCatalogResult.Fail($"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token).ConfigureAwait(false);

            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
            {
                // 形状不认识；交给 SDK 路径去试，它对少数非标准端点的兼容更宽。
                return null;
            }

            var ids = new List<string>();
            foreach (var item in data.EnumerateArray())
            {
                if (ProviderReportedMetadataParser.ReadId(item) is { Length: > 0 } id)
                {
                    ids.Add(id);
                }
            }

            var reported = ProviderReportedMetadataParser.ParseCatalog(doc.RootElement);
            var distinct = ids
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToList();

            _logger.Information(
                "Model list fetched successfully, {Count} total, {ReportedCount} with provider-reported metadata (endpoint={Endpoint})",
                distinct.Count, reported.Count, baseUrl ?? "default");
            return ModelCatalogResult.Ok(distinct, reported);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 外部主动取消，向上抛出由调用方处理。
            throw;
        }
        catch (OperationCanceledException)
        {
            // 仅内部超时触发。
            return ModelCatalogResult.Fail($"Request timed out after {RequestTimeout.TotalSeconds:F0}s");
        }
        catch (JsonException ex)
        {
            _logger.Debug(ex, "Model list response was not valid JSON (endpoint={Endpoint})", baseUrl ?? "default");
            return null;
        }
        catch (HttpRequestException ex)
        {
            _logger.Debug(ex, "Raw model list request failed at the transport layer (endpoint={Endpoint})", baseUrl ?? "default");
            return null;
        }
    }

    /// <summary>
    /// SDK 回退路径。只产出模型 ID：SDK 的模型对象不暴露协议之外的字段，
    /// 因此结果里的 <c>Reported</c> 保持 null，上层据此保留已有的自报元数据。
    /// </summary>
    private async Task<ModelCatalogResult> GetModelsViaSdkAsync(string? baseUrl, string apiKey, CancellationToken cancellationToken)
    {
        OpenAIModelClient client;
        try
        {
            var options = OpenAiClientOptionsFactory.Create(baseUrl, (int)RequestTimeout.TotalSeconds);

            client = new OpenAIModelClient(new ApiKeyCredential(apiKey.Trim()), options);
        }
        catch (UriFormatException)
        {
            return ModelCatalogResult.Fail($"Invalid Base URL: {baseUrl}");
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to construct OpenAIModelClient");
            return ModelCatalogResult.Fail(ex.Message);
        }

        // 叠加内部超时，但仍尊重外部取消（例如用户离开页面）。
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(RequestTimeout);

        try
        {
            var result = await client.GetModelsAsync(cts.Token).ConfigureAwait(false);

            var models = result.Value
                .Select(m => m.Id)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToList();

            _logger.Information("Model list fetched via SDK fallback, {Count} total (endpoint={Endpoint})", models.Count, baseUrl ?? "default");
            return ModelCatalogResult.Ok(models);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return ModelCatalogResult.Fail($"Request timed out after {RequestTimeout.TotalSeconds:F0}s");
        }
        catch (ClientResultException ex)
        {
            _logger.Warning(ex, "Failed to fetch model list (HTTP {Status})", ex.Status);
            return ModelCatalogResult.Fail($"HTTP {ex.Status}: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to fetch model list");
            return ModelCatalogResult.Fail(ex.Message);
        }
    }

    /// <summary>端点主机是否为 OpenRouter（含自定义子域，如 openrouter.ai）。</summary>
    private static bool IsOpenRouter(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Host.Equals("openrouter.ai", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".openrouter.ai", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>在已含 /v1 的 BaseUrl 后追加 /models，与 SDK 端点处理保持一致。</summary>
    private static string AppendModelsPath(string? baseUrl)
    {
        var trimmed = (string.IsNullOrWhiteSpace(baseUrl) ? "https://api.openai.com/v1" : baseUrl).Trim().TrimEnd('/');
        return trimmed + "/models";
    }
}
