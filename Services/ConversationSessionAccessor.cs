using System;
using System.Threading;
using Athena.UI.Services.Interfaces;

namespace Athena.UI.Services;

public sealed class ConversationSessionAccessor : IConversationSessionAccessor
{
    private readonly AsyncLocal<string?> _currentConversationId = new();
    private readonly AsyncLocal<string?> _currentWorkspaceId = new();

    public string? CurrentConversationId => _currentConversationId.Value;

    public string? CurrentWorkspaceId => _currentWorkspaceId.Value;

    public IDisposable Enter(string conversationId)
    {
        var previous = _currentConversationId.Value;
        _currentConversationId.Value = conversationId;
        return new Scope(this, previous);
    }

    public IDisposable EnterWorkspace(string? workspaceId)
    {
        var previous = _currentWorkspaceId.Value;
        _currentWorkspaceId.Value = workspaceId;
        return new WorkspaceScope(this, previous);
    }

    private sealed class Scope(ConversationSessionAccessor owner, string? previousConversationId) : IDisposable
    {
        public void Dispose()
        {
            owner._currentConversationId.Value = previousConversationId;
        }
    }

    private sealed class WorkspaceScope(ConversationSessionAccessor owner, string? previousWorkspaceId) : IDisposable
    {
        public void Dispose()
        {
            owner._currentWorkspaceId.Value = previousWorkspaceId;
        }
    }
}
