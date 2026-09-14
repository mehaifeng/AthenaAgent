using Serilog;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.OrcaRouter;

/// <summary>一次环回回调的判定结果。</summary>
public enum LoopbackCallbackKind
{
    /// <summary>不是我们的回调（浏览器顺手要的 /favicon.ico 之类）。回 404 并继续等。</summary>
    Ignored,

    /// <summary>路径对上了但 state 不符。不换 key，也**不结束流程**，继续等真正的回调。</summary>
    StateMismatch,

    /// <summary>state 相符的回调，但对方给的是错误、或根本没带 code。终止流程。</summary>
    ProviderError,

    /// <summary>state 相符且带回了授权码。</summary>
    Success
}

/// <summary>回调判定的结果与载荷。</summary>
public sealed record LoopbackCallbackOutcome(LoopbackCallbackKind Kind, string? Code = null, string? Error = null);

/// <summary>
/// PKCE 授权流程的环回接收端（RFC 8252）。
///
/// 用 <see cref="TcpListener"/> 而不是 <c>HttpListener</c>：后者在 Windows 上需要 URL ACL 保留，
/// 非管理员进程在不同机器上表现不一致；而这里只需要读一行 GET 请求再回一页 HTML，
/// 裸 TCP 在三个平台上都不需要任何额外配置。
///
/// 三条规则是会踩的坑：
/// 1. 端口先绑定、再打开浏览器——反过来存在窗口期。端口取 0 由内核分配，按 RFC 8252 §7.3 只有端口可变。
/// 2. 浏览器会顺手请求 /favicon.ico，只有路径与 state 双双对上的请求才算回调，其余回 404 后继续等。
/// 3. state 不符不等于流程失败：直接结束等于给了任何本机进程一个打断授权的开关，所以只记日志、继续等。
/// 每个连接各跑一个任务，一条迟迟不发数据的连接因此挡不住真正的回调（队头阻塞）。
/// </summary>
public sealed class LoopbackCallbackListener : IDisposable
{
    /// <summary>单条请求行的上限。回调 URL 再长也到不了这个量级，超过即视为垃圾流量。</summary>
    private const int MaxRequestLineBytes = 8 * 1024;

    /// <summary>单条连接的读取预算。防止一条连上来不说话的连接占住一个任务。</summary>
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);

    private readonly TcpListener _listener;
    private readonly string _callbackPath;
    private readonly ILogger _logger;
    private bool _disposed;

    public LoopbackCallbackListener(string callbackPath, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callbackPath);
        ArgumentNullException.ThrowIfNull(logger);
        _callbackPath = callbackPath;
        _logger = logger;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    /// <summary>内核分配的环回端口。</summary>
    public int Port { get; }

    /// <summary>要报给授权页的回调地址。</summary>
    public string CallbackUrl => string.Create(
        CultureInfo.InvariantCulture,
        $"http://127.0.0.1:{Port}{_callbackPath}");

    /// <summary>等待与 <paramref name="expectedState"/> 相符的回调。取消与超时由调用方通过 token 控制。</summary>
    public async Task<LoopbackCallbackOutcome> WaitAsync(string expectedState, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedState);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var completion = new TaskCompletionSource<LoopbackCallbackOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));

        var acceptLoop = AcceptLoopAsync(expectedState, completion, cancellationToken);
        try
        {
            return await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            // 接受循环靠 listener 关闭或 token 取消退出；这里只是别把它留成无人观察的异常。
            _ = acceptLoop.ContinueWith(
                static t => t.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task AcceptLoopAsync(
        string expectedState,
        TaskCompletionSource<LoopbackCallbackOutcome> completion,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && !completion.Task.IsCompleted)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException or InvalidOperationException)
            {
                // 监听器已被释放（流程结束或取消），正常收尾。
                return;
            }

            _ = HandleClientAsync(client, expectedState, completion, cancellationToken);
        }
    }

    private async Task HandleClientAsync(
        TcpClient client,
        string expectedState,
        TaskCompletionSource<LoopbackCallbackOutcome> completion,
        CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                using var readBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                readBudget.CancelAfter(ReadTimeout);

                var stream = client.GetStream();
                var requestLine = await ReadRequestLineAsync(stream, readBudget.Token).ConfigureAwait(false);
                if (requestLine == null) return;

                var target = ExtractRequestTarget(requestLine);
                var outcome = Classify(target, _callbackPath, expectedState);

                switch (outcome.Kind)
                {
                    case LoopbackCallbackKind.Ignored:
                        await RespondAsync(stream, 404, "Not Found", NotFoundPage, readBudget.Token).ConfigureAwait(false);
                        break;
                    case LoopbackCallbackKind.StateMismatch:
                        // 记下来但不结束流程——见类型注释的规则 3。
                        _logger.Warning("OrcaRouter: discarded a loopback callback whose state did not match the pending authorization");
                        await RespondAsync(stream, 400, "Bad Request", MismatchPage, readBudget.Token).ConfigureAwait(false);
                        break;
                    case LoopbackCallbackKind.ProviderError:
                        await RespondAsync(stream, 200, "OK", FailurePage, readBudget.Token).ConfigureAwait(false);
                        completion.TrySetResult(outcome);
                        break;
                    case LoopbackCallbackKind.Success:
                        await RespondAsync(stream, 200, "OK", SuccessPage, readBudget.Token).ConfigureAwait(false);
                        completion.TrySetResult(outcome);
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                // 读取预算用尽或整体取消：丢掉这条连接即可，真正的回调还在后面。
            }
            catch (Exception ex) when (ex is System.IO.IOException or SocketException or ObjectDisposedException)
            {
                _logger.Debug(ex, "OrcaRouter: a loopback connection dropped before it could be read");
            }
        }
    }

    /// <summary>
    /// 判定一次请求。纯函数，离开 socket 也能直接断言。
    /// </summary>
    /// <param name="requestTarget">请求行里的 target，例如 <c>/cb?code=x&amp;state=y</c>。</param>
    /// <param name="callbackPath">已注册的回调路径。</param>
    /// <param name="expectedState">本次授权生成的 state。</param>
    public static LoopbackCallbackOutcome Classify(string? requestTarget, string callbackPath, string expectedState)
    {
        if (string.IsNullOrEmpty(requestTarget) || requestTarget[0] != '/')
        {
            return new LoopbackCallbackOutcome(LoopbackCallbackKind.Ignored);
        }

        var split = requestTarget.IndexOf('?', StringComparison.Ordinal);
        var path = split < 0 ? requestTarget : requestTarget[..split];
        if (!string.Equals(path, callbackPath, StringComparison.Ordinal))
        {
            return new LoopbackCallbackOutcome(LoopbackCallbackKind.Ignored);
        }

        var query = ParseQuery(split < 0 ? string.Empty : requestTarget[(split + 1)..]);

        // state 必须先于一切成立：没有它，任何本机进程都能伪造一次「授权失败」来打断流程。
        if (!query.TryGetValue("state", out var state) || !FixedTimeEquals(state, expectedState))
        {
            return new LoopbackCallbackOutcome(LoopbackCallbackKind.StateMismatch);
        }

        if (query.TryGetValue("error", out var error) && !string.IsNullOrWhiteSpace(error))
        {
            query.TryGetValue("error_description", out var description);
            return new LoopbackCallbackOutcome(
                LoopbackCallbackKind.ProviderError,
                Error: string.IsNullOrWhiteSpace(description) ? error : $"{error}: {description}");
        }

        if (!query.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
        {
            // state 对上了，所以这确实是我们的回调——它只是没带回可用的 code。
            return new LoopbackCallbackOutcome(LoopbackCallbackKind.ProviderError, Error: "missing_code");
        }

        return new LoopbackCallbackOutcome(LoopbackCallbackKind.Success, Code: code);
    }

    /// <summary>定长比较，避免用比较耗时泄漏 state。</summary>
    private static bool FixedTimeEquals(string? left, string right)
        => left != null
           && CryptographicOperations.FixedTimeEquals(
               Encoding.UTF8.GetBytes(left),
               Encoding.UTF8.GetBytes(right));

    /// <summary>从请求行 <c>GET /cb?... HTTP/1.1</c> 中取出 target。</summary>
    internal static string? ExtractRequestTarget(string requestLine)
    {
        var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? parts[1] : null;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=', StringComparison.Ordinal);
            var key = equals < 0 ? pair : pair[..equals];
            var value = equals < 0 ? string.Empty : pair[(equals + 1)..];
            result[WebUtility.UrlDecode(key)] = WebUtility.UrlDecode(value);
        }
        return result;
    }

    private static async Task<string?> ReadRequestLineAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[1];
        var line = new StringBuilder();
        while (line.Length < MaxRequestLineBytes)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) return line.Length > 0 ? line.ToString() : null;
            if (buffer[0] == (byte)'\n') return line.ToString().TrimEnd('\r');
            line.Append((char)buffer[0]);
        }
        return null;
    }

    private static async Task RespondAsync(
        NetworkStream stream,
        int statusCode,
        string reason,
        string body,
        CancellationToken cancellationToken)
    {
        var payload = Encoding.UTF8.GetBytes(body);
        var header = Encoding.ASCII.GetBytes(string.Create(
            CultureInfo.InvariantCulture,
            $"HTTP/1.1 {statusCode} {reason}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {payload.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n"));
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Page(string title, string message)
        => "<!doctype html><html><head><meta charset=\"utf-8\"><title>Athena · OrcaRouter</title>"
           + "<style>body{font:16px/1.6 system-ui,-apple-system,\"Segoe UI\",sans-serif;margin:0;display:flex;"
           + "min-height:100vh;align-items:center;justify-content:center;background:#16181d;color:#e6e8ee}"
           + "main{text-align:center;padding:32px}h1{font-size:20px;margin:0 0 8px}p{margin:0;opacity:.7}</style>"
           + $"</head><body><main><h1>{title}</h1><p>{message}</p></main></body></html>";

    private static readonly string SuccessPage =
        Page("已授权 · Connected", "可以关闭此页面，回到 Athena 继续。You can close this tab and return to Athena.");

    private static readonly string FailurePage =
        Page("授权未完成 · Not connected", "请回到 Athena 重试。Please return to Athena and try again.");

    private static readonly string MismatchPage =
        Page("请求已被忽略 · Ignored", "该回调与当前授权不匹配。This callback does not match the pending authorization.");

    private static readonly string NotFoundPage =
        Page("404", "Athena OrcaRouter callback listener.");

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _listener.Stop();
        _listener.Dispose();
    }
}
