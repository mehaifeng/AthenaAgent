#pragma warning disable OPENAI001 // Exercise both production SDK protocols.
#pragma warning disable CA2000 // Every Fixture is await-using disposed; the analyzer misses its loop-scoped await using. DI factories own their created services.

using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Athena.UI;
using Athena.UI.Controls;
using Athena.UI.Models;
using Athena.UI.Services;
using Athena.UI.Services.ConfigSurface;
using Athena.UI.Services.Context;
using Athena.UI.Services.Functions;
using Athena.UI.Services.Interfaces;
using Athena.UI.Services.ModelMetadata;
using Athena.UI.Services.Ptc;
using Athena.UI.Services.SubAgents;
using Athena.UI.Services.ExecutionTimeline;
using Athena.UI.ViewModels;
using Avalonia.Controls;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using ChatTool = OpenAI.Chat.ChatTool;
using Ptc.Core;
using Serilog;

internal static class PtcTests
{
    private static int _assertions;
    private static void Check(bool condition, string message)
    {
        Interlocked.Increment(ref _assertions);
        if (!condition) throw new InvalidOperationException("PTC: " + message);
    }

    public static async Task RunAsync()
    {
        _assertions = 0;
        await DisabledAndUnattendedAsync();
        await SnapshotGrantAsync();
        await ApprovalModesAsync();
        await ParallelSessionsAsync();
        await CompleteRawDataAsync();
        await LoopbackAsync(ProviderProtocol.ChatCompletions);
        await LoopbackAsync(ProviderProtocol.Responses);
        await DirectTimelineAsync();
        await MessageTimelineAsync();
        await ConcurrentMessageTimelinesAsync();
        await CancelledLoopbackAsync();
        await ArchiveAndTraceAsync();
        await FileSafetyAsync();
        await ArtifactOwnershipAsync();
        await TraceQuotaAndSaveFailureAsync();
        await LivePolicyAndBudgetAsync();
        await FailedTraceIsNeverReferencedAsync();
        VerifyPackedAssemblies();
        Check(Environment.GetEnvironmentVariable("ATHENA_PTC_ASSERTION_PROBE") != "1", "intentional failure probe");
        Console.WriteLine($"[PASS] PTC A01-A14 plus execution timeline: 18 integration groups, {_assertions} assertions, both real HTTP loopbacks, production Registry and packed components");
    }

    public static void VerifyUiProjection()
    {
        var parent = new ToolCallEntry { ToolCallId = "outer", Name = "execute_program", Arguments = "{}" };
        var bubble = new ChatMessage();
        bubble.Segments.Add(new ChatMessageSegment { Kind = ChatMessageSegmentKind.ToolCallGroup, ToolCalls = new ObservableCollection<ToolCallEntry> { parent } });
        var baseline = JsonSerializer.Serialize(bubble);
        PtcProgressProjection.Apply(bubble, new("run", "other-parent", "c", 1, "CallStarted", "child", "read_system_file", null));
        Check(parent.ProgramCalls is null, "progress must not attach to another parent");
        PtcProgressProjection.Apply(bubble, new("run", "outer", "c", 1, "RunStarted", null, null, null));
        PtcProgressProjection.Apply(bubble, new("run", "outer", "c", 2, "CallQueued", "child", "read_system_file", null));
        PtcProgressProjection.Apply(bubble, new("run", "outer", "c", 3, "CallStarted", "child", "read_system_file", null));
        PtcProgressProjection.Apply(bubble, new("run", "outer", "c", 4, "CallFinished", "child", "read_system_file", "cancelled"));
        PtcProgressProjection.Apply(bubble, new("run", "outer", "c", 5, "RunFinished", null, null, "cancelled"));
        Check(parent.ProgramCalls is { Count: 1 } && parent.ProgramCalls[0].Status == ToolCallStatus.Failed, "cancelled child card must finish");
        Check(JsonSerializer.Serialize(bubble) == baseline, "all child UI state must be absent from message persistence");
        parent.IsExpanded = true;
        var view = new ToolCallRowView { DataContext = parent };
        var window = new Window { Content = view, Width = 640, Height = 400 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Check(parent.ExpandedDetails == parent && parent.ProgramCalls![0].HasResult, "expanded parent exposes real child terminal metadata");
        window.Close();
        Console.WriteLine("[PASS] PTC child progress belongs to the outer row, ends on Stop, realizes its view and never changes archive JSON");
    }

    private static async Task DisabledAndUnattendedAsync()
    {
        await using var fixture = new Fixture();
        fixture.Config.PtcEnabled = false;
        Check(!fixture.Registry.GetToolDefinitions().Cast<ChatTool>().Any(tool => tool.FunctionName == "execute_program"), "A01 disabled declaration hidden");
        var guessed = await fixture.Registry.ExecuteAsync("execute_program", "{\"code\":\"text(1)\"}");
        Check(!guessed.Success && guessed.ErrorCode == "tool_not_available", "A01 guessed name rejected");
        Check(fixture.Prompter.Calls.Count == 0, "disabled guessed tool does not prompt");
        var off = new AppConfig();
        var on = new AppConfig { PtcEnabled = true };
        Check(!ConfigFieldCatalog.Fields.Any(field => JsonSerializer.Serialize(field.Get(off)) != JsonSerializer.Serialize(field.Get(on))), "switch is not model writable or readable through config catalog");
        var analysisOn = new AppConfig { ExecutionAnalysisEnabled = true };
        Check(!ConfigFieldCatalog.Fields.Any(field => JsonSerializer.Serialize(field.Get(off)) != JsonSerializer.Serialize(field.Get(analysisOn))), "the model cannot turn on local execution analysis");
        Check(!JsonSerializer.Deserialize<AppConfig>("{}")!.PtcEnabled, "old configs default PTC off");
        Check(JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(on))!.PtcEnabled, "user toggle survives config serialization");
        fixture.Config.PtcEnabled = true;
        Check(fixture.Registry.GetToolDefinitions().Cast<ChatTool>().Any(tool => tool.FunctionName == "execute_program"), "enabled declaration visible");
        Check(!fixture.Registry.GetToolDefinitions(["execute_program"]).Any(), "A12 explicit unattended name list still hides PTC");
        foreach (var mode in new[] { ToolApprovalMode.Off, ToolApprovalMode.Balanced, ToolApprovalMode.Strict, ToolApprovalMode.Automatic })
        {
            fixture.Config.ToolApprovalMode = mode;
            using (ToolApprovalContext.EnterNonInteractive())
                Check(!(await fixture.Registry.ExecuteAsync("execute_program", "{\"code\":\"text(1)\"}")).Success, "A12 subagent cannot guess PTC under " + mode);
            using (ToolApprovalContext.EnterTrusted(new TrustedRoutineGrant("test", ["execute_program"], fixture.Root)))
                Check(!(await fixture.Registry.ExecuteAsync("execute_program", "{\"code\":\"text(1)\"}")).Success, "A12 even a trusted grant cannot open PTC under " + mode);
            using (AthenaPtcInvocationScope.Enter(fixture.Invocation()))
            using (ToolApprovalContext.EnterNonInteractive())
                Check(!(await fixture.Registry.ExecuteAsync("execute_program", "{\"code\":\"text(1)\"}")).Success, "a subagent cannot borrow an inherited Interactive PTC grant under " + mode);
        }
        Check(fixture.Files.Observations.IsEmpty, "no disabled/unattended program reached file tools");
    }

    private static async Task SnapshotGrantAsync()
    {
        await using var fixture = new Fixture();
        var result = await fixture.ProgramAsync("text(await tools.call('read_system_file',{path:inputs.path}));", names: ["list_system_directory"]);
        Check(result.Success, "business denial can be handled explicitly by script");
        Check(Output(result)[0].GetProperty("errorCode").GetString() == "tool_not_available", "A03 global Registry name is not in the run snapshot grant");
        Check(fixture.Files.Observations.IsEmpty, "snapshot denial causes no real read");
        foreach (var name in new[] { "execute_terminal_command", "modify_self_configuration", "dispatch_subagents", "run_browser_task", "mcp_call_tool", "create_task", "execute_program" })
        {
            result = await fixture.ProgramAsync("text(await tools.call(inputs.name,{}));", inputs: new { name });
            Check(Output(result)[0].GetProperty("errorCode").GetString() == "tool_not_available", "A03 V0 denies " + name);
        }
        var badTop = await fixture.ProgramArgumentsAsync("{\"code\":\"text(1)\",\"AllowedTools\":[\"execute_terminal_command\"]}");
        Check(!badTop.Success, "outer schema forbids model-supplied permissions");
        var duplicate = await fixture.ProgramArgumentsAsync("{\"code\":\"text(1)\",\"code\":\"text(2)\"}");
        Check(!duplicate.Success && duplicate.ErrorCode == "invalid_arguments", "outer JSON rejects duplicate properties");
    }

    private static async Task ApprovalModesAsync()
    {
        foreach (var mode in new[] { ToolApprovalMode.Off, ToolApprovalMode.Balanced, ToolApprovalMode.Strict, ToolApprovalMode.Automatic })
        {
            await using var fixture = new Fixture();
            fixture.Config.ToolApprovalMode = mode;
            var result = await fixture.ProgramAsync("const r=await tools.call('read_system_file',{path:inputs.path}); text(r.success);");
            Check(result.Success && Output(result)[0].GetBoolean(), "A04 approved program works under " + mode);
            Check(fixture.Prompter.Calls.Count == (mode == ToolApprovalMode.Balanced ? 1 : mode == ToolApprovalMode.Strict ? 2 : 0), "A04 outer and child prompt counts under " + mode);
            Check(fixture.Judge.Calls.Count == (mode == ToolApprovalMode.Automatic ? 1 : 0), "A04 Automatic only judges the sensitive outer call");
            var observation = fixture.Files.Observations.Single();
            Check(observation.Mode == ToolApprovalContext.ExecutionMode.Interactive && observation.Conversation == "conversation" && observation.Workspace == "workspace"
                && observation.Task == "delegated task" && observation.Token.CanBeCanceled && observation.IsChild, "A05 explicitly restored scope under " + mode);
        }
        await using (var fixture = new Fixture())
        {
            fixture.Config.ToolApprovalMode = ToolApprovalMode.Strict;
            fixture.Prompter.Decide = request => request.FunctionName == "execute_program" ? ToolApprovalScope.AllowOnce : ToolApprovalScope.Deny;
            var result = await fixture.ProgramAsync("const r=await tools.call('read_system_file',{path:inputs.path}); text({error:r.errorCode,message:r.message});");
            Check(result.Success && Output(result)[0].GetProperty("error").GetString() == "tool_denied", "A04 child denial has a stable code");
            Check(Output(result)[0].GetProperty("message").GetString()!.Contains("用户拒绝", StringComparison.Ordinal), "actual user denial retains its source");
            Check(fixture.Files.Observations.IsEmpty, "denied child never executes");
        }
        await using (var fixture = new Fixture())
        {
            fixture.Config.ToolApprovalMode = ToolApprovalMode.Automatic;
            fixture.Judge.Decision = ToolApprovalDecision.JudgeDenied("test judge refusal");
            var result = await fixture.ProgramAsync("text(1);");
            Check(!result.Success && !result.Message.Contains("用户拒绝", StringComparison.Ordinal), "judge refusal is not relabeled as a user refusal");
            Check(fixture.Files.Observations.IsEmpty, "outer denial executes no child");
        }
    }

    private static async Task ParallelSessionsAsync()
    {
        await using var fixture = new Fixture();
        fixture.Config.ToolApprovalMode = ToolApprovalMode.Off;
        var rendezvous = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrived = 0;
        fixture.Files.BeforeRead = async (_, token) =>
        {
            if (Interlocked.Increment(ref arrived) == 2) rendezvous.TrySetResult();
            await rendezvous.Task.WaitAsync(token);
        };
        var results = await Task.WhenAll(new[] { "one", "two" }.Select(id => fixture.ProgramAsync(
            "const r=await tools.call('read_system_file',{path:inputs.path}); text(r.success);", conversation: id, workspace: "w-" + id, task: "t-" + id)));
        Check(results.All(result => result.Success), "A06 simultaneous sessions complete");
        Check(fixture.Files.Observations.Count == 2, "two real overlapping calls");
        foreach (var observation in fixture.Files.Observations)
            Check(observation.Workspace == "w-" + observation.Conversation && observation.Task == "t-" + observation.Conversation, "A06 actual call scopes do not cross");
        Check(fixture.Progress.Select(item => item.RunId).Distinct().Count() == 2, "each session owns a unique run event stream");
        foreach (var group in fixture.Progress.GroupBy(item => item.RunId))
            Check(group.Select(item => item.ConversationId).Distinct().Count() == 1 && group.First().Kind == "RunStarted" && group.Last().Kind == "RunFinished", "run event attribution and terminal boundaries are intact");
    }

    private static async Task CompleteRawDataAsync()
    {
        await using var fixture = new Fixture();
        fixture.Config.MaxToolResultChars = 2000;
        var content = new string('x', 12_000) + "SYNTHETIC_TAIL";
        await File.WriteAllTextAsync(fixture.File, content);
        FunctionResult direct;
        using (ToolApprovalContext.EnterInteractive())
            direct = await fixture.Registry.ExecuteAsync("read_system_file", JsonSerializer.Serialize(new { path = fixture.File }));
        Check(direct.BudgetedJson is not null && direct.ToJson().Length <= 2000, "ordinary result budget is active");
        var result = await fixture.ProgramAsync("const r=await tools.call('read_system_file',{path:inputs.path}); text({length:r.data.content.length,tail:r.data.content.includes('SYNTHETIC_TAIL')});");
        Check(result.Success && Output(result)[0].GetProperty("tail").GetBoolean(), "A07 script sees full actually-returned data beyond the ordinary budget");
        Check(Output(result)[0].GetProperty("length").GetInt32() > 12_000 && result.ToJson().Length <= 2000, "only accurate bounded aggregate reaches model");
        Check(!result.ToJson().Contains(new string('x', 100), StringComparison.Ordinal), "raw body absent from model envelope");
        var trace = await fixture.Store.ReadAsync(Data(result).GetProperty("traceRef").GetString()!);
        Check(trace!.Execution.ToolRecords[0].Result!.Data!.Value.GetProperty("content").GetString()!.Contains("SYNTHETIC_TAIL", StringComparison.Ordinal), "raw record remains complete");
    }

    private static async Task LoopbackAsync(ProviderProtocol protocol)
    {
        await using var fixture = new Fixture();
        await using var model = new LoopbackModel("const r=await tools.call('read_system_file',{path:inputs.path}); text({length:r.data.content.length});", fixture.File);
        var service = fixture.Chat(model.BaseUrl, protocol);
        var context = new ConversationContext { ConversationId = "loop-" + protocol, WorkspaceId = "loop-workspace" };
        var observed = new List<ChatMessage>();
        var text = new StringBuilder();
        var timeline = new ExecutionTimelineCollector(Guid.NewGuid().ToString("N"), System.Diagnostics.Stopwatch.GetTimestamp(), DateTimeOffset.UtcNow);
        await foreach (var delta in service.StreamMessageAsync("Read and aggregate the synthetic fixture.", context,
            onMessageAdded: observed.Add, onPtcProgress: fixture.Progress.Enqueue, executionTimeline: timeline)) text.Append(delta);
        Check(text.ToString() == "done", "A02 production SDK/main loop completes on " + protocol + ": " + text);
        Check(model.Requests.Count == 2, "exactly one tool round and final round on " + protocol);
        Check(fixture.Files.Observations.Count == 1, "A02 real production Registry reached the file service once on " + protocol);
        var calls = observed.Where(item => !string.IsNullOrEmpty(item.ToolCallsJson)).SelectMany(item => JsonDocument.Parse(item.ToolCallsJson!).RootElement.EnumerateArray()).ToArray();
        Check(calls.Length == 1, "only outer assistant tool carrier exists");
        var tool = observed.Single(item => item.Role == "tool");
        Check(tool.ToolCallId == "call_program" && tool.ToolName == "execute_program", "A08 only paired outer result on " + protocol);
        var result = PtcJson.Parse(tool.Content!);
        Check(result.GetProperty("success").GetBoolean(), "outer program result successful on " + protocol);
        Check(result.GetProperty("data").GetProperty("metrics").GetProperty("callsStarted").GetInt32() == 1, "one child was executed, not hidden as another protocol call");
        Check(fixture.Progress.Any(item => item.Kind == "CallFinished") && fixture.Progress.All(item => item.ParentToolCallId == "call_program" && item.ConversationId == context.ConversationId), "A05 event/session/parent attribution on " + protocol);
        var trace = timeline.Complete(ExecutionTimelineState.Succeeded);
        var outer = trace.Spans.Single(item => item.Lane == ExecutionTimelineLane.Tools && item.Name == "execute_program");
        var script = trace.Spans.Single(item => item.Lane == ExecutionTimelineLane.Script);
        var child = trace.Spans.Single(item => item.Lane == ExecutionTimelineLane.PtcTools && item.Name == "read_system_file");
        var requestingModel = trace.Spans.First(item => item.Kind == ExecutionTimelineKind.ModelRequest);
        Check(trace.ModelAttempts == 2 && trace.OuterTools == 1 && trace.OuterToolsStarted == 1 && trace.PtcAttempts == 1 && trace.PtcStarted == 1, "timeline counts actual model, wrapper and child independently on " + protocol);
        Check(script.ParentId == outer.Id && child.ParentId == script.Id && child.EndUs >= child.StartUs && script.State == ExecutionTimelineState.Succeeded, "PTC timing source, interpreter lifetime and hierarchy are intact on " + protocol);
        Check(outer.ParentId == requestingModel.Id && requestingModel.EndUs <= outer.StartUs,
            "outer tool is linked to the model attempt that requested it on " + protocol);
        Check(outer.StartUs <= script.StartUs && script.EndUs <= outer.EndUs && script.StartUs <= child.StartUs && child.EndUs <= script.EndUs,
            "real PTC child and script intervals fit their actual parents on " + protocol);
        Check(trace.Spans.Any(item => item.Kind == ExecutionTimelineKind.Approval && item.ParentId == child.Id),
            "child approval is attached to the right PTC call even when observer delivery is delayed");
        Check(trace.Spans.Count(item => item.Kind == ExecutionTimelineKind.ModelRequest && item.InputTokens is not null) == 2, "timeline only records actual provider usage on " + protocol);
        Check(!JsonSerializer.Serialize(trace).Contains(fixture.File, StringComparison.Ordinal), "timeline does not persist tool paths on " + protocol);
        var sent = model.Requests.ToArray();
        var tools = sent[0].GetProperty("tools").EnumerateArray().ToArray();
        Check(tools.Any(item => protocol == ProviderProtocol.Responses ? item.GetProperty("name").GetString() == "execute_program" : item.GetProperty("function").GetProperty("name").GetString() == "execute_program"), "A02 real outbound declaration contains execute_program");
        if (protocol == ProviderProtocol.ChatCompletions)
        {
            var messages = sent[1].GetProperty("messages").EnumerateArray().ToArray();
            Check(messages.Count(item => item.GetProperty("role").GetString() == "tool") == 1, "A08 Chat has no child tool messages");
            Check(messages.Where(item => item.TryGetProperty("tool_calls", out _)).Sum(item => item.GetProperty("tool_calls").GetArrayLength()) == 1, "Chat has exactly one paired assistant tool call");
        }
        else
        {
            var items = sent[1].GetProperty("input").EnumerateArray().ToArray();
            Check(items.Count(item => item.GetProperty("type").GetString() == "function_call") == 1, "A08 Responses only outer function_call");
            Check(items.Count(item => item.GetProperty("type").GetString() == "function_call_output") == 1, "Responses only paired outer function_call_output");
        }
    }

    private static async Task DirectTimelineAsync()
    {
        await using var fixture = new Fixture();
        await using var model = new LoopbackModel("", fixture.File, "read_system_file");
        var service = fixture.Chat(model.BaseUrl, ProviderProtocol.ChatCompletions);
        var context = new ConversationContext { ConversationId = "dtc-timeline" };
        var timeline = new ExecutionTimelineCollector(Guid.NewGuid().ToString("N"), System.Diagnostics.Stopwatch.GetTimestamp(), DateTimeOffset.UtcNow);
        await foreach (var _ in service.StreamMessageAsync("Read the synthetic fixture.", context, executionTimeline: timeline)) { }
        var trace = timeline.Complete(ExecutionTimelineState.Succeeded);
        Check(trace.ModelAttempts == 2 && trace.OuterTools == 1 && trace.OuterToolsStarted == 1 && trace.PtcAttempts == 0, "DTC model/tool counts do not include PTC");
        Check(trace.Spans.Any(item => item.Lane == ExecutionTimelineLane.Tools && item.Name == "read_system_file" && item.State == ExecutionTimelineState.Succeeded), "DTC real Registry tool gets an actual duration");
        Check(trace.Spans.Single(item => item.Lane == ExecutionTimelineLane.Tools).ParentId
            == trace.Spans.First(item => item.Kind == ExecutionTimelineKind.ModelRequest).Id,
            "DTC tool is linked to its requesting model attempt");
        Check(trace.Spans.Any(item => item.Kind == ExecutionTimelineKind.Approval && item.EndUs >= item.StartUs), "DTC approval gets its own system span");
        Check(!trace.Spans.Any(item => item.Lane == ExecutionTimelineLane.Script), "DTC does not invent a script");
        Check(fixture.Files.Observations.Count == 1 && model.Requests.Count == 2, "recording adds no model or tool calls");
        Check(model.Requests.All(item => !item.GetRawText().Contains("executionTimeline", StringComparison.OrdinalIgnoreCase)), "trace metadata never enters model payload");
    }

    private static async Task MessageTimelineAsync()
    {
        await using var fixture = new Fixture();
        fixture.Config.ExecutionAnalysisEnabled = true;
        await using var model = new LoopbackModel("const r=await tools.call('read_system_file',{path:inputs.path}); text(r.success);", fixture.File);
        using var timelines = new ExecutionTimelineService(fixture.ConfigService, fixture.Paths, Log.Logger);
        using var chat = new MainConversationViewModel(fixture.Chat(model.BaseUrl, ProviderProtocol.ChatCompletions),
            fixture.ConfigService, new HeadlessPromptService(), fixture.Registry, new TokenService(), null,
            sessionAccessor: fixture.Sessions, executionTimelines: timelines);
        chat.InputText = "Read the synthetic file.";
        await chat.SendMessageCommand.ExecuteAsync(null);
        var reply = chat.Messages.Last(item => item.Role == "assistant" && !item.IsHidden);
        var saved = await timelines.LoadAsync(reply.Id);
        Check(saved.Snapshot is { State: ExecutionTimelineState.Succeeded, ModelAttempts: 2, PtcAttempts: 1 }, "real message command records and saves its own complete timeline");
        Check(reply.DurationMs.HasValue && Math.Abs(saved.Snapshot!.DurationUs / 1000 - reply.DurationMs.Value) <= 1,
            "timeline total and bubble duration share the same monotonic start and end");
        Check(!saved.IsLive && reply.HasExecutionTimelineAction, "completed assistant bubble keeps the clickable timeline action");
        using var reopened = new ExecutionTimelineService(fixture.ConfigService, fixture.Paths, Log.Logger);
        Check((await reopened.LoadAsync(reply.Id)).Snapshot?.MessageId == reply.Id, "a reopened service resolves the timeline by stable message ID");
        Check(fixture.Files.Observations.Count == 1 && model.Requests.Count == 2, "the message recorder never re-executes tools or models");

        await using var failingFixture = new Fixture();
        failingFixture.Config.ExecutionAnalysisEnabled = true;
        await File.WriteAllTextAsync(Path.Combine(failingFixture.Root, "ExecutionTimelines"), "synthetic storage failure");
        await using var failingModel = new LoopbackModel("const r=await tools.call('read_system_file',{path:inputs.path}); text(r.success);", failingFixture.File);
        using var failingStore = new ExecutionTimelineService(failingFixture.ConfigService, failingFixture.Paths, Log.Logger);
        using var failingChat = new MainConversationViewModel(failingFixture.Chat(failingModel.BaseUrl, ProviderProtocol.ChatCompletions),
            failingFixture.ConfigService, new HeadlessPromptService(), failingFixture.Registry, new TokenService(), null,
            sessionAccessor: failingFixture.Sessions, executionTimelines: failingStore);
        failingChat.InputText = "Read the synthetic file.";
        await failingChat.SendMessageCommand.ExecuteAsync(null);
        var unaffected = failingChat.Messages.Last(item => item.Role == "assistant" && !item.IsHidden);
        Check(unaffected.Content == "done" && (await failingStore.LoadAsync(unaffected.Id)).Snapshot?.PersistenceFailed == true,
            "timeline storage failure is visible but never replaces or fails the actual assistant reply");
        using var failedView = new ExecutionTimelineViewModel(failingStore, unaffected.Id, null);
        await failedView.RefreshAsync();
        Check(failedView.Notice.Contains("could not be saved", StringComparison.Ordinal), "the timeline window explicitly explains an unsaved record");
        Check(failingModel.Requests.Count == 2 && failingFixture.Files.Observations.Count == 1, "storage failure never replays the model or tool");
    }

    private static async Task ConcurrentMessageTimelinesAsync()
    {
        await using var fixture = new Fixture();
        fixture.Config.ToolApprovalMode = ToolApprovalMode.Off;
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        fixture.Files.BeforeRead = async (_, token) =>
        {
            if (Interlocked.Increment(ref started) == 2) bothStarted.TrySetResult();
            await bothStarted.Task.WaitAsync(token);
        };
        await using var firstModel = new LoopbackModel("const r=await tools.call('read_system_file',{path:inputs.path}); text(r.success);", fixture.File);
        await using var secondModel = new LoopbackModel("const r=await tools.call('read_system_file',{path:inputs.path}); text(r.success);", fixture.File);
        var firstService = fixture.Chat(firstModel.BaseUrl, ProviderProtocol.ChatCompletions);
        var secondService = fixture.Chat(secondModel.BaseUrl, ProviderProtocol.ChatCompletions);
        var first = new ExecutionTimelineCollector(Guid.NewGuid().ToString("N"), System.Diagnostics.Stopwatch.GetTimestamp(), DateTimeOffset.UtcNow);
        var second = new ExecutionTimelineCollector(Guid.NewGuid().ToString("N"), System.Diagnostics.Stopwatch.GetTimestamp(), DateTimeOffset.UtcNow);
        static async Task Drive(OpenAIChatService service, string session, ExecutionTimelineCollector recorder)
        {
            var context = new ConversationContext { ConversationId = session, WorkspaceId = "w-" + session };
            await foreach (var _ in service.StreamMessageAsync("Read the synthetic file.", context, executionTimeline: recorder)) { }
        }
        await Task.WhenAll(Drive(firstService, "one", first), Drive(secondService, "two", second));
        var traces = new[] { first.Complete(ExecutionTimelineState.Succeeded), second.Complete(ExecutionTimelineState.Succeeded) };
        Check(traces[0].MessageId != traces[1].MessageId && traces.All(item => item.ModelAttempts == 2 && item.PtcAttempts == 1),
            "concurrent sessions maintain separate per-message model and PTC timelines");
        Check(traces.All(item => item.Spans.Count(span => span.Lane == ExecutionTimelineLane.PtcTools) == 1),
            "parallel sessions do not cross-contaminate child events");
        Check(fixture.Files.Observations.Count == 2 && firstModel.Requests.Count == 2 && secondModel.Requests.Count == 2,
            "parallel trace collection adds no extra business operations");
    }

    private static async Task CancelledLoopbackAsync()
    {
        await using var fixture = new Fixture();
        await using var model = new LoopbackModel("await tools.call('read_system_file',{path:inputs.path});", fixture.File);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource();
        fixture.Files.BeforeRead = async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        };
        var context = new ConversationContext { ConversationId = "cancel-loop" };
        var observed = new List<ChatMessage>();
        var service = fixture.Chat(model.BaseUrl, ProviderProtocol.ChatCompletions);
        var timeline = new ExecutionTimelineCollector(Guid.NewGuid().ToString("N"), System.Diagnostics.Stopwatch.GetTimestamp(), DateTimeOffset.UtcNow);
        var execution = Task.Run(async () =>
        {
            try
            {
                await foreach (var _ in service.StreamMessageAsync("Read fixture then stop.", context, cancellationToken: stop.Token,
                    onMessageAdded: observed.Add, onPtcProgress: fixture.Progress.Enqueue, executionTimeline: timeline)) { }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        stop.Cancel();
        await execution.WaitAsync(TimeSpan.FromSeconds(10));
        Check(model.Requests.Count == 1, "A09 Stop does not send a new model round");
        var tool = observed.Single(item => item.Role == "tool");
        var data = PtcJson.Parse(tool.Content!).GetProperty("data");
        Check(data.GetProperty("status").GetString() == "cancelled" && data.GetProperty("traceRef").ValueKind == JsonValueKind.String, "A09 interruption repair retains actual cancelled record/reference");
        Check((await fixture.Store.ReadAsync(data.GetProperty("traceRef").GetString()!))!.Execution.Status == PtcRunStatus.Cancelled, "cancelled run has an immutable terminal trace");
        Check(fixture.Progress.Last().Kind == "RunFinished" && fixture.Progress.Any(item => item.Kind == "CallFinished"), "cancellation ends process cards through independent cleanup events");
        Check(context.Messages.Count(item => item.Role == "tool") == 1 && tool.ToolCallId == "call_program", "Stop leaves valid outer protocol pairing");
        var trace = timeline.Complete(ExecutionTimelineState.Cancelled);
        Check(trace.State == ExecutionTimelineState.Cancelled && trace.Spans.All(item => item.EndUs is not null), "Stop finishes every timeline span without leaving running cards");
        Check(trace.Spans.Any(item => item.Lane == ExecutionTimelineLane.Script && item.State is ExecutionTimelineState.Cancelled or ExecutionTimelineState.OutcomeUnknown),
            "a stopped script is cancelled or honestly unknown, never a fabricated success");
    }

    private static async Task ArchiveAndTraceAsync()
    {
        await using var fixture = new Fixture();
        var result = await fixture.ProgramAsync("const r=await tools.call('read_system_file',{path:inputs.path}); text({ok:r.success});");
        var traceRef = Data(result).GetProperty("traceRef").GetString()!;
        var messages = new List<ChatMessage>
        {
            new() { Id = "u", Role = "user", Content = "Synthetic request" },
            new() { Id = "a", Role = "assistant", ToolCallsJson = "[{\"Id\":\"outer\",\"FunctionName\":\"execute_program\",\"Arguments\":\"{}\"}]", IsHidden = true },
            new() { Id = "t", Role = "tool", ToolCallId = "outer", ToolName = "execute_program", Content = result.ToJson() },
            new() { Id = "done", Role = "assistant", Content = "done" }
        };
        using var store = new ConversationArchiveStore(fixture.Paths, Log.Logger);
        var historyId = Guid.NewGuid().ToString("N");
        var forkId = Guid.NewGuid().ToString("N");
        var conversationId = Guid.NewGuid().ToString("N");
        await store.SaveAsync(new ConversationHistoryItem { Id = historyId, ConversationId = conversationId, Summary = "PTC fixture", Messages = messages });
        var restored = (await store.LoadByIdAsync(historyId))!;
        Check(restored.Messages.Single(item => item.Id == "t").Content!.Contains(traceRef, StringComparison.Ordinal), "A10 reopening preserves opaque reference");
        var forkMessages = restored.Messages.Select(ConversationPersistenceHelper.CloneMessage).ToList();
        await store.SaveAsync(new ConversationHistoryItem { Id = forkId, ConversationId = Guid.NewGuid().ToString("N"), Summary = "PTC fork", ForkedFromConversationId = conversationId, ForkedAtMessageId = "done", Messages = forkMessages });
        var fork = (await store.LoadByIdAsync(forkId))!;
        Check(fork.Messages.Single(item => item.Id == "t").Content == messages[2].Content, "branch keeps the same immutable result, not a replay");
        var before = JsonSerializer.Serialize((await fixture.Store.ReadAsync(traceRef))!, PtcJson.Options);
        // Rewind removes a later reply; it only projects historical messages and must not call Registry.
        var rewind = fork.Messages.Take(3).Select(ConversationPersistenceHelper.CloneMessage).ToList();
        Check(rewind.Single(item => item.Role == "tool").Content!.Contains(traceRef, StringComparison.Ordinal), "rewind retains an existing trace reference");
        using var reopened = new AthenaPtcRunStore(fixture.Paths, Log.Logger);
        Check(JsonSerializer.Serialize((await reopened.ReadAsync(traceRef))!, PtcJson.Options) == before, "a new store instance can query unchanged history");
        Check(fixture.Files.Observations.Count == 1, "restore/fork/rewind/query do not execute tools again");
        try { await reopened.ReadAsync("ptc:../../outside"); throw new InvalidOperationException("caller path was accepted"); }
        catch (ArgumentException) { Check(true, "trace lookup rejects script paths"); }
    }

    private static async Task FileSafetyAsync()
    {
        await using var fixture = new Fixture();
        var blocked = Path.Combine(fixture.Root, "blocked");
        Directory.CreateDirectory(blocked);
        var secret = Path.Combine(blocked, "synthetic-secret.txt");
        await File.WriteAllTextAsync(secret, "SYNTHETIC_ONLY");
        foreach (var rules in new[] { fixture.Config.FileSystemPolicy.Platforms.Linux, fixture.Config.FileSystemPolicy.Platforms.MacOS, fixture.Config.FileSystemPolicy.Platforms.Windows })
            rules.ReadAccess.BlockedDirectories.Add(blocked);
        var result = await fixture.ProgramAsync("text((await tools.call('read_system_file',{path:inputs.path})).success);", inputs: new { path = secret });
        Check(!Output(result)[0].GetBoolean(), "A11 PTC cannot bypass production read blocklist");
        if (!OperatingSystem.IsWindows())
        {
            var link = Path.Combine(fixture.Root, "allowed-looking-link");
            Directory.CreateSymbolicLink(link, blocked);
            result = await fixture.ProgramAsync("text((await tools.call('read_system_file',{path:inputs.path})).success);", inputs: new { path = Path.Combine(link, "synthetic-secret.txt") });
            Check(!Output(result)[0].GetBoolean(), "PTC retains resolved symlink security checks");
        }
        fixture.Config.FileSystemPolicy.Global.MaxReadSizeBytes = 2;
        result = await fixture.ProgramAsync("text((await tools.call('read_system_file',{path:inputs.path})).success);");
        Check(!Output(result)[0].GetBoolean(), "PTC cannot widen the original file size quota");
        fixture.Config.PtcEnabled = false;
        fixture.Config.FileSystemPolicy.Global.MaxReadSizeBytes = 1024 * 1024;
        using (ToolApprovalContext.EnterInteractive())
        {
            var direct = await fixture.Registry.ExecuteAsync("read_system_file", JsonSerializer.Serialize(new { path = fixture.File }));
            Check(direct.Success, "A13 direct tools still work after PTC is disabled");
        }
    }

    private static async Task ArtifactOwnershipAsync()
    {
        await using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Paths.GetAttachmentDirectory());
        var path = Path.Combine(fixture.Paths.GetAttachmentDirectory(), "synthetic.txt");
        await File.WriteAllTextAsync(path, "test");
        var attachment = new ChatAttachment { Id = "artifact", FileName = "synthetic.txt", StoredPath = path, MimeType = "text/plain", SizeBytes = 4 };
        var fake = new AttachmentRegistry(attachment);
        var invocation = fixture.Invocation();
        var context = new PtcRunContext(Guid.NewGuid().ToString("N"), "outer", "conversation", "workspace", "task");
        var host = new AthenaPtcToolHost(invocation, context, fake, fixture.ConfigService, fixture.Sessions, fixture.Files, fixture.Paths);
        var request = new PtcExecutionRequest("await tools.call('read_system_file',{path:inputs.path}); throw new Error('after artifact');", JsonSerializer.SerializeToElement(new { path = fixture.File }),
            new HashSet<string>(["read_system_file"], StringComparer.Ordinal), context, new());
        var result = await new PtcExecutor(new global::Ptc.JavaScript.JintScriptRuntime()).ExecuteAsync(request, host);
        Check(result.Status == PtcRunStatus.Failed && result.Artifacts.Count == 1, "A11 generated reference survives later script failure");
        Check(host.GetAttachments(result.Artifacts).Single().Id == attachment.Id && fake.Calls == 1, "completed attachment is neither regenerated nor dropped");
        attachment.StoredPath = fixture.File; // Existing but outside the host-owned attachment root.
        var invalid = await new PtcExecutor(new global::Ptc.JavaScript.JintScriptRuntime()).ExecuteAsync(request with { Code = "await tools.call('read_system_file',{path:inputs.path});" }, host);
        Check(invalid.Status == PtcRunStatus.Failed && invalid.Artifacts.Count == 0, "foreign attachment paths cannot become trusted references");
    }

    private static async Task TraceQuotaAndSaveFailureAsync()
    {
        await using var fixture = new Fixture();
        using var store = new AthenaPtcRunStore(fixture.Paths, Log.Logger, maxRunBytes: 1024, maxStoreBytes: 1024);
        using var reservation = await store.ReserveAsync(default);
        try { using var extra = await store.ReserveAsync(default); throw new InvalidOperationException("quota not enforced"); }
        catch (PtcRuntimeException ex) { Check(ex.Code == "trace_store_full", "store reserves capacity before execution"); }
        var record = new PtcExecutionResult(new(reservation.RunId, "outer", null, null, null), PtcRunStatus.Completed, [], [], [], new(0, 0, 0, 0, 0, 0, TimeSpan.Zero), null);
        var reference = await store.SaveAsync(reservation, "text(1);", record, default);
        Check(await store.ReadAsync(reference) is not null, "reference is returned only after an atomic completed record exists");
        try { await store.SaveAsync(reservation, "text(2);", record, default); throw new InvalidOperationException("immutable record overwritten"); }
        catch (IOException) { Check(true, "existing trace is immutable"); }
        Check(!Directory.EnumerateFiles(Path.Combine(fixture.Root, "PtcRuns"), "*.tmp").Any(), "failed saves clean temporary files");
    }

    private static JsonElement Data(FunctionResult result) => JsonSerializer.SerializeToElement(result.Data, PtcJson.Options);
    private static JsonElement Output(FunctionResult result) => Data(result).GetProperty("output");

    private static async Task LivePolicyAndBudgetAsync()
    {
        await using var fixture = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        fixture.Files.BeforeRead = async (_, token) =>
        {
            if (Interlocked.Increment(ref reads) != 1) return;
            fixture.Config.ToolApprovalMode = ToolApprovalMode.Strict;
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
        };
        var execution = fixture.ProgramAsync("await Promise.all([1,2].map(()=>tools.call('read_system_file',{path:inputs.path}))); text('ok');");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(fixture.Files.Observations.Count == 1, "a queued read cannot cross a live Strict policy barrier");
        release.TrySetResult();
        var result = await execution;
        Check(result.Success && fixture.Prompter.Calls.Count == 2, "queued child gets actual Strict approval after the outer Balanced approval");
        var trace = await fixture.Store.ReadAsync(Data(result).GetProperty("traceRef").GetString()!);
        Check(trace!.Execution.Metrics.PeakConcurrency == 1, "live policy downgrade is serial, not merely an old scheduling label");

        fixture.Files.BeforeRead = (_, _) => { fixture.Config.MaxToolResultChars = 100; return Task.CompletedTask; };
        result = await fixture.ProgramAsync("await tools.call('read_system_file',{path:inputs.path}); text({long:'x'.repeat(300)});");
        using var parsed = JsonDocument.Parse(result.ToJson());
        Check(!result.Success && parsed.RootElement.GetProperty("message").GetString() == "model_output_limit_exceeded", "a live ordinary budget shrink returns complete error JSON, never a sliced PTC projection");
        Check(result.ToJson().Length <= 100, "short structural limit error obeys the live budget");
    }

    private static async Task FailedTraceIsNeverReferencedAsync()
    {
        await using var fixture = new Fixture(maxTraceRunBytes: 256, maxTraceStoreBytes: 256);
        var result = await fixture.ProgramAsync("text({ok:true});");
        Check(!result.Success && Data(result).GetProperty("traceRef").ValueKind == JsonValueKind.Null, "save failure never returns an unqueryable trace reference");
        Check(Data(result).GetProperty("failure").GetProperty("code").GetString() == "trace_store_failed", "record persistence failure is explicit");
        Check(!Directory.EnumerateFiles(Path.Combine(fixture.Root, "PtcRuns"), "*.json").Any(), "failed record was not partly committed");
        await File.WriteAllBytesAsync(Path.Combine(fixture.Root, "PtcRuns", "capacity-fixture.json"), new byte[256]);
        result = await fixture.ProgramAsync("await tools.call('read_system_file',{path:inputs.path});");
        Check(!result.Success && result.ErrorCode == "trace_store_full", "full store rejects execution before acquiring any tool capability");
        Check(fixture.Files.Observations.IsEmpty, "full trace storage cannot start real tools");
    }

    private static void VerifyPackedAssemblies()
    {
        Check(File.Exists(Path.Combine(AppContext.BaseDirectory, "PTC-LICENSE.txt")), "component MIT license accompanies the desktop binary output");
        Check(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "PTC-ThirdPartyNotices.md")).Contains("Acornima", StringComparison.Ordinal), "Jint/Acornima redistribution notices accompany the host output");
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Athena.UI.csproj"))) root = root.Parent;
        if (root is null) throw new InvalidOperationException("PTC package verification could not locate repository root.");
        var source = System.Xml.Linq.XDocument.Load(Path.Combine(root.FullName, "Athena.UI.csproj"));
        Check(!source.Descendants("ProjectReference").Any(item => ((string?)item.Attribute("Include"))?.Contains("Libraries/Ptc", StringComparison.Ordinal) == true), "A14 Athena never uses component ProjectReference");
        using var assets = JsonDocument.Parse(File.ReadAllText(Path.Combine(root.FullName, "obj", "project.assets.json")));
        foreach (var (id, assembly) in new[] { ("AthenaAgent.Ptc.Core", typeof(PtcExecutor).Assembly), ("AthenaAgent.Ptc.JavaScript", typeof(global::Ptc.JavaScript.JintScriptRuntime).Assembly) })
        {
            var library = assets.RootElement.GetProperty("libraries").GetProperty(id + "/0.1.0-preview.4");
            Check(library.GetProperty("type").GetString() == "package", "A14 actual dependency graph uses a package for " + id);
            var relative = library.GetProperty("path").GetString()!;
            var packageRoot = assets.RootElement.GetProperty("packageFolders").EnumerateObject()
                .Select(item => Path.Combine(item.Name, relative)).Single(Directory.Exists);
            var dll = Path.Combine(packageRoot, "lib", "net10.0", assembly.GetName().Name + ".dll");
            Check(SHA256.HashData(File.ReadAllBytes(assembly.Location)).SequenceEqual(SHA256.HashData(File.ReadAllBytes(dll))), "loaded DLL equals installed package asset for " + id);
            var archive = Path.Combine(root.FullName, "artifacts", "ptc", "feed", id + ".0.1.0-preview.4.nupkg");
            var hash = File.ReadAllText(Path.Combine(packageRoot, id.ToLowerInvariant() + ".0.1.0-preview.4.nupkg.sha512")).Trim();
            Check(hash == Convert.ToBase64String(SHA512.HashData(File.ReadAllBytes(archive))), "installed nupkg is this local feed build for " + id);
            Check(source.Descendants("PackageReference").Any(item => (string?)item.Attribute("Include") == id && (string?)item.Attribute("Version") == "[0.1.0-preview.4]"), "component version is exact in the application project");
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public string Root { get; } = Path.Combine(Directory.Exists("/tmp/opencode") ? "/tmp/opencode" : Path.GetTempPath(), "athena-ptc-" + Guid.NewGuid().ToString("N"));
        public string File => Path.Combine(Root, "fixture.txt");
        public AppConfig Config { get; } = new() { PtcEnabled = true, ToolApprovalMode = ToolApprovalMode.Balanced, ImageGenerationEnabled = false, BrowserEnabled = false, EnableSkills = false, EnableMcp = false };
        public IConfigService ConfigService { get; }
        public TemporaryPathService Paths { get; }
        public IConversationSessionAccessor Sessions { get; } = new ConversationSessionAccessor();
        public ProbeFileSystem Files { get; }
        public Prompt Prompter { get; } = new();
        public Judge Judge { get; } = new();
        public ConcurrentQueue<PtcProgress> Progress { get; } = new();
        private readonly ServiceProvider _provider;
        public IFunctionRegistry Registry => _provider.GetRequiredService<IFunctionRegistry>();
        public AthenaPtcRunStore Store => _provider.GetRequiredService<AthenaPtcRunStore>();

        public Fixture(int maxTraceRunBytes = AthenaPtcRunStore.DefaultMaxRunBytes, long maxTraceStoreBytes = AthenaPtcRunStore.DefaultMaxStoreBytes)
        {
            Directory.CreateDirectory(Root);
            System.IO.File.WriteAllText(File, "synthetic data");
            Paths = new TemporaryPathService(Root);
            ConfigService = new FixtureConfig(Config, Paths.GetConfigFilePath());
            Files = new ProbeFileSystem(new FileSystemService(ConfigService, Paths, Log.Logger), Sessions);
            var services = new ServiceCollection();
            var configure = typeof(App).GetMethod("ConfigureServices", BindingFlags.NonPublic | BindingFlags.Static)!;
            configure.Invoke(null, [services]);
            services.AddSingleton<IConfigService>(ConfigService);
            services.AddSingleton<IPlatformPathService>(Paths);
            services.AddSingleton<IConversationSessionAccessor>(Sessions);
            services.AddSingleton<IFileSystemService>(Files);
            services.AddSingleton<IKnowledgeBaseService>(_ => new KnowledgeBaseService(Log.Logger, platformPathService: Paths));
            services.AddSingleton<IToolApprovalPrompter>(Prompter);
            services.AddSingleton<IAiToolApprovalEvaluator>(Judge);
            if (maxTraceRunBytes != AthenaPtcRunStore.DefaultMaxRunBytes || maxTraceStoreBytes != AthenaPtcRunStore.DefaultMaxStoreBytes)
                services.AddSingleton(_ => new AthenaPtcRunStore(Paths, Log.Logger, maxTraceRunBytes, maxTraceStoreBytes));
            _provider = services.BuildServiceProvider();
            Check(Registry is FunctionRegistry, "production Registry resolved through real DI without a constructor cycle");
        }

        public AthenaPtcInvocation Invocation(IEnumerable<string>? names = null, string conversation = "conversation", string workspace = "workspace", string task = "delegated task")
        {
            var all = Registry.GetToolDefinitions().Cast<ChatTool>().Where(tool => AthenaPtcInvocationScope.ReadOnlyTools.Contains(tool.FunctionName));
            if (names is not null) all = all.Where(tool => names.Contains(tool.FunctionName, StringComparer.Ordinal));
            return new("outer", all.Select(tool => new PtcToolDefinition(tool.FunctionName, tool.FunctionDescription, PtcJson.Parse(tool.FunctionParameters.ToString()))).ToArray(),
                conversation, workspace, task, ToolApprovalContext.ExecutionMode.Interactive, Progress.Enqueue);
        }

        public Task<FunctionResult> ProgramAsync(string code, IEnumerable<string>? names = null, object? inputs = null,
            string conversation = "conversation", string workspace = "workspace", string task = "delegated task")
            => ProgramArgumentsAsync(JsonSerializer.Serialize(new { code, inputs = inputs ?? new { path = File } }), names, conversation, workspace, task);

        public async Task<FunctionResult> ProgramArgumentsAsync(string json, IEnumerable<string>? names = null,
            string conversation = "conversation", string workspace = "workspace", string task = "delegated task")
        {
            using var token = new CancellationTokenSource();
            using var conversationScope = Sessions.Enter(conversation);
            using var workspaceScope = Sessions.EnterWorkspace(workspace);
            using var approvalScope = ToolApprovalContext.EnterInteractive(task);
            using var tokenScope = ToolExecutionContext.Enter(token.Token);
            using var invocation = AthenaPtcInvocationScope.Enter(Invocation(names, conversation, workspace, task));
            return await Registry.ExecuteAsync("execute_program", json);
        }

        public OpenAIChatService Chat(string endpoint, ProviderProtocol protocol)
        {
            var provider = new OpenAiProviderConfiguration { Id = Guid.NewGuid().ToString("N"), BaseUrl = endpoint, ApiKey = "ptc-test-key-not-real", Protocol = protocol, ProviderPreset = "Custom" };
            provider.Models.Add(new ProviderModelDescriptor { Id = "ptc-test", Capability = ModelCapability.Text });
            Config.AiModels.Providers.Add(provider);
            Config.AiModels.MainConversation.ProviderId = provider.Id;
            Config.AiModels.MainConversation.Model = "ptc-test";
            return new OpenAIChatService(Config, new HeadlessPromptService(), functionRegistry: Registry, configService: ConfigService,
                conversationSessionAccessor: Sessions, metadataResolver: new ModelMetadataResolver(new ModelIdentityMatcher()),
                contextPolicyResolver: new ModelContextPolicyResolver(), requestPreparer: new ContextRequestPreparer(new TokenFingerprintService(Paths)));
        }

        public async ValueTask DisposeAsync()
        {
            await _provider.DisposeAsync();
            // Only this test's newly-created GUID directory; never a user workspace or app data directory.
            Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class FixtureConfig(AppConfig config, string file) : IConfigService
    {
        public AppConfig Load() => config;
        public Task<AppConfig> LoadAsync() => Task.FromResult(config);
        public string ConfigFilePath => file;
        public event EventHandler<AppConfig>? ConfigChanged;
        public Task SaveAsync(AppConfig value) { ConfigChanged?.Invoke(this, value); return Task.CompletedTask; }
    }

    private sealed class Prompt : IToolApprovalPrompter
    {
        public ConcurrentQueue<ToolApprovalRequest> Calls { get; } = new();
        public Func<ToolApprovalRequest, ToolApprovalScope> Decide { get; set; } = _ => ToolApprovalScope.AllowOnce;
        public Task<ToolApprovalScope> PromptAsync(ToolApprovalRequest request, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Calls.Enqueue(request); return Task.FromResult(Decide(request)); }
    }
    private sealed class Judge : IAiToolApprovalEvaluator
    {
        public ConcurrentQueue<ToolApprovalRequest> Calls { get; } = new();
        public ToolApprovalDecision Decision { get; set; } = ToolApprovalDecision.JudgeAllowed("synthetic fixture");
        public Task<ToolApprovalDecision> EvaluateAsync(ToolApprovalRequest request, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Calls.Enqueue(request); return Task.FromResult(Decision); }
    }

    private sealed record Observation(string Name, string? Conversation, string? Workspace, string? Task,
        ToolApprovalContext.ExecutionMode Mode, CancellationToken Token, bool IsChild);

    private sealed class ProbeFileSystem(IFileSystemService inner, IConversationSessionAccessor sessions) : IFileSystemService
    {
        public ConcurrentQueue<Observation> Observations { get; } = new();
        public Func<string, CancellationToken, Task>? BeforeRead { get; set; }
        private async Task Observe(string name, string path)
        {
            var token = ToolExecutionContext.CurrentCancellationToken;
            Observations.Enqueue(new(name, sessions.CurrentConversationId, sessions.CurrentWorkspaceId,
                ToolApprovalContext.CurrentDelegatedTask, ToolApprovalContext.CurrentMode, token, AthenaPtcInvocationScope.IsChild));
            if (BeforeRead is not null) await BeforeRead(path, token);
        }
        public async Task<string?> ReadFileAsync(string absolutePath, int? startLine = null, int? endLine = null, string? sectionTitle = null, int? chunkIndex = null, bool includeLineNumbers = false)
        { await Observe("read_system_file", absolutePath); return await inner.ReadFileAsync(absolutePath, startLine, endLine, sectionTitle, chunkIndex, includeLineNumbers); }
        public async Task<FileMetadata?> GetFileInfoAsync(string absolutePath, bool includeTextStatistics = false)
        { return await inner.GetFileInfoAsync(absolutePath, includeTextStatistics); }
        public async Task<FileSearchResult> SearchInFileAsync(string absolutePath, string pattern, int contextLines = 3, int maxMatches = 10)
        { await Observe("search_in_file", absolutePath); return await inner.SearchInFileAsync(absolutePath, pattern, contextLines, maxMatches); }
        public async Task<DirectorySearchResult> SearchInDirectoryAsync(string absolutePath, string pattern, DirectorySearchOptions options, CancellationToken cancellationToken = default)
        { await Observe("search_in_directory", absolutePath); return await inner.SearchInDirectoryAsync(absolutePath, pattern, options, cancellationToken); }
        public async Task<List<FileSystemEntry>> ListDirectoryAsync(string absolutePath, bool recursive = false, string? filter = null)
        { await Observe("list_system_directory", absolutePath); return await inner.ListDirectoryAsync(absolutePath, recursive, filter); }
        public Task<DocumentOutline> GetDocumentOutlineAsync(string absolutePath) => inner.GetDocumentOutlineAsync(absolutePath);
        public string GetAbsoluteSecurePath(string path, bool enforceReadSizeLimit = true) => inner.GetAbsoluteSecurePath(path, enforceReadSizeLimit);
        public string GetAbsoluteSecureWritePath(string path, long estimatedSizeBytes = 0) => inner.GetAbsoluteSecureWritePath(path, estimatedSizeBytes);
        public Task<bool> WriteFileAsync(string absolutePath, string content) => throw new InvalidOperationException("PTC V0 cannot write");
        public Task<FileUpdateResult> ModifyFileWithDiffAsync(string absolutePath, string diffContent, bool fuzzyMatch = true, bool replaceAll = false) => throw new InvalidOperationException("PTC V0 cannot modify");
        public Task<bool> DeleteFileAsync(string absolutePath, bool recursive = false) => throw new InvalidOperationException("PTC V0 cannot delete");
        public Task<bool> MoveFileAsync(string sourcePath, string destinationPath) => throw new InvalidOperationException("PTC V0 cannot move");
        public Task<bool> CopyFileAsync(string sourcePath, string destinationPath, bool overwrite = false) => throw new InvalidOperationException("PTC V0 cannot copy");
        public Task<DirectoryCreateOutcome> CreateDirectoryAsync(string absolutePath) => throw new InvalidOperationException("PTC V0 cannot create directories");
    }

    private sealed class AttachmentRegistry(ChatAttachment attachment) : IFunctionRegistry
    {
        public int Calls;
        public bool HasFunctions => true;
        public IEnumerable<object> GetToolDefinitions(bool includeOfficeTools = false) => [];
        public IEnumerable<object> GetToolDefinitions(IEnumerable<string> toolNames) => [];
        public Task<FunctionResult> ExecuteAsync(string functionName, string argumentsJson)
        { Interlocked.Increment(ref Calls); return Task.FromResult(FunctionResult.SuccessResult("artifact", new { ok = true }, [attachment])); }
    }

    /// <summary>Actual TCP/HTTP loopback, not a mock executor, transport or SDK deserializer.</summary>
    private sealed class LoopbackModel : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _pump;
        private readonly string _arguments;
        private readonly string _toolName;
        public string BaseUrl { get; }
        public ConcurrentQueue<JsonElement> Requests { get; } = new();

        public LoopbackModel(string code, string path, string toolName = "execute_program")
        {
            using var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            BaseUrl = $"http://127.0.0.1:{port}/v1";
            _toolName = toolName;
            _arguments = toolName == "execute_program" ? JsonSerializer.Serialize(new { code, inputs = new { path } })
                : JsonSerializer.Serialize(new { path });
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _pump = Task.Run(PumpAsync);
        }
        private async Task PumpAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var request = await _listener.GetContextAsync();
                    using var reader = new StreamReader(request.Request.InputStream, Encoding.UTF8);
                    var body = await reader.ReadToEndAsync(_stop.Token);
                    Requests.Enqueue(PtcJson.Parse(body, 1024 * 1024, 64));
                    Check(request.Request.Headers["Authorization"] == "Bearer ptc-test-key-not-real", "only synthetic test credentials reach localhost");
                    var responses = request.Request.Url!.AbsolutePath.EndsWith("/responses", StringComparison.Ordinal);
                    var sse = responses ? ResponsesBody(Requests.Count == 1) : ChatBody(Requests.Count == 1);
                    var bytes = Encoding.UTF8.GetBytes(sse);
                    request.Response.ContentType = "text/event-stream";
                    request.Response.ContentLength64 = bytes.Length;
                    await request.Response.OutputStream.WriteAsync(bytes, _stop.Token);
                    request.Response.Close();
                }
            }
            catch (HttpListenerException) when (_stop.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        }

        private static string Sse(object value) => "data: " + JsonSerializer.Serialize(value) + "\n\n";
        private string ChatBody(bool first)
        {
            object delta = first ? new { role = "assistant", tool_calls = new[] { new { index = 0, id = "call_program", type = "function", function = new { name = _toolName, arguments = _arguments } } } }
                : new { role = "assistant", content = "done" };
            return Sse(new { id = "chat_ptc", @object = "chat.completion.chunk", created = 1785580000, model = "ptc-test",
                choices = new[] { new { index = 0, delta, finish_reason = first ? "tool_calls" : "stop" } },
                usage = new { prompt_tokens = 41, completion_tokens = 5, total_tokens = 46 } }) + "data: [DONE]\n\n";
        }
        private string ResponsesBody(bool first)
        {
            if (!first) return ResponsesSseHandler.FinalRoundBody;
            object item = new { id = "fc_ptc", type = "function_call", status = "in_progress", call_id = "call_program", name = _toolName, arguments = "" };
            object done = new { id = "fc_ptc", type = "function_call", status = "completed", call_id = "call_program", name = _toolName, arguments = _arguments };
            return Sse(new { type = "response.created", sequence_number = 0, response = new { id = "resp_ptc", @object = "response", created_at = 1785580000, status = "in_progress", model = "ptc-test", output = Array.Empty<object>() } })
                + Sse(new { type = "response.output_item.added", sequence_number = 1, output_index = 0, item })
                + Sse(new { type = "response.function_call_arguments.delta", sequence_number = 2, item_id = "fc_ptc", output_index = 0, delta = _arguments })
                + Sse(new { type = "response.function_call_arguments.done", sequence_number = 3, item_id = "fc_ptc", output_index = 0, arguments = _arguments })
                + Sse(new { type = "response.output_item.done", sequence_number = 4, output_index = 0, item = done })
                + Sse(new { type = "response.completed", sequence_number = 5, response = new { id = "resp_ptc", @object = "response", created_at = 1785580000, status = "completed", model = "ptc-test", output = new[] { done }, usage = new { input_tokens = 41, output_tokens = 5, total_tokens = 46 } } })
                + "data: [DONE]\n\n";
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Close();
            await _pump;
            _stop.Dispose();
        }
    }
}
