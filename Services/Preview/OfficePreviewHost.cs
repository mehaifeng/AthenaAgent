using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform;
using Serilog;

namespace Athena.UI.Services.Preview;

/// <summary>
/// Office 预览的本地回环 HTTP 服务器。
/// 仅监听 127.0.0.1 随机端口，页面与静态资源从 avares:// 嵌入资源流式提供，
/// 文件经会话表 + 进程级令牌鉴权后只读输出（支持 HTTP Range 供 PDF.js 分块）。
/// 惰性启动：首次注册预览会话时才拉起监听。
/// </summary>
public sealed class OfficePreviewHost : IDisposable
{
    private const string AssetPrefix = "avares://Athena.UI/Assets/Preview/";
    private const int MaxStartAttempts = 5;
    private readonly OfficePreviewSessionStore _store = new();
    private readonly object _startGate = new();
    private readonly ILogger _logger = Log.ForContext<OfficePreviewHost>();
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private string? _baseUrl;
    private bool _disposed;

    /// <summary>注册一个可预览文件的只读会话，返回会话 ID（配合 <see cref="BuildPreviewUrl"/> 使用）。</summary>
    public string RegisterSession(string path) => _store.CreateSession(path);

    public void ReleaseSession(string sessionId) => _store.ReleaseSession(sessionId);

    public void ReleaseAll() => _store.ReleaseAll();

    /// <summary>构造 NativeWebView 加载用的预览 URL（type=pdf|docx|xlsx|pptx）。</summary>
    public string BuildPreviewUrl(string sessionId, string type, string theme, string lang, string fileName)
    {
        EnsureStarted();
        var query = string.Join('&',
            $"type={Uri.EscapeDataString(type)}",
            $"file={Uri.EscapeDataString(sessionId)}",
            $"t={Uri.EscapeDataString(_store.Token)}",
            $"theme={Uri.EscapeDataString(theme)}",
            $"lang={Uri.EscapeDataString(lang)}",
            $"name={Uri.EscapeDataString(fileName)}");
        return $"{_baseUrl}?{query}";
    }

    public int SessionCount => _store.SessionCount;

    private void EnsureStarted()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(OfficePreviewHost));
        if (_listener != null) return;
        lock (_startGate)
        {
            if (_listener != null) return;
            Exception? lastError = null;
            for (var attempt = 0; attempt < MaxStartAttempts; attempt++)
            {
                var port = ReservePort();
                var prefix = BuildPrefix(port);
                var listener = new HttpListener { IgnoreWriteExceptions = true };
                try
                {
                    listener.Prefixes.Add(prefix);
                    listener.Start();
                    _listener = listener;
                    _baseUrl = prefix;
                    _cts = new CancellationTokenSource();
                    _ = Task.Run(() => ListenLoopAsync(_cts.Token));
                    _logger.Information("Office preview server started at {Prefix}", prefix);
                    return;
                }
                catch (Exception ex) when (ex is HttpListenerException or SocketException)
                {
                    lastError = ex;
                    listener.Close();
                    _logger.Debug(ex, "Failed to start office preview server at {Prefix}, retrying", prefix);
                }
            }
            _logger.Error(lastError, "Office preview server failed to start after {Attempts} attempts", MaxStartAttempts);
            throw new InvalidOperationException("Office preview server could not be started.", lastError);
        }
    }

    /// <summary>HttpListener 前缀不支持端口 0，先用 TcpListener 预留一个空闲端口再启动。</summary>
    private static int ReservePort()
    {
        using var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();
        return port;
    }

    /// <summary>
    /// 前缀主机名按平台区分：Windows 的 http.sys 对非管理员隐式放行 localhost 前缀
    /// （127.0.0.1 前缀在部分 Windows 版本会 Access Denied）；macOS/Linux 为托管实现无此限制。
    /// </summary>
    private static string BuildPrefix(int port)
        => OperatingSystem.IsWindows()
            ? $"http://localhost:{port}/"
            : $"http://127.0.0.1:{port}/";

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener!.GetContextAsync().WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Office preview listener stopped");
                break;
            }
            _ = HandleContextAsync(context);
        }
    }

    private async Task HandleContextAsync(HttpListenerContext context)
    {
        // 默认 200，各分支在失败时显式设置 response.StatusCode（统一记录 4xx/5xx 日志）
        var status = (int)HttpStatusCode.OK;
        try
        {
            var path = context.Request.Url?.AbsolutePath ?? "/";
            if (path == "/" || path is "/index.html" or "/viewer.js")
            {
                // 页面顶层静态资源（index.html 内部以相对路径引用 viewer.js，必须同源可加载）
                await ServeAssetAsync(context, path == "/" ? "index.html" : path.TrimStart('/'));
            }
            else if (path.StartsWith("/libs/", StringComparison.Ordinal))
            {
                // 仅允许纯文件名，杜绝路径遍历
                var name = path["/libs/".Length..];
                if (IsSafeFileName(name))
                {
                    await ServeAssetAsync(context, $"lib/{name}");
                }
                else
                {
                    context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                }
            }
            else if (path.StartsWith("/file/", StringComparison.Ordinal))
            {
                await ServeFileAsync(context, path["/file/".Length..]);
            }
            else
            {
                context.Response.StatusCode = (int)HttpStatusCode.NotFound;
            }
            status = (int)context.Response.StatusCode;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Office preview request failed: {Path}", context.Request.Url?.AbsolutePath);
        }
        finally
        {
            if (status is >= 400 or 0)
                _logger.Information("Office preview {Method} {Path} -> {Status}", context.Request.HttpMethod, context.Request.Url?.AbsolutePath, status);
            try { context.Response.Close(); } catch { /* 客户端提前断开属正常 */ }
        }
    }

    private static bool IsSafeFileName(string name)
        => name.Length is > 0 and <= 128
           && name != ".."
           && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    private async Task ServeAssetAsync(HttpListenerContext context, string assetRelativePath)
    {
        using var stream = TryOpenAsset(new Uri(AssetPrefix + assetRelativePath, UriKind.Absolute));
        if (stream == null)
        {
            context.Response.StatusCode = (int)HttpStatusCode.NotFound;
            return;
        }

        var response = context.Response;
        response.ContentType = OfficeMimeMap.ForPath(assetRelativePath);
        response.AddHeader("Cache-Control", "public, max-age=86400");
        if (stream.CanSeek) response.ContentLength64 = stream.Length;
        if (string.Equals(context.Request.HttpMethod, "HEAD", StringComparison.OrdinalIgnoreCase)) return;
        await stream.CopyToAsync(response.OutputStream);
    }

    /// <summary>打开嵌入资源；资源不存在返回 null（调用方映射为 404）。</summary>
    private Stream? TryOpenAsset(Uri uri)
    {
        try
        {
            return AssetLoader.Open(uri);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Office preview asset not found: {Uri}", uri);
            return null;
        }
    }

    private async Task ServeFileAsync(HttpListenerContext context, string sessionId)
    {
        var response = context.Response;
        var token = context.Request.QueryString["t"];
        if (!_store.ValidateToken(token))
        {
            response.StatusCode = (int)HttpStatusCode.Forbidden;
            return;
        }
        if (!_store.TryGetSession(sessionId, out var filePath) || !File.Exists(filePath))
        {
            response.StatusCode = (int)HttpStatusCode.NotFound;
            return;
        }

        var total = new FileInfo(filePath).Length;
        var rangeResult = OfficeRangeParser.TryParse(context.Request.Headers["Range"], total, out var start, out var end);
        if (rangeResult == OfficeRangeResult.Invalid)
        {
            response.StatusCode = (int)HttpStatusCode.RequestedRangeNotSatisfiable;
            response.AddHeader("Content-Range", $"bytes */{total}");
            response.AddHeader("Accept-Ranges", "bytes");
            return;
        }

        response.StatusCode = rangeResult == OfficeRangeResult.Valid
            ? (int)HttpStatusCode.PartialContent
            : (int)HttpStatusCode.OK;
        response.AddHeader("Accept-Ranges", "bytes");
        response.AddHeader("Cache-Control", "no-store");
        response.ContentType = OfficeMimeMap.ForPath(filePath);
        if (rangeResult == OfficeRangeResult.Valid)
        {
            response.AddHeader("Content-Range", $"bytes {start}-{end}/{total}");
            response.ContentLength64 = end - start + 1;
        }
        else
        {
            response.ContentLength64 = total;
        }

        if (string.Equals(context.Request.HttpMethod, "HEAD", StringComparison.OrdinalIgnoreCase)) return;

        // 每次请求按路径现读现发（FileShare.ReadWrite 允许文件被外部修改）
        await using var file = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (rangeResult == OfficeRangeResult.Valid)
        {
            file.Seek(start, SeekOrigin.Begin);
            var remaining = end - start + 1;
            var buffer = new byte[81920];
            while (remaining > 0)
            {
                var read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)));
                if (read == 0) break;
                await response.OutputStream.WriteAsync(buffer.AsMemory(0, read));
                remaining -= read;
            }
        }
        else
        {
            await file.CopyToAsync(response.OutputStream);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _store.ReleaseAll();
        _cts?.Cancel();
        _cts?.Dispose();
        _listener?.Close();
        _listener = null;
        _baseUrl = null;
    }
}
