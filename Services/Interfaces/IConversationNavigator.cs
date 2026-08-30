using System.Threading.Tasks;

namespace Athena.UI.Services.Interfaces;

/// <summary>
/// 会话跳转边界。任务页需要"点开这次运行创建的会话"，但它不该伸手进
/// MainWindowViewModel 的集合里翻——那既是层级越权，也让任务页无法被单独测试。
/// 主窗口在运行期把自己注册进来，任务页只认这个接口。
/// </summary>
public interface IConversationNavigationTarget
{
    /// <summary>
    /// 按历史条目 ID 或会话 ID 选中一个会话；找不到返回 false。
    /// 异步是为了让后台调用方能 await 而不是阻塞：选中会话必须在 UI 线程上做，
    /// 同步签名会逼实现方 <c>InvokeAsync(...).GetAwaiter().GetResult()</c>，
    /// 那正是本项目退出流程栽过的那种互等死锁。
    /// </summary>
    Task<bool> TryNavigateToConversationAsync(string? historyId, string? conversationId);
}

/// <summary>可注入的跳转入口；宿主未挂载时安全地返回 false。</summary>
public interface IConversationNavigator
{
    bool IsReady { get; }

    void AttachTarget(IConversationNavigationTarget target);

    void DetachTarget(IConversationNavigationTarget target);

    Task<bool> TryNavigateToConversationAsync(string? historyId, string? conversationId);
}
