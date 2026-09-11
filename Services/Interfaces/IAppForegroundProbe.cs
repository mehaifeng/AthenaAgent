namespace Athena.UI.Services.Interfaces;

/// <summary>
/// 「应用现在是不是在用户眼前」。系统通知只在不在眼前时才该发——
/// 用户正盯着审批窗口时再弹一条系统通知纯属噪音。
///
/// 判断的是**任意一个窗口**是否处于激活态，而不是主窗口：审批队列是独立窗口，
/// 它被激活时主窗口必然不是激活的，只看主窗口会把「用户正在看审批」误判成「人不在」。
/// </summary>
public interface IAppForegroundProbe
{
    bool IsForeground { get; }
}
