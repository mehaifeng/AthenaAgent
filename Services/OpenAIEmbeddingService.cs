using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using OpenAI;
using OpenAI.Embeddings;
using Serilog;
using System;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics.Tensors;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;

namespace Athena.UI.Services;

/// <summary>
/// OpenAI Embedding 服务实现
/// 使用 OpenAI API 生成文本向量
/// </summary>
public class OpenAIEmbeddingService : IEmbeddingService
{
    private readonly ILogger _logger;
    private readonly ILocalizationService? _localizationService;
    private AppConfig _config;
    private OpenAIClient? _client;
    private EmbeddingClient? _embeddingClient;
    private string? _effectiveModelId;
    private OpenAiModelClientIdentity _clientIdentity;
    private readonly PipelineTransport? _transportOverride;

    public bool IsConfigured => _embeddingClient != null;

    public string? ModelId => _embeddingClient != null ? _effectiveModelId : null;

    public OpenAIEmbeddingService(AppConfig config, ILogger logger, ILocalizationService? localizationService = null)
        : this(config, logger, localizationService, transportOverride: null)
    {
    }

    /// <summary>测试入口：把 HTTP 传输换成脚本化的处理器，其余管线（重试、超时、端点）与生产一致。</summary>
    internal OpenAIEmbeddingService(AppConfig config, ILogger logger, ILocalizationService? localizationService, PipelineTransport? transportOverride)
    {
        _transportOverride = transportOverride;
        _config = config;
        _clientIdentity = OpenAiModelRuntimeFactory.ComputeClientIdentity(
            config,
            AiModelRole.Embedding);
        _logger = logger.ForContext<OpenAIEmbeddingService>();
        _localizationService = localizationService;
        InitializeClient();
    }

    private string GetLocalized(string key, string defaultValue)
        => _localizationService?.GetString(key, defaultValue) ?? defaultValue;

    /// <summary>
    /// 更新配置；仅当客户端连接指纹变化时重新初始化。
    /// </summary>
    public void UpdateConfig(AppConfig config)
    {
        var nextClientIdentity = OpenAiModelRuntimeFactory.ComputeClientIdentity(
            config,
            AiModelRole.Embedding);
        _config = config;
        if (_clientIdentity == nextClientIdentity)
            return;

        _clientIdentity = nextClientIdentity;
        InitializeClient();
    }

    private void InitializeClient()
    {
        _client = null;
        _embeddingClient = null;
        _effectiveModelId = null;

        EffectiveOpenAiModel effective;
        try
        {
            effective = OpenAiModelRuntimeFactory.Resolve(_config, AiModelRole.Embedding);
        }
        catch (InvalidOperationException ex)
        {
            _logger.Warning("Embedding not fully configured; service remains disabled: {Reason}", ex.Message);
            return;
        }

        var provider = effective.ProviderDisplayName;
        var apiKey = effective.ApiKey;
        var baseUrl = effective.BaseUrl;

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.Warning("Embedding API key is empty; service not initialized");
            return;
        }

        try
        {
            var options = OpenAiClientOptionsFactory.Create(baseUrl, _config.Timeout);
            if (_transportOverride != null)
            {
                options.Transport = _transportOverride;
            }
            if (!string.IsNullOrWhiteSpace(baseUrl))
            {
                _logger.Information("Embedding using custom Base URL: {BaseUrl}", baseUrl);
            }

            _client = new OpenAIClient(new ApiKeyCredential(apiKey), options);

            if (!string.IsNullOrWhiteSpace(effective.Model))
            {
                _embeddingClient = _client.GetEmbeddingClient(effective.Model);
                _effectiveModelId = effective.Model;
                _logger.Information("Embedding client initialized successfully, provider: {Provider}, model: {Model}", provider, effective.Model);
            }
            else
            {
                _embeddingClient = null;
                _effectiveModelId = null;
                _logger.Warning("Embedding model not configured");
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Embedding client initialization failed");
            _client = null;
            _embeddingClient = null;
            _effectiveModelId = null;
        }
    }

    public async Task<float[]?> GenerateEmbeddingAsync(string text)
    {
        if (_embeddingClient == null || string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            var vectors = await RequestEmbeddingsAsync(new[] { text });

            if (vectors.Count > 0)
            {
                var embedding = NormalizeL2(vectors[0]);
                _logger.Debug("Embedding generated successfully, dimension: {Dimension}", embedding.Length);
                return embedding;
            }

            _logger.Warning("Embedding generation returned an empty result");
            return null;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to generate embedding (text length: {Length})", text.Length);
            return null;
        }
    }

    public async Task<List<float[]?>> GenerateEmbeddingsAsync(IEnumerable<string> texts)
    {
        var results = new List<float[]?>();
        var textList = texts.ToList();

        if (_embeddingClient == null || textList.Count == 0)
        {
            return results;
        }

        try
        {
            foreach (var embedding in await RequestEmbeddingsAsync(textList))
            {
                results.Add(NormalizeL2(embedding));
            }

            _logger.Debug("Batch embedding generation succeeded, count: {Count}", results.Count);
            return results;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Batch embedding generation failed");
            return new List<float[]?>();
        }
    }

    /// <summary>
    /// 走 SDK 的协议层方法，请求体由 <see cref="EmbeddingWireFormat"/> 自己写。
    /// SDK 的便捷方法把 <c>encoding_format</c> 固定为 <c>base64</c>（没有公开开关），而有的端点只接受 <c>float</c>
    /// （2026-10-10，<c>infra-text-embedding-4b</c>：400 "encoding_format only supports float"），
    /// 于是探测失败、整轮向量被跳过、重建永远停在 0 个向量。<c>float</c> 是 OpenAI 协议的缺省值，所有兼容端点都认。
    /// </summary>
    private async Task<IReadOnlyList<float[]>> RequestEmbeddingsAsync(IReadOnlyList<string> texts)
    {
        var client = _embeddingClient ?? throw new InvalidOperationException("Embedding client is not configured.");
        var model = _effectiveModelId ?? throw new InvalidOperationException("Embedding model is not configured.");
        using var content = BinaryContent.Create(EmbeddingWireFormat.BuildRequest(model, texts));
        var result = await client.GenerateEmbeddingsAsync(content, new RequestOptions());
        return EmbeddingWireFormat.ParseResponse(result.GetRawResponse().Content, texts.Count);
    }

    /// <summary>
    /// L2 归一化为单位向量。归一化后余弦相似度等价于点积，便于快速检索。
    /// 零向量原样返回。
    /// </summary>
    private static float[] NormalizeL2(float[] v)
    {
        var norm = MathF.Sqrt(TensorPrimitives.Dot(v.AsSpan(), v.AsSpan()));
        if (norm <= 1e-8f) return v;

        var inv = 1f / norm;
        for (int i = 0; i < v.Length; i++) v[i] *= inv;
        return v;
    }

    public float CosineSimilarity(float[] a, float[] b)
    {
        if (a == null || b == null)
        {
            return 0f;
        }

        if (a.Length != b.Length)
        {
            _logger.Warning("Embedding dimension mismatch: Query({QLen}) vs Doc({DLen}). Consider refreshing the knowledge base cache.", a.Length, b.Length);
            return 0f;
        }

        try
        {
            var similarity = TensorPrimitives.CosineSimilarity(a.AsSpan(), b.AsSpan());

            if (float.IsNaN(similarity))
            {
                _logger.Warning("CosineSimilarity returned NaN (vector A or vector B is empty)");
                return 0f;
            }

            return similarity;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to compute cosine similarity");
            return 0f;
        }
    }

    public async Task<(bool Success, string Message)> TestConnectionAsync()
    {
        if (_embeddingClient == null)
        {
            return (false, GetLocalized("Embedding.NotConfigured", "Please configure the API Key and embedding model first"));
        }

        try
        {
            var result = await GenerateEmbeddingAsync("test");
            if (result != null && result.Length > 0)
            {
                return (true, GetLocalized("Embedding.TestSuccess", "Connection succeeded"));
            }
            return (false, GetLocalized("Embedding.EmbedFailed", "Failed to generate embedding vector"));
        }
        catch (Exception ex)
        {
            return (false, string.Format(GetLocalized("Service.ConnectionFailed", "Connection failed: {0}"), ex.Message));
        }
    }
}

/// <summary>嵌入请求/响应的线上格式。请求固定 <c>encoding_format: "float"</c>；响应两种编码都读，因为有的端点不理会这个参数。</summary>
internal static class EmbeddingWireFormat
{
    public static BinaryData BuildRequest(string model, IReadOnlyList<string> texts)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("model", model);
            writer.WriteStartArray("input");
            foreach (var text in texts)
            {
                writer.WriteStringValue(text);
            }
            writer.WriteEndArray();
            writer.WriteString("encoding_format", "float");
            writer.WriteEndObject();
        }
        return BinaryData.FromBytes(stream.ToArray());
    }

    /// <summary>按 <c>index</c> 排回输入顺序；条数或形状不对就抛，不让半截结果冒充完整索引。</summary>
    public static IReadOnlyList<float[]> ParseResponse(BinaryData body, int expectedCount)
    {
        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Embedding response has no data array.");
        }

        var vectors = new float[expectedCount][];
        var position = 0;
        foreach (var item in data.EnumerateArray())
        {
            var index = item.TryGetProperty("index", out var indexElement) && indexElement.ValueKind == JsonValueKind.Number
                ? indexElement.GetInt32()
                : position;
            if (index < 0 || index >= expectedCount || vectors[index] != null)
            {
                throw new InvalidDataException($"Embedding response index {index} is out of range or repeated (expected {expectedCount}).");
            }
            if (!item.TryGetProperty("embedding", out var embedding))
            {
                throw new InvalidDataException($"Embedding response item {index} has no embedding.");
            }
            vectors[index] = ReadVector(embedding, index);
            position++;
        }

        if (position != expectedCount)
        {
            throw new InvalidDataException($"Embedding response returned {position} vector(s) for {expectedCount} input(s).");
        }
        return vectors;
    }

    private static float[] ReadVector(JsonElement embedding, int index)
    {
        switch (embedding.ValueKind)
        {
            case JsonValueKind.Array:
                var vector = new float[embedding.GetArrayLength()];
                var i = 0;
                foreach (var value in embedding.EnumerateArray())
                {
                    vector[i++] = value.GetSingle();
                }
                return vector;
            case JsonValueKind.String:
                var bytes = Convert.FromBase64String(embedding.GetString()!);
                if (bytes.Length % sizeof(float) != 0)
                {
                    throw new InvalidDataException($"Embedding response item {index} has a base64 payload that is not a whole number of floats.");
                }
                return MemoryMarshal.Cast<byte, float>(bytes).ToArray();
            default:
                throw new InvalidDataException($"Embedding response item {index} has an embedding of kind {embedding.ValueKind}.");
        }
    }
}
