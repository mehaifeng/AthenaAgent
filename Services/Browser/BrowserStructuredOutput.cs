using Athena.UI.Models;
using Athena.UI.Services.Context;
using OpenAI.Chat;
using OpenAI.Responses;
using Serilog;
using System;
using System.ClientModel;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

// OpenAI SDK Experimental 面（OPENAI001）在浏览器协商层集中使用。
#pragma warning disable OPENAI001

namespace Athena.UI.Services.Browser;

/// <summary>
/// 浏览器 Agent 模型调用的结构化输出协商层（Phase 1/3）。
/// 依据配置的 <see cref="BrowserStructuredOutputMode"/> 决定是否附带
/// <c>response_format=json_object</c>：Auto 档乐观地尝试该参数，若后端明确拒绝（400/404/422
/// 且错误信息点名 response_format/json）则自动降级为纯 prompt 约束并按 (BaseUrl, Model) 记忆，
/// 避免每次调用重复探测。json_object 只保证"是合法 JSON"，字段 shape 仍交由
/// <see cref="JsonExtraction"/> 与解析层的容错处理，二者互补。
/// </summary>
internal static class BrowserStructuredOutput
{
    // 记录某个 (BaseUrl, Model) 是否已被判定为不支持 response_format=json_object。
    private static readonly ConcurrentDictionary<string, bool> _jsonObjectUnsupported = new();

    /// <summary>
    /// 发起一次补全调用，按协商结果附带 response_format，并在 Auto 档遭拒时降级重试一次。
    /// </summary>
    public static async Task<ChatCompletion> CompleteAsync(
        ChatClient chatClient,
        IEnumerable<OpenAI.Chat.ChatMessage> messages,
        ChatCompletionOptions options,
        BrowserStructuredOutputMode mode,
        string baseUrl,
        string model,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var useJsonObject = ShouldUseJsonObject(mode, baseUrl, model);
        if (useJsonObject)
        {
            options.ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat();
        }

        try
        {
            var result = await chatClient.CompleteChatAsync(messages, options, cancellationToken);
            return result.Value;
        }
        catch (Exception ex) when (useJsonObject
            && mode == BrowserStructuredOutputMode.Auto
            && IsResponseFormatRejection(ex))
        {
            logger.Warning(
                ex,
                "Browser model rejected response_format=json_object; downgrading to prompt-only and remembering. Model={Model}, BaseUrl={BaseUrl}",
                model,
                baseUrl);
            _jsonObjectUnsupported[Key(baseUrl, model)] = true;
            options.ResponseFormat = null;
            var result = await chatClient.CompleteChatAsync(messages, options, cancellationToken);
            return result.Value;
        }
    }

    /// <summary>
    /// Responses 传输下的等价调用：同一套 json_object 协商（TextOptions + 400/404/422 降级重试）。
    /// messages 不含 system（system 提示经 <paramref name="systemPrompt"/> 进 Instructions）。
    /// </summary>
    public static async Task<ResponseResult> CompleteResponsesAsync(
        EffectiveBrowserAgentConfig effective,
        IEnumerable<OpenAI.Chat.ChatMessage> messages,
        string systemPrompt,
        float temperature,
        int maxOutputTokens,
        BrowserStructuredOutputMode mode,
        int timeoutSeconds,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var model = effective.ToEffectiveOpenAiModel();
        var useJsonObject = ShouldUseJsonObject(mode, model.BaseUrl, model.Model);
        var responses = ResponsesCallHelpers.CreateResponsesClient(model, timeoutSeconds);
        var options = ResponsesCallHelpers.CreateOptions(
            model,
            systemPrompt,
            temperature,
            maxOutputTokens,
            jsonObjectFormat: useJsonObject);
        ResponsesCallHelpers.AddInputItems(options, messages);

        try
        {
            return (await responses.CreateResponseAsync(options, cancellationToken)).Value;
        }
        catch (Exception ex) when (useJsonObject
            && mode == BrowserStructuredOutputMode.Auto
            && IsResponseFormatRejection(ex))
        {
            logger.Warning(
                ex,
                "Browser model rejected response_format=json_object (responses); downgrading to prompt-only and remembering. Model={Model}, BaseUrl={BaseUrl}",
                model.Model,
                model.BaseUrl);
            _jsonObjectUnsupported[Key(model.BaseUrl, model.Model)] = true;
            options.TextOptions = null;
            return (await responses.CreateResponseAsync(options, cancellationToken)).Value;
        }
    }

    /// <summary>Responses 传输的文本化调用：messages 首条 system 进 Instructions，返回首个 output_text。</summary>
    public static async Task<string> CompleteResponsesTextAsync(
        EffectiveBrowserAgentConfig effective,
        IReadOnlyList<OpenAI.Chat.ChatMessage> messages,
        float temperature,
        int maxOutputTokens,
        BrowserStructuredOutputMode mode,
        int timeoutSeconds,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var system = messages.FirstOrDefault() is SystemChatMessage systemMessage
            ? string.Concat(systemMessage.Content
                .Where(part => part.Kind == ChatMessageContentPartKind.Text)
                .Select(part => part.Text))
            : string.Empty;
        var result = await CompleteResponsesAsync(
            effective,
            messages.Skip(1),
            system,
            temperature,
            maxOutputTokens,
            mode,
            timeoutSeconds,
            logger,
            cancellationToken);
        return ResponsesCallHelpers.GetFirstOutputText(result) ?? string.Empty;
    }

    private static bool ShouldUseJsonObject(BrowserStructuredOutputMode mode, string baseUrl, string model)
    {
        return mode switch
        {
            BrowserStructuredOutputMode.PromptOnly => false,
            BrowserStructuredOutputMode.JsonObject => true,
            // Auto：默认乐观启用，除非该后端/模型此前已被判定不支持。
            _ => !_jsonObjectUnsupported.GetValueOrDefault(Key(baseUrl, model))
        };
    }

    /// <summary>
    /// 仅当明确是"后端不认 response_format 参数"时才判为可降级，避免把真实的鉴权/网络/视觉
    /// 不支持等错误误判为格式问题而掩盖掉真实故障。
    /// </summary>
    private static bool IsResponseFormatRejection(Exception ex)
    {
        if (ex is not ClientResultException cre)
        {
            return false;
        }

        if (cre.Status is not (400 or 404 or 422))
        {
            return false;
        }

        var message = cre.Message ?? string.Empty;
        return message.Contains("response_format", StringComparison.OrdinalIgnoreCase)
            || message.Contains("json_object", StringComparison.OrdinalIgnoreCase)
            || message.Contains("json mode", StringComparison.OrdinalIgnoreCase);
    }

    private static string Key(string? baseUrl, string? model) =>
        string.Concat(baseUrl?.Trim().ToLowerInvariant(), "|", model?.Trim().ToLowerInvariant());
}
