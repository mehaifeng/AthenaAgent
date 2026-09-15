using Athena.UI.Models;
using OpenAI.Chat;
using System;
using System.ClientModel.Primitives;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;

namespace Athena.UI.Services.Context;

/// <summary>
/// Chat Completions 传输实现：包住 <see cref="EffectiveRequestRuntimeSnapshot.ChatClient"/> 的流式调用，
/// 并把增量（正文 / 推理文本 / 工具参数 / usage / 完成原因）归一化为 <see cref="NormalizedUpdate"/>。
/// 行为与重构前的主对话流式环逐字节一致（回归基线）。
/// </summary>
public sealed class ChatCompletionsTransport : ICompletionTransport
{
    public static ChatCompletionsTransport Instance { get; } = new();

    public string TransportId => "chat";

    public async IAsyncEnumerable<NormalizedUpdate> StreamUpdatesAsync(
        EffectiveRequestRuntimeSnapshot runtime,
        IReadOnlyList<OpenAI.Chat.ChatMessage> messages,
        int maxOutputTokens,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var stream = runtime.ChatClient.CompleteChatStreamingAsync(
            messages, WithMaxOutputTokens(runtime.ChatOptions, maxOutputTokens), cancellationToken);
        var sawAnyResponseData = false;
        await using var enumerator = stream.GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            StreamingChatCompletionUpdate update;
            try
            {
                if (!await enumerator.MoveNextAsync().ConfigureAwait(false)) break;
                update = enumerator.Current;
            }
            catch (ArgumentOutOfRangeException ex) when (ex.ParamName == "value" && ex.ActualValue is string unknownValue)
            {
                // 兜底层：SDK 的闭集枚举遇到未知取值会在反序列化时直接抛，异常从 MoveNextAsync 里飞出来。
                // 正常路径上 ProviderStreamSanitizer 已经把 finish_reason 的未知取值改写掉了，走到这里
                // 说明命中的是别的字段（或响应体不是 SSE 形状）——仍然是「上游给了本 SDK 认不出的东西」，
                // 而不该以一句 SDK 断言的形式糊到用户脸上。
                throw new ProviderStreamInterruptedException(
                    $"The provider returned a value this SDK does not recognize (\"{unknownValue}\"), and the response stream ended there.",
                    unknownValue,
                    innerException: ex);
            }

            // 供应商回报的真实 token 用量随最后一个 chunk 到达（SDK 已自动开启 include_usage）。
            if (update.Usage != null)
            {
                sawAnyResponseData = true;
                var usage = update.Usage;
                yield return new NormalizedUpdate(
                    Usage: new TokenUsageSnapshot(
                        usage.InputTokenCount,
                        usage.InputTokenDetails?.CachedTokenCount ?? 0,
                        usage.OutputTokenCount,
                        usage.TotalTokenCount),
                    ReasoningTokenCount: usage.OutputTokenDetails?.ReasoningTokenCount,
                    InputModalityUsage: ExtractInputModalityUsage(update, usage));
            }

            // 第三方 OpenAI 兼容端点的思考文本扩展字段（DeepSeek 等）；官方端点不返回。
#pragma warning disable SCME0001
            if (update.Patch.TryGetValue("$.choices[0].delta.reasoning_content"u8, out string? reasoningChunk)
                && reasoningChunk != null)
            {
                sawAnyResponseData = true;
                yield return new NormalizedUpdate(ReasoningText: reasoningChunk);
            }
#pragma warning restore SCME0001

            foreach (var contentPart in update.ContentUpdate)
            {
                if (!string.IsNullOrEmpty(contentPart.Text))
                {
                    sawAnyResponseData = true;
                    yield return new NormalizedUpdate(Text: contentPart.Text);
                }
            }

            foreach (var toolCallUpdate in update.ToolCallUpdates)
            {
                sawAnyResponseData = true;
                string? argsText = null;
                if (toolCallUpdate.FunctionArgumentsUpdate != null
                    && toolCallUpdate.FunctionArgumentsUpdate.ToMemory().Length > 0)
                {
                    try
                    {
                        argsText = toolCallUpdate.FunctionArgumentsUpdate.ToString();
                    }
                    catch (ArgumentNullException)
                    {
                    }
                }

                yield return new NormalizedUpdate(
                    ToolCallIndex: toolCallUpdate.Index,
                    ToolCallId: toolCallUpdate.ToolCallId,
                    ToolCallName: toolCallUpdate.FunctionName,
                    ToolCallArgumentsDelta: string.IsNullOrEmpty(argsText) ? null : argsText);
            }

            if (update.FinishReason != null)
            {
                sawAnyResponseData = true;
                yield return new NormalizedUpdate(FinishReason: MapFinishReason(update.FinishReason.Value));
            }

            // 被 ProviderStreamSanitizer 摘下来的原始 finish_reason（OpenRouter 的上游中途失败是 "error"）。
            // 本轮回复就停在这里：把它当成正常收尾，界面上只会剩半句话而没有任何解释。
            if (TryReadRawFinishReason(update) is { } rawFinishReason)
            {
                throw new ProviderStreamInterruptedException(
                    DescribeProviderInterruption(update, rawFinishReason),
                    rawFinishReason,
                    TryReadPatchString(update, "$.error.code"));
            }
        }

        // 部分 OpenAI 兼容端点会用 HTTP 200 + 空流（或携带 error 字段的 SSE chunk）回应上游失败，
        // 例如 OpenRouter 在图片解码失败时返回 choices=[] + error，而 OpenAI SDK 会静默丢弃该
        // error chunk、不抛异常。若不拦截，主对话会把「异常空响应」当成成功空回复，什么都不输出。
        if (!sawAnyResponseData)
        {
            throw new InvalidOperationException(
                "Provider returned an empty streaming response (no content, no finish reason, and no usage). "
                + "The upstream may have rejected the request; for requests with images, this usually means "
                + "the provider could not decode an attached image.");
        }
    }

    public void AppendAssistantWithTools(
        List<OpenAI.Chat.ChatMessage> messages,
        string content,
        IReadOnlyList<ToolCallInfo> toolCalls,
        string? reasoningContent)
    {
        var message = new AssistantChatMessage(content ?? "");
        foreach (var toolCall in toolCalls)
        {
            message.ToolCalls.Add(ChatToolCall.CreateFunctionToolCall(
                toolCall.Id,
                toolCall.FunctionName,
                BinaryData.FromString(toolCall.Arguments)));
        }

        ApplyReasoningContent(message, reasoningContent);
        messages.Add(message);
    }

    public void AppendToolResult(List<OpenAI.Chat.ChatMessage> messages, string callId, string resultJson)
        => messages.Add(new ToolChatMessage(callId, resultJson));

    public bool IsToolCallArgumentsComplete(string? arguments)
    {
        // 空串 / "{}" 视为合法（无参工具的常见输出）；只有能完整解析的对象/数组才算完整。
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return true;
        }

        try
        {
            using var _ = JsonDocument.Parse(arguments);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// 逐请求的输出上限：快照里的 options 是整个回合共用的一份，绝不能就地改写
    /// （下一轮、以及并发读它的估算/指纹路径都会跟着变）。这里按 SDK 的持久化模型做一份
    /// 完整副本——温度、TopP、工具、推理强度乃至 Patch 扩展字段都会原样带过来——只改这一个值。
    /// 值与快照一致时直接复用，省掉这次拷贝。
    /// </summary>
    private static ChatCompletionOptions WithMaxOutputTokens(ChatCompletionOptions options, int maxOutputTokens)
    {
        if (maxOutputTokens <= 0 || options.MaxOutputTokenCount == maxOutputTokens)
        {
            return options;
        }

        var copy = ModelReaderWriter.Read<ChatCompletionOptions>(ModelReaderWriter.Write(options));
        if (copy == null)
        {
            return options;
        }

        copy.MaxOutputTokenCount = maxOutputTokens;
        return copy;
    }

    /// <summary>把推理内容写回 chat 消息的 reasoning_content 扩展字段（DeepSeek 系校验要求回放）。</summary>
    internal static void ApplyReasoningContent(AssistantChatMessage message, string? reasoningContent)
    {
        if (reasoningContent == null)
        {
            return;
        }

#pragma warning disable SCME0001
        message.Patch.Set("$.reasoning_content"u8, reasoningContent);
#pragma warning restore SCME0001
    }

    /// <summary>
    /// 读回 <see cref="ProviderStreamSanitizer"/> 从 finish_reason 上摘下来的原始取值。
    /// SDK 把它不认识的字段原样留在 Patch 里，路径与 reasoning_content 同理。
    /// </summary>
    private static string? TryReadRawFinishReason(StreamingChatCompletionUpdate update)
    {
        var raw = TryReadPatchString(update, $"$.choices[0].{ProviderStreamSanitizer.RawFinishReasonProperty}");
        return string.IsNullOrWhiteSpace(raw) ? null : raw;
    }

    /// <summary>失败 chunk 里供应商自己的说法（OpenRouter 放在顶层 error 对象里），没有就给一句通用描述。</summary>
    private static string DescribeProviderInterruption(StreamingChatCompletionUpdate update, string rawFinishReason)
    {
        var providerMessage = TryReadPatchString(update, "$.error.message");
        return string.IsNullOrWhiteSpace(providerMessage)
            ? $"The provider ended the response stream with finish_reason=\"{rawFinishReason}\" and gave no further detail."
            : providerMessage!;
    }

    private static string? TryReadPatchString(StreamingChatCompletionUpdate update, string jsonPath)
    {
#pragma warning disable SCME0001
        try
        {
            return update.Patch.TryGetValue(System.Text.Encoding.UTF8.GetBytes(jsonPath), out string? value) ? value : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or JsonException)
        {
            // 该路径上是个非字符串（数字 error.code、对象形态的 error 等）。诊断信息缺一条无所谓，
            // 但不能因为读它而把一次本来说得清楚的中断变成另一个异常。
            return null;
        }
#pragma warning restore SCME0001
    }

    private static TransportFinishReason MapFinishReason(ChatFinishReason reason) => reason switch
    {
        ChatFinishReason.ToolCalls or ChatFinishReason.FunctionCall => TransportFinishReason.ToolCalls,
        ChatFinishReason.Length => TransportFinishReason.Length,
        ChatFinishReason.ContentFilter => TransportFinishReason.Incomplete,
        _ => TransportFinishReason.Stop
    };

    private static ProviderInputModalityUsage? ExtractInputModalityUsage(
        StreamingChatCompletionUpdate update,
        ChatTokenUsage usage)
    {
        long? audioTokens = usage.InputTokenDetails?.AudioTokenCount is > 0
            ? usage.InputTokenDetails.AudioTokenCount
            : null;
        var imageTokens = TryGetUsageDetail(update, "image_tokens");
        var textTokens = TryGetUsageDetail(update, "text_tokens");
        return audioTokens.HasValue || imageTokens.HasValue || textTokens.HasValue
            ? new ProviderInputModalityUsage(textTokens, imageTokens, audioTokens)
            : null;
    }

    private static long? TryGetUsageDetail(StreamingChatCompletionUpdate update, string fieldName)
    {
#pragma warning disable SCME0001
        var promptPath = System.Text.Encoding.UTF8.GetBytes($"$.usage.prompt_tokens_details.{fieldName}");
        if (update.Patch.TryGetValue(promptPath, out long promptValue) && promptValue >= 0)
            return promptValue;
        var inputPath = System.Text.Encoding.UTF8.GetBytes($"$.usage.input_tokens_details.{fieldName}");
        return update.Patch.TryGetValue(inputPath, out long inputValue) && inputValue >= 0
            ? inputValue
            : null;
#pragma warning restore SCME0001
    }
}
