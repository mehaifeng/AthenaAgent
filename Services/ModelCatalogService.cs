using Athena.UI.Services.Interfaces;
using OpenAI;
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
/// 基于 OpenAI .NET SDK 的 <see cref="OpenAIModelClient"/> 实现模型列表查询。
/// 复用各模型字段已配置的 BaseUrl（已含 /v1），SDK 会在其后追加 /models，
/// 与现有 ChatClient 的端点处理保持一致。
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

        OpenAIModelClient client;
        try
        {
            var options = new OpenAIClientOptions();
            if (!string.IsNullOrWhiteSpace(baseUrl))
            {
                options.Endpoint = new Uri(baseUrl.Trim());
            }

            client = new OpenAIModelClient(new ApiKeyCredential(apiKey.Trim()), options);
        }
        catch (UriFormatException)
        {
            return ModelCatalogResult.Fail($"Invalid Base URL: {baseUrl}");
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "构造 OpenAIModelClient 失败");
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
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToList();

            _logger.Information("模型列表拉取成功，共 {Count} 个 (endpoint={Endpoint})", models.Count, baseUrl ?? "default");
            return ModelCatalogResult.Ok(models);
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
        catch (ClientResultException ex)
        {
            _logger.Warning(ex, "拉取模型列表失败 (HTTP {Status})", ex.Status);
            return ModelCatalogResult.Fail($"HTTP {ex.Status}: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "拉取模型列表失败");
            return ModelCatalogResult.Fail(ex.Message);
        }
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

        Uri requestUri;
        try
        {
            requestUri = new Uri(AppendModelsPath(baseUrl) + $"?output_modalities={Uri.EscapeDataString(outputModality)}");
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

            var models = new List<string>();
            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    if (item.TryGetProperty("id", out var idElement) &&
                        idElement.ValueKind == JsonValueKind.String &&
                        idElement.GetString() is { Length: > 0 } id)
                    {
                        models.Add(id);
                    }
                }
            }

            var distinct = models
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToList();

            _logger.Information("OpenRouter {Modality} 模型列表拉取成功，共 {Count} 个", outputModality, distinct.Count);
            return ModelCatalogResult.Ok(distinct);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return ModelCatalogResult.Fail($"Request timed out after {RequestTimeout.TotalSeconds:F0}s");
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "拉取 OpenRouter {Modality} 模型列表失败", outputModality);
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
    private static string AppendModelsPath(string baseUrl)
    {
        var trimmed = baseUrl.Trim().TrimEnd('/');
        return trimmed + "/models";
    }
}
