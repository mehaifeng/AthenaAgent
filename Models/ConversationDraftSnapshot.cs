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

    /// <summary>自动压缩的防抖门槛（token）；重启后据此继续生效。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public long AutoCompactionFloorTokens { get; set; }

    /// <summary>压缩刚提交、还在等第一次实测；为真时收到 usage 后重新评估防抖门槛。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool PostCompactionMeasurePending { get; set; }

    /// <summary>工具结果清理后还在等第一次实测。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool PostClearingMeasurePending { get; set; }

    /// <summary>清理后实测仍高于阈值，下一次超阈值直接全量压缩。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool CompactionDueAfterClearing { get; set; }

    /// <summary>
    /// 已被「工具结果清理」换成占位说明的工具消息 ID（只增不减）。它是会话状态而不是消息字段，
    /// 所以不放在 <c>ChatMessage</c> 上、也就不碰消息持久化白名单；null 表示从未清理过。
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? ClearedToolResultIds { get; set; }

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
