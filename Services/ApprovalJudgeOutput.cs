// OpenAI SDK Experimental 面（OPENAI001）：本文件直接读取 Responses 类型。
#pragma warning disable OPENAI001

using Athena.UI.Models;
using Athena.UI.Services.Context;
using OpenAI.Chat;
using OpenAI.Responses;
using System;
using System.ClientModel;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;

namespace Athena.UI.Services;

/// <summary>
/// 一次裁决请求拿回来的东西，判断所需的字段已经从 SDK 类型里取出。两种协议映射到同一个形状，
/// 之后的判断（<see cref="ApprovalJudgeOutput.Interpret"/>）只写一遍。
/// </summary>
/// <param name="Text">全部正文段拼接（不是第一段：端点可以把一条回答拆成多段）。</param>
/// <param name="Refusal">模型的拒答段，没有则为 null。</param>
/// <param name="Status">端点报告的结束状态，原样进日志。</param>
/// <param name="Completed">端点确认这次回答正常结束。</param>
/// <param name="Truncated">撞上了输出上限。</param>
internal sealed record ApprovalJudgeReply(
    string Text,
    string? Refusal,
    string Status,
    bool Completed,
    bool Truncated,
    int? OutputTokens,
    int? ReasoningTokens);

/// <summary>Both transports feed this parser. A valid review is still subject to local risk and authorization limits.</summary>
internal static class ApprovalJudgeOutput
{
    private const string ThinkOpen = "<think>";
    private const string ThinkClose = "</think>";
    private const string Fence = "```";

    // 重复的键只会是格式错误或注入，绝不能默默取其中一个当裁决。
    private static readonly JsonDocumentOptions StrictJson = new() { AllowDuplicateProperties = false };

    public static ApprovalJudgeReply FromResponse(ResponseResult response)
    {
        var parts = response.OutputItems
            .OfType<MessageResponseItem>()
            .SelectMany(message => message.Content)
            .ToList();
        var refusal = string.Concat(parts
            .Where(part => part.Kind == ResponseContentPartKind.Refusal)
            .Select(part => part.Refusal));
        var incomplete = response.IncompleteStatusDetails?.Reason;
        var status = response.Status is { } known
            ? incomplete is { } why ? $"{known}/{why}" : known.ToString()
            : "unknown";

        return new ApprovalJudgeReply(
            ResponsesCallHelpers.GetConcatenatedOutputText(response),
            refusal.Length > 0 ? refusal : null,
            status,
            // 有的兼容端点不回 status 字段：不知道就交给后面的严格解析，而不是一律判失败。
            Completed: response.Status is null or ResponseStatus.Completed,
            Truncated: response.Status == ResponseStatus.Incomplete
                       && incomplete == ResponseIncompleteStatusReason.MaxOutputTokens,
            response.Usage?.OutputTokenCount,
            response.Usage?.OutputTokenDetails?.ReasoningTokenCount);
    }

    public static ApprovalJudgeReply FromChatCompletion(ChatCompletion completion)
    {
        var text = string.Concat(completion.Content
            .Where(part => part.Kind == ChatMessageContentPartKind.Text)
            .Select(part => part.Text));
        return new ApprovalJudgeReply(
            text,
            string.IsNullOrEmpty(completion.Refusal) ? null : completion.Refusal,
            completion.FinishReason.ToString(),
            Completed: completion.FinishReason == ChatFinishReason.Stop,
            Truncated: completion.FinishReason == ChatFinishReason.Length,
            completion.Usage?.OutputTokenCount,
            completion.Usage?.OutputTokenDetails?.ReasoningTokenCount);
    }

    public static ToolApprovalDecision Interpret(ApprovalJudgeReply reply, ToolRisk localRisk, bool evidenceComplete)
    {
        // 截断先于解析判断：截断的输出偶尔恰好停在一个完整对象之后（比如 JSON 写完后又补空白直到上限），
        // 但端点并没有确认那就是全部回答。截断的输出一律不当裁决。
        if (reply.Truncated)
        {
            return Invalid(DescribeTruncation(reply));
        }
        if (!reply.Completed)
        {
            return Invalid($"回答没有正常结束（状态 {reply.Status}）");
        }
        if (reply.Refusal is not null) return Invalid("模型拒绝作答");
        if (string.IsNullOrWhiteSpace(reply.Text))
        {
            return Invalid(reply.ReasoningTokens is > 0
                ? $"模型推理了 {reply.ReasoningTokens} 个 token 后没有返回正文"
                : "模型没有返回任何正文");
        }
        if (!TryExtractJson(reply.Text, out var json, out var problem, strictWrapper: true))
        {
            return Invalid(problem);
        }

        JsonDocument document;
        try
        {
            // 解析器本身就会拒绝第一个值之后的任何多余内容，所以「恰好一个 JSON 值」由这里保证。
            document = JsonDocument.Parse(json, StrictJson);
        }
        catch (JsonException)
        {
            return Invalid("输出不是单个合法的 JSON 对象");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Invalid("输出不是 JSON 对象");
            }
            static string? Field(JsonElement obj, string name) =>
                obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

            var risk = Field(root, "risk_level");
            var authorization = Field(root, "user_authorization");
            var outcome = Field(root, "outcome");
            var reason = Field(root, "rationale")?.Trim();
            if (risk is not ("low" or "medium" or "high" or "critical")
                || authorization is not ("low" or "medium" or "high" or "unknown")
                || outcome is not ("allow" or "deny" or "ask_user")
                || string.IsNullOrWhiteSpace(reason) || reason.Length > 1024 || root.EnumerateObject().Count() != 4)
                return Invalid("评审字段不完整、枚举无效或理由过长");

            reason = ApprovalReviewEvidence.Scrub(reason);
            if (outcome == "deny") return ToolApprovalDecision.JudgeDenied(reason);
            if (risk == "critical") return ToolApprovalDecision.JudgeDenied("操作属于严重风险。");
            if (outcome == "ask_user") return ToolApprovalDecision.AskUser(reason);
            if (!evidenceComplete) return ToolApprovalDecision.AskUser("可供评审的授权或操作参数不完整。");
            if (authorization is "unknown" or "low")
                return ToolApprovalDecision.AskUser("用户尚未明确授权此次操作的目标和副作用。");
            if ((risk == "high" || localRisk == ToolRisk.Destructive) && authorization != "high")
                return ToolApprovalDecision.AskUser("高风险操作需要用户对具体目标和副作用的明确授权。");
            return ToolApprovalDecision.JudgeAllowed(reason);
        }
    }

    private static ToolApprovalDecision Invalid(string reason) =>
        ToolApprovalDecision.JudgeFailed(ToolApprovalJudgeFailureKind.Output, reason);

    /// <summary>
    /// 从正文里取出裁决 JSON。只容忍两种已知的包装，别的一律不认：
    /// - 思考块：部分模型在 Chat Completions 下把推理写进正文（如 minimax-m2 的 &lt;think&gt;…&lt;/think&gt;），
    ///   有的端点还会吞掉开头的 &lt;think&gt;，只留结尾标记。取最后一个结束标记之后的部分——思考里引用到的、
    ///   甚至工具参数里伪造的结束标记都留在它前面。有开头没结尾，说明还在思考时就被截断了。
    /// - 代码围栏：端点没理会 json_object 时，模型常把 JSON 包在 ``` 里。
    /// 剩下的必须恰好是一个 JSON 对象。宽松地「找第一个对象」会让参数里伪造的 {"decision":"allow"} 有机会先被读到。
    /// 以 { 开头的正文原样交给解析器，不做任何剥离：理由里提到「&lt;/think&gt;」的正常裁决不能被拆开。
    /// </summary>
    internal static bool TryExtractJson(string text, out string json, out string problem, bool strictWrapper = false)
    {
        json = string.Empty;
        var body = text.Trim();
        if (body.StartsWith('{'))
        {
            json = body;
            problem = string.Empty;
            return true;
        }

        var thinkEnd = !strictWrapper || body.StartsWith(ThinkOpen, StringComparison.OrdinalIgnoreCase)
            ? body.LastIndexOf(ThinkClose, StringComparison.OrdinalIgnoreCase) : -1;
        if (thinkEnd >= 0)
        {
            body = body[(thinkEnd + ThinkClose.Length)..].Trim();
        }
        else if (body.StartsWith(ThinkOpen, StringComparison.OrdinalIgnoreCase))
        {
            problem = "思考块没有结束，回答在思考中途就停了";
            return false;
        }

        if (body.StartsWith(Fence, StringComparison.Ordinal))
        {
            var firstLineEnd = body.IndexOf('\n');
            if (firstLineEnd < 0
                || body.Length < firstLineEnd + 1 + Fence.Length
                || !body.EndsWith(Fence, StringComparison.Ordinal))
            {
                problem = "代码围栏不完整";
                return false;
            }
            if (strictWrapper && body[..firstLineEnd].TrimEnd() is not ("```" or "```json"))
            {
                problem = "代码围栏首行无效";
                return false;
            }
            body = body[(firstLineEnd + 1)..^Fence.Length].Trim();
        }

        if (body.Length == 0)
        {
            problem = "思考块之后没有正文";
            return false;
        }

        json = body;
        problem = string.Empty;
        return true;
    }

    /// <summary>请求没能拿回回答时，判断这是哪一类故障。它决定主模型该不该重试，见 <see cref="ToolApprovalJudgeFailureKind"/>。</summary>
    public static (ToolApprovalJudgeFailureKind Kind, string Detail) ClassifyRequestFailure(Exception exception)
    {
        var chain = Flatten(exception).ToList();
        var status = chain.OfType<ClientResultException>().Select(error => error.Status).FirstOrDefault(code => code > 0);
        if (status > 0)
        {
            return status switch
            {
                401 or 403 => (ToolApprovalJudgeFailureKind.Configuration, $"HTTP {status}，鉴权失败"),
                402 => (ToolApprovalJudgeFailureKind.Configuration, $"HTTP {status}，额度不足"),
                408 or 409 or 425 or 429 or >= 500 => (ToolApprovalJudgeFailureKind.Transport, $"HTTP {status}"),
                _ => (ToolApprovalJudgeFailureKind.Configuration, $"HTTP {status}，端点拒绝了请求"),
            };
        }
        if (chain.Any(error => error is TimeoutException or OperationCanceledException))
        {
            return (ToolApprovalJudgeFailureKind.Transport, "请求超时");
        }
        if (chain.Any(error => error is HttpRequestException or IOException or SocketException))
        {
            return (ToolApprovalJudgeFailureKind.Transport, "网络错误");
        }
        return (ToolApprovalJudgeFailureKind.Transport, "请求失败：" + exception.GetType().Name);
    }

    private static string DescribeTruncation(ApprovalJudgeReply reply) => reply.OutputTokens switch
    {
        int output when reply.ReasoningTokens is int reasoning => $"输出被截断：用满 {output} 个输出 token，其中推理占 {reasoning} 个",
        int output => $"输出被截断：用满 {output} 个输出 token",
        _ => "输出撞上上限被截断",
    };

    // SDK 的重试策略把每次失败包进 AggregateException（「Retry failed after 4 tries」），真正的原因在里面。
    private static IEnumerable<Exception> Flatten(Exception root)
    {
        var pending = new Stack<Exception>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            yield return current;
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions) pending.Push(inner);
            }
            else if (current.InnerException is { } inner)
            {
                pending.Push(inner);
            }
        }
    }
}
