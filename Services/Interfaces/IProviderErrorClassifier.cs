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
