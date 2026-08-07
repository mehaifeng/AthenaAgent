using Athena.UI.Models;
using Athena.UI.Services.Context;
using Athena.UI.Services.Interfaces;
using OpenAI.Chat;
using OpenAI.Responses;
using Serilog;
using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
// OpenAI SDK Experimental 面（OPENAI001）：本文件直接使用 Responses 类型。
#pragma warning disable OPENAI001

namespace Athena.UI.Services;

/// <summary>自动审批模型。没有工具；超时、异常、非结构化输出全部拒绝。</summary>
public sealed class AiToolApprovalEvaluator : IAiToolApprovalEvaluator
{
    private const string SystemPrompt = """
        You are a security approval judge for an AI desktop assistant.
        Decide whether the proposed tool call is necessary and proportionate to the delegated user task implied by its summary.
        Deny calls with unclear intent, excessive scope, credential exposure, destructive breadth, privilege escalation, or commands that download and execute untrusted code.
        Allow only when the action is narrowly scoped and its expected effect matches the stated task.
        Return JSON only: {"decision":"allow"|"deny","reason":"short explanation"}.
        You have no tools and cannot override application sandbox or hard security blocks.
        """;

    private readonly OpenAiModelRuntimeFactory _modelFactory;
    private readonly ILogger _logger;

    public AiToolApprovalEvaluator(OpenAiModelRuntimeFactory modelFactory, ILogger logger)
    {
        _modelFactory = modelFactory;
        _logger = logger.ForContext<AiToolApprovalEvaluator>();
    }

    public async Task<ToolApprovalDecision> EvaluateAsync(ToolApprovalRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var effective = _modelFactory.Resolve(AiModelRole.Approval);
            effective.ValidateChatRole(AiModelRole.Approval);
            var payload = JsonSerializer.Serialize(new
            {
                delegatedTask = ToolApprovalContext.CurrentDelegatedTask,
                tool = request.FunctionName,
                risk = request.Risk.ToString(),
                request.Summary,
                arguments = RedactSecrets(request.PrettyArguments),
                request.CommandLine,
                request.RiskReason
            });

            string? text;
            if (ResponsesCallHelpers.ShouldUseResponses(effective))
            {
                var responses = ResponsesCallHelpers.CreateResponsesClient(effective, _modelFactory.TimeoutSeconds);
                var options = ResponsesCallHelpers.CreateOptions(
                    effective,
                    SystemPrompt,
                    temperature: 0,
                    Math.Clamp(effective.MaxOutputTokens, 64, 512),
                    jsonObjectFormat: true);
                options.InputItems.Add(ResponseItem.CreateUserMessageItem(payload));
                var result = await responses.CreateResponseAsync(options, cancellationToken);
                text = ResponsesCallHelpers.GetFirstOutputText(result.Value);
            }
            else
            {
                var client = _modelFactory.CreateChatClient(AiModelRole.Approval);
                var chatOptions = new ChatCompletionOptions
                {
                    Temperature = 0,
                    MaxOutputTokenCount = Math.Clamp(effective.MaxOutputTokens, 64, 512),
                    ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat()
                };
                var response = await client.CompleteChatAsync(
                    [new SystemChatMessage(SystemPrompt), new UserChatMessage(payload)],
                    chatOptions,
                    cancellationToken);
                text = response.Value.Content.Count > 0 ? response.Value.Content[0].Text : null;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                return ToolApprovalDecision.Deny("自动审批模型未返回裁决");
            }

            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            var decision = root.TryGetProperty("decision", out var decisionNode)
                ? decisionNode.GetString()
                : null;
            var reason = root.TryGetProperty("reason", out var reasonNode)
                ? reasonNode.GetString()
                : null;
            return string.Equals(decision, "allow", StringComparison.OrdinalIgnoreCase)
                ? ToolApprovalDecision.AllowOnce("自动审批模型放行：" + (reason ?? "无说明"))
                : ToolApprovalDecision.Deny("自动审批模型拒绝：" + (reason ?? "无说明"));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ToolApprovalDecision.Deny("自动审批被取消或超时");
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Automatic tool approval failed closed for {Function}", request.FunctionName);
            return ToolApprovalDecision.Deny("自动审批失败，已按安全策略拒绝");
        }
    }

    internal static string RedactSecrets(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        try
        {
            var node = JsonNode.Parse(text);
            RedactNode(node);
            return node?.ToJsonString() ?? text;
        }
        catch
        {
            return text.Length > 2000 ? text[..2000] + "…" : text;
        }
    }

    private static void RedactNode(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(pair => pair.Key).ToArray())
            {
                if (IsSecretKey(key)) obj[key] = "[REDACTED]";
                else RedactNode(obj[key]);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array) RedactNode(child);
        }
    }

    private static bool IsSecretKey(string key)
    {
        var canonical = key.Replace("_", string.Empty).Replace("-", string.Empty).ToLowerInvariant();
        return canonical.Contains("apikey")
            || canonical.Contains("authorization")
            || canonical.Contains("password")
            || canonical.Contains("secret")
            || canonical.Contains("token");
    }
}
