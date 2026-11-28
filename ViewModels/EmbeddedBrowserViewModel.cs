using Athena.UI.Services.ComputerUse;
using Athena.UI.Services.Interfaces;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
    private readonly Func<string, Task>? _injectedClipboard;
    private Func<string, Task>? _viewClipboard;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Queue<BrowserInteraction> _interactions = new();
    private ChromeBrowserPage? _lastPage;
    private IDisposable? _noticeTimeout;
    private bool _handlingInteraction;
    private bool _updatingTabs;
    private bool _addressEditing;
    private bool _disposed;

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(FullViewToggleIconKey)), NotifyPropertyChangedFor(nameof(FullViewToggleTip))]
    private bool _isFullView;
    [ObservableProperty] private bool _isConversationTabSelected;
    [ObservableProperty] private string _address = "";
    [ObservableProperty] private string _browseStatus = "";
    [ObservableProperty] private string _taskStatus = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasNotice))] private string _notice = "";
    [ObservableProperty] private BrowserTabItemViewModel? _activePage;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasPrompt))]
    private BrowserUserPrompt? _prompt;

    public EmbeddedBrowserViewModel(
        EmbeddedBrowser browser,
        ComputerUseService computerUse,
        ILocalizationService localization,
        IExternalUrlOpener external,
        IUserInteractionService? interaction = null,
        Func<string, Task>? copyToClipboard = null)
    {
        _browser = browser;
        _computerUse = computerUse;
        _localization = localization;
        _external = external;
        _interaction = interaction;
        _injectedClipboard = copyToClipboard;
        _browser.PagesChanged += OnPagesChanged;
        _browser.InteractionRequested += OnInteractionRequested;
        _browser.DownloadCompleted += OnDownloadCompleted;
        _browser.UserInteracted += OnBrowserUserInteracted;
        _computerUse.ProgressChanged += OnProgress;
        _localization.LanguageChanged += OnLanguageChanged;
    }

    public event EventHandler? RevealRequested;
    public ObservableCollection<BrowserTabItemViewModel> Tabs { get; } = [];
    public IReadOnlyList<BrowserTabItemViewModel> Pages => Tabs;
    public bool IsTask => CurrentPage?.IsTask == true || (CurrentPage is null && _computerUse.HasBrowserTask);
    public bool IsBrowsing => !IsTask;
    public bool HasPage => CurrentPage is not null;
    public bool HasLivePage => CurrentPage is { IsPlaceholder: false };
    public bool HasMultipleTabs => Tabs.Count > 1;
    public bool HasPrompt => Prompt != null;
    public bool HasNotice => IsTask && Notice.Length != 0;
    public bool CanTakeOver => IsTask && _computerUse.HasBrowserTask && !_computerUse.IsBrowserPaused;
    public bool CanResume => IsTask && _computerUse.IsBrowserPaused && !_handlingInteraction;
    public string FullViewToggleIconKey => IsFullView ? "AthenaIconBrowserExitFullView" : "AthenaIconBrowserEnterFullView";
    public string FullViewToggleTip => IsFullView
        ? T("Browser.ExitFullView", "Exit full view")
        : T("Browser.EnterFullView", "Enter full view");
    internal ChromeBrowserPage? CurrentPage => _browser.ActivePage;
    internal string RuntimeDirectory => _browser.RuntimeDirectory;

    internal void SetClipboardHandler(Func<string, Task>? handler) => _viewClipboard = handler;
    internal void SetPreferredViewport(BrowserViewportSize size) => _browser.SetPreferredViewport(size);
    internal void UpdateSurface(nint parentHandle, BrowserViewportSize size, bool visible, ChromeBrowserPage? displayedPage) =>
        _browser.UpdateSurface(parentHandle, size, visible, displayedPage);
    internal void DetachSurface() => _browser.DetachSurface();
    internal void SetSurfaceFocus(bool focused) => _browser.SetSurfaceFocus(focused);
    internal void SetAddressEditing(bool editing)
    {
        _addressEditing = editing;
        if (editing) _browser.SetSurfaceFocus(false);
        else RefreshPage();
    }

    partial void OnIsFullViewChanged(bool value)
    {
        if (!value && IsConversationTabSelected)
            IsConversationTabSelected = false;
        SyncTabActiveStates();
    }

    partial void OnIsConversationTabSelectedChanged(bool value)
    {
        if (value && !IsFullView)
        {
            IsConversationTabSelected = false;
            return;
        }
        SyncTabActiveStates();
    }

    partial void OnActivePageChanged(BrowserTabItemViewModel? value)
    {
        if (value == null || _updatingTabs) return;
        SelectTab(value);
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (_disposed) return;
        OnPropertyChanged(nameof(FullViewToggleTip));
        RefreshPage();
    });

    private void OnPagesChanged() => Dispatcher.UIThread.Post(() =>
    {
        if (!_disposed) RefreshPage();
    });

    private void SyncTabActiveStates()
    {
        var selectedId = IsConversationTabSelected ? null : CurrentPage?.Id;
        foreach (var tab in Tabs)
            tab.IsActive = selectedId is not null && tab.Id == selectedId;
    }

    private void RefreshPage()
    {
        var selected = CurrentPage;
        var all = _browser.AllTabs();
        var previousCount = Tabs.Count;
        var existingById = new Dictionary<string, BrowserTabItemViewModel>(Tabs.Count, StringComparer.Ordinal);
        foreach (var item in Tabs)
            existingById[item.Id] = item;

        var liveIds = new HashSet<string>(all.Select(p => p.Id), StringComparer.Ordinal);

        _updatingTabs = true;
        try
        {
            for (var i = Tabs.Count - 1; i >= 0; i--)
            {
                if (!liveIds.Contains(Tabs[i].Id))
                    Tabs.RemoveAt(i);
            }

            for (var i = 0; i < all.Count; i++)
            {
                var page = all[i];
                var label = ResolveTabLabel(page);
                var hasUrl = EmbeddedBrowser.IsPageUri(page.Address);
                var tip = BuildTabToolTip(page, label, hasUrl);
                var canCloseOthers = all.Count > 1;
                var canCloseToRight = i < all.Count - 1;
                var muteHeader = page.IsMuted
                    ? T("Browser.Tab.Unmute", "Unmute tab")
                    : T("Browser.Tab.Mute", "Mute tab");
                var muteIcon = page.IsMuted ? "AthenaIconBrowserUnmute" : "AthenaIconBrowserMute";
                var isActive = !IsConversationTabSelected && ReferenceEquals(page, selected);

                if (existingById.TryGetValue(page.Id, out var tab))
                {
                    var currentIndex = Tabs.IndexOf(tab);
                    if (currentIndex >= 0 && currentIndex != i)
                        Tabs.Move(currentIndex, i);
                    tab.Update(label, page.Address, tip, page.IsTask, page.IsMuted, isActive,
                        hasUrl, canCloseOthers, canCloseToRight, muteHeader, muteIcon);
                }
                else
                {
                    var created = new BrowserTabItemViewModel(this, page.Id, label, page.Address, tip,
                        page.IsTask, page.IsMuted, isActive, hasUrl, canCloseOthers, canCloseToRight,
                        muteHeader, muteIcon);
                    Tabs.Insert(i, created);
                }
            }

            ActivePage = selected is not null ? Tabs.FirstOrDefault(t => t.Id == selected.Id) : null;
        }
        finally
        {
            _updatingTabs = false;
        }

        if (previousCount != Tabs.Count)
            OnPropertyChanged(nameof(HasMultipleTabs));

        var changed = !ReferenceEquals(_lastPage, selected);
        if (changed || !_addressEditing)
            Address = selected != null && selected.Address != EmbeddedBrowser.Blank ? selected.Address.AbsoluteUri : "";

        OnPropertyChanged(nameof(IsTask));
        OnPropertyChanged(nameof(IsBrowsing));
        OnPropertyChanged(nameof(HasNotice));
        RefreshControl();

        if (changed)
        {
            _lastPage = selected;
            OnPropertyChanged(nameof(CurrentPage));
            OnPropertyChanged(nameof(HasPage));
            OnPropertyChanged(nameof(HasLivePage));
        }

        // Closing the last remaining tab closes the browser pane (and exits full view if active),
        // restoring the right workbench/logs in split view without leaving an empty 0-tab shell.
        if (previousCount > 0 && Tabs.Count == 0 && IsOpen && !_browser.HasPendingOpen)
            Close();
    }

    private string ResolveTabLabel(ChromeBrowserPage page) =>
        page.Title.Length != 0
            ? page.Title
            : page.Address == EmbeddedBrowser.Blank
                ? T("Browser.NewTab", "New tab")
                : page.Address.Host;

    private string BuildTabToolTip(ChromeBrowserPage page, string label, bool hasUrl)
    {
        var badges = new List<string>(2);
        if (page.IsTask) badges.Add(T("Browser.Tab.TaskBadge", "Web task tab"));
        if (page.IsMuted) badges.Add(T("Browser.Tab.MutedBadge", "Muted"));
        var header = badges.Count > 0 ? $"{label} · {string.Join(" · ", badges)}" : label;
        if (hasUrl && !string.Equals(label, page.Address.AbsoluteUri, StringComparison.Ordinal))
            return $"{header}\n{page.Address.AbsoluteUri}";
        return header;
    }

    [RelayCommand]
    public void Reveal()
    {
        if (IsFullView && IsConversationTabSelected)
        {
            IsConversationTabSelected = false;
            return;
        }
        ShowPane();
        if (!HasPage)
        {
            var home = _browser.HomePage;
            _browser.OpenBlankTab(home);
            RefreshPage();
            if (home is not null)
                _ = BrowseAsync(token => _browser.OpenAsync(home, token), inTask: false);
        }
    }

    private void ShowPane()
    {
        IsOpen = true;
        RevealRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Close()
    {
        IsConversationTabSelected = false;
        IsFullView = false;
        IsOpen = false;
    }

    [RelayCommand]
    private void ToggleFullView()
    {
        if (IsFullView) ExitFullView();
        else EnterFullView();
    }

    internal void EnterFullView()
    {
        IsFullView = true;
        IsConversationTabSelected = false;
        ShowPane();
    }

    [RelayCommand]
    private void ExitFullView()
    {
        IsConversationTabSelected = false;
        IsFullView = false;
    }

    [RelayCommand]
    private void SelectConversationTab()
    {
        if (!IsFullView) return;
        IsConversationTabSelected = true;
    }

    internal void SelectTab(BrowserTabItemViewModel tab)
    {
        var wasConversation = IsConversationTabSelected;
        IsConversationTabSelected = false;
        if (_browser.AllTabs().FirstOrDefault(page => page.Id == tab.Id) is { } page)
        {
            var switching = !ReferenceEquals(CurrentPage, page);
            if (switching)
            {
                _browser.Select(page);
                if (page.IsTask)
                {
                    _computerUse.BrowserTaskPageChanged();
                    WarnAboutManualInput();
                }
            }
            if (switching || wasConversation)
                RefreshPage();
        }
    }

    internal bool MoveTab(string tabId, int targetIndex)
    {
        if (!_browser.MoveTab(tabId, targetIndex))
            return false;
        RefreshPage();
        return true;
    }

    public Task OpenLinkAsync(Uri address)
    {
        if (!EmbeddedBrowser.IsPageUri(address)) return Task.CompletedTask;
        IsConversationTabSelected = false;
        Address = address.AbsoluteUri;
        ShowPane();
        return BrowseAsync(token => _browser.OpenAsync(address, token), inTask: false);
    }

    [RelayCommand]
    private Task NavigateAsync()
    {
        if (!EmbeddedBrowser.TryNormalizeUserAddress(Address, out var address))
        {
            SetStatus(IsTask, T("Browser.InvalidAddress", "Enter an HTTP or HTTPS address."));
            return Task.CompletedTask;
        }
        IsConversationTabSelected = false;
        Address = address.AbsoluteUri;
        return CurrentPage is { IsTask: true }
            ? WithPageAsync((page, token) => page.NavigateAsync(address, token))
            : BrowseAsync(token => _browser.OpenAsync(address, token), inTask: false);
    }

    [RelayCommand]
    private Task BackAsync() => WithLivePageAsync((page, token) => page.GoAsync(-1, token));

    [RelayCommand]
    private Task ForwardAsync() => WithLivePageAsync((page, token) => page.GoAsync(1, token));

    [RelayCommand]
    private Task ReloadAsync() => WithLivePageAsync((page, token) => page.Driver.CallAsync("Page.reload", new { }, token));

    [RelayCommand]
    private Task NewTabAsync()
    {
        IsConversationTabSelected = false;
        if (_browser.HomePage is { } home)
            return BrowseAsync(token => _browser.NewTabAsync(home, token), inTask: false);
        _browser.OpenBlankTab();
        RefreshPage();
        return Task.CompletedTask;
    }

    [RelayCommand]
    private Task CloseTabAsync() => WithPageAsync((page, token) => _browser.CloseTabAsync(page, token));

    internal Task CloseTabByIdAsync(string id)
    {
        var target = _browser.AllTabs().FirstOrDefault(page => page.Id == id);
        if (target is null) return Task.CompletedTask;
        return BrowseAsync(token => _browser.CloseTabAsync(target, token), target.IsTask);
    }

    internal Task CloseOtherTabsAsync(string id)
    {
        var all = _browser.AllTabs();
        var keep = all.FirstOrDefault(page => page.Id == id);
        if (keep is null) return Task.CompletedTask;
        var others = all.Where(page => page.Id != id).ToArray();
        if (others.Length == 0) return Task.CompletedTask;
        if (!ReferenceEquals(CurrentPage, keep))
            _browser.Select(keep);
        return BrowseAsync(token => _browser.CloseTabsAsync(others, token), keep.IsTask);
    }

    internal Task CloseTabsToRightAsync(string id)
    {
        var all = _browser.AllTabs();
        var index = -1;
        for (var i = 0; i < all.Count; i++)
        {
            if (all[i].Id == id) { index = i; break; }
        }
        if (index < 0 || index >= all.Count - 1) return Task.CompletedTask;
        var right = all.Skip(index + 1).ToArray();
        return BrowseAsync(token => _browser.CloseTabsAsync(right, token), all[index].IsTask);
    }

    internal Task CloseAllTabsAsync()
    {
        var all = _browser.AllTabs();
        if (all.Count == 0) return Task.CompletedTask;
        return BrowseAsync(token => _browser.CloseTabsAsync(all, token), inTask: false);
    }

    internal Task ToggleMuteTabAsync(string id)
    {
        var target = _browser.AllTabs().FirstOrDefault(page => page.Id == id);
        if (target is null) return Task.CompletedTask;
        var nextMuted = !target.IsMuted;
        return BrowseAsync(async token =>
        {
            await _browser.SetTabMutedAsync(target, nextMuted, token);
            RefreshPage();
        }, target.IsTask);
    }

    internal void OpenTabInSystemBrowser(string id)
    {
        var target = _browser.AllTabs().FirstOrDefault(page => page.Id == id);
        OpenLinkInSystemBrowser(target?.Address);
    }

    internal async Task CopyTabUrlAsync(string id)
    {
        var target = _browser.AllTabs().FirstOrDefault(page => page.Id == id);
        if (target is null || !EmbeddedBrowser.IsPageUri(target.Address)) return;
        var handler = _injectedClipboard ?? _viewClipboard;
        if (handler is null) return;
        try
        {
            await handler(target.Address.AbsoluteUri);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Copying browser tab URL to clipboard failed");
        }
    }

    [RelayCommand]
    private void TakeOver() => _computerUse.RequestBrowserTakeover();

    [RelayCommand]
    private Task ResumeAsync() => BrowseAsync(_computerUse.ResumeBrowserAsync, inTask: true);

    private Task WithPageAsync(Func<ChromeBrowserPage, CancellationToken, Task> operation) =>
        CurrentPage is { } page
            ? BrowseAsync(token => WithManualInputAsync(page, () => operation(page, token), token), page.IsTask)
            : Task.CompletedTask;

    private Task WithLivePageAsync(Func<ChromeBrowserPage, CancellationToken, Task> operation) =>
        CurrentPage is { IsPlaceholder: false } page
            ? BrowseAsync(token => WithManualInputAsync(page, () => operation(page, token), token), page.IsTask)
            : Task.CompletedTask;

    internal Task WithManualInputAsync(ChromeBrowserPage page, Func<Task> input, CancellationToken token, bool changesPage = true)
    {
        if (page.IsTask && changesPage) WarnAboutManualInput();
        return _computerUse.WithManualBrowserInputAsync(page, () =>
            ReferenceEquals(CurrentPage, page) ? input() : Task.CompletedTask, token, changesPage);
    }

    private void WarnAboutManualInput()
    {
        if (_computerUse.IsBrowserTaskRunning)
            ShowNotice(T("Browser.ManualInput", "Computer Use is running. Your input can affect the task."));
    }

    private void ShowNotice(string text)
    {
        if (_disposed || string.Equals(Notice, text, StringComparison.Ordinal)) return;
        _noticeTimeout?.Dispose();
        Notice = text;
        _noticeTimeout = DispatcherTimer.RunOnce(() =>
        {
            Notice = "";
            _noticeTimeout = null;
        }, TimeSpan.FromSeconds(4));
    }

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
                IsConversationTabSelected = false;
                if (_browser.TaskPage is { } taskPage)
                    _browser.Select(taskPage);
                ShowPane();
                RefreshPage();
            }
            if (progress.Phase == ComputerUsePhase.Paused)
            {
                TaskStatus = progress.Detail ?? T("Browser.Paused", "Paused for your input");
                IsConversationTabSelected = false;
                if (_browser.TaskPage is { } taskPage)
                    _browser.Select(taskPage);
                ShowPane();
                RefreshPage();
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
    }

    private void OnInteractionRequested(BrowserInteraction request)
    {
        if (DeclineRunningTaskPermission(request)) return;
        if (request.InTask && request.Kind != "permission")
            _computerUse.RequestBrowserTakeover(T("Browser.Interaction.Required", "The website needs your input."));
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

    private bool DeclineRunningTaskPermission(BrowserInteraction request)
    {
        if (!request.InTask || request.Kind != "permission" || !_computerUse.IsBrowserTaskRunning) return false;
        _ = AnswerAsync(request, new { accept = false });
        Dispatcher.UIThread.Post(() => ShowNotice(T("Browser.PermissionDeclined",
            "Website permission declined. You can take over and try again to grant it.")));
        return true;
    }

    private async Task ProcessInteractionsAsync()
    {
        _handlingInteraction = true;
        try
        {
            while (_interactions.TryDequeue(out var request))
            {
                if (DeclineRunningTaskPermission(request)) continue;
                RefreshControl();
                IsConversationTabSelected = false;
                if (_browser.AllTabs().FirstOrDefault(page => page.Id == request.Tab) is { } tab)
                    _browser.Select(tab);
                ShowPane();
                RefreshPage();
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
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException or OperationCanceledException)
        {
            // Tab closed or browser shut down before reply completed; safe to drop late response.
        }
    }

    private void OnBrowserUserInteracted(ChromeBrowserPage page)
    {
        if (!page.IsTask || !_computerUse.IsBrowserTaskRunning) return;
        _computerUse.BrowserTaskPageChanged();
        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed) WarnAboutManualInput();
        });
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
        _browser.UserInteracted -= OnBrowserUserInteracted;
        _computerUse.ProgressChanged -= OnProgress;
        _localization.LanguageChanged -= OnLanguageChanged;
        Prompt?.Confirmation.TrySetResult(false);
        _noticeTimeout?.Dispose();
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}

public sealed partial class BrowserTabItemViewModel : ViewModelBase
{
    private readonly EmbeddedBrowserViewModel _owner;

    [ObservableProperty] private string _label;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(Url))] private Uri _address;
    [ObservableProperty] private string _toolTipText;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(TabIconKey))] private bool _isTask;
    [ObservableProperty] private bool _isMuted;
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _hasNavigableUrl;
    [ObservableProperty] private bool _canCloseOthers;
    [ObservableProperty] private bool _canCloseToRight;
    [ObservableProperty] private string _muteMenuHeader;
    [ObservableProperty] private string _muteIconKey;

    internal BrowserTabItemViewModel(
        EmbeddedBrowserViewModel owner,
        string id,
        string label,
        Uri address,
        string toolTipText,
        bool isTask,
        bool isMuted,
        bool isActive,
        bool hasNavigableUrl,
        bool canCloseOthers,
        bool canCloseToRight,
        string muteMenuHeader,
        string muteIconKey)
    {
        _owner = owner;
        Id = id;
        _label = label;
        _address = address;
        _toolTipText = toolTipText;
        _isTask = isTask;
        _isMuted = isMuted;
        _isActive = isActive;
        _hasNavigableUrl = hasNavigableUrl;
        _canCloseOthers = canCloseOthers;
        _canCloseToRight = canCloseToRight;
        _muteMenuHeader = muteMenuHeader;
        _muteIconKey = muteIconKey;
    }

    public string Id { get; }
    public string Url => Address == EmbeddedBrowser.Blank ? "" : Address.AbsoluteUri;
    public string TabIconKey => IsTask ? "AthenaIconComputerUse" : "AthenaIconFileWeb";

    internal void Update(
        string label,
        Uri address,
        string toolTipText,
        bool isTask,
        bool isMuted,
        bool isActive,
        bool hasNavigableUrl,
        bool canCloseOthers,
        bool canCloseToRight,
        string muteMenuHeader,
        string muteIconKey)
    {
        Label = label;
        Address = address;
        ToolTipText = toolTipText;
        IsTask = isTask;
        IsMuted = isMuted;
        IsActive = isActive;
        HasNavigableUrl = hasNavigableUrl;
        CanCloseOthers = canCloseOthers;
        CanCloseToRight = canCloseToRight;
        MuteMenuHeader = muteMenuHeader;
        MuteIconKey = muteIconKey;
    }

    [RelayCommand] private Task CloseAsync() => _owner.CloseTabByIdAsync(Id);
    [RelayCommand] private Task CloseOthersAsync() => _owner.CloseOtherTabsAsync(Id);
    [RelayCommand] private Task CloseAllAsync() => _owner.CloseAllTabsAsync();
    [RelayCommand] private Task CloseToRightAsync() => _owner.CloseTabsToRightAsync(Id);
    [RelayCommand] private Task ToggleMuteAsync() => _owner.ToggleMuteTabAsync(Id);
    [RelayCommand] private void OpenExternal() => _owner.OpenTabInSystemBrowser(Id);
    [RelayCommand] private Task CopyUrlAsync() => _owner.CopyTabUrlAsync(Id);
}

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
