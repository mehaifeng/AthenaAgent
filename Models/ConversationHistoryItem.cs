using System;
using System.Collections.Generic;

namespace Athena.UI.Models;

/// <summary>
/// 对话历史条目
/// </summary>
public class ConversationHistoryItem
{
    /// <summary>
    /// 唯一标识
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// 对话摘要
    /// </summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>
    /// 创建时间
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>
    /// 最后更新时间
    /// </summary>
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    /// <summary>
    /// 消息数量
    /// </summary>
    public int MessageCount { get; set; }

    /// <summary>
    /// 消息列表
    /// </summary>
    public List<ChatMessage> Messages { get; set; } = new();

    /// <summary>
    /// 显示用的时间文本
    /// </summary>
    public string DisplayTime => UpdatedAt.ToString("yyyy-MM-dd HH:mm");
}
