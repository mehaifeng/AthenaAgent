using Athena.UI.Services.ComputerUse;
using Athena.UI.Services.Interfaces;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.ViewModels;

public sealed partial class EmbeddedBrowserViewModel : ViewModelBase, IDisposable
{
    private readonly EmbeddedBrowser _browser;
    private readonly ComputerUseService _computerUse;
    private readonly ILocalizationService _localization;
    private readonly IExternalUrlOpener _external;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private int _selectedTab;
    [ObservableProperty] private string _address = "";
    [ObservableProperty] private string _taskAddress = "";
    [ObservableProperty] private string _browseStatus = "";
    [ObservableProperty] private string _taskStatus = "";

    public EmbeddedBrowserViewModel(EmbeddedBrowser browser, ComputerUseService computerUse,
        ILocalizationService localization, IExternalUrlOpener external)
    {
        _browser = browser;
        _computerUse = computerUse;
        _localization = localization;
        _external = external;
        _browser.PagesChanged += OnPagesChanged;
        _computerUse.ProgressChanged += OnProgress;
    }

    public event EventHandler? RevealRequested;
    public bool IsTask => SelectedTab == 1;
    public bool CanBrowse => !IsTask;
    public bool HasPage => CurrentPage is not null;
    internal ChromeBrowserPage? CurrentPage => IsTask ? _browser.TaskPage : _browser.BrowsingPage;

    partial void OnSelectedTabChanged(int value)
    {
        OnPropertyChanged(nameof(IsTask));
        OnPropertyChanged(nameof(CanBrowse));
        RefreshPage();
    }

    private void OnPagesChanged() => Dispatcher.UIThread.Post(() =>
    {
        if (!_disposed)
            RefreshPage();
    });

    private void RefreshPage()
    {
        OnPropertyChanged(nameof(CurrentPage));
        OnPropertyChanged(nameof(HasPage));
    }

    [RelayCommand]
    public void Reveal()
    {
        IsOpen = true;
        RevealRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Close() => IsOpen = false;

    public Task OpenLinkAsync(Uri address)
    {
        if (!EmbeddedBrowser.IsPageUri(address)) return Task.CompletedTask;
        SelectedTab = 0;
        Address = address.AbsoluteUri;
        Reveal();
        return NavigateToAsync(address);
    }

    [RelayCommand]
    private Task NavigateAsync()
    {
        if (!Uri.TryCreate(Address.Trim(), UriKind.Absolute, out var address) || !EmbeddedBrowser.IsPageUri(address))
        {
            BrowseStatus = _localization.GetString("Browser.InvalidAddress", "Enter an HTTP or HTTPS address.");
            return Task.CompletedTask;
        }
        return NavigateToAsync(address);
    }

    private Task NavigateToAsync(Uri address)
    {
        BrowseStatus = "";
        return BrowseAsync(token => _browser.OpenAsync(address, token));
    }

    [RelayCommand]
    private Task BackAsync() => BrowseAsync(token => _browser.BrowsingPage?.GoAsync(-1, token) ?? Task.CompletedTask);

    [RelayCommand]
    private Task ForwardAsync() => BrowseAsync(token => _browser.BrowsingPage?.GoAsync(1, token) ?? Task.CompletedTask);

    [RelayCommand]
    private Task ReloadAsync() => BrowseAsync(token => _browser.BrowsingPage?.Driver.CallAsync("Page.reload", new { }, token)
        ?? Task.CompletedTask);

    private async Task BrowseAsync(Func<CancellationToken, Task> operation)
    {
        if (_disposed) return;
        try
        {
            await operation(_lifetime.Token);
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception exception)
        {
            BrowseStatus = exception.Message;
        }
    }

    internal void UpdateAddress(ChromeBrowserPage page, Uri? location, bool addressHasFocus)
    {
        if (IsTask || location is null || !ReferenceEquals(page, CurrentPage)) return;
        page.UpdateAddress(location);
        if (!addressHasFocus)
            Address = location.AbsoluteUri;
    }

    [RelayCommand]
    private void OpenInSystemBrowser() => OpenLinkInSystemBrowser(CurrentPage?.Address);

    internal void OpenLinkInSystemBrowser(Uri? uri)
    {
        if (uri is not null && EmbeddedBrowser.IsPageUri(uri))
            _external.TryOpen(uri.AbsoluteUri);
    }

    private void OnProgress(ComputerUseProgress progress)
    {
        if (!progress.InBrowser) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed) return;
            if (progress is { Phase: ComputerUsePhase.Observing, Frame: null })
            {
                SelectedTab = 1;
                Reveal();
            }
            if ((progress.Address ?? progress.Frame?.BrowserUri) is { } location)
                TaskAddress = location.AbsoluteUri;
            if (progress.Phase is ComputerUsePhase.Completed or ComputerUsePhase.Failed or ComputerUsePhase.Stopped)
            {
                TaskStatus = progress.Detail ?? _localization.GetString("ComputerUse.Stopped", "Stopped");
            }
            else
            {
                TaskStatus = progress.Phase switch
                {
                    ComputerUsePhase.Typing => _localization.GetString("ComputerUse.Typing", "Entering text"),
                    ComputerUsePhase.Moving => _localization.GetString("ComputerUse.Moving", "Moving the AI cursor"),
                    ComputerUsePhase.Clicking => _localization.GetString("ComputerUse.Clicking", "Clicked"),
                    ComputerUsePhase.Navigating => _localization.GetString("Browser.Navigating", "Navigating to address"),
                    ComputerUsePhase.OtherAction => _localization.GetString("ComputerUse.OtherAction", "Input sent"),
                    _ => _localization.GetString("Browser.Observing", "Observing webpage")
                };
            }
        });
    }

    internal void ReportPreviewError(string message)
    {
        if (IsTask)
            TaskStatus = message;
        else
            BrowseStatus = message;
        Log.Debug("Embedded browser preview ended: {Error}", message);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _browser.PagesChanged -= OnPagesChanged;
        _computerUse.ProgressChanged -= OnProgress;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
