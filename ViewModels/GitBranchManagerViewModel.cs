using Athena.UI.Models;
using Athena.UI.Services;
using Athena.UI.Services.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.ViewModels;

public enum GitBranchDialogMode { Switch, Create, Manage }
public sealed record GitBranchStartPoint(string Reference, string Name);
public sealed record GitBranchRow(GitBranchInfo Branch, string Badges, string Metadata, string Details)
{
    public string Name => Branch.Name;
}

public partial class GitBranchManagerViewModel : ObservableObject, IDisposable
{
    private readonly GitBranchService _service;
    private readonly string _directory;
    private readonly ILocalizationService _localization;
    private readonly Func<string?> _mutationGuard;
    private readonly Func<bool, Task> _changed;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<GitBranchRow> _allBranches = new();
    private GitBranchSnapshot? _snapshot;
    private GitBranchInfo? _pendingDeletion;
    private bool _disposed;

    public GitBranchManagerViewModel(GitBranchService service, string directory, ILocalizationService localization,
        Func<string?> mutationGuard, Func<bool, Task> changed, GitBranchDialogMode mode)
    {
        _service = service;
        _directory = directory;
        _localization = localization;
        _mutationGuard = mutationGuard;
        _changed = changed;
        Mode = mode;
    }

    public GitBranchDialogMode Mode { get; }
    public bool IsCreateMode => Mode == GitBranchDialogMode.Create;
    public bool IsBrowseMode => !IsCreateMode;
    public bool IsManageMode => Mode == GitBranchDialogMode.Manage;
    public string Title => L(IsCreateMode ? "Create" : IsManageMode ? "Manage" : "Switch");
    public ObservableCollection<GitBranchRow> Branches { get; } = new();
    public ObservableCollection<GitBranchStartPoint> StartPoints { get; } = new();
    [ObservableProperty] private GitBranchRow? _selectedBranch;
    [ObservableProperty] private GitBranchStartPoint? _selectedStartPoint;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _showRemote;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _repositoryTitle = string.Empty;
    [ObservableProperty] private string _currentBranch = string.Empty;
    [ObservableProperty] private string _baseBranchName = "—";
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private string _errorText = string.Empty;
    [ObservableProperty] private string _newBranchName = string.Empty;
    [ObservableProperty] private bool _switchAfterCreate = true;
    [ObservableProperty] private string _trackingBranchName = string.Empty;
    [ObservableProperty] private bool _isRenaming;
    [ObservableProperty] private string _renameText = string.Empty;
    [ObservableProperty] private bool _isDeleteConfirmVisible;
    [ObservableProperty] private string _deleteMessage = string.Empty;
    [ObservableProperty] private string _comparisonText = string.Empty;
    public bool HasError => ErrorText.Length > 0;
    public bool HasComparison => ComparisonText.Length > 0;
    public bool IsEmpty => !IsBusy && Branches.Count == 0;
    public bool NeedsTrackingName => SelectedBranch?.Branch.IsRemote == true;
    public bool CanSwitch => !IsBusy && _snapshot?.HasCommits == true && SelectedBranch is { Branch.IsCurrent: false, Branch.WorktreePath: null }
        && (!NeedsTrackingName || !string.IsNullOrWhiteSpace(TrackingBranchName));
    public bool CanCreate => !IsBusy && _snapshot?.HasCommits == true && SelectedStartPoint != null && !string.IsNullOrWhiteSpace(NewBranchName);
    public bool CanRename => !IsBusy && SelectedBranch is { Branch.IsRemote: false, Branch.IsDefault: false } row
        && (row.Branch.IsCurrent || row.Branch.WorktreePath == null);
    public bool CanDelete => CanRename && SelectedBranch is { Branch.IsCurrent: false, Branch.IsMerged: true } row
        && row.Branch.Reference != _snapshot?.BaseReference;
    public bool CanCompare => !IsBusy && _snapshot?.HasCommits == true && SelectedBranch != null;
    public string SelectionDetails => SelectedBranch?.Details ?? string.Empty;
    public string LocalTitle => $"{L("Local")} {_allBranches.Count(b => !b.Branch.IsRemote)}";
    public string RemoteTitle => $"{L("Remote")} {_allBranches.Count(b => b.Branch.IsRemote)}";
    public string SwitchLabel => SelectedBranch?.Branch.IsCurrent == true ? L("Current") : NeedsTrackingName ? L("TrackAndSwitch") : L("SwitchTo");

    private string L(string suffix) => _localization.GetString("Git.Branches." + suffix, suffix);
    partial void OnSearchTextChanged(string value) => Filter();
    partial void OnShowRemoteChanged(bool value) => Filter();
    partial void OnSelectedBranchChanged(GitBranchRow? value)
    {
        CancelEdit();
        ComparisonText = string.Empty;
        TrackingBranchName = value?.Branch.IsRemote == true ? value.Name[(value.Name.IndexOf('/') + 1)..] : string.Empty;
        NotifyState();
    }
    partial void OnSelectedStartPointChanged(GitBranchStartPoint? value) => NotifyState();
    partial void OnIsBusyChanged(bool value) => NotifyState();
    partial void OnNewBranchNameChanged(string value) => NotifyState();
    partial void OnTrackingBranchNameChanged(string value) => NotifyState();
    partial void OnErrorTextChanged(string value) => OnPropertyChanged(nameof(HasError));
    partial void OnComparisonTextChanged(string value) => OnPropertyChanged(nameof(HasComparison));

    private void NotifyState()
    {
        foreach (var name in new[] { nameof(CanSwitch), nameof(CanCreate), nameof(CanRename), nameof(CanDelete), nameof(CanCompare),
            nameof(NeedsTrackingName), nameof(SelectionDetails), nameof(SwitchLabel), nameof(IsEmpty), nameof(LocalTitle), nameof(RemoteTitle) }) OnPropertyChanged(name);
        SwitchCommand.NotifyCanExecuteChanged(); CreateCommand.NotifyCanExecuteChanged(); RenameCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged(); CompareCommand.NotifyCanExecuteChanged();
    }

    private void Filter()
    {
        var selected = SelectedBranch?.Branch.Reference;
        Branches.Clear();
        foreach (var row in _allBranches.Where(row => row.Branch.IsRemote == ShowRemote && row.Name.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(row => row.Branch.IsCurrent).ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)) Branches.Add(row);
        SelectedBranch = Branches.FirstOrDefault(b => b.Branch.Reference == selected) ?? Branches.FirstOrDefault();
        NotifyState();
    }

    [RelayCommand]
    private Task RefreshAsync() => RunAsync(async ct => await LoadCoreAsync(ct));

    private async Task LoadCoreAsync(CancellationToken ct)
    {
        _snapshot = await _service.ReadAsync(_directory, ct);
        CurrentBranch = _snapshot.CurrentName;
        RepositoryTitle = _snapshot.RepositoryRoot;
        var baseName = _snapshot.BaseReference?.Replace("refs/heads/", "", StringComparison.Ordinal).Replace("refs/remotes/", "", StringComparison.Ordinal) ?? "HEAD";
        BaseBranchName = _snapshot.HasCommits ? baseName : "—";
        _allBranches.Clear();
        foreach (var branch in _snapshot.Branches)
        {
            var tags = new List<string>();
            if (branch.IsCurrent) tags.Add(L("Current"));
            if (branch.IsDefault) tags.Add(L("Default"));
            if (branch.IsMerged && !branch.IsCurrent && branch.Reference != _snapshot.BaseReference) tags.Add(string.Format(L("MergedInto"), baseName));
            if (branch.WorktreePath != null && !branch.IsCurrent) tags.Add(L("Occupied"));
            var upstream = branch.Upstream.Replace("refs/remotes/", "", StringComparison.Ordinal).Replace("refs/heads/", "", StringComparison.Ordinal);
            var tracking = branch.TrackingStatus.Replace("ahead", L("Ahead"), StringComparison.Ordinal).Replace("behind", L("Behind"), StringComparison.Ordinal).Replace("gone", L("Gone"), StringComparison.Ordinal);
            var details = branch.IsRemote ? L("RemoteSnapshot") : upstream.Length == 0 ? L("NoUpstream") : $"{L("Upstream")} {upstream} · {(tracking.Length == 0 ? L("Synced") : tracking)}";
            if (branch.WorktreePath != null && !branch.IsCurrent) details += $" · {branch.WorktreePath}";
            var metadata = $"{branch.CommitId[..7]} · {branch.Subject} · {branch.CommittedAt?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}";
            _allBranches.Add(new(branch, string.Join(" · ", tags), metadata, details));
        }
        var previousStart = SelectedStartPoint?.Reference;
        StartPoints.Clear();
        if (_snapshot.HasCommits) StartPoints.Add(new("HEAD", $"HEAD · {CurrentBranch}"));
        foreach (var branch in _snapshot.Branches) StartPoints.Add(new(branch.Reference, branch.Name));
        SelectedStartPoint = StartPoints.FirstOrDefault(b => b.Reference == previousStart) ?? StartPoints.FirstOrDefault();
        Filter();
        StatusText = !_snapshot.HasCommits ? L("Error.Unborn") : _snapshot.OperationInProgress ? L("Error.InProgress") : _snapshot.IsDirty ? L("DirtyHint") : L("RemoteSnapshot");
    }

    [RelayCommand(CanExecute = nameof(CanSwitch))]
    private Task SwitchAsync() => MutateAsync(ct => _service.SwitchAsync(_directory, SelectedBranch!.Branch, TrackingBranchName.Trim(), ct), true);
    [RelayCommand(CanExecute = nameof(CanCreate))]
    private Task CreateAsync() => MutateAsync(ct => _service.CreateAsync(_directory, NewBranchName.Trim(), SelectedStartPoint!.Reference, SwitchAfterCreate, ct), SwitchAfterCreate);
    [RelayCommand(CanExecute = nameof(CanRename))]
    private void Rename() { IsDeleteConfirmVisible = false; RenameText = SelectedBranch!.Name; IsRenaming = true; }
    [RelayCommand]
    private Task SaveRenameAsync() => !CanRename || !IsRenaming ? Task.CompletedTask : MutateAsync(ct => _service.RenameAsync(_directory, SelectedBranch!.Branch, RenameText.Trim(), ct), false);
    [RelayCommand(CanExecute = nameof(CanDelete))]
    private void Delete()
    {
        IsRenaming = false;
        _pendingDeletion = SelectedBranch!.Branch;
        DeleteMessage = string.Format(L("DeleteConfirm"), _pendingDeletion.Name);
        IsDeleteConfirmVisible = true;
    }
    [RelayCommand]
    private Task ConfirmDeleteAsync() => !IsDeleteConfirmVisible || _pendingDeletion == null || IsBusy ? Task.CompletedTask
        : MutateAsync(ct => _service.DeleteAsync(_directory, _pendingDeletion, ct), false);
    [RelayCommand]
    private void CancelEdit() { IsRenaming = false; IsDeleteConfirmVisible = false; _pendingDeletion = null; }
    [RelayCommand(CanExecute = nameof(CanCompare))]
    private Task CompareAsync() => RunAsync(async ct =>
    {
        var output = await _service.CompareAsync(_directory, SelectedBranch!.Branch, ct);
        ComparisonText = string.IsNullOrWhiteSpace(output.Text) ? L("NoDiff") : $"HEAD → {SelectedBranch.Name}\n{output.Text}";
        if (output.IsTruncated) ComparisonText += "\n" + L("DiffTruncated");
    });

    private Task MutateAsync(Func<CancellationToken, Task> operation, bool worktreeChanged) => RunAsync(async ct =>
    {
        var reason = _mutationGuard();
        if (reason != null) { ErrorText = reason; return; }
        await operation(ct);
        CancelEdit();
        StatusText = L("Success");
        try
        {
            await _changed(worktreeChanged);
            await LoadCoreAsync(ct);
            StatusText = L("Success");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error(ex, "Git operation succeeded, but branch view refresh failed");
            ErrorText = L("Error.Refresh");
        }
    });

    private async Task RunAsync(Func<CancellationToken, Task> operation)
    {
        if (IsBusy || _disposed) return;
        IsBusy = true;
        ErrorText = string.Empty;
        try { await operation(_lifetime.Token); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { /* The owning dialog was closed. */ }
        catch (GitBranchException ex)
        {
            ErrorText = L("Error." + ex.Code);
            if (ex.Code is "Failed" or "Occupied") ErrorText += "\n" + ex.Message[..Math.Min(ex.Message.Length, 1800)].Trim();
        }
        catch (Exception ex) { Log.Error(ex, "Git branch operation failed"); ErrorText = L("Error.Failed") + "\n" + ex.Message; }
        finally { IsBusy = false; }
    }

    public void Dispose() { if (_disposed) return; _disposed = true; _lifetime.Cancel(); _lifetime.Dispose(); }
}
