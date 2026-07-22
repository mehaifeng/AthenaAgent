using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Embeddings;
using System;
using System.ClientModel;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services;

/// <summary>解析统一供应商配置，并为业务角色创建 OpenAI SDK 客户端。</summary>
public sealed class OpenAiModelRuntimeFactory
{
    private readonly IConfigService _configService;

    public OpenAiModelRuntimeFactory(IConfigService configService)
    {
        _configService = configService;
    }

    public EffectiveOpenAiModel Resolve(AiModelRole role)
        => Resolve(_configService.Load(), role);

    public static EffectiveOpenAiModel Resolve(AppConfig config, AiModelRole role)
    {
        var settings = role switch
        {
            AiModelRole.MainConversation => config.AiModels.MainConversation,
            AiModelRole.TitleGeneration => config.AiModels.TitleGeneration,
            AiModelRole.ContextCompression => config.AiModels.ContextCompression,
            AiModelRole.Approval => config.AiModels.Approval,
            AiModelRole.Embedding => config.AiModels.Embedding,
            AiModelRole.BrowserAgent => config.AiModels.BrowserAgent,
            AiModelRole.SubAgent => config.AiModels.SubAgent,
            AiModelRole.KnowledgeMaintenance => config.AiModels.KnowledgeMaintenance,
            AiModelRole.ImageRecognition => config.AiModels.ImageRecognition,
            _ => throw new ArgumentOutOfRangeException(nameof(role), role, null)
        };

        if (role == AiModelRole.Embedding && config.EmbeddingCredentialSource == EmbeddingConnectionSource.Custom)
        {
            return new EffectiveOpenAiModel(
                "embedding-custom",
                string.IsNullOrWhiteSpace(config.EmbeddingProvider) ? "Custom" : config.EmbeddingProvider,
                config.EmbeddingBaseUrl,
                config.EmbeddingApiKey,
                settings.Model,
                GetInternalTemperature(role),
                GetInternalMaxOutputTokens(role));
        }

        var provider = config.AiModels.Providers.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, settings.ProviderId, StringComparison.Ordinal));
        if (provider == null)
        {
            throw new InvalidOperationException($"Provider for model role '{role}' is not configured.");
        }

        return new EffectiveOpenAiModel(
            provider.ProviderPreset,
            provider.DisplayName,
            provider.BaseUrl,
            provider.ApiKey,
            settings.Model,
            GetInternalTemperature(role),
            GetInternalMaxOutputTokens(role));
    }

    private static double GetInternalTemperature(AiModelRole role) => role switch
    {
        AiModelRole.MainConversation => 0.7,
        AiModelRole.TitleGeneration => 0.2,
        AiModelRole.ContextCompression => 0.2,
        AiModelRole.Approval => 0,
        AiModelRole.Embedding => 0,
        AiModelRole.BrowserAgent => 0.2,
        AiModelRole.SubAgent => 0.3,
        AiModelRole.KnowledgeMaintenance => 0.1,
        AiModelRole.ImageRecognition => 0.1,
        _ => 0.3
    };

    private static int GetInternalMaxOutputTokens(AiModelRole role) => role switch
    {
        AiModelRole.Approval => 256,
        AiModelRole.Embedding => 0,
        AiModelRole.KnowledgeMaintenance => 4096,
        AiModelRole.ImageRecognition => 4096,
        _ => 16000
    };

    public ChatClient CreateChatClient(AiModelRole role)
    {
        return CreateChatClient(_configService.Load(), role);
    }

    public ChatClient CreateChatClient(AppConfig config, AiModelRole role)
    {
        var effective = Resolve(config, role);
        effective.ValidateChatRole(role);
        return CreateClient(effective, config.Timeout).GetChatClient(effective.Model);
    }

    public EmbeddingClient CreateEmbeddingClient()
    {
        var config = _configService.Load();
        var effective = Resolve(config, AiModelRole.Embedding);
        if (string.IsNullOrWhiteSpace(effective.ApiKey) || string.IsNullOrWhiteSpace(effective.Model))
        {
            throw new InvalidOperationException("Embedding model is not configured.");
        }
        return CreateClient(effective, config.Timeout).GetEmbeddingClient(effective.Model);
    }

    public async Task<(bool Success, string Message)> TestChatAsync(
        AiModelRole role,
        CancellationToken cancellationToken = default)
        => await TestChatAsync(_configService.Load(), role, cancellationToken);

    public async Task<(bool Success, string Message)> TestChatAsync(
        AppConfig config,
        AiModelRole role,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var client = CreateChatClient(config, role);
            var completion = await client.CompleteChatAsync(
                new List<OpenAI.Chat.ChatMessage> { new UserChatMessage("Reply with OK.") },
                new ChatCompletionOptions(),
                cancellationToken);
            var text = string.Concat(completion.Value.Content.Select(part => part.Text));
            return (true, string.IsNullOrWhiteSpace(text) ? "Connection succeeded." : text.Trim());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static OpenAIClient CreateClient(EffectiveOpenAiModel effective, int timeoutSeconds)
    {
        var options = OpenAiClientOptionsFactory.Create(effective.BaseUrl, timeoutSeconds);
        return new OpenAIClient(new ApiKeyCredential(effective.ApiKey), options);
    }
}

public readonly record struct EffectiveOpenAiModel(
    string ProviderPreset,
    string ProviderDisplayName,
    string BaseUrl,
    string ApiKey,
    string Model,
    double Temperature,
    int MaxOutputTokens)
{
    public void ValidateChatRole(AiModelRole role)
    {
        if (string.IsNullOrWhiteSpace(ApiKey) || string.IsNullOrWhiteSpace(Model) || MaxOutputTokens <= 0)
        {
            throw new InvalidOperationException($"Model role '{role}' is not configured.");
        }
    }
}
