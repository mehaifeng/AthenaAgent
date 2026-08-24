using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.Browser;

public class BrowserAgentService : IBrowserAgentService
{
    private const int MaxActionsPerStep = 5;
    private const int MaxConsecutiveFailures = 3;
    private const int OpenLoopWindow = 6;

    // 原地打转的两道线。动作本身"成功"却什么也没改变时，连续失败计数永远是 0，步数上限
    // 是唯一的刹车——实测一次 12306 选站任务就这样烧掉 25 步 / 10 分钟，期间同一张标注图
    // 逐字节重复出现了三次。第 3 次重复时把这件事明确写给模型（它自己看不出来），
    // 第 6 次仍在同一状态就直接收尾，把"卡在哪、试过什么"交回给调用方，而不是继续烧步数。
    private const int StuckStateNoticeThreshold = 3;
    private const int StuckStateAbortThreshold = 6;

    private readonly IHeadlessBrowserService _browserService;
    private readonly IBrowserVisionService _browserVisionService;
    private readonly IConfigService _configService;
    private readonly ILogger _logger;
    private readonly BrowserActionRegistry _actionRegistry = new();

    public BrowserAgentService(
        IHeadlessBrowserService browserService,
        IBrowserVisionService browserVisionService,
        IConfigService configService,
        ILogger logger)
    {
        _browserService = browserService;
        _browserVisionService = browserVisionService;
        _configService = configService;
        _logger = logger.ForContext<BrowserAgentService>();
    }

    public async Task<BrowserTaskResult> RunTaskAsync(BrowserTaskRequest request, CancellationToken cancellationToken = default)
    {
        if (request == null)
        {
            return Failure("Browser task request is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Instruction))
        {
            return Failure("Browser task instruction is required.");
        }

        var config = _configService.Load();
        _logger.Information(
            "BrowserAgent task started: Instruction={Preview}, MaxSteps={MaxSteps}, SomMaxElements={SomMaxElements}",
            Trim(request.Instruction, 200), ResolveMaxSteps(request, config), ResolveSomMaxElements(request, config));

        if (!config.BrowserEnabled)
        {
            _logger.Warning("BrowserAgent rejected: Browser is disabled in settings");
            return Failure("Browser is disabled in settings.");
        }

        var runtimeStatus = await _browserService.GetRuntimeStatusAsync(cancellationToken);
        if (!runtimeStatus.IsReady)
        {
            _logger.Warning(
                "BrowserAgent rejected: Runtime not ready, State={State}, Message={Message}",
                runtimeStatus.State, runtimeStatus.Message);
            return Failure(runtimeStatus.Details == null
                ? runtimeStatus.Message
                : $"{runtimeStatus.Message} {runtimeStatus.Details}");
        }

        BrowserSession? session = null;
        var history = new List<BrowserActionResult>();
        var evidence = new List<string>();
        BrowserStateSummary? finalState = null;
        var completionStatus = BrowserTaskCompletionStatus.Unknown;
        var longTermMemory = new StringBuilder();
        var readState = new StringBuilder();
        var consecutiveFailures = 0;
        var openLoopRecoveryAttempted = false;
        var reachedMaxSteps = false;
        var stateVisits = new Dictionary<string, int>(StringComparer.Ordinal);

        try
        {
            session = await BrowserSession.CreateAsync(_browserService, CreateSessionOptions(config, request), _logger, cancellationToken);
            await session.StartAsync(cancellationToken);
            _logger.Information("BrowserAgent session created: SessionId={SessionId}", session.SessionId);

            var initialActions = BuildInitialActions(request);
            if (initialActions.Count > 0)
            {
                foreach (var action in initialActions)
                {
                    var result = await ExecuteSingleActionAsync(session, action, cancellationToken);
                    RecordResult(history, evidence, longTermMemory, readState, result);
                    if (!result.Success || result.IsDone)
                    {
                        break;
                    }
                }
            }

            var initialDone = history.FirstOrDefault(result => result.IsDone);
            if (initialDone != null)
            {
                completionStatus = initialDone.CompletionSuccess == true
                    ? ResolveCompletedStatus(history)
                    : BrowserTaskCompletionStatus.Failed;
            }
            else if (history.Any(result => result.IsDone && result.CompletionSuccess != true))
            {
                completionStatus = BrowserTaskCompletionStatus.Failed;
            }
            else
            {
                var maxSteps = ResolveMaxSteps(request, config);
                for (var step = 1; step <= maxSteps; step++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _logger.Debug(
                        "BrowserAgent step start: Step={Step}/{Max}, ConsecutiveFailures={Fails}",
                        step, maxSteps, consecutiveFailures);
                    var useVision = config.BrowserObservationMode != BrowserObservationMode.DomOnly;
                    finalState = await session.GetStateAsync(useVision, cancellationToken);

                    var signature = ComputeStateSignature(finalState);
                    stateVisits.TryGetValue(signature, out var previousVisits);
                    var stateVisitCount = previousVisits + 1;
                    stateVisits[signature] = stateVisitCount;

                    if (stateVisitCount >= StuckStateAbortThreshold)
                    {
                        _logger.Warning(
                            "BrowserAgent stuck-state abort: Step={Step}, IdenticalObservations={Count}",
                            step, stateVisitCount);
                        var stuckFailure = BrowserActionFailure(
                            BrowserActionType.Finish,
                            session.SessionId,
                            $"Browser task stopped: the page state repeated identically {stateVisitCount} times, so the actions taken were changing nothing. "
                            + "The remaining steps would have repeated the same loop.");
                        stuckFailure.IsDone = true;
                        stuckFailure.CompletionSuccess = false;
                        stuckFailure.IsRecoverableFailure = false;
                        RecordResult(history, evidence, longTermMemory, readState, stuckFailure);
                        completionStatus = BrowserTaskCompletionStatus.Failed;
                        break;
                    }

                    var stepInfo = new BrowserAgentStepInfo
                    {
                        StepNumber = step,
                        MaxSteps = maxSteps,
                        ConsecutiveFailures = consecutiveFailures,
                        LongTermMemory = longTermMemory.ToString(),
                        ReadState = readState.ToString(),
                        UseVision = useVision,
                        RepeatedStateCount = stateVisitCount >= StuckStateNoticeThreshold ? stateVisitCount : 0
                    };

                    var modelOutput = await _browserVisionService.DecideNextActionsAsync(
                        request,
                        finalState,
                        stepInfo,
                        history,
                        _actionRegistry.Definitions,
                        cancellationToken);

                    AppendMemory(longTermMemory, modelOutput.Memory);
                    var actions = modelOutput.Action
                        .Where(action => !string.IsNullOrWhiteSpace(action.Name))
                        .Take(MaxActionsPerStep)
                        .ToList();

                    if (actions.Count == 0)
                    {
                        actions.Add(CreateDoneAction("Browser model returned no actions.", success: false));
                    }

                    var stepResults = await ExecuteActionsAsync(session, actions, cancellationToken);
                    foreach (var result in stepResults)
                    {
                        RecordResult(history, evidence, longTermMemory, readState, result);
                    }

                    var done = stepResults.FirstOrDefault(result => result.IsDone);
                    if (done != null)
                    {
                        completionStatus = done.CompletionSuccess == true
                            ? ResolveCompletedStatus(history)
                            : BrowserTaskCompletionStatus.Failed;
                        break;
                    }

                    if (LooksLikeRepeatedOpenLoop(history))
                    {
                        _logger.Warning(
                            "BrowserAgent open-loop detected: Step={Step}, HistoryCount={HistoryCount}",
                            step, history.Count);
                        if (openLoopRecoveryAttempted)
                        {
                            var loopFailure = BrowserActionFailure(BrowserActionType.Finish, session.SessionId, "Browser task stopped due to repeated new-tab opening without progress.");
                            loopFailure.IsDone = true;
                            loopFailure.CompletionSuccess = false;
                            loopFailure.IsRecoverableFailure = false;
                            RecordResult(history, evidence, longTermMemory, readState, loopFailure);
                            completionStatus = BrowserTaskCompletionStatus.Failed;
                            break;
                        }

                        openLoopRecoveryAttempted = true;
                        var recovered = await TryRecoverFromOpenLoopAsync(session, useVision, history, evidence, longTermMemory, readState, cancellationToken);
                        if (!recovered)
                        {
                            var loopFailure = BrowserActionFailure(BrowserActionType.Finish, session.SessionId, "Browser task stopped after repeated new-tab opening and failed recovery.");
                            loopFailure.IsDone = true;
                            loopFailure.CompletionSuccess = false;
                            loopFailure.IsRecoverableFailure = false;
                            RecordResult(history, evidence, longTermMemory, readState, loopFailure);
                            completionStatus = BrowserTaskCompletionStatus.Failed;
                            break;
                        }
                    }

                    if (stepResults.Count == 1 && !stepResults[0].Success)
                    {
                        consecutiveFailures++;
                    }
                    else if (stepResults.Any(result => result.Success))
                    {
                        consecutiveFailures = 0;
                    }

                    if (consecutiveFailures >= MaxConsecutiveFailures)
                    {
                        _logger.Warning(
                            "BrowserAgent consecutive failures: Count={Count}, Threshold={Threshold}",
                            consecutiveFailures, MaxConsecutiveFailures);
                        var failure = BrowserActionFailure(BrowserActionType.Finish, session.SessionId, $"Browser task stopped after {MaxConsecutiveFailures} consecutive action failures.");
                        failure.IsDone = true;
                        failure.CompletionSuccess = false;
                        RecordResult(history, evidence, longTermMemory, readState, failure);
                        completionStatus = BrowserTaskCompletionStatus.Failed;
                        break;
                    }

                    if (step == maxSteps)
                    {
                        reachedMaxSteps = true;
                        _logger.Information("BrowserAgent reached MaxSteps: Max={Max}", maxSteps);
                    }
                }
            }

            finalState ??= session.CachedState;
            if (reachedMaxSteps && completionStatus == BrowserTaskCompletionStatus.Unknown)
            {
                completionStatus = BrowserTaskCompletionStatus.MaxStepsReached;
                var maxStepResult = BrowserActionFailure(BrowserActionType.Finish, session.SessionId, "Browser task reached the maximum step limit before done.");
                RecordResult(history, evidence, longTermMemory, readState, maxStepResult);
            }
            else if (completionStatus == BrowserTaskCompletionStatus.Unknown)
            {
                completionStatus = ResolveCompletionStatus(history);
            }

            var finalObservation = finalState?.Observation ?? history.LastOrDefault(item => item.Observation != null)?.Observation;
            _logger.Information(
                "BrowserAgent task completed: Status={Status}, Success={Success}, Actions={Actions}, FinalUrl={Url}",
                completionStatus,
                completionStatus is BrowserTaskCompletionStatus.Completed or BrowserTaskCompletionStatus.CompletedWithRecoverableFailures,
                history.Count,
                finalObservation?.Url);
            return new BrowserTaskResult
            {
                Success = completionStatus is BrowserTaskCompletionStatus.Completed or BrowserTaskCompletionStatus.CompletedWithRecoverableFailures,
                Summary = BuildSummary(request, history, finalState, completionStatus),
                FinalUrl = finalState?.Url ?? history.LastOrDefault(item => !string.IsNullOrWhiteSpace(item.Url))?.Url ?? session.CurrentUrl,
                Evidence = evidence.TakeLast(24).ToList(),
                ActionsTakenCount = history.Count,
                RequiresUserInput = history.Any(item => item.RequiresUserConfirmation),
                SessionId = session.SessionId,
                FinalObservation = finalObservation,
                ActionHistory = history,
                CompletionStatus = completionStatus,
                Error = completionStatus is BrowserTaskCompletionStatus.Failed or BrowserTaskCompletionStatus.MaxStepsReached
                    ? history.LastOrDefault(item => !item.Success)?.Error ?? history.LastOrDefault(item => !item.Success)?.Message
                    : null
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Error(ex, "Browser agent task failed");
            return new BrowserTaskResult
            {
                Success = false,
                Summary = "Browser task failed.",
                Error = ex.Message,
                SessionId = session?.SessionId,
                Evidence = evidence.TakeLast(24).ToList(),
                ActionsTakenCount = history.Count,
                FinalObservation = finalState?.Observation,
                ActionHistory = history,
                CompletionStatus = BrowserTaskCompletionStatus.Failed,
            };
        }
        finally
        {
            if (session != null && request.CloseSessionOnCompletion)
            {
                await session.StopAsync(CancellationToken.None);
            }
        }
    }

    private async Task<List<BrowserActionResult>> ExecuteActionsAsync(
        BrowserSession session,
        IReadOnlyList<BrowserAgentAction> actions,
        CancellationToken cancellationToken)
    {
        var results = new List<BrowserActionResult>();
        var totalActions = actions.Count;

        for (var index = 0; index < totalActions; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var action = actions[index];
            action.Name = BrowserActionRegistry.NormalizeName(action.Name);

            if (index > 0 && action.Name == "done")
            {
                break;
            }

            var preUrl = session.CurrentUrl;
            var preDomVersion = session.DomVersion;
            var result = await ExecuteSingleActionAsync(session, action, cancellationToken);
            var postUrl = result.Url ?? session.CurrentUrl;
            result.PageChanged = HasPageChanged(preUrl, postUrl, preDomVersion, session.DomVersion);
            results.Add(result);

            if (result.IsDone || !result.Success)
            {
                break;
            }

            if (!_actionRegistry.TryGet(action.Name, out var definition))
            {
                break;
            }

            if (definition.TerminatesSequence)
            {
                break;
            }

            if (index < totalActions - 1 && result.PageChanged)
            {
                break;
            }
        }

        return results;
    }

    private async Task<BrowserActionResult> ExecuteSingleActionAsync(
        BrowserSession session,
        BrowserAgentAction action,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _actionRegistry.ExecuteAsync(session, action, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warning(ex, "Browser action failed. SessionId={SessionId}, Action={Action}", session.SessionId, action.Name);
            return BrowserActionFailure(BrowserActionType.None, session.SessionId, $"Browser action `{action.Name}` failed: {ex.Message}", action.Name);
        }
    }

    private static List<BrowserAgentAction> BuildInitialActions(BrowserTaskRequest request)
    {
        var actions = new List<BrowserAgentAction>();
        if (!string.IsNullOrWhiteSpace(request.StartUrl))
        {
            actions.Add(CreateAction("navigate", ("url", request.StartUrl)));
        }
        else if (ExtractFirstUrl(request.Instruction) is { } url)
        {
            actions.Add(CreateAction("navigate", ("url", url)));
        }

        foreach (var plannedAction in request.PlannedActions)
        {
            actions.Add(ConvertPlannedAction(plannedAction));
        }

        return actions;
    }

    private static BrowserAgentAction ConvertPlannedAction(BrowserActionRequest request)
    {
        return request.Action switch
        {
            BrowserActionType.Navigate => CreateAction("navigate", ("url", request.Url ?? string.Empty)),
            BrowserActionType.Click => CreateAction("click", ("elementId", request.ElementId ?? string.Empty)),
            BrowserActionType.Type => CreateAction("input", ("elementId", request.ElementId ?? string.Empty), ("text", request.Text ?? string.Empty)),
            BrowserActionType.Upload => CreateAction("upload_file", ("elementId", request.ElementId ?? string.Empty), ("path", request.FilePath ?? string.Empty)),
            BrowserActionType.PressKey => CreateAction("send_keys", ("keys", request.Key ?? string.Empty)),
            BrowserActionType.Scroll => CreateAction("scroll", ("deltaX", request.DeltaX), ("deltaY", request.DeltaY)),
            BrowserActionType.Wait => CreateAction("wait", ("milliseconds", request.WaitMilliseconds)),
            BrowserActionType.ExtractText => CreateAction("extract"),
            BrowserActionType.Finish => CreateDoneAction(request.Reason ?? "Browser task marked as done.", !request.IsTerminalFailure),
            _ => CreateDoneAction($"Unsupported planned browser action: {request.Action}", success: false)
        };
    }

    private static BrowserAgentAction CreateAction(string name, params (string Name, object? Value)[] parameters)
    {
        var action = new BrowserAgentAction { Name = name };
        foreach (var parameter in parameters)
        {
            action.Parameters[parameter.Name] = JsonSerializer.SerializeToElement(parameter.Value);
        }

        return action;
    }

    private static BrowserAgentAction CreateDoneAction(string text, bool success) =>
        CreateAction("done", ("text", text), ("success", success));

    private static BrowserActionResult BrowserActionFailure(BrowserActionType action, string sessionId, string message, string? actionName = null) =>
        new()
        {
            Success = false,
            Action = action,
            ActionName = actionName,
            Message = message,
            Error = message,
            SessionId = sessionId
        };

    private static void RecordResult(
        List<BrowserActionResult> history,
        List<string> evidence,
        StringBuilder longTermMemory,
        StringBuilder readState,
        BrowserActionResult result)
    {
        history.Add(result);
        AddEvidence(evidence, result);
        AppendMemory(longTermMemory, result.LongTermMemory);

        if (!string.IsNullOrWhiteSpace(result.ExtractedContent))
        {
            if (result.IncludeExtractedContentOnlyOnce)
            {
                AppendMemory(readState, result.ExtractedContent);
            }
            else
            {
                AppendMemory(longTermMemory, Trim(result.ExtractedContent, 2000));
            }
        }
    }

    private static void AppendMemory(StringBuilder builder, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (builder.Length > 0)
        {
            builder.AppendLine();
        }

        builder.AppendLine(Trim(value.Trim(), 6000));
        if (builder.Length > 24000)
        {
            builder.Remove(0, builder.Length - 24000);
        }
    }

    private static void AddEvidence(List<string> evidence, BrowserActionResult result)
    {
        var action = result.ActionName ?? result.Action.ToString();
        var message = string.IsNullOrWhiteSpace(result.Message)
            ? result.ExtractedContent ?? result.ExtractedText
            : result.Message;
        if (!string.IsNullOrWhiteSpace(message))
        {
            evidence.Add($"{action}: {Trim(message, 500)}");
        }
    }

    private static bool LooksLikeRepeatedOpenLoop(IReadOnlyList<BrowserActionResult> history)
    {
        if (history.Count < 3)
        {
            return false;
        }

        var recent = history.TakeLast(OpenLoopWindow)
            .Where(item => item.Action is BrowserActionType.Click or BrowserActionType.PressKey)
            .ToList();
        if (recent.Count < 3)
        {
            return false;
        }

        var openEvents = recent
            .Where(item => item.Metadata.TryGetValue("openedTabId", out var openedTabId) && !string.IsNullOrWhiteSpace(openedTabId))
            .ToList();
        if (openEvents.Count < 2)
        {
            return false;
        }

        var repeatedTarget = openEvents
            .Where(item => !string.IsNullOrWhiteSpace(item.Effect?.ElementId))
            .GroupBy(item => item.Effect!.ElementId!, StringComparer.OrdinalIgnoreCase)
            .Any(group => group.Count() >= 2);
        if (!repeatedTarget)
        {
            return false;
        }

        var normalizedUrls = openEvents
            .Select(item => NormalizeUrl(item.Url))
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return normalizedUrls.Count <= 1;
    }

    private async Task<bool> TryRecoverFromOpenLoopAsync(
        BrowserSession session,
        bool useVision,
        List<BrowserActionResult> history,
        List<string> evidence,
        StringBuilder longTermMemory,
        StringBuilder readState,
        CancellationToken cancellationToken)
    {
        if (_browserService is not PlaywrightBrowserService playwrightService)
        {
            return false;
        }

        var tabs = playwrightService.GetTabsSnapshot(session.SessionId);
        var active = tabs.FirstOrDefault(tab => tab.IsActive);
        var candidate = tabs
            .Where(tab => !tab.IsActive)
            .OrderByDescending(tab => ParseTabOrdinal(tab.TabId))
            .FirstOrDefault();
        if (candidate == null)
        {
            return false;
        }

        var switched = await playwrightService.SwitchTabAsync(session.SessionId, candidate.TabId, cancellationToken);
        switched.LongTermMemory ??= $"Loop guard switched tab from `{active?.TabId ?? "unknown"}` to `{candidate.TabId}`.";
        switched.Metadata["loopGuardTriggered"] = "true";
        RecordResult(history, evidence, longTermMemory, readState, switched);
        if (!switched.Success)
        {
            return false;
        }

        var state = await session.GetStateAsync(useVision, cancellationToken);
        var recovered = new BrowserActionResult
        {
            Success = true,
            Action = BrowserActionType.Observe,
            ActionName = "observe",
            Message = $"Loop guard refreshed state on tab `{candidate.TabId}`.",
            SessionId = session.SessionId,
            Url = state.Url,
            Observation = state.Observation,
            Metadata = { ["loopGuardTriggered"] = "true", ["loopGuardRecovered"] = "true" },
            LongTermMemory = $"Loop guard refreshed state after repeated new-tab opening. Active tab: {candidate.TabId}."
        };
        RecordResult(history, evidence, longTermMemory, readState, recovered);
        return true;
    }

    private static BrowserTaskCompletionStatus ResolveCompletedStatus(IReadOnlyList<BrowserActionResult> history) =>
        history.Any(item => !item.Success && item.IsRecoverableFailure)
            ? BrowserTaskCompletionStatus.CompletedWithRecoverableFailures
            : BrowserTaskCompletionStatus.Completed;

    private static BrowserTaskCompletionStatus ResolveCompletionStatus(IReadOnlyList<BrowserActionResult> history)
    {
        var done = history.LastOrDefault(item => item.IsDone);
        if (done != null)
        {
            return done.CompletionSuccess == true ? ResolveCompletedStatus(history) : BrowserTaskCompletionStatus.Failed;
        }

        return history.Count > 0 && history.All(item => item.Success)
            ? BrowserTaskCompletionStatus.Completed
            : BrowserTaskCompletionStatus.Failed;
    }

    private static string BuildSummary(
        BrowserTaskRequest request,
        IReadOnlyList<BrowserActionResult> history,
        BrowserStateSummary? finalState,
        BrowserTaskCompletionStatus completionStatus)
    {
        var done = history.LastOrDefault(item => item.IsDone);
        if (!string.IsNullOrWhiteSpace(done?.ExtractedContent))
        {
            return done.ExtractedContent;
        }

        var extracted = history.LastOrDefault(item => !string.IsNullOrWhiteSpace(item.ExtractedContent))?.ExtractedContent
            ?? history.LastOrDefault(item => !string.IsNullOrWhiteSpace(item.ExtractedText))?.ExtractedText;
        if (!string.IsNullOrWhiteSpace(extracted) && completionStatus is BrowserTaskCompletionStatus.Completed or BrowserTaskCompletionStatus.CompletedWithRecoverableFailures)
        {
            return Trim(extracted, 2000);
        }

        var builder = new StringBuilder();
        builder.Append(completionStatus switch
        {
            BrowserTaskCompletionStatus.Completed => "Browser task completed.",
            BrowserTaskCompletionStatus.CompletedWithRecoverableFailures => "Browser task completed with recoverable failures.",
            BrowserTaskCompletionStatus.MaxStepsReached => "Browser task reached the maximum step limit.",
            BrowserTaskCompletionStatus.Failed => "Browser task failed.",
            _ => "Browser task finished."
        });

        if (!string.IsNullOrWhiteSpace(finalState?.Url))
        {
            builder.Append(' ').Append("Final URL: ").Append(finalState.Url);
        }

        var lastError = history.LastOrDefault(item => !item.Success)?.Error;
        if (!string.IsNullOrWhiteSpace(lastError))
        {
            builder.Append(' ').Append(Trim(lastError, 600));
        }

        return builder.ToString();
    }

    private static BrowserTaskResult Failure(string message) => new()
    {
        Success = false,
        Summary = message,
        Error = message,
        CompletionStatus = BrowserTaskCompletionStatus.Failed
    };

    private static bool HasPageChanged(string? preUrl, string? postUrl, int preDomVersion, int postDomVersion)
    {
        if (!string.Equals(preUrl ?? string.Empty, postUrl ?? string.Empty, StringComparison.Ordinal))
        {
            return true;
        }

        return postDomVersion != preDomVersion;
    }

    private static int ParseTabOrdinal(string? tabId)
    {
        if (string.IsNullOrWhiteSpace(tabId))
        {
            return -1;
        }

        return tabId.StartsWith("tab-", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(tabId[4..], out var number)
            ? number
            : -1;
    }

    private static string NormalizeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        var trimmed = url.Trim();
        return trimmed.EndsWith("/", StringComparison.Ordinal) ? trimmed[..^1] : trimmed;
    }

    /// <summary>
    /// 一次观察的状态指纹。首选带标注截图的字节：页面没变 → 元素框没变 → 标注图逐字节相同，
    /// 这正好是"模型眼里这一页有没有变过"。DomOnly 模式下没有截图，退回 URL + DOM 版本 +
    /// 各元素当前值——值必须参与，否则往输入框里填了字也会被判成同一个状态。
    /// </summary>
    private static string ComputeStateSignature(BrowserStateSummary state)
    {
        if (!string.IsNullOrEmpty(state.ScreenshotBase64))
        {
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(state.ScreenshotBase64)));
        }

        var builder = new StringBuilder(state.Url ?? string.Empty);
        builder.Append('|').Append(state.DomVersion);
        foreach (var element in state.Elements)
        {
            builder.Append('|').Append(element.StableKey).Append('=').Append(element.Value);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static int ResolveMaxSteps(BrowserTaskRequest request, AppConfig config) =>
        Math.Clamp(request.MaxSteps ?? config.BrowserMaxSteps, 1, 50);

    /// <summary>
    /// 标注上限允许被单次任务覆盖：页面密度差得很远，一个搜索页 40 个标记就够，
    /// 后台表格/长列表 150 个都嫌少，而这件事只有发起任务的模型看得见。
    /// 上界与 <see cref="AppConfigNormalizer.NormalizeBrowser"/> 保持一致。
    /// </summary>
    private static int ResolveSomMaxElements(BrowserTaskRequest request, AppConfig config) =>
        Math.Clamp(request.SomMaxElements ?? config.BrowserSomMaxElements, 10, 300);

    private static BrowserSessionOptions CreateSessionOptions(AppConfig config, BrowserTaskRequest request) =>
        new()
        {
            Headless = config.BrowserHeadless,
            PersistSession = config.BrowserPersistSession,
            DownloadEnabled = config.BrowserDownloadEnabled,
            ObservationMode = config.BrowserObservationMode,
            SomMaxElements = ResolveSomMaxElements(request, config),
            SomIncludeText = config.BrowserSomIncludeText,
            ScreenshotScale = config.BrowserScreenshotScale,
            OperationTimeoutSeconds = config.BrowserOperationTimeoutSeconds,
            SessionTtlMinutes = config.BrowserSessionTtlMinutes,
            Viewport = new BrowserViewport
            {
                Width = config.BrowserViewportWidth,
                Height = config.BrowserViewportHeight,
                DeviceScaleFactor = 1.0
            }
        };

    private static string? ExtractFirstUrl(string value)
    {
        var match = Regex.Match(value, @"https?://[^\s""'<>，。；、)）\]]+", RegexOptions.IgnoreCase);
        return match.Success ? match.Value : null;
    }

    private static string Trim(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "...";
}
