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
