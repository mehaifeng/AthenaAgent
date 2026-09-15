using Athena.UI.Models;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.Interfaces;

/// <summary>
/// 拉取某个 OpenAI 协议端点 (/v1/models) 上的可用模型列表的结果。
/// </summary>
/// <param name="Reported">
/// 供应商在同一份响应里自报的能力元数据，按模型 ID 索引。
/// <c>null</c> 与空字典含义不同，上层依赖这个区分：<c>null</c> 表示这次压根没解析到
/// 原始 JSON（走了 SDK 回退路径），已有的自报数据应当原样保留；空字典表示解析成功
/// 但端点什么都没报，此时应当清掉过期的旧值。
/// </param>
public sealed record ModelCatalogResult(
    bool Success,
    IReadOnlyList<string> Models,
    string? ErrorMessage,
    IReadOnlyDictionary<string, ProviderReportedModelMetadata>? Reported = null)
{
    public static ModelCatalogResult Ok(IReadOnlyList<string> models) => new(true, models, null);

    public static ModelCatalogResult Ok(
        IReadOnlyList<string> models,
        IReadOnlyDictionary<string, ProviderReportedModelMetadata>? reported) => new(true, models, null, reported);

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

    /// <summary>
    /// 查询可用于文本回复的模型。OpenRouter 使用 <c>output_modalities=text</c> 精确过滤；
    /// 其它 OpenAI 兼容端点回退到普通 <c>/models</c> 列表。
    /// </summary>
    Task<ModelCatalogResult> GetTextModelsAsync(string? baseUrl, string? apiKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// 仅查询嵌入类模型。识别到 OpenRouter 端点时，走其私有的
    /// <c>?output_modalities=embeddings</c> 查询参数由服务端精确过滤；
    /// 其它端点不支持该参数，回退为全量拉取（由上层按 ID 关键字过滤）。
    /// </summary>
    Task<ModelCatalogResult> GetEmbeddingModelsAsync(string? baseUrl, string? apiKey, CancellationToken cancellationToken = default);
}
