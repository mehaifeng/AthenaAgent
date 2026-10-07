using Athena.UI.Models;
using Athena.UI.Services.Context;
using Athena.UI.Services.Interfaces;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Responses;
using Serilog;
using System;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
// OpenAI SDK Experimental 面（OPENAI001）：本文件直接使用 Responses 类型。
#pragma warning disable OPENAI001

namespace Athena.UI.Services;

/// <summary>
/// 自动审批模型。没有工具；只认一个完整、无歧义的裁决，其余一律按安全策略拒绝。
/// 拒绝分两种来源，主模型收到的说法不同：模型的裁决（<see cref="ToolApprovalSource.JudgeVerdict"/>），
/// 与模型没能给出裁决（<see cref="ToolApprovalSource.JudgeFailure"/>，再按该不该重试分三类）。
/// 输出怎么判读见 <see cref="ApprovalJudgeOutput"/>。
/// </summary>
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
    private readonly PipelineTransport? _transport;

    public AiToolApprovalEvaluator(OpenAiModelRuntimeFactory modelFactory, ILogger logger)
        : this(modelFactory, logger, transport: null)
    {
    }

    private AiToolApprovalEvaluator(OpenAiModelRuntimeFactory modelFactory, ILogger logger, PipelineTransport? transport)
    {
        _modelFactory = modelFactory;
        _logger = logger.ForContext<AiToolApprovalEvaluator>();
        _transport = transport;
    }

    /// <summary>
    /// 测试入口：请求经由给定的 HTTP 传输发出。请求构造、SDK 反序列化与判读都和生产路径是同一份代码，
    /// 只有网络被换掉。
    /// </summary>
    internal static AiToolApprovalEvaluator CreateWithTransport(
        OpenAiModelRuntimeFactory modelFactory,
        ILogger logger,
        PipelineTransport transport)
        => new(modelFactory, logger, transport);

    public async Task<ToolApprovalDecision> EvaluateAsync(ToolApprovalRequest request, CancellationToken cancellationToken)
    {
        EffectiveOpenAiModel effective;
        try
        {
            effective = _modelFactory.Resolve(AiModelRole.Approval);
            effective.ValidateChatRole(AiModelRole.Approval);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Automatic tool approval failed closed for {Function}: the approval model role is not usable", request.FunctionName);
            return ToolApprovalDecision.JudgeFailed(ToolApprovalJudgeFailureKind.Configuration, "没有可用的自动审批模型");
        }

        var useResponses = ResponsesCallHelpers.ShouldUseResponses(effective);
        var protocol = useResponses ? "Responses" : "ChatCompletions";
        var maxOutputTokens = Math.Clamp(effective.MaxOutputTokens, 64, 512);
        // 推理强度只在 Responses 分支发送（取自该模型的元数据档案）；Chat 分支从来不发。
        var effortSent = useResponses && effective.Effort != ReasoningEffort.Auto
            ? effective.Effort.ToString()
            : "(not sent)";
        var payload = BuildPayload(request);

        ApprovalJudgeReply reply;
        try
        {
            reply = useResponses
                ? await AskViaResponsesAsync(effective, maxOutputTokens, payload, cancellationToken)
                : await AskViaChatAsync(effective, maxOutputTokens, payload, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ToolApprovalDecision.Cancelled("自动审批被取消或超时");
        }
        catch (Exception ex)
        {
            var (kind, detail) = ApprovalJudgeOutput.ClassifyRequestFailure(ex);
            _logger.Warning(
                ex,
                "Automatic tool approval failed closed for {Function}: request failed ({Kind}: {Detail}) | model={Model} protocol={Protocol} effort={Effort} cap={Cap}",
                request.FunctionName, kind, detail, effective.Model, protocol, effortSent, maxOutputTokens);
            return ToolApprovalDecision.JudgeFailed(kind, detail);
        }

        var outcome = ApprovalJudgeOutput.Interpret(reply);
        if (outcome.IsVerdict)
        {
            return outcome.Allow
                ? ToolApprovalDecision.JudgeAllowed(outcome.Text)
                : ToolApprovalDecision.JudgeDenied(outcome.Text);
        }

        // 这一行要能独自解释一次失败。事故当晚日志里只有一个异常名，靠重放上千次请求才弄清是推理吃光了输出预算。
        _logger.Warning(
            "Automatic tool approval failed closed for {Function}: {Problem} | model={Model} protocol={Protocol} effort={Effort} cap={Cap} status={Status} outputTokens={OutputTokens} reasoningTokens={ReasoningTokens} textLength={TextLength} textHead={TextHead}",
            request.FunctionName, outcome.Text, effective.Model, protocol, effortSent, maxOutputTokens,
            reply.Status, reply.OutputTokens, reply.ReasoningTokens, reply.Text.Length, ApprovalJudgeOutput.Head(reply.Text, 200));
        return ToolApprovalDecision.JudgeFailed(ToolApprovalJudgeFailureKind.Output, outcome.Text);
    }

    private static string BuildPayload(ToolApprovalRequest request) => JsonSerializer.Serialize(new
    {
        delegatedTask = ToolApprovalContext.CurrentDelegatedTask,
        tool = request.FunctionName,
        risk = request.Risk.ToString(),
        request.Summary,
        arguments = RedactSecrets(request.PrettyArguments),
        request.CommandLine,
        request.RiskReason
    });

    private async Task<ApprovalJudgeReply> AskViaResponsesAsync(
        EffectiveOpenAiModel effective,
        int maxOutputTokens,
        string payload,
        CancellationToken cancellationToken)
    {
        var client = ResponsesCallHelpers.CreateResponsesClient(effective, _modelFactory.TimeoutSeconds, _transport);
        var options = ResponsesCallHelpers.CreateOptions(
            effective,
            SystemPrompt,
            temperature: 0,
            maxOutputTokens,
            jsonObjectFormat: true);
        options.InputItems.Add(ResponseItem.CreateUserMessageItem(payload));
        var result = await client.CreateResponseAsync(options, cancellationToken);
        return ApprovalJudgeOutput.FromResponse(result.Value);
    }

    private async Task<ApprovalJudgeReply> AskViaChatAsync(
        EffectiveOpenAiModel effective,
        int maxOutputTokens,
        string payload,
        CancellationToken cancellationToken)
    {
        // 与 OpenAiModelRuntimeFactory.CreateChatClient 同一套选项（重试、超时、流清洗），但直接用上面已经解析好的
        // effective 构造：不再读第二次配置，也就不会与那次解析读到不同的模型。
        var clientOptions = OpenAiClientOptionsFactory.Create(effective.BaseUrl, _modelFactory.TimeoutSeconds);
        if (_transport != null) clientOptions.Transport = _transport;
        var client = new OpenAIClient(new ApiKeyCredential(effective.ApiKey), clientOptions).GetChatClient(effective.Model);
        var chatOptions = new ChatCompletionOptions
        {
            Temperature = 0,
            MaxOutputTokenCount = maxOutputTokens,
            ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat()
        };
        var response = await client.CompleteChatAsync(
            [new SystemChatMessage(SystemPrompt), new UserChatMessage(payload)],
            chatOptions,
            cancellationToken);
        return ApprovalJudgeOutput.FromChatCompletion(response.Value);
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
