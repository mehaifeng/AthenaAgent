using System.Collections.Frozen;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Ptc.Core;

/// <summary>One immutable grant, bounded queue, serial scheduler and stop latch per execution.</summary>
public sealed class PtcExecutor(IPtcScriptRuntime runtime) : IPtcExecutor
{
    private readonly IPtcScriptRuntime _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));

    public async Task<PtcExecutionResult> ExecuteAsync(PtcExecutionRequest request, IPtcToolHost host,
        IPtcEventSink? events = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(request.Limits);
        ArgumentNullException.ThrowIfNull(request.Context);
        ArgumentNullException.ThrowIfNull(request.AllowedTools);
        request.Limits.Validate();
        if (string.IsNullOrWhiteSpace(request.Context.RunId) || request.Context.RunId.Length > 256
            || string.IsNullOrWhiteSpace(request.Context.ParentToolCallId) || request.Context.ParentToolCallId.Length > 256)
            throw new ArgumentException("Host-created run and parent call IDs are required and limited to 256 characters.");

        using var run = new Run(request with { Limits = request.Limits with { }, Context = request.Context with { } },
            host, events, cancellationToken);
        return await run.ExecuteAsync(_runtime).ConfigureAwait(false);
    }

    private sealed class Pending(PtcToolCall call)
    {
        public PtcToolCall Call { get; } = call;
        public TaskCompletionSource<PtcToolResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public PtcCallState State { get; set; } = PtcCallState.Queued;
        public PtcToolResult? Result { get; set; }
        public long StartedAt { get; set; }
        public TimeSpan Duration { get; set; }
        public long RawBytes { get; set; }
        public bool Delivered { get; set; }
        public string? DeliveryError { get; set; }
        public bool Terminal { get; set; }
    }

    private sealed class Run : IPtcScriptBridge, IDisposable
    {
        private readonly object _gate = new();
        private readonly PtcExecutionRequest _request;
        private readonly PtcExecutionLimits _limits;
        private readonly IPtcToolHost _host;
        private readonly IPtcEventSink? _sink;
        private readonly CancellationTokenSource _stop = new();
        private readonly CancellationToken _token;
        private readonly CancellationTokenSource _deadline;
        private readonly CancellationTokenRegistration _callerRegistration;
        private readonly CancellationTokenRegistration _deadlineRegistration;
        private readonly Channel<Pending> _queue;
        private readonly Channel<PtcEvent> _events;
        private readonly List<Pending> _calls = [];
        private readonly List<JsonElement> _output = [];
        private readonly Dictionary<string, PtcArtifactReference> _artifacts = new(StringComparer.Ordinal);
        private readonly long _started = Stopwatch.GetTimestamp();
        private FrozenDictionary<string, PtcToolDefinition> _definitions = FrozenDictionary<string, PtcToolDefinition>.Empty;
        private PtcFailure? _failure;
        private PtcRunStatus _status = PtcRunStatus.Completed;
        private bool _sealed;
        private bool _accepting = true;
        private int _attempted, _startedCalls, _finishedCalls, _live, _peak, _pending, _outputBytes;
        private long _dataBytes, _resultBytes, _sequence;

        public Run(PtcExecutionRequest request, IPtcToolHost host, IPtcEventSink? sink, CancellationToken caller)
        {
            _request = request;
            _limits = request.Limits;
            _host = host;
            _sink = sink;
            _token = _stop.Token;
            _queue = Channel.CreateBounded<Pending>(new BoundedChannelOptions(_limits.MaxPendingCalls)
            { SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false });
            // Three per admitted call, two for the limit-triggering attempt, and four run/script
            // lifecycle events. Keep the observer queue bounded without dropping terminal events.
            _events = Channel.CreateBounded<PtcEvent>(new BoundedChannelOptions(Math.Min(_limits.MaxToolCalls, 1024) * 3 + 7)
            { SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false });
            _deadline = new CancellationTokenSource(_limits.MaxWallTime);
            _callerRegistration = caller.Register(() => Stop(PtcRunStatus.Cancelled, "cancelled", "The caller cancelled execution."));
            _deadlineRegistration = _deadline.Token.Register(() => Stop(PtcRunStatus.TimedOut, "deadline_exceeded", "The run deadline elapsed."));
        }

        public IReadOnlyList<PtcToolDefinition> Tools { get; private set; } = [];

        private JsonElement Snapshot()
        {
            if (_request.Code is null || _request.Code.Length > _limits.MaxCodeChars)
                throw new PtcRuntimeException("invalid_code", "Program exceeds its character limit.", PtcRunStatus.Rejected);
            var inputs = PtcJson.Snapshot(_request.Inputs, _limits.MaxInputBytes, _limits.MaxJsonDepth, requireObject: true);
            var source = _host.Tools.ToArray();
            if (source.Length > _limits.MaxToolDefinitions)
                throw new PtcRuntimeException("data_limit_exceeded", "Tool catalog has too many definitions.", PtcRunStatus.Rejected);
            var all = new Dictionary<string, PtcToolDefinition>(StringComparer.Ordinal);
            foreach (var tool in source)
            {
                if (tool.Name is null || tool.Name.Length is < 1 or > 256 || all.ContainsKey(tool.Name))
                    throw new ArgumentException("Tool names must be unique ordinal strings of 1–256 characters.");
                PtcJson.CountText(tool.Name);
                PtcJson.CountText(tool.Description);
                var schema = PtcJson.Snapshot(tool.InputSchema, _limits.MaxCatalogBytes, _limits.MaxJsonDepth);
                PtcSchema.AssertSupported(schema);
                all.Add(tool.Name, tool with { InputSchema = schema });
            }
            var catalogBytes = JsonSerializer.SerializeToUtf8Bytes(all.Values.ToArray(), PtcJson.Options).Length;
            if (catalogBytes > _limits.MaxCatalogBytes)
                throw new PtcRuntimeException("data_limit_exceeded", "Tool catalog exceeds its byte limit.", PtcRunStatus.Rejected);
            var allowed = _request.AllowedTools.ToFrozenSet(StringComparer.Ordinal);
            _definitions = all.Where(item => allowed.Contains(item.Key) && item.Key != "execute_program")
                .ToFrozenDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
            Tools = Array.AsReadOnly(_definitions.Values.OrderBy(item => item.Name, StringComparer.Ordinal).ToArray());
            AddData(catalogBytes + PtcJson.ByteCount(inputs));
            return inputs;
        }

        public async Task<PtcExecutionResult> ExecuteAsync(IPtcScriptRuntime runtime)
        {
            var eventPump = DeliverEventsAsync();
            Event("RunStarted");
            var scheduler = ScheduleAsync();
            Task? script = null;
            try
            {
                var inputs = Snapshot();
                ThrowIfStopped();
                // One asynchronous engine owner. No UI-thread interpretation and no synchronous I/O bridge.
                script = Task.Run(async () =>
                {
                    Event("ScriptStarted");
                    var scriptStatus = PtcRunStatus.Completed;
                    string? scriptError = null;
                    try { await runtime.RunAsync(_request.Code, inputs, this, _limits, _token).ConfigureAwait(false); }
                    catch (PtcRuntimeException ex)
                    {
                        scriptStatus = ex.Status;
                        scriptError = ex.Code;
                        throw;
                    }
                    catch (OperationCanceledException) when (_token.IsCancellationRequested)
                    {
                        lock (_gate) { scriptStatus = _status; scriptError = _failure?.Code; }
                        if (scriptStatus == PtcRunStatus.Completed) scriptStatus = PtcRunStatus.Cancelled;
                        throw;
                    }
                    catch
                    {
                        scriptStatus = PtcRunStatus.Failed;
                        scriptError = "script_error";
                        throw;
                    }
                    finally { Event("ScriptFinished", error: scriptError, runStatus: scriptStatus); }
                });
                await script.WaitAsync(_token).ConfigureAwait(false);
                lock (_gate)
                {
                    _accepting = false;
                    if (_calls.Any(call => !call.Terminal))
                        Stop(PtcRunStatus.Failed, "script_error", "Program ended with unfinished tool promises.");
                }
            }
            catch (PtcRuntimeException ex) { Stop(ex.Status, ex.Code, ex.Message); }
            catch (OperationCanceledException) when (_token.IsCancellationRequested) { /* The first-reason latch already owns classification. */ }
            catch (ArgumentException)
            {
                // Invalid host catalogs/configuration are caller errors, never successful executions.
                Stop(PtcRunStatus.Failed, "host_error", "Invalid host tool catalog.");
                throw;
            }
            catch (Exception) { Stop(PtcRunStatus.Failed, "host_error", "The script runtime failed."); }
            finally
            {
                lock (_gate) _accepting = false;
                _queue.Writer.TryComplete();
                await DrainAsync(scheduler, script).ConfigureAwait(false);
                lock (_gate)
                {
                    foreach (var call in _calls.Where(call => !call.Terminal))
                    {
                        var state = call.State == PtcCallState.Running ? PtcCallState.OutcomeUnknown : PtcCallState.NotStarted;
                        Finish(call, state, PtcToolResult.Error(state == PtcCallState.OutcomeUnknown ? "outcome_unknown" : "cancelled",
                            state == PtcCallState.OutcomeUnknown ? "Host did not finish within the drain deadline." : "Call never started."));
                    }
                    _sealed = true;
                }
                Event("RunFinished", error: _failure?.Code);
                _events.Writer.TryComplete();
                try { await eventPump.ConfigureAwait(false); }
                catch (Exception) { Stop(PtcRunStatus.Failed, "observer_error", "Execution event delivery failed."); }
            }
            lock (_gate)
                return new PtcExecutionResult(_request.Context, _status, _output.ToArray(),
                    _calls.Select(call => new PtcToolRecord(call.Call, call.State, call.Result, call.Duration)
                    { RawResultBytes = call.RawBytes, ResultDelivered = call.Delivered, DeliveryErrorCode = call.DeliveryError }).ToArray(),
                    _artifacts.Values.ToArray(),
                    new PtcRunMetrics(_attempted, _startedCalls, _finishedCalls, _resultBytes, _outputBytes, _peak, Stopwatch.GetElapsedTime(_started)), _failure);
        }

        public ValueTask<PtcToolResult> CallAsync(string name, JsonElement arguments, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                ThrowIfStopped();
                if (!_accepting) throw new PtcRuntimeException("cancelled", "Execution is closed.", PtcRunStatus.Cancelled);
                cancellationToken.ThrowIfCancellationRequested();
                var index = ++_attempted;
                var call = new Pending(new PtcToolCall($"{_request.Context.RunId}:{index}", name ?? string.Empty,
                    JsonSerializer.SerializeToElement(new { })));
                _calls.Add(call);
                Event("CallQueued", call);
                if (index > _limits.MaxToolCalls)
                {
                    Stop(PtcRunStatus.BudgetExceeded, "call_limit_exceeded", "Tool call attempt limit exceeded.");
                    Finish(call, PtcCallState.NotStarted, PtcToolResult.Error("call_limit_exceeded", "Tool call attempt limit exceeded."));
                    return new(call.Completion.Task);
                }
                try
                {
                    var args = PtcJson.Snapshot(arguments, _limits.MaxArgumentsBytes, _limits.MaxJsonDepth, requireObject: true);
                    call = ReplaceArguments(call, args);
                    AddData(PtcJson.ByteCount(args) + Encoding.UTF8.GetByteCount(call.Call.Name));
                    if (string.IsNullOrEmpty(name) || name.Length > 256 || name == "execute_program" || !_definitions.TryGetValue(name, out var definition))
                    {
                        Finish(call, PtcCallState.Denied, PtcToolResult.Error("tool_not_available", "Tool is outside this run's grant."));
                        return new(call.Completion.Task);
                    }
                    if (!PtcSchema.IsValid(definition.InputSchema, args))
                    {
                        Finish(call, PtcCallState.Denied, PtcToolResult.Error("invalid_arguments", "Arguments do not match the tool schema."));
                        return new(call.Completion.Task);
                    }
                }
                catch (PtcRuntimeException ex)
                {
                    Finish(call, PtcCallState.Denied, PtcToolResult.Error(ex.Code, ex.Message));
                    return new(call.Completion.Task);
                }
                if (_pending >= _limits.MaxPendingCalls || !_queue.Writer.TryWrite(call))
                {
                    Stop(PtcRunStatus.BudgetExceeded, "call_limit_exceeded", "Pending tool queue limit exceeded.");
                    Finish(call, PtcCallState.NotStarted, PtcToolResult.Error("call_limit_exceeded", "Pending tool queue limit exceeded."));
                }
                else _pending++;
                return new(call.Completion.Task);
            }
        }

        private Pending ReplaceArguments(Pending call, JsonElement args)
        {
            var replacement = new Pending(call.Call with { Arguments = args });
            _calls[^1] = replacement;
            return replacement;
        }

        public void Emit(JsonElement value)
        {
            lock (_gate)
            {
                ThrowIfStopped();
                if (!_accepting || _sealed) throw new PtcRuntimeException("cancelled", "Execution is closed.", PtcRunStatus.Cancelled);
                JsonElement snapshot;
                try { snapshot = PtcJson.Snapshot(value, _limits.MaxOutputBytes, _limits.MaxJsonDepth); }
                catch (PtcRuntimeException ex)
                {
                    Stop(PtcRunStatus.BudgetExceeded, "output_limit_exceeded", "Explicit output is invalid or exceeds its JSON limit.");
                    throw new PtcRuntimeException("output_limit_exceeded", ex.Message, PtcRunStatus.BudgetExceeded);
                }
                var bytes = PtcJson.ByteCount(snapshot);
                if ((long)_outputBytes + bytes > _limits.MaxOutputBytes)
                {
                    Stop(PtcRunStatus.BudgetExceeded, "output_limit_exceeded", "Explicit output byte limit exceeded.");
                    ThrowIfStopped();
                }
                _output.Add(snapshot);
                _outputBytes += bytes;
            }
        }

        private async Task ScheduleAsync()
        {
            var running = new List<Task>(_limits.MaxConcurrency);
            try
            {
                await foreach (var call in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
                {
                    lock (_gate) _pending--;
                    PtcScheduling scheduling;
                    try { scheduling = _host.GetScheduling(call.Call, _request.Context); }
                    catch (Exception)
                    {
                        Stop(PtcRunStatus.Failed, "host_error", "Host scheduling failed.");
                        scheduling = PtcScheduling.Serial;
                    }
                    if (scheduling != PtcScheduling.ParallelReadOnly)
                    {
                        await Task.WhenAll(running).ConfigureAwait(false);
                        running.Clear();
                        await InvokeAsync(call).ConfigureAwait(false);
                    }
                    else
                    {
                        if (running.Count >= _limits.MaxConcurrency)
                        {
                            await Task.WhenAny(running).ConfigureAwait(false);
                            running.RemoveAll(task => task.IsCompleted);
                        }
                        // A policy can change while waiting for a slot. Reclassify before starting.
                        if (_host.GetScheduling(call.Call, _request.Context) != PtcScheduling.ParallelReadOnly)
                        {
                            await Task.WhenAll(running).ConfigureAwait(false);
                            running.Clear();
                            await InvokeAsync(call).ConfigureAwait(false);
                        }
                        else running.Add(InvokeAsync(call));
                    }
                }
                await Task.WhenAll(running).ConfigureAwait(false);
            }
            catch (Exception) { Stop(PtcRunStatus.Failed, "host_error", "Tool scheduler failed."); }
        }

        private async Task InvokeAsync(Pending call)
        {
            PtcCallState? observedState = null;
            lock (_gate)
            {
                if (_failure is not null || _sealed || call.Terminal)
                {
                    if (!call.Terminal) Finish(call, PtcCallState.NotStarted, PtcToolResult.Error(_failure?.Code ?? "cancelled", "Call never started."));
                    return;
                }
                call.State = PtcCallState.Running;
                call.StartedAt = Stopwatch.GetTimestamp();
                _startedCalls++;
                _live++;
                _peak = Math.Max(_peak, _live);
                Event("CallStarted", call);
            }
            using var toolTimeout = new CancellationTokenSource(_limits.MaxToolTime);
            using var registration = toolTimeout.Token.Register(() => Stop(PtcRunStatus.TimedOut, "deadline_exceeded", "The tool/approval deadline elapsed."));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_token, toolTimeout.Token);
            try
            {
                linked.Token.ThrowIfCancellationRequested();
                // A host must reauthorize here. This is the sole actual invocation path.
                var result = await _host.InvokeAsync(call.Call, _request.Context, linked.Token).ConfigureAwait(false);
                observedState = result.Success ? PtcCallState.Succeeded
                    : result.ErrorCode is "tool_denied" or "tool_not_available" ? PtcCallState.Denied : PtcCallState.Failed;
                lock (_gate)
                {
                    if (_sealed || call.Terminal) return;
                    CaptureArtifacts(result.Artifacts);
                }
                PtcJson.CountText(result.Message);
                if (result.ErrorCode is not null) PtcJson.CountText(result.ErrorCode);
                var json = JsonSerializer.Serialize(result, PtcJson.Options);
                var bytes = Encoding.UTF8.GetByteCount(json);
                lock (_gate)
                {
                    if (_sealed || call.Terminal) return; // A late completion cannot mutate finalized/other run records.
                    call.RawBytes = bytes;
                    _resultBytes += bytes;
                    var state = observedState.Value;
                    if (bytes > _limits.MaxToolResultBytes || _dataBytes + bytes > _limits.MaxRunDataBytes)
                    {
                        call.DeliveryError = "data_limit_exceeded";
                        Stop(PtcRunStatus.BudgetExceeded, "data_limit_exceeded", "Tool ran, but its result exceeds the data budget.");
                        Finish(call, state, null);
                        return;
                    }
                    _dataBytes += bytes;
                    var safe = PtcJson.Parse(json, _limits.MaxToolResultBytes, _limits.MaxJsonDepth);
                    var snapshot = JsonSerializer.Deserialize<PtcToolResult>(safe, PtcJson.Options)
                        ?? throw new PtcRuntimeException("host_error", "Host returned an invalid tool result.");
                    call.Delivered = _failure is null;
                    Finish(call, state, snapshot);
                }
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                lock (_gate) if (!_sealed && !call.Terminal)
                    Finish(call, PtcCallState.Cancelled, PtcToolResult.Error(_failure?.Code ?? "cancelled", "Tool was cancelled."));
            }
            catch (PtcRuntimeException ex)
            {
                Stop(ex.Status, ex.Code, ex.Message);
                lock (_gate) if (!_sealed && !call.Terminal)
                {
                    call.DeliveryError = ex.Code;
                    Finish(call, observedState ?? PtcCallState.Failed, null);
                }
            }
            catch (Exception)
            {
                // Host exception text/stack never crosses the JSON boundary.
                Stop(PtcRunStatus.Failed, "host_error", "Host tool invocation failed.");
                lock (_gate) if (!_sealed && !call.Terminal)
                    Finish(call, PtcCallState.Failed, PtcToolResult.Error("host_error", "Host tool invocation failed."));
            }
        }

        private void CaptureArtifacts(IReadOnlyList<PtcArtifactReference> references)
        {
            foreach (var item in references)
            {
                PtcJson.CountText(item.Id);
                PtcJson.CountText(item.MediaType);
                PtcJson.CountText(item.DisplayName);
                if (_artifacts.ContainsKey(item.Id)) continue;
                if (string.IsNullOrWhiteSpace(item.Id) || _artifacts.Count >= _limits.MaxArtifactReferences)
                {
                    Stop(PtcRunStatus.BudgetExceeded, "data_limit_exceeded", "Artifact reference limit exceeded.");
                    return;
                }
                _artifacts.Add(item.Id, item with { });
            }
        }

        private void Finish(Pending call, PtcCallState state, PtcToolResult? result)
        {
            if (call.Terminal) return;
            if (call.State == PtcCallState.Running)
            {
                _live--;
                if (state != PtcCallState.OutcomeUnknown) _finishedCalls++;
                call.Duration = Stopwatch.GetElapsedTime(call.StartedAt);
            }
            call.State = state;
            call.Result = result;
            call.Terminal = true;
            Event("CallFinished", call, call.DeliveryError ?? result?.ErrorCode);
            call.Completion.TrySetResult(call.Delivered ? result!
                : result is { Success: false } ? result : PtcToolResult.Error(call.DeliveryError ?? _failure?.Code ?? "cancelled", "Result was not delivered to the program."));
        }

        private void AddData(long bytes)
        {
            lock (_gate)
            {
                if (bytes > _limits.MaxRunDataBytes - _dataBytes)
                {
                    Stop(PtcRunStatus.BudgetExceeded, "data_limit_exceeded", "Run data byte limit exceeded.");
                    ThrowIfStopped();
                }
                _dataBytes += bytes;
            }
        }

        private void ThrowIfStopped()
        {
            if (_failure is { } failure) throw new PtcRuntimeException(failure.Code, failure.Message, _status);
        }

        private void Stop(PtcRunStatus status, string code, string message)
        {
            lock (_gate)
            {
                if (_failure is not null) return;
                _failure = new PtcFailure(code, message);
                _status = status;
            }
            // CancelAsync signals immediately but never blocks the engine owner on arbitrary host callbacks.
            _ = ObserveCancellationAsync();
        }

        private async Task ObserveCancellationAsync()
        {
            try { await _stop.CancelAsync().ConfigureAwait(false); }
            catch (Exception) { /* Host cancellation callbacks cannot replace the already-latched stop reason. */ }
        }

        private void Event(string kind, Pending? call = null, string? error = null, PtcRunStatus? runStatus = null)
        {
            if (_sink is null) return;
            // Capture at the source, not in DeliverEventsAsync: a slow observer can lag behind
            // already-running concurrent tool calls by an arbitrary amount of time.
            var occurredAt = Stopwatch.GetTimestamp();
            lock (_gate)
            {
                // A noncooperative script may finish after bounded drain. Never publish a late
                // event to the completed run or touch a reused observer.
                if (_sealed && kind != "RunFinished") return;
                if (!_events.Writer.TryWrite(new PtcEvent(_request.Context.RunId, _request.Context.ParentToolCallId,
                        ++_sequence, kind, call?.Call.CallId, call?.Call.Name, error)
                    {
                        OccurredTimestamp = occurredAt,
                        CallState = call?.State,
                        RunStatus = kind == "RunFinished" ? _status : runStatus
                    }))
                    Stop(PtcRunStatus.Failed, "observer_error", "Bounded event delivery queue is full.");
            }
        }

        private async Task DeliverEventsAsync()
        {
            if (_sink is null) return;
            await foreach (var item in _events.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                using var cleanup = new CancellationTokenSource(_limits.EventDeliveryTimeout);
                Task? delivery = null;
                try
                {
                    delivery = _sink.OnEventAsync(item, cleanup.Token).AsTask();
                    await delivery.WaitAsync(cleanup.Token).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    Stop(PtcRunStatus.Failed, "observer_error", "Execution observer failed or exceeded its delivery deadline.");
                    if (delivery is not null) _ = ObserveAsync(delivery);
                    return;
                }
            }
        }

        private async Task DrainAsync(Task scheduler, Task? script)
        {
            var all = script is null ? scheduler : Task.WhenAll(scheduler, ObserveAsync(script));
            try { await all.WaitAsync(_limits.DrainTimeout).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                Stop(PtcRunStatus.Failed, "host_error", "In-flight work exceeded the drain deadline; inspect unknown outcomes.");
                _ = ObserveAsync(all);
            }
        }

        private static async Task ObserveAsync(Task task)
        {
            try { await task.ConfigureAwait(false); }
            catch (Exception) { /* Late runtime/host faults are observed; a finalized run is immutable and is never replayed. */ }
        }

        public void Dispose()
        {
            _callerRegistration.Dispose();
            _deadlineRegistration.Dispose();
            _deadline.Dispose();
            // Retain the stop source for late host callbacks. Disposing it here would make a late
            // cancellation registration throw. It is collected with this non-reused run.
        }
    }
}
