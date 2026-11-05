using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Athena.UI.Models;
using Athena.UI.Services.ExecutionTimeline;
using Serilog;

internal static class ExecutionTimelineTests
{
    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException("Execution timeline: " + message);
    }

    public static async Task RunAsync()
    {
        var origin = Stopwatch.GetTimestamp() - 5 * Stopwatch.Frequency;
        long At(double seconds) => origin + (long)(Stopwatch.Frequency * seconds);
        var messageId = Guid.NewGuid().ToString("N");
        var trace = new ExecutionTimelineCollector(messageId, origin, DateTimeOffset.UtcNow);
        var model = trace.Begin(ExecutionTimelineLane.Model, ExecutionTimelineKind.ModelRequest, "model", round: 1, attempt: 1, timestamp: At(0.1));
        trace.Usage(model, 200, 30, 40);
        trace.End(model, ExecutionTimelineState.Succeeded, At(1));
        var outer = trace.QueueOuterTool("parent", "execute_program", declared: true, round: 1);
        trace.StartOuterTool(outer);
        // The host can enter approval before the queued observer has delivered CallStarted.
        var precreated = trace.EnsurePtcChild("r:1", "read_system_file");
        trace.ObservePtc(new("r", "parent", "conversation", 1, "ScriptStarted", null, null, null)
        { OccurredTimestamp = At(1.1) });
        trace.ObservePtc(new("r", "parent", "conversation", 2, "CallQueued", "r:1", "read_system_file", null)
        { OccurredTimestamp = At(1.3), CallState = "Queued" });
        trace.ObservePtc(new("r", "parent", "conversation", 3, "CallStarted", "r:1", "read_system_file", null)
        { OccurredTimestamp = At(1.5), CallState = "Running" });
        trace.ObservePtc(new("r", "parent", "conversation", 4, "CallQueued", "r:2", "get_file_info", null)
        { OccurredTimestamp = At(1.6), CallState = "Queued" });
        trace.ObservePtc(new("r", "parent", "conversation", 5, "CallStarted", "r:2", "get_file_info", null)
        { OccurredTimestamp = At(1.7), CallState = "Running" });
        trace.ObservePtc(new("r", "parent", "conversation", 6, "CallFinished", "r:1", "read_system_file", null)
        { OccurredTimestamp = At(2.2), CallState = "Succeeded" });
        trace.ObservePtc(new("r", "parent", "conversation", 7, "CallFinished", "r:2", "get_file_info", "cancelled")
        { OccurredTimestamp = At(2.5), CallState = "OutcomeUnknown" });
        trace.ObservePtc(new("r", "parent", "conversation", 8, "CallQueued", "r:3", "secret_should_not_be_saved", null)
        { OccurredTimestamp = At(2.6), CallState = "Queued" });
        trace.ObservePtc(new("r", "parent", "conversation", 9, "CallFinished", "r:3", "secret_should_not_be_saved", "tool_not_available")
        { OccurredTimestamp = At(2.6), CallState = "Denied" });
        trace.ObservePtc(new("r", "parent", "conversation", 10, "RunFinished", null, null, null)
        { OccurredTimestamp = At(3), RunStatus = "Completed" });
        trace.End(outer, ExecutionTimelineState.Succeeded);
        var snapshot = trace.Complete(ExecutionTimelineState.Succeeded);
        var script = snapshot.Spans.Single(s => s.Kind == ExecutionTimelineKind.ScriptRun);
        var first = snapshot.Spans.Single(s => s.Id == precreated);
        var second = snapshot.Spans.Single(s => s.Name == "get_file_info");
        var denied = snapshot.Spans.Single(s => s.Name == "unavailable_tool");
        Check(first.ParentId == script.Id && first.StartUs == 1_500_000 && first.EndUs == 2_200_000, "PTC timestamps are source times even after delayed event delivery");
        Check(second.ParentId == script.Id && second.StartUs < first.EndUs && second.State == ExecutionTimelineState.OutcomeUnknown, "parallel child bars overlap and unknown outcome is not success");
        Check(denied.State == ExecutionTimelineState.Denied && denied.StartUs == denied.EndUs, "unstarted denial is a marker, not an executed duration");
        Check(snapshot.ModelAttempts == 1 && snapshot.OuterTools == 1 && snapshot.OuterToolsStarted == 1 && snapshot.PtcAttempts == 3 && snapshot.PtcStarted == 2, "outer tool and PTC attempts are not double-counted");
        Check(snapshot.Spans.Single(s => s.Id == model).InputTokens == 200, "provider usage is preserved");
        Check(snapshot.Spans.Where(s => s.Kind != ExecutionTimelineKind.ModelRequest).All(s => s.InputTokens is null && s.OutputTokens is null),
            "tool and script lengths are never invented as token usage");
        Check(!JsonSerializer.Serialize(snapshot).Contains("secret_should_not_be_saved", StringComparison.Ordinal), "untrusted unavailable tool names are not persisted");

        var bounded = new ExecutionTimelineCollector(Guid.NewGuid().ToString("N"), Stopwatch.GetTimestamp(), DateTimeOffset.UtcNow);
        for (var i = 0; i < ExecutionTimelineCollector.MaxSpans + 10; i++)
            bounded.Begin(ExecutionTimelineLane.Model, ExecutionTimelineKind.ModelRequest, "model");
        var limited = bounded.Complete(ExecutionTimelineState.Succeeded);
        Check(limited.Spans.Count == ExecutionTimelineCollector.MaxSpans && limited.DroppedSpans == 10 && limited.IsPartial && limited.ModelAttempts == ExecutionTimelineCollector.MaxSpans + 10,
            "bounded spans preserve counts and disclose dropped data");

        var interrupted = new ExecutionTimelineCollector(Guid.NewGuid().ToString("N"), Stopwatch.GetTimestamp(), DateTimeOffset.UtcNow);
        var pending = interrupted.QueueOuterTool("queued", "get_file_info", true, 1);
        var started = interrupted.QueueOuterTool("started", "read_system_file", true, 1);
        interrupted.StartOuterTool(started);
        var stopped = interrupted.Complete(ExecutionTimelineState.Cancelled);
        var pendingSpan = stopped.Spans.Single(item => item.Id == pending);
        Check(pendingSpan.State == ExecutionTimelineState.NotStarted && pendingSpan.StartUs == pendingSpan.EndUs,
            "a cancelled queued call is a marker, not a fabricated execution duration");
        Check(stopped.Spans.Single(item => item.Id == started).State == ExecutionTimelineState.OutcomeUnknown,
            "an unfinished tool is not misrepresented as definitely cancelled");
        Check(stopped.OuterTools == 2 && stopped.OuterToolsStarted == 1, "attempt and actual start counts remain distinct");
        var blocked = new ExecutionTimelineCollector(Guid.NewGuid().ToString("N"), Stopwatch.GetTimestamp(), DateTimeOffset.UtcNow);
        var rejected = blocked.QueueOuterTool("guard", "read_system_file", true, 1);
        blocked.RejectQueuedOuterTool(rejected);
        var blockedSnapshot = blocked.Complete(ExecutionTimelineState.Succeeded);
        Check(blockedSnapshot.OuterTools == 1 && blockedSnapshot.OuterToolsStarted == 0
            && blockedSnapshot.Spans.Single(item => item.Id == rejected).State == ExecutionTimelineState.Denied,
            "repeat-failure guard is an attempt, not an execution");
        var unknownScript = new ExecutionTimelineCollector(Guid.NewGuid().ToString("N"), origin, DateTimeOffset.UtcNow);
        unknownScript.ObservePtc(new("late", "none", null, 1, "ScriptStarted", null, null, null) { OccurredTimestamp = At(1) });
        unknownScript.ObservePtc(new("late", "none", null, 2, "RunFinished", null, null, "cancelled") { OccurredTimestamp = At(2), RunStatus = "Cancelled" });
        Check(unknownScript.Complete(ExecutionTimelineState.Cancelled).Spans.Single().State == ExecutionTimelineState.OutcomeUnknown,
            "a script without a completion event remains outcome unknown after bounded drain");
        var missingChild = new ExecutionTimelineCollector(Guid.NewGuid().ToString("N"), origin, DateTimeOffset.UtcNow);
        missingChild.ObservePtc(new("r", "none", null, 1, "ScriptStarted", null, null, null) { OccurredTimestamp = At(1) });
        missingChild.ObservePtc(new("r", "none", null, 2, "CallQueued", "r:1", "read_system_file", null) { OccurredTimestamp = At(1.1) });
        missingChild.ObservePtc(new("r", "none", null, 3, "CallStarted", "r:1", "read_system_file", null) { OccurredTimestamp = At(1.2) });
        missingChild.ObservePtc(new("r", "none", null, 4, "RunFinished", null, null, "observer_error") { OccurredTimestamp = At(2) });
        var incomplete = missingChild.Complete(ExecutionTimelineState.Succeeded);
        Check(incomplete.IsPartial && incomplete.Spans.Single(item => item.Lane == ExecutionTimelineLane.PtcTools).State == ExecutionTimelineState.OutcomeUnknown,
            "a missing child terminal event is closed as unknown and explicitly marks the trace partial");
        var delayedStart = new ExecutionTimelineCollector(Guid.NewGuid().ToString("N"), origin, DateTimeOffset.UtcNow);
        delayedStart.ObservePtc(new("r", "none", null, 1, "ScriptStarted", null, null, null) { OccurredTimestamp = At(1) });
        delayedStart.EnsurePtcChild("r:1", "read_system_file", "r");
        delayedStart.ObservePtc(new("r", "none", null, 2, "CallQueued", "r:1", "read_system_file", null) { OccurredTimestamp = At(1.1) });
        delayedStart.ObservePtc(new("r", "none", null, 3, "RunFinished", null, null, "observer_error") { OccurredTimestamp = At(2) });
        var delayedSnapshot = delayedStart.Complete(ExecutionTimelineState.Failed);
        Check(delayedSnapshot.IsPartial && delayedSnapshot.PtcStarted == 1
            && delayedSnapshot.Spans.Single(item => item.Lane == ExecutionTimelineLane.PtcTools).State == ExecutionTimelineState.OutcomeUnknown,
            "actual host start survives a missing CallStarted event and is never called not-started");
        Check(delayedSnapshot.Spans.Single(item => item.Lane == ExecutionTimelineLane.PtcTools).ParentId
            == delayedSnapshot.Spans.Single(item => item.Lane == ExecutionTimelineLane.Script).Id,
            "missing observer events do not erase the child/script hierarchy");
        var boundedEvents = new ExecutionTimelineCollector(Guid.NewGuid().ToString("N"), origin, DateTimeOffset.UtcNow);
        for (var i = 0; i < ExecutionTimelineCollector.MaxSpans + 5; i++)
            boundedEvents.ObservePtc(new("r", "none", null, i + 1, "CallQueued", "r:" + i, "read_system_file", null) { OccurredTimestamp = At(1) });
        var clipped = boundedEvents.Complete(ExecutionTimelineState.Cancelled);
        Check(clipped.PtcAttempts == ExecutionTimelineCollector.MaxSpans + 5 && clipped.IsPartial,
            "unclosed PTC correlation state is bounded without losing attempt counts");
        var reusedId = new ExecutionTimelineCollector(Guid.NewGuid().ToString("N"), origin, DateTimeOffset.UtcNow);
        var firstOuter = reusedId.QueueOuterTool("same-id", "execute_program", true, 1, At(0.2));
        var secondOuter = reusedId.QueueOuterTool("same-id", "execute_program", true, 1, At(0.2));
        reusedId.StartOuterTool(firstOuter, At(0.3));
        reusedId.ObservePtc(new("first-run", "same-id", null, 1, "ScriptStarted", null, null, null) { OccurredTimestamp = At(0.4) });
        reusedId.ObservePtc(new("first-run", "same-id", null, 2, "ScriptFinished", null, null, null) { OccurredTimestamp = At(0.5), RunStatus = "Completed" });
        reusedId.ObservePtc(new("first-run", "same-id", null, 3, "RunFinished", null, null, null) { OccurredTimestamp = At(0.5), RunStatus = "Completed" });
        reusedId.End(firstOuter, ExecutionTimelineState.Succeeded, At(0.6));
        reusedId.StartOuterTool(secondOuter, At(0.7));
        reusedId.ObservePtc(new("second-run", "same-id", null, 1, "ScriptStarted", null, null, null) { OccurredTimestamp = At(0.8) });
        var scripts = reusedId.Complete(ExecutionTimelineState.Cancelled).Spans.Where(item => item.Kind == ExecutionTimelineKind.ScriptRun).ToArray();
        Check(scripts.Length == 2 && scripts[0].ParentId == firstOuter && scripts[1].ParentId == secondOuter,
            "a reused provider call ID cannot attach the first script to a later queued wrapper");

        var root = Path.Combine(Path.GetTempPath(), "athena-timeline-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var config = new AppConfig();
            Check(!config.ExecutionAnalysisEnabled, "analysis is off in old/default configuration");
            var roundtrip = JsonSerializer.Deserialize<AppConfig>(JsonSerializer.Serialize(new AppConfig { ExecutionAnalysisEnabled = true }));
            Check(roundtrip?.ExecutionAnalysisEnabled == true, "the user-owned analysis switch survives config serialization");
            var configService = new TestConfigService(config);
            var paths = new TestPlatformPathService(root);
            using (var store = new ExecutionTimelineService(configService, paths, Log.Logger))
            {
                var oldHistory = await store.LoadAsync(Guid.NewGuid().ToString("N"));
                Check(oldHistory.Snapshot is null && oldHistory.ErrorCode is null,
                    "old bubbles are an empty state even before the trace directory exists");
                Check(store.TryBegin(Guid.NewGuid().ToString("N"), Stopwatch.GetTimestamp(), DateTimeOffset.UtcNow) is null, "default off does not start recording");
                Check(!Directory.Exists(Path.Combine(root, "ExecutionTimelines")), "analysis disabled creates no trace files or directories");
                config.ExecutionAnalysisEnabled = true;
                var active = store.TryBegin(messageId, Stopwatch.GetTimestamp(), DateTimeOffset.UtcNow)!;
                active.Begin(ExecutionTimelineLane.Model, ExecutionTimelineKind.ModelRequest, "model");
                Check((await store.LoadAsync(messageId)).IsLive, "active run can be viewed live");
                config.ExecutionAnalysisEnabled = false;
                Check(active.Snapshot().IsPartial && active.Snapshot().State == ExecutionTimelineState.Incomplete, "switching off immediately stops and marks partial");
                await store.CompleteAsync(active, ExecutionTimelineState.Succeeded);
            }
            using (var reopened = new ExecutionTimelineService(configService, paths, Log.Logger))
            {
                var restored = await reopened.LoadAsync(messageId);
                Check(restored.Snapshot?.MessageId == messageId && restored.Snapshot.IsPartial && !restored.IsLive, "trace survives service restart and shared message IDs");
                var originalBubble = new ChatMessage { Id = messageId, Role = "assistant", Content = "reply" };
                var forkedBubble = ConversationPersistenceHelper.CloneMessage(originalBubble);
                Check(forkedBubble.Id == messageId && (await reopened.LoadAsync(forkedBubble.Id)).Snapshot?.MessageId == messageId,
                    "forked conversation clones resolve the same historical trace without replay");
                if (!OperatingSystem.IsWindows())
                {
                    var mode = File.GetUnixFileMode(Path.Combine(root, "ExecutionTimelines", messageId + ".json"));
                    Check((mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) == 0,
                        "local metadata files are private to the user on Unix");
                }
                Check((await reopened.LoadAsync("../../not-a-message")).Snapshot is null, "caller text cannot choose a path");
                Check((await reopened.LoadAsync(Guid.NewGuid().ToString("N"))).Snapshot is null, "old unrecorded bubbles are an explicit empty state");
            }

            var failureRoot = Path.Combine(root, "failure");
            Directory.CreateDirectory(failureRoot);
            await File.WriteAllTextAsync(Path.Combine(failureRoot, "ExecutionTimelines"), "not a directory");
            var failureConfig = new AppConfig { ExecutionAnalysisEnabled = true };
            using var failing = new ExecutionTimelineService(new TestConfigService(failureConfig), new TestPlatformPathService(failureRoot), Log.Logger);
            var failed = failing.TryBegin(Guid.NewGuid().ToString("N"), Stopwatch.GetTimestamp(), DateTimeOffset.UtcNow)!;
            await failing.CompleteAsync(failed, ExecutionTimelineState.Succeeded);
            Check((await failing.LoadAsync(failed.MessageId)).Snapshot!.PersistenceFailed, "write failure is visible and does not throw into the reply");
            for (var i = 0; i < 33; i++)
            {
                var newer = failing.TryBegin(Guid.NewGuid().ToString("N"), Stopwatch.GetTimestamp(), DateTimeOffset.UtcNow)!;
                await failing.CompleteAsync(newer, ExecutionTimelineState.Succeeded);
            }
            Check((await failing.LoadAsync(failed.MessageId)).ErrorCode == "storage_error", "a failed save is not relabelled as never collected after cache eviction");

            var quotaRoot = Path.Combine(root, "quota");
            Directory.CreateDirectory(Path.Combine(quotaRoot, "ExecutionTimelines"));
            var retained = Path.Combine(quotaRoot, "ExecutionTimelines", "retained.json");
            await File.WriteAllBytesAsync(retained, new byte[4096]);
            using (var limitedStore = new ExecutionTimelineService(new TestConfigService(failureConfig), new TestPlatformPathService(quotaRoot), Log.Logger,
                maxTraceBytes: 4096, maxStoreBytes: 4096))
            {
                var noRoom = limitedStore.TryBegin(Guid.NewGuid().ToString("N"), Stopwatch.GetTimestamp(), DateTimeOffset.UtcNow)!;
                await limitedStore.CompleteAsync(noRoom, ExecutionTimelineState.Succeeded);
                Check((await limitedStore.LoadAsync(noRoom.MessageId)).Snapshot!.PersistenceFailed && new FileInfo(retained).Length == 4096,
                    "full storage reports a visible failure and never deletes existing records");
            }

            if (!OperatingSystem.IsWindows())
            {
                var confined = Path.Combine(root, "app-data");
                var outside = Path.Combine(root, "outside");
                Directory.CreateDirectory(confined);
                Directory.CreateDirectory(outside);
                Directory.CreateSymbolicLink(Path.Combine(confined, "ExecutionTimelines"), outside);
                using var guarded = new ExecutionTimelineService(new TestConfigService(failureConfig), new TestPlatformPathService(confined), Log.Logger);
                var guardedTrace = guarded.TryBegin(Guid.NewGuid().ToString("N"), Stopwatch.GetTimestamp(), DateTimeOffset.UtcNow)!;
                await guarded.CompleteAsync(guardedTrace, ExecutionTimelineState.Succeeded);
                Check((await guarded.LoadAsync(guardedTrace.MessageId)).Snapshot!.PersistenceFailed && !Directory.EnumerateFiles(outside).Any(),
                    "trace output cannot follow a storage symlink outside app data");
            }
        }
        finally
        {
            // This test owns only its newly-created GUID directory, not an application data tree.
            Directory.Delete(root, recursive: true);
        }
    }
}
