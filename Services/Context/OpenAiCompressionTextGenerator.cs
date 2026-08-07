using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using OpenAI.Chat;
using OpenAI.Responses;
using System;
using System.Linq;
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
        if (ResponsesCallHelpers.ShouldUseResponses(effective))
        {
            var responses = ResponsesCallHelpers.CreateResponsesClient(effective, _modelFactory.TimeoutSeconds);
            var options = ResponsesCallHelpers.CreateOptions(effective, systemPrompt, (float)effective.Temperature, Math.Min(maxOutputTokens, effective.MaxOutputTokens));
            options.InputItems.Add(ResponseItem.CreateUserMessageItem(userPrompt));
            var result = await responses.CreateResponseAsync(options, cancellationToken);
            return ResponsesCallHelpers.GetConcatenatedOutputText(result.Value).Trim();
        }

        var client = _modelFactory.CreateChatClient(AiModelRole.ContextCompression);
        var response = await client.CompleteChatAsync(
            [new SystemChatMessage(systemPrompt), new UserChatMessage(userPrompt)],
            new ChatCompletionOptions
            {
                Temperature = (float)effective.Temperature,
                MaxOutputTokenCount = Math.Min(maxOutputTokens, effective.MaxOutputTokens)
            },
            cancellationToken);
        return string.Concat(response.Value.Content.Select(part => part.Text)).Trim();
    }
}
