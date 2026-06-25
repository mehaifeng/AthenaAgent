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
    private AppConfig _config;
    private OpenAIClient? _client;
    private EmbeddingClient? _embeddingClient;

    public bool IsConfigured => _embeddingClient != null;

    public string? ModelId => _embeddingClient != null && !string.IsNullOrWhiteSpace(_config.EmbeddingModel)
        ? _config.EmbeddingModel
        : null;

    public OpenAIEmbeddingService(AppConfig config, ILogger logger)
    {
        _config = config;
        _logger = logger.ForContext<OpenAIEmbeddingService>();
        InitializeClient();
    }

    /// <summary>
    /// 更新配置并重新初始化客户端
    /// </summary>
    public void UpdateConfig(AppConfig config)
    {
        _config = config;
        InitializeClient();
    }

    private void InitializeClient()
    {
        // 使用 Embedding 专用配置，如果为空则回退到主配置
        var provider = string.IsNullOrWhiteSpace(_config.EmbeddingProvider) || _config.EmbeddingProvider == "Inherit"
            ? _config.Provider
            : _config.EmbeddingProvider;

        var apiKey = string.IsNullOrWhiteSpace(_config.EmbeddingApiKey)
            ? _config.ApiKey
            : _config.EmbeddingApiKey;

        var baseUrl = string.IsNullOrWhiteSpace(_config.EmbeddingBaseUrl)
            ? _config.BaseUrl
            : _config.EmbeddingBaseUrl;

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _client = null;
            _embeddingClient = null;
            _logger.Warning("Embedding API Key 为空，服务未初始化");
            return;
        }

        try
        {
            var options = new OpenAIClientOptions();
            if (!string.IsNullOrWhiteSpace(baseUrl))
            {
                options.Endpoint = new Uri(baseUrl);
                _logger.Information("Embedding 使用自定义 Base URL: {BaseUrl}", baseUrl);
            }

            _client = new OpenAIClient(new ApiKeyCredential(apiKey), options);
            
            if (!string.IsNullOrWhiteSpace(_config.EmbeddingModel))
            {
                _embeddingClient = _client.GetEmbeddingClient(_config.EmbeddingModel);
                _logger.Information("Embedding 客户端初始化成功，提供商: {Provider}, 模型: {Model}", provider, _config.EmbeddingModel);
            }
            else
            {
                _embeddingClient = null;
                _logger.Warning("Embedding 模型未配置");
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Embedding 客户端初始化失败");
            _client = null;
            _embeddingClient = null;
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
                _logger.Debug("生成 Embedding 成功，维度: {Dimension}", embedding.Length);
                return embedding;
            }

            _logger.Warning("生成 Embedding 返回结果为空");
            return null;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "生成 Embedding 失败 (文本长度: {Length})", text.Length);
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

            _logger.Debug("批量生成 Embedding 成功，数量: {Count}", results.Count);
            return results;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "批量生成 Embedding 失败");
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
            _logger.Warning("Embedding 维度不匹配: Query({QLen}) vs Doc({DLen})。建议刷新知识库缓存。", a.Length, b.Length);
            return 0f;
        }

        try
        {
            var similarity = TensorPrimitives.CosineSimilarity(a.AsSpan(), b.AsSpan());
            
            if (float.IsNaN(similarity))
            {
                _logger.Warning("CosineSimilarity 返回了 NaN (向量 A 为空或 B 为空)");
                return 0f;
            }
            
            return similarity;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "计算余弦相似度失败");
            return 0f;
        }
    }

    public async Task<(bool Success, string Message)> TestConnectionAsync()
    {
        if (_embeddingClient == null)
        {
            return (false, "请先配置 API Key 和模型");
        }

        try
        {
            var result = await GenerateEmbeddingAsync("test");
            if (result != null && result.Length > 0)
            {
                return (true, "连接成功");
            }
            return (false, "生成向量失败");
        }
        catch (Exception ex)
        {
            return (false, $"连接失败: {ex.Message}");
        }
    }
}
