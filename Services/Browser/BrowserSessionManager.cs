using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.Browser;

public class BrowserSessionManager : IBrowserSessionManager
{
    private readonly ConcurrentDictionary<string, BrowserSessionInfo> _sessions = new();
    private readonly ILogger _logger;

    public BrowserSessionManager(ILogger logger)
    {
        _logger = logger.ForContext<BrowserSessionManager>();
    }

    public IReadOnlyCollection<BrowserSessionInfo> ActiveSessions => _sessions.Values.ToList();

    public Task<BrowserSessionInfo> CreateAsync(BrowserSessionOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var session = new BrowserSessionInfo
        {
            SessionId = Guid.NewGuid().ToString("N"),
            CreatedAt = DateTime.Now,
            LastAccessedAt = DateTime.Now,
            IsPersistent = options.PersistSession,
            SessionTtlMinutes = Math.Max(1, options.SessionTtlMinutes)
        };

        _sessions[session.SessionId] = session;
        _logger.Information("Browser session created: {SessionId}", session.SessionId);
        return Task.FromResult(session);
    }

    public Task<BrowserSessionInfo?> GetAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _sessions.TryGetValue(sessionId, out var session);
        return Task.FromResult(session);
    }

    public Task TouchAsync(string sessionId, string? currentUrl = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_sessions.TryGetValue(sessionId, out var session))
        {
            session.LastAccessedAt = DateTime.Now;
            if (!string.IsNullOrWhiteSpace(currentUrl))
            {
                session.CurrentUrl = currentUrl;
            }
        }

        return Task.CompletedTask;
    }

    public Task CloseAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_sessions.TryRemove(sessionId, out _))
        {
            _logger.Information("Browser session removed: {SessionId}", sessionId);
        }

        return Task.CompletedTask;
    }

    public async Task<int> CleanupExpiredAsync(CancellationToken cancellationToken = default)
    {
        var expiredIds = await GetExpiredSessionIdsAsync(cancellationToken);
        foreach (var sessionId in expiredIds)
        {
            await CloseAsync(sessionId, cancellationToken);
        }

        if (expiredIds.Count > 0)
        {
            _logger.Information("BrowserSessionManager cleaned up expired sessions: Count={Count}", expiredIds.Count);
        }
        return expiredIds.Count;
    }

    public Task<IReadOnlyList<string>> GetExpiredSessionIdsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var now = DateTime.Now;
        var expiredIds = _sessions.Values
            .Where(session => now - session.LastAccessedAt > TimeSpan.FromMinutes(Math.Max(1, session.SessionTtlMinutes)))
            .Select(session => session.SessionId)
            .ToList();

        return Task.FromResult<IReadOnlyList<string>>(expiredIds);
    }
}
