using Athena.UI.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Athena.UI.Services.Interfaces;

public interface IConversationArchiveStore
{
    Task<List<ConversationHistoryItem>> LoadAllAsync();
    Task<ConversationHistoryItem?> LoadByIdAsync(string id);
    Task SaveAsync(ConversationHistoryItem item);
    Task DeleteAsync(string id);
}

public interface IConversationDraftStore
{
    void Save(ConversationDraftSnapshot snapshot);
    ConversationDraftSnapshot? Load();
    void Delete();
}
