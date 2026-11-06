using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services;

public sealed class RepositoryBusyException() : InvalidOperationException("The repository is in use or its path cannot be resolved.");

/// <summary>Allows concurrent turns, but excludes branch mutations on their worktrees.
/// A global conversation conservatively excludes mutations on every repository.</summary>
public sealed class RepositoryActivityGate
{
    private readonly object _sync = new();
    private readonly Dictionary<string, int> _turns = new(PathComparer);
    private readonly HashSet<string> _mutations = new(PathComparer);
    private TaskCompletionSource _changed = NewSignal();
    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public async Task<IDisposable> EnterTurnAsync(string? directory, CancellationToken cancellationToken)
    {
        var root = ResolveWorktree(directory);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task wait;
            lock (_sync)
            {
                if (!_mutations.Any(path => Overlaps(root, path)))
                {
                    _turns[root] = _turns.GetValueOrDefault(root) + 1;
                    return new Lease(() => Release(root, false));
                }
                wait = _changed.Task;
            }
            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public IDisposable EnterMutation(string repositoryRoot)
    {
        var root = ResolveWorktree(repositoryRoot);
        lock (_sync)
        {
            if (_turns.Keys.Any(path => Overlaps(root, path)) || _mutations.Any(path => Overlaps(root, path)))
                throw new RepositoryBusyException();
            _mutations.Add(root);
            return new Lease(() => Release(root, true));
        }
    }

    private void Release(string root, bool mutation)
    {
        lock (_sync)
        {
            if (mutation) _mutations.Remove(root);
            else if (_turns[root] == 1) _turns.Remove(root);
            else _turns[root]--;
            var previous = _changed;
            _changed = NewSignal();
            previous.TrySetResult();
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static bool Overlaps(string left, string right) => left.Length == 0 || right.Length == 0
        || PathComparer.Equals(left, right)
        || left.StartsWith(Path.EndsInDirectorySeparator(right) ? right : right + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
        || right.StartsWith(Path.EndsInDirectorySeparator(left) ? left : left + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string ResolveWorktree(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return string.Empty;
        var fullPath = Path.GetFullPath(directory);
        if (FileSystemService.TryResolveFully(fullPath, out var resolved)) fullPath = resolved;
        else throw new RepositoryBusyException(); // Unknown aliases must not bypass turn exclusion.
        for (var current = new DirectoryInfo(fullPath); current != null; current = current.Parent)
            if (Directory.Exists(Path.Combine(current.FullName, ".git")) || File.Exists(Path.Combine(current.FullName, ".git")))
                return Path.TrimEndingDirectorySeparator(current.FullName);
        return Path.TrimEndingDirectorySeparator(fullPath);
    }

    private sealed class Lease(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
