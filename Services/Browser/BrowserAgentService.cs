using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.Browser;

public class BrowserAgentService : IBrowserAgentService
{
    private readonly IHeadlessBrowserService _browserService;
    private readonly IBrowserVisionService _browserVisionService;
    private readonly IBrowserTaskPlanner _taskPlanner;
    private readonly IConfigService _configService;
    private readonly ILogger _logger;

    public BrowserAgentService(IHeadlessBrowserService browserService, IBrowserVisionService browserVisionService, IBrowserTaskPlanner taskPlanner, IConfigService configService, ILogger logger)
    {
        _browserService = browserService;
        _browserVisionService = browserVisionService;
        _taskPlanner = taskPlanner;
        _configService = configService;
        _logger = logger.ForContext<BrowserAgentService>();
    }

    public async Task<BrowserTaskResult> RunTaskAsync(BrowserTaskRequest request, CancellationToken cancellationToken = default)
    {
        if (request == null)
        {
            return BrowserTaskResultFailure("Browser task request is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Instruction))
        {
            return BrowserTaskResultFailure("Browser task instruction is required.");
        }

        var config = _configService.Load();
        if (!config.BrowserEnabled)
        {
            return BrowserTaskResultFailure("Browser is disabled in settings.");
        }

        var runtimeStatus = await _browserService.GetRuntimeStatusAsync(cancellationToken);
        if (!runtimeStatus.IsReady)
        {
            return BrowserTaskResultFailure(runtimeStatus.Details == null
                ? runtimeStatus.Message
                : $"{runtimeStatus.Message} {runtimeStatus.Details}");
        }

        var session = await _browserService.CreateSessionAsync(CreateSessionOptions(config), cancellationToken);
        var history = new List<BrowserActionResult>();
        var evidence = new List<string>();
        var goalResults = new List<BrowserGoalResult>();
        SomObservation? finalObservation = null;

        try
        {
            if (request.PlannedActions.Count > 0 || !config.BrowserUseVisionMode)
            {
                finalObservation = await RunPlannedActionsAsync(request, config, session.SessionId, history, evidence, cancellationToken);
            }
            else
            {
                finalObservation = await RunLedgerAsync(request, config, session.SessionId, history, evidence, goalResults, cancellationToken);
            }

            var finalUrl = history.LastOrDefault(h => !string.IsNullOrWhiteSpace(h.Url))?.Url ?? session.CurrentUrl;
            var blockingFailure = history.FirstOrDefault(h => !h.Success && !h.IsRecoverableFailure);
            var completionStatus = ResolveCompletionStatus(history, goalResults);
            return new BrowserTaskResult
            {
                Success = completionStatus is BrowserTaskCompletionStatus.Completed or BrowserTaskCompletionStatus.CompletedWithRecoverableFailures,
                Summary = BuildSummary(request, history, finalObservation, goalResults, completionStatus),
                FinalUrl = finalUrl,
                Evidence = evidence,
                ActionsTakenCount = history.Count,
                RequiresUserInput = history.Any(h => h.RequiresUserConfirmation),
                SessionId = session.SessionId,
                FinalObservation = finalObservation,
                ActionHistory = history,
                CompletionStatus = completionStatus,
                GoalResults = goalResults,
                Error = blockingFailure?.Message
            };
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Browser agent task failed");
            return new BrowserTaskResult
            {
                Success = false,
                Summary = "Browser task failed.",
                Error = ex.Message,
                SessionId = session.SessionId,
                Evidence = evidence,
                ActionsTakenCount = history.Count,
                FinalObservation = finalObservation,
                ActionHistory = history,
                CompletionStatus = BrowserTaskCompletionStatus.Failed,
                GoalResults = goalResults
            };
        }
        finally
        {
            if (request.CloseSessionOnCompletion)
            {
                await _browserService.CloseSessionAsync(session.SessionId, cancellationToken);
            }
        }
    }

    private async Task<SomObservation?> RunPlannedActionsAsync(
        BrowserTaskRequest request,
        AppConfig config,
        string sessionId,
        List<BrowserActionResult> history,
        List<string> evidence,
        CancellationToken cancellationToken)
    {
        SomObservation? finalObservation = null;
        var maxSteps = ResolveMaxSteps(request, config);
        var plannedActions = BuildActionPlan(request);

        foreach (var action in plannedActions.Take(maxSteps))
        {
            cancellationToken.ThrowIfCancellationRequested();
            action.SessionId = sessionId;

            var result = await ExecuteActionAsync(action, cancellationToken);
            MarkRecoverableFailureIfApplicable(request, result);
            if (result.IsRecoverableFailure)
            {
                _logger.Information("Browser action failure treated as recoverable. SessionId={SessionId}, Action={Action}, Message={Message}", sessionId, result.Action, result.Message);
            }
            history.Add(result);
            AddEvidence(evidence, result);

            if (result.Observation != null)
            {
                finalObservation = result.Observation;
            }

            if ((!result.Success && !result.IsRecoverableFailure) || action.Action == BrowserActionType.Finish || result.RequiresUserConfirmation)
            {
                break;
            }
        }

        return finalObservation;
    }

    private async Task<SomObservation?> RunLedgerAsync(
        BrowserTaskRequest request,
        AppConfig config,
        string sessionId,
        List<BrowserActionResult> history,
        List<string> evidence,
        List<BrowserGoalResult> goalResults,
        CancellationToken cancellationToken)
    {
        SomObservation? finalObservation = null;
        var maxSteps = ResolveMaxSteps(request, config);
        var plan = await _taskPlanner.CreatePlanAsync(request, cancellationToken);
        if (plan.Goals.Count == 0)
        {
            var failure = new BrowserGoalResult
            {
                GoalId = Guid.NewGuid().ToString("N"),
                Index = 1,
                Type = BrowserTaskGoalType.Verify,
                Label = "Create browser task plan",
                Status = BrowserTaskGoalStatus.Failed,
                Message = "Browser task planner did not return any goals."
            };
            goalResults.Add(failure);
            evidence.Add(failure.Message);
            return finalObservation;
        }

        evidence.Add($"Browser task plan: {plan.Summary ?? "planned"} Goals={plan.Goals.Count}.");
        _logger.Information("Browser ledger execution started. SessionId={SessionId}, Goals={GoalCount}, Summary={Summary}", sessionId, plan.Goals.Count, plan.Summary ?? "(none)");

        foreach (var goal in plan.Goals)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (history.Count >= maxSteps)
            {
                var maxStepFailure = BrowserActionFailure(BrowserActionType.Finish, sessionId, $"Browser task reached the maximum step limit ({maxSteps}) before completing goal {goal.Index}: {goal.Label}");
                history.Add(maxStepFailure);
                AddEvidence(evidence, maxStepFailure);
                break;
            }

            var goalResult = await ExecuteGoalAsync(request, goal, sessionId, history, evidence, maxSteps, cancellationToken);
            goalResults.Add(goalResult);
            goal.Status = goalResult.Status;
            goal.Message = goalResult.Message;
            finalObservation = history.LastOrDefault(h => h.Observation != null)?.Observation ?? finalObservation;

            if (goal.Type == BrowserTaskGoalType.Navigate && goalResult.Status == BrowserTaskGoalStatus.Failed)
            {
                break;
            }
        }

        return finalObservation;
    }

    private async Task<BrowserGoalResult> ExecuteGoalAsync(
        BrowserTaskRequest request,
        BrowserTaskGoal goal,
        string sessionId,
        List<BrowserActionResult> history,
        List<string> evidence,
        int maxSteps,
        CancellationToken cancellationToken)
    {
        var result = new BrowserGoalResult
        {
            GoalId = goal.GoalId,
            Index = goal.Index,
            Type = goal.Type,
            Label = goal.Label,
            Value = goal.Value,
            Status = BrowserTaskGoalStatus.Running
        };

        _logger.Information("Browser goal started. SessionId={SessionId}, Goal={GoalIndex}, Type={GoalType}, Label={GoalLabel}", sessionId, goal.Index, goal.Type, goal.Label);

        if (goal.Type == BrowserTaskGoalType.Navigate)
        {
            var url = goal.Url ?? ExtractFirstUrl(goal.Label) ?? ExtractFirstUrl(request.Instruction);
            if (string.IsNullOrWhiteSpace(url))
            {
                return CompleteGoal(result, BrowserTaskGoalStatus.Failed, "Navigate goal has no URL.");
            }

            var navigate = await ExecuteActionAsync(new BrowserActionRequest
            {
                SessionId = sessionId,
                Action = BrowserActionType.Navigate,
                Url = url
            }, cancellationToken);
            RecordAction(history, evidence, navigate);
            result.Attempts = 1;
            result.Evidence.Add(navigate.Message);
            return CompleteGoal(result, navigate.Success ? BrowserTaskGoalStatus.Succeeded : BrowserTaskGoalStatus.Failed, navigate.Message);
        }

        if (goal.Type is BrowserTaskGoalType.Extract or BrowserTaskGoalType.Verify)
        {
            var observe = await ExecuteActionAsync(new BrowserActionRequest
            {
                SessionId = sessionId,
                Action = BrowserActionType.Observe
            }, cancellationToken);
            RecordAction(history, evidence, observe);
            result.Attempts = 1;
            result.Evidence.Add(observe.Message);
            if (!observe.Success || observe.Observation == null)
            {
                return CompleteGoal(result, BrowserTaskGoalStatus.Failed, observe.Message);
            }

            var extract = await ExecuteActionAsync(new BrowserActionRequest
            {
                SessionId = sessionId,
                Action = BrowserActionType.ExtractText
            }, cancellationToken);
            RecordAction(history, evidence, extract);
            result.Evidence.Add(extract.Message);
            if (!string.IsNullOrWhiteSpace(extract.ExtractedText))
            {
                result.Evidence.Add(TrimForEvidence(extract.ExtractedText));
            }

            return CompleteGoal(result, extract.Success ? BrowserTaskGoalStatus.Succeeded : BrowserTaskGoalStatus.Failed, extract.Message);
        }

        var attempts = Math.Clamp(goal.MaxAttempts, 1, 3);
        for (var attempt = 1; attempt <= attempts && history.Count < maxSteps; attempt++)
        {
            result.Attempts = attempt;
            var observe = await ExecuteActionAsync(new BrowserActionRequest
            {
                SessionId = sessionId,
                Action = BrowserActionType.Observe
            }, cancellationToken);
            RecordAction(history, evidence, observe);
            if (!observe.Success || observe.Observation == null)
            {
                result.Evidence.Add(observe.Message);
                return CompleteGoal(result, BrowserTaskGoalStatus.Failed, observe.Message);
            }

            var target = FindBestGoalElement(goal, observe.Observation);
            BrowserActionResult actionResult;
            if (target == null)
            {
                actionResult = await TryVisionFallbackActionAsync(request, goal, sessionId, observe.Observation, history, cancellationToken);
                if (actionResult.Action == BrowserActionType.None)
                {
                    result.Evidence.Add(actionResult.Message);
                    if (attempt < attempts)
                    {
                        var scroll = await ExecuteActionAsync(new BrowserActionRequest
                        {
                            SessionId = sessionId,
                            Action = BrowserActionType.Scroll,
                            DeltaY = 500
                        }, cancellationToken);
                        RecordAction(history, evidence, scroll);
                        continue;
                    }

                    return CompleteGoal(result, goal.Optional ? BrowserTaskGoalStatus.Skipped : BrowserTaskGoalStatus.Failed, actionResult.Message);
                }
            }
            else
            {
                actionResult = await ExecuteGoalActionAsync(goal, target, sessionId, cancellationToken);
            }

            RecordAction(history, evidence, actionResult);
            result.ElementId = actionResult.Effect?.ElementId ?? target?.ElementId;
            result.Evidence.Add($"{actionResult.Action}: {actionResult.Message}");

            if (IsGoalActionVerified(goal, actionResult))
            {
                return CompleteGoal(result, BrowserTaskGoalStatus.Succeeded, $"Goal completed: {goal.Label}");
            }

            if (!actionResult.Success && attempt < attempts)
            {
                continue;
            }
        }

        return CompleteGoal(result, goal.Optional ? BrowserTaskGoalStatus.Skipped : BrowserTaskGoalStatus.Failed, $"Goal was not completed: {goal.Label}");
    }

    private async Task<BrowserActionResult> ExecuteActionAsync(BrowserActionRequest action, CancellationToken cancellationToken)
    {
        return action.Action switch
        {
            BrowserActionType.Navigate => await _browserService.NavigateAsync(action.SessionId, Require(action.Url, "Navigation URL is required."), cancellationToken),
            BrowserActionType.Observe => new BrowserActionResult
            {
                Success = true,
                Action = BrowserActionType.Observe,
                Message = "Observation completed.",
                SessionId = action.SessionId,
                Observation = await _browserService.ObserveAsync(action.SessionId, cancellationToken)
            },
            BrowserActionType.Click => await _browserService.ClickAsync(action.SessionId, Require(action.ElementId, "Element ID is required."), cancellationToken),
            BrowserActionType.Type => await _browserService.TypeAsync(action.SessionId, Require(action.ElementId, "Element ID is required."), action.Text ?? string.Empty, cancellationToken),
            BrowserActionType.Upload => await _browserService.UploadAsync(action.SessionId, Require(action.ElementId, "Element ID is required."), Require(action.FilePath, "Upload file path is required."), cancellationToken),
            BrowserActionType.PressKey => await _browserService.PressKeyAsync(action.SessionId, Require(action.Key, "Key is required."), cancellationToken),
            BrowserActionType.Scroll => await _browserService.ScrollAsync(action.SessionId, action.DeltaX, action.DeltaY, cancellationToken),
            BrowserActionType.Wait => await _browserService.WaitAsync(action.SessionId, action.WaitMilliseconds, cancellationToken),
            BrowserActionType.ExtractText => await _browserService.ExtractTextAsync(action.SessionId, cancellationToken),
            BrowserActionType.Close => await CloseActionAsync(action.SessionId, cancellationToken),
            BrowserActionType.Finish => FinishAction(action.SessionId, action.Reason, !action.IsTerminalFailure),
            _ => new BrowserActionResult
            {
                Success = false,
                Action = action.Action,
                Message = $"Unsupported browser action: {action.Action}",
                SessionId = action.SessionId
            }
        };
    }

    private async Task<BrowserActionResult> ExecuteGoalActionAsync(BrowserTaskGoal goal, SomElement target, string sessionId, CancellationToken cancellationToken)
    {
        if (goal.Type == BrowserTaskGoalType.Upload && string.IsNullOrWhiteSpace(goal.Value))
        {
            return BrowserActionFailure(BrowserActionType.Upload, sessionId, "Upload goal has no local file path.");
        }

        return goal.Type switch
        {
            BrowserTaskGoalType.Fill => await _browserService.TypeAsync(sessionId, target.ElementId, ResolveGoalInputValue(goal, target), cancellationToken),
            BrowserTaskGoalType.Select => await _browserService.SelectAsync(sessionId, target.ElementId, goal.Value ?? string.Empty, cancellationToken),
            BrowserTaskGoalType.Upload => await _browserService.UploadAsync(sessionId, target.ElementId, goal.Value!, cancellationToken),
            BrowserTaskGoalType.SetChecked => await _browserService.SetCheckedAsync(sessionId, target.ElementId, ResolveCheckedValue(goal), cancellationToken),
            BrowserTaskGoalType.Click or BrowserTaskGoalType.Submit => await _browserService.ClickAsync(sessionId, target.ElementId, cancellationToken),
            _ => BrowserActionFailure(BrowserActionType.None, sessionId, $"Unsupported goal action: {goal.Type}")
        };
    }

    private async Task<BrowserActionResult> TryVisionFallbackActionAsync(
        BrowserTaskRequest request,
        BrowserTaskGoal goal,
        string sessionId,
        SomObservation observation,
        IReadOnlyList<BrowserActionResult> history,
        CancellationToken cancellationToken)
    {
        if (goal.Type == BrowserTaskGoalType.Upload && string.IsNullOrWhiteSpace(goal.Value))
        {
            return BrowserActionFailure(BrowserActionType.Upload, sessionId, "Upload goal has no local file path.");
        }

        var subTask = new BrowserTaskRequest
        {
            Instruction = $"""
                Execute only this browser subgoal. Do not finish the overall task.
                Original task: {request.Instruction}
                Subgoal type: {goal.Type}
                Subgoal target label: {goal.Label}
                Subgoal value: {goal.Value ?? goal.Checked?.ToString() ?? "(none)"}
                Return one concrete action to advance this subgoal. If the target is not visible, scroll or wait instead of finish.
                """,
            StartUrl = request.StartUrl,
            MaxSteps = 1,
            CloseSessionOnCompletion = false
        };

        var action = await _browserVisionService.DecideNextActionAsync(subTask, observation, history, cancellationToken);
        action.SessionId = sessionId;
        if (action.Action == BrowserActionType.Finish)
        {
            return BrowserActionFailure(BrowserActionType.None, sessionId, $"No DOM or vision target found for goal: {goal.Label}");
        }

        if (goal.Type == BrowserTaskGoalType.Upload && string.IsNullOrWhiteSpace(action.FilePath))
        {
            action.FilePath = goal.Value;
        }

        if (goal.Type is BrowserTaskGoalType.Fill or BrowserTaskGoalType.Select && string.IsNullOrWhiteSpace(action.Text))
        {
            action.Text = ResolveGoalInputValue(goal, null);
        }

        try
        {
            return await ExecuteActionAsync(action, cancellationToken);
        }
        catch (ArgumentException ex)
        {
            return BrowserActionFailure(action.Action, sessionId, ex.Message);
        }
    }

    private static bool IsGoalActionVerified(BrowserTaskGoal goal, BrowserActionResult actionResult)
    {
        if (!actionResult.Success)
        {
            return false;
        }

        return goal.Type switch
        {
            BrowserTaskGoalType.Fill or BrowserTaskGoalType.Select or BrowserTaskGoalType.Upload or BrowserTaskGoalType.SetChecked =>
                actionResult.Effect == null || actionResult.Effect.MatchesRequestedValue || actionResult.Effect.Skipped,
            BrowserTaskGoalType.Click or BrowserTaskGoalType.Submit => true,
            _ => actionResult.Success
        };
    }

    private static BrowserGoalResult CompleteGoal(BrowserGoalResult result, BrowserTaskGoalStatus status, string message)
    {
        result.Status = status;
        result.Message = message;
        return result;
    }

    private static void RecordAction(List<BrowserActionResult> history, List<string> evidence, BrowserActionResult result)
    {
        history.Add(result);
        AddEvidence(evidence, result);
    }

    private static BrowserActionResult BrowserActionFailure(BrowserActionType action, string sessionId, string message) =>
        new()
        {
            Success = false,
            Action = action,
            Message = message,
            SessionId = sessionId
        };

    private static SomElement? FindBestGoalElement(BrowserTaskGoal goal, SomObservation observation)
    {
        return observation.Elements
            .Where(e => e.IsVisible && e.IsEnabled && IsElementCompatibleWithGoal(goal, e))
            .Select(e => new { Element = e, Score = ScoreGoalElement(goal, e) })
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Element.BoundingBox.Y)
            .ThenBy(item => item.Element.BoundingBox.X)
            .FirstOrDefault()
            ?.Element;
    }

    private static bool IsElementCompatibleWithGoal(BrowserTaskGoal goal, SomElement element)
    {
        var inputType = element.InputType ?? string.Empty;
        var tag = element.TagName;
        var role = element.Role ?? string.Empty;
        return goal.Type switch
        {
            BrowserTaskGoalType.Fill => element.IsEditable ||
                inputType is "date" or "time" or "datetime-local" or "month" or "week" or "number" or "color" or "range",
            BrowserTaskGoalType.Select => inputType == "select" || string.Equals(tag, "select", StringComparison.OrdinalIgnoreCase),
            BrowserTaskGoalType.Upload => inputType == "file",
            BrowserTaskGoalType.SetChecked => inputType is "checkbox" or "radio" ||
                role.Equals("checkbox", StringComparison.OrdinalIgnoreCase) ||
                role.Equals("radio", StringComparison.OrdinalIgnoreCase),
            BrowserTaskGoalType.Submit => IsClickableElement(element) && ContainsAny(BuildElementSearchText(element), "submit", "提交"),
            BrowserTaskGoalType.Click => IsClickableElement(element),
            _ => false
        };
    }

    private static bool IsClickableElement(SomElement element) =>
        element.TagName.Equals("button", StringComparison.OrdinalIgnoreCase) ||
        element.TagName.Equals("a", StringComparison.OrdinalIgnoreCase) ||
        element.TagName.Equals("label", StringComparison.OrdinalIgnoreCase) ||
        !string.IsNullOrWhiteSpace(element.Role) ||
        !string.IsNullOrWhiteSpace(element.Href) ||
        element.InputType is "button" or "submit" or "checkbox" or "radio" or "file";

    private static double ScoreGoalElement(BrowserTaskGoal goal, SomElement element)
    {
        var label = NormalizeForMatch(goal.Label);
        var value = NormalizeForMatch(goal.Value);
        var haystack = NormalizeForMatch(BuildElementSearchText(element));
        var score = 20d;

        if (!string.IsNullOrWhiteSpace(label))
        {
            if (haystack.Contains(label, StringComparison.OrdinalIgnoreCase))
            {
                score += 100;
            }

            foreach (var token in Tokenize(label))
            {
                if (haystack.Contains(token, StringComparison.OrdinalIgnoreCase))
                {
                    score += 18;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(value) && haystack.Contains(value, StringComparison.OrdinalIgnoreCase))
        {
            score += 30;
        }

        score += goal.Type switch
        {
            BrowserTaskGoalType.Fill when element.IsEditable => 60,
            BrowserTaskGoalType.Select when element.InputType == "select" => 90,
            BrowserTaskGoalType.Upload when element.InputType == "file" => 90,
            BrowserTaskGoalType.SetChecked when element.InputType is "checkbox" or "radio" => 80,
            BrowserTaskGoalType.Submit when ContainsAny(haystack, "submit", "提交") => 100,
            _ => 0
        };

        return score;
    }

    private static string BuildElementSearchText(SomElement element) =>
        string.Join(" ", new[]
        {
            element.Text,
            element.AriaLabel,
            element.Placeholder,
            element.Value,
            element.InputType,
            element.Role,
            element.TagName,
            element.StableKey,
            element.Selector
        }.Where(value => !string.IsNullOrWhiteSpace(value)));

    private static string ResolveGoalInputValue(BrowserTaskGoal goal, SomElement? target)
    {
        if (!string.IsNullOrWhiteSpace(goal.Value))
        {
            return goal.Value;
        }

        return (target?.InputType ?? string.Empty).ToLowerInvariant() switch
        {
            "color" => "#3366cc",
            "date" => DateTime.Today.ToString("yyyy-MM-dd"),
            "datetime-local" => DateTime.Today.ToString("yyyy-MM-ddTHH:mm"),
            "time" => "12:30",
            "month" => DateTime.Today.ToString("yyyy-MM"),
            "week" => $"{DateTime.Today:yyyy}-W01",
            "range" or "number" => "7",
            "password" => "AthenaPassword123!",
            "textarea" => "Athena textarea test line",
            _ => "Athena test"
        };
    }

    private static bool ResolveCheckedValue(BrowserTaskGoal goal)
    {
        if (goal.Checked.HasValue)
        {
            return goal.Checked.Value;
        }

        if (bool.TryParse(goal.Value, out var parsed))
        {
            return parsed;
        }

        var value = (goal.Value ?? string.Empty).Trim().ToLowerInvariant();
        return value is not ("false" or "unchecked" or "uncheck" or "off" or "0");
    }

    private static string NormalizeForMatch(string? value) =>
        Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim().ToLowerInvariant();

    private static IEnumerable<string> Tokenize(string value) =>
        Regex.Split(value, @"[^\p{L}\p{N}#:/._-]+")
            .Select(token => token.Trim().ToLowerInvariant())
            .Where(token => token.Length >= 2);

    private static void MarkRecoverableFailureIfApplicable(BrowserTaskRequest request, BrowserActionResult result)
    {
        if (result.Success || result.RequiresUserConfirmation || !LooksLikeComplexBrowserTask(request.Instruction))
        {
            return;
        }

        if (result.Action is BrowserActionType.Click or BrowserActionType.Type or BrowserActionType.Upload or BrowserActionType.PressKey)
        {
            result.IsRecoverableFailure = true;
            result.Message = $"Recoverable action failure; continue with remaining requested controls. {result.Message}";
        }
    }

    private static int ResolveMaxSteps(BrowserTaskRequest request, AppConfig config)
    {
        var maxSteps = request.MaxSteps ?? config.BrowserMaxSteps;
        if (request.MaxSteps == null && LooksLikeComplexBrowserTask(request.Instruction))
        {
            maxSteps = Math.Max(maxSteps, 40);
        }

        return Math.Clamp(maxSteps, 1, 50);
    }

    private static bool LooksLikeComplexBrowserTask(string instruction) =>
        CountInstructionListItems(instruction) >= 3 ||
        CountRequestedInteractionGroups(instruction) >= 4 ||
        ContainsAny(instruction, "various controls", "all controls", "main controls", "主要控件", "各种控件", "多种控件", "表单控件");

    private static int CountRequestedInteractionGroups(string instruction)
    {
        var groups = new[]
        {
            new[] { "text input", "text field", "文本输入", "文本框" },
            new[] { "password", "密码" },
            new[] { "textarea", "text area", "文本域", "多行文本" },
            new[] { "dropdown select", "selectbox", "select box", "select", "下拉框", "下拉列表" },
            new[] { "datalist", "data list", "自动完成" },
            new[] { "file input", "upload", "上传", "文件选择" },
            new[] { "checkbox", "check box", "复选框", "勾选" },
            new[] { "radio", "单选" },
            new[] { "color picker", "color", "颜色" },
            new[] { "date picker", "date", "日期" },
            new[] { "range", "slider", "滑块" },
            new[] { "submit", "提交" }
        };

        return groups.Count(group => ContainsAny(instruction, group));
    }

    private static int CountInstructionListItems(string instruction) =>
        Regex.Matches(instruction, @"(?m)^\s*(?:\d+[\.\、\)]|[-*])\s+").Count;

    private static bool ContainsAny(string value, params string[] terms) =>
        terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static BrowserSessionOptions CreateSessionOptions(AppConfig config) =>
        new()
        {
            Headless = config.BrowserHeadless,
            PersistSession = config.BrowserPersistSession,
            DownloadEnabled = config.BrowserDownloadEnabled,
            SomEnabled = config.BrowserSomEnabled,
            SomMaxElements = config.BrowserSomMaxElements,
            SomIncludeText = config.BrowserSomIncludeText,
            ScreenshotScale = config.BrowserScreenshotScale,
            ImageQuality = config.BrowserImageQuality,
            OperationTimeoutSeconds = config.BrowserOperationTimeoutSeconds,
            SessionTtlMinutes = config.BrowserSessionTtlMinutes,
            Viewport = new BrowserViewport
            {
                Width = config.BrowserViewportWidth,
                Height = config.BrowserViewportHeight
            }
        };

    private static List<BrowserActionRequest> BuildActionPlan(BrowserTaskRequest request)
    {
        if (request.PlannedActions.Count > 0)
        {
            return request.PlannedActions;
        }

        var actions = new List<BrowserActionRequest>();
        var initialUrl = string.IsNullOrWhiteSpace(request.StartUrl)
            ? ExtractFirstUrl(request.Instruction)
            : request.StartUrl;

        if (!string.IsNullOrWhiteSpace(initialUrl))
        {
            actions.Add(new BrowserActionRequest { Action = BrowserActionType.Navigate, Url = initialUrl });
        }

        actions.Add(new BrowserActionRequest { Action = BrowserActionType.Observe });
        actions.Add(new BrowserActionRequest { Action = BrowserActionType.ExtractText });
        actions.Add(new BrowserActionRequest { Action = BrowserActionType.Finish });

        return actions;
    }

    private static void AddEvidence(List<string> evidence, BrowserActionResult result)
    {
        switch (result.Action)
        {
            case BrowserActionType.Observe when result.Observation != null:
                evidence.Add($"Observed `{result.Observation.Title ?? "Untitled"}` at `{result.Observation.Url}` with {result.Observation.Elements.Count} marked elements.");
                break;
            case BrowserActionType.ExtractText when !string.IsNullOrWhiteSpace(result.ExtractedText):
                evidence.Add($"Extracted text preview: {TrimForEvidence(result.ExtractedText)}");
                break;
            default:
                if (result.Effect != null)
                {
                    evidence.Add($"{result.Action}: {result.Message} Target={result.Effect.ElementId ?? result.Effect.TargetStableKey ?? "(none)"}, Changed={result.Effect.Changed}, Skipped={result.Effect.Skipped}, MatchesRequestedValue={result.Effect.MatchesRequestedValue}.");
                }
                else
                {
                    evidence.Add($"{result.Action}: {result.Message}");
                }
                break;
        }
    }

    private static BrowserTaskCompletionStatus ResolveCompletionStatus(IReadOnlyList<BrowserActionResult> history, IReadOnlyList<BrowserGoalResult> goalResults)
    {
        if (history.Any(h => !h.Success && !h.IsRecoverableFailure && h.Message.Contains("maximum step limit", StringComparison.OrdinalIgnoreCase)))
        {
            return BrowserTaskCompletionStatus.MaxStepsReached;
        }

        if (goalResults.Any(g => g.Type == BrowserTaskGoalType.Navigate && g.Status == BrowserTaskGoalStatus.Failed))
        {
            return BrowserTaskCompletionStatus.Failed;
        }

        if (goalResults.Count > 0)
        {
            return goalResults.Any(g => g.Status == BrowserTaskGoalStatus.Failed)
                ? BrowserTaskCompletionStatus.CompletedWithRecoverableFailures
                : BrowserTaskCompletionStatus.Completed;
        }

        return history.Count > 0 && history.All(h => h.Success || h.IsRecoverableFailure)
            ? BrowserTaskCompletionStatus.Completed
            : BrowserTaskCompletionStatus.Failed;
    }

    private static string BuildSummary(
        BrowserTaskRequest request,
        IReadOnlyList<BrowserActionResult> history,
        SomObservation? finalObservation,
        IReadOnlyList<BrowserGoalResult> goalResults,
        BrowserTaskCompletionStatus completionStatus)
    {
        var builder = new StringBuilder();
        builder.Append("Browser task executed in an isolated session.");
        builder.Append($" Status: {completionStatus}.");
        if (finalObservation != null)
        {
            builder.Append($" Final page: {finalObservation.Title ?? "Untitled"} ({finalObservation.Url}).");
            builder.Append($" Marked elements: {finalObservation.Elements.Count}.");
        }
        else if (!string.IsNullOrWhiteSpace(request.StartUrl))
        {
            builder.Append($" Start URL: {request.StartUrl}.");
        }

        if (history.Any(h => !h.Success && !h.IsRecoverableFailure))
        {
            builder.Append($" Stopped after failure: {history.First(h => !h.Success && !h.IsRecoverableFailure).Message}");
        }
        else if (history.Any(h => h.IsRecoverableFailure))
        {
            builder.Append($" Recoverable action failures: {history.Count(h => h.IsRecoverableFailure)}.");
        }

        if (goalResults.Count > 0)
        {
            builder.Append($" Goals: {goalResults.Count(g => g.Status == BrowserTaskGoalStatus.Succeeded)} succeeded");
            var failedCount = goalResults.Count(g => g.Status == BrowserTaskGoalStatus.Failed);
            if (failedCount > 0)
            {
                builder.Append($", {failedCount} failed");
            }

            builder.Append(".");
        }

        return builder.ToString();
    }

    private static string TrimForEvidence(string value)
    {
        var compact = value.Replace("\r", " ").Replace("\n", " ").Trim();
        return compact.Length <= 240 ? compact : compact[..240] + "...";
    }

    private static string Require(string? value, string message)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(message);
        }

        return value;
    }

    private static string? ExtractFirstUrl(string value)
    {
        var match = Regex.Match(value, @"https?://[^\s""'<>，。；、)）\]]+", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return null;
        }

        return Uri.TryCreate(match.Value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri.ToString()
            : null;
    }

    private async Task<BrowserActionResult> CloseActionAsync(string sessionId, CancellationToken cancellationToken)
    {
        await _browserService.CloseSessionAsync(sessionId, cancellationToken);
        return new BrowserActionResult
        {
            Success = true,
            Action = BrowserActionType.Close,
            Message = "Browser session closed.",
            SessionId = sessionId
        };
    }

    private static BrowserActionResult FinishAction(string sessionId, string? reason = null, bool success = true) =>
        new()
        {
            Success = success,
            Action = BrowserActionType.Finish,
            Message = string.IsNullOrWhiteSpace(reason) ? "Browser task finished." : $"Browser task finished: {reason}",
            SessionId = sessionId
        };

    private static BrowserTaskResult BrowserTaskResultFailure(string message) =>
        new()
        {
            Success = false,
            Summary = message,
            Error = message
        };

}
