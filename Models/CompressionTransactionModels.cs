using System;
using System.Collections.Generic;

namespace Athena.UI.Models;

public enum CompressionTriggerMode
{
    Auto,
    Manual
}

public enum CompressionPlanStatus
{
    Ready,
    NotCompressible
}

public sealed record CompressionAttachmentReference(
    string Id,
    AttachmentKind Kind,
    string FileName,
    string StoredPath,
    string MimeType,
    long SizeBytes,
    int Width,
    int Height);

public sealed record CompressionMaterialMessage(
    string Id,
    string Role,
    string Content,
    string? ToolCallId,
    string? ToolCallsJson,
    string? ReasoningContent,
    DateTime Timestamp,
    IReadOnlyList<CompressionAttachmentReference> Attachments);

/// <summary>
/// 压缩提示词版本。提示词（结构化九章节）与附录格式一并升级时 +1：
/// 校验按版本匹配候选与计划，旧检查点里记录的旧版本号仍可读。
/// </summary>
public static class CompressionPromptVersion
{
    /// <summary>1 = 按轮次窗口的自由格式摘要；2 = 全量压缩的结构化九章节摘要 + 代码追加的附录。</summary>
    public const int Current = 2;
}

public sealed record CompressionPlan(
    string PlanId,
    string ConversationId,
    long BaseRevision,
    string BaseContextFingerprint,
    CompressionTriggerMode TriggerMode,
    string? ExistingSummary,
    IReadOnlyList<string> CompressMessageIds,
    IReadOnlyList<string> RetainMessageIds,
    IReadOnlyList<CompressionMaterialMessage> Material,
    long PreCompressionTokens,
    long SummaryMaxTokens,
    ResolvedContextPolicy MainModelPolicy,
    ResolvedContextPolicy CompressionModelPolicy,
    int PromptVersion,
    string? FocusInstruction = null,
    IReadOnlyCollection<string>? ClearedToolResultIds = null);

public sealed record CompressionPlanRequest(
    string ConversationId,
    long BaseRevision,
    string BaseContextFingerprint,
    CompressionTriggerMode TriggerMode,
    string? ExistingSummary,
    IReadOnlyList<ChatMessage> Messages,
    long PreCompressionTokens,
    long RequestedSummaryMaxTokens,
    ResolvedContextPolicy MainModelPolicy,
    ResolvedContextPolicy CompressionModelPolicy,
    int PromptVersion = CompressionPromptVersion.Current,
    string? FocusInstruction = null,
    IReadOnlyCollection<string>? ClearedToolResultIds = null);

public sealed record CompressionPlanResult(
    CompressionPlanStatus Status,
    CompressionPlan? Plan,
    string Reason)
{
    public static CompressionPlanResult NotCompressible(string reason) =>
        new(CompressionPlanStatus.NotCompressible, null, reason);

    public static CompressionPlanResult Ready(CompressionPlan plan) =>
        new(CompressionPlanStatus.Ready, plan, string.Empty);
}

public enum CompressionGenerationStatus
{
    Generated,
    Failed,
    NotCompressible
}

public sealed record CompressionCandidate(
    string CandidateId,
    string PlanId,
    long BaseRevision,
    string Summary,
    string CompressionModelFingerprint,
    int PromptVersion,
    DateTimeOffset GeneratedAtUtc,
    bool UsedLocalFallback,
    // 摘要的 token 大小（压缩模型最终输出的实测 output tokens）。供应商不报 usage 则为 0。
    long SummaryTokens = 0);

public sealed record CompressionGenerationResult(
    CompressionGenerationStatus Status,
    CompressionCandidate? Candidate,
    string Error)
{
    public static CompressionGenerationResult Generated(CompressionCandidate candidate) =>
        new(CompressionGenerationStatus.Generated, candidate, string.Empty);

    public static CompressionGenerationResult Failed(string error) =>
        new(CompressionGenerationStatus.Failed, null, error);

    public static CompressionGenerationResult NotCompressible(string error) =>
        new(CompressionGenerationStatus.NotCompressible, null, error);
}

public enum CompressionValidationStatus
{
    Valid,
    Stale,
    Empty,
    InsufficientBenefit,
    MissingHardAnchors
}

/// <summary>
/// 摘要必须逐字带走的句柄：后续轮次要靠它去够到一个真实存在的东西（附件、磁盘上的文件）。
/// 只有这一类——路径、URL、错误码、数字这些是「过程痕迹」，它们的价值在于和"发生了什么"
/// 绑在一起被叙述出来，单独罗列成清单既占预算又无法推理，所以交给提示词，不做结构化保证。
/// 句柄由代码在摘要末尾结构化追加，不赌模型的复述能力。
/// </summary>
public sealed record CompressionHardAnchor(string Kind, string Value);

/// <param name="SummaryChars">候选摘要的字符数。</param>
/// <param name="MaterialChars">它所取代的材料的字符数；收益规则就是 <c>SummaryChars &lt; MaterialChars</c>。</param>
public sealed record CompressionValidationResult(
    CompressionValidationStatus Status,
    long SummaryChars,
    long MaterialChars,
    IReadOnlyList<CompressionHardAnchor> MissingHardAnchors,
    string Error)
{
    public bool IsValid => Status == CompressionValidationStatus.Valid;
}

public sealed record CompressionTransition(
    string PlanId,
    string CandidateId,
    string ConversationId,
    long BaseRevision,
    string BaseContextFingerprint,
    CompressionTriggerMode Mode,
    IReadOnlyList<string> MessageIds,
    string? SummaryBefore,
    string SummaryAfter,
    string CompressionModelFingerprint,
    int PromptVersion,
    long PreCompressionTokens,
    long PostCompressionTokens,
    bool UsedLocalFallback,
    // 摘要的 token 大小（压缩模型最终输出的实测 output tokens）；供应商不报则为 0。这是压缩后新内容的精确下界。
    long SummaryTokens = 0);

public sealed record CompressionUndoTransition(
    string CompressionId,
    string ConversationId,
    long BaseRevision,
    string BaseContextFingerprint,
    IReadOnlyList<string> MessageIds,
    string? SummaryBeforeUndo,
    string? SummaryAfterUndo);

public enum CompressionCommitStatus
{
    Committed,
    Stale,
    PersistenceUnavailable,
    PersistenceFailed
}

public sealed record CompressionCommitResult(
    CompressionCommitStatus Status,
    long Revision,
    string Error)
{
    public bool IsCommitted => Status == CompressionCommitStatus.Committed;

    public static CompressionCommitResult Committed(long revision) =>
        new(CompressionCommitStatus.Committed, revision, string.Empty);

    public static CompressionCommitResult Failed(CompressionCommitStatus status, long revision, string error) =>
        new(status, revision, error);
}
