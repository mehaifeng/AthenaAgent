// Responses API 传输：本文件全部类型均为 OpenAI SDK 的 Experimental(OPENAI001) 面。
#pragma warning disable OPENAI001

using Athena.UI.Models;
using Athena.UI.Services.Protocol;
using OpenAI.Chat;
using OpenAI.Responses;
using Serilog;
using System;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;

namespace Athena.UI.Services.Context;

/// <summary>
/// 会话级「端点不支持 /responses」记忆：首次 404/405 降级后标记，后续请求直接走 Chat Completions。
/// 键为 provider id，进程内有效（与配置无关，不持久化）。
/// </summary>
public static class ResponsesUnsupportedRegistry
{
    private static readonly ConcurrentDictionary<string, byte> Unsupported = new(StringComparer.Ordinal);

    public static void Mark(string providerId) => Unsupported.TryAdd(providerId, 0);

    public static bool IsMarked(string providerId) => Unsupported.ContainsKey(providerId);
}

/// <summary>
/// 一个 <c>response.failed</c> / <c>error</c> 事件从已经打开的 /responses 流里到达。
///
/// 单独一个类型只为一件事：让 ResponsesTransport 的 IsEndpointUnsupported 一眼把它排除。
/// 流都建立起来了，端点显然支持这个协议——而现在异常消息里带上了供应商错误原文，
/// 原文里恰好出现「not found」就会被那个按关键字匹配的判断读成「端点不支持」，
/// 于是一次普通的上游故障会把整个会话永久降级到 Chat Completions。
/// </summary>
internal sealed class ResponsesStreamFailureException(string message)
    : ClientResultException(message, response: null, innerException: null);

/// <summary>
/// Responses API 传输实现：把主环的规范请求形状（List&lt;ChatMessage&gt;）翻译为 input items，
/// 并把响应事件流归一化为 <see cref="NormalizedUpdate"/>。
/// 规则：
/// - 无状态（store: false），每次请求全量重发 items，永不使用 previous_response_id（Athena 自管上下文）；
/// - 工具全部经自有 FunctionRegistry 执行，不使用内置工具；
/// - include: ["reasoning"] 取完整推理文本（第三方端点忽略时退化为摘要通道）；
/// - 端点不支持（404/405 或明确暗示）时自动降级 Chat Completions 重发本轮并记忆。
/// </summary>
public sealed class ResponsesTransport : ICompletionTransport
{
    public static ResponsesTransport Instance { get; } = new();

    public string TransportId => "responses";

    public async IAsyncEnumerable<NormalizedUpdate> StreamUpdatesAsync(
        EffectiveRequestRuntimeSnapshot runtime,
        IReadOnlyList<OpenAI.Chat.ChatMessage> messages,
        int maxOutputTokens,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // 主枚举：responses 协议；端点不支持时切换到 chat 重发本轮。
        // 注意迭代器约束：yield 不能在带 catch 的 try 里，因此捕获只包住 MoveNextAsync。
        var current = EnumerateResponses(runtime, messages, maxOutputTokens, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        var usingChatFallback = false;
        try
        {
            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = await current.MoveNextAsync();
                }
                catch (Exception ex) when (!usingChatFallback && IsEndpointUnsupported(ex))
                {
                    await current.DisposeAsync();
                    Log.Warning(
                        "Provider does not support /responses; falling back to Chat Completions for this request. Provider={Provider} Model={Model}",
                        runtime.ExecutionPolicyIdentity.ProviderId,
                        runtime.ExecutionPolicyIdentity.ExternalModelId);
                    ResponsesUnsupportedRegistry.Mark(runtime.ExecutionPolicyIdentity.ProviderId);
                    usingChatFallback = true;
                    current = ChatCompletionsTransport.Instance
                        .StreamUpdatesAsync(runtime, messages, maxOutputTokens, cancellationToken)
                        .GetAsyncEnumerator(cancellationToken);
                    continue;
                }

                if (!hasNext)
                {
                    break;
                }

                yield return current.Current;
            }
        }
        finally
        {
            await current.DisposeAsync();
        }
    }

    public void AppendAssistantWithTools(
        List<OpenAI.Chat.ChatMessage> messages,
        string content,
        IReadOnlyList<ToolCallInfo> toolCalls,
        string? reasoningContent)
    {
        // 请求侧不回放推理（官方 /responses 无需回放；跨协议历史推理由设计文档 §5.4 决策处理）。
        // 回填形状与 chat 一致：一条携带全部 ToolCalls 的 assistant 消息，BuildInputItems 转换
        // 为 message item + function_call items。
        var message = new AssistantChatMessage(content ?? "");
        foreach (var toolCall in toolCalls)
        {
            message.ToolCalls.Add(ChatToolCall.CreateFunctionToolCall(
                toolCall.Id,
                toolCall.FunctionName,
                BinaryData.FromString(toolCall.Arguments)));
        }
        messages.Add(message);
    }

    public void AppendToolResult(List<OpenAI.Chat.ChatMessage> messages, string callId, string resultJson)
        => messages.Add(new ToolChatMessage(callId, resultJson));

    public bool IsToolCallArgumentsComplete(string? arguments)
        // responses：服务端 status=incomplete 权威标记，参数 JSON 猜测无意义。
        => true;

    private static async IAsyncEnumerable<NormalizedUpdate> EnumerateResponses(
        EffectiveRequestRuntimeSnapshot runtime,
        IReadOnlyList<OpenAI.Chat.ChatMessage> messages,
        int maxOutputTokens,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var options = BuildOptions(runtime, messages, maxOutputTokens);
        var stream = runtime.ResponsesClient!.CreateResponseStreamingAsync(options, cancellationToken);

        var itemIdToIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var nextToolIndex = 0;
        var sawFunctionCall = false;
        var sawFullReasoningText = false;
        var sawTerminalStatus = false;

        await foreach (var update in stream)
        {
            // 终止事件有两个：response.completed 与 response.incomplete。usage 与最终 status 都挂在
            // 事件自带的 response 上，两者必须同等对待——只认 completed，会让「输出被 max_output_tokens
            // 截断」这一路既拿不到 usage 也拿不到完成原因，主环只能把它读成一次正常收尾，
            // 于是回合在正文出现前静默结束（MapStatus 的 Incomplete 分支也永远走不到）。
            ResponseResult? terminalResponse = null;

            switch (update)
            {
                case StreamingResponseOutputItemAddedUpdate added when added.Item is FunctionCallResponseItem functionCall:
                    sawFunctionCall = true;
                    var toolIndex = nextToolIndex++;
                    itemIdToIndex[functionCall.Id] = toolIndex;
                    yield return new NormalizedUpdate(
                        ToolCallIndex: toolIndex,
                        ToolCallId: functionCall.CallId,
                        ToolCallName: functionCall.FunctionName,
                        ToolCallArgumentsDelta: TextOrNull(functionCall.FunctionArguments));
                    break;
                case StreamingResponseOutputTextDeltaUpdate text:
                    yield return new NormalizedUpdate(Text: text.Delta);
                    break;
                case StreamingResponseReasoningTextDeltaUpdate reasoning:
                    sawFullReasoningText = true;
                    yield return new NormalizedUpdate(ReasoningText: reasoning.Delta);
                    break;
                case StreamingResponseReasoningSummaryTextDeltaUpdate summary when !sawFullReasoningText:
                    // 第三方端点可能忽略 include 只发摘要：作为推理文本的回退通道。
                    yield return new NormalizedUpdate(ReasoningText: summary.Delta);
                    break;
                case StreamingResponseFunctionCallArgumentsDeltaUpdate argumentsDelta:
                    if (itemIdToIndex.TryGetValue(argumentsDelta.ItemId, out var argumentsIndex))
                    {
                        yield return new NormalizedUpdate(
                            ToolCallIndex: argumentsIndex,
                            ToolCallArgumentsDelta: TextOrNull(argumentsDelta.Delta));
                    }
                    break;
                case StreamingResponseOutputItemDoneUpdate done when done.Item is FunctionCallResponseItem doneCall:
                    // 服务端权威截断标记：替代 chat 的参数 JSON 猜测。
                    if (doneCall.Status == FunctionCallStatus.Incomplete)
                    {
                        yield return new NormalizedUpdate(ToolCallIncomplete: true);
                    }
                    break;
                case StreamingResponseCompletedUpdate completed:
                    terminalResponse = completed.Response;
                    break;
                case StreamingResponseIncompleteUpdate incomplete:
                    terminalResponse = incomplete.Response;
                    break;
                case StreamingResponseFailedUpdate failed:
                    throw new ResponsesStreamFailureException(DescribeFailedResponse(failed.Response));
                case StreamingResponseErrorUpdate error:
                    throw new ResponsesStreamFailureException(
                        $"Responses stream error: {error.Message} (code={error.Code}, param={error.Param})");
            }

            if (terminalResponse == null)
            {
                continue;
            }

            sawTerminalStatus = true;
            if (terminalResponse.Usage is { } usage)
            {
                yield return new NormalizedUpdate(
                    Usage: new TokenUsageSnapshot(
                        usage.InputTokenCount,
                        usage.InputTokenDetails?.CachedTokenCount ?? 0,
                        usage.OutputTokenCount,
                        usage.TotalTokenCount),
                    ReasoningTokenCount: usage.OutputTokenDetails?.ReasoningTokenCount,
                    InputModalityUsage: ExtractInputModalityUsage(usage));
            }

            yield return new NormalizedUpdate(FinishReason: MapStatus(terminalResponse, sawFunctionCall));
        }

        // 一句终止事件都没给就把流关掉：既不是正常收尾，也不是能分类的错误。必须显式标记未完成，
        // 否则主环会把「连接被掐断」读成「模型说完了」，回合照样静默结束。
        if (!sawTerminalStatus)
        {
            Log.Warning("Responses stream ended without a terminal status event (no response.completed/incomplete/failed)");
            yield return new NormalizedUpdate(FinishReason: TransportFinishReason.Incomplete);
        }
    }

    private static CreateResponseOptions BuildOptions(
        EffectiveRequestRuntimeSnapshot runtime,
        IReadOnlyList<OpenAI.Chat.ChatMessage> messages,
        int maxOutputTokens)
    {
        var options = new CreateResponseOptions
        {
            Model = runtime.MainModel.Model,
            Instructions = BuildInstructions(messages),
            Temperature = runtime.ChatOptions.Temperature,
            TopP = runtime.ChatOptions.TopP,
            MaxOutputTokenCount = maxOutputTokens > 0 ? maxOutputTokens : runtime.ChatOptions.MaxOutputTokenCount,
            StreamingEnabled = true,
            // 无状态：Athena 自管上下文（压缩/回缩/分支），绝不使用服务端 store。
            StoredOutputEnabled = false,
        };

        foreach (var tool in runtime.ToolDefinitions)
        {
            options.Tools.Add(new FunctionTool(
                tool.FunctionName,
                tool.FunctionParameters,
                tool.FunctionSchemaIsStrict)
            {
                FunctionDescription = tool.FunctionDescription ?? string.Empty,
            });
        }

        // 完整推理文本：官方 OpenAI 端点经 include=reasoning 获取（responses 协议下完整思考
        // 文本的唯一通道）。第三方 /responses 端点（OpenRouter 实测）对 include 支持面极窄，
        // 除 reasoning.encrypted_content 外的任何取值都会 400 invalid_prompt，因此只在官方
        // 端点携带；第三方端点的推理文本靠默认摘要事件（response.reasoning_summary_text.delta，
        // 推理模型无需任何参数即可产出）走回退通道并入 ReasoningContent。
        if (ResponsesProtocolResolver.IsOfficialOpenAi(runtime.MainModel.ProviderPreset, runtime.MainModel.BaseUrl))
        {
            options.IncludedProperties.Add("reasoning");
        }
        else
        {
            Log.Debug(
                "Skipping include=reasoning for third-party /responses endpoint. Provider={Provider} BaseUrl={BaseUrl}",
                runtime.MainModel.ProviderPreset,
                runtime.MainModel.BaseUrl);
        }

        // 推理强度：仅在显式配置时发送（Auto = 端点默认），wire 名 effort。
        if (runtime.MainModel.Effort != ReasoningEffort.Auto)
        {
            options.ReasoningOptions = new ResponseReasoningOptions
            {
                ReasoningEffortLevel = MapEffort(runtime.MainModel.Effort)
            };
        }

        ResponsesCallHelpers.AddInputItems(options, messages);

        return options;
    }

    private static ResponseReasoningEffortLevel MapEffort(ReasoningEffort effort) => effort switch
    {
        ReasoningEffort.None => ResponseReasoningEffortLevel.None,
        ReasoningEffort.Minimal => ResponseReasoningEffortLevel.Minimal,
        ReasoningEffort.Low => ResponseReasoningEffortLevel.Low,
        ReasoningEffort.Medium => ResponseReasoningEffortLevel.Medium,
        ReasoningEffort.High => ResponseReasoningEffortLevel.High,
        ReasoningEffort.XHigh => (ResponseReasoningEffortLevel)"xhigh",
        ReasoningEffort.Max => (ResponseReasoningEffortLevel)"max",
        _ => ResponseReasoningEffortLevel.Medium
    };

    private static string BuildInstructions(IReadOnlyList<OpenAI.Chat.ChatMessage> messages)
    {
        if (messages.FirstOrDefault() is SystemChatMessage system)
        {
            return string.Concat(system.Content
                .Where(part => part.Kind == ChatMessageContentPartKind.Text)
                .Select(part => part.Text));
        }

        return string.Empty;
    }

    private static ProviderInputModalityUsage? ExtractInputModalityUsage(ResponseTokenUsage usage)
    {
#pragma warning disable SCME0001
        long? imageTokens = usage.Patch.TryGetValue("$.input_tokens_details.image_tokens"u8, out long image)
                            && image >= 0
            ? image
            : null;
        long? textTokens = usage.Patch.TryGetValue("$.input_tokens_details.text_tokens"u8, out long text)
                           && text >= 0
            ? text
            : null;
#pragma warning restore SCME0001
        return imageTokens.HasValue || textTokens.HasValue
            ? new ProviderInputModalityUsage(textTokens, imageTokens, null)
            : null;
    }

    private static TransportFinishReason MapStatus(ResponseResult response, bool sawFunctionCall)
    {
        switch (response.Status)
        {
            case ResponseStatus.Failed:
            case ResponseStatus.Cancelled:
                return TransportFinishReason.Error;
            case ResponseStatus.Incomplete:
                if (response.IncompleteStatusDetails?.Reason == ResponseIncompleteStatusReason.MaxOutputTokens)
                {
                    return TransportFinishReason.Length;
                }
                return TransportFinishReason.Incomplete;
            default:
                return sawFunctionCall ? TransportFinishReason.ToolCalls : TransportFinishReason.Stop;
        }
    }

    /// <summary>原始 error JSON 进异常消息的上限：够看清原因，又不至于把一整个响应糊进气泡。</summary>
    private const int RawErrorCharBudget = 2000;

    /// <summary>
    /// 把 <c>response.failed</c> 里能拿到的一切都写进异常消息。
    ///
    /// 这里曾经只拼一个 response id，于是气泡和日志里都只剩
    /// 「Responses request failed (response gen-…)」——供应商明明在 <c>error</c> 里写了原因，
    /// 是我们自己丢掉的。代价不止是看不见：ProviderErrorClassifier 按消息文本匹配关键字
    /// （rate limit / context length / timeout…），消息里没有原文就只能落到兜底的
    /// ProviderRawError，一次限流和一次上游 5xx 长得一模一样，也就没有任何一条提示能对症。
    /// </summary>
    private static string DescribeFailedResponse(ResponseResult response)
    {
        var parts = new List<string>();
        if (response.Status is { } status)
        {
            parts.Add($"status={status}");
        }

        if (response.Error is { } error)
        {
            // code= 的写法是给 ProviderErrorClassifier 的 ExtractCode 正则认的。
            var code = error.Code.ToString();
            if (!string.IsNullOrWhiteSpace(code)) parts.Add($"code={code}");
            if (!string.IsNullOrWhiteSpace(error.Param)) parts.Add($"param={error.Param}");
            if (!string.IsNullOrWhiteSpace(error.Message)) parts.Add(error.Message);
        }
        else if (TryReadRawError(response) is { } raw)
        {
            parts.Add($"raw_error={raw}");
        }
        else
        {
            parts.Add("the provider reported response.failed without an error payload");
        }

        if (response.IncompleteStatusDetails?.Reason is { } reason)
        {
            parts.Add($"incomplete_reason={reason}");
        }

        return $"Responses request failed (response {response.Id}): {string.Join(", ", parts)}";
    }

    /// <summary>
    /// SDK 的 <c>Error</c> 为空时的兜底：把响应原样序列化回 JSON，只取 <c>error</c> 这一段。
    /// 第三方端点（OpenRouter 等）常把上游错误塞成 SDK 模型不认识的形状，
    /// 回一段原始 JSON 也远好过回一个只有 id 的空壳。
    /// </summary>
    private static string? TryReadRawError(ResponseResult response)
    {
        try
        {
            using var document = JsonDocument.Parse(ModelReaderWriter.Write(response).ToMemory());
            if (!document.RootElement.TryGetProperty("error", out var error)
                || error.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return null;
            }

            var raw = error.GetRawText();
            return raw.Length <= RawErrorCharBudget ? raw : raw[..RawErrorCharBudget] + "…";
        }
        catch (Exception ex)
        {
            // 兜底路径失败没有后果：调用方会退回「没有 error 载荷」的说法。
            Log.Debug(ex, "Could not re-serialize a failed response to recover its raw error payload");
            return null;
        }
    }

    /// <summary>
    /// 端点不支持 /responses 的判定：404/405 直判；400 或协议层错误仅在错误文案
    /// 明确暗示 /responses 不存在时判定（避免吞掉真实业务错误，如模型不存在）。
    /// </summary>
    private static bool IsEndpointUnsupported(Exception ex)
    {
        // 流已经建立过了，端点支持与否没有讨论余地——见 ResponsesStreamFailureException。
        if (ex is ResponsesStreamFailureException)
        {
            return false;
        }

        if (ex is ClientResultException { Status: 404 or 405 })
        {
            return true;
        }

        var message = ex.Message ?? string.Empty;
        var lowered = message.ToLowerInvariant();
        if (!lowered.Contains("/responses", StringComparison.Ordinal)
            && !lowered.Contains("responses api", StringComparison.Ordinal))
        {
            return false;
        }

        return lowered.Contains("not found", StringComparison.Ordinal)
               || lowered.Contains("not supported", StringComparison.Ordinal)
               || lowered.Contains("404", StringComparison.Ordinal)
               || lowered.Contains("405", StringComparison.Ordinal)
               || lowered.Contains("not implement", StringComparison.Ordinal);
    }

    private static string? TextOrNull(BinaryData data)
    {
        if (data == null || data.ToMemory().Length == 0)
        {
            return null;
        }

        var text = data.ToString();
        return string.IsNullOrEmpty(text) ? null : text;
    }
}

#pragma warning restore OPENAI001
