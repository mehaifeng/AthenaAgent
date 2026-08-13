// Responses API 调用辅助：本文件全部类型均为 OpenAI SDK 的 Experimental(OPENAI001) 面。
#pragma warning disable OPENAI001

using Athena.UI.Models;
using OpenAI.Chat;
using OpenAI.Responses;
using System;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.Context;

/// <summary>
/// 主环之外（辅助角色）的 Responses API 通用映射。
/// 约定：
/// - 协议跟随 <see cref="EffectiveOpenAiModel.Protocol"/>；Auto 在非主对话角色一律走 Chat Completions
///   （主对话的 Auto 判定才接模型元数据，见 ResponsesProtocolResolver）。
/// - 非流式：<see cref="CreateOptions"/> 造请求，响应文本读取统一走 GetFirstOutputText /
///   GetConcatenatedOutputText，等价于 chat 的 Content[0].Text / string.Concat(Content.Select(Text))。
/// - 流式：一律经 <see cref="StreamOutputTextAsync"/>，不要自己写 CreateResponseStreamingAsync 循环——
///   SDK 对 StreamingEnabled 有双向断言，该旗标由读取器统一置位。
/// </summary>
public static class ResponsesCallHelpers
{
    private static readonly PipelineTransport CompatibilityTransport = CreateCompatibilityTransport();

    /// <summary>该角色是否使用 Responses 传输（仅显式配置生效；Auto 视为 Chat Completions）。</summary>
    public static bool ShouldUseResponses(EffectiveOpenAiModel effective)
        => effective.Protocol == ProviderProtocol.Responses;

    public static ResponsesClient CreateResponsesClient(EffectiveOpenAiModel effective, int timeoutSeconds)
    {
        var options = new ResponsesClientOptions
        {
            RetryPolicy = new ClientRetryPolicy(OpenAiClientOptionsFactory.DefaultMaxRetries),
            NetworkTimeout = TimeSpan.FromSeconds(OpenAiClientOptionsFactory.NormalizeTimeoutSeconds(timeoutSeconds)),
            Transport = CompatibilityTransport
        };
        if (!string.IsNullOrWhiteSpace(effective.BaseUrl))
        {
            options.Endpoint = new Uri(effective.BaseUrl.Trim());
        }
        return new ResponsesClient(new ApiKeyCredential(effective.ApiKey), options);
    }

    // Process-wide transport: the SDK clients share its connection pool for the app lifetime.
#pragma warning disable CA2000 // Ownership intentionally transfers through the handler chain to the static transport.
    private static PipelineTransport CreateCompatibilityTransport()
    {
        var networkHandler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip
                                     | DecompressionMethods.Deflate
                                     | DecompressionMethods.Brotli
        };
        var httpClient = new HttpClient(new ResponsesCompatibilityHandler(networkHandler))
        {
            // System.ClientModel owns the per-request network timeout; avoid a second,
            // conflicting HttpClient timeout for long-running Responses streams.
            Timeout = System.Threading.Timeout.InfiniteTimeSpan
        };
        return new HttpClientPipelineTransport(httpClient);
    }
#pragma warning restore CA2000

    /// <summary>
    /// 构造请求选项：system 提示进 Instructions；工具进 FunctionTool；json_object 走 TextOptions。
    /// 不设 StreamingEnabled——非流式调用点（CreateResponseAsync）要求它不为 true，
    /// 流式调用点的置位由 <see cref="StreamOutputTextAsync"/> 负责。
    /// </summary>
    public static CreateResponseOptions CreateOptions(
        EffectiveOpenAiModel effective,
        string? instructions,
        float? temperature,
        int? maxOutputTokens,
        IReadOnlyList<ChatTool>? tools = null,
        bool jsonObjectFormat = false)
    {
        var options = new CreateResponseOptions
        {
            Model = effective.Model,
            Instructions = instructions ?? string.Empty,
            Temperature = temperature,
            TopP = null,
            MaxOutputTokenCount = maxOutputTokens,
            // 无状态：Athena 自管上下文，非流式单发也不使用服务端 store。
            StoredOutputEnabled = false,
        };
        if (tools != null)
        {
            foreach (var tool in tools)
            {
                options.Tools.Add(new FunctionTool(
                    tool.FunctionName,
                    tool.FunctionParameters,
                    tool.FunctionSchemaIsStrict)
                {
                    FunctionDescription = tool.FunctionDescription ?? string.Empty,
                });
            }
        }
        if (jsonObjectFormat)
        {
            options.TextOptions = new ResponseTextOptions
            {
                TextFormat = ResponseTextFormat.CreateJsonObjectFormat(),
            };
        }
        if (effective.Effort != ReasoningEffort.Auto)
        {
            options.ReasoningOptions = new ResponseReasoningOptions
            {
                ReasoningEffortLevel = MapEffort(effective.Effort)
            };
        }
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

    /// <summary>
    /// 流式读取一次 Responses 调用产出的 output_text。
    /// StreamingEnabled 由本方法置位：SDK 对这面旗有双向断言（非流式选项进流式方法直接抛
    /// InvalidOperationException），把它交给调用方去记，只会得到一个运行期才暴露的失败——
    /// 压缩改流式时复用了非流式工厂，正是这样让 Responses 端点上的压缩全线静默失效。
    /// 服务端 failed/error 事件按异常抛出：静默返回空串会让上层把「供应商拒绝」误报成「模型没输出」。
    /// </summary>
    public static async Task<string> StreamOutputTextAsync(
        ResponsesClient client,
        CreateResponseOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        options.StreamingEnabled = true;
        var builder = new StringBuilder();
        ResponseResult? completed = null;
        // ConfigureAwait(false)：整条 SSE 的续体（含每个事件的 JSON 反序列化）默认会回到调用线程，
        // 而辅助角色多半从 UI 线程发起。一次几千事件的摘要流因此把界面线程当成解析线程用。
        await foreach (var update in client
                           .CreateResponseStreamingAsync(options, cancellationToken)
                           .ConfigureAwait(false))
        {
            switch (update)
            {
                case StreamingResponseOutputTextDeltaUpdate delta:
                    builder.Append(delta.Delta);
                    break;
                case StreamingResponseCompletedUpdate done:
                    completed = done.Response;
                    break;
                case StreamingResponseFailedUpdate failed:
                    throw new ClientResultException(
                        $"Responses request failed (response {failed.Response.Id})", response: null, innerException: null);
                case StreamingResponseErrorUpdate error:
                    throw new ClientResultException(
                        $"Responses stream error: {error.Message} ({error.Code})", response: null, innerException: null);
            }
        }

        if (builder.Length > 0) return builder.ToString();
        if (completed == null) return string.Empty;

        // 有端点只在终局事件里给全文，不发增量：先按最终响应兜底取一次。
        var finalText = GetConcatenatedOutputText(completed);
        if (!string.IsNullOrEmpty(finalText)) return finalText;

        // 一个字都没有还要说清楚为什么。推理模型把输出预算全花在推理上就是这个形状，
        // 报成「模型返回了空摘要」会让人去换模型，而真正该调的是输出预算或推理强度。
        if (completed.Status == ResponseStatus.Incomplete)
        {
            var reason = completed.IncompleteStatusDetails?.Reason.ToString() ?? "unknown";
            throw new ClientResultException(
                $"The response ended incomplete ({reason}) before any text was produced; "
                + "the whole output budget was consumed (reasoning models spend it on reasoning first).",
                response: null,
                innerException: null);
        }
        return string.Empty;
    }

    /// <summary>把 chat 形状的消息列表转换为 input items（system 并入 Instructions 由调用方负责）。</summary>
    public static void AddInputItems(CreateResponseOptions options, IEnumerable<OpenAI.Chat.ChatMessage> messages)
    {
        foreach (var item in BuildInputItems(messages))
        {
            options.InputItems.Add(item);
        }
    }

    /// <summary>与 chat Content[0].Text 等价：取输出里第一条 assistant 消息的第一段 output_text。</summary>
    public static string? GetFirstOutputText(ResponseResult response)
    {
        var firstMessage = response.OutputItems.OfType<MessageResponseItem>().FirstOrDefault();
        if (firstMessage == null)
        {
            return null;
        }
        var firstText = firstMessage.Content.FirstOrDefault(part => part.Kind == ResponseContentPartKind.OutputText);
        return firstText?.Text;
    }

    /// <summary>与 string.Concat(Content.Select(Text)) 等价：拼接所有 assistant 消息的 output_text。</summary>
    public static string GetConcatenatedOutputText(ResponseResult response)
        => string.Concat(response.OutputItems
            .OfType<MessageResponseItem>()
            .SelectMany(message => message.Content)
            .Where(part => part.Kind == ResponseContentPartKind.OutputText)
            .Select(part => part.Text));

    /// <summary>与 ChatCompletion.ToolCalls 等价：提取全部 function_call 输出项。</summary>
    public static IReadOnlyList<ToolCallInfo> GetFunctionCalls(ResponseResult response)
        => response.OutputItems
            .OfType<FunctionCallResponseItem>()
            .Select(call => new ToolCallInfo(
                call.CallId ?? string.Empty,
                call.FunctionName ?? string.Empty,
                call.FunctionArguments?.ToString() ?? string.Empty))
            .ToList();

    /// <summary>把一个调用结果追加为 FunctionCallOutputResponseItem（等价于 ToolChatMessage 回填）。</summary>
    public static ResponseItem CreateFunctionCallOutputItem(string callId, string resultJson)
        => ResponseItem.CreateFunctionCallOutputItem(callId, resultJson);

    internal static List<ResponseItem> BuildInputItems(IEnumerable<OpenAI.Chat.ChatMessage> messages)
    {
        var items = new List<ResponseItem>();
        foreach (var message in messages)
        {
            switch (message)
            {
                case SystemChatMessage:
                    // 已并入 Instructions。
                    break;
                case UserChatMessage user:
                    items.Add(ResponseItem.CreateUserMessageItem(BuildContentParts(user.Content)));
                    break;
                case AssistantChatMessage assistant:
                    var text = string.Concat(assistant.Content
                        .Where(part => part.Kind == ChatMessageContentPartKind.Text)
                        .Select(part => part.Text));
                    items.Add(ResponseItem.CreateAssistantMessageItem(text));
                    foreach (var call in assistant.ToolCalls)
                    {
                        items.Add(ResponseItem.CreateFunctionCallItem(
                            call.Id,
                            call.FunctionName,
                            call.FunctionArguments));
                    }
                    break;
                case ToolChatMessage tool:
                    items.Add(ResponseItem.CreateFunctionCallOutputItem(
                        tool.ToolCallId,
                        string.Concat(tool.Content
                            .Where(part => part.Kind == ChatMessageContentPartKind.Text)
                            .Select(part => part.Text))));
                    break;
            }
        }
        return items;
    }

    internal static List<ResponseContentPart> BuildContentParts(IEnumerable<ChatMessageContentPart> parts)
    {
        var list = new List<ResponseContentPart>();
        foreach (var part in parts)
        {
            switch (part.Kind)
            {
                case ChatMessageContentPartKind.Text when !string.IsNullOrEmpty(part.Text):
                    list.Add(ResponseContentPart.CreateInputTextPart(part.Text));
                    break;
                case ChatMessageContentPartKind.Image when part.ImageBytes != null:
                    // SDK 的 BinaryData 重载要求 MediaType 非空；chat 侧的字节不带媒体类型时补一个。
                    var imageBytes = part.ImageBytes.MediaType is { Length: > 0 }
                        ? part.ImageBytes
                        : BinaryData.FromBytes(part.ImageBytes.ToArray(), part.ImageBytesMediaType ?? "image/png");
                    list.Add(ResponseContentPart.CreateInputImagePart(imageBytes, ResponseImageDetailLevel.Auto));
                    break;
                case ChatMessageContentPartKind.Image when part.ImageUri != null:
                    list.Add(ResponseContentPart.CreateInputImagePart(part.ImageUri, ResponseImageDetailLevel.Auto));
                    break;
            }
        }
        return list;
    }
}
