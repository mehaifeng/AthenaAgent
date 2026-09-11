using Athena.UI.Services.Interfaces;
using Serilog;
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.Notifications;

/// <summary>
/// macOS 通知，走 osascript 的 <c>display notification</c>。
///
/// 为什么不用 UNUserNotificationCenter：它要求宿主是一个**正确签名**的 bundle，
/// 而 release.sh 只做 ad-hoc 签名（<c>codesign -s -</c>，见其中关于 Gatekeeper 的注释），
/// 那种 bundle 申请通知授权大概率直接抛异常。与其写一段在自家发行包上
/// 跑不起来的原生调用，不如用一条到处都能跑的路径。
///
/// 代价要说清楚：通知会以「脚本编辑器」的身份和图标出现，
/// 而且用户必须在「系统设置 → 通知」里允许脚本编辑器，否则静默丢弃。
/// 撤回与紧急程度都无从表达——osascript 这条路根本没有对应的入口。
/// 换成正式签名之后，把本类替换为原生实现即可，接口不必动。
/// </summary>
internal sealed class MacOsNotificationBackend : IPlatformNotificationBackend
{
    private readonly ICliService _cliService;
    private readonly ILogger _logger;

    public MacOsNotificationBackend(ICliService cliService, ILogger logger)
    {
        _cliService = cliService;
        _logger = logger;
    }

    public async Task ShowAsync(SystemNotificationRequest request, CancellationToken cancellationToken)
    {
        var script = "display notification "
                     + AppleScriptString(request.Body)
                     + " with title "
                     + AppleScriptString(request.Title);

        try
        {
            var result = await _cliService.ExecuteAsync(
                "osascript",
                ["-e", script],
                timeoutSeconds: 15,
                ct: cancellationToken).ConfigureAwait(false);

            if (!result.IsSuccess)
            {
                _logger.Debug(
                    "osascript could not deliver the notification (exit {ExitCode}): {Error}",
                    result.ExitCode,
                    string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "osascript is unavailable");
        }
    }

    /// <summary>macOS 这条路没有撤回入口，通知发出去就收不回来了。</summary>
    public Task WithdrawAsync(string key, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// 包成 AppleScript 字符串字面量。整段脚本是作为单个 argv 元素传给 osascript 的，
    /// 中间没有 shell 参与，所以只需要处理 AppleScript 自己的转义。
    /// </summary>
    internal static string AppleScriptString(string value)
    {
        var builder = new StringBuilder(value.Length + 8);
        builder.Append('"');
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\\': builder.Append("\\\\"); break;
                case '"': builder.Append("\\\""); break;
                // AppleScript 的字符串字面量不能跨行，裸换行会让整段脚本语法错误。
                case '\n': builder.Append("\\n"); break;
                case '\r': break;
                case '\t': builder.Append("\\t"); break;
                default: builder.Append(ch); break;
            }
        }
        builder.Append('"');
        return builder.ToString();
    }
}
