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

    /// <summary>
    /// 按本次调用实际要生成的输出量放宽网络超时。一个扁平的 60 秒被审批（256 token 输出）
    /// 和上下文压缩（12,000 token 输出）共用，对后者等于必然踩线——实测一轮压缩里 7 次
    /// 调用有 2 次超时，每次要赔上 60+60+重试 共约 175 秒，而成功的那几次最慢已到 57 秒。
    /// 这里按保守的 50 token/秒折算生成耗时，并保留一个与配置值等长的建连/首字节余量。
    /// </summary>
    public static int ResolveTimeoutSeconds(int configuredTimeoutSeconds, int maxOutputTokens)
    {
        var configured = NormalizeTimeoutSeconds(configuredTimeoutSeconds);
        if (maxOutputTokens <= 0) return configured;
        return Math.Clamp(configured + maxOutputTokens / 50, configured, MaxTimeoutSeconds);
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
