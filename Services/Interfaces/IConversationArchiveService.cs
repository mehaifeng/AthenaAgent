using System;
using System.Threading;
using System.Threading.Tasks;
using Athena.UI.Models;

namespace Athena.UI.Services.Interfaces;

public interface IConversationArchiveService
{
    Task StageArchiveAsync(ConversationArchiveSnapshot snapshot, CancellationToken ct = default);

    event EventHandler<ConversationArchiveResultEventArgs>? ArchiveStaged;

    event EventHandler<ConversationArchiveResultEventArgs>? ArchiveCompleted;

    event EventHandler<ConversationArchiveResultEventArgs>? ArchiveFailed;
}
