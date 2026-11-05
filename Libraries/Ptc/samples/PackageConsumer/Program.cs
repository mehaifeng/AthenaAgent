using System.Security.Cryptography;
using System.Text.Json;
using Ptc.Core;
using Ptc.JavaScript;

var executor = new PtcExecutor(new JintScriptRuntime());
const string program = """
    const listed = await tools.call("list_orders", {});
    if (!listed.success) { text({error: listed.errorCode}); return; }
    const details = await Promise.all(listed.data.ids.map(id => tools.call("get_order", {id})));
    const failed = details.filter(item => !item.success);
    if (failed.length) { text({failed: failed.map(item => item.errorCode)}); return; }
    const ids = details.map(item=>item.data).filter(item=>item.overdue).map(item=>item.id);
    text({count:ids.length, ids});
    """;
var host = new OrdersHost();
var result = await executor.ExecuteAsync(Request(program, "aggregate"), host);
Check(result.Status == PtcRunStatus.Completed, "aggregate completes");
Check(result.Output.Single().GetRawText() == "{\"count\":2,\"ids\":[1,3]}", "exact aggregation");
Check(result.Metrics.CallsStarted == 4 && host.Calls == 4, "four actual tool calls");
Console.WriteLine(PtcModelResultProjector.Project(result, 4096));
Check(PtcJson.Parse(PtcModelResultProjector.Project(result, 4096)).GetProperty("isolation").GetString() == "inProcess", "honest isolation metadata");

var denied = await executor.ExecuteAsync(Request("text(await tools.call('not_granted',{}));", "denied"), host);
Check(denied.Status == PtcRunStatus.Completed && denied.Output[0].GetProperty("errorCode").GetString() == "tool_not_available", "unavailable tool denied");
Check(host.Calls == 4, "denial makes no host call");

var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var tokenSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
using var stop = new CancellationTokenSource();
host.BeforeReturn = async token =>
{
    entered.TrySetResult();
    using var registration = token.Register(() => tokenSeen.TrySetResult());
    await Task.Delay(Timeout.InfiniteTimeSpan, token);
};
var cancelledTask = executor.ExecuteAsync(Request("await tools.call('list_orders',{});", "cancelled"), host, cancellationToken: stop.Token);
await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
stop.Cancel();
var cancelled = await cancelledTask.WaitAsync(TimeSpan.FromSeconds(5));
Check(cancelled.Status == PtcRunStatus.Cancelled && tokenSeen.Task.IsCompleted, "cancel reaches host I/O");
Check(cancelled.ToolRecords.All(item => item.State is not (PtcCallState.Running or PtcCallState.Queued)), "all calls have terminal records");

foreach (var assembly in new[] { typeof(PtcExecutor).Assembly, typeof(JintScriptRuntime).Assembly })
{
    var path = assembly.Location;
    Console.WriteLine("ASSEMBLY " + JsonSerializer.Serialize(new { name = assembly.GetName().Name, path, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant() }));
}
Console.WriteLine("[PACKAGE CONSUMER PASSED] 8 assertions; aggregate=4 calls; denied=0 calls; cancellation propagated");

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException("Consumer assertion failed: " + message);
}
static PtcExecutionRequest Request(string code, string id) => new(code, PtcJson.Parse("{}"),
    new HashSet<string>(["list_orders", "get_order"], StringComparer.Ordinal), new(id, "parent-" + id, null, null, "synthetic orders"), new());

sealed class OrdersHost : IPtcToolHost
{
    public int Calls;
    public Func<CancellationToken, Task>? BeforeReturn { get; set; }
    public IReadOnlyList<PtcToolDefinition> Tools { get; } =
    [new("list_orders", "Lists order IDs", PtcJson.Parse("{\"type\":\"object\",\"additionalProperties\":false}")),
     new("get_order", "Gets an order", PtcJson.Parse("{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"integer\"}},\"required\":[\"id\"],\"additionalProperties\":false}"))];
    public PtcScheduling GetScheduling(PtcToolCall call, PtcRunContext context) => PtcScheduling.ParallelReadOnly;
    public async ValueTask<PtcToolResult> InvokeAsync(PtcToolCall call, PtcRunContext context, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Calls);
        if (BeforeReturn is not null) await BeforeReturn(cancellationToken);
        object data = call.Name == "list_orders" ? new { ids = new[] { 1, 2, 3 } }
            : new { id = call.Arguments.GetProperty("id").GetInt32(), overdue = call.Arguments.GetProperty("id").GetInt32() is 1 or 3 };
        return new(true, "ok", JsonSerializer.SerializeToElement(data), null, []);
    }
}
