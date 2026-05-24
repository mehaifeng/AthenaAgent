using System;
using System.Threading;
using Athena.UI.Services.Interfaces;

namespace Athena.UI.Services;

public sealed class ConversationSessionAccessor : IConversationSessionAccessor
{
    private readonly AsyncLocal<string?> _currentConversationId = new();

    public string? CurrentConversationId => _currentConversationId.Value;

    public IDisposable Enter(string conversationId)
    {
        var previous = _currentConversationId.Value;
        _currentConversationId.Value = conversationId;
        return new Scope(this, previous);
    }

    private sealed class Scope(ConversationSessionAccessor owner, string? previousConversationId) : IDisposable
    {
        public void Dispose()
        {
            owner._currentConversationId.Value = previousConversationId;
        }
    }
}
