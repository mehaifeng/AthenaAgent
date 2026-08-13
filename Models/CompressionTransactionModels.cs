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
    long PreCompressionEstimate,
    long TargetSummaryTokens,
    ResolvedContextPolicy MainModelPolicy,
    ResolvedContextPolicy CompressionModelPolicy,
    int PromptVersion);

public sealed record CompressionPlanRequest(
    string ConversationId,
    long BaseRevision,
    string BaseContextFingerprint,
    CompressionTriggerMode TriggerMode,
    string? ExistingSummary,
    IReadOnlyList<ChatMessage> Messages,
    int KeepRecentRounds,
    long PreCompressionEstimate,
    long RequestedTargetSummaryTokens,
    ResolvedContextPolicy MainModelPolicy,
    ResolvedContextPolicy CompressionModelPolicy,
    int PromptVersion = 1);

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
    bool UsedLocalFallback);

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
    OverBudget,
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

/// <summary>
/// 压缩可行性判定的结果。全部由本地字符统计得出，不需要任何模型调用——
/// 一次注定失败的尝试要花 20–175 秒和一次完整计费，必须在发请求之前就挡下来。
/// </summary>
public sealed record CompressionFeasibilityVerdict(
    bool IsFeasible,
    string Reason,
    long MaterialTokens,
    long TargetTokens,
    double RequiredRatio,
    int HandleAnchorCount,
    long HandleAnchorTokens,
    long ProjectedBenefitTokens);

public sealed record CompressionValidationResult(
    CompressionValidationStatus Status,
    long SummaryTokens,
    long PostCompressionEstimate,
    long EstimatedBenefitTokens,
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
    bool UsedLocalFallback);

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
