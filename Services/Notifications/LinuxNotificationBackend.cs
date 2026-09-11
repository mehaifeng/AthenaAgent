using Athena.UI.Services.Interfaces;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.Notifications;

/// <summary>
/// freedesktop 通知（org.freedesktop.Notifications）。
///
/// 主路径是 gdbus 而不是 notify-send：notify-send 来自 libnotify-bin，
/// 在精简发行版上经常没装；gdbus 属于 glib2，任何跑得起通知守护进程的桌面上都在。
/// 更要紧的是 gdbus 直接返回通知 id——替换与撤回都要靠它，
/// 而老版本的 notify-send 连 --print-id 都没有。
///
/// 这里刻意不用已在依赖里的 Tmds.DBus.Protocol 手写 DBus 帧：那需要自己拼
/// a{sv} 的 marshalling，而本机无法验证的二进制协议代码属于写完只能祈祷的那类。
/// 走 CLI 的代价是每条通知一次进程启动，对人类节奏的审批提示完全无所谓。
/// </summary>
internal sealed class LinuxNotificationBackend : IPlatformNotificationBackend
{
    private const string BusName = "org.freedesktop.Notifications";
    private const string ObjectPath = "/org/freedesktop/Notifications";
    private const string AppName = "Athena";

    /// <summary>Key → 通知守护进程分配的 id。替换与撤回都要拿它当句柄。</summary>
    private readonly ConcurrentDictionary<string, uint> _liveNotifications = new(StringComparer.Ordinal);

    private readonly ICliService _cliService;
    private readonly ILogger _logger;

    public LinuxNotificationBackend(ICliService cliService, ILogger logger)
    {
        _cliService = cliService;
        _logger = logger;
    }

    public async Task ShowAsync(SystemNotificationRequest request, CancellationToken cancellationToken)
    {
        var replacesId = 0u;
        if (!string.IsNullOrEmpty(request.Key)
            && _liveNotifications.TryGetValue(request.Key, out var existing))
        {
            replacesId = existing;
        }

        var id = await NotifyViaGdbusAsync(request, replacesId, cancellationToken).ConfigureAwait(false)
                 ?? await NotifyViaNotifySendAsync(request, replacesId, cancellationToken).ConfigureAwait(false);

        if (id.HasValue && !string.IsNullOrEmpty(request.Key))
        {
            _liveNotifications[request.Key] = id.Value;
        }
    }

    public async Task WithdrawAsync(string key, CancellationToken cancellationToken)
    {
        if (!_liveNotifications.TryRemove(key, out var id)) return;

        try
        {
            await _cliService.ExecuteAsync(
                "gdbus",
                BuildGdbusArguments("CloseNotification", [id.ToString(CultureInfo.InvariantCulture)]),
                timeoutSeconds: 10,
                ct: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 撤回失败只会留下一条陈旧通知，不该把调用方的流程带崩。
            _logger.Debug(ex, "Could not withdraw the notification for key {Key}", key);
        }
    }

    private async Task<uint?> NotifyViaGdbusAsync(
        SystemNotificationRequest request,
        uint replacesId,
        CancellationToken cancellationToken)
    {
        // Notify(app_name s, replaces_id u, app_icon s, summary s, body s,
        //        actions as, hints a{sv}, expire_timeout i) -> u
        string[] parameters =
        [
            GVariantString(AppName),
            replacesId.ToString(CultureInfo.InvariantCulture),
            GVariantString(string.Empty),
            GVariantString(request.Title),
            GVariantString(request.Body),
            "@as []",
            BuildHints(request.Urgency),
            // critical 用 0（永不自动过期）：审批不处理，应用就停在那里，
            // 一条 5 秒后自己消失的提示等于没提示。-1 表示交给守护进程的默认值。
            request.Urgency == SystemNotificationUrgency.Critical ? "0" : "-1"
        ];

        try
        {
            var result = await _cliService.ExecuteAsync(
                "gdbus",
                BuildGdbusArguments("Notify", parameters),
                timeoutSeconds: 15,
                ct: cancellationToken).ConfigureAwait(false);

            if (!result.IsSuccess)
            {
                _logger.Debug(
                    "gdbus could not deliver the notification (exit {ExitCode}): {Error}",
                    result.ExitCode,
                    string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError);
                return null;
            }

            return ParseGdbusNotificationId(result.StandardOutput);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "gdbus is unavailable; falling back to notify-send");
            return null;
        }
    }

    private async Task<uint?> NotifyViaNotifySendAsync(
        SystemNotificationRequest request,
        uint replacesId,
        CancellationToken cancellationToken)
    {
        var urgency = request.Urgency == SystemNotificationUrgency.Critical ? "critical" : "normal";
        var expire = request.Urgency == SystemNotificationUrgency.Critical ? "0" : "-1";

        // 先试带 id 的完整形态；--print-id / --replace-id 是较新的选项，
        // 老版本会直接报参数错误，那时退回到没有替换语义的最简形态。
        string[][] attempts =
        [
            [
                "--app-name", AppName,
                "--urgency", urgency,
                "--expire-time", expire,
                "--print-id",
                "--replace-id", replacesId.ToString(CultureInfo.InvariantCulture),
                request.Title,
                request.Body
            ],
            [request.Title, request.Body]
        ];

        foreach (var arguments in attempts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var result = await _cliService.ExecuteAsync(
                    "notify-send",
                    arguments,
                    timeoutSeconds: 15,
                    ct: cancellationToken).ConfigureAwait(false);

                if (!result.IsSuccess) continue;

                return uint.TryParse(
                    result.StandardOutput.Trim(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var id)
                    ? id
                    : null;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 命令本身不存在：两种形态都不会有结果，不必再试第二遍。
                _logger.Debug(ex, "notify-send is unavailable");
                return null;
            }
        }

        return null;
    }

    private static string[] BuildGdbusArguments(string method, string[] parameters)
    {
        var arguments = new string[8 + parameters.Length];
        arguments[0] = "call";
        arguments[1] = "--session";
        arguments[2] = "--dest";
        arguments[3] = BusName;
        arguments[4] = "--object-path";
        arguments[5] = ObjectPath;
        arguments[6] = "--method";
        arguments[7] = BusName + "." + method;
        parameters.CopyTo(arguments, 8);
        return arguments;
    }

    private static string BuildHints(SystemNotificationUrgency urgency)
    {
        // urgency 是 byte：0=low 1=normal 2=critical。
        // resident 让支持它的守护进程在用户点击后仍保留条目——审批场景下
        // 「手滑点掉就再也想不起来还有个调用卡着」是真实的失败模式。
        return urgency == SystemNotificationUrgency.Critical
            ? "{'urgency': <byte 2>, 'resident': <true>}"
            : "{'urgency': <byte 1>}";
    }

    /// <summary>gdbus 的返回形如 <c>(uint32 42,)</c>。</summary>
    internal static uint? ParseGdbusNotificationId(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return null;
        var match = Regex.Match(stdout, "uint32\\s+(\\d+)", RegexOptions.CultureInvariant);
        return match.Success
               && uint.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
            ? id
            : null;
    }

    /// <summary>
    /// 把任意字符串包成 GVariant 文本字面量。gdbus 会把每个 argv 元素当 GVariant 解析，
    /// 所以裸的 <c>Hello world</c> 会被当成语法错误——引号必须在参数值里面。
    /// </summary>
    internal static string GVariantString(string value)
    {
        var builder = new StringBuilder(value.Length + 8);
        builder.Append('\'');
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\\': builder.Append("\\\\"); break;
                case '\'': builder.Append("\\'"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default: builder.Append(ch); break;
            }
        }
        builder.Append('\'');
        return builder.ToString();
    }
}
