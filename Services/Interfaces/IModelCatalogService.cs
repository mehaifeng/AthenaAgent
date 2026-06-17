using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.Interfaces;

/// <summary>
/// 拉取某个 OpenAI 协议端点 (/v1/models) 上的可用模型列表的结果。
/// </summary>
public sealed record ModelCatalogResult(bool Success, IReadOnlyList<string> Models, string? ErrorMessage)
{
    public static ModelCatalogResult Ok(IReadOnlyList<string> models) => new(true, models, null);

    public static ModelCatalogResult Fail(string error) => new(false, Array.Empty<string>(), error);
}

/// <summary>
/// 通过 OpenAI 协议的 <c>GET /v1/models</c> 接口查询某个端点的可用模型列表。
/// 该服务是无状态的：每次调用都使用传入的 BaseUrl / ApiKey 临时构造客户端。
/// </summary>
public interface IModelCatalogService
{
    /// <summary>
    /// 查询指定端点的可用模型 Id 列表（已去重、按字母排序）。
    /// </summary>
    /// <param name="baseUrl">端点地址；为空时回退到 OpenAI 官方端点。</param>
    /// <param name="apiKey">用于鉴权的 API Key；为空时直接返回失败。</param>
    Task<ModelCatalogResult> GetModelsAsync(string? baseUrl, string? apiKey, CancellationToken cancellationToken = default);
}
