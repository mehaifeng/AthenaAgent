using Athena.UI.Services;
using Athena.UI.Services.Interfaces;
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
    private readonly CancellationTokenSource _operations = new();
    private bool _disposed;

    public RuntimeDiagnosticsViewModel(
        AppSettingsState state,
        IHeadlessBrowserService? browserService = null,
        IBrowserVisionService? browserVisionService = null,
        ILocalizationService? localizationService = null)
    {
        State = state;
        _browserService = browserService;
        _browserVisionService = browserVisionService;
        _localizationService = localizationService;
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

    public bool HasBrowserRuntimeStatus => !string.IsNullOrWhiteSpace(BrowserRuntimeStatus);
    public bool HasBrowserAgentTestStatus => !string.IsNullOrWhiteSpace(BrowserAgentTestStatus);
    private bool CanTestBrowserRuntime => !IsTestingBrowserRuntime && !IsInstallingBrowserRuntime;
    private bool CanInstallBrowserRuntime => !IsInstallingBrowserRuntime && !IsTestingBrowserRuntime;
    private bool CanTestBrowserAgent => !IsTestingBrowserAgent;

    partial void OnBrowserRuntimeStatusChanged(string value) => OnPropertyChanged(nameof(HasBrowserRuntimeStatus));
    partial void OnBrowserAgentTestStatusChanged(string value) => OnPropertyChanged(nameof(HasBrowserAgentTestStatus));

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
        _operations.Cancel();
        _operations.Dispose();
    }
}
