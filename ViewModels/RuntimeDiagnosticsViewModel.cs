using Athena.UI.Services;
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
    private readonly IHeadlessBrowserService? _browserService;
    private readonly IBrowserVisionService? _browserVisionService;
    private readonly ILocalizationService? _localizationService;
    private readonly ITokenCalibrationService? _tokenCalibration;
    private readonly IOpenRouterModelMetadataCatalog? _metadataCatalog;
    private readonly IUserInteractionService? _userInteractionService;
    private readonly CancellationTokenSource _operations = new();
    private bool _disposed;

    public RuntimeDiagnosticsViewModel(
        AppSettingsState state,
        IHeadlessBrowserService? browserService = null,
        IBrowserVisionService? browserVisionService = null,
        ILocalizationService? localizationService = null,
        ITokenCalibrationService? tokenCalibration = null,
        IOpenRouterModelMetadataCatalog? metadataCatalog = null,
        IUserInteractionService? userInteractionService = null)
    {
        State = state;
        _browserService = browserService;
        _browserVisionService = browserVisionService;
        _localizationService = localizationService;
        _tokenCalibration = tokenCalibration;
        _metadataCatalog = metadataCatalog;
        _userInteractionService = userInteractionService;
        if (_localizationService != null) _localizationService.LanguageChanged += OnLanguageChanged;
        if (_metadataCatalog != null) _metadataCatalog.CatalogChanged += OnCatalogChanged;
        RefreshContextDiagnostics();
    }

    public AppSettingsState State { get; }

    [ObservableProperty]
    private string _browserRuntimeStatus = string.Empty;

    [ObservableProperty]
    private string _browserAgentTestStatus = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestBrowserRuntimeCommand))]
    [NotifyCanExecuteChangedFor(nameof(InstallBrowserRuntimeCommand))]
    private bool _isTestingBrowserRuntime;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestBrowserRuntimeCommand))]
    [NotifyCanExecuteChangedFor(nameof(InstallBrowserRuntimeCommand))]
    private bool _isInstallingBrowserRuntime;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestBrowserAgentCommand))]
    private bool _isTestingBrowserAgent;

    [ObservableProperty]
    private string _metadataDiagnosticsStatus = string.Empty;

    [ObservableProperty]
    private string _calibrationDiagnosticsStatus = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasContextMaintenanceStatus))]
    private string _contextMaintenanceStatus = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ClearCalibrationCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearMetadataCacheCommand))]
    private bool _isMaintainingContextData;

    public bool HasBrowserRuntimeStatus => !string.IsNullOrWhiteSpace(BrowserRuntimeStatus);
    public bool HasBrowserAgentTestStatus => !string.IsNullOrWhiteSpace(BrowserAgentTestStatus);
    public bool HasContextMaintenanceStatus => !string.IsNullOrWhiteSpace(ContextMaintenanceStatus);
    private bool CanTestBrowserRuntime => !IsTestingBrowserRuntime && !IsInstallingBrowserRuntime;
    private bool CanInstallBrowserRuntime => !IsInstallingBrowserRuntime && !IsTestingBrowserRuntime;
    private bool CanTestBrowserAgent => !IsTestingBrowserAgent;
    private bool CanMaintainContextData => !IsMaintainingContextData;

    partial void OnBrowserRuntimeStatusChanged(string value) => OnPropertyChanged(nameof(HasBrowserRuntimeStatus));
    partial void OnBrowserAgentTestStatusChanged(string value) => OnPropertyChanged(nameof(HasBrowserAgentTestStatus));

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

        if (_tokenCalibration == null)
        {
            CalibrationDiagnosticsStatus = GetString("Status.ServiceNotInitialized", "Service not initialized");
        }
        else
        {
            var diagnostics = _tokenCalibration.GetDiagnostics();
            CalibrationDiagnosticsStatus = string.Format(
                GetString(
                    "Settings.Diagnostics.CalibrationSummary",
                    "{0:N0} profiles · {1:N0} text samples · {2:N0} image samples ({3:N0} clean, {4:N0} direct) · estimator v{5} · updated {6}"),
                diagnostics.ProfileCount,
                diagnostics.TextSampleCount,
                diagnostics.ImageSampleCount,
                diagnostics.CleanImageSampleCount,
                diagnostics.DirectImageUsageSampleCount,
                diagnostics.CurrentEstimatorVersion,
                diagnostics.LastUpdatedAtUtc?.ToLocalTime().ToString("g") ?? "—");
        }
    }

    [RelayCommand(CanExecute = nameof(CanMaintainContextData))]
    private async Task ClearCalibrationAsync()
    {
        if (_tokenCalibration == null) return;
        if (!await ConfirmClearAsync(
                GetString("Settings.Diagnostics.ClearCalibration", "Clear calibration"),
                GetString("Settings.Diagnostics.ClearCalibrationConfirm", "Clear all local aggregate token calibration profiles? They will be learned again from future Usage.")))
            return;
        IsMaintainingContextData = true;
        try
        {
            await _tokenCalibration.ClearAsync(_operations.Token);
            ContextMaintenanceStatus = GetString("Settings.Diagnostics.CalibrationCleared", "Local calibration profiles were cleared.");
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

    [RelayCommand(CanExecute = nameof(CanTestBrowserRuntime))]
    private async Task TestBrowserRuntimeAsync()
    {
        if (_browserService == null)
        {
            BrowserRuntimeStatus = GetString("Status.ServiceNotInitialized", "Service not initialized");
            return;
        }

        IsTestingBrowserRuntime = true;
        BrowserRuntimeStatus = GetString("Status.TestingConnection", "Testing...");
        var cancellationToken = _operations.Token;
        try
        {
            var status = await _browserService.GetRuntimeStatusAsync(cancellationToken);
            if (!cancellationToken.IsCancellationRequested)
                BrowserRuntimeStatus = status.Details == null ? status.Message : $"{status.Message}\n{status.Details}";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested) IsTestingBrowserRuntime = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanInstallBrowserRuntime))]
    private async Task InstallBrowserRuntimeAsync()
    {
        if (_browserService == null)
        {
            BrowserRuntimeStatus = GetString("Status.ServiceNotInitialized", "Service not initialized");
            return;
        }

        IsInstallingBrowserRuntime = true;
        BrowserRuntimeStatus = GetString("Status.InstallingBrowserRuntime", "Installing browser runtime...");
        var cancellationToken = _operations.Token;
        try
        {
            var result = await _browserService.InstallRuntimeAsync(cancellationToken);
            if (!cancellationToken.IsCancellationRequested) BrowserRuntimeStatus = result.Message;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested) IsInstallingBrowserRuntime = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanTestBrowserAgent))]
    private async Task TestBrowserAgentAsync()
    {
        if (_browserVisionService == null)
        {
            BrowserAgentTestStatus = GetString("Status.ServiceNotInitialized", "Service not initialized");
            return;
        }

        IsTestingBrowserAgent = true;
        BrowserAgentTestStatus = GetString("Status.TestingConnection", "Testing...");
        var cancellationToken = _operations.Token;
        try
        {
            AppConfigNormalizer.NormalizeBrowser(State.Config);
            await State.SaveNowAsync();
            var result = await _browserVisionService.TestConnectionAsync(cancellationToken);
            if (!cancellationToken.IsCancellationRequested) BrowserAgentTestStatus = result.Message;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested) IsTestingBrowserAgent = false;
        }
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
