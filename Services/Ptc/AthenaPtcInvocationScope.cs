using System;
using System.Collections.Generic;
using System.Collections.Frozen;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Athena.UI.Models;
using OpenAI.Chat;
using Ptc.Core;

namespace Athena.UI.Services.Ptc;

/// <summary>A host-only immutable grant captured immediately before the outer Registry call.</summary>
public sealed class AthenaPtcInvocation
{
    public AthenaPtcInvocation(string parentToolCallId, IReadOnlyList<PtcToolDefinition> tools,
        string? conversationId, string? workspaceId, string? delegatedTask,
        ToolApprovalContext.ExecutionMode mode, Action<PtcProgress>? progress)
    {
        ParentToolCallId = parentToolCallId;
        Tools = Array.AsReadOnly(tools.Select(tool => tool with { InputSchema = tool.InputSchema.Clone() }).ToArray());
        ConversationId = conversationId;
        WorkspaceId = workspaceId;
        DelegatedTask = delegatedTask;
        Mode = mode;
        Progress = progress;
    }
    public string ParentToolCallId { get; }
    public IReadOnlyList<PtcToolDefinition> Tools { get; }
    public string? ConversationId { get; }
    public string? WorkspaceId { get; }
    public string? DelegatedTask { get; }
    public ToolApprovalContext.ExecutionMode Mode { get; }
    public Action<PtcProgress>? Progress { get; }
    // Only approval, not read-only I/O, is serialized. A live switch to Strict cannot open
    // several child prompts at once even if those calls were initially judged parallel-safe.
    internal SemaphoreSlim ApprovalGate { get; } = new(1, 1);
}

public static class AthenaPtcInvocationScope
{
    public static readonly IReadOnlySet<string> ReadOnlyTools = new[]
    { "read_system_file", "get_file_info", "search_in_file", "search_in_directory", "list_system_directory" }.ToFrozenSet(StringComparer.Ordinal);
    private static readonly AsyncLocal<AthenaPtcInvocation?> Invocation = new();
    private static readonly AsyncLocal<SemaphoreSlim?> ChildApprovalGate = new();

    public static AthenaPtcInvocation? Current => Invocation.Value;
    public static bool IsChild => ChildApprovalGate.Value is not null;

    public static IDisposable Enter(string parentToolCallId, IReadOnlyList<ChatTool> tools,
        string? conversationId, string? workspaceId, string? delegatedTask, Action<PtcProgress>? progress)
    {
        var definitions = tools.Where(tool => ReadOnlyTools.Contains(tool.FunctionName))
            .Select(tool => new PtcToolDefinition(tool.FunctionName, tool.FunctionDescription,
                PtcJson.Parse(tool.FunctionParameters.ToString()))).ToArray();
        return Enter(new AthenaPtcInvocation(parentToolCallId, definitions, conversationId, workspaceId,
            delegatedTask, ToolApprovalContext.CurrentMode, progress));
    }

    public static IDisposable Enter(AthenaPtcInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        var before = Invocation.Value;
        Invocation.Value = invocation;
        return new Scope(() => Invocation.Value = before);
    }

    internal static IDisposable EnterChild(AthenaPtcInvocation invocation)
    {
        var before = ChildApprovalGate.Value;
        ChildApprovalGate.Value = invocation.ApprovalGate;
        return new Scope(() => ChildApprovalGate.Value = before);
    }

    public static async ValueTask<IDisposable> AcquireChildApprovalAsync(CancellationToken cancellationToken)
    {
        var gate = ChildApprovalGate.Value;
        if (gate is null) return new Scope(() => { }); // Ordinary direct calls have no PTC child approval scope.
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Scope(() => gate.Release());
    }

    private sealed class Scope(Action restore) : IDisposable
    {
        private Action? _restore = restore;
        public void Dispose() => Interlocked.Exchange(ref _restore, null)?.Invoke();
    }
}
