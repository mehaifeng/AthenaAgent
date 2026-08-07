// Responses API 传输：本文件全部类型均为 OpenAI SDK 的 Experimental(OPENAI001) 面。
#pragma warning disable OPENAI001

using Athena.UI.Models;
using Athena.UI.Services.Protocol;
using OpenAI.Chat;
using OpenAI.Responses;
using Serilog;
using System;
using System.ClientModel;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
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
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // 主枚举：responses 协议；端点不支持时切换到 chat 重发本轮。
        // 注意迭代器约束：yield 不能在带 catch 的 try 里，因此捕获只包住 MoveNextAsync。
        var current = EnumerateResponses(runtime, messages, cancellationToken).GetAsyncEnumerator(cancellationToken);
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
                        .StreamUpdatesAsync(runtime, messages, cancellationToken)
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
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var options = BuildOptions(runtime, messages);
        var stream = runtime.ResponsesClient!.CreateResponseStreamingAsync(options, cancellationToken);

        var itemIdToIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var nextToolIndex = 0;
        var sawFunctionCall = false;
        var sawFullReasoningText = false;

        await foreach (var update in stream)
        {
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
                    if (completed.Response.Usage is { } usage)
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
                    yield return new NormalizedUpdate(FinishReason: MapStatus(completed.Response, sawFunctionCall));
                    break;
                case StreamingResponseFailedUpdate failed:
                    throw new ClientResultException(
                        $"Responses request failed (response {failed.Response.Id})", response: null, innerException: null);
                case StreamingResponseErrorUpdate error:
                    throw new ClientResultException(
                        $"Responses stream error: {error.Message} ({error.Code})", response: null, innerException: null);
            }
        }
    }

    private static CreateResponseOptions BuildOptions(
        EffectiveRequestRuntimeSnapshot runtime,
        IReadOnlyList<OpenAI.Chat.ChatMessage> messages)
    {
        var options = new CreateResponseOptions
        {
            Model = runtime.MainModel.Model,
            Instructions = BuildInstructions(messages),
            Temperature = runtime.ChatOptions.Temperature,
            TopP = runtime.ChatOptions.TopP,
            MaxOutputTokenCount = runtime.ChatOptions.MaxOutputTokenCount,
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

    /// <summary>
    /// 端点不支持 /responses 的判定：404/405 直判；400 或协议层错误仅在错误文案
    /// 明确暗示 /responses 不存在时判定（避免吞掉真实业务错误，如模型不存在）。
    /// </summary>
    private static bool IsEndpointUnsupported(Exception ex)
    {
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
