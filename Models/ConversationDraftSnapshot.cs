using System;
using System.Collections.Generic;

namespace Athena.UI.Models;

/// <summary>
/// 主聊天页的未归档对话快照
/// </summary>
public class ConversationDraftSnapshot
{
    public string ConversationId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// 当前关联的历史记录 ID；若为新对话则为空
    /// </summary>
    public string? CurrentHistoryId { get; set; }

    /// <summary>
    /// 加载历史时的初始签名，用于判断后续是否被修改
    /// </summary>
    public string? InitialConversationSignature { get; set; }

    /// <summary>
    /// 上下文压缩摘要
    /// </summary>
    public string? ContextSummary { get; set; }

    /// <summary>
    /// 当前消息列表
    /// </summary>
    public List<ChatMessage> Messages { get; set; } = new();

    /// <summary>
    /// 最后保存时间
    /// </summary>
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}
