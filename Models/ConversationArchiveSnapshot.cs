using System;

namespace Athena.UI.Models;

/// <summary>
/// 待归档对话的不可变快照
/// </summary>
public class ConversationArchiveSnapshot : ConversationPersistenceSnapshot
{
    public DateTime CapturedAt { get; init; } = DateTime.Now;

    public bool ForceGenerateSummary { get; init; }
}
