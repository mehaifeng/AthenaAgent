using Athena.UI.Services.Interfaces;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.ViewModels;

public sealed partial class RuntimeDiagnosticsViewModel : ViewModelBase, IDisposable
{
    private readonly ILocalizationService? _localizationService;
    private readonly IOpenRouterModelMetadataCatalog? _metadataCatalog;
    private readonly IUserInteractionService? _userInteractionService;
    private readonly CancellationTokenSource _operations = new();
    private bool _disposed;

    public RuntimeDiagnosticsViewModel(
        ILocalizationService? localizationService = null,
        IOpenRouterModelMetadataCatalog? metadataCatalog = null,
        IUserInteractionService? userInteractionService = null)
    {
        _localizationService = localizationService;
        _metadataCatalog = metadataCatalog;
        _userInteractionService = userInteractionService;
        if (_localizationService != null) _localizationService.LanguageChanged += OnLanguageChanged;
        if (_metadataCatalog != null) _metadataCatalog.CatalogChanged += OnCatalogChanged;
        RefreshContextDiagnostics();
    }


    [ObservableProperty]
    private string _metadataDiagnosticsStatus = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasContextMaintenanceStatus))]
    private string _contextMaintenanceStatus = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ClearMetadataCacheCommand))]
    private bool _isMaintainingContextData;

    public bool HasContextMaintenanceStatus => !string.IsNullOrWhiteSpace(ContextMaintenanceStatus);
    private bool CanMaintainContextData => !IsMaintainingContextData;


    [RelayCommand]
    private void RefreshContextDiagnostics()
    {
        if (_metadataCatalog == null)
        {
            MetadataDiagnosticsStatus = GetString("Status.ServiceNotInitialized", "Service not initialized");
        }
        else
        {
            var snapshot = _metadataCatalog.Current;
            MetadataDiagnosticsStatus = string.Format(
                GetString(
                    "Settings.Diagnostics.MetadataSummary",
                    "Revision {0} · {1:N0} models · fetched {2:g} · {3}"),
                string.IsNullOrWhiteSpace(snapshot.CatalogRevision) ? "—" : snapshot.CatalogRevision,
                snapshot.Models.Count,
                snapshot.FetchedAtUtc == DateTimeOffset.MinValue ? "—" : snapshot.FetchedAtUtc.ToLocalTime(),
                _metadataCatalog.IsStale
                    ? GetString("ProviderModels.Metadata.Stale", "stale")
                    : GetString("ProviderModels.Metadata.Fresh", "fresh"));
        }
    }

    [RelayCommand(CanExecute = nameof(CanMaintainContextData))]
    private async Task ClearMetadataCacheAsync()
    {
        if (_metadataCatalog == null) return;
        if (!await ConfirmClearAsync(
                GetString("Settings.Diagnostics.ClearMetadataCache", "Clear metadata cache"),
                GetString("Settings.Diagnostics.ClearMetadataCacheConfirm", "Clear downloaded OpenRouter metadata snapshots and return to the bundled seed?")))
            return;
        IsMaintainingContextData = true;
        try
        {
            await _metadataCatalog.ClearLocalCacheAsync(_operations.Token);
            ContextMaintenanceStatus = GetString("Settings.Diagnostics.MetadataCacheCleared", "Downloaded metadata cache was cleared; bundled seed remains available.");
            RefreshContextDiagnostics();
        }
        catch (OperationCanceledException) when (_operations.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ContextMaintenanceStatus = string.Format(
                GetString("Settings.Diagnostics.ClearFailed", "Clear failed: {0}"),
                ex.Message);
        }
        finally
        {
            if (!_operations.IsCancellationRequested) IsMaintainingContextData = false;
        }
    }

    private Task<bool> ConfirmClearAsync(string title, string message) =>
        _userInteractionService?.ConfirmAsync(
            title,
            message,
            GetString("Common.Confirm", "Confirm"),
            GetString("Common.Cancel", "Cancel"),
            showDontAskAgain: false)
        ?? Task.FromResult(true);

    private void OnLanguageChanged(object? sender, EventArgs e) => RefreshContextDiagnostics();

    private void OnCatalogChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess()) RefreshContextDiagnostics();
        else Dispatcher.UIThread.Post(RefreshContextDiagnostics);
    }

    private string GetString(string key, string fallback) =>
        _localizationService?.GetString(key, fallback) ?? fallback;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_localizationService != null) _localizationService.LanguageChanged -= OnLanguageChanged;
        if (_metadataCatalog != null) _metadataCatalog.CatalogChanged -= OnCatalogChanged;
        _operations.Cancel();
        _operations.Dispose();
    }
}
