using System;

namespace Athena.UI.Services.Interfaces;

public enum ProviderErrorCategory
{
    Authentication,
    RateLimit,
    TimeoutOrNetwork,
    ContextOverflow,
    UnsupportedModality,
    InvalidRequest,
    // HTTP 200 之后流到一半被上游宣告失败（OpenRouter 的 finish_reason=error 等）。
    // 与 TimeoutOrNetwork 分开：请求本身是成立的，重发同一份消息通常就能过。
    StreamInterrupted,
    // 流式回复里有 SDK 读不懂的取值（MiniMax 的 tool_calls[].type:"" 曾经就是这个）。上游没出错，是格式不兼容：
    // 同一个端点重发多半还是这样，所以与 StreamInterrupted 分开，既不重试，也不说「继续即可」。
    StreamIncompatible,
    ProviderRawError
}

public sealed record ProviderErrorClassification(
    ProviderErrorCategory Category,
    string SafeProviderMessage,
    int? HttpStatus = null,
    string? ProviderErrorCode = null);

public interface IProviderErrorClassifier
{
    ProviderErrorClassification Classify(Exception exception);
}
