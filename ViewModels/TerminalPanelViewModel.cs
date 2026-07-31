using Athena.UI.Services;
using Athena.UI.Services.Interfaces;
using Avalonia.Threading;
using AvaloniaTerminal;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace Athena.UI.ViewModels;

public partial class TerminalPanelViewModel : ViewModelBase, IDisposable
{
    public const string GlobalScopeKey = "__global__";

    private readonly ITerminalSessionManager? _manager;
    private readonly ILogger _logger = Log.ForContext<TerminalPanelViewModel>();
    private readonly Dictionary<string, TerminalSessionViewModel> _viewModels = new(StringComparer.Ordinal);
    private bool _disposed;

    public TerminalPanelViewModel()
    {
    }

    public TerminalPanelViewModel(ITerminalSessionManager manager)
    {
        _manager = manager;
        _manager.SessionsChanged += OnSessionsChanged;
    }

    public event EventHandler? AllTerminalsClosed;

    public ObservableCollection<TerminalSessionViewModel> Sessions { get; } = new();

    [ObservableProperty]
    private TerminalSessionViewModel? _selectedSession;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NewTerminalCommand))]
    private bool _isCreating;

    public bool HasMultipleSessions => Sessions.Count > 1;

    public string ActiveScopeKey { get; private set; } = GlobalScopeKey;

    public string ActiveWorkingDirectory { get; private set; } = GetUserProfileDirectory();

    public void ActivateScope(string? workspaceId, string? workspaceDirectory)
    {
        ActiveScopeKey = string.IsNullOrWhiteSpace(workspaceId) ? GlobalScopeKey : workspaceId;
        ActiveWorkingDirectory = string.IsNullOrWhiteSpace(workspaceId)
            ? GetUserProfileDirectory()
            : ResolveDirectory(workspaceDirectory);
        RefreshCurrentScope();
    }

    public async Task EnsureTerminalAsync()
    {
        if (Sessions.Count == 0)
            await CreateTerminalAsync();
    }

    [RelayCommand(CanExecute = nameof(CanCreateTerminal))]
    private async Task NewTerminalAsync() => await CreateTerminalAsync();

    [RelayCommand]
    private async Task CloseTerminalAsync(TerminalSessionViewModel? session)
    {
        if (_manager == null || session == null) return;
        await _manager.CloseAsync(ActiveScopeKey, session.Id);
    }

    [RelayCommand]
    private async Task CloseOtherTerminalsAsync(TerminalSessionViewModel? session)
    {
        if (_manager == null || session == null || Sessions.Count <= 1) return;
        await _manager.CloseOthersAsync(ActiveScopeKey, session.Id);
    }

    [RelayCommand]
    private async Task CloseAllTerminalsAsync()
    {
        if (_manager == null || Sessions.Count == 0) return;
        await _manager.CloseAllAsync(ActiveScopeKey);
    }

    public async Task CloseScopeAsync(string workspaceId)
    {
        if (_manager == null || string.IsNullOrWhiteSpace(workspaceId)) return;
        await _manager.CloseAllAsync(workspaceId);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_manager != null) _manager.SessionsChanged -= OnSessionsChanged;
        foreach (var viewModel in _viewModels.Values) viewModel.Dispose();
        _viewModels.Clear();
        Sessions.Clear();
    }

    private bool CanCreateTerminal() => !IsCreating && _manager != null;

    private async Task CreateTerminalAsync()
    {
        if (_manager == null || IsCreating) return;
        IsCreating = true;
        try
        {
            var session = await _manager.CreateAsync(ActiveScopeKey, ActiveWorkingDirectory);
            RefreshCurrentScope(session.Id);
        }
        catch (Exception ex)
        {
            _logger.Error(
                ex,
                "创建终端失败: Scope={Scope}, WorkingDirectory={WorkingDirectory}",
                ActiveScopeKey,
                ActiveWorkingDirectory);
        }
        finally
        {
            IsCreating = false;
        }
    }

    private void OnSessionsChanged(object? sender, TerminalSessionsChangedEventArgs e)
    {
        if (!string.Equals(e.ScopeKey, ActiveScopeKey, StringComparison.Ordinal)) return;
        Dispatcher.UIThread.Post(() =>
        {
            var hadSessions = Sessions.Count > 0;
            RefreshCurrentScope();
            if (hadSessions && Sessions.Count == 0)
                AllTerminalsClosed?.Invoke(this, EventArgs.Empty);
        });
    }

    private void RefreshCurrentScope(string? preferredSessionId = null)
    {
        if (_manager == null) return;
        var sessions = _manager.GetSessions(ActiveScopeKey);
        var activeIds = sessions.Select(session => session.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var staleId in _viewModels.Keys.Where(id => !activeIds.Contains(id)).ToList())
        {
            _viewModels[staleId].Dispose();
            _viewModels.Remove(staleId);
        }

        var previousId = preferredSessionId ?? SelectedSession?.Id;
        Sessions.Clear();
        foreach (var session in sessions)
        {
            if (!_viewModels.TryGetValue(session.Id, out var viewModel))
            {
                viewModel = new TerminalSessionViewModel(session);
                _viewModels[session.Id] = viewModel;
            }
            Sessions.Add(viewModel);
        }

        SelectedSession = Sessions.FirstOrDefault(session => session.Id == previousId)
                          ?? Sessions.LastOrDefault();
        OnPropertyChanged(nameof(HasMultipleSessions));
    }

    private static string GetUserProfileDirectory() =>
        ResolveDirectory(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    private static string ResolveDirectory(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && System.IO.Directory.Exists(path))
            return System.IO.Path.GetFullPath(path);
        var home = OperatingSystem.IsWindows()
            ? Environment.GetEnvironmentVariable("USERPROFILE")
            : Environment.GetEnvironmentVariable("HOME");
        return !string.IsNullOrWhiteSpace(home) && System.IO.Directory.Exists(home)
            ? home
            : Environment.CurrentDirectory;
    }
}

public partial class TerminalSessionViewModel : ViewModelBase, IDisposable
{
    private readonly TerminalSession _session;
    private bool _disposed;

    public TerminalSessionViewModel(TerminalSession session)
    {
        _session = session;
        Model = new TerminalControlModel(new TerminalOptions
        {
            Cols = 100,
            Rows = 24,
            Scrollback = 5000,
            ReflowOnResize = false,
            TermName = "xterm-256color"
        });
        _session.OutputReceived += OnOutputReceived;
        _session.Exited += OnExited;
        Model.UserInput += (_, e) => _ = _session.WriteAsync(e.Data);
        Model.SizeChanged += (_, _) =>
            _session.Resize(Model.Terminal.Cols, Model.Terminal.Rows);
    }

    public string Id => _session.Id;

    public string Name => _session.Name;

    public string WorkingDirectory => _session.WorkingDirectory;

    public TerminalControlModel Model { get; }

    [ObservableProperty]
    private bool _isRunning = true;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session.OutputReceived -= OnOutputReceived;
        _session.Exited -= OnExited;
    }

    private void OnOutputReceived(object? sender, TerminalOutputEventArgs e)
    {
        var data = e.Data;
        Dispatcher.UIThread.Post(() => Model.Feed(data, data.Length));
    }

    private void OnExited(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() => IsRunning = false);
}
