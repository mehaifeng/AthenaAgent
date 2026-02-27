using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using OpenAI;
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

    public bool IsConfigured => _client != null;

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
        var apiKey = string.IsNullOrWhiteSpace(_config.EmbeddingApiKey)
            ? _config.ApiKey
            : _config.EmbeddingApiKey;

        var baseUrl = string.IsNullOrWhiteSpace(_config.EmbeddingBaseUrl)
            ? _config.BaseUrl
            : _config.EmbeddingBaseUrl;

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _client = null;
            _logger.Warning("Embedding API Key 为空，服务未初始化");
            return;
        }

        try
        {
            var options = new OpenAIClientOptions();
            if (!string.IsNullOrWhiteSpace(baseUrl))
            {
                options.Endpoint = new Uri(baseUrl);
            }

            _client = new OpenAIClient(new ApiKeyCredential(apiKey), options);
            _logger.Information("Embedding 客户端初始化成功，模型: {Model}", _config.EmbeddingModel);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Embedding 客户端初始化失败");
            _client = null;
        }
    }

    public async Task<float[]?> GenerateEmbeddingAsync(string text)
    {
        if (_client == null || string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            var embeddingClient = _client.GetEmbeddingClient(_config.EmbeddingModel);
            var response = await embeddingClient.GenerateEmbeddingAsync(text);

            var embedding = response.Value.ToFloats().ToArray();
            _logger.Debug("生成 Embedding 成功，维度: {Dimension}", embedding.Length);
            return embedding;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "生成 Embedding 失败");
            return null;
        }
    }

    public async Task<List<float[]?>> GenerateEmbeddingsAsync(IEnumerable<string> texts)
    {
        var results = new List<float[]?>();
        var textList = texts.ToList();

        if (_client == null || textList.Count == 0)
        {
            return results;
        }

        try
        {
            var embeddingClient = _client.GetEmbeddingClient(_config.EmbeddingModel);
            var response = await embeddingClient.GenerateEmbeddingsAsync(textList);

            foreach (var embedding in response.Value)
            {
                results.Add(embedding.ToFloats().ToArray());
            }

            _logger.Debug("批量生成 Embedding 成功，数量: {Count}", results.Count);
            return results;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "批量生成 Embedding 失败");
            // 返回空列表表示失败
            return new List<float[]?>();
        }
    }

    public float CosineSimilarity(float[] a, float[] b)
    {
        if (a == null || b == null || a.Length != b.Length)
        {
            return 0f;
        }

        return TensorPrimitives.CosineSimilarity(a.AsSpan(), b.AsSpan());
    }
}
