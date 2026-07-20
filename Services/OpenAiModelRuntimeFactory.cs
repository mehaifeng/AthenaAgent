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
            _ => throw new ArgumentOutOfRangeException(nameof(role), role, null)
        };

        var provider = config.AiModels.Provider;

        if (role == AiModelRole.Embedding && config.EmbeddingCredentialSource == EmbeddingConnectionSource.Custom)
        {
            return new EffectiveOpenAiModel(
                "embedding-custom",
                string.IsNullOrWhiteSpace(config.EmbeddingProvider) ? "Custom" : config.EmbeddingProvider,
                config.EmbeddingBaseUrl,
                config.EmbeddingApiKey,
                settings.Model,
                settings.Temperature,
                settings.MaxOutputTokens);
        }

        return new EffectiveOpenAiModel(
            provider.ProviderPreset,
            provider.DisplayName,
            provider.BaseUrl,
            provider.ApiKey,
            settings.Model,
            settings.Temperature,
            settings.MaxOutputTokens);
    }

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
