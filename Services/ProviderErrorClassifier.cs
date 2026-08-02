using Athena.UI.Services.Interfaces;
using System;
using System.ClientModel;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Athena.UI.Services;

public sealed partial class ProviderErrorClassifier : IProviderErrorClassifier
{
    public ProviderErrorClassification Classify(Exception exception)
    {
        int? status = exception is ClientResultException client ? client.Status : null;
        var safeMessage = Sanitize(exception.Message);
        var normalized = safeMessage.ToLowerInvariant();
        var code = ExtractCode(normalized);

        var category = status is 401 or 403
                       || ContainsAny(normalized, "unauthorized", "authentication", "invalid api key", "permission denied")
            ? ProviderErrorCategory.Authentication
            : status == 429 || ContainsAny(normalized, "rate_limit", "rate limit", "too many requests")
                ? ProviderErrorCategory.RateLimit
                : exception is TimeoutException or TaskCanceledException or HttpRequestException
                  || status == 408
                  || ContainsAny(normalized, "timed out", "timeout", "connection refused", "network error", "dns")
                    ? ProviderErrorCategory.TimeoutOrNetwork
                    // Context overflow must win over image heuristics for image-bearing HTTP 400 requests.
                    : ContainsAny(normalized,
                        "context_length_exceeded",
                        "maximum context length",
                        "max context length",
                        "context window",
                        "too many tokens",
                        "prompt is too long",
                        "prompt too long",
                        "input tokens exceed",
                        "input token count exceeds")
                        ? ProviderErrorCategory.ContextOverflow
                        : ContainsAny(normalized,
                            "unsupported modality",
                            "image input is not supported",
                            "does not support image",
                            "not support image",
                            "vision is not supported",
                            "invalid content type",
                            "text-only")
                            ? ProviderErrorCategory.UnsupportedModality
                            : status is 400 or 404 or 409 or 415 or 422
                                ? ProviderErrorCategory.InvalidRequest
                                : ProviderErrorCategory.ProviderRawError;
        return new ProviderErrorClassification(category, safeMessage, status, code);
    }

    private static bool ContainsAny(string value, params string[] candidates)
        => Array.Exists(candidates, value.Contains);

    private static string? ExtractCode(string message)
    {
        var match = ErrorCodeRegex().Match(message);
        return match.Success ? match.Groups[1].Value : null;
    }

    internal static string Sanitize(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "Provider request failed.";
        var value = AuthorizationRegex().Replace(message, "$1[redacted]");
        value = ApiKeyRegex().Replace(value, "$1[redacted]");
        return value.Length <= 4096 ? value : value[..4096] + "…";
    }

    [GeneratedRegex("""(?i)(authorization\s*[:=]\s*(?:bearer\s+)?)[^\s,;\]}]+""")]
    private static partial Regex AuthorizationRegex();

    [GeneratedRegex("""(?i)((?:api[-_ ]?key|x-api-key)\s*[:=]\s*)[^\s,;\]}]+""")]
    private static partial Regex ApiKeyRegex();

    [GeneratedRegex("""(?i)(?:error[_ ]?code|code)\s*[:=]\s*["']?([a-z0-9_.-]+)""")]
    private static partial Regex ErrorCodeRegex();
}
