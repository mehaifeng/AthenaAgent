using Athena.UI.Services.Interfaces;
using Serilog;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.Notifications;

/// <summary>
/// 系统通知的统一入口：选平台后端、读配置开关、兜住所有异常。
///
/// 「通知失败绝不能影响调用方」是本类唯一的硬要求。调用点都在业务流程上
/// （审批弹窗、回合收尾），一次 notify-send 不存在或 osascript 被拒授权
/// 都不该让那条流程抖一下，所以对外的两个方法都不抛。
/// </summary>
public sealed class SystemNotificationService : ISystemNotificationService
{
    /// <summary>
    /// 标题/正文的硬上限。三端都会自己截断，这里截是为了别把一整段命令行
    /// 塞进进程参数——Windows 那条路要把它拼进 PowerShell 脚本里。
    /// </summary>
    private const int MaxTitleLength = 120;
    private const int MaxBodyLength = 400;

    private readonly IPlatformNotificationBackend? _backend;
    private readonly IConfigService? _configService;
    private readonly ILogger _logger;

    public SystemNotificationService(
        ICliService cliService,
        ILogger logger,
        IConfigService? configService = null)
    {
        _logger = logger;
        _configService = configService;
        _backend = CreateBackend(cliService, logger);
    }

    public bool IsSupported => _backend != null;

    public async Task ShowAsync(SystemNotificationRequest request, CancellationToken cancellationToken = default)
    {
        if (_backend == null || !IsEnabled()) return;

        var normalized = new SystemNotificationRequest
        {
            Title = Truncate(request.Title, MaxTitleLength),
            Body = Truncate(request.Body, MaxBodyLength),
            Urgency = request.Urgency,
            Key = request.Key
        };

        if (string.IsNullOrWhiteSpace(normalized.Title) && string.IsNullOrWhiteSpace(normalized.Body)) return;

        try
        {
            await _backend.ShowAsync(normalized, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 取消是调用方自己的决定，安静返回即可。
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "System notification could not be delivered");
        }
    }

    public async Task WithdrawAsync(string key, CancellationToken cancellationToken = default)
    {
        if (_backend == null || string.IsNullOrEmpty(key)) return;

        // 撤回不看配置开关：用户可能在通知发出之后才把开关关掉，
        // 那条已经挂在通知中心里的提示仍然该被清掉。
        try
        {
            await _backend.WithdrawAsync(key, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "System notification could not be withdrawn");
        }
    }

    private bool IsEnabled()
    {
        if (_configService == null) return true;
        try
        {
            return _configService.Load().SystemNotificationsEnabled;
        }
        catch (Exception ex)
        {
            // 读不到配置就按开启处理：漏掉一条审批提示的代价远高于多弹一条。
            _logger.Debug(ex, "Could not read the notification setting; assuming it is on");
            return true;
        }
    }

    private static IPlatformNotificationBackend? CreateBackend(ICliService cliService, ILogger logger)
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsToastNotificationBackend(cliService, logger.ForContext<WindowsToastNotificationBackend>());
        }
        if (OperatingSystem.IsMacOS())
        {
            return new MacOsNotificationBackend(cliService, logger.ForContext<MacOsNotificationBackend>());
        }
        if (OperatingSystem.IsLinux())
        {
            return new LinuxNotificationBackend(cliService, logger.ForContext<LinuxNotificationBackend>());
        }
        return null;
    }

    internal static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var collapsed = value.Trim();
        return collapsed.Length <= maxLength ? collapsed : collapsed[..(maxLength - 1)] + "…";
    }
}
