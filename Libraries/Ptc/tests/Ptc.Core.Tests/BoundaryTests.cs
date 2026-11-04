using System.Text.Json;
using Ptc.Core;
using Xunit;
using static Ptc.Core.Tests.CoordinatorTests;

namespace Ptc.Core.Tests;

public sealed class BoundaryTests
{
    [Fact]
    public async Task C05_DuplicateAndDeepArgumentsAreRejectedBeforeTheHost()
    {
        var host = new Host();
        using var duplicates = JsonDocument.Parse("{\"x\":1,\"x\":2}");
        using var deep = JsonDocument.Parse("{\"a\":{\"b\":{\"c\":{}}}}");
        var result = await new PtcExecutor(new Runtime(async bridge =>
        {
            Assert.False((await bridge.CallAsync("write", duplicates.RootElement, default)).Success);
            Assert.False((await bridge.CallAsync("write", deep.RootElement, default)).Success);
        })).ExecuteAsync(Request(new() { MaxJsonDepth = 3 }), host);
        Assert.Equal(PtcRunStatus.Completed, result.Status);
        Assert.Equal(2, result.Metrics.CallsAttempted);
        Assert.Empty(host.Invoked);
        Assert.All(result.ToolRecords, item => Assert.Equal("invalid_arguments", item.Result?.ErrorCode));
    }

    [Fact]
    public async Task C06_PendingQueueLimitStopsNewWorkWithoutCreatingUnboundedTasks()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new Host { Handler = async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Ok();
        } };
        var result = await new PtcExecutor(new Runtime(async bridge =>
        {
            var first = bridge.CallAsync("write", Json(), default).AsTask();
            await entered.Task;
            var queued = bridge.CallAsync("write", Json(), default).AsTask();
            var over = bridge.CallAsync("write", Json(), default).AsTask();
            await Task.WhenAll(first, queued, over);
        })).ExecuteAsync(Request(new() { MaxPendingCalls = 1 }), host);
        Assert.Equal(PtcRunStatus.BudgetExceeded, result.Status);
        Assert.Equal("call_limit_exceeded", result.Failure?.Code);
        Assert.Equal(3, result.Metrics.CallsAttempted);
        Assert.Single(host.Invoked);
        Assert.All(result.ToolRecords.Skip(1), item => Assert.Equal(PtcCallState.NotStarted, item.State));
    }

    [Fact]
    public async Task C08_TotalDataBudgetDiffersFromTheSingleResultBudget()
    {
        var host = new Host { Handler = (_, _) => ValueTask.FromResult(Ok(new { value = new string('x', 2000) })) };
        var result = await new PtcExecutor(new Runtime(async bridge =>
        {
            Assert.True((await bridge.CallAsync("read", Json(), default)).Success);
            await bridge.CallAsync("read", Json(), default);
        })).ExecuteAsync(Request(new() { MaxRunDataBytes = 3000, MaxToolResultBytes = 4000 }), host);
        Assert.Equal(PtcRunStatus.BudgetExceeded, result.Status);
        Assert.Equal("data_limit_exceeded", result.Failure?.Code);
        Assert.Equal(2, host.Invoked.Count);
        Assert.True(result.ToolRecords[0].ResultDelivered);
        Assert.False(result.ToolRecords[1].ResultDelivered);
        Assert.Equal(PtcCallState.Succeeded, result.ToolRecords[1].State);
        Assert.True(result.ToolRecords[1].RawResultBytes > 2000);
    }

    [Fact]
    public async Task C08_ArtifactReferenceLimitIsUniqueAndStopsTheRun()
    {
        var host = new Host { Handler = (_, _) => ValueTask.FromResult(Ok() with
        { Artifacts = [new("one", "text/plain", "one"), new("one", "text/plain", "one"), new("two", "text/plain", "two")] }) };
        var result = await new PtcExecutor(new Runtime(async bridge => { await bridge.CallAsync("read", Json(), default); }))
            .ExecuteAsync(Request(new() { MaxArtifactReferences = 1 }), host);
        Assert.Equal(PtcRunStatus.BudgetExceeded, result.Status);
        Assert.Equal("data_limit_exceeded", result.Failure?.Code);
        Assert.Equal("one", Assert.Single(result.Artifacts).Id);
        Assert.Single(host.Invoked);
    }

    [Fact]
    public async Task C09_CancellationBeforeRunStartsNothing()
    {
        var host = new Host();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var result = await new PtcExecutor(new Runtime(async bridge => { await bridge.CallAsync("read", Json(), default); }))
            .ExecuteAsync(Request(), host, cancellationToken: cancelled.Token);
        Assert.Equal(PtcRunStatus.Cancelled, result.Status);
        Assert.Empty(host.Invoked);
        Assert.Empty(result.ToolRecords);
    }

    [Fact]
    public async Task C15_HungObserverHasABoundedVisibleFailure()
    {
        var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new Host { Handler = async (_, token) => { await Task.Delay(Timeout.InfiniteTimeSpan, token); return Ok(); } };
        var result = await new PtcExecutor(new Runtime(async bridge => { await bridge.CallAsync("read", Json(), default); }))
            .ExecuteAsync(Request(new() { EventDeliveryTimeout = TimeSpan.FromMilliseconds(50) }), host, new HangingSink(never.Task))
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PtcRunStatus.Failed, result.Status);
        Assert.Equal("observer_error", result.Failure?.Code);
        Assert.DoesNotContain(result.ToolRecords, item => item.State is PtcCallState.Running or PtcCallState.Queued);
        never.SetResult();
    }

    [Theory]
    [InlineData("code")]
    [InlineData("input")]
    [InlineData("catalogCount")]
    [InlineData("catalogBytes")]
    public async Task CatalogAndAdmissionLimitsAreNotSilentlyTruncated(string kind)
    {
        var host = new Host();
        var limits = kind switch
        {
            "code" => new PtcExecutionLimits { MaxCodeChars = 1 },
            "input" => new PtcExecutionLimits { MaxInputBytes = 1 },
            "catalogCount" => new PtcExecutionLimits { MaxToolDefinitions = 1 },
            _ => new PtcExecutionLimits { MaxCatalogBytes = 1 }
        };
        var entered = false;
        var result = await new PtcExecutor(new Runtime(_ => { entered = true; return Task.CompletedTask; }))
            .ExecuteAsync(Request(limits), host);
        Assert.NotEqual(PtcRunStatus.Completed, result.Status);
        Assert.False(entered);
        Assert.Empty(host.Invoked);
    }

    [Fact]
    public async Task SchemaAnnotationsDoNotSupplyRequiredArguments()
    {
        var host = new Host { Tools = [new("read", "read", Json("{\"type\":\"object\",\"properties\":{\"id\":{\"type\":\"integer\",\"default\":1,\"title\":\"id\",\"examples\":[1]}},\"required\":[\"id\"]}"))] };
        var result = await new PtcExecutor(new Runtime(async bridge =>
        {
            Assert.Equal("invalid_arguments", (await bridge.CallAsync("read", Json(), default)).ErrorCode);
        })).ExecuteAsync(Request(), host);
        Assert.Equal(PtcRunStatus.Completed, result.Status);
        Assert.Empty(host.Invoked);
    }

    private sealed class HangingSink(Task task) : IPtcEventSink
    {
        public async ValueTask OnEventAsync(PtcEvent item, CancellationToken cancellationToken) => await task;
    }

    [Theory]
    [InlineData("""{"x":"\uD800"}""")]
    [InlineData("""{"\uDC00":1}""")]
    public void JsonBoundaryRejectsEscapedUnpairedUnicode(string json)
        => Assert.Equal("invalid_arguments", Assert.Throws<PtcRuntimeException>(() => Json(json)).Code);

    [Fact]
    public void JsonBoundaryRejectsRawUnpairedUnicodeWithoutReplacement()
        => Assert.Equal("invalid_arguments", Assert.Throws<PtcRuntimeException>(() => Json("\"" + (char)0xD800 + "\"")).Code);

    [Fact]
    public async Task InvalidHostJsonPreservesActualExecutedOutcomeAndArtifacts()
    {
        var host = new Host { Handler = (_, _) => ValueTask.FromResult(Ok() with
        { Message = new string((char)0xD800, 1), Artifacts = [new("done", "text/plain", "completed")] }) };
        var result = await new PtcExecutor(new Runtime(async bridge => { await bridge.CallAsync("read", Json(), default); }))
            .ExecuteAsync(Request(), host);
        Assert.Equal(PtcRunStatus.Failed, result.Status);
        var record = Assert.Single(result.ToolRecords);
        Assert.Equal(PtcCallState.Succeeded, record.State);
        Assert.False(record.ResultDelivered);
        Assert.Equal("invalid_arguments", record.DeliveryErrorCode);
        Assert.Null(record.Result);
        Assert.Equal("done", Assert.Single(result.Artifacts).Id);
        Assert.Single(host.Invoked);
    }

    [Theory]
    [InlineData(PtcRunStatus.Completed, "completed")]
    [InlineData(PtcRunStatus.Rejected, "rejected")]
    [InlineData(PtcRunStatus.Failed, "failed")]
    [InlineData(PtcRunStatus.Cancelled, "cancelled")]
    [InlineData(PtcRunStatus.TimedOut, "timedOut")]
    [InlineData(PtcRunStatus.BudgetExceeded, "budgetExceeded")]
    public void ModelStatusUsesOneCamelCaseProtocol(PtcRunStatus status, string expected)
    {
        var result = new PtcExecutionResult(new("run", "parent", null, null, null), status, [], [], [], new(0, 0, 0, 0, 0, 0, TimeSpan.Zero), null);
        Assert.Equal(expected, Json(PtcModelResultProjector.Project(result, 1024)).GetProperty("status").GetString());
    }
}
