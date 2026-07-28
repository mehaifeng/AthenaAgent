using Athena.UI.Models;
using Athena.UI.Services;
using Athena.UI.Services.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.ViewModels;

public sealed partial class AppSettingsViewModel : ViewModelBase, IDisposable
{
    private readonly AppConfigurationSession _configurationSession;
    private readonly IHeadlessBrowserService? _browserService;
    private readonly IBrowserVisionService? _browserVisionService;
    private readonly ILocalizationService? _localizationService;
    private CancellationTokenSource _windowOperations = new();

    public AppSettingsViewModel(
        AppConfigurationSession configurationSession,
        IHeadlessBrowserService? browserService = null,
        IBrowserVisionService? browserVisionService = null,
        ILocalizationService? localizationService = null)
    {
        _configurationSession = configurationSession;
        _browserService = browserService;
        _browserVisionService = browserVisionService;
        _localizationService = localizationService;
        _config = configurationSession.Current;
        _configurationSession.CurrentChanged += OnCurrentConfigChanged;

        // 浏览器页已从常规设置中移除，能力默认可用；实际调用仍受模型配置与工具审批约束。
        Config.BrowserEnabled = true;
    }

    [ObservableProperty]
    private AppConfig _config;

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

    private void OnCurrentConfigChanged(object? sender, AppConfig config) => Config = config;

    public void ActivateWindow()
    {
        if (!_windowOperations.IsCancellationRequested) return;
        _windowOperations.Dispose();
        _windowOperations = new CancellationTokenSource();
        IsTestingBrowserRuntime = false;
        IsInstallingBrowserRuntime = false;
        IsTestingBrowserAgent = false;
    }

    public void DeactivateWindow()
    {
        if (!_windowOperations.IsCancellationRequested)
            _windowOperations.Cancel();
    }

    [RelayCommand]
    private void SetApprovalMode(string? mode)
    {
        if (System.Enum.TryParse<ToolApprovalMode>(mode, true, out var parsed))
            Config.ToolApprovalMode = parsed;
    }

    [RelayCommand]
    private async Task RevokeAutoAllowedToolAsync(string? tool)
    {
        if (!string.IsNullOrEmpty(tool) && Config.AutoAllowedTools.Remove(tool))
            await _configurationSession.SaveNowAsync();
    }

    [RelayCommand]
    private async Task RevokeTerminalAllowlistAsync(string? command)
    {
        if (!string.IsNullOrEmpty(command) && Config.TerminalAllowlist.Remove(command))
            await _configurationSession.SaveNowAsync();
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
        var cancellationToken = _windowOperations.Token;
        try
        {
            var status = await _browserService.GetRuntimeStatusAsync(cancellationToken);
            if (cancellationToken.IsCancellationRequested) return;
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
        var cancellationToken = _windowOperations.Token;
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
        var cancellationToken = _windowOperations.Token;
        try
        {
            AppConfigNormalizer.NormalizeBrowser(Config);
            await _configurationSession.SaveNowAsync();
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
        _windowOperations.Cancel();
        _windowOperations.Dispose();
        _configurationSession.CurrentChanged -= OnCurrentConfigChanged;
    }
}
