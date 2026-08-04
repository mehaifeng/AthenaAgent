using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using OpenAI;
using OpenAI.Embeddings;
using Serilog;
using System;
using System.ClientModel;
using System.Collections.Generic;
using System.Linq;
using System.Numerics.Tensors;
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

    public bool IsConfigured => _embeddingClient != null;

    public string? ModelId => _embeddingClient != null ? _effectiveModelId : null;

    public OpenAIEmbeddingService(AppConfig config, ILogger logger, ILocalizationService? localizationService = null)
    {
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
            // 使用复数形式的 GenerateEmbeddingsAsync，这在某些 SDK 版本中更稳定
            // 且能更好地处理响应解析
            ClientResult<OpenAIEmbeddingCollection> result = await _embeddingClient.GenerateEmbeddingsAsync(new[] { text });

            if (result?.Value != null && result.Value.Count > 0)
            {
                var embedding = NormalizeL2(result.Value[0].ToFloats().ToArray());
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
            ClientResult<OpenAIEmbeddingCollection> response = await _embeddingClient.GenerateEmbeddingsAsync(textList);

            foreach (var embedding in response.Value)
            {
                results.Add(NormalizeL2(embedding.ToFloats().ToArray()));
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
