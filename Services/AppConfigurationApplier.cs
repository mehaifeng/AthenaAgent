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
    private readonly ILocalizationService? _localizationService;
    private readonly ILogger _logger;
    private AppConfig? _lastChatConfig;
    private AppConfig? _lastEmbeddingConfig;
    private string? _themeIdentity;
    private string? _fontScale;
    private OpenAiModelClientIdentity? _chatClientIdentity;
    private OpenAiModelClientIdentity? _embeddingClientIdentity;
    private string _embeddingIdentity;

    public AppConfigurationApplier(
        IConfigService configService,
        IChatService? chatService,
        IEmbeddingService? embeddingService,
        IKnowledgeBaseService? knowledgeBaseService,
        ILocalizationService? localizationService)
    {
        _configService = configService;
        _chatService = chatService;
        _embeddingService = embeddingService;
        _knowledgeBaseService = knowledgeBaseService;
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
        var nextThemeIdentity = string.Equals(config.Theme, "Light", StringComparison.OrdinalIgnoreCase)
            ? "Light"
            : "Dark";
        var shouldApplyTheme = !string.Equals(_themeIdentity, nextThemeIdentity, StringComparison.Ordinal);
        _themeIdentity = nextThemeIdentity;
        var shouldApplyFontScale = !string.Equals(_fontScale, config.FontScale, StringComparison.Ordinal);
        _fontScale = config.FontScale;

        void ApplyOnUiThread()
        {
            if (shouldApplyTheme)
                App.SetTheme(nextThemeIdentity);
            if (_localizationService?.CurrentLanguage != config.Language)
                _localizationService?.SwitchLanguage(config.Language);
            if (shouldApplyFontScale)
                FontScaleService.Apply(config.FontScale);
        }

        if (Dispatcher.UIThread.CheckAccess()) ApplyOnUiThread();
        else Dispatcher.UIThread.Post(ApplyOnUiThread);

        var nextChatClientIdentity = OpenAiModelRuntimeFactory.ComputeClientIdentity(
            config,
            AiModelRole.MainConversation);
        if (!ReferenceEquals(_lastChatConfig, config)
            || _chatClientIdentity != nextChatClientIdentity)
        {
            _chatService?.UpdateConfig(config);
            _lastChatConfig = config;
            _chatClientIdentity = nextChatClientIdentity;
        }

        var nextEmbeddingClientIdentity = OpenAiModelRuntimeFactory.ComputeClientIdentity(
            config,
            AiModelRole.Embedding);
        if (!ReferenceEquals(_lastEmbeddingConfig, config)
            || _embeddingClientIdentity != nextEmbeddingClientIdentity)
        {
            _embeddingService?.UpdateConfig(config);
            _lastEmbeddingConfig = config;
            _embeddingClientIdentity = nextEmbeddingClientIdentity;
        }
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
