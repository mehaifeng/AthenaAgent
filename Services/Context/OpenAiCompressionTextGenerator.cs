using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using OpenAI.Chat;
using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

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
