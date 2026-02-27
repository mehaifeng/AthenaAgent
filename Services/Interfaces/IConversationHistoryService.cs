using Athena.UI.Models;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Athena.UI.Services.Interfaces;

/// <summary>
/// 对话历史服务接口
/// </summary>
public interface IConversationHistoryService
{
    /// <summary>
    /// 加载所有对话历史
    /// </summary>
    Task<List<ConversationHistoryItem>> LoadAllAsync();

    /// <summary>
    /// 保存对话历史
    /// </summary>
    Task SaveAsync(ConversationHistoryItem item);

    /// <summary>
    /// 删除对话历史
    /// </summary>
    Task DeleteAsync(string id);

    /// <summary>
    /// 从消息集合创建对话历史条目
    /// </summary>
    Task<ConversationHistoryItem> CreateFromMessagesAsync(ObservableCollection<ChatMessage> messages, bool forceGenerateSummary = false);

    /// <summary>
    /// 生成对话摘要
    /// </summary>
    Task<string> GenerateSummaryAsync(List<ChatMessage> messages);

    /// <summary>
    /// 根据 ID 加载对话历史
    /// </summary>
    Task<ConversationHistoryItem?> LoadByIdAsync(string id);

    /// <summary>
    /// 更新次级模型配置
    /// </summary>
    void UpdateSecondaryConfig(AppConfig config);

    /// <summary>
    /// 压缩对话上下文，将旧消息生成摘要
    /// </summary>
    /// <param name="messages">当前消息列表</param>
    /// <param name="keepRecentCount">保留最近的消息数量</param>
    /// <returns>压缩后的摘要文本，如果无需压缩则返回 null</returns>
    Task<string?> CompressContextAsync(List<ChatMessage> messages, int keepRecentCount = 10);
}
