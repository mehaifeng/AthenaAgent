using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.Interfaces;

/// <summary>
/// 通知的紧急程度。只有两档，因为三端里唯一都能表达的区别就是
/// 「看到就行」和「不处理它就一直卡在这」。
/// </summary>
public enum SystemNotificationUrgency
{
    Normal,

    /// <summary>
    /// 需要用户回来处理，否则应用停在原地。Linux 映射为 urgency=critical 且永不过期；
    /// Windows / macOS 无对应概念，退化为普通通知（两者的通知中心都会留存条目）。
    /// </summary>
    Critical
}

/// <summary>一条待发的系统通知。</summary>
public sealed class SystemNotificationRequest
{
    public required string Title { get; init; }

    public required string Body { get; init; }

    public SystemNotificationUrgency Urgency { get; init; } = SystemNotificationUrgency.Normal;

    /// <summary>
    /// 稳定的去重键。同一个键再次 Show 会就地替换而不是叠一条新的，
    /// 并且可以被 <see cref="ISystemNotificationService.WithdrawAsync"/> 撤回。
    /// 为空表示这是一条「发完就不管」的通知。
    /// </summary>
    public string? Key { get; init; }
}

/// <summary>
/// 操作系统级通知。与应用内的未读标记是两件事：
/// 应用内标记负责「回来之后找到现场」，这里只负责「把人叫回来」。
///
/// 三端的能力并不齐平，接口刻意只暴露各端都能兑现的部分：
/// 点击回调不在其中——Windows 未打包应用要拿点击事件必须注册 COM 激活器，
/// 而 osascript / notify-send 这两条路根本拿不到，把它写进接口只会得到一个
/// 两端永远为假的承诺。
/// </summary>
public interface ISystemNotificationService
{
    /// <summary>当前系统上是否有可用后端。为 false 时 Show/Withdraw 都是安全的空操作。</summary>
    bool IsSupported { get; }

    Task ShowAsync(SystemNotificationRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// 撤回此前用同一个 Key 发出的通知。目标不存在、已被用户关掉、
    /// 或当前平台不支持撤回时都不算失败——撤回是尽力而为的清理。
    /// </summary>
    Task WithdrawAsync(string key, CancellationToken cancellationToken = default);
}
