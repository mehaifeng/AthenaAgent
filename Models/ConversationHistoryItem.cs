using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Athena.UI.Models;

/// <summary>
/// 对话历史条目
/// </summary>
public class ConversationHistoryItem
{
    public int SchemaVersion { get; set; } = ConversationPersistenceSnapshot.CurrentSchemaVersion;

    /// <summary>会话语义 Revision；存储层只允许更大的 Revision 覆盖。</summary>
    public long Revision { get; set; }

    public string ConversationId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// 唯一标识
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// 对话摘要 (对话标题)
    /// </summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>
    /// 上下文压缩摘要 (LLM 压缩后的结果)
    /// </summary>
    public string? ContextSummary { get; set; }

    public string? OrphanedLegacySummary { get; set; }

    public List<CompressionCheckpointRecord> CompressionHistory { get; set; } = new();

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
    /// fork 来源会话 ID（仅分支会话携带；null 表示普通会话）
    /// </summary>
    public string? ForkedFromConversationId { get; set; }

    /// <summary>
    /// fork 来源历史条目 ID（父会话 fork 时尚未归档过则为 null）
    /// </summary>
    public string? ForkedFromHistoryId { get; set; }

    /// <summary>
    /// fork 锚点：父会话中被 fork 的那条 user 消息的 Id
    /// </summary>
    public string? ForkedAtMessageId { get; set; }

    [JsonIgnore]
    public bool IsForked => !string.IsNullOrWhiteSpace(ForkedFromConversationId);

    /// <summary>
    /// 显示用的时间文本
    /// </summary>
    public string DisplayTime => UpdatedAt.ToString("yyyy-MM-dd HH:mm");

    [JsonIgnore]
    public bool IsArchivePlaceholder { get; set; }

    [JsonIgnore]
    public string? ArchiveStagePath { get; set; }

    [JsonIgnore]
    public string? ArchiveStatusText { get; set; }

    [JsonIgnore]
    public bool HasArchiveStatus => !string.IsNullOrWhiteSpace(ArchiveStatusText);

    [JsonIgnore]
    public bool AreActionsEnabled => !IsArchivePlaceholder;

    /// <summary>
    /// 所属工作区 ID（null 表示未绑定工作区）
    /// </summary>
    public string? WorkspaceId { get; set; }

    /// <summary>持续保存的输入草稿。</summary>
    public string Draft { get; set; } = string.Empty;

    public bool IsPinned { get; set; }

    /// <summary>退出时仍在运行的会话会以 interrupted 恢复，绝不自动重放。</summary>
    public string RuntimeStatus { get; set; } = "idle";
}
