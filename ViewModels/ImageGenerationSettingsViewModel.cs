using Athena.UI.Models;
using Athena.UI.Services;
using Athena.UI.Services.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Athena.UI.ViewModels;

public partial class ImageGenerationSettingsViewModel : ViewModelBase, IDisposable
{
    private readonly AppConfigurationSession _configurationSession;
    private readonly ILocalizationService? _localizationService;
    private bool _disposed;

    public ImageGenerationSettingsViewModel(
        AppConfigurationSession configurationSession,
        ILocalizationService? localizationService = null)
    {
        _configurationSession = configurationSession;
        _localizationService = localizationService;
        _config = configurationSession.Current;
        _configurationSession.CurrentChanged += OnCurrentConfigChanged;
        RebuildCards();
    }

    [ObservableProperty] private AppConfig _config;
    [ObservableProperty] private IReadOnlyList<ExtensionProviderCardViewModel> _providerCards = [];

    private void RebuildCards()
    {
        var providers = ExtensionProviderCatalog.ImageProviders;
        ExtensionSettingsSupport.EnsureSettings(
            Config.ImageProviderSettings,
            providers,
            Config.ImageGenerationProvider,
            selected =>
            {
                selected.BaseUrl = Config.ImageGenerationBaseUrl;
                selected.ApiKey = Config.ImageGenerationApiKey;
                selected.Model = Config.ImageGenerationModel;
                selected.AspectRatio = Config.ImageGenerationAspectRatio;
            });
        ProviderCards = providers.Select(option => new ExtensionProviderCardViewModel(
            ExtensionProviderKind.Image,
            option,
            Config.ImageProviderSettings.First(setting => setting.ProviderId == option.Id),
            option.Id == Config.ImageGenerationProvider,
            SelectProvider,
            _localizationService)).ToList();
    }

    private void SelectProvider(ExtensionProviderCardViewModel card)
    {
        Config.ImageGenerationProvider = card.Id;
        foreach (var candidate in ProviderCards)
            if (!ReferenceEquals(candidate, card)) candidate.IsSelected = false;
    }

    private void OnCurrentConfigChanged(object? sender, AppConfig config)
    {
        Config = config;
        RebuildCards();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _configurationSession.CurrentChanged -= OnCurrentConfigChanged;
    }
}
