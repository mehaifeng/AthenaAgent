using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Ptc.Core;
using Xunit;

namespace Ptc.Core.Tests;

public sealed class CoordinatorTests
{
    internal static JsonElement Json(string value = "{}") => PtcJson.Parse(value);
    internal static PtcExecutionRequest Request(PtcExecutionLimits? limits = null, params string[] allowed)
        => new("test", Json(), new HashSet<string>(allowed.Length == 0 ? ["read", "write"] : allowed, StringComparer.Ordinal),
            new("run", "parent", "conversation", "workspace", "task"), limits ?? new());
    internal static PtcToolResult Ok(object? value = null)
        => new(true, "ok", JsonSerializer.SerializeToElement(value ?? new { value = 1 }), null, []);
    internal sealed class Runtime(Func<IPtcScriptBridge, Task> body) : IPtcScriptRuntime
    {
        public async ValueTask RunAsync(string code, JsonElement inputs, IPtcScriptBridge bridge, PtcExecutionLimits limits, CancellationToken cancellationToken)
            => await body(bridge);
    }
    internal sealed class Host : IPtcToolHost
    {
        public IReadOnlyList<PtcToolDefinition> Tools { get; set; } =
        [new("read", "read", Json("{\"type\":\"object\",\"additionalProperties\":false}")), new("write", "write", Json())];
        public Func<PtcToolCall, CancellationToken, ValueTask<PtcToolResult>> Handler { get; set; } = (_, _) => ValueTask.FromResult(Ok());
        public Func<PtcToolCall, PtcScheduling> Scheduling { get; set; } = call => call.Name == "read" ? PtcScheduling.ParallelReadOnly : PtcScheduling.Serial;
        public ConcurrentQueue<PtcToolCall> Invoked { get; } = new();
        public PtcScheduling GetScheduling(PtcToolCall call, PtcRunContext context) => Scheduling(call);
        public ValueTask<PtcToolResult> InvokeAsync(PtcToolCall call, PtcRunContext context, CancellationToken cancellationToken)
        { Invoked.Enqueue(call); return Handler(call, cancellationToken); }
    }
    internal sealed class Sink : IPtcEventSink
    {
        public List<PtcEvent> Items { get; } = [];
        public bool Throw { get; set; }
        public ValueTask OnEventAsync(PtcEvent item, CancellationToken cancellationToken)
        {
            if (Throw) throw new InvalidOperationException("secret observer stack");
            Items.Add(item);
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public void TestHarnessFailureProbe() => Assert.NotEqual("1", Environment.GetEnvironmentVariable("PTC_ASSERTION_PROBE"));

    [Fact]
    public async Task UnauthorizedAttemptsConsumeBudgetAndStopCannotBeCaught()
    {
        var host = new Host();
        var executor = new PtcExecutor(new Runtime(async bridge =>
        {
            for (var i = 0; i < 8; i++)
            {
                try { await bridge.CallAsync("READ", Json(), default); }
                catch (PtcRuntimeException) { /* Malicious script attempts to recover. */ }
            }
            try { bridge.Emit(Json("{\"lie\":\"success\"}")); }
            catch (PtcRuntimeException) { }
        }));
        var result = await executor.ExecuteAsync(Request(new() { MaxToolCalls = 2 }), host);
        Assert.Equal(PtcRunStatus.BudgetExceeded, result.Status);
        Assert.Equal("call_limit_exceeded", result.Failure?.Code);
        Assert.Equal(3, result.Metrics.CallsAttempted);
        Assert.Empty(host.Invoked);
        Assert.Empty(result.Output);
        Assert.Equal(3, result.ToolRecords.Count);
        Assert.Equal(3, result.ToolRecords.Select(item => item.Call.CallId).Distinct().Count());
    }

    [Fact]
    public async Task SerialCallIsAnOrderBarrier()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new ConcurrentQueue<string>();
        var number = 0;
        var host = new Host
        {
            Handler = async (call, token) =>
            {
                var index = Interlocked.Increment(ref number);
                order.Enqueue($"start:{call.Name}:{index}");
                if (index == 1) { firstStarted.SetResult(); await release.Task.WaitAsync(token); }
                order.Enqueue($"end:{call.Name}:{index}");
                return Ok();
            }
        };
        var executor = new PtcExecutor(new Runtime(async bridge =>
        {
            var tasks = new[] { bridge.CallAsync("read", Json(), default).AsTask(),
                bridge.CallAsync("write", Json(), default).AsTask(), bridge.CallAsync("read", Json(), default).AsTask() };
            await Task.WhenAll(tasks);
        }));
        var execution = executor.ExecuteAsync(Request(), host);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(host.Invoked);
        release.SetResult();
        var result = await execution;
        Assert.Equal(PtcRunStatus.Completed, result.Status);
        Assert.Equal(["start:read:1", "end:read:1", "start:write:2", "end:write:2", "start:read:3", "end:read:3"], order.ToArray());
    }

    [Fact]
    public async Task BoundedReadConcurrencyUsesRealOverlap()
    {
        var overlap = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var live = 0;
        var peak = 0;
        var host = new Host { Handler = async (_, token) =>
        {
            var active = Interlocked.Increment(ref live);
            InterlockedExtensions.Max(ref peak, active);
            if (active == 2) overlap.TrySetResult();
            await release.Task.WaitAsync(token);
            Interlocked.Decrement(ref live);
            return Ok();
        } };
        var resultTask = new PtcExecutor(new Runtime(async bridge =>
            await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => bridge.CallAsync("read", Json(), default).AsTask()))))
            .ExecuteAsync(Request(new() { MaxConcurrency = 2 }), host);
        await overlap.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, host.Invoked.Count);
        release.SetResult();
        var result = await resultTask;
        Assert.Equal(PtcRunStatus.Completed, result.Status);
        Assert.Equal(2, peak);
        Assert.Equal(2, result.Metrics.PeakConcurrency);
        Assert.Equal(5, result.Metrics.CallsFinished);
    }

    [Fact]
    public async Task ObserverFailureStopsRunAndNeverLeaksException()
    {
        var sink = new Sink { Throw = true };
        var result = await new PtcExecutor(new Runtime(bridge =>
        { bridge.Emit(Json("1")); return Task.CompletedTask; })).ExecuteAsync(Request(), new Host(), sink);
        Assert.Equal(PtcRunStatus.Failed, result.Status);
        Assert.Equal("observer_error", result.Failure?.Code);
        Assert.DoesNotContain("secret", PtcModelResultProjector.Project(result, 1024));
    }

    [Fact]
    public async Task EventsAreOrderedPairedAndContainOnlyMetadata()
    {
        var sink = new Sink();
        var result = await new PtcExecutor(new Runtime(async bridge =>
        { await bridge.CallAsync("read", Json(), default); await bridge.CallAsync("unknown", Json(), default); }))
            .ExecuteAsync(Request(), new Host(), sink);
        Assert.Equal(PtcRunStatus.Completed, result.Status);
        Assert.Equal("RunStarted", sink.Items[0].Kind);
        Assert.Equal("RunFinished", sink.Items[^1].Kind);
        Assert.Equal(Enumerable.Range(1, sink.Items.Count).Select(i => (long)i), sink.Items.Select(item => item.Sequence));
        foreach (var call in result.ToolRecords)
        {
            Assert.Single(sink.Items, item => item.CallId == call.Call.CallId && item.Kind == "CallFinished");
            Assert.All(sink.Items.Where(item => item.CallId == call.Call.CallId), item => Assert.Equal("parent", item.ParentToolCallId));
        }
        Assert.DoesNotContain("arguments", JsonSerializer.Serialize(sink.Items, PtcJson.Options));
    }

    [Fact]
    public async Task ToolTimeoutCancelsAndDoesNotStartQueuedCalls()
    {
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new Host { Handler = async (_, token) =>
        {
            using var registration = token.Register(() => observed.TrySetResult());
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Ok();
        } };
        var result = await new PtcExecutor(new Runtime(async bridge =>
            await Task.WhenAll(bridge.CallAsync("write", Json(), default).AsTask(), bridge.CallAsync("read", Json(), default).AsTask())))
            .ExecuteAsync(Request(new() { MaxToolTime = TimeSpan.FromMilliseconds(100) }), host);
        Assert.True(observed.Task.IsCompleted);
        Assert.Equal(PtcRunStatus.TimedOut, result.Status);
        Assert.Equal("deadline_exceeded", result.Failure?.Code);
        Assert.Single(host.Invoked);
        Assert.Equal(PtcCallState.NotStarted, result.ToolRecords[1].State);
    }

    [Fact]
    public async Task IgnoredCancellationIsUnknownAndLateCompletionCannotMutateResult()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<PtcToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = new CancellationTokenSource();
        var host = new Host { Handler = (_, _) => { entered.TrySetResult(); return new(release.Task); } };
        var execution = new PtcExecutor(new Runtime(async bridge => { await bridge.CallAsync("read", Json(), default); }))
            .ExecuteAsync(Request(new() { DrainTimeout = TimeSpan.FromMilliseconds(50) }), host, cancellationToken: cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancel.Cancel();
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PtcRunStatus.Cancelled, result.Status);
        Assert.Equal(PtcCallState.OutcomeUnknown, Assert.Single(result.ToolRecords).State);
        Assert.Equal(0, result.Metrics.CallsFinished);
        var before = JsonSerializer.Serialize(result, PtcJson.Options);
        release.SetResult(Ok(new { late = true }));
        await Task.Yield();
        Assert.Equal(before, JsonSerializer.Serialize(result, PtcJson.Options));
    }

    [Fact]
    public async Task UnawaitedCallIsCancelledAndDrained()
    {
        var host = new Host { Handler = async (_, token) => { await Task.Delay(Timeout.InfiniteTimeSpan, token); return Ok(); } };
        var result = await new PtcExecutor(new Runtime(bridge =>
        { _ = bridge.CallAsync("read", Json(), default).AsTask(); return Task.CompletedTask; }))
            .ExecuteAsync(Request(), host);
        Assert.Equal(PtcRunStatus.Failed, result.Status);
        Assert.Equal("script_error", result.Failure?.Code);
        Assert.DoesNotContain(result.ToolRecords, item => item.State is PtcCallState.Running or PtcCallState.Queued);
    }

    [Fact]
    public async Task OverlargeExecutedResultIsNotMisreportedAsNotStarted()
    {
        var host = new Host { Handler = (_, _) => ValueTask.FromResult(Ok(new { huge = new string('x', 2000) })) };
        var result = await new PtcExecutor(new Runtime(async bridge => { await bridge.CallAsync("read", Json(), default); }))
            .ExecuteAsync(Request(new() { MaxToolResultBytes = 256 }), host);
        Assert.Equal(PtcRunStatus.BudgetExceeded, result.Status);
        var record = Assert.Single(result.ToolRecords);
        Assert.Equal(PtcCallState.Succeeded, record.State);
        Assert.False(record.ResultDelivered);
        Assert.Null(record.Result);
        Assert.Equal("data_limit_exceeded", record.DeliveryErrorCode);
        Assert.True(record.RawResultBytes > 2000);
        Assert.Single(host.Invoked);
    }

    [Fact]
    public async Task LargeRawResultDoesNotEnterModelProjection()
    {
        var host = new Host { Handler = (_, _) => ValueTask.FromResult(Ok(new { hugeSecret = new string('x', 20_000) })) };
        var result = await new PtcExecutor(new Runtime(async bridge =>
        {
            var raw = await bridge.CallAsync("read", Json(), default);
            bridge.Emit(JsonSerializer.SerializeToElement(new { count = raw.Data!.Value.GetProperty("hugeSecret").GetString()!.Length }));
        })).ExecuteAsync(Request(), host);
        var projection = PtcModelResultProjector.Project(result, 1024, "ptc:opaque");
        Assert.True(Encoding.UTF8.GetByteCount(projection) < 1024);
        Assert.DoesNotContain("hugeSecret", projection);
        Assert.DoesNotContain("toolRecords", projection);
        Assert.DoesNotContain("workspace", projection);
        Assert.Equal(20_000, Json(projection).GetProperty("output")[0].GetProperty("count").GetInt32());
        Assert.Contains("hugeSecret", Assert.Single(result.ToolRecords).Result!.Data!.Value.GetRawText());
    }

    [Fact]
    public async Task OutputLimitRetainsPriorOutputAndProjectionFallbackIsValidJson()
    {
        var result = await new PtcExecutor(new Runtime(bridge =>
        {
            bridge.Emit(Json("1"));
            bridge.Emit(JsonSerializer.SerializeToElement(new string('x', 1000)));
            return Task.CompletedTask;
        })).ExecuteAsync(Request(new() { MaxOutputBytes = 64 }), new Host());
        Assert.Equal(PtcRunStatus.BudgetExceeded, result.Status);
        Assert.Single(result.Output);
        Assert.Equal("output_limit_exceeded", result.Failure?.Code);
        var large = result with { Output = [JsonSerializer.SerializeToElement(new string('x', 1000))] };
        var small = PtcModelResultProjector.Project(large, 256);
        Assert.Equal("model_output_limit_exceeded", Json(small).GetProperty("failure").GetProperty("code").GetString());
        Assert.True(Encoding.UTF8.GetByteCount(small) <= 256);
    }

    [Fact]
    public async Task ArtifactsSurviveSubsequentScriptFailure()
    {
        var host = new Host { Handler = (_, _) => ValueTask.FromResult(Ok() with
        { Artifacts = [new("a", "image/png", "result"), new("a", "image/png", "result")] }) };
        var result = await new PtcExecutor(new Runtime(async bridge =>
        { await bridge.CallAsync("read", Json(), default); throw new PtcRuntimeException("script_error", "Script failed."); }))
            .ExecuteAsync(Request(), host);
        Assert.Equal(PtcRunStatus.Failed, result.Status);
        Assert.Equal("a", Assert.Single(result.Artifacts).Id);
        Assert.Single(host.Invoked);
    }

    [Fact]
    public async Task HostExceptionIsSanitizedAndNeverRetried()
    {
        var host = new Host { Handler = (_, _) => throw new InvalidOperationException("secret /private/path stack") };
        var result = await new PtcExecutor(new Runtime(async bridge => { await bridge.CallAsync("read", Json(), default); }))
            .ExecuteAsync(Request(), host);
        Assert.Equal("host_error", result.Failure?.Code);
        Assert.Single(host.Invoked);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(result, PtcJson.Options));
    }

    [Fact]
    public async Task UnsupportedSchemaRejectsBeforeAnyTool()
    {
        var host = new Host { Tools = [new("read", "read", Json("{\"$ref\":\"https://example.invalid/schema\"}"))] };
        var result = await new PtcExecutor(new Runtime(async bridge => { await bridge.CallAsync("read", Json(), default); }))
            .ExecuteAsync(Request(), host);
        Assert.Equal(PtcRunStatus.Rejected, result.Status);
        Assert.Equal("schema_unsupported", result.Failure?.Code);
        Assert.Empty(host.Invoked);
    }

    [Fact]
    public async Task SnapshotClonesDocumentsAndOriginalCollections()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var document = JsonDocument.Parse("{\"type\":\"object\",\"additionalProperties\":false}");
        var definitions = new List<PtcToolDefinition> { new("read", "read", document.RootElement) };
        var allowed = new HashSet<string>(["read"], StringComparer.Ordinal);
        var host = new Host { Tools = definitions };
        var request = Request() with { AllowedTools = allowed };
        var execution = new PtcExecutor(new Runtime(async bridge =>
        {
            ready.SetResult();
            await proceed.Task;
            Assert.Single(bridge.Tools);
            Assert.True((await bridge.CallAsync("read", Json(), default)).Success);
        })).ExecuteAsync(request, host);
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        document.Dispose();
        definitions.Clear(); allowed.Clear();
        proceed.SetResult();
        Assert.Equal(PtcRunStatus.Completed, (await execution).Status);
        Assert.Single(host.Invoked);
    }

    [Fact]
    public async Task InvalidConfigurationThrowsBeforeExecution()
    {
        var host = new Host();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new PtcExecutor(new Runtime(_ => Task.CompletedTask))
            .ExecuteAsync(Request(new() { MaxToolCalls = 0 }), host));
        Assert.Empty(host.Invoked);
    }

    [Theory]
    [InlineData("{\"type\":\"string\",\"minLength\":2,\"maxLength\":3}", "\"😀a\"", true)]
    [InlineData("{\"type\":\"integer\",\"minimum\":1,\"maximum\":3}", "1.5", false)]
    [InlineData("{\"type\":\"integer\",\"minimum\":1,\"maximum\":3}", "2", true)]
    [InlineData("{\"type\":\"array\",\"minItems\":1,\"maxItems\":2,\"items\":{\"enum\":[1,2]}}", "[1,3]", false)]
    [InlineData("{\"required\":[\"x\"],\"properties\":{\"x\":{\"type\":\"boolean\"}},\"additionalProperties\":false}", "{\"x\":true,\"extra\":1}", false)]
    [InlineData("{\"required\":[\"x\"]}", "{}", false)]
    [InlineData("{\"properties\":{\"x\":{\"type\":\"boolean\"}}}", "{\"extra\":1}", true)]
    [InlineData("{}", "{\"extra\":1}", true)]
    [InlineData("{\"oneOf\":[{\"required\":[\"x\"]},{\"not\":{\"anyOf\":[{\"required\":[\"x\"]},{\"required\":[\"y\"]}]}}]}", "{\"y\":1}", false)]
    [InlineData("{\"oneOf\":[{\"required\":[\"x\"]},{\"not\":{\"anyOf\":[{\"required\":[\"x\"]},{\"required\":[\"y\"]}]}}]}", "{}", true)]
    [InlineData("{\"type\":[\"null\",\"string\"]}", "null", true)]
    public void SchemaSubsetHasExplicitSemantics(string schemaText, string valueText, bool valid)
    {
        var schema = Json(schemaText);
        PtcSchema.AssertSupported(schema);
        Assert.Equal(valid, PtcSchema.IsValid(schema, Json(valueText)));
    }

    [Theory]
    [InlineData("{\"pattern\":\"x\"}")]
    [InlineData("{\"format\":\"email\"}")]
    [InlineData("{\"$ref\":\"#/definitions/x\"}")]
    [InlineData("{\"properties\":{\"x\":{\"minLength\":-1}}}")]
    public void UnknownOrMalformedConstraintsFailClosed(string schema)
        => Assert.Equal("schema_unsupported", Assert.Throws<PtcRuntimeException>(() => PtcSchema.AssertSupported(Json(schema))).Code);

    [Theory]
    [InlineData("{\"x\":1,\"x\":2}")]
    [InlineData("{\"n\":9007199254740992}")]
    [InlineData("{\"n\":1e999}")]
    public void JsonBoundaryRejectsLossyOrDuplicateValues(string value)
        => Assert.Throws<PtcRuntimeException>(() => Json(value));

    [Fact]
    public void JsonBoundaryCountsUtf8AndDepth()
    {
        Assert.Throws<PtcRuntimeException>(() => PtcJson.Parse("\"中文\"", maxBytes: 6));
        Assert.Throws<PtcRuntimeException>(() => PtcJson.Parse("{\"a\":{\"b\":{}}}", maxDepth: 2));
    }
}

internal static class InterlockedExtensions
{
    internal static void Max(ref int target, int value)
    {
        int previous;
        do { previous = Volatile.Read(ref target); if (previous >= value) return; }
        while (Interlocked.CompareExchange(ref target, value, previous) != previous);
    }
}
