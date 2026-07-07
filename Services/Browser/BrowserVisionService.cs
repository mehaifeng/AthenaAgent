using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using OpenAI;
using OpenAI.Chat;
using Serilog;
using System;
using System.ClientModel;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.Browser;

public class BrowserVisionService : IBrowserVisionService
{
    private readonly IConfigService _configService;
    private readonly IModelEndpointResolver _endpointResolver;
    private readonly ILogger _logger;
    private readonly ILocalizationService? _localizationService;

    // 1×1 透明 PNG，仅用于连接测试时探测模型是否接受图像输入。
    private static readonly byte[] OnePixelPngBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    public BrowserVisionService(IConfigService configService, ILogger logger, ILocalizationService? localizationService = null, IModelEndpointResolver? endpointResolver = null)
    {
        _configService = configService;
        _endpointResolver = endpointResolver ?? CustomModelEndpointResolver.Instance;
        _logger = logger.ForContext<BrowserVisionService>();
        _localizationService = localizationService;
    }

    private string GetLocalized(string key, string defaultValue)
        => _localizationService?.GetString(key, defaultValue) ?? defaultValue;

    public async Task<BrowserAgentOutput> DecideNextActionsAsync(
        BrowserTaskRequest task,
        BrowserStateSummary browserState,
        BrowserAgentStepInfo stepInfo,
        IReadOnlyList<BrowserActionResult> actionHistory,
        IReadOnlyList<BrowserActionDefinition> availableActions,
        CancellationToken cancellationToken = default)
    {
        var config = _configService.Load();
        var effectiveConfig = BrowserAgentModelResolver.Resolve(config, _endpointResolver);
        if (string.IsNullOrWhiteSpace(effectiveConfig.ApiKey))
        {
            return DoneOutput("Browser model API key is not configured.", success: false);
        }

        if (string.IsNullOrWhiteSpace(effectiveConfig.Model))
        {
            return DoneOutput("Browser model is not configured.", success: false);
        }

        try
        {
            var clientOptions = new OpenAIClientOptions();
            if (!string.IsNullOrWhiteSpace(effectiveConfig.BaseUrl))
            {
                clientOptions.Endpoint = new Uri(effectiveConfig.BaseUrl);
            }

            var client = new OpenAIClient(new ApiKeyCredential(effectiveConfig.ApiKey), clientOptions);
            var chatClient = client.GetChatClient(effectiveConfig.Model);
            var messages = BuildAgentMessages(task, browserState, stepInfo, actionHistory, availableActions);
            var options = new ChatCompletionOptions
            {
                Temperature = (float)effectiveConfig.Temperature,
                MaxOutputTokenCount = effectiveConfig.MaxTokens
            };

            var completion = await chatClient.CompleteChatAsync(messages, options, cancellationToken);
            var content = completion.Value.Content.FirstOrDefault()?.Text ?? string.Empty;
            var output = ParseAgentOutput(content);
            if (output.Action.Count == 0)
            {
                output.Action.Add(CreateDoneAction("Browser model returned no actions.", success: false));
            }

            _logger.Information(
                "Browser agent output received. Model={Model}, Step={Step}, Actions={ActionCount}, FirstAction={FirstAction}, ResponseLength={ResponseLength}",
                effectiveConfig.Model,
                stepInfo.StepNumber,
                output.Action.Count,
                output.Action.FirstOrDefault()?.Name ?? "(none)",
                content.Length);
            return output;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Browser agent model call failed");
            return DoneOutput($"Browser agent model call failed: {ex.Message}", success: false);
        }
    }

    public async Task<BrowserActionRequest> DecideNextActionAsync(
        BrowserTaskRequest task,
        SomObservation observation,
        IReadOnlyList<BrowserActionResult> actionHistory,
        CancellationToken cancellationToken = default)
    {
        var config = _configService.Load();
        var effectiveConfig = BrowserAgentModelResolver.Resolve(config, _endpointResolver);
        if (string.IsNullOrWhiteSpace(effectiveConfig.ApiKey))
        {
            return Finish("Browser vision API key is not configured.", isFailure: true);
        }

        if (string.IsNullOrWhiteSpace(effectiveConfig.Model))
        {
            return Finish("Browser vision model is not configured.", isFailure: true);
        }

        try
        {
            var clientOptions = new OpenAIClientOptions();
            if (!string.IsNullOrWhiteSpace(effectiveConfig.BaseUrl))
            {
                clientOptions.Endpoint = new Uri(effectiveConfig.BaseUrl);
            }

            _logger.Information(
                "Browser vision call using Provider={Provider}, Model={Model}, BaseUrl={BaseUrl}, BaseUrlSource={BaseUrlSource}, ApiKeySource={ApiKeySource}, Elements={ElementCount}, ScreenshotPath={ScreenshotPath}",
                effectiveConfig.Provider,
                effectiveConfig.Model,
                string.IsNullOrWhiteSpace(effectiveConfig.BaseUrl) ? "(default)" : effectiveConfig.BaseUrl,
                effectiveConfig.BaseUrlSource,
                effectiveConfig.ApiKeySource,
                observation.Elements.Count,
                observation.AnnotatedScreenshotPath ?? "(not saved)");

            var client = new OpenAIClient(new ApiKeyCredential(effectiveConfig.ApiKey), clientOptions);
            var chatClient = client.GetChatClient(effectiveConfig.Model);
            var messages = BuildMessages(task, observation, actionHistory);
            var options = new ChatCompletionOptions
            {
                Temperature = (float)effectiveConfig.Temperature,
                MaxOutputTokenCount = effectiveConfig.MaxTokens
            };

            var completion = await chatClient.CompleteChatAsync(messages, options, cancellationToken);
            var content = completion.Value.Content.FirstOrDefault()?.Text ?? string.Empty;
            var decision = ParseDecision(content, task, observation);
            _logger.Information(
                "Browser vision decision received. Model={Model}, Action={Action}, ElementId={ElementId}, Url={Url}, Confidence={Confidence}, ResponseLength={ResponseLength}",
                effectiveConfig.Model,
                decision.Action,
                decision.ElementId ?? "(none)",
                decision.Url ?? "(none)",
                decision.Confidence,
                content.Length);
            return decision;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Browser vision decision failed");
            return Finish($"Vision decision failed: {ex.Message}", isFailure: true);
        }
    }

    public async Task<(bool Success, string Message)> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        var config = _configService.Load();
        var effectiveConfig = BrowserAgentModelResolver.Resolve(config, _endpointResolver);
        if (string.IsNullOrWhiteSpace(effectiveConfig.ApiKey))
        {
            return (false, GetLocalized("Vision.ApiKeyMissing", "Browser vision API key is not configured."));
        }

        if (string.IsNullOrWhiteSpace(effectiveConfig.Model))
        {
            return (false, GetLocalized("Vision.ModelMissing", "Browser vision model is not configured."));
        }

        try
        {
            var clientOptions = new OpenAIClientOptions();
            if (!string.IsNullOrWhiteSpace(effectiveConfig.BaseUrl))
            {
                clientOptions.Endpoint = new Uri(effectiveConfig.BaseUrl);
            }

            _logger.Information(
                "Testing browser vision model using Provider={Provider}, Model={Model}, BaseUrl={BaseUrl}, BaseUrlSource={BaseUrlSource}, ApiKeySource={ApiKeySource}",
                effectiveConfig.Provider,
                effectiveConfig.Model,
                string.IsNullOrWhiteSpace(effectiveConfig.BaseUrl) ? "(default)" : effectiveConfig.BaseUrl,
                effectiveConfig.BaseUrlSource,
                effectiveConfig.ApiKeySource);

            var client = new OpenAIClient(new ApiKeyCredential(effectiveConfig.ApiKey), clientOptions);
            var chatClient = client.GetChatClient(effectiveConfig.Model);

            // SoM 模式真正依赖图像输入：附带一张 1×1 测试图，以验证该模型确实支持视觉，
            // 而不仅仅是文本可达。DomOnly 模式只需文本推理，发纯文本即可。
            var needsVision = config.BrowserObservationMode != BrowserObservationMode.DomOnly;
            UserChatMessage userMessage = needsVision
                ? new UserChatMessage(
                    ChatMessageContentPart.CreateTextPart("Reply with OK only."),
                    ChatMessageContentPart.CreateImagePart(BinaryData.FromBytes(OnePixelPngBytes), "image/png", ChatImageDetailLevel.Low))
                : new UserChatMessage("test");

            var response = await chatClient.CompleteChatAsync(
                new OpenAI.Chat.ChatMessage[]
                {
                    new SystemChatMessage("Reply with OK only."),
                    userMessage
                },
                new ChatCompletionOptions { MaxOutputTokenCount = 10, Temperature = 0 },
                cancellationToken);

            var text = response.Value.Content.FirstOrDefault()?.Text;
            var capability = needsVision ? "vision (image input verified)" : "text-only";
            return string.IsNullOrWhiteSpace(text)
                ? (false, "Browser agent model returned an empty response.")
                : (true, $"Browser agent model connection succeeded [{capability}]. ApiKeySource={effectiveConfig.ApiKeySource}, BaseUrlSource={effectiveConfig.BaseUrlSource}, Model={effectiveConfig.Model}.");
        }
        catch (Exception ex)
        {
            return (false, string.Format(GetLocalized("Vision.ConnectionFailed", "Browser vision model connection failed: {0}"), ex.Message));
        }
    }

    private static List<OpenAI.Chat.ChatMessage> BuildMessages(BrowserTaskRequest task, SomObservation observation, IReadOnlyList<BrowserActionResult> actionHistory)
    {
        var imageBytes = Convert.FromBase64String(observation.ScreenshotBase64);
        var elementJson = JsonSerializer.Serialize(observation.Elements.Select(e => new
        {
            id = e.ElementId,
            index = e.Index,
            stableKey = e.StableKey,
            tag = e.TagName,
            role = e.Role,
            text = e.Text,
            ariaLabel = e.AriaLabel,
            placeholder = e.Placeholder,
            href = e.Href,
            value = e.IsSensitive ? null : e.Value,
            inputType = e.InputType,
            isEditable = e.IsEditable,
            isChecked = e.IsChecked,
            isSensitive = e.IsSensitive,
            box = new { e.BoundingBox.X, e.BoundingBox.Y, e.BoundingBox.Width, e.BoundingBox.Height }
        }));
        var recentActionsJson = JsonSerializer.Serialize(actionHistory.TakeLast(8).Select(a => new
        {
            action = a.Action.ToString(),
            success = a.Success,
            recoverableFailure = a.IsRecoverableFailure,
            message = a.Message,
            effect = a.Effect == null ? null : new
            {
                elementId = a.Effect.ElementId,
                targetStableKey = a.Effect.TargetStableKey,
                targetSelector = a.Effect.TargetSelector,
                requestedText = a.Effect.RequestedText,
                valueBefore = a.Effect.ValueBefore,
                valueAfter = a.Effect.ValueAfter,
                changed = a.Effect.Changed,
                skipped = a.Effect.Skipped,
                matchesRequestedValue = a.Effect.MatchesRequestedValue,
                skipReason = a.Effect.SkipReason
            }
        }));

        var prompt = $"""
            Task: {task.Instruction}
            Current URL: {observation.Url}
            Page title: {observation.Title}
            Recent actions JSON: {recentActionsJson}
            SoM elements JSON: {elementJson}
            """;

        return new List<OpenAI.Chat.ChatMessage>
        {
            new SystemChatMessage(BrowserPrompts.VisionDecisionPrompt),
            new UserChatMessage(
                ChatMessageContentPart.CreateTextPart(prompt),
                ChatMessageContentPart.CreateImagePart(BinaryData.FromBytes(imageBytes), observation.ScreenshotMimeType, ChatImageDetailLevel.Low))
        };
    }

    private static List<OpenAI.Chat.ChatMessage> BuildAgentMessages(
        BrowserTaskRequest task,
        BrowserStateSummary browserState,
        BrowserAgentStepInfo stepInfo,
        IReadOnlyList<BrowserActionResult> actionHistory,
        IReadOnlyList<BrowserActionDefinition> availableActions)
    {
        var actionsJson = JsonSerializer.Serialize(availableActions.Select(action => new
        {
            name = action.Name,
            description = action.Description,
            parameters = action.ParametersJsonSchema,
            terminatesSequence = action.TerminatesSequence,
            isTerminal = action.IsTerminal
        }));

        var elementsJson = JsonSerializer.Serialize(browserState.Elements.Select(e => new
        {
            index = e.Index,
            id = e.ElementId,
            tag = e.TagName,
            role = e.Role,
            text = e.Text,
            ariaLabel = e.AriaLabel,
            placeholder = e.Placeholder,
            href = e.Href,
            value = e.IsSensitive ? null : e.Value,
            inputType = e.InputType,
            isEditable = e.IsEditable,
            isChecked = e.IsChecked,
            isSensitive = e.IsSensitive,
            box = new { e.BoundingBox.X, e.BoundingBox.Y, e.BoundingBox.Width, e.BoundingBox.Height }
        }));

        var recentActionsJson = JsonSerializer.Serialize(actionHistory.TakeLast(12).Select(a => new
        {
            action = a.ActionName ?? a.Action.ToString(),
            success = a.Success,
            done = a.IsDone,
            message = a.Message,
            error = a.Error,
            extractedContent = TrimForPrompt(a.ExtractedContent ?? a.ExtractedText, 1200),
            url = a.Url
        }));

        var stateJson = JsonSerializer.Serialize(new
        {
            url = browserState.Url,
            title = browserState.Title,
            tabs = browserState.Tabs,
            page = browserState.PageInfo,
            elements = browserState.Elements.Count,
            browserErrors = browserState.BrowserErrors
        });

        var prompt = $"""
            <task>
            {task.Instruction}
            </task>

            <step>
            {stepInfo.StepNumber}/{stepInfo.MaxSteps}
            </step>

            <agent_memory>
            {TrimForPrompt(stepInfo.LongTermMemory, 4000)}
            </agent_memory>

            <read_state>
            {TrimForPrompt(stepInfo.ReadState, 4000)}
            </read_state>

            <browser_state_json>
            {stateJson}
            </browser_state_json>

            <som_elements_json>
            {elementsJson}
            </som_elements_json>

            <recent_actions_json>
            {recentActionsJson}
            </recent_actions_json>

            <available_actions_json>
            {actionsJson}
            </available_actions_json>
            """;

        if (stepInfo.UseVision && !string.IsNullOrWhiteSpace(browserState.ScreenshotBase64))
        {
            var imageBytes = Convert.FromBase64String(browserState.ScreenshotBase64);
            return new List<OpenAI.Chat.ChatMessage>
            {
                new SystemChatMessage(BrowserPrompts.AgentOutputPrompt),
                new UserChatMessage(
                    ChatMessageContentPart.CreateTextPart(prompt),
                    ChatMessageContentPart.CreateImagePart(BinaryData.FromBytes(imageBytes), browserState.ScreenshotMimeType ?? "image/png", ChatImageDetailLevel.Low))
            };
        }

        return new List<OpenAI.Chat.ChatMessage>
        {
            new SystemChatMessage(BrowserPrompts.AgentOutputPrompt),
            new UserChatMessage(prompt)
        };
    }

    private static BrowserAgentOutput ParseAgentOutput(string content)
    {
        var json = ExtractJson(content);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var output = new BrowserAgentOutput
        {
            Thinking = ReadString(root, "thinking"),
            EvaluationPreviousGoal = ReadString(root, "evaluation_previous_goal") ?? ReadString(root, "evaluationPreviousGoal"),
            Memory = ReadString(root, "memory"),
            NextGoal = ReadString(root, "next_goal") ?? ReadString(root, "nextGoal"),
            CurrentPlanItem = ReadInt(root, "current_plan_item") ?? ReadInt(root, "currentPlanItem")
        };

        if (root.TryGetProperty("plan_update", out var planUpdate) || root.TryGetProperty("planUpdate", out planUpdate))
        {
            if (planUpdate.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in planUpdate.EnumerateArray())
                {
                    var value = ReadJsonElementString(item);
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        output.PlanUpdate.Add(value);
                    }
                }
            }
        }

        if ((root.TryGetProperty("action", out var actionElement) || root.TryGetProperty("actions", out actionElement))
            && actionElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in actionElement.EnumerateArray())
            {
                var action = ParseAgentAction(item);
                if (!string.IsNullOrWhiteSpace(action.Name))
                {
                    output.Action.Add(action);
                }
            }
        }

        return output;
    }

    private static BrowserAgentAction ParseAgentAction(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return new BrowserAgentAction();
        }

        var properties = item.EnumerateObject().ToList();
        if (properties.Count == 1
            && !string.Equals(properties[0].Name, "name", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(properties[0].Name, "action", StringComparison.OrdinalIgnoreCase))
        {
            var action = new BrowserAgentAction
            {
                Name = BrowserActionRegistry.NormalizeName(properties[0].Name)
            };

            if (properties[0].Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var parameter in properties[0].Value.EnumerateObject())
                {
                    action.Parameters[parameter.Name] = parameter.Value.Clone();
                }
            }

            return action;
        }

        var flat = new BrowserAgentAction
        {
            Name = BrowserActionRegistry.NormalizeName(ReadString(item, "name") ?? ReadString(item, "action"))
        };

        foreach (var property in properties)
        {
            if (string.Equals(property.Name, "name", StringComparison.OrdinalIgnoreCase)
                || string.Equals(property.Name, "action", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            flat.Parameters[property.Name] = property.Value.Clone();
        }

        return flat;
    }

    private static BrowserActionRequest ParseDecision(string content, BrowserTaskRequest task, SomObservation observation)
    {
        try
        {
            var json = ExtractJson(content);
            var decision = JsonSerializer.Deserialize<VisionDecision>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (decision == null)
            {
                return Finish("Vision model returned an empty decision.");
            }

            var action = NormalizeAction(decision.Action);
            var request = new BrowserActionRequest
            {
                Action = action,
                ElementId = ReadDecisionString(decision.TargetElementId),
                Url = NormalizeUrl(decision.Url),
                Text = decision.Text,
                FilePath = NullIfWhiteSpace(decision.FilePath) ?? NullIfWhiteSpace(decision.FilePathSnake) ?? NullIfWhiteSpace(decision.Text),
                Key = decision.Key,
                DeltaX = decision.DeltaX ?? 0,
                DeltaY = decision.DeltaY ?? 700,
                WaitMilliseconds = decision.WaitMilliseconds ?? 1000,
                Reason = decision.Reason,
                Confidence = decision.Confidence
            };

            if (action == BrowserActionType.Navigate)
            {
                request.Url ??= ExtractFirstUrl(task.Instruction);
                if (string.IsNullOrWhiteSpace(request.Url))
                {
                    return Finish("Vision model selected navigate without a URL.", isFailure: true);
                }
            }

            if (action == BrowserActionType.PressKey && string.IsNullOrWhiteSpace(request.Key))
            {
                return Finish("Vision model selected press_key without a key.", isFailure: true);
            }

            if (action == BrowserActionType.Upload && string.IsNullOrWhiteSpace(request.FilePath))
            {
                return Finish("Vision model selected upload without a filePath.", isFailure: true);
            }

            if (action == BrowserActionType.Click || action == BrowserActionType.Type || action == BrowserActionType.Upload)
            {
                var canonicalElementId = NormalizeElementId(request.ElementId, observation);
                if (canonicalElementId == null)
                {
                    return Finish($"Vision model selected an unknown element: {request.ElementId}", isFailure: true);
                }

                request.ElementId = canonicalElementId;
            }

            return request;
        }
        catch (Exception ex)
        {
            return Finish($"Failed to parse vision decision: {ex.Message}", isFailure: true);
        }
    }

    private static BrowserActionType NormalizeAction(string? action)
    {
        return (action ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "navigate" or "goto" or "go_to" or "open" or "open_url" => BrowserActionType.Navigate,
            "click" => BrowserActionType.Click,
            "type" => BrowserActionType.Type,
            "upload" or "set_file" or "set_input_files" or "file_upload" => BrowserActionType.Upload,
            "press_key" or "presskey" => BrowserActionType.PressKey,
            "scroll" => BrowserActionType.Scroll,
            "wait" => BrowserActionType.Wait,
            "extract_text" or "extracttext" => BrowserActionType.ExtractText,
            "finish" or "done" => BrowserActionType.Finish,
            _ => BrowserActionType.Finish
        };
    }

    private static string? ReadDecisionString(object? value)
    {
        return value switch
        {
            null => null,
            string text => string.IsNullOrWhiteSpace(text) ? null : text.Trim(),
            JsonElement json => ReadJsonElementString(json),
            _ => value.ToString()?.Trim()
        };
    }

    private static string? ReadJsonElementString(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => NullIfWhiteSpace(value.GetString()),
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => NullIfWhiteSpace(value.ToString())
        };
    }

    private static string? NormalizeElementId(string? rawElementId, SomObservation observation)
    {
        var elementId = NullIfWhiteSpace(rawElementId);
        if (elementId == null)
        {
            return null;
        }

        var exact = observation.Elements.FirstOrDefault(e => string.Equals(e.ElementId, elementId, StringComparison.OrdinalIgnoreCase));
        if (exact != null)
        {
            return exact.ElementId;
        }

        if (elementId.StartsWith("som-", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(elementId[4..], out var somIndex))
        {
            return observation.Elements.FirstOrDefault(e => e.Index == somIndex)?.ElementId;
        }

        if (int.TryParse(elementId, out var numericId))
        {
            if (numericId > 0)
            {
                var oneBasedMatch = observation.Elements.FirstOrDefault(e => e.Index == numericId)
                    ?? observation.Elements.FirstOrDefault(e => string.Equals(e.ElementId, $"som-{numericId}", StringComparison.OrdinalIgnoreCase));
                if (oneBasedMatch != null)
                {
                    return oneBasedMatch.ElementId;
                }
            }

            if (numericId >= 0 && numericId < observation.Elements.Count)
            {
                return observation.Elements[numericId].ElementId;
            }
        }

        return null;
    }

    private static string? NormalizeUrl(string? url)
    {
        var trimmed = NullIfWhiteSpace(url);
        if (trimmed == null)
        {
            return null;
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absolute)
            && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
        {
            return absolute.ToString();
        }

        return null;
    }

    private static string? ExtractFirstUrl(string value)
    {
        var match = Regex.Match(value, @"https?://[^\s""'<>，。；、)）\]]+", RegexOptions.IgnoreCase);
        return match.Success ? NormalizeUrl(match.Value) : null;
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string ExtractJson(string content)
    {
        var trimmed = content.Trim();
        if (trimmed.StartsWith('{') && trimmed.EndsWith('}'))
        {
            return trimmed;
        }

        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            return trimmed[start..(end + 1)];
        }

        throw new JsonException("No JSON object found.");
    }

    private static BrowserActionRequest Finish(string reason, bool isFailure = false) =>
        new()
        {
            Action = BrowserActionType.Finish,
            Reason = reason,
            Confidence = 0,
            IsTerminalFailure = isFailure
        };

    private static BrowserAgentOutput DoneOutput(string text, bool success) => new()
    {
        EvaluationPreviousGoal = success ? "success" : "failure",
        Memory = text,
        NextGoal = "done",
        Action = { CreateDoneAction(text, success) }
    };

    private static BrowserAgentAction CreateDoneAction(string text, bool success) => new()
    {
        Name = "done",
        Parameters = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
        {
            ["text"] = JsonSerializer.SerializeToElement(text),
            ["success"] = JsonSerializer.SerializeToElement(success)
        }
    };

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return ReadJsonElementString(value);
    }

    private static int? ReadInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        return int.TryParse(ReadJsonElementString(value), out var parsed) ? parsed : null;
    }

    private static string? TrimForPrompt(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength] + "\n... [truncated]";
    }

    private sealed class VisionDecision
    {
        [JsonPropertyName("action")]
        public string? Action { get; set; }

        [JsonPropertyName("targetElementId")]
        public object? TargetElementId { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }

        [JsonPropertyName("text")]
        public string? Text { get; set; }

        [JsonPropertyName("filePath")]
        public string? FilePath { get; set; }

        [JsonPropertyName("file_path")]
        public string? FilePathSnake { get; set; }

        [JsonPropertyName("key")]
        public string? Key { get; set; }

        [JsonPropertyName("deltaX")]
        public int? DeltaX { get; set; }

        [JsonPropertyName("deltaY")]
        public int? DeltaY { get; set; }

        [JsonPropertyName("waitMilliseconds")]
        public int? WaitMilliseconds { get; set; }

        [JsonPropertyName("reason")]
        public string? Reason { get; set; }

        [JsonPropertyName("confidence")]
        public double? Confidence { get; set; }
    }
}
