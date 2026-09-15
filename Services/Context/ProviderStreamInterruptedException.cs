using System;

namespace Athena.UI.Services.Context;

/// <summary>
/// 供应商在 HTTP 200 之后、流式响应中途宣告失败（OpenRouter 等网关会发一个
/// <c>finish_reason: "error"</c> 的 chunk，并在同一 chunk 里带上真正的上游错误）。
/// SDK 的重试策略管不到这里——它只重试建连与状态码，而这时响应体已经开始流了。
/// </summary>
public sealed class ProviderStreamInterruptedException : Exception
{
    public ProviderStreamInterruptedException(
        string message,
        string? providerFinishReason = null,
        string? providerErrorCode = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ProviderFinishReason = providerFinishReason;
        ProviderErrorCode = providerErrorCode;
    }

    /// <summary>供应商原样回报的 finish_reason（如 <c>error</c>），仅用于日志与文案。</summary>
    public string? ProviderFinishReason { get; }

    public string? ProviderErrorCode { get; }
}

/// <summary>
/// 自动重试用尽之后仍然失败。包住最后一次的真实异常，只是为了把「已经重试过几次」这件事
/// 带到能说人话的那一层——判定逻辑一律看 <see cref="Exception.InnerException"/>，不看这一层。
/// </summary>
public sealed class ProviderRetriesExhaustedException(int attempts, Exception innerException)
    : Exception(innerException.Message, innerException)
{
    public int Attempts { get; } = attempts;
}
