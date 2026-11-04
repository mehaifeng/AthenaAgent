using System.Text.Json;

namespace Ptc.Core;

/// <summary>Serial is an in-run barrier; ParallelReadOnly requires a host guarantee of no approval prompt or write.</summary>
public enum PtcScheduling { Serial, ParallelReadOnly }
/// <summary>Run outcome; the first fatal stop reason cannot be undone by script catch.</summary>
public enum PtcRunStatus { Completed, Rejected, Failed, Cancelled, TimedOut, BudgetExceeded }
/// <summary>OutcomeUnknown means host I/O outlived bounded drain, not that it was forcibly stopped.</summary>
public enum PtcCallState { Queued, Running, Succeeded, Failed, Denied, Cancelled, NotStarted, OutcomeUnknown }

/// <summary>Host-supplied ordinal tool name and supported JSON input schema; cloned before script admission.</summary>
public sealed record PtcToolDefinition(string Name, string Description, JsonElement InputSchema);
/// <summary>Host-created correlation and business context, never accepted from script arguments.</summary>
public sealed record PtcRunContext(string RunId, string ParentToolCallId, string? ConversationId,
    string? WorkspaceId, string? DelegatedTask);
/// <summary>An async JavaScript function body, object inputs and a host-authorized tool/limit snapshot.</summary>
public sealed record PtcExecutionRequest(string Code, JsonElement Inputs, IReadOnlySet<string> AllowedTools,
    PtcRunContext Context, PtcExecutionLimits Limits);

/// <summary>Finite positive limits; times include awaits and approval. Engine memory is not process-wide memory.</summary>
public sealed record PtcExecutionLimits
{
    public int MaxCodeChars { get; init; } = 32_000;
    public long MaxStatements { get; init; } = 100_000;
    public int MaxRecursionDepth { get; init; } = 64;
    public long EngineMemoryBytes { get; init; } = 32 * 1024 * 1024;
    public TimeSpan MaxWallTime { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan MaxToolTime { get; init; } = TimeSpan.FromSeconds(10);
    public int MaxToolCalls { get; init; } = 64;
    public int MaxConcurrency { get; init; } = 4;
    public int MaxPendingCalls { get; init; } = 64;
    public int MaxJsonDepth { get; init; } = 32;
    public int MaxInputBytes { get; init; } = 128 * 1024;
    public int MaxArgumentsBytes { get; init; } = 64 * 1024;
    public int MaxToolResultBytes { get; init; } = 512 * 1024;
    public long MaxRunDataBytes { get; init; } = 8 * 1024 * 1024;
    public int MaxOutputBytes { get; init; } = 16 * 1024;
    public int MaxModelResultBytes { get; init; } = 24 * 1024;
    public int MaxArtifactReferences { get; init; } = 32;
    public int MaxToolDefinitions { get; init; } = 128;
    public int MaxCatalogBytes { get; init; } = 512 * 1024;
    public TimeSpan DrainTimeout { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan EventDeliveryTimeout { get; init; } = TimeSpan.FromSeconds(2);

    public void Validate()
    {
        if (MaxCodeChars <= 0 || MaxStatements <= 0 || MaxRecursionDepth <= 0 || EngineMemoryBytes <= 0
            || MaxToolCalls <= 0 || MaxConcurrency <= 0 || MaxPendingCalls <= 0 || MaxJsonDepth <= 0
            || MaxInputBytes <= 0 || MaxArgumentsBytes <= 0 || MaxToolResultBytes <= 0 || MaxRunDataBytes <= 0
            || MaxOutputBytes <= 0 || MaxModelResultBytes < 256 || MaxArtifactReferences <= 0
            || MaxToolDefinitions <= 0 || MaxCatalogBytes <= 0
            || !Finite(MaxWallTime) || !Finite(MaxToolTime) || !Finite(DrainTimeout) || !Finite(EventDeliveryTimeout)
            || MaxStatements >= int.MaxValue || MaxRecursionDepth >= int.MaxValue || EngineMemoryBytes == long.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(PtcExecutionLimits), "PTC limits must be finite and positive; model results need at least 256 bytes.");
    }

    private static bool Finite(TimeSpan value) => value > TimeSpan.Zero && value.TotalMilliseconds <= uint.MaxValue - 1;
}

/// <summary>One coordinator-issued unique call identity and detached JSON object arguments.</summary>
public sealed record PtcToolCall(string CallId, string Name, JsonElement Arguments);
/// <summary>Opaque host artifact identity; no file paths or binary data belong in this contract.</summary>
public sealed record PtcArtifactReference(string Id, string MediaType, string DisplayName);
/// <summary>Business results use camelCase JSON. A business denial is distinct from an irrevocable run stop.</summary>
public sealed record PtcToolResult(bool Success, string Message, JsonElement? Data, string? ErrorCode,
    IReadOnlyList<PtcArtifactReference> Artifacts)
{
    public static PtcToolResult Error(string code, string message) => new(false, message, null, code, []);
}
/// <summary>Stable error code and safe public diagnostic, never a host exception stack.</summary>
public sealed record PtcFailure(string Code, string Message);
/// <summary>Actual outcome with separate result-delivery metadata; do not send full records to a model.</summary>
public sealed record PtcToolRecord(PtcToolCall Call, PtcCallState State, PtcToolResult? Result, TimeSpan Duration)
{
    public long RawResultBytes { get; init; }
    public bool ResultDelivered { get; init; }
    public string? DeliveryErrorCode { get; init; }
}
/// <summary>Observed calls, bytes, concurrency and elapsed time; these are not token estimates.</summary>
public sealed record PtcRunMetrics(int CallsAttempted, int CallsStarted, int CallsFinished,
    long ToolResultBytes, int OutputBytes, int PeakConcurrency, TimeSpan Duration);
/// <summary>Host-only complete execution record; use PtcModelResultProjector for model-facing output.</summary>
public sealed record PtcExecutionResult(PtcRunContext Context, PtcRunStatus Status,
    IReadOnlyList<JsonElement> Output, IReadOnlyList<PtcToolRecord> ToolRecords,
    IReadOnlyList<PtcArtifactReference> Artifacts, PtcRunMetrics Metrics, PtcFailure? Failure);
/// <summary>Monotonic metadata event, deliberately excluding arguments, result bodies and secrets.</summary>
public sealed record PtcEvent(string RunId, string ParentToolCallId, long Sequence,
    string Kind, string? CallId, string? ToolName, string? ErrorCode);

/// <summary>Business authorization, actual tool dispatch and resource ownership stay in the host.</summary>
public interface IPtcToolHost
{
    IReadOnlyList<PtcToolDefinition> Tools { get; }
    PtcScheduling GetScheduling(PtcToolCall call, PtcRunContext context);
    ValueTask<PtcToolResult> InvokeAsync(PtcToolCall call, PtcRunContext context, CancellationToken cancellationToken);
}
/// <summary>Bounded observer delivery; failures are visible and stop new calls.</summary>
public interface IPtcEventSink
{
    ValueTask OnEventAsync(PtcEvent item, CancellationToken cancellationToken);
}
/// <summary>The runtime's only tool/output capability; authorization and budgets cannot be bypassed through a host reference.</summary>
public interface IPtcScriptBridge
{
    IReadOnlyList<PtcToolDefinition> Tools { get; }
    ValueTask<PtcToolResult> CallAsync(string name, JsonElement arguments, CancellationToken cancellationToken);
    void Emit(JsonElement value);
}
/// <summary>Runs one isolated language instance without models, credentials, UI dependencies or persistent globals.</summary>
public interface IPtcScriptRuntime
{
    ValueTask RunAsync(string code, JsonElement inputs, IPtcScriptBridge bridge,
        PtcExecutionLimits limits, CancellationToken cancellationToken);
}
/// <summary>Coordinates one immutable grant, finite budgets, bounded scheduling and terminal records per run.</summary>
public interface IPtcExecutor
{
    Task<PtcExecutionResult> ExecuteAsync(PtcExecutionRequest request, IPtcToolHost host,
        IPtcEventSink? events = null, CancellationToken cancellationToken = default);
}

/// <summary>Stable, deliberately non-forensic runtime diagnostics. Never put a host exception or stack here.</summary>
public sealed class PtcRuntimeException(string code, string message, PtcRunStatus status = PtcRunStatus.Failed)
    : Exception(message)
{
    public string Code { get; } = code;
    public PtcRunStatus Status { get; } = status;
}
