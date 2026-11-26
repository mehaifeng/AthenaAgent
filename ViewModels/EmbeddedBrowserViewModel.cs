using Athena.UI.Services.ComputerUse;
using Athena.UI.Services.Interfaces;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.ViewModels;

public sealed partial class EmbeddedBrowserViewModel : ViewModelBase, IDisposable
{
    private readonly EmbeddedBrowser _browser;
    private readonly ComputerUseService _computerUse;
    private readonly ILocalizationService _localization;
    private readonly IExternalUrlOpener _external;
    private readonly IUserInteractionService? _interaction;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Queue<BrowserInteraction> _interactions = new();
    private ChromeBrowserPage? _lastPage;
    private bool _handlingInteraction;
    private bool _updatingTabs;
    private bool _addressEditing;
    private bool _disposed;

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private int _selectedTab;
    [ObservableProperty] private string _address = "";
    [ObservableProperty] private string _browseStatus = "";
    [ObservableProperty] private string _taskStatus = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasMultipleTabs))]
    private IReadOnlyList<BrowserPageOption> _pages = [];
    [ObservableProperty] private BrowserPageOption? _activePage;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasPrompt))]
    private BrowserUserPrompt? _prompt;

    public EmbeddedBrowserViewModel(EmbeddedBrowser browser, ComputerUseService computerUse,
        ILocalizationService localization, IExternalUrlOpener external, IUserInteractionService? interaction = null)
    {
        _browser = browser;
        _computerUse = computerUse;
        _localization = localization;
        _external = external;
        _interaction = interaction;
        _browser.PagesChanged += OnPagesChanged;
        _browser.InteractionRequested += OnInteractionRequested;
        _browser.DownloadCompleted += OnDownloadCompleted;
        _computerUse.ProgressChanged += OnProgress;
    }

    public event EventHandler? RevealRequested;
    public bool IsTask => SelectedTab == 1;
    public bool IsBrowsing => !IsTask;
    public bool HasPage => CurrentPage is not null;
    public bool HasMultipleTabs => Pages.Count > 1;
    public bool HasPrompt => Prompt != null;
    public bool CanTakeOver => IsTask && _computerUse.HasBrowserTask && !_computerUse.IsBrowserPaused;
    public bool CanResume => IsTask && _computerUse.IsBrowserPaused && !_handlingInteraction;
    public bool CanHoverPage => !IsTask || !_computerUse.HasBrowserTask || _computerUse.IsBrowserPaused;
    internal ChromeBrowserPage? CurrentPage => IsTask ? _browser.TaskPage : _browser.BrowsingPage;

    internal void SetPreferredViewport(BrowserViewportSize size) => _browser.SetPreferredViewport(size);
    internal void SetAddressEditing(bool editing)
    {
        _addressEditing = editing;
        if (!editing) RefreshPage();
    }

    partial void OnSelectedTabChanged(int value)
    {
        OnPropertyChanged(nameof(IsTask));
        OnPropertyChanged(nameof(IsBrowsing));
        RefreshPage();
        RefreshControl();
    }

    partial void OnActivePageChanged(BrowserPageOption? value)
    {
        if (value == null || _updatingTabs || value.Id == CurrentPage?.Id) return;
        if (_browser.Tabs(IsTask).FirstOrDefault(page => page.Id == value.Id) is { } page)
        {
            if (page.IsTask) _computerUse.RequestBrowserTakeover();
            _browser.Select(page);
            RefreshPage();
        }
    }

    private void OnPagesChanged() => Dispatcher.UIThread.Post(() =>
    {
        if (!_disposed) RefreshPage();
    });

    private void RefreshPage()
    {
        var selected = CurrentPage;
        var all = _browser.Tabs(IsTask).Select(page => new BrowserPageOption(page.Id,
            page.Title.Length != 0 ? page.Title : page.Address == EmbeddedBrowser.Blank
                ? T("Browser.NewTab", "New tab") : page.Address.Host)).ToArray();
        _updatingTabs = true;
        if (!Pages.SequenceEqual(all)) Pages = all;
        ActivePage = Pages.FirstOrDefault(page => page.Id == selected?.Id);
        _updatingTabs = false;
        var changed = !ReferenceEquals(_lastPage, selected);
        if (changed || !_addressEditing)
            Address = selected != null && selected.Address != EmbeddedBrowser.Blank ? selected.Address.AbsoluteUri : "";
        if (!changed) return;
        _lastPage = selected;
        OnPropertyChanged(nameof(CurrentPage));
        OnPropertyChanged(nameof(HasPage));
    }

    [RelayCommand]
    public void Reveal()
    {
        ShowPane();
        if (IsBrowsing && !HasPage && _browser.HomePage is { } home)
            _ = BrowseAsync(token => _browser.OpenAsync(home, token), inTask: false);
    }

    private void ShowPane()
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
        ShowPane();
        return BrowseAsync(token => _browser.OpenAsync(address, token), inTask: false);
    }

    [RelayCommand]
    private Task NavigateAsync()
    {
        if (!Uri.TryCreate(Address.Trim(), UriKind.Absolute, out var address) || !EmbeddedBrowser.IsPageUri(address))
        {
            SetStatus(IsTask, T("Browser.InvalidAddress", "Enter an HTTP or HTTPS address."));
            return Task.CompletedTask;
        }
        return IsTask
            ? WithPageAsync((page, token) => page.NavigateAsync(address, token))
            : BrowseAsync(token => _browser.OpenAsync(address, token), inTask: false);
    }

    [RelayCommand]
    private Task BackAsync() => WithPageAsync((page, token) => page.GoAsync(-1, token));

    [RelayCommand]
    private Task ForwardAsync() => WithPageAsync((page, token) => page.GoAsync(1, token));

    [RelayCommand]
    private Task ReloadAsync() => WithPageAsync((page, token) => page.Driver.CallAsync("Page.reload", new { }, token));

    [RelayCommand]
    private Task NewTabAsync()
    {
        SelectedTab = 0;
        return BrowseAsync(token => _browser.NewTabAsync(_browser.HomePage ?? EmbeddedBrowser.Blank, token), inTask: false);
    }

    [RelayCommand]
    private Task CloseTabAsync() => WithPageAsync((page, token) => _browser.CloseTabAsync(page, token));

    [RelayCommand]
    private void TakeOver() => _computerUse.RequestBrowserTakeover();

    [RelayCommand]
    private Task ResumeAsync() => BrowseAsync(_computerUse.ResumeBrowserAsync, inTask: true);

    private Task WithPageAsync(Func<ChromeBrowserPage, CancellationToken, Task> operation) =>
        CurrentPage is { } page
            ? BrowseAsync(token => _computerUse.WithManualBrowserInputAsync(page, () =>
                ReferenceEquals(CurrentPage, page) ? operation(page, token) : Task.CompletedTask, token), page.IsTask)
            : Task.CompletedTask;

    internal Task WithManualInputAsync(ChromeBrowserPage page, Func<Task> input, CancellationToken token) =>
        _computerUse.WithManualBrowserInputAsync(page, () =>
            ReferenceEquals(CurrentPage, page) ? input() : Task.CompletedTask, token);

    private async Task BrowseAsync(Func<CancellationToken, Task> operation, bool inTask)
    {
        if (_disposed) return;
        try { await operation(_lifetime.Token); }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception exception) { SetStatus(inTask, exception.Message); }
    }

    [RelayCommand]
    private void OpenInSystemBrowser() => OpenLinkInSystemBrowser(CurrentPage?.Address);

    internal void OpenLinkInSystemBrowser(Uri? uri)
    {
        if (uri != null && EmbeddedBrowser.IsPageUri(uri)) _external.TryOpen(uri.AbsoluteUri);
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
                ShowPane();
            }
            if (progress.Phase == ComputerUsePhase.Paused)
            {
                TaskStatus = progress.Detail ?? T("Browser.Paused", "Paused for your input");
                SelectedTab = 1;
                ShowPane();
            }
            else if (!_computerUse.IsBrowserPaused || progress.Phase is ComputerUsePhase.Completed or ComputerUsePhase.Failed
                         or ComputerUsePhase.Stopped or ComputerUsePhase.TimedOut)
            {
                TaskStatus = progress.Phase switch
                {
                    ComputerUsePhase.Completed or ComputerUsePhase.Failed or ComputerUsePhase.Stopped =>
                        progress.Detail ?? T("ComputerUse.Stopped", "Stopped"),
                    ComputerUsePhase.TimedOut => T("Browser.TimedOut", "The browser task reached its active time limit."),
                    ComputerUsePhase.Typing => T("ComputerUse.Typing", "Entering text"),
                    ComputerUsePhase.Moving => T("ComputerUse.Moving", "Moving the AI cursor"),
                    ComputerUsePhase.Clicking => T("ComputerUse.Clicking", "Clicked"),
                    ComputerUsePhase.Navigating => T("Browser.Navigating", "Navigating to address"),
                    ComputerUsePhase.OtherAction => T("ComputerUse.OtherAction", "Input sent"),
                    _ => T("Browser.Observing", "Observing webpage")
                };
            }
            RefreshControl();
        });
    }

    private void RefreshControl()
    {
        OnPropertyChanged(nameof(CanTakeOver));
        OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(CanHoverPage));
    }

    private void OnInteractionRequested(BrowserInteraction request)
    {
        if (request.InTask) _computerUse.RequestBrowserTakeover(T("Browser.Interaction.Required", "The website needs your input."));
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed)
            {
                _ = AnswerAsync(request, new { });
                return;
            }
            _interactions.Enqueue(request);
            if (!_handlingInteraction) _ = ProcessInteractionsAsync();
        });
    }

    private async Task ProcessInteractionsAsync()
    {
        _handlingInteraction = true;
        try
        {
            while (_interactions.TryDequeue(out var request))
            {
                RefreshControl();
                SelectedTab = request.InTask ? 1 : 0;
                if (_browser.Tabs(request.InTask).FirstOrDefault(page => page.Id == request.Tab) is { } tab)
                    _browser.Select(tab);
                ShowPane();
                object reply = new { };
                try
                {
                    if (request.Kind == "file" && _interaction != null)
                    {
                        var files = await _interaction.PickFilesAsync(T("Browser.Upload", "Choose files to upload"),
                            T("Browser.AllFiles", "All files"), ["*"], request.Request.GetProperty("multiple").GetBoolean());
                        reply = new { files };
                    }
                    else if (request.Kind == "download" && _interaction != null)
                    {
                        var folder = _browser.DownloadDirectory;
                        if (string.IsNullOrWhiteSpace(folder)) folder = request.Text("directory");
                        var path = await _interaction.PickBrowserDownloadAsync(T("Browser.SaveDownload", "Save download"),
                            request.Text("name"), folder);
                        reply = new { path };
                    }
                    else if (request.Kind is "dialog" or "permission" or "authentication")
                    {
                        var authentication = request.Kind == "authentication";
                        var permission = request.Kind == "permission";
                        var caption = authentication ? T("Browser.SignIn", "Website sign-in")
                            : permission ? T("Browser.Permission", "Website permission") : T("Browser.WebDialog", "Website message");
                        var text = authentication ? request.Text("url") : permission
                            ? $"{request.Text("url")} · {request.Text("permission")}" : request.Text("message");
                        var prompt = new BrowserUserPrompt(caption, text, authentication,
                            request.Text("dialogType") == "prompt", request.Text("defaultValue"));
                        Prompt = prompt;
                        if (await prompt.Confirmation.Task)
                            reply = new { accept = true, text = prompt.Text, user = prompt.User, password = prompt.Password };
                        Prompt = null;
                    }
                }
                catch (Exception ex) { SetStatus(request.InTask, ex.Message); }
                await AnswerAsync(request, reply);
            }
        }
        finally
        {
            _handlingInteraction = false;
            RefreshControl();
        }
    }

    private static async Task AnswerAsync(BrowserInteraction request, object reply)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await request.RespondAsync(reply, timeout.Token); }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException or OperationCanceledException) { }
    }

    private void OnDownloadCompleted(string name, bool success, bool task) => Dispatcher.UIThread.Post(() =>
    {
        if (_disposed) return;
        SetStatus(task, string.Format(success ? T("Browser.Downloaded", "Saved {0}")
            : T("Browser.DownloadFailed", "Could not save {0}"), name));
    });

    private string T(string key, string fallback) => _localization.GetString(key, fallback);

    private void SetStatus(bool inTask, string message)
    {
        if (inTask) TaskStatus = message;
        else BrowseStatus = message;
    }

    internal void ReportPreviewError(string message)
    {
        SetStatus(IsTask, message);
        Log.Debug("Embedded browser preview ended: {Error}", message);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _browser.PagesChanged -= OnPagesChanged;
        _browser.InteractionRequested -= OnInteractionRequested;
        _browser.DownloadCompleted -= OnDownloadCompleted;
        _computerUse.ProgressChanged -= OnProgress;
        Prompt?.Confirmation.TrySetResult(false);
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}

public sealed record BrowserPageOption(string Id, string Label);

public sealed partial class BrowserUserPrompt(string title, string message, bool authentication, bool textInput, string defaultValue)
    : ViewModelBase
{
    public string Title => title;
    public string Message => message;
    public bool Authentication => authentication;
    public bool TextInput => textInput;
    internal TaskCompletionSource<bool> Confirmation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    [ObservableProperty] private string _user = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _text = defaultValue;
    [RelayCommand] private void Accept() => Confirmation.TrySetResult(true);
    [RelayCommand] private void Cancel() => Confirmation.TrySetResult(false);
}
