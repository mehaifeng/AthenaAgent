using Athena.UI.Services.Interfaces;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services;

public sealed class TerminalSessionManager : ITerminalSessionManager
{
    private readonly object _sync = new();
    private readonly Dictionary<string, List<TerminalSession>> _sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _nextNumbers = new(StringComparer.Ordinal);
    private readonly ILogger _logger;
    private bool _disposed;

    public TerminalSessionManager(ILogger logger)
    {
        _logger = logger.ForContext<TerminalSessionManager>();
    }

    public event EventHandler<TerminalSessionsChangedEventArgs>? SessionsChanged;

    public IReadOnlyList<TerminalSession> GetSessions(string scopeKey)
    {
        lock (_sync)
        {
            return _sessions.TryGetValue(scopeKey, out var sessions)
                ? sessions.ToArray()
                : [];
        }
    }

    public async Task<TerminalSession> CreateAsync(
        string scopeKey,
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var directory = ResolveWorkingDirectory(workingDirectory);
        int number;
        lock (_sync)
        {
            number = _nextNumbers.TryGetValue(scopeKey, out var current) ? current + 1 : 1;
            _nextNumbers[scopeKey] = number;
        }

        var shellPrefix = OperatingSystem.IsWindows()
            ? "PS"
            : Path.GetFileName(Environment.GetEnvironmentVariable("SHELL")) switch
            {
                { Length: > 0 } shell => shell,
                _ when OperatingSystem.IsMacOS() => "zsh",
                _ => "bash"
            };
        var session = await TerminalSession.StartAsync(
            scopeKey,
            $"{shellPrefix} {number}",
            directory,
            _logger,
            cancellationToken);
        session.Exited += OnSessionExited;

        lock (_sync)
        {
            if (!_sessions.TryGetValue(scopeKey, out var sessions))
            {
                sessions = [];
                _sessions[scopeKey] = sessions;
            }
            sessions.Add(session);
        }

        _logger.Information(
            "Terminal created: {TerminalName}, Scope={Scope}, WorkingDirectory={WorkingDirectory}, PID={ProcessId}",
            session.Name,
            scopeKey,
            directory,
            session.ProcessId);
        RaiseSessionsChanged(scopeKey);
        return session;
    }

    public async Task CloseAsync(string scopeKey, string sessionId)
    {
        var removed = RemoveSessions(
            scopeKey,
            session => string.Equals(session.Id, sessionId, StringComparison.Ordinal));
        await DisposeSessionsAsync(removed);
        if (removed.Count > 0) RaiseSessionsChanged(scopeKey);
    }

    public async Task CloseOthersAsync(string scopeKey, string sessionId)
    {
        var removed = RemoveSessions(
            scopeKey,
            session => !string.Equals(session.Id, sessionId, StringComparison.Ordinal));
        await DisposeSessionsAsync(removed);
        if (removed.Count > 0) RaiseSessionsChanged(scopeKey);
    }

    public async Task CloseAllAsync(string scopeKey)
    {
        var removed = RemoveSessions(scopeKey, _ => true);
        await DisposeSessionsAsync(removed);
        if (removed.Count > 0) RaiseSessionsChanged(scopeKey);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        List<TerminalSession> all;
        lock (_sync)
        {
            all = _sessions.Values.SelectMany(sessions => sessions).ToList();
            _sessions.Clear();
            _nextNumbers.Clear();
        }
        await DisposeSessionsAsync(all);
    }

    private void OnSessionExited(object? sender, EventArgs e)
        => AsyncEventGuard.Run(() => OnSessionExitedAsync(sender, e), nameof(OnSessionExited));

    private async Task OnSessionExitedAsync(object? sender, EventArgs e)
    {
        if (sender is not TerminalSession session) return;
        session.Exited -= OnSessionExited;
        var removed = RemoveSessions(
            session.ScopeKey,
            candidate => ReferenceEquals(candidate, session));
        await DisposeSessionsAsync(removed);
        if (removed.Count > 0) RaiseSessionsChanged(session.ScopeKey);
    }

    private List<TerminalSession> RemoveSessions(
        string scopeKey,
        Func<TerminalSession, bool> predicate)
    {
        lock (_sync)
        {
            if (!_sessions.TryGetValue(scopeKey, out var sessions)) return [];
            var removed = sessions.Where(predicate).ToList();
            foreach (var session in removed)
            {
                session.Exited -= OnSessionExited;
                sessions.Remove(session);
            }
            if (sessions.Count == 0) _sessions.Remove(scopeKey);
            return removed;
        }
    }

    private static async Task DisposeSessionsAsync(IEnumerable<TerminalSession> sessions)
    {
        await Task.WhenAll(sessions.Select(session => session.DisposeAsync().AsTask()));
    }

    private void RaiseSessionsChanged(string scopeKey) =>
        SessionsChanged?.Invoke(this, new TerminalSessionsChangedEventArgs(scopeKey));

    private static string ResolveWorkingDirectory(string workingDirectory)
    {
        if (!string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory))
            return Path.GetFullPath(workingDirectory);

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile) && Directory.Exists(userProfile))
            return userProfile;

        var environmentHome = OperatingSystem.IsWindows()
            ? Environment.GetEnvironmentVariable("USERPROFILE")
            : Environment.GetEnvironmentVariable("HOME");
        return !string.IsNullOrWhiteSpace(environmentHome) && Directory.Exists(environmentHome)
            ? environmentHome
            : Environment.CurrentDirectory;
    }
}
