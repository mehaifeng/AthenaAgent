using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Athena.UI.Models;

namespace Athena.UI.Services.Interfaces;

public interface IConversationArchiveService
{
    Task<List<ConversationHistoryItem>> LoadAllAsync();
    Task<ConversationHistoryItem?> LoadByIdAsync(string id);
    Task DeleteAsync(string id);
    void SaveDraft(ConversationDraftSnapshot snapshot);
    ConversationDraftSnapshot? LoadDraft();
    void DeleteDraft();

    Task StageArchiveAsync(ConversationArchiveSnapshot snapshot, CancellationToken ct = default);

    event EventHandler<ConversationArchiveResultEventArgs>? ArchiveStaged;

    event EventHandler<ConversationArchiveResultEventArgs>? ArchiveCompleted;

    event EventHandler<ConversationArchiveResultEventArgs>? ArchiveFailed;
}
