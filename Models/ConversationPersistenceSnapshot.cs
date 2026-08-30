using System;
using System.Collections.Generic;

namespace Athena.UI.Models;

/// <summary>
/// 会话持久化的统一不可变投影。所有保存路径必须从同一次捕获中取得消息、摘要、
/// 压缩记录、Fork 身份和 Revision，避免分别挑字段形成半状态。
/// </summary>
public class ConversationPersistenceSnapshot
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public string ConversationId { get; set; } = Guid.NewGuid().ToString("N");

    public string? HistoryId { get; set; }

    public long Revision { get; set; }

    public string Title { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    public string? ContextSummary { get; set; }

    public string? OrphanedLegacySummary { get; set; }

    public List<CompressionCheckpointRecord> CompressionHistory { get; set; } = new();

    /// <summary>供应商回报的真实用量锚点；回溯/分支/重开会话时据此复用精确测量。</summary>
    public List<ContextAnchorRecord> Anchors { get; set; } = new();

    public string? ForkedFromConversationId { get; set; }

    public string? ForkedFromHistoryId { get; set; }

    public string? ForkedAtMessageId { get; set; }

    /// <summary>创建本会话的 cron 任务 ID（仅定时触发的会话携带；null 表示普通会话）。</summary>
    public string? CreatedByCronTaskId { get; set; }

    /// <summary>对应的那一次 cron 运行 ID，用于从任务运行记录跳回这个会话。</summary>
    public string? CronTaskRunId { get; set; }

    /// <summary>该次 cron 触发的计划时刻（UTC）。手动运行为 null。</summary>
    public DateTimeOffset? ScheduledFiredAt { get; set; }

    public List<ChatMessage> Messages { get; set; } = new();

    public ImageGenerationSessionSnapshot? ImageSession { get; set; }

    public string? WorkspaceId { get; set; }

    public string Draft { get; set; } = string.Empty;

    public bool IsPinned { get; set; }

    public string RuntimeStatus { get; set; } = "idle";
}

/// <summary>可持久化的压缩检查点；消息以稳定 ID 引用。</summary>
public sealed class CompressionCheckpointRecord
{
    public string CompressionId { get; set; } = Guid.NewGuid().ToString("N");

    public long AppliedRevision { get; set; }

    public List<string> MessageIds { get; set; } = new();

    public string? SummaryBefore { get; set; }

    public string? SummaryAfter { get; set; }

    public string SummaryAfterHash { get; set; } = string.Empty;

    public CompressionTriggerMode Mode { get; set; } = CompressionTriggerMode.Manual;

    public string CompressionModelFingerprint { get; set; } = string.Empty;

    public int PromptVersion { get; set; } = 1;

    public long PreCompressionTokens { get; set; }

    public long PostCompressionTokens { get; set; }

    public bool UsedLocalFallback { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
