using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Athena.UI.Models;

namespace Athena.UI.Services.ExecutionTimeline;

/// <summary>
/// One bounded, thread-safe recorder for one visible assistant bubble. Methods never perform I/O,
/// invoke UI, or consume tool parameters. All clocks are captured at the actual execution site.
/// </summary>
public sealed class ExecutionTimelineCollector
{
    public const int MaxSpans = 4096;
    private readonly object _gate = new();
    private readonly string _messageId;
    private readonly long _start;
    private readonly DateTimeOffset _startedAt;
    private readonly List<ExecutionTimelineSpan> _spans = [];
    private readonly Dictionary<long, int> _indexes = [];
    private readonly Dictionary<string, long> _outerCalls = new(StringComparer.Ordinal);
    private readonly Dictionary<long, string> _outerProviderIds = [];
    private readonly Dictionary<string, long> _scripts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PtcChild> _children = new(StringComparer.Ordinal);
    private long _nextId;
    private int _models, _outerTools, _outerStarted, _ptcAttempts, _ptcStarted, _dropped;
    private bool _stopped, _finished, _persistenceFailed, _captureFault;
    private long? _endUs;
    private long _compressionSpan;
    private ExecutionTimelineState _state = ExecutionTimelineState.Running;

    private sealed class PtcChild(string name, long queuedUs, string runId)
    {
        public string Name { get; } = name;
        public long QueuedUs { get; set; } = queuedUs;
        public string RunId { get; } = runId;
        public long? SpanId { get; set; }
        public bool Started { get; set; }
        public bool HostStarted { get; set; }
    }

    public ExecutionTimelineCollector(string messageId, long startedAt, DateTimeOffset startedAtUtc)
    {
        _messageId = messageId;
        _start = startedAt;
        _startedAt = startedAtUtc;
    }

    public string MessageId => _messageId;

    private long Offset(long timestamp = 0)
    {
        var end = timestamp > 0 ? timestamp : Stopwatch.GetTimestamp();
        return Math.Max(0, (long)Stopwatch.GetElapsedTime(_start, end).TotalMicroseconds);
    }

    public long Begin(ExecutionTimelineLane lane, ExecutionTimelineKind kind, string name,
        long? parentId = null, int? round = null, int? attempt = null, long timestamp = 0)
    {
        lock (_gate)
        {
            if (_stopped || _finished) return 0;
            if (kind == ExecutionTimelineKind.ModelRequest) _models++;
            return Add(lane, kind, name, parentId, Offset(timestamp), round, attempt);
        }
    }

    public long QueueOuterTool(string providerCallId, string name, bool declared, int round, long timestamp = 0, long? parentId = null)
    {
        lock (_gate)
        {
            if (_stopped || _finished) return 0;
            _outerTools++;
            var id = Add(ExecutionTimelineLane.Tools, ExecutionTimelineKind.ToolCall,
                SafeToolName(name, declared), parentId, Offset(timestamp), round, null, ExecutionTimelineState.Queued);
            if (id != 0) _outerProviderIds[id] = providerCallId;
            return id;
        }
    }

    public void StartOuterTool(long id, long timestamp = 0)
    {
        lock (_gate)
        {
            if (_stopped || _finished) return;
            _outerStarted++;
            if (!TryGet(id, out var index)) return;
            // Bind on actual start, not when the whole batch is queued. A malformed provider can
            // reuse a call ID; a later queued call must not steal the current PTC parent.
            if (_outerProviderIds.TryGetValue(id, out var providerId)) _outerCalls[providerId] = id;
            var span = _spans[index];
            var actual = Offset(timestamp);
            if (actual > span.StartUs)
            {
                var queue = Add(ExecutionTimelineLane.System, ExecutionTimelineKind.ToolQueue, "tool_queue", id, span.StartUs, span.Round, null);
                SetEnd(queue, ExecutionTimelineState.Succeeded, actual);
            }
            _spans[index] = span with { StartUs = actual, State = ExecutionTimelineState.Running };
        }
    }

    public void RejectQueuedOuterTool(long id)
    {
        lock (_gate)
        {
            if (!TryGet(id, out var index)) return;
            var span = _spans[index];
            var at = Offset();
            if (at > span.StartUs)
            {
                var queue = Add(ExecutionTimelineLane.System, ExecutionTimelineKind.ToolQueue, "tool_queue", id, span.StartUs, span.Round, null);
                SetEnd(queue, ExecutionTimelineState.Succeeded, at);
            }
            // The guard rejected it before the Registry, so it is a marker, not an executed bar.
            _spans[index] = span with { EndUs = span.StartUs, State = ExecutionTimelineState.Denied };
            _outerProviderIds.Remove(id);
        }
    }

    public void End(long id, ExecutionTimelineState state, long timestamp = 0)
    {
        lock (_gate)
        {
            SetEnd(id, state, Offset(timestamp));
            if (_outerProviderIds.Remove(id, out var providerId)
                && _outerCalls.TryGetValue(providerId, out var bound) && bound == id)
                _outerCalls.Remove(providerId);
        }
    }

    public void FirstText(long id)
    {
        lock (_gate)
        {
            if (TryGet(id, out var index) && _spans[index].FirstTextUs is null)
                _spans[index] = _spans[index] with { FirstTextUs = Offset() };
        }
    }

    public void Usage(long id, long input, long output, long cached)
    {
        lock (_gate)
        {
            if (TryGet(id, out var index))
                _spans[index] = _spans[index] with { InputTokens = input, OutputTokens = output, CachedInputTokens = cached };
        }
    }

    /// <summary>Only the existing measured compression phases are represented; no token estimates.</summary>
    public void ObserveCompression(CompressionProgress progress)
    {
        lock (_gate)
        {
            if (_stopped || _finished) return;
            if (progress.Phase is CompressionProgressPhase.Mapping or CompressionProgressPhase.Reducing)
            {
                if (_compressionSpan == 0)
                    _compressionSpan = Add(ExecutionTimelineLane.System, ExecutionTimelineKind.Compression,
                        "compression", null, Offset(), null, null);
            }
            else if (_compressionSpan != 0)
            {
                SetEnd(_compressionSpan, progress.Phase switch
                {
                    CompressionProgressPhase.Committed => ExecutionTimelineState.Succeeded,
                    CompressionProgressPhase.Skipped => ExecutionTimelineState.Cancelled,
                    _ => ExecutionTimelineState.Failed
                }, Offset());
                _compressionSpan = 0;
            }
        }
    }

    /// <summary>Creates/looks up the actual PTC child before Registry approval, even if event delivery is lagging.</summary>
    public long EnsurePtcChild(string callId, string name, string runId = "")
    {
        lock (_gate)
        {
            if (_stopped || _finished) return 0;
            if (!_children.TryGetValue(callId, out var child))
            {
                if (_children.Count >= MaxSpans) { _dropped++; return 0; }
                _children[callId] = child = new PtcChild(SafePtcName(name), Offset(), runId);
            }
            child.HostStarted = true; // This method is called only at actual host invocation.
            if (child.SpanId is { } existing) return existing;
            child.SpanId = Add(ExecutionTimelineLane.PtcTools, ExecutionTimelineKind.ToolCall, child.Name, null, Offset(), null, null);
            return child.SpanId.Value;
        }
    }

    public void ObservePtc(PtcProgress item)
    {
        lock (_gate)
        {
            if (_stopped || _finished || item.OccurredTimestamp <= 0) return;
            var at = Offset(item.OccurredTimestamp);
            switch (item.Kind)
            {
                case "ScriptStarted":
                    _outerCalls.TryGetValue(item.ParentToolCallId, out var parent);
                    var addedScript = Add(ExecutionTimelineLane.Script, ExecutionTimelineKind.ScriptRun,
                        "execute_program", parent == 0 ? null : parent, at, null, null);
                    if (addedScript != 0) _scripts[item.RunId] = addedScript;
                    break;
                case "CallQueued" when item.CallId is not null:
                    _ptcAttempts++;
                    if (!_children.TryGetValue(item.CallId, out var queuedChild))
                    {
                        if (_children.Count >= MaxSpans) { _dropped++; break; }
                        _children[item.CallId] = new PtcChild(SafePtcName(item.ToolName), at, item.RunId);
                    }
                    else queuedChild.QueuedUs = at;
                    break;
                case "CallStarted" when item.CallId is not null:
                {
                    if (!_children.TryGetValue(item.CallId, out var child))
                    {
                        if (_children.Count >= MaxSpans) { _ptcStarted++; _dropped++; break; }
                        _children[item.CallId] = child = new PtcChild(SafePtcName(item.ToolName), at, item.RunId);
                    }
                    if (!child.Started) { child.Started = true; _ptcStarted++; }
                    _scripts.TryGetValue(item.RunId, out var script);
                    if (child.SpanId is not { } childId || childId == 0)
                        child.SpanId = Add(ExecutionTimelineLane.PtcTools, ExecutionTimelineKind.ToolCall, child.Name,
                            script == 0 ? null : script, at, null, null);
                    else if (TryGet(childId, out var index))
                        _spans[index] = _spans[index] with { StartUs = at, ParentId = script == 0 ? null : script };
                    if (at > child.QueuedUs)
                    {
                        var queue = Add(ExecutionTimelineLane.System, ExecutionTimelineKind.ToolQueue, "tool_queue",
                            child.SpanId, child.QueuedUs, null, null);
                        SetEnd(queue, ExecutionTimelineState.Succeeded, at);
                    }
                    break;
                }
                case "CallFinished" when item.CallId is not null:
                {
                    if (!_children.TryGetValue(item.CallId, out var child))
                    {
                        if (_children.Count >= MaxSpans) { _dropped++; break; }
                        _children[item.CallId] = child = new PtcChild(SafePtcName(item.ToolName), at, item.RunId);
                    }
                    _scripts.TryGetValue(item.RunId, out var script);
                    if (child.SpanId is not { } childId || childId == 0)
                        child.SpanId = Add(ExecutionTimelineLane.PtcTools, ExecutionTimelineKind.ToolCall,
                            child.Name, script == 0 ? null : script, at, null, null);
                    SetEnd(child.SpanId.Value, MapPtcCall(item.CallState, item.ErrorCode), at);
                    _children.Remove(item.CallId);
                    break;
                }
                case "ScriptFinished":
                    if (_scripts.TryGetValue(item.RunId, out var scriptId))
                        SetEnd(scriptId, MapPtcRun(item.RunStatus, item.ErrorCode), at);
                    break;
                case "RunFinished": // A missing ScriptFinished after bounded drain is not proof it stopped.
                    if (_scripts.TryGetValue(item.RunId, out var unfinishedScript))
                        SetEnd(unfinishedScript, ExecutionTimelineState.OutcomeUnknown, at);
                    _scripts.Remove(item.RunId);
                    foreach (var pair in _children.Where(pair => pair.Value.RunId == item.RunId).ToArray())
                    {
                        _captureFault = true; // A missing terminal event must never look like a complete capture.
                        var actuallyStarted = pair.Value.Started || pair.Value.HostStarted;
                        if (pair.Value.HostStarted && !pair.Value.Started) _ptcStarted++;
                        var pendingId = pair.Value.SpanId ?? Add(ExecutionTimelineLane.PtcTools, ExecutionTimelineKind.ToolCall,
                            pair.Value.Name, unfinishedScript == 0 ? null : unfinishedScript, pair.Value.QueuedUs, null, null);
                        if (unfinishedScript != 0 && TryGet(pendingId, out var pendingIndex))
                            _spans[pendingIndex] = _spans[pendingIndex] with { ParentId = unfinishedScript };
                        SetEnd(pendingId, actuallyStarted ? ExecutionTimelineState.OutcomeUnknown : ExecutionTimelineState.NotStarted,
                            actuallyStarted ? at : pair.Value.QueuedUs);
                        _children.Remove(pair.Key);
                    }
                    break;
            }
        }
    }

    /// <summary>Turning the user switch off stops collection immediately and marks the result partial.</summary>
    public void StopCollecting()
    {
        lock (_gate)
        {
            if (_stopped || _finished) return;
            _stopped = true;
            _endUs = Offset();
            _state = ExecutionTimelineState.Incomplete;
            CloseOpen(ExecutionTimelineState.Incomplete, _endUs.Value);
            ClearCorrelation();
        }
    }

    public ExecutionTimelineSnapshot Complete(ExecutionTimelineState state, long completedAt = 0)
    {
        lock (_gate)
        {
            if (!_finished)
            {
                // If an observer failed before terminal delivery, preserve actual host starts
                // and mark the result partial rather than silently losing those operations.
                foreach (var child in _children.Values)
                {
                    _captureFault = true;
                    if (child.HostStarted && !child.Started) _ptcStarted++;
                }
                _finished = true;
                if (!_stopped)
                {
                    _endUs = Offset(completedAt);
                    _state = state;
                    CloseOpen(state == ExecutionTimelineState.Cancelled ? state : ExecutionTimelineState.Incomplete, _endUs.Value);
                }
                else _endUs = Offset(completedAt); // Total reply duration remains honest; no events are collected after opt-out.
                ClearCorrelation();
            }
            return SnapshotCore();
        }
    }

    public void MarkPersistenceFailed() { lock (_gate) _persistenceFailed = true; }
    public void MarkCaptureFault() { lock (_gate) _captureFault = true; }
    public ExecutionTimelineSnapshot Snapshot() { lock (_gate) return SnapshotCore(); }

    private ExecutionTimelineSnapshot SnapshotCore() => new(1, _messageId, _startedAt,
        _endUs ?? Offset(), _state, _stopped || _dropped > 0 || _captureFault, _persistenceFailed,
        _models, _outerTools, _outerStarted, _ptcAttempts, _ptcStarted, _dropped, _spans.ToArray());

    private long Add(ExecutionTimelineLane lane, ExecutionTimelineKind kind, string name, long? parent,
        long start, int? round, int? attempt, ExecutionTimelineState state = ExecutionTimelineState.Running)
    {
        if (_spans.Count >= MaxSpans) { _dropped++; return 0; }
        var id = ++_nextId;
        _indexes[id] = _spans.Count;
        _spans.Add(new ExecutionTimelineSpan(id, parent is > 0 ? parent : null, lane, kind, name, start, null, state, round, attempt));
        return id;
    }

    private bool TryGet(long id, out int index)
    {
        index = 0;
        return !_stopped && !_finished && _indexes.TryGetValue(id, out index);
    }

    private void SetEnd(long id, ExecutionTimelineState state, long end)
    {
        if (!TryGet(id, out var index)) return;
        var span = _spans[index];
        if (span.EndUs is not null) return;
        _spans[index] = span with { EndUs = Math.Max(span.StartUs, end), State = state };
    }

    private void CloseOpen(ExecutionTimelineState state, long end)
    {
        for (var i = 0; i < _spans.Count; i++)
        {
            var span = _spans[i];
            if (span.EndUs is not null) continue;
            _spans[i] = span with
            {
                EndUs = span.State == ExecutionTimelineState.Queued ? span.StartUs : Math.Max(span.StartUs, end),
                State = span.State == ExecutionTimelineState.Queued ? ExecutionTimelineState.NotStarted
                    : !_stopped && (span.Lane is ExecutionTimelineLane.Tools or ExecutionTimelineLane.PtcTools)
                        ? ExecutionTimelineState.OutcomeUnknown : state
            };
        }
    }

    private void ClearCorrelation()
    {
        _children.Clear();
        _scripts.Clear();
        _outerCalls.Clear();
        _outerProviderIds.Clear();
    }

    private static string SafePtcName(string? name)
        => name is "read_system_file" or "get_file_info" or "search_in_file" or "search_in_directory" or "list_system_directory"
            ? name : "unavailable_tool";

    private static string SafeToolName(string? name, bool declared)
        => declared && name is { Length: > 0 and <= 128 } && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.')
            ? name : "unavailable_tool";

    private static ExecutionTimelineState MapPtcCall(string? state, string? error) => state switch
    {
        "Succeeded" => ExecutionTimelineState.Succeeded,
        "Denied" => ExecutionTimelineState.Denied,
        "Cancelled" => ExecutionTimelineState.Cancelled,
        "NotStarted" => ExecutionTimelineState.NotStarted,
        "OutcomeUnknown" => ExecutionTimelineState.OutcomeUnknown,
        "Failed" => ExecutionTimelineState.Failed,
        _ => error is null ? ExecutionTimelineState.Incomplete : ExecutionTimelineState.Failed
    };

    private static ExecutionTimelineState MapPtcRun(string? state, string? error) => state switch
    {
        "Completed" => ExecutionTimelineState.Succeeded,
        "Cancelled" => ExecutionTimelineState.Cancelled,
        "TimedOut" => ExecutionTimelineState.TimedOut,
        "Rejected" or "Failed" or "BudgetExceeded" => ExecutionTimelineState.Failed,
        _ => error is null ? ExecutionTimelineState.Incomplete : ExecutionTimelineState.Failed
    };
}
