using Athena.UI.Models;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Athena.UI.Services.Interfaces;

public interface IConversationArchiveStore
{
    Task<List<ConversationHistoryItem>> LoadAllAsync();
    Task<ConversationHistoryItem?> LoadByIdAsync(string id);
    Task SaveAsync(ConversationHistoryItem item);

    /// <summary>
    /// 普通保存通道：落库并返回真正写入的 revision。与 <see cref="SaveAsync"/> 的差别只在
    /// 「同 revision、不同 payload」——那是流式就地追加造成的正常漂移，顺延一个 revision 写入，
    /// 而不是当成写入者冲突抛出。真正的过期写入（incoming 小于 stored）仍然抛出。
    /// </summary>
    Task<long> SaveLatestAsync(ConversationHistoryItem item);
    Task DeleteAsync(string id);
}

public interface IConversationDraftStore
{
    void Save(ConversationDraftSnapshot snapshot);
    ConversationDraftSnapshot? Load();
    void Delete();
}
