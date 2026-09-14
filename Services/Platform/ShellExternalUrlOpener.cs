using Athena.UI.Services.Interfaces;
using Serilog;
using System;
using System.Diagnostics;

namespace Athena.UI.Services.Platform;

/// <summary>交给操作系统的默认处理程序（Windows/macOS/Linux 都靠 UseShellExecute 分派）。</summary>
public sealed class ShellExternalUrlOpener : IExternalUrlOpener
{
    private readonly ILogger _logger;

    public ShellExternalUrlOpener(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public bool TryOpen(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            // 只把 http(s) 交给 shell：UseShellExecute 会执行它认得的任何东西，
            // 包括本地路径和自定义协议，不能让一个拼错的 URL 变成一次任意启动。
            _logger.Error("Refused to open a non-http(s) url in the system browser");
            return false;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = uri.AbsoluteUri,
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception ex)
        {
            // URL 本身可能带着 query 参数，所以只记 host。
            _logger.Error(ex, "Failed to open {Host} in the system browser", uri.Host);
            return false;
        }
    }
}
