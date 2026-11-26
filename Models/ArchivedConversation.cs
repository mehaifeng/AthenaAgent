using System;

namespace Athena.UI.Models;

/// <summary>Directory entry for the conversation tree. Opening the payload is explicit.</summary>
public sealed record ArchivedConversation(string Id, string ConversationId, string? WorkspaceId, string Summary,
    DateTime UpdatedAt, bool IsPinned, string RuntimeStatus, string? ForkedFromConversationId,
    string? ForkedFromHistoryId, string? CreatedByCronTaskId, string? CronTaskRunId,
    Func<ConversationHistoryItem> Read)
{
    internal bool OwnsReadMessages { get; init; }
    internal ConversationHistoryItem? Prepared { get; init; }
    public static ArchivedConversation FromHistory(ConversationHistoryItem item) => new(
        item.Id, item.ConversationId, item.WorkspaceId, item.Summary, item.UpdatedAt, item.IsPinned,
        item.RuntimeStatus, item.ForkedFromConversationId, item.ForkedFromHistoryId,
        item.CreatedByCronTaskId, item.CronTaskRunId, () => item) { Prepared = item };
}
