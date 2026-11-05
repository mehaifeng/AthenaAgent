using System;
using System.Collections.Generic;

namespace Athena.UI.Models;

/// <summary>Independent lanes; PTC child tools are deliberately distinct from their outer tool call.</summary>
public enum ExecutionTimelineLane { Model, Tools, Script, PtcTools, System }

/// <summary>NotStarted and OutcomeUnknown are never presented as successful or executed work.</summary>
public enum ExecutionTimelineState { Queued, Running, Succeeded, Failed, Denied, Cancelled, TimedOut, NotStarted, OutcomeUnknown, Incomplete }

public enum ExecutionTimelineKind { ModelRequest, ToolCall, ScriptRun, ToolQueue, Approval, ConversationQueue, RetryWait, Compression, Preparation }

/// <summary>
/// Local, metadata-only trace span. Offsets use a single monotonic origin and are persisted in
/// microseconds; raw Stopwatch timestamps, prompts, script, paths, arguments and results are not stored.
/// </summary>
public sealed record ExecutionTimelineSpan(
    long Id,
    long? ParentId,
    ExecutionTimelineLane Lane,
    ExecutionTimelineKind Kind,
    string Name,
    long StartUs,
    long? EndUs,
    ExecutionTimelineState State,
    int? Round = null,
    int? Attempt = null,
    long? FirstTextUs = null,
    long? InputTokens = null,
    long? OutputTokens = null,
    long? CachedInputTokens = null);

/// <summary>Host-owned per-bubble projection; never sent to a model or embedded in ChatMessage.</summary>
public sealed record ExecutionTimelineSnapshot(
    int Version,
    string MessageId,
    DateTimeOffset StartedAtUtc,
    long DurationUs,
    ExecutionTimelineState State,
    bool IsPartial,
    bool PersistenceFailed,
    int ModelAttempts,
    int OuterTools,
    int OuterToolsStarted,
    int PtcAttempts,
    int PtcStarted,
    int DroppedSpans,
    IReadOnlyList<ExecutionTimelineSpan> Spans);

public sealed record ExecutionTimelineLoadResult(ExecutionTimelineSnapshot? Snapshot, bool IsLive, string? ErrorCode = null);
