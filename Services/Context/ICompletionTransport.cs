using Athena.UI.Models;
using System.Collections.Generic;
using System.Threading;

namespace Athena.UI.Services.Context;

/// <summary>
/// 一次请求的传输无关输入：由各 transport 自行把内部消息形状翻译为协议请求，并把协议事件流
/// 归一化为 <see cref="NormalizedUpdate"/>。主对话流式环（ProcessStreamAsync）只消费该形状。
/// 实现：ChatCompletionsTransport / ResponsesTransport。
/// </summary>
public interface ICompletionTransport
{
    /// <summary>传输标识，进入校准分桶与执行策略身份（"chat" / "responses"）。</summary>
    string TransportId { get; }

    /// <summary>
    /// 开流并把协议事件流归一化为内部增量序列。messages 是主环的规范请求形状
    /// （List&lt;ChatMessage&gt;）；Responses 实现在此处转换为 input items。
    /// </summary>
    IAsyncEnumerable<NormalizedUpdate> StreamUpdatesAsync(
        EffectiveRequestRuntimeSnapshot runtime,
        IReadOnlyList<OpenAI.Chat.ChatMessage> messages,
        CancellationToken cancellationToken);

    /// <summary>把「带工具调用的助手消息」追加进请求输入。</summary>
    void AppendAssistantWithTools(
        List<OpenAI.Chat.ChatMessage> messages,
        string content,
        IReadOnlyList<ToolCallInfo> toolCalls,
        string? reasoningContent);

    /// <summary>把工具结果追加进请求输入。</summary>
    void AppendToolResult(List<OpenAI.Chat.ChatMessage> messages, string callId, string resultJson);

    /// <summary>参数 JSON 是否完整（chat：解析校验；responses：服务端 status 权威，恒 true）。</summary>
    bool IsToolCallArgumentsComplete(string? arguments);
}

/// <summary>归一化流增量：主环只认这个形状，传输实现负责把各自的事件流翻译进来。</summary>
public readonly record struct NormalizedUpdate(
    string? Text = null,
    string? ReasoningText = null,
    int? ToolCallIndex = null,
    string? ToolCallId = null,
    string? ToolCallName = null,
    string? ToolCallArgumentsDelta = null,
    bool? ToolCallIncomplete = null,
    TransportFinishReason? FinishReason = null,
    TokenUsageSnapshot? Usage = null,
    int? ReasoningTokenCount = null,
    ProviderInputModalityUsage? InputModalityUsage = null);

public enum TransportFinishReason
{
    Stop,
    ToolCalls,
    Length,
    Incomplete,
    Error
}

/// <summary>工具调用信息（传输无关；主环与压缩物共用）。</summary>
public sealed record ToolCallInfo(string Id, string FunctionName, string Arguments);
