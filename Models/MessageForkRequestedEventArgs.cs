using System;

namespace Athena.UI.Models;

/// <summary>
/// 消息级分支请求：由会话 ViewModel 触发、会话项转发、窗口 ViewModel 执行。
/// </summary>
public sealed class MessageForkRequestedEventArgs : EventArgs
{
    public MessageForkRequestedEventArgs(ChatMessage message) => Message = message;

    public ChatMessage Message { get; }
}
