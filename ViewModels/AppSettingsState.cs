using Athena.UI.Models;
using Athena.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Threading.Tasks;

namespace Athena.UI.ViewModels;

/// <summary>
/// Shares the live application configuration across the App Settings pages.
/// The configuration session remains the sole automatic-save owner.
/// </summary>
public sealed partial class AppSettingsState : ViewModelBase, IDisposable
{
    private readonly AppConfigurationSession _configurationSession;
    private bool _disposed;

    public AppSettingsState(AppConfigurationSession configurationSession)
    {
        _configurationSession = configurationSession;
        _config = configurationSession.Current;
        _configurationSession.CurrentChanged += OnCurrentConfigChanged;
    }

    [ObservableProperty]
    private AppConfig _config;

    public Task SaveNowAsync() => _configurationSession.SaveNowAsync();

    private void OnCurrentConfigChanged(object? sender, AppConfig config) => Config = config;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _configurationSession.CurrentChanged -= OnCurrentConfigChanged;
    }
}
