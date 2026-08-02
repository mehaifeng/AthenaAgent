using Athena.UI.Models;
using Athena.UI.Services.Context;
using System.Collections.Generic;

namespace Athena.UI.Services.Interfaces;

public interface IContextRequestPreparer
{
    PreparedChatRequest Prepare(
        EffectiveRequestRuntimeSnapshot runtime,
        IReadOnlyList<OpenAI.Chat.ChatMessage> messages,
        ConversationContext context,
        string requestId,
        long conversationRevision = 0,
        bool imageBinaryIncluded = true,
        bool isImageFallback = false);
}
