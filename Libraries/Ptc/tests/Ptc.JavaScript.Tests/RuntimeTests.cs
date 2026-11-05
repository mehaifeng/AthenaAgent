using System.Collections.Concurrent;
using System.Text.Json;
using Ptc.Core;
using Ptc.JavaScript;
using Xunit;

namespace Ptc.JavaScript.Tests;

public sealed class RuntimeTests
{
    private const string OrderProgram = """
        const listed = await tools.call("list_orders", {});
        if (!listed.success) { text({error: listed.errorCode}); return; }
        const details = await Promise.all(listed.data.ids.map(id => tools.call("get_order", {id})));
        const failed = details.filter(item => !item.success);
        if (failed.length) { text({failed: failed.map(item => item.errorCode)}); return; }
        const ids = details.map(item => item.data).filter(item => item.overdue).map(item => item.id);
        text({count: ids.length, ids});
        """;

    private static JsonElement Json(string value = "{}") => PtcJson.Parse(value);
    private static PtcToolResult Ok(object? value = null) => new(true, "ok", JsonSerializer.SerializeToElement(value ?? new { }), null, []);
    private static PtcExecutionRequest Request(string code, PtcExecutionLimits? limits = null, string run = "js")
        => new(code, Json("{\"label\":\"input\"}"), new HashSet<string>(["list_orders", "get_order", "write", "special['name']"], StringComparer.Ordinal),
            new(run, "parent-" + run, run, "workspace-" + run, "task-" + run), limits ?? new());

    private sealed class Host : IPtcToolHost
    {
        public IReadOnlyList<PtcToolDefinition> Tools { get; set; } =
        [new("list_orders", "Lists orders", Json("{\"type\":\"object\",\"additionalProperties\":false}")),
         new("get_order", "Gets one order", Json("{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"integer\",\"minimum\":1}},\"required\":[\"id\"],\"additionalProperties\":false}")),
         new("write", "Synthetic serial write", Json()), new("special['name']", "Special name", Json())];
        public ConcurrentQueue<(PtcToolCall Call, PtcRunContext Context)> Calls { get; } = new();
        public Func<PtcToolCall, PtcRunContext, CancellationToken, ValueTask<PtcToolResult>>? Custom { get; set; }
        public PtcScheduling GetScheduling(PtcToolCall call, PtcRunContext context)
            => call.Name == "write" ? PtcScheduling.Serial : PtcScheduling.ParallelReadOnly;
        public ValueTask<PtcToolResult> InvokeAsync(PtcToolCall call, PtcRunContext context, CancellationToken cancellationToken)
        {
            Calls.Enqueue((call, context));
            if (Custom is not null) return Custom(call, context, cancellationToken);
            return ValueTask.FromResult(call.Name switch
            {
                "list_orders" => Ok(new { ids = new[] { 1, 2, 3 } }),
                "get_order" => Ok(new { id = call.Arguments.GetProperty("id").GetInt32(), overdue = call.Arguments.GetProperty("id").GetInt32() != 2 }),
                _ => Ok()
            });
        }
    }

    private static Task<PtcExecutionResult> Execute(string code, Host? host = null, PtcExecutionLimits? limits = null, CancellationToken token = default)
        => new PtcExecutor(new JintScriptRuntime()).ExecuteAsync(Request(code, limits), host ?? new Host(), cancellationToken: token);

    [Fact]
    public async Task C01_DependencyChainConditionsAndAggregation()
    {
        var host = new Host();
        var result = await Execute(OrderProgram, host);
        Assert.Equal(PtcRunStatus.Completed, result.Status);
        Assert.Equal("{\"count\":2,\"ids\":[1,3]}", Assert.Single(result.Output).GetRawText());
        Assert.Equal(4, host.Calls.Count);
        Assert.Equal(4, result.Metrics.CallsStarted);
    }

    [Fact]
    public async Task C02_RealConcurrentIoAndReverseCompletionPreservePromiseOrder()
    {
        var overlap = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new[] { new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var started = 0;
        var completed = new ConcurrentQueue<int>();
        var host = new Host { Custom = async (call, _, token) =>
        {
            var id = call.Arguments.GetProperty("id").GetInt32();
            if (Interlocked.Increment(ref started) == 3) overlap.SetResult();
            await release[id - 1].Task.WaitAsync(token);
            completed.Enqueue(id);
            return Ok(new { id });
        } };
        var execution = Execute("const r = await Promise.all([1,2,3].map(id=>tools.call('get_order',{id}))); text(r.map(x=>x.data.id));", host);
        await overlap.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, host.Calls.Count);
        release[2].SetResult();
        await WaitUntil(() => completed.Count == 1);
        release[1].SetResult();
        await WaitUntil(() => completed.Count == 2);
        release[0].SetResult();
        var result = await execution;
        Assert.Equal(PtcRunStatus.Completed, result.Status);
        Assert.Equal([3, 2, 1], completed.ToArray());
        Assert.Equal("[1,2,3]", Assert.Single(result.Output).GetRawText());
        Assert.Equal(3, result.Metrics.PeakConcurrency);
    }

    [Fact]
    public async Task C03_SerialWriteDoesNotMoveAheadOrLetReadsCross()
    {
        var sequence = new ConcurrentQueue<string>();
        var host = new Host { Custom = async (call, _, _) =>
        {
            sequence.Enqueue("start:" + call.Name);
            await Task.Yield();
            sequence.Enqueue("end:" + call.Name);
            return Ok();
        } };
        var result = await Execute("await Promise.all([tools.call('list_orders',{}),tools.call('write',{}),tools.call('list_orders',{})]);", host);
        Assert.Equal(PtcRunStatus.Completed, result.Status);
        Assert.Equal(["start:list_orders", "end:list_orders", "start:write", "end:write", "start:list_orders", "end:list_orders"], sequence.ToArray());
    }

    [Fact]
    public async Task C04_OrdinalGrantAndSpecialNamesNeverBecomeSource()
    {
        var host = new Host();
        var result = await Execute("""
            const r = await Promise.allSettled([
              tools.call("special['name']",{}), tools.call("GET_ORDER",{id:1}),
              tools.call("execute_program",{}), tools.call("System.IO.File",{})]);
            text(r.map(x=>({status:x.status,success:x.value.success,error:x.value.errorCode})));
            """, host);
        Assert.Equal(PtcRunStatus.Completed, result.Status);
        Assert.Single(host.Calls);
        Assert.True(result.Output[0][0].GetProperty("success").GetBoolean());
        Assert.All(result.Output[0].EnumerateArray().Skip(1), item => Assert.Equal("tool_not_available", item.GetProperty("error").GetString()));
    }

    [Theory]
    [InlineData("{id:1,extra:true}")]
    [InlineData("{id:'one'}")]
    [InlineData("[]")]
    [InlineData("{id:1,bad:1n}")]
    [InlineData("{id:NaN}")]
    [InlineData("{id:Infinity}")]
    [InlineData("{id:9007199254740992}")]
    public async Task C05_InvalidArgumentsAreCountedButNeverExecuted(string args)
    {
        var host = new Host();
        var result = await Execute("text(await tools.call('get_order'," + args + "));", host);
        Assert.Equal(PtcRunStatus.Completed, result.Status);
        Assert.Empty(host.Calls);
        Assert.Equal(1, result.Metrics.CallsAttempted);
        Assert.Equal("invalid_arguments", Assert.Single(result.Output).GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task C06_ParallelAttemptsCannotOverrunCallBudget()
    {
        var host = new Host();
        var result = await Execute("try { await Promise.all(Array.from({length:20},()=>tools.call('missing',{}))); } catch {} text('not success');",
            host, new() { MaxToolCalls = 3 });
        Assert.Equal(PtcRunStatus.BudgetExceeded, result.Status);
        Assert.Equal("call_limit_exceeded", result.Failure?.Code);
        Assert.Equal(4, result.Metrics.CallsAttempted);
        Assert.Empty(host.Calls);
        Assert.Empty(result.Output);
    }

    [Theory]
    [InlineData("while(true) {}", "statement_limit_exceeded")]
    [InlineData("await Promise.resolve(); while(true) {}", "statement_limit_exceeded")]
    [InlineData("try { while(true) {} } catch {} text('lie');", "statement_limit_exceeded")]
    [InlineData("function recurse(){return 1+recurse();} recurse();", "recursion_limit_exceeded")]
    [InlineData("await tools.call('list_orders',{}); function recurse(){return 1+recurse();} recurse();", "recursion_limit_exceeded")]
    public async Task C07_InterpreterConstraintsSurviveAsyncResumption(string code, string error)
    {
        var result = await Execute(code, limits: new() { MaxStatements = 3000, MaxRecursionDepth = 16 });
        Assert.Equal(PtcRunStatus.BudgetExceeded, result.Status);
        Assert.Equal(error, result.Failure?.Code);
        Assert.Empty(result.Output);
    }

    [Fact]
    public async Task C07_MemoryAllocationIsBounded()
    {
        var result = await Execute("const s = 'x'.repeat(4000000); text(s);", limits: new() { EngineMemoryBytes = 1024 * 1024 });
        Assert.Equal(PtcRunStatus.BudgetExceeded, result.Status);
        Assert.Equal("engine_memory_limit_exceeded", result.Failure?.Code);
    }

    [Fact]
    public async Task C08_OutputLimitCannotBeCaughtToProduceSuccess()
    {
        var result = await Execute("text({first:true}); try {text('x'.repeat(1000));}catch{} text('lie');", limits: new() { MaxOutputBytes = 64 });
        Assert.Equal(PtcRunStatus.BudgetExceeded, result.Status);
        Assert.Equal("output_limit_exceeded", result.Failure?.Code);
        Assert.Single(result.Output);
    }

    [Fact]
    public async Task C09_CallerCancellationReachesIoAndSealsOutputs()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource();
        var host = new Host { Custom = async (_, _, token) =>
        {
            entered.SetResult();
            using var registration = token.Register(() => cancelled.TrySetResult());
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Ok();
        } };
        var execution = Execute("await tools.call('list_orders',{}); text('late');", host, token: stop.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        stop.Cancel();
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(cancelled.Task.IsCompleted);
        Assert.Equal(PtcRunStatus.Cancelled, result.Status);
        Assert.Empty(result.Output);
        Assert.DoesNotContain(result.ToolRecords, item => item.State is PtcCallState.Running or PtcCallState.Queued);
    }

    [Fact]
    public async Task C10_UnawaitedPromiseIsNotBackgroundWork()
    {
        var release = new TaskCompletionSource<PtcToolResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new Host { Custom = (_, _, _) => new(release.Task) };
        var result = await Execute("tools.call('list_orders',{});", host, new() { DrainTimeout = TimeSpan.FromMilliseconds(50) });
        Assert.Equal(PtcRunStatus.Failed, result.Status);
        Assert.DoesNotContain(result.ToolRecords, item => item.State is PtcCallState.Running or PtcCallState.Queued);
        release.SetResult(Ok()); // Late Jint settlement must never re-enter user code or mutate the returned record.
        await Task.Yield();
        Assert.Empty(result.Output);
    }

    [Fact]
    public async Task C11_ParallelRunsNeverShareEngineGlobalsOrHostContext()
    {
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        var host = new Host { Custom = async (_, context, token) =>
        {
            if (Interlocked.Increment(ref count) == 2) arrived.SetResult();
            await arrived.Task.WaitAsync(token);
            return Ok(new { conversation = context.ConversationId, workspace = context.WorkspaceId, task = context.DelegatedTask });
        } };
        var executor = new PtcExecutor(new JintScriptRuntime());
        var runs = await Task.WhenAll(new[] { "one", "two" }.Select(run => executor.ExecuteAsync(
            Request("globalThis.privateValue=inputs.label; const r=await tools.call('list_orders',{}); text(r.data);", run: run), host)));
        for (var i = 0; i < runs.Length; i++)
        {
            Assert.Equal(PtcRunStatus.Completed, runs[i].Status);
            Assert.Equal(i == 0 ? "one" : "two", runs[i].Output[0].GetProperty("conversation").GetString());
            Assert.Equal("workspace-" + (i == 0 ? "one" : "two"), runs[i].Output[0].GetProperty("workspace").GetString());
        }
        var fresh = await Execute("text(typeof privateValue);");
        Assert.Equal("undefined", fresh.Output[0].GetString());
    }

    [Fact]
    public async Task C12_CatalogAndInputsArePlainIndependentJson()
    {
        var result = await Execute("""
            const names=tools.list();
            names[0].name='changed';
            const def=tools.describe('get_order'); def.inputSchema.properties.id.minimum=99;
            inputs.label='local';
            text({same:tools.describe('get_order').inputSchema.properties.id.minimum===1,
              names:tools.list().map(x=>x.name), hidden:tools.describe('not-allowed'), task:typeof inputs.GetType});
            """);
        Assert.Equal(PtcRunStatus.Completed, result.Status);
        Assert.True(result.Output[0].GetProperty("same").GetBoolean());
        Assert.Equal(JsonValueKind.Null, result.Output[0].GetProperty("hidden").ValueKind);
        Assert.Equal("undefined", result.Output[0].GetProperty("task").GetString());
    }

    [Fact]
    public async Task C13_NoClrModulesNetworkRawTasksOrBridgeGlobals()
    {
        var result = await Execute("""
            const promise=tools.call('list_orders',{});
            text({System:typeof System,require:typeof require,fetch:typeof fetch,
              process:typeof process,importNamespace:typeof importNamespace,clrHelper:typeof clrHelper,
              bridge:typeof __ptc_call,task:typeof promise.GetType,plain:typeof tools.list().GetType});
            await promise;
            let denied=0;
            for (const attempt of [()=>eval('1'),()=>Function('return 1')(),()=>tools.call.constructor('return 1')()])
              try {attempt();} catch {denied++;}
            text({denied});
            """);
        Assert.Equal(PtcRunStatus.Completed, result.Status);
        Assert.All(result.Output[0].EnumerateObject(), item => Assert.Equal("undefined", item.Value.GetString()));
        Assert.Equal(3, result.Output[1].GetProperty("denied").GetInt32());
    }

    [Theory]
    [InlineData("text(1n);")]
    [InlineData("text(NaN);")]
    [InlineData("text(Infinity);")]
    [InlineData("text({a:undefined});")]
    [InlineData("const x={}; x.self=x; text(x);")]
    [InlineData("text([,1]);")]
    [InlineData("text(new Date());")]
    [InlineData("text({get x(){return 1;}});")]
    [InlineData("""text('\uD800');""")]
    public async Task C13_LossyJsonIsNeverSilentlyConverted(string code)
    {
        var result = await Execute(code);
        Assert.NotEqual(PtcRunStatus.Completed, result.Status);
        Assert.Empty(result.Output);
    }

    [Theory]
    [InlineData("const = ;")]
    [InlineData("}); text(typeof __ptc_call); (async function(){")]
    public async Task C14_InvalidCodeDoesNotExecute(string code)
    {
        var host = new Host();
        var result = await Execute(code, host);
        Assert.Equal(PtcRunStatus.Rejected, result.Status);
        Assert.Equal("invalid_code", result.Failure?.Code);
        Assert.Empty(host.Calls);
        Assert.Empty(result.Output);
    }

    [Fact]
    public async Task C14_ReturnIsNotImplicitModelOutputAndFailureIsNotRetried()
    {
        var host = new Host { Custom = (_, _, _) => ValueTask.FromResult(PtcToolResult.Error("tool_denied", "Denied by host policy.")) };
        var result = await Execute("const r=await tools.call('list_orders',{}); text(r.errorCode); return {huge:'not emitted'};", host);
        Assert.Equal(PtcRunStatus.Completed, result.Status);
        Assert.Equal("tool_denied", Assert.Single(result.Output).GetString());
        Assert.Equal(PtcCallState.Denied, Assert.Single(result.ToolRecords).State);
        Assert.Single(host.Calls);
    }

    [Fact]
    public async Task C16_CompleteToolDataAndSmallExplicitProjection()
    {
        var host = new Host { Custom = (_, _, _) => ValueTask.FromResult(Ok(new { rows = Enumerable.Range(0, 4000).ToArray() })) };
        var result = await Execute("const r=await tools.call('list_orders',{}); text({count:r.data.rows.length,last:r.data.rows.at(-1)});", host);
        Assert.Equal(PtcRunStatus.Completed, result.Status);
        Assert.Equal(4000, result.Output[0].GetProperty("count").GetInt32());
        Assert.Equal(3999, result.Output[0].GetProperty("last").GetInt32());
        Assert.True(PtcModelResultProjector.Project(result, 1024).Length < 1024);
        Assert.Equal(4000, result.ToolRecords[0].Result!.Data!.Value.GetProperty("rows").GetArrayLength());
    }

    [Fact]
    public async Task C17_ArtifactsRemainAfterThrow()
    {
        var host = new Host { Custom = (_, _, _) => ValueTask.FromResult(Ok() with { Artifacts = [new("artifact", "text/plain", "generated")] }) };
        var result = await Execute("await tools.call('list_orders',{}); throw new Error('fail');", host);
        Assert.Equal(PtcRunStatus.Failed, result.Status);
        Assert.Equal("artifact", Assert.Single(result.Artifacts).Id);
        Assert.Single(host.Calls);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) { timeout.Token.ThrowIfCancellationRequested(); await Task.Delay(1, timeout.Token); }
    }
}
