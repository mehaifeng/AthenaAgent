using System;

namespace Athena.UI.Services.Interfaces;

public interface IConversationSessionAccessor
{
    string? CurrentConversationId { get; }

    string? CurrentWorkspaceId { get; }

    IDisposable Enter(string conversationId);

    IDisposable EnterWorkspace(string? workspaceId);
}
