using Athena.UI.Models;
using System;

namespace Athena.UI.Services.Protocol;

/// <summary>
/// 请求传输协议的保守判定。规则：
/// - 显式配置（ChatCompletions / Responses）直接生效；
/// - Auto 仅在「能确认支持」时切换到 Responses：
///   1) 官方 OpenAI 端点 + 推理模型（元数据确认 SupportsReasoning）；
///   2) 模型元数据确认 SupportsResponses（如 OpenRouter 目录 supported_parameters 含 "responses"）；
/// - 未知/手动 provider、非推理模型、无元数据一律走 Chat Completions（最稳）。
/// 不做网络探测；端点不支持由调用方在请求期按错误分类自动降级。
/// </summary>
public static class ResponsesProtocolResolver
{
    public static ProviderProtocol Resolve(
        ProviderProtocol configured,
        string? providerPreset,
        string? baseUrl,
        ResolvedModelMetadata? metadata)
    {
        if (configured != ProviderProtocol.Auto)
        {
            return configured;
        }

        if (IsOfficialOpenAi(providerPreset, baseUrl)
            && metadata?.SupportsReasoning.Value == CapabilitySupport.Supported)
        {
            return ProviderProtocol.Responses;
        }

        if (metadata?.SupportsResponses is { } responses
            && responses.Value == CapabilitySupport.Supported)
        {
            return ProviderProtocol.Responses;
        }

        return ProviderProtocol.ChatCompletions;
    }

    /// <summary>官方 OpenAI 端点：预设为 OpenAI，或 BaseUrl 主机匹配 api.openai.com。</summary>
    public static bool IsOfficialOpenAi(string? providerPreset, string? baseUrl)
    {
        if (string.Equals(providerPreset, "OpenAI", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Host is { } host
            && (host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase)
                || host.EndsWith(".api.openai.com", StringComparison.OrdinalIgnoreCase));
    }
}
