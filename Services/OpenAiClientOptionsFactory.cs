using System;
using System.ClientModel.Primitives;
using OpenAI;

namespace Athena.UI.Services;

/// <summary>
/// Creates OpenAI SDK client options with one application-wide retry and timeout policy.
/// Retry stays inside the SDK HTTP pipeline; callers must not add another business-level retry loop.
/// </summary>
public static class OpenAiClientOptionsFactory
{
    public const int DefaultMaxRetries = 3;
    public const int DefaultTimeoutSeconds = 60;
    public const int MinTimeoutSeconds = 10;
    public const int MaxTimeoutSeconds = 600;

    public static OpenAIClientOptions Create(string? baseUrl, int timeoutSeconds = DefaultTimeoutSeconds)
    {
        var options = new OpenAIClientOptions
        {
            RetryPolicy = new ClientRetryPolicy(DefaultMaxRetries),
            NetworkTimeout = TimeSpan.FromSeconds(NormalizeTimeoutSeconds(timeoutSeconds))
        };

        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            options.Endpoint = new Uri(baseUrl.Trim());
        }

        return options;
    }

    public static int NormalizeTimeoutSeconds(int timeoutSeconds)
    {
        if (timeoutSeconds <= 0)
        {
            return DefaultTimeoutSeconds;
        }

        return Math.Clamp(timeoutSeconds, MinTimeoutSeconds, MaxTimeoutSeconds);
    }
}
