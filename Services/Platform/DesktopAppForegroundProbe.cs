using Athena.UI.Services.Interfaces;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using System.Linq;

namespace Athena.UI.Services.Platform;

/// <summary>
/// 桌面端实现：遍历生命周期持有的全部窗口。
///
/// 只读属性，可以在任意线程被读到（通知是在后台线程决定发不发的）。
/// Avalonia 的 IsActive / IsVisible 是普通的 AvaloniaProperty 读取，
/// 跨线程读不会抛，最坏情况是拿到刚刚过期的一帧状态——
/// 那对「要不要弹通知」这个判断完全够用，而为它切一次 UI 线程
/// 会把一个纯查询变成可能死锁的等待。
/// </summary>
public sealed class DesktopAppForegroundProbe : IAppForegroundProbe
{
    public bool IsForeground
    {
        get
        {
            if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            {
                return false;
            }

            return desktop.Windows.Any(window => window.IsVisible && window.IsActive);
        }
    }
}
