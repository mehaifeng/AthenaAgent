using Athena.UI.Services.Interfaces;
using Serilog;
using System;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.OrcaRouter;

/// <summary>
/// OrcaRouter 的 PKCE 接入编排：绑端口 → 开浏览器 → 等回调 → 用 code + verifier 换 API Key。
///
/// 这个服务只负责"拿到一把 key"。写不写进配置、写到哪个连接上、要不要顺手指派主对话角色，
/// 都是调用方的事——它自己不认识 AppConfig，也就不可能在用户取消时留下半份配置。
///
/// 三条不变量：
/// - 归因码只出现在授权 URL 的 query 里，绝不进入任何 API 请求的 header 或 body。
/// - 日志只留 host、阶段与结果；code / verifier / key 一律不落盘（<see cref="Redact"/> 兜底）。
/// - 响应里找不到 key 就是失败。空 key 写进配置，用户看到的是"已接入"而实际每次请求都 401。
/// </summary>
public sealed class OrcaRouterConnectService : IOrcaRouterConnectService, IDisposable
{
    /// <summary>用户在浏览器里完成授权的总预算。超过就收摊，免得一个监听端口挂到进程结束。</summary>
    public static readonly TimeSpan DefaultAuthorizationTimeout = TimeSpan.FromMinutes(5);

    /// <summary>响应里 API Key 可能的字段名。文档没写明返回体形状，所以按候选逐个试。</summary>
    private static readonly string[] KeyPropertyNames = ["key", "api_key", "apiKey", "secret_key", "token"];

    /// <summary>可能包住 key 的一层信封。</summary>
    private static readonly string[] EnvelopePropertyNames = ["data", "result", "key"];

    /// <summary>凭据形状的脱敏兜底：任何 sk- 开头的串都不该出现在日志或错误文案里。</summary>
    private static readonly Regex CredentialPattern = new(
        @"sk-[A-Za-z0-9_\-]{6,}",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private readonly HttpClient _httpClient;
    private readonly IExternalUrlOpener _urlOpener;
    private readonly ILogger _logger;
    private readonly TimeSpan _authorizationTimeout;

    /// <summary>单飞闸门。连点两次会开出两个浏览器页，两个 state 互相污染。</summary>
    private readonly SemaphoreSlim _singleFlight = new(1, 1);

    private bool _disposed;

    /// <param name="endpoints">
    /// 随包的端点配置，由组合根通过 <see cref="OrcaRouterEndpoints.Load"/> 提供。
    /// 允许为 null（资源缺失或被改坏），此时整个接入能力对外报告不可用——调用方据此禁用入口。
    /// </param>
    public OrcaRouterConnectService(
        HttpClient httpClient,
        IExternalUrlOpener urlOpener,
        ILogger logger,
        OrcaRouterEndpoints? endpoints,
        TimeSpan? authorizationTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(urlOpener);
        ArgumentNullException.ThrowIfNull(logger);
        _httpClient = httpClient;
        _urlOpener = urlOpener;
        _logger = logger;
        Endpoints = endpoints;
        _authorizationTimeout = authorizationTimeout ?? DefaultAuthorizationTimeout;

        if (endpoints == null)
        {
            // 不可用要留下痕迹。静默不可用会以"按钮点了没反应"的形式在几个月后被报成 UI bug。
            _logger.Warning("OrcaRouter connect is unavailable: no usable endpoint configuration was supplied");
        }
    }

    public bool IsAvailable => Endpoints != null;

    public OrcaRouterEndpoints? Endpoints { get; }

    public async Task<OrcaRouterConnectResult> ConnectAsync(
        IProgress<OrcaRouterConnectProgress>? progress,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (Endpoints is not { } endpoints)
        {
            return Fail(OrcaRouterConnectFailure.Unavailable, "OrcaRouter endpoint configuration is unavailable.");
        }

        if (!await _singleFlight.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return Fail(OrcaRouterConnectFailure.AlreadyRunning, "An OrcaRouter authorization is already in progress.");
        }

        try
        {
            // 端口先绑定、再打开浏览器：顺序反了，用户可能在监听就绪前就被重定向回来。
            using var listener = new LoopbackCallbackListener(endpoints.CallbackPath, _logger);
            var codes = PkceCodes.Create();
            var authorizationUrl = BuildAuthorizationUrl(endpoints, listener.CallbackUrl, codes);

            progress?.Report(new OrcaRouterConnectProgress(OrcaRouterConnectStage.Starting, authorizationUrl, BrowserOpened: false));

            // 打不开浏览器**不是**失败：监听器还活着，用户手动粘贴这个链接照样能走完。
            var browserOpened = _urlOpener.TryOpen(authorizationUrl);
            if (!browserOpened)
            {
                _logger.Warning("OrcaRouter: could not launch a browser; the authorization url must be opened manually");
            }

            progress?.Report(new OrcaRouterConnectProgress(
                OrcaRouterConnectStage.AwaitingAuthorization,
                authorizationUrl,
                browserOpened));

            LoopbackCallbackOutcome outcome;
            using (var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                budget.CancelAfter(_authorizationTimeout);
                try
                {
                    outcome = await listener.WaitAsync(codes.State, budget.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return cancellationToken.IsCancellationRequested
                        ? Fail(OrcaRouterConnectFailure.Canceled, "The authorization was canceled.")
                        : Fail(
                            OrcaRouterConnectFailure.TimedOut,
                            string.Create(
                                CultureInfo.InvariantCulture,
                                $"No authorization callback arrived within {_authorizationTimeout.TotalMinutes:0} minutes."));
                }
            }

            if (outcome.Kind != LoopbackCallbackKind.Success || string.IsNullOrWhiteSpace(outcome.Code))
            {
                _logger.Warning("OrcaRouter: authorization did not complete ({Kind})", outcome.Kind);
                return Fail(OrcaRouterConnectFailure.ProviderDenied, Redact(outcome.Error) ?? "Authorization was not granted.");
            }

            progress?.Report(new OrcaRouterConnectProgress(
                OrcaRouterConnectStage.ExchangingCode,
                authorizationUrl,
                browserOpened));

            return await ExchangeCodeAsync(endpoints, outcome.Code, codes.Verifier, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _singleFlight.Release();
        }
    }

    /// <summary>
    /// 拼授权 URL。归因码在这里、也只在这里出现。
    /// </summary>
    internal static string BuildAuthorizationUrl(OrcaRouterEndpoints endpoints, string callbackUrl, PkceCodes codes)
    {
        var query = new StringBuilder()
            .Append("callback_url=").Append(Uri.EscapeDataString(callbackUrl))
            .Append("&code_challenge=").Append(Uri.EscapeDataString(codes.Challenge))
            .Append("&code_challenge_method=").Append(PkceCodes.ChallengeMethod)
            .Append("&state=").Append(Uri.EscapeDataString(codes.State))
            .Append("&app_name=").Append(Uri.EscapeDataString(endpoints.AppName))
            .Append("&ref=").Append(Uri.EscapeDataString(endpoints.ReferralCode));

        var separator = string.IsNullOrEmpty(endpoints.AuthUrl.Query) ? '?' : '&';
        return string.Create(CultureInfo.InvariantCulture, $"{endpoints.AuthUrl}{separator}{query}");
    }

    private async Task<OrcaRouterConnectResult> ExchangeCodeAsync(
        OrcaRouterEndpoints endpoints,
        string code,
        string verifier,
        CancellationToken cancellationToken)
    {
        using var content = new StringContent(
            JsonSerializer.Serialize(new { code, code_verifier = verifier }),
            Encoding.UTF8,
            "application/json");

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.PostAsync(endpoints.TokenUrl, content, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Fail(OrcaRouterConnectFailure.Canceled, "The authorization was canceled.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.Error(ex, "OrcaRouter: the key exchange request to {Host} failed", endpoints.TokenUrl.Host);
            return Fail(OrcaRouterConnectFailure.ExchangeFailed, Redact(ex.Message) ?? "The key exchange request failed.");
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.Error(
                    "OrcaRouter: the key exchange returned {StatusCode} from {Host}",
                    (int)response.StatusCode,
                    endpoints.TokenUrl.Host);
                return Fail(
                    OrcaRouterConnectFailure.ExchangeFailed,
                    string.Create(CultureInfo.InvariantCulture, $"HTTP {(int)response.StatusCode}: {Snippet(body)}"));
            }

            var apiKey = ExtractApiKey(body);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                // 2xx 但没有 key。写进配置只会变成一个"已接入"却每次都 401 的连接。
                _logger.Error("OrcaRouter: the key exchange succeeded but no API key field was present in the response");
                return Fail(
                    OrcaRouterConnectFailure.MalformedResponse,
                    "The key exchange response did not contain an API key.");
            }

            _logger.Information("OrcaRouter: connected; an API key was issued by {Host}", endpoints.TokenUrl.Host);
            return new OrcaRouterConnectResult
            {
                Succeeded = true,
                ApiKey = apiKey,
                Endpoints = endpoints,
                Failure = OrcaRouterConnectFailure.None
            };
        }
    }

    /// <summary>
    /// 从响应里取 API Key。接入文档没写明返回体形状，所以按候选字段名找，并允许包一层信封。
    /// 找不到就返回 null——绝不"猜"出一个空字符串当成功。
    /// </summary>
    internal static string? ExtractApiKey(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;

        try
        {
            using var document = JsonDocument.Parse(body);
            return FindKey(document.RootElement, depth: 0);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? FindKey(JsonElement element, int depth)
    {
        if (element.ValueKind != JsonValueKind.Object || depth > 2) return null;

        foreach (var name in KeyPropertyNames)
        {
            if (element.TryGetProperty(name, out var candidate)
                && candidate.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(candidate.GetString()))
            {
                return candidate.GetString()!.Trim();
            }
        }

        foreach (var name in EnvelopePropertyNames)
        {
            if (element.TryGetProperty(name, out var envelope)
                && envelope.ValueKind == JsonValueKind.Object
                && FindKey(envelope, depth + 1) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    /// <summary>错误文案里带一小段响应体便于排查，但先截断再脱敏。</summary>
    private static string Snippet(string body)
    {
        var redacted = Redact(body) ?? string.Empty;
        redacted = redacted.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return redacted.Length <= 200 ? redacted : redacted[..200] + "…";
    }

    /// <summary>把凭据形状的串换成占位符。</summary>
    internal static string? Redact(string? text)
        => text == null ? null : CredentialPattern.Replace(text, "sk-***");

    private static OrcaRouterConnectResult Fail(OrcaRouterConnectFailure failure, string error)
        => new() { Succeeded = false, Failure = failure, Error = error };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _singleFlight.Dispose();
    }
}
