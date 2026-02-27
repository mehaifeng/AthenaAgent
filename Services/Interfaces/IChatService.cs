using Athena.UI.Models;
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
    /// 发送消息并获取流式响应
    /// </summary>
    /// <param name="userMessage">用户消息</param>
    /// <param name="context">对话上下文</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>流式响应枚举器</returns>
    IAsyncEnumerable<string> StreamMessageAsync(
        string userMessage,
        ConversationContext context,
        CancellationToken cancellationToken = default);

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
