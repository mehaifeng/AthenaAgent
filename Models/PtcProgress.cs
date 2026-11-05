namespace Athena.UI.Models;

/// <summary>Metadata-only child progress, never a provider message or a persisted message field.</summary>
public sealed record PtcProgress(string RunId, string ParentToolCallId, string? ConversationId,
    long Sequence, string Kind, string? CallId, string? ToolName, string? ErrorCode)
{
    /// <summary>Source Stopwatch timestamp, not the delayed UI/observer delivery time.</summary>
    public long OccurredTimestamp { get; init; }
    public string? CallState { get; init; }
    public string? RunStatus { get; init; }
}
