using System;
using System.Collections.Generic;

namespace Athena.UI.Models;

/// <summary>
/// 待归档对话的不可变快照
/// </summary>
public class ConversationArchiveSnapshot
{
    public string ConversationId { get; init; } = Guid.NewGuid().ToString("N");

    public string? HistoryId { get; init; }

    public string? ContextSummary { get; init; }

    public string? ForkedFromConversationId { get; init; }

    public string? ForkedFromHistoryId { get; init; }

    public string? ForkedAtMessageId { get; init; }

    public List<ChatMessage> Messages { get; init; } = new();

    public ImageGenerationSessionSnapshot? ImageSession { get; init; }

    public DateTime CapturedAt { get; init; } = DateTime.Now;

    public bool ForceGenerateSummary { get; init; }
}
