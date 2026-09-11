using Athena.UI.Services.Interfaces;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.Notifications;

/// <summary>
/// 单个平台的通知投递方式。三端能力不齐，差异全部吸收在实现里，
/// 对上只暴露「发」和「撤」两个动作。
/// </summary>
internal interface IPlatformNotificationBackend
{
    /// <summary>投递一条通知。带 Key 时应就地替换同 Key 的上一条，而不是叠加。</summary>
    Task ShowAsync(SystemNotificationRequest request, CancellationToken cancellationToken);

    /// <summary>撤回同 Key 的通知。平台不支持、目标已消失都不算失败。</summary>
    Task WithdrawAsync(string key, CancellationToken cancellationToken);
}
