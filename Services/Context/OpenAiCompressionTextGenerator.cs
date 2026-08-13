using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using OpenAI.Chat;
using OpenAI.Responses;
using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
// OpenAI SDK Experimental 面（OPENAI001）：本文件直接使用 Responses 类型。
#pragma warning disable OPENAI001

namespace Athena.UI.Services.Context;

public sealed class OpenAiCompressionTextGenerator : ICompressionTextGenerator
{
    private readonly OpenAiModelRuntimeFactory _modelFactory;

    public OpenAiCompressionTextGenerator(OpenAiModelRuntimeFactory modelFactory)
    {
        _modelFactory = modelFactory;
    }

    public string ModelFingerprint
    {
        get
        {
            var model = _modelFactory.Resolve(AiModelRole.ContextCompression);
            var material = string.Join('\u001f', model.ProviderPreset, model.BaseUrl, model.Model);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
        }
    }

    public async Task<string?> GenerateAsync(
        string systemPrompt,
        string userPrompt,
        int maxOutputTokens,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var effective = _modelFactory.Resolve(AiModelRole.ContextCompression);
        effective.ValidateChatRole(AiModelRole.ContextCompression);
        var outputTokens = Math.Min(maxOutputTokens, effective.MaxOutputTokens);
        // 压缩是本应用输出量最大的辅助调用，不能和 256 token 的审批共用一个扁平超时。
        var timeoutSeconds = OpenAiClientOptionsFactory.ResolveTimeoutSeconds(
            _modelFactory.TimeoutSeconds, outputTokens);
        var builder = new StringBuilder();

        // 必须走流式。非流式响应会被 SDK 整包缓冲，网络超时因此覆盖「生成完整篇摘要」的
        // 全过程——实测栈顶正是 HttpConnection.ChunkedEncodingReadStream.CopyToAsyncCore。
        // 流式下超时只覆盖首字节，长摘要不再被自身的生成耗时判定为网络故障。
        if (ResponsesCallHelpers.ShouldUseResponses(effective))
        {
            var responses = ResponsesCallHelpers.CreateResponsesClient(effective, timeoutSeconds);
            var options = ResponsesCallHelpers.CreateOptions(effective, systemPrompt, (float)effective.Temperature, outputTokens);
            options.InputItems.Add(ResponseItem.CreateUserMessageItem(userPrompt));
            await foreach (var update in responses.CreateResponseStreamingAsync(options, cancellationToken))
            {
                if (update is StreamingResponseOutputTextDeltaUpdate delta)
                    builder.Append(delta.Delta);
            }
            return builder.ToString().Trim();
        }

        var client = _modelFactory.CreateChatClient(AiModelRole.ContextCompression, timeoutSeconds);
        var stream = client.CompleteChatStreamingAsync(
            [new SystemChatMessage(systemPrompt), new UserChatMessage(userPrompt)],
            new ChatCompletionOptions
            {
                Temperature = (float)effective.Temperature,
                MaxOutputTokenCount = outputTokens
            },
            cancellationToken);
        await foreach (var update in stream)
        {
            foreach (var part in update.ContentUpdate)
                builder.Append(part.Text);
        }
        return builder.ToString().Trim();
    }
}
