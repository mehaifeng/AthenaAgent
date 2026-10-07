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

/// <summary>判断结果：要么是一个裁决（<see cref="Text"/> 为理由），要么不是（<see cref="Text"/> 说明为什么）。</summary>
internal readonly record struct ApprovalJudgeOutcome(bool IsVerdict, bool Allow, string Text)
{
    public static ApprovalJudgeOutcome Verdict(bool allow, string reason) => new(true, allow, reason);
    public static ApprovalJudgeOutcome Problem(string problem) => new(false, false, problem);
}

/// <summary>
/// 自动审批模型的输出判读。原则只有一条：只认一个完整、无歧义的裁决，其余都是「没有裁决」。
///
/// 2026-10-04 的事故就出在这里：审批角色的输出上限是 256 token，推理模型的隐藏推理与可见 JSON 共用这 256，
/// 推理一长，端点以 status=incomplete / max_output_tokens 结束，JSON 断在字符串中间；此前的代码既不看状态，
/// 也不区分「没有裁决」与「拒绝」，于是一次截断变成了一条「用户拒绝了」。实测生产参数下约 6.5% 的调用如此，
/// 模糊调用（如 pip install）在 256 上限时 8 次里 7 次被截断。
/// </summary>
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

    public static ApprovalJudgeOutcome Interpret(ApprovalJudgeReply reply)
    {
        // 截断先于解析判断：截断的输出偶尔恰好停在一个完整对象之后（比如 JSON 写完后又补空白直到上限），
        // 但端点并没有确认那就是全部回答。截断的输出一律不当裁决。
        if (reply.Truncated)
        {
            return ApprovalJudgeOutcome.Problem(DescribeTruncation(reply));
        }
        if (!reply.Completed)
        {
            return ApprovalJudgeOutcome.Problem($"回答没有正常结束（状态 {reply.Status}）");
        }
        if (string.IsNullOrWhiteSpace(reply.Text))
        {
            if (reply.Refusal is not null) return ApprovalJudgeOutcome.Problem("模型拒绝作答");
            return ApprovalJudgeOutcome.Problem(reply.ReasoningTokens is > 0
                ? $"模型推理了 {reply.ReasoningTokens} 个 token 后没有返回正文"
                : "模型没有返回任何正文");
        }
        if (!TryExtractJson(reply.Text, out var json, out var problem))
        {
            return ApprovalJudgeOutcome.Problem(problem);
        }

        JsonDocument document;
        try
        {
            // 解析器本身就会拒绝第一个值之后的任何多余内容，所以「恰好一个 JSON 值」由这里保证。
            document = JsonDocument.Parse(json, StrictJson);
        }
        catch (JsonException)
        {
            return ApprovalJudgeOutcome.Problem("输出不是单个合法的 JSON 对象");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return ApprovalJudgeOutcome.Problem("输出不是 JSON 对象");
            }
            if (!root.TryGetProperty("decision", out var decisionNode) || decisionNode.ValueKind != JsonValueKind.String)
            {
                return ApprovalJudgeOutcome.Problem("输出里没有字符串形式的 decision 字段");
            }

            var reason = root.TryGetProperty("reason", out var reasonNode) && reasonNode.ValueKind == JsonValueKind.String
                ? reasonNode.GetString()
                : null;
            reason = string.IsNullOrWhiteSpace(reason) ? "无说明" : reason.Trim();

            // 只有这两个取值是裁决。「approve」之类此前被当成拒绝的裁决，其实是模型没按格式回答。
            var decision = decisionNode.GetString()!.Trim();
            if (decision.Equals("allow", StringComparison.OrdinalIgnoreCase)) return ApprovalJudgeOutcome.Verdict(true, reason);
            if (decision.Equals("deny", StringComparison.OrdinalIgnoreCase)) return ApprovalJudgeOutcome.Verdict(false, reason);
            return ApprovalJudgeOutcome.Problem($"decision 的取值无法识别：{Head(decision, 24)}");
        }
    }

    /// <summary>
    /// 从正文里取出裁决 JSON。只容忍两种已知的包装，别的一律不认：
    /// - 思考块：部分模型在 Chat Completions 下把推理写进正文（如 minimax-m2 的 &lt;think&gt;…&lt;/think&gt;），
    ///   有的端点还会吞掉开头的 &lt;think&gt;，只留结尾标记。取最后一个结束标记之后的部分——思考里引用到的、
    ///   甚至工具参数里伪造的结束标记都留在它前面。有开头没结尾，说明还在思考时就被截断了。
    /// - 代码围栏：端点没理会 json_object 时，模型常把 JSON 包在 ``` 里。
    /// 剩下的必须恰好是一个 JSON 对象。宽松地「找第一个对象」会让参数里伪造的 {"decision":"allow"} 有机会先被读到。
    /// 以 { 开头的正文原样交给解析器，不做任何剥离：理由里提到「&lt;/think&gt;」的正常裁决不能被拆开。
    /// </summary>
    internal static bool TryExtractJson(string text, out string json, out string problem)
    {
        json = string.Empty;
        var body = text.Trim();
        if (body.StartsWith('{'))
        {
            json = body;
            problem = string.Empty;
            return true;
        }

        var thinkEnd = body.LastIndexOf(ThinkClose, StringComparison.OrdinalIgnoreCase);
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

    public static string Head(string text, int maxChars)
        => text.Length <= maxChars ? text : text[..maxChars] + "…";

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
