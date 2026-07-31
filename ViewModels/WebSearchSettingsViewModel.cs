using Athena.UI.Models;
using Athena.UI.Services;
using Athena.UI.Services.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.ViewModels;

public partial class WebSearchSettingsViewModel : ViewModelBase, IDisposable
{
    private readonly AppConfigurationSession _configurationSession;
    private readonly IWebSearchService? _webSearchService;
    private readonly ILocalizationService? _localizationService;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private bool _disposed;

    public WebSearchSettingsViewModel(
        AppConfigurationSession configurationSession,
        IWebSearchService? webSearchService = null,
        ILocalizationService? localizationService = null)
    {
        _configurationSession = configurationSession;
        _webSearchService = webSearchService;
        _localizationService = localizationService;
        _config = configurationSession.Current;
        _configurationSession.CurrentChanged += OnCurrentConfigChanged;
        RebuildCards();
    }

    [ObservableProperty] private AppConfig _config;
    [ObservableProperty] private IReadOnlyList<ExtensionProviderCardViewModel> _providerCards = [];
    [ObservableProperty] private string _testStatus = string.Empty;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(TestConnectionCommand))] private bool _isTesting;

    public bool CanTest => !IsTesting;

    private void RebuildCards()
    {
        DisposeProviderCards();
        var providers = ExtensionProviderCatalog.WebSearchProviders;
        ExtensionSettingsSupport.EnsureSettings(
            Config.WebSearchProviderSettings,
            providers);
        ProviderCards = providers.Select(option => new ExtensionProviderCardViewModel(
            ExtensionProviderKind.WebSearch,
            option,
            Config.WebSearchProviderSettings.First(setting => setting.ProviderId == option.Id),
            option.Id == Config.WebSearchProvider,
            SelectProvider,
            _localizationService)).ToList();
    }

    private void SelectProvider(ExtensionProviderCardViewModel card)
    {
        Config.WebSearchProvider = card.Id;
        foreach (var candidate in ProviderCards)
            if (!ReferenceEquals(candidate, card)) candidate.IsSelected = false;
    }

    [RelayCommand(CanExecute = nameof(CanTest))]
    private async Task TestConnectionAsync()
    {
        if (_webSearchService == null)
        {
            TestStatus = GetString("Status.ServiceNotInitialized", "Service not initialized");
            return;
        }
        if (!Config.WebSearchEnabled)
        {
            TestStatus = GetString("Status.EnableWebSearchFirst", "Please enable Web Search first");
            return;
        }

        IsTesting = true;
        TestStatus = GetString("Status.TestingConnection", "Testing...");
        try
        {
            await _configurationSession.SaveNowAsync();
            if (_webSearchService is WebSearchService service) service.RefreshConfig();
            var (_, message) = await _webSearchService.TestConnectionAsync(_lifetimeCancellation.Token);
            if (_disposed) return;
            TestStatus = message;
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        finally
        {
            if (!_disposed) IsTesting = false;
        }
    }

    private void OnCurrentConfigChanged(object? sender, AppConfig config)
    {
        Config = config;
        RebuildCards();
    }

    private string GetString(string key, string fallback) => _localizationService?.GetString(key, fallback) ?? fallback;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetimeCancellation.Cancel();
        _lifetimeCancellation.Dispose();
        _configurationSession.CurrentChanged -= OnCurrentConfigChanged;
        DisposeProviderCards();
    }

    private void DisposeProviderCards()
    {
        foreach (var card in ProviderCards)
            card.Dispose();
    }
}
