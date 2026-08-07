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

namespace Athena.UI.Services.Context;

/// <summary>
/// 非流式调用点的 Responses API 通用映射。
/// 约定：
/// - 协议跟随 <see cref="EffectiveOpenAiModel.Protocol"/>；Auto 在非主对话角色一律走 Chat Completions
///   （主对话的 Auto 判定才接模型元数据，见 ResponsesProtocolResolver）。
/// - 响应文本读取统一走 GetFirstOutputText / GetConcatenatedOutputText，等价于 chat 的
///   Content[0].Text / string.Concat(Content.Select(Text))。
/// </summary>
public static class ResponsesCallHelpers
{
    /// <summary>该角色是否使用 Responses 传输（仅显式配置生效；Auto 视为 Chat Completions）。</summary>
    public static bool ShouldUseResponses(EffectiveOpenAiModel effective)
        => effective.Protocol == ProviderProtocol.Responses;

    public static ResponsesClient CreateResponsesClient(EffectiveOpenAiModel effective, int timeoutSeconds)
    {
        var options = new ResponsesClientOptions
        {
            RetryPolicy = new ClientRetryPolicy(OpenAiClientOptionsFactory.DefaultMaxRetries),
            NetworkTimeout = TimeSpan.FromSeconds(OpenAiClientOptionsFactory.NormalizeTimeoutSeconds(timeoutSeconds))
        };
        if (!string.IsNullOrWhiteSpace(effective.BaseUrl))
        {
            options.Endpoint = new Uri(effective.BaseUrl.Trim());
        }
        return new ResponsesClient(new ApiKeyCredential(effective.ApiKey), options);
    }

    /// <summary>构造非流式请求选项：system 提示进 Instructions；工具进 FunctionTool；json_object 走 TextOptions。</summary>
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
