using System;
using System.Collections.Generic;

namespace Athena.UI.Models;

/// <summary>
/// 待归档对话的不可变快照
/// </summary>
public class ConversationArchiveSnapshot
{
    public string? HistoryId { get; init; }

    public string? ContextSummary { get; init; }

    public List<ChatMessage> Messages { get; init; } = new();

    public DateTime CapturedAt { get; init; } = DateTime.Now;

    public bool ForceGenerateSummary { get; init; }
}
