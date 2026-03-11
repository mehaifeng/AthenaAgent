using Athena.UI.Models;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.Interfaces;

/// <summary>
/// AI 对话服务接口
/// </summary>
public interface IChatService
{
    /// <summary>
    /// 流式发送消息
    /// </summary>
    /// <param name="userMessage">用户消息</param>
    /// <param name="context">对话上下文</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <param name="onMessageAdded">当产生中间消息（如工具结果）时的回调</param>
    /// <returns>AI 响应文本流</returns>
    IAsyncEnumerable<string> StreamMessageAsync(
        string userMessage,
        ConversationContext context,
        CancellationToken cancellationToken = default,
        Action<ChatMessage>? onMessageAdded = null,
        bool addToContext = true);

    /// <summary>
    /// 测试 API 连接
    /// </summary>
    /// <returns>是否连接成功</returns>
    Task<(bool Success, string Message)> TestConnectionAsync();

    /// <summary>
    /// 更新配置
    /// </summary>
    void UpdateConfig(AppConfig config);
}
