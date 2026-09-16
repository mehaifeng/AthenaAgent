using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using Athena.UI.Services.ModelMetadata;
using OpenAI.Models;
using Serilog;
using System;
using System.ClientModel;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
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
    private static readonly HttpClient SharedHttpClient = new();
    private readonly HttpClient _httpClient;
    private readonly IConfigService _configService;
    private readonly ILogger _logger;

    public ModelCatalogService(IConfigService configService)
        : this(configService, SharedHttpClient, Log.ForContext<ModelCatalogService>()) { }

    public ModelCatalogService(IConfigService configService, HttpClient httpClient, ILogger logger)
    {
        _configService = configService;
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <summary>
    /// 本次请求的超时（秒）。它必须跟随 <c>AppConfig.Timeout</c>，不能是本文件里的一个独立常量：
    /// 模型列表和对话走的是同一条链路、同一个端点，链路变慢时一个更短的硬编码值只会让列表
    /// 先于对话放弃，用户看到的是「对话能用，但刷新模型列表坏了」——症状指向 UI，真正的问题在链路上。
    /// 取值范围与 SDK 客户端完全一致，统一由 <see cref="OpenAiClientOptionsFactory.NormalizeTimeoutSeconds"/> 夹取，
    /// 所以这里不重复写边界。
    /// </summary>
    private int ResolveTimeoutSeconds()
        => OpenAiClientOptionsFactory.NormalizeTimeoutSeconds(_configService.Load().Timeout);

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

        var timeoutSeconds = ResolveTimeoutSeconds();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        // 这两个计数只在异常分支里有用，所以它们必须比 try 的作用域活得久；
        // 包装流本身不行——它要随响应体一起释放，所以字节数记在外面的盒子里。
        long? declaredLength = null;
        var bytesRead = new StrongBox<long>();

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());

            using var response = await _httpClient.SendAsync(request, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return ModelCatalogResult.Fail($"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}");
            }

            declaredLength = response.Content.Headers.ContentLength;
            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            await using var counted = new ByteCountingStream(stream, bytesRead);
            using var doc = await JsonDocument.ParseAsync(counted, cancellationToken: cts.Token).ConfigureAwait(false);

            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
            {
                // 形状不认识；交给 SDK 路径去试，它对少数非标准端点的兼容更宽。
                // 这一支必须留声：回退之后这个端点的自报元数据整轮缺失，而那是没人会主动去看的地方。
                _logger.Warning(
                    "Model list response parsed but carried no data array (root={RootKind}, {BytesRead} bytes read of {DeclaredBytes} declared, endpoint={Endpoint}); "
                        + "the body arrived complete, so this is a shape the parser does not recognize — falling back to the SDK path without provider-reported metadata",
                    doc.RootElement.ValueKind,
                    bytesRead.Value,
                    FormatDeclaredLength(declaredLength),
                    baseUrl ?? "default");
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
            return ModelCatalogResult.Fail($"Request timed out after {timeoutSeconds}s");
        }
        catch (JsonException ex)
        {
            // Warning 而不是 Debug：这一支同时吃下「响应被截断」和「端点形状不认识」两种情况，
            // 之后一律降级到 SDK 回退路径，UI 上只剩下回退那一轮的笼统失败——真实原因到此为止。
            // 实测链路中途断流时端点会返回截断的 JSON（完整 149 KB，只收到 3.8 KB / 7.6 KB / 30 KB / 31.5 KB），
            // 落的正是这一支；已读字节数与声明长度的差额是把两种情况分开的唯一线索。
            _logger.Warning(
                ex,
                "Model list response was not valid JSON after reading {BytesRead} bytes of {DeclaredBytes} declared (endpoint={Endpoint}); "
                    + "a short read against a larger Content-Length means the body was truncated mid-transfer, a complete read means the endpoint returned something that is not JSON — "
                    + "falling back to the SDK path without provider-reported metadata",
                bytesRead.Value,
                FormatDeclaredLength(declaredLength),
                baseUrl ?? "default");
            return null;
        }
        catch (HttpRequestException ex)
        {
            // 同理。这里的字节数几乎总是 0（SendAsync 阶段就失败，正文一个字节都没开始读），
            // 而「0 字节」本身就是与上面那一支的分界：失败发生在响应体之前，不是传输中途。
            _logger.Warning(
                ex,
                "Raw model list request failed at the transport layer after reading {BytesRead} bytes of {DeclaredBytes} declared (endpoint={Endpoint}); "
                    + "falling back to the SDK path without provider-reported metadata",
                bytesRead.Value,
                FormatDeclaredLength(declaredLength),
                baseUrl ?? "default");
            return null;
        }
    }

    /// <summary>把 Content-Length 渲染成日志里能读的样子；缺头时说「unknown」而不是「null」。</summary>
    private static string FormatDeclaredLength(long? declaredLength)
        => declaredLength?.ToString(CultureInfo.InvariantCulture) ?? "unknown";

    /// <summary>
    /// SDK 回退路径。只产出模型 ID：SDK 的模型对象不暴露协议之外的字段，
    /// 因此结果里的 <c>Reported</c> 保持 null，上层据此保留已有的自报元数据。
    /// </summary>
    private async Task<ModelCatalogResult> GetModelsViaSdkAsync(string? baseUrl, string apiKey, CancellationToken cancellationToken)
    {
        var timeoutSeconds = ResolveTimeoutSeconds();

        OpenAIModelClient client;
        try
        {
            var options = OpenAiClientOptionsFactory.Create(baseUrl, timeoutSeconds);

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
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

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
            return ModelCatalogResult.Fail($"Request timed out after {timeoutSeconds}s");
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

    /// <summary>
    /// 只做一件事：把实际从响应体读到的字节数累加进外部的计数盒。
    /// 断流时 JSON 解析器只会说「文档不完整」，而「读到 3.8 KB、声明 149 KB」才是能把
    /// 「响应被截断」和「端点返回了别的东西」分开的那个数字——两者都落在同一个 JsonException 分支里。
    /// 计数写在盒子里而不是这个类的属性上，是因为它要在流释放之后、于 catch 分支里被读到。
    /// 本身不接管内层流的生命周期：内层流由调用处自己的 <c>await using</c> 释放。
    /// </summary>
    private sealed class ByteCountingStream(Stream inner, StrongBox<long> counter) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => counter.Value;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            counter.Value += read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            counter.Value += read;
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
