using Athena.UI.Models;
using Athena.UI.Services;
using Athena.UI.Services.ComputerUse;
using Athena.UI.Services.Interfaces;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.ViewModels;

public sealed partial class EmbeddedBrowserSettingsViewModel : ViewModelBase, IDisposable
{
    private readonly EmbeddedBrowser? _browser;
    private readonly EmbeddedBrowserViewModel? _surface;
    private readonly ComputerUseService? _computerUse;
    private readonly IUserInteractionService? _interaction;
    private readonly ILocalizationService? _localization;
    private readonly ModelRoleProbeService _roleProbes;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _search;
    private bool _shown;

    internal EmbeddedBrowserSettingsViewModel(AppSettingsState state, EmbeddedBrowser? browser, EmbeddedBrowserViewModel? surface,
        ComputerUseService? computerUse, IUserInteractionService? interaction, ILocalizationService? localization,
        ModelRoleProbeService roleProbes)
    {
        State = state;
        _browser = browser;
        _surface = surface;
        _computerUse = computerUse;
        _interaction = interaction;
        _localization = localization;
        _roleProbes = roleProbes;
        _homePage = State.Config.EmbeddedBrowser.HomePage;
        ModelSelection = CreateModelSelection();
        State.PropertyChanged += OnConfigChanged;
        if (_browser != null)
        {
            _browser.RuntimeChanged += OnRuntimeChanged;
            _browser.HistoryChanged += OnHistoryChanged;
        }
        if (_computerUse != null) _computerUse.ProgressChanged += OnTaskProgress;
        if (_localization != null) _localization.LanguageChanged += OnLanguageChanged;
    }

    public AppSettingsState State { get; }
    public ProviderRoleSelectionViewModel ModelSelection { get; private set; }
    public string RuntimeVersion => _browser != null ? $"Electron {_browser.RuntimeVersion}" : "";
    public string RuntimeStatus => _browser?.RuntimeError ?? (_browser?.IsRunning == true
        ? T("Settings.Browser.RuntimeRunning", "Running") : _browser?.IsAvailable == true
            ? T("Settings.Browser.RuntimeReady", "Bundled and ready")
            : T("Settings.Browser.RuntimeMissing", "Runtime package missing"));
    public bool CanClearSiteData => _browser?.IsAvailable == true && _computerUse?.HasBrowserTask != true;
    public string DownloadDirectory => string.IsNullOrWhiteSpace(State.Config.EmbeddedBrowser.DownloadDirectory)
        ? T("Settings.Browser.DownloadDefault", "System Downloads folder") : State.Config.EmbeddedBrowser.DownloadDirectory;

    [ObservableProperty] private string _homePage;
    [ObservableProperty] private string? _homePageError;
    [ObservableProperty] private string _historyQuery = "";
    [ObservableProperty] private IReadOnlyList<BrowserHistoryEntry> _history = [];
    [ObservableProperty] private string? _status;

    internal void Show(bool shown)
    {
        _shown = shown;
        if (shown) _ = RefreshHistoryAsync();
    }

    partial void OnHomePageChanged(string value)
    {
        var address = value.Trim();
        if (address.Length > 0 && (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || !EmbeddedBrowser.IsPageUri(uri)))
        {
            HomePageError = T("Browser.InvalidAddress", "Enter an HTTP or HTTPS address.");
            return;
        }
        HomePageError = null;
        State.Config.EmbeddedBrowser.HomePage = address;
    }

    partial void OnHistoryQueryChanged(string value) => _ = RefreshHistoryAsync();

    [RelayCommand]
    private Task PickDownloadDirectoryAsync() => RunAsync(async () =>
    {
        if (_interaction == null) return;
        var folder = await _interaction.PickFolderAsync(T("Settings.Browser.DownloadDirectory", "Download folder"));
        if (!string.IsNullOrWhiteSpace(folder))
        {
            State.Config.EmbeddedBrowser.DownloadDirectory = folder;
            OnPropertyChanged(nameof(DownloadDirectory));
        }
    });

    [RelayCommand]
    private void ResetDownloadDirectory()
    {
        State.Config.EmbeddedBrowser.DownloadDirectory = "";
        OnPropertyChanged(nameof(DownloadDirectory));
    }

    public event EventHandler? OpenInMainRequested;

    [RelayCommand]
    private Task OpenHistoryAsync(BrowserHistoryEntry? entry)
    {
        if (entry == null || _surface == null || !Uri.TryCreate(entry.Url, UriKind.Absolute, out var address) || !EmbeddedBrowser.IsPageUri(address))
            return Task.CompletedTask;
        OpenInMainRequested?.Invoke(this, EventArgs.Empty);
        return _surface.OpenLinkAsync(address);
    }

    [RelayCommand]
    private Task ClearHistoryAsync() => RunAsync(async () =>
    {
        var title = T("Settings.Browser.ClearHistory", "Clear history");
        if (_browser != null && await ConfirmAsync(title,
            T("Settings.Browser.ClearHistoryConfirm", "Remove the browsing history from this device?")))
        {
            await _browser.ClearHistoryAsync(_lifetime.Token);
            await RefreshHistoryAsync();
        }
    });

    [RelayCommand]
    private Task ClearSiteDataAsync() => RunAsync(async () =>
    {
        var title = T("Settings.Browser.ClearData", "Clear website data");
        if (_browser != null && CanClearSiteData && await ConfirmAsync(title,
            T("Settings.Browser.ClearDataConfirm", "This closes the browser tabs and signs you out of websites.")) && CanClearSiteData)
        {
            await _browser.ClearSiteDataAsync(_lifetime.Token);
            Status = T("Settings.Browser.DataCleared", "Website data cleared");
        }
    });

    private Task<bool> ConfirmAsync(string title, string message) => _interaction?.ConfirmAsync(title, message,
        T("Common.Confirm", "Confirm"), T("Common.Cancel", "Cancel"), showDontAskAgain: false) ?? Task.FromResult(false);

    private async Task RunAsync(Func<Task> action)
    {
        Status = null;
        try { await action(); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { Status = ex.Message; }
    }

    private async Task RefreshHistoryAsync()
    {
        if (_browser == null || !_shown || _lifetime.IsCancellationRequested) return;
        _search?.Cancel();
        using var search = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _search = search;
        try
        {
            await Task.Delay(120, search.Token);
            var history = await _browser.SearchHistoryAsync(HistoryQuery, search.Token);
            if (ReferenceEquals(_search, search)) History = history;
        }
        catch (OperationCanceledException) when (search.IsCancellationRequested) { }
        catch (Exception ex) { Status = ex.Message; }
        finally { if (ReferenceEquals(_search, search)) _search = null; }
    }

    private void OnConfigChanged(object? sender, PropertyChangedEventArgs e)
    {
        RefreshModelSelection();
        HomePage = State.Config.EmbeddedBrowser.HomePage;
        OnPropertyChanged(nameof(DownloadDirectory));
    }

    private void OnRuntimeChanged() => Dispatcher.UIThread.Post(() =>
    {
        OnPropertyChanged(nameof(RuntimeStatus));
        OnPropertyChanged(nameof(CanClearSiteData));
    });
    private void OnHistoryChanged() => Dispatcher.UIThread.Post(() => _ = RefreshHistoryAsync());
    private void OnTaskProgress(ComputerUseProgress progress) => Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(CanClearSiteData)));
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        RefreshModelSelection();
        OnPropertyChanged(nameof(RuntimeStatus));
        OnPropertyChanged(nameof(DownloadDirectory));
    }

    private ProviderRoleSelectionViewModel CreateModelSelection() =>
        new(T("ProviderModels.Role.ComputerUse", "Computer Use"), AiModelRole.ComputerUse, State.Config, _roleProbes, T);

    private void RefreshModelSelection()
    {
        ModelSelection.Dispose();
        ModelSelection = CreateModelSelection();
        OnPropertyChanged(nameof(ModelSelection));
    }

    private string T(string key, string fallback) => _localization?.GetString(key, fallback) ?? fallback;

    public void Dispose()
    {
        State.PropertyChanged -= OnConfigChanged;
        ModelSelection.Dispose();
        if (_browser != null)
        {
            _browser.RuntimeChanged -= OnRuntimeChanged;
            _browser.HistoryChanged -= OnHistoryChanged;
        }
        if (_computerUse != null) _computerUse.ProgressChanged -= OnTaskProgress;
        if (_localization != null) _localization.LanguageChanged -= OnLanguageChanged;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
