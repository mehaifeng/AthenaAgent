using Athena.UI.Services.Interfaces;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.Notifications;

/// <summary>
/// Windows 通知（WinRT Toast），经由 Windows PowerShell 投递。
///
/// 三个约束是实测出来的，改动前请先复现：
///
/// 1. **AUMID 必须真的注册过，且失败是静默的。** 未打包的 Win32 应用调用
///    <c>CreateToastNotifier(aumid)</c> 时，如果这个 AUMID 没有对应的开始菜单快捷方式，
///    <c>Show()</c> **不抛异常也不显示任何东西**。所以这里绝不能写成
///    「先试自家 AUMID，catch 了再回退」——那个 catch 永远不会进，用户什么也收不到。
///    身份由 <see cref="WindowsShortcutAumid"/> 在 C# 侧确定：它会把
///    System.AppUserModel.ID 补到开始菜单快捷方式上，从而让通知显示成 Athena
///    的名字和图标；补不上才退回 PowerShell 自己的 AUMID（每台 Windows 上都存在，
///    但署名会是「Windows PowerShell」）。
///
/// 2. **必须是 Windows PowerShell 5.1（powershell.exe），不能是 pwsh。**
///    <c>[Type, Assembly, ContentType=WindowsRuntime]</c> 这种 WinRT 类型加载语法
///    在 PowerShell 7 上没有 WinRT 兼容层就用不了。SystemAudioService 也是同样的理由。
///
/// 3. **撤回要带 AUMID。** <c>History.Remove(tag, group, aumid)</c> 的三参重载才是
///    未打包应用能用的那个。
///
/// 4. **脚本必须走 -EncodedCommand，不能当普通参数传。** toast XML 里有
///    <c>template="ToastGeneric"</c> 这样的双引号，把整段脚本作为一个命令行参数
///    传给 powershell.exe 时，这些引号会在命令行重解析中被吃掉，
///    LoadXml 直接以 0xC00CE502 失败。Base64（UTF-16LE）之后参数里只剩
///    字母数字和 +/=，没有任何一层还能改写它。
///
/// 退回 PowerShell AUMID 时通知会显示成「Windows PowerShell」的名字和图标。
/// 想要品牌化，安装包侧已在 [Icons] 上写了 AppUserModelID；zip 便携版没有快捷方式，
/// 也就只能是这个样子——但它至少一定弹得出来。
/// </summary>
internal sealed class WindowsToastNotificationBackend : IPlatformNotificationBackend
{
    /// <summary>每台 Windows 都有的兜底身份。用它发出的通知署名「Windows PowerShell」。</summary>
    private const string PowerShellAumid =
        @"{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\WindowsPowerShell\v1.0\powershell.exe";

    private const string ToastGroup = "Athena";

    /// <summary>本应用希望注册的身份，与 windows-installer.iss 的 [Icons] 保持一致。</summary>
    private const string AthenaAumid = "com.athena.ai";

    /// <summary>开始菜单里的显示名，同时也是通知上出现的名字（对应 Inno 的 DefaultGroupName）。</summary>
    private const string ShortcutName = "Athena";

    /// <summary>Key → toast 的 Tag。撤回时要按 Tag 定位。</summary>
    private readonly ConcurrentDictionary<string, string> _liveNotifications = new(StringComparer.Ordinal);

    private readonly ICliService _cliService;
    private readonly ILogger _logger;

    /// <summary>
    /// 首次发送时解析一次并缓存整个进程生命周期：读写快捷方式要起 STA 线程走 shell COM，
    /// 不该每条通知都付一次。为空表示还没解析过。
    /// </summary>
    private string? _resolvedAumid;

    private readonly object _aumidLock = new();

    public WindowsToastNotificationBackend(ICliService cliService, ILogger logger)
    {
        _cliService = cliService;
        _logger = logger;
    }

    public async Task ShowAsync(SystemNotificationRequest request, CancellationToken cancellationToken)
    {
        // 同一个 Key 复用同一个 Tag：Windows 会就地替换同 Tag+Group 的通知，
        // 于是「还有 3 个待审批」不会在通知中心堆成三条。
        var tag = string.IsNullOrEmpty(request.Key) ? NewTransientTag() : TagForKey(request.Key);

        try
        {
            var aumid = ResolveAumid();
            var result = await _cliService.ExecuteAsync(
                "powershell",
                ["-NoProfile", "-NonInteractive", "-EncodedCommand", EncodeCommand(BuildShowScript(request, tag, aumid))],
                timeoutSeconds: 30,
                ct: cancellationToken).ConfigureAwait(false);

            if (!result.IsSuccess)
            {
                _logger.Debug(
                    "PowerShell could not deliver the toast (exit {ExitCode}): {Error}",
                    result.ExitCode,
                    string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError);
                return;
            }

            if (!string.IsNullOrEmpty(request.Key)) _liveNotifications[request.Key] = tag;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Windows toast delivery failed");
        }
    }

    public async Task WithdrawAsync(string key, CancellationToken cancellationToken)
    {
        if (!_liveNotifications.TryRemove(key, out var tag)) return;

        // 从没发过通知就没有需要清理的历史条目，也就不必为撤回去解析身份。
        var aumid = _resolvedAumid;
        if (string.IsNullOrEmpty(aumid)) return;

        try
        {
            await _cliService.ExecuteAsync(
                "powershell",
                ["-NoProfile", "-NonInteractive", "-EncodedCommand", EncodeCommand(BuildWithdrawScript(tag, aumid))],
                timeoutSeconds: 20,
                ct: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Could not withdraw the toast for key {Key}", key);
        }
    }

    /// <summary>
    /// 解析一次通知身份并缓存。优先让 <see cref="WindowsShortcutAumid"/> 把
    /// AUMID 补到开始菜单快捷方式上——那样通知才会署 Athena 的名字和图标；
    /// 补不上（比如快捷方式所在目录不可写）才退回 PowerShell 的通用身份。
    /// </summary>
    private string ResolveAumid()
    {
        if (_resolvedAumid != null) return _resolvedAumid;

        lock (_aumidLock)
        {
            if (_resolvedAumid != null) return _resolvedAumid;

            var provisioned = WindowsShortcutAumid.TryEnsure(AthenaAumid, ShortcutName, _logger);
            if (string.IsNullOrEmpty(provisioned))
            {
                _logger.Information(
                    "No Start Menu identity available; notifications will be delivered under the generic PowerShell identity");
                _resolvedAumid = PowerShellAumid;
            }
            else
            {
                _resolvedAumid = provisioned;
            }
            return _resolvedAumid;
        }
    }

    private string BuildShowScript(SystemNotificationRequest request, string tag, string aumid)
    {
        var toastXml = "<toast><visual><binding template=\"ToastGeneric\">"
                       + "<text>" + XmlEscape(request.Title) + "</text>"
                       + "<text>" + XmlEscape(request.Body) + "</text>"
                       + "</binding></visual></toast>";

        var builder = new StringBuilder();
        builder.Append("$ErrorActionPreference='Stop';");
        builder.Append("$null=[Windows.UI.Notifications.ToastNotificationManager,Windows.UI.Notifications,ContentType=WindowsRuntime];");
        builder.Append("$null=[Windows.Data.Xml.Dom.XmlDocument,Windows.Data.Xml.Dom,ContentType=WindowsRuntime];");
        builder.Append("$aumid='").Append(PowerShellLiteral(aumid)).Append("';");
        builder.Append("$xml=New-Object Windows.Data.Xml.Dom.XmlDocument;");
        builder.Append("$xml.LoadXml('").Append(PowerShellLiteral(toastXml)).Append("');");
        builder.Append("$toast=New-Object Windows.UI.Notifications.ToastNotification $xml;");
        builder.Append("$toast.Tag='").Append(PowerShellLiteral(tag)).Append("';");
        builder.Append("$toast.Group='").Append(ToastGroup).Append("';");
        builder.Append("[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($aumid).Show($toast);");
        return builder.ToString();
    }

    private static string BuildWithdrawScript(string tag, string aumid)
    {
        var builder = new StringBuilder();
        builder.Append("$ErrorActionPreference='SilentlyContinue';");
        builder.Append("$null=[Windows.UI.Notifications.ToastNotificationManager,Windows.UI.Notifications,ContentType=WindowsRuntime];");
        // 三参重载是未打包应用唯一能用的那个：没有 AUMID 就定位不到历史记录。
        builder.Append("[Windows.UI.Notifications.ToastNotificationManager]::History.Remove('")
               .Append(PowerShellLiteral(tag)).Append("','")
               .Append(ToastGroup).Append("','")
               .Append(PowerShellLiteral(aumid)).Append("');");
        return builder.ToString();
    }

    /// <summary>powershell.exe 的 -EncodedCommand 要的是 UTF-16LE 的 Base64。</summary>
    internal static string EncodeCommand(string script)
        => Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

    /// <summary>
    /// Tag 上限 64 字符且不接受任意内容，而 Key 里带的是会话 id 这类长字符串，
    /// 所以按 Key 取一个稳定短哈希——稳定是关键，撤回要靠它找回同一条通知。
    /// </summary>
    internal static string TagForKey(string key)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return "athena-" + Convert.ToHexString(bytes, 0, 8).ToLowerInvariant();
    }

    private static string NewTransientTag()
        => "athena-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..16];

    /// <summary>PowerShell 单引号字面量里，唯一需要转义的就是单引号本身（写成两个）。</summary>
    internal static string PowerShellLiteral(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);

    /// <summary>
    /// XML 文本转义。单引号也一并转成实体，这样整段 toast XML 里不会再出现裸单引号，
    /// 可以安全地塞进上面那个 PowerShell 单引号字面量。
    /// </summary>
    internal static string XmlEscape(string value)
    {
        var builder = new StringBuilder(value.Length + 16);
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '&': builder.Append("&amp;"); break;
                case '<': builder.Append("&lt;"); break;
                case '>': builder.Append("&gt;"); break;
                case '"': builder.Append("&quot;"); break;
                case '\'': builder.Append("&apos;"); break;
                case '\r': break;
                case '\n': builder.Append(' '); break;
                default: builder.Append(ch); break;
            }
        }
        return builder.ToString();
    }
}
