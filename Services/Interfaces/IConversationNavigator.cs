namespace Athena.UI.Services.Interfaces;

/// <summary>
/// 会话跳转边界。任务页需要"点开这次运行创建的会话"，但它不该伸手进
/// MainWindowViewModel 的集合里翻——那既是层级越权，也让任务页无法被单独测试。
/// 主窗口在运行期把自己注册进来，任务页只认这个接口。
/// </summary>
public interface IConversationNavigationTarget
{
    /// <summary>按历史条目 ID 或会话 ID 选中一个会话；找不到返回 false。</summary>
    bool TryNavigateToConversation(string? historyId, string? conversationId);
}

/// <summary>可注入的跳转入口；宿主未挂载时安全地返回 false。</summary>
public interface IConversationNavigator
{
    bool IsReady { get; }

    void AttachTarget(IConversationNavigationTarget target);

    void DetachTarget(IConversationNavigationTarget target);

    bool TryNavigateToConversation(string? historyId, string? conversationId);
}
