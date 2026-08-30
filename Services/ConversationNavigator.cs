using Athena.UI.Services.Interfaces;
using Serilog;
using System;

namespace Athena.UI.Services;

/// <summary>
/// 会话跳转的运行期中介。主窗口构造时挂载自己，销毁时摘下；
/// 未挂载期间所有跳转请求安全失败，不抛异常也不留悬挂引用。
/// </summary>
public sealed class ConversationNavigator : IConversationNavigator
{
    private readonly ILogger _logger;
    private IConversationNavigationTarget? _target;

    public ConversationNavigator(ILogger logger)
    {
        _logger = logger.ForContext<ConversationNavigator>();
    }

    public bool IsReady => _target != null;

    public void AttachTarget(IConversationNavigationTarget target)
        => _target = target ?? throw new ArgumentNullException(nameof(target));

    public void DetachTarget(IConversationNavigationTarget target)
    {
        if (ReferenceEquals(_target, target)) _target = null;
    }

    public bool TryNavigateToConversation(string? historyId, string? conversationId)
    {
        var target = _target;
        if (target == null)
        {
            _logger.Debug("Conversation navigation requested before the main window attached");
            return false;
        }

        try
        {
            return target.TryNavigateToConversation(historyId, conversationId);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to navigate to conversation {HistoryId}/{ConversationId}", historyId, conversationId);
            return false;
        }
    }
}
