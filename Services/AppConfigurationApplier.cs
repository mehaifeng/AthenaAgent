using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using Avalonia.Threading;
using Serilog;
using System;

namespace Athena.UI.Services;

/// <summary>Applies persisted configuration changes to long-lived runtime services.</summary>
public sealed class AppConfigurationApplier : IDisposable
{
    private readonly IConfigService _configService;
    private readonly IChatService? _chatService;
    private readonly IEmbeddingService? _embeddingService;
    private readonly IKnowledgeBaseService? _knowledgeBaseService;
    private readonly ITokenService? _tokenService;
    private readonly ILocalizationService? _localizationService;
    private readonly ILogger _logger;
    private string _embeddingIdentity;

    public AppConfigurationApplier(
        IConfigService configService,
        IChatService? chatService,
        IEmbeddingService? embeddingService,
        IKnowledgeBaseService? knowledgeBaseService,
        ITokenService? tokenService,
        ILocalizationService? localizationService)
    {
        _configService = configService;
        _chatService = chatService;
        _embeddingService = embeddingService;
        _knowledgeBaseService = knowledgeBaseService;
        _tokenService = tokenService;
        _localizationService = localizationService;
        _logger = Log.ForContext<AppConfigurationApplier>();

        var current = configService.Load();
        _embeddingIdentity = ComputeEmbeddingIdentity(current);
        Apply(current);
        _configService.ConfigChanged += OnConfigChanged;
    }

    private async void OnConfigChanged(object? sender, AppConfig config)
    {
        try
        {
            Apply(config);
            var nextIdentity = ComputeEmbeddingIdentity(config);
            if (nextIdentity != _embeddingIdentity)
            {
                _embeddingIdentity = nextIdentity;
                if (_knowledgeBaseService != null)
                {
                    await _knowledgeBaseService.RefreshVectorCacheAsync();
                    _logger.Information("Embedding configuration changed; vector cache invalidated");
                }
            }
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "Failed to apply saved application configuration");
        }
    }

    private void Apply(AppConfig config)
    {
        void ApplyOnUiThread()
        {
            App.SetTheme(config.Theme);
            if (_localizationService?.CurrentLanguage != config.Language)
                _localizationService?.SwitchLanguage(config.Language);
            _tokenService?.MaxTokens = config.MaxContextTokens;
        }

        if (Dispatcher.UIThread.CheckAccess()) ApplyOnUiThread();
        else Dispatcher.UIThread.Post(ApplyOnUiThread);

        _chatService?.UpdateConfig(config);
        if (_embeddingService is OpenAIEmbeddingService embeddingService)
            embeddingService.UpdateConfig(config);
    }

    private static string ComputeEmbeddingIdentity(AppConfig config)
    {
        try
        {
            var effective = OpenAiModelRuntimeFactory.Resolve(config, AiModelRole.Embedding);
            return string.Join('|', effective.ProviderPreset, effective.BaseUrl, effective.Model);
        }
        catch
        {
            return string.Empty;
        }
    }

    public void Dispose() => _configService.ConfigChanged -= OnConfigChanged;
}
