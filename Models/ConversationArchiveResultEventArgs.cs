using System;

namespace Athena.UI.Models;

/// <summary>
/// 后台归档处理结果
/// </summary>
public class ConversationArchiveResultEventArgs : EventArgs
{
    public ConversationArchiveResultEventArgs(
        ConversationArchiveSnapshot snapshot,
        string stagedFilePath,
        ConversationHistoryItem? historyItem = null,
        Exception? exception = null)
    {
        Snapshot = snapshot;
        StagedFilePath = stagedFilePath;
        HistoryItem = historyItem;
        Exception = exception;
    }

    public ConversationArchiveSnapshot Snapshot { get; }

    public string StagedFilePath { get; }

    public ConversationHistoryItem? HistoryItem { get; }

    public Exception? Exception { get; }
}
