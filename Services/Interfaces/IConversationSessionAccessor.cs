using System;

namespace Athena.UI.Services.Interfaces;

public interface IConversationSessionAccessor
{
    string? CurrentConversationId { get; }

    IDisposable Enter(string conversationId);
}
