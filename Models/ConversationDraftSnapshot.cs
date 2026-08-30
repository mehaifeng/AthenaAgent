using System;
using System.Collections.Generic;

namespace Athena.UI.Models;

/// <summary>
/// 主聊天页的未归档对话快照
/// </summary>
public class ConversationDraftSnapshot
{
    public int SchemaVersion { get; set; } = ConversationPersistenceSnapshot.CurrentSchemaVersion;

    public long Revision { get; set; }

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

    public string? OrphanedLegacySummary { get; set; }

    public List<CompressionCheckpointRecord> CompressionHistory { get; set; } = new();

    /// <summary>供应商回报的真实用量锚点；重启后据此复用精确测量，无需重新估算整段上下文。</summary>
    public List<ContextAnchorRecord> Anchors { get; set; } = new();

    /// <summary>
    /// fork 元数据：当前会话若是分支，重启后仍需携带
    /// </summary>
    public string? ForkedFromConversationId { get; set; }

    public string? ForkedFromHistoryId { get; set; }

    public string? ForkedAtMessageId { get; set; }

    /// <summary>
    /// cron 溯源元数据：定时触发产生的会话重启后仍需携带
    /// </summary>
    public string? CreatedByCronTaskId { get; set; }

    public string? CronTaskRunId { get; set; }

    public DateTimeOffset? ScheduledFiredAt { get; set; }

    /// <summary>
    /// 当前消息列表
    /// </summary>
    public List<ChatMessage> Messages { get; set; } = new();

    /// <summary>
    /// 最后保存时间
    /// </summary>
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}
