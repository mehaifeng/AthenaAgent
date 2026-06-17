using Athena.UI.Services.Interfaces;
using OpenAI;
using OpenAI.Models;
using Serilog;
using System;
using System.ClientModel;
using System.Collections.Generic;
using System.Linq;
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
    private readonly ILogger _logger = Log.ForContext<ModelCatalogService>();

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
}
