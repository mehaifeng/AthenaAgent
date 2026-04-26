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
    private readonly IConfigService _configService;
    private readonly ILogger _logger;

    public BrowserAgentService(IHeadlessBrowserService browserService, IBrowserVisionService browserVisionService, IConfigService configService, ILogger logger)
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
        SomObservation? finalObservation = null;

        try
        {
            if (request.PlannedActions.Count > 0 || !config.BrowserUseVisionMode)
            {
                finalObservation = await RunPlannedActionsAsync(request, config, session.SessionId, history, evidence, cancellationToken);
            }
            else
            {
                finalObservation = await RunVisionLoopAsync(request, config, session.SessionId, history, evidence, cancellationToken);
            }

            var finalUrl = history.LastOrDefault(h => !string.IsNullOrWhiteSpace(h.Url))?.Url ?? session.CurrentUrl;
            return new BrowserTaskResult
            {
                Success = history.Count > 0 && history.All(h => h.Success),
                Summary = BuildSummary(request, history, finalObservation),
                FinalUrl = finalUrl,
                Evidence = evidence,
                ActionsTakenCount = history.Count,
                RequiresUserInput = history.Any(h => h.RequiresUserConfirmation),
                SessionId = session.SessionId,
                FinalObservation = finalObservation,
                ActionHistory = history,
                Error = history.FirstOrDefault(h => !h.Success)?.Message
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
                ActionHistory = history
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
        var maxSteps = Math.Clamp(request.MaxSteps ?? config.BrowserMaxSteps, 1, 50);
        var plannedActions = BuildActionPlan(request);

        foreach (var action in plannedActions.Take(maxSteps))
        {
            cancellationToken.ThrowIfCancellationRequested();
            action.SessionId = sessionId;

            var result = await ExecuteActionAsync(action, cancellationToken);
            history.Add(result);
            AddEvidence(evidence, result);

            if (result.Observation != null)
            {
                finalObservation = result.Observation;
            }

            if (!result.Success || action.Action == BrowserActionType.Finish || result.RequiresUserConfirmation)
            {
                break;
            }
        }

        return finalObservation;
    }

    private async Task<SomObservation?> RunVisionLoopAsync(
        BrowserTaskRequest request,
        AppConfig config,
        string sessionId,
        List<BrowserActionResult> history,
        List<string> evidence,
        CancellationToken cancellationToken)
    {
        SomObservation? finalObservation = null;
        var maxSteps = Math.Clamp(request.MaxSteps ?? config.BrowserMaxSteps, 1, 50);

        var initialUrl = string.IsNullOrWhiteSpace(request.StartUrl)
            ? ExtractFirstUrl(request.Instruction)
            : request.StartUrl;

        if (!string.IsNullOrWhiteSpace(initialUrl))
        {
            var navigate = await ExecuteActionAsync(new BrowserActionRequest
            {
                SessionId = sessionId,
                Action = BrowserActionType.Navigate,
                Url = initialUrl
            }, cancellationToken);
            history.Add(navigate);
            AddEvidence(evidence, navigate);
            if (!navigate.Success) return finalObservation;
        }

        for (var step = history.Count; step < maxSteps; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var observe = await ExecuteActionAsync(new BrowserActionRequest
            {
                SessionId = sessionId,
                Action = BrowserActionType.Observe
            }, cancellationToken);
            history.Add(observe);
            AddEvidence(evidence, observe);
            finalObservation = observe.Observation;
            if (!observe.Success || finalObservation == null) break;

            var nextAction = await _browserVisionService.DecideNextActionAsync(request, finalObservation, history, cancellationToken);
            nextAction.SessionId = sessionId;
            if (nextAction.Action == BrowserActionType.Finish)
            {
                var finish = FinishAction(sessionId, nextAction.Reason, !nextAction.IsTerminalFailure);
                history.Add(finish);
                AddEvidence(evidence, finish);
                break;
            }

            var result = await ExecuteActionAsync(nextAction, cancellationToken);
            history.Add(result);
            AddEvidence(evidence, result);

            if (result.Observation != null)
            {
                finalObservation = result.Observation;
            }

            if (!result.Success || result.RequiresUserConfirmation)
            {
                break;
            }

            if (result.Action == BrowserActionType.ExtractText)
            {
                var finish = FinishAction(sessionId, "Text evidence extracted.");
                history.Add(finish);
                AddEvidence(evidence, finish);
                break;
            }
        }

        return finalObservation;
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
                evidence.Add($"{result.Action}: {result.Message}");
                break;
        }
    }

    private static string BuildSummary(BrowserTaskRequest request, IReadOnlyList<BrowserActionResult> history, SomObservation? finalObservation)
    {
        var builder = new StringBuilder();
        builder.Append("Browser task executed in an isolated session.");
        if (finalObservation != null)
        {
            builder.Append($" Final page: {finalObservation.Title ?? "Untitled"} ({finalObservation.Url}).");
            builder.Append($" Marked elements: {finalObservation.Elements.Count}.");
        }
        else if (!string.IsNullOrWhiteSpace(request.StartUrl))
        {
            builder.Append($" Start URL: {request.StartUrl}.");
        }

        if (history.Any(h => !h.Success))
        {
            builder.Append($" Stopped after failure: {history.First(h => !h.Success).Message}");
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
