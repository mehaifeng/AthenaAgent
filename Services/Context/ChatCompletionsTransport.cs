using Athena.UI.Models;
using OpenAI.Chat;
using System;
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
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var stream = runtime.ChatClient.CompleteChatStreamingAsync(messages, runtime.ChatOptions, cancellationToken);
        await foreach (var update in stream)
        {
            // 供应商回报的真实 token 用量随最后一个 chunk 到达（SDK 已自动开启 include_usage）。
            if (update.Usage != null)
            {
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
                yield return new NormalizedUpdate(ReasoningText: reasoningChunk);
            }
#pragma warning restore SCME0001

            foreach (var contentPart in update.ContentUpdate)
            {
                if (!string.IsNullOrEmpty(contentPart.Text))
                {
                    yield return new NormalizedUpdate(Text: contentPart.Text);
                }
            }

            foreach (var toolCallUpdate in update.ToolCallUpdates)
            {
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
                yield return new NormalizedUpdate(FinishReason: MapFinishReason(update.FinishReason.Value));
            }
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
