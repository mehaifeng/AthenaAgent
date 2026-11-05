using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using Athena.UI.Services.SubAgents;
using Ptc.Core;
using Athena.UI.Services.ExecutionTimeline;

namespace Athena.UI.Services.Ptc;

public sealed class AthenaPtcToolHost : IPtcToolHost
{
    private readonly AthenaPtcInvocation _invocation;
    private readonly PtcRunContext _context;
    private readonly IFunctionRegistry _registry;
    private readonly IConfigService _config;
    private readonly IConversationSessionAccessor _sessions;
    private readonly IFileSystemService _files;
    private readonly string _attachmentRoot;
    private readonly HashSet<string> _allowed;
    private readonly ConcurrentDictionary<string, ChatAttachment> _attachments = new(StringComparer.Ordinal);

    public AthenaPtcToolHost(AthenaPtcInvocation invocation, PtcRunContext context, IFunctionRegistry registry,
        IConfigService config, IConversationSessionAccessor sessions, IFileSystemService files, IPlatformPathService paths)
    {
        _invocation = invocation;
        _context = context;
        _registry = registry;
        _config = config;
        _sessions = sessions;
        _files = files;
        _attachmentRoot = paths.GetAttachmentDirectory();
        Tools = Array.AsReadOnly(invocation.Tools.Where(tool => AthenaPtcInvocationScope.ReadOnlyTools.Contains(tool.Name)).ToArray());
        _allowed = Tools.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
    }

    public IReadOnlyList<PtcToolDefinition> Tools { get; }

    public PtcScheduling GetScheduling(PtcToolCall call, PtcRunContext context)
    {
        var config = _config.Load();
        return ToolCallParallelism.IsParallelSafe(call.Name, call.Arguments.GetRawText(), config.ToolApprovalMode,
            SensitiveLocations.From(config.FileSystemPolicy, _config.ConfigFilePath))
            ? PtcScheduling.ParallelReadOnly : PtcScheduling.Serial;
    }

    public async ValueTask<PtcToolResult> InvokeAsync(PtcToolCall call, PtcRunContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_config.Load().PtcEnabled || context != _context || !_allowed.Contains(call.Name)
            || _invocation.Mode != ToolApprovalContext.ExecutionMode.Interactive)
            return PtcToolResult.Error("tool_not_available", "PTC tool grant is no longer available.");

        using var conversation = _sessions.Enter(_invocation.ConversationId ?? string.Empty);
        using var workspace = _sessions.EnterWorkspace(_invocation.WorkspaceId);
        using var tokenScope = ToolExecutionContext.Enter(cancellationToken);
        using var approval = ToolApprovalContext.EnterInteractive(_invocation.DelegatedTask);
        using var child = AthenaPtcInvocationScope.EnterChild(_invocation);
        var timeline = _invocation.Timeline?.Recorder;
        var childSpan = timeline?.EnsurePtcChild(call.CallId, call.Name, context.RunId) ?? 0;
        using var timelineScope = ExecutionTimelineScope.Enter(timeline, childSpan);
        var result = await _registry.ExecuteAsync(call.Name, call.Arguments.GetRawText()).ConfigureAwait(false);

        // Do not use BudgetedJson: that is a model-reading projection, not complete business data.
        // The coordinator, not the ordinary context budget, bounds this complete returned result.
        var raw = PtcJson.Parse(result.SerializeRaw(), int.MaxValue, 40);
        var artifacts = new List<PtcArtifactReference>();
        foreach (var attachment in result.GeneratedAttachments)
        {
            ValidateArtifact(attachment);
            if (_attachments.Count >= 32 && !_attachments.ContainsKey(attachment.Id))
                throw new PtcRuntimeException("data_limit_exceeded", "Generated artifact reference limit exceeded.", PtcRunStatus.BudgetExceeded);
            _attachments.TryAdd(attachment.Id, ConversationPersistenceHelper.CloneAttachment(attachment));
            artifacts.Add(new(attachment.Id, attachment.MimeType, attachment.FileName));
        }
        // Returning a completed result even when Stop just arrived preserves its actual outcome
        // and any completed artifacts. Core's stop latch prevents delivering it back to the script.
        return new(result.Success, result.Message, raw.GetProperty("data").Clone(),
            result.Success ? null : result.ErrorCode ?? "tool_failed", artifacts);
    }

    public IReadOnlyList<ChatAttachment> GetAttachments(IEnumerable<PtcArtifactReference> references)
        => references.Select(item => _attachments.TryGetValue(item.Id, out var attachment) ? attachment : null)
            .Where(item => item is not null).Select(item => ConversationPersistenceHelper.CloneAttachment(item!)).ToArray();

    private void ValidateArtifact(ChatAttachment attachment)
    {
        var path = _files.GetAbsoluteSecurePath(attachment.StoredPath);
        if (string.IsNullOrWhiteSpace(attachment.Id) || attachment.Id.Length > 256
            || !FileSystemService.TryResolveFully(_attachmentRoot, out var root)
            || !FileSystemService.TryResolveFully(path, out var resolved)
            || !resolved.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            || !File.Exists(resolved) || new FileInfo(resolved).Length != attachment.SizeBytes
            || attachment.SizeBytes is < 0 or > 16 * 1024 * 1024)
            throw new PtcRuntimeException("host_error", "Generated artifact does not belong to the host attachment store.");
    }
}
