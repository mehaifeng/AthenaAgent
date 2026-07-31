using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.Interfaces;

public sealed class TerminalSessionsChangedEventArgs(string scopeKey) : EventArgs
{
    public string ScopeKey { get; } = scopeKey;
}

public interface ITerminalSessionManager : IAsyncDisposable
{
    event EventHandler<TerminalSessionsChangedEventArgs>? SessionsChanged;

    IReadOnlyList<TerminalSession> GetSessions(string scopeKey);

    Task<TerminalSession> CreateAsync(
        string scopeKey,
        string workingDirectory,
        CancellationToken cancellationToken = default);

    Task CloseAsync(string scopeKey, string sessionId);

    Task CloseOthersAsync(string scopeKey, string sessionId);

    Task CloseAllAsync(string scopeKey);
}
