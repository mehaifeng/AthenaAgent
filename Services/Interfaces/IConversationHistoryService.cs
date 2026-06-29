using Athena.UI.Models;
using System.Collections.Generic;
using System.Threading;
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
    /// 从对话快照创建或更新历史条目
    /// </summary>
    Task<ConversationHistoryItem> UpsertFromSnapshotAsync(ConversationArchiveSnapshot snapshot, CancellationToken ct = default);

    /// <summary>
    /// 生成对话摘要
    /// </summary>
    Task<string> GenerateSummaryAsync(List<ChatMessage> messages);

    /// <summary>
    /// 根据 ID 加载对话历史
    /// </summary>
    Task<ConversationHistoryItem?> LoadByIdAsync(string id);

    /// <summary>
    /// 删除与对话关联的图像生成会话
    /// </summary>
    Task DeleteImageSessionAsync(string? conversationId);

    /// <summary>
    /// 保存主聊天页的未归档对话快照
    /// </summary>
    void SaveDraft(ConversationDraftSnapshot snapshot);

    /// <summary>
    /// 加载主聊天页的未归档对话快照
    /// </summary>
    ConversationDraftSnapshot? LoadDraft();

    /// <summary>
    /// 删除主聊天页的未归档对话快照
    /// </summary>
    void DeleteDraft();

    /// <summary>
    /// 更新次级模型配置
    /// </summary>
    void UpdateSecondaryConfig(AppConfig config);

    /// <summary>
    /// 压缩对话上下文，将旧消息（连同已有摘要）滚动合并为一份新摘要
    /// </summary>
    /// <param name="messages">当前消息列表</param>
    /// <param name="existingSummary">当前会话已有的摘要（带前缀），会被合并进新摘要以避免历史丢失；无则传 null</param>
    /// <param name="keepRecentRounds">保留最近的对话轮次（1轮 = 1个 User 消息及其后的所有 Assistant/Tool 消息）</param>
    /// <returns>压缩结果；若无需压缩返回 <see cref="CompressionResult.None"/></returns>
    Task<CompressionResult> CompressContextAsync(List<ChatMessage> messages, string? existingSummary, int keepRecentRounds = 3);

    /// <summary>
    /// 测试次级模型连接
    /// </summary>
    Task<(bool Success, string Message)> TestSecondaryConnectionAsync();
}
