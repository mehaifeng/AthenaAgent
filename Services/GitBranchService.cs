using Athena.UI.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services;

public sealed class GitBranchException(string code, string? detail = null) : Exception(detail ?? code)
{
    public string Code { get; } = code;
}

/// <summary>Local Git operations only. No shell, fetch, force, stash, reset or remote deletion.</summary>
public sealed class GitBranchService(RepositoryActivityGate activity)
{
    public async Task<GitBranchSnapshot> ReadAsync(string directory, CancellationToken cancellationToken = default)
    {
        var root = (await RunAsync(directory, cancellationToken, "rev-parse", "--show-toplevel")).TrimEnd('\r', '\n');
        var symbolic = await ProbeAsync(root, cancellationToken, "symbolic-ref", "--quiet", "HEAD");
        var currentRef = symbolic.ExitCode == 0 ? symbolic.Output.Trim() : null;
        var head = await ProbeAsync(root, cancellationToken, "rev-parse", "--verify", "HEAD");
        var hasCommits = head.ExitCode == 0;
        var currentName = currentRef?.Replace("refs/heads/", "", StringComparison.Ordinal)
            ?? (hasCommits ? $"HEAD@{head.Output.Trim()[..7]}" : "HEAD");
        var remoteHead = await ProbeAsync(root, cancellationToken, "symbolic-ref", "--quiet", "refs/remotes/origin/HEAD");
        var defaultRemote = remoteHead.ExitCode == 0 ? remoteHead.Output.Trim() : null;
        var defaultLocal = defaultRemote?.Replace("refs/remotes/origin/", "refs/heads/", StringComparison.Ordinal);
        var output = await RunAsync(root, cancellationToken, "for-each-ref",
            "--sort=-committerdate", "--format=%(refname)%00%(objectname)%00%(subject)%00%(committerdate:iso-strict)%00%(upstream)%00%(upstream:track,nobracket)%00%(symref)", "refs/heads", "refs/remotes");
        var records = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r').Split('\0')).Where(fields => fields.Length == 7 && fields[6].Length == 0).ToList();
        var baseRef = defaultRemote != null && records.Any(f => f[0] == defaultRemote) ? defaultRemote
            : records.Any(f => f[0] == "refs/heads/main") ? "refs/heads/main"
            : records.Any(f => f[0] == "refs/heads/master") ? "refs/heads/master" : currentRef;
        var merged = new HashSet<string>(StringComparer.Ordinal);
        if (hasCommits && baseRef != null)
        {
            var result = await RunAsync(root, cancellationToken, "for-each-ref", $"--merged={baseRef}", "--format=%(refname)", "refs/heads");
            merged.UnionWith(result.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
        }
        var worktrees = ParseWorktrees(await RunAsync(root, cancellationToken, "worktree", "list", "--porcelain", "-z"));
        var branches = records.Select(f => new GitBranchInfo(f[0], f[0].StartsWith("refs/heads/", StringComparison.Ordinal) ? f[0][11..] : f[0][13..],
            f[1], f[2], DateTimeOffset.TryParse(f[3], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null,
            f[4], f[5], f[0].StartsWith("refs/remotes/", StringComparison.Ordinal), f[0] == currentRef,
            f[0] == defaultLocal || f[0] == defaultRemote || f[0] == baseRef, merged.Contains(f[0]), worktrees.GetValueOrDefault(f[0]))).ToList();
        var dirty = await RunAsync(root, cancellationToken, "status", "--porcelain=v1", "-z", "--untracked-files=all", "--ignore-submodules=none");
        var gitDirectory = (await RunAsync(root, cancellationToken, "rev-parse", "--absolute-git-dir")).TrimEnd('\r', '\n');
        var operation = new[] { "MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD", "rebase-merge", "rebase-apply", "sequencer", "BISECT_LOG" }
            .Any(name => File.Exists(Path.Combine(gitDirectory, name)) || Directory.Exists(Path.Combine(gitDirectory, name)));
        return new(root, currentName, currentRef, baseRef, hasCommits, dirty.Length != 0, operation, branches);
    }

    public async Task SwitchAsync(string directory, GitBranchInfo branch, string localName, CancellationToken ct = default)
    {
        using var lease = EnterMutation(directory);
        var snapshot = await ReadAsync(directory, ct);
        EnsureCanChange(snapshot, requireClean: true);
        var fresh = RequireUnchanged(snapshot, branch);
        if (fresh.IsCurrent) return;
        if (fresh.WorktreePath != null) throw new GitBranchException("Occupied", fresh.WorktreePath);
        if (fresh.IsRemote)
        {
            await ValidateNameAsync(snapshot.RepositoryRoot, localName, ct);
            if (snapshot.Branches.Any(b => !b.IsRemote && b.Name == localName)) throw new GitBranchException("Duplicate");
            await RunAsync(snapshot.RepositoryRoot, ct, "switch", "--no-guess", "--track", "-c", localName, fresh.Reference);
        }
        else await RunAsync(snapshot.RepositoryRoot, ct, "switch", "--no-guess", "--", fresh.Name);
    }

    public async Task CreateAsync(string directory, string name, string startReference, bool switchAfterCreate, CancellationToken ct = default)
    {
        using var lease = EnterMutation(directory);
        var snapshot = await ReadAsync(directory, ct);
        EnsureCanChange(snapshot, requireClean: switchAfterCreate);
        await ValidateNameAsync(snapshot.RepositoryRoot, name, ct);
        if (snapshot.Branches.Any(b => !b.IsRemote && b.Name == name)) throw new GitBranchException("Duplicate");
        if (startReference != "HEAD" && !snapshot.Branches.Any(b => b.Reference == startReference)) throw new GitBranchException("Stale");
        if (switchAfterCreate) await RunAsync(snapshot.RepositoryRoot, ct, "switch", "--no-guess", "--no-track", "-c", name, startReference);
        else await RunAsync(snapshot.RepositoryRoot, ct, "branch", "--no-track", "--", name, startReference);
    }

    public async Task RenameAsync(string directory, GitBranchInfo branch, string name, CancellationToken ct = default)
    {
        using var lease = EnterMutation(directory);
        var snapshot = await ReadAsync(directory, ct);
        EnsureCanChange(snapshot, requireClean: false);
        var fresh = RequireUnchanged(snapshot, branch);
        EnsureEditable(fresh, allowCurrent: true);
        await ValidateNameAsync(snapshot.RepositoryRoot, name, ct);
        if (snapshot.Branches.Any(b => !b.IsRemote && b.Name == name)) throw new GitBranchException("Duplicate");
        await RunAsync(snapshot.RepositoryRoot, ct, "branch", "-m", "--", fresh.Name, name);
    }

    public async Task DeleteAsync(string directory, GitBranchInfo branch, CancellationToken ct = default)
    {
        using var lease = EnterMutation(directory);
        var snapshot = await ReadAsync(directory, ct);
        EnsureCanChange(snapshot, requireClean: false);
        var fresh = RequireUnchanged(snapshot, branch);
        EnsureEditable(fresh, allowCurrent: false);
        if (!fresh.IsMerged || snapshot.BaseReference == null || fresh.Reference == snapshot.BaseReference)
            throw new GitBranchException("Unmerged");
        await RunAsync(snapshot.RepositoryRoot, ct, "branch", "-d", "--", fresh.Name);
    }

    public async Task<GitBranchComparison> CompareAsync(string directory, GitBranchInfo branch, CancellationToken ct = default)
    {
        var snapshot = await ReadAsync(directory, ct);
        var fresh = RequireUnchanged(snapshot, branch);
        if (!snapshot.HasCommits) throw new GitBranchException("Unborn");
        var result = await ProbeBoundedAsync(snapshot.RepositoryRoot, ct, 120_000, "diff", "--stat", "--patch", "--no-color", "--no-ext-diff", "--no-textconv", "HEAD", fresh.Reference, "--");
        if (result.ExitCode != 0) throw new GitBranchException("Failed", result.Error);
        return new(result.Output, result.Truncated);
    }

    private IDisposable EnterMutation(string directory)
    {
        try { return activity.EnterMutation(directory); }
        catch (RepositoryBusyException) { throw new GitBranchException("Busy"); }
    }

    private static void EnsureCanChange(GitBranchSnapshot snapshot, bool requireClean)
    {
        if (!snapshot.HasCommits) throw new GitBranchException("Unborn");
        if (snapshot.OperationInProgress) throw new GitBranchException("InProgress");
        if (requireClean && snapshot.IsDirty) throw new GitBranchException("Dirty");
    }

    private static void EnsureEditable(GitBranchInfo branch, bool allowCurrent)
    {
        if (branch.IsRemote || branch.IsDefault || (!allowCurrent && branch.IsCurrent)) throw new GitBranchException("Protected");
        if (branch.WorktreePath != null && !branch.IsCurrent) throw new GitBranchException("Occupied", branch.WorktreePath);
    }

    private static GitBranchInfo RequireUnchanged(GitBranchSnapshot snapshot, GitBranchInfo branch)
        => snapshot.Branches.FirstOrDefault(b => b.Reference == branch.Reference && b.CommitId == branch.CommitId)
            ?? throw new GitBranchException("Stale");

    private static async Task ValidateNameAsync(string root, string name, CancellationToken ct)
    {
        // --branch expands @{-n}; accepting it would make the displayed name differ from the created ref.
        if (string.IsNullOrWhiteSpace(name) || name != name.Trim() || name.StartsWith('-') || name.StartsWith("@{", StringComparison.Ordinal)
            || name.StartsWith("refs/", StringComparison.Ordinal) || name == "HEAD") throw new GitBranchException("InvalidName");
        var result = await ProbeAsync(root, ct, "check-ref-format", "--branch", name);
        if (result.ExitCode != 0) throw new GitBranchException("InvalidName");
    }

    private static Dictionary<string, string> ParseWorktrees(string output)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        string? path = null;
        foreach (var field in output.Split('\0'))
        {
            if (field.StartsWith("worktree ", StringComparison.Ordinal)) path = field[9..];
            else if (field.StartsWith("branch ", StringComparison.Ordinal) && path != null) result[field[7..]] = path;
        }
        return result;
    }

    private static async Task<string> RunAsync(string directory, CancellationToken ct, params string[] arguments)
    {
        var result = await ProbeAsync(directory, ct, arguments);
        if (result.ExitCode != 0) throw new GitBranchException("Failed", string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error);
        return result.Output;
    }

    private static async Task<(int ExitCode, string Output, string Error)> ProbeAsync(string directory, CancellationToken ct, params string[] arguments)
    {
        var result = await ProbeBoundedAsync(directory, ct, 2_000_000, arguments);
        if (result.Truncated) throw new GitBranchException("TooLarge");
        return (result.ExitCode, result.Output, result.Error);
    }

    private static async Task<(int ExitCode, string Output, string Error, bool Truncated)> ProbeBoundedAsync(string directory, CancellationToken ct, int outputLimit, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var process = Process.Start(start) ?? throw new GitBranchException("Failed", "Git could not start.");
        var output = ReadBoundedAsync(process.StandardOutput, outputLimit);
        var error = ReadBoundedAsync(process.StandardError, 16_000);
        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(output, error).ConfigureAwait(false);
            if (ct.IsCancellationRequested) throw;
            throw new GitBranchException("Timeout");
        }
        var stdout = await output.ConfigureAwait(false);
        var stderr = await error.ConfigureAwait(false);
        return (process.ExitCode, stdout.Text, stderr.Text, stdout.Truncated);
    }

    private static async Task<(string Text, bool Truncated)> ReadBoundedAsync(StreamReader reader, int limit)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        var truncated = false;
        int count;
        while ((count = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            var retained = Math.Min(count, limit - text.Length);
            text.Append(buffer, 0, retained);
            truncated |= retained < count;
        }
        return (text.ToString(), truncated);
    }
}
