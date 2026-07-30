using Athena.UI.Models;
using Athena.UI.Services;
using Athena.UI.Services.Interfaces;
using Avalonia.Media.Imaging;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.ViewModels;

public partial class WorkspaceFileNodeViewModel : ViewModelBase
{
    public string Name { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public string RelativePath { get; init; } = string.Empty;
    public bool IsDirectory { get; init; }
    public bool IsFile => !IsDirectory;
    public bool IsFolderClosed => IsDirectory && !IsExpanded;
    public bool IsFolderOpen => IsDirectory && IsExpanded;
    public ObservableCollection<WorkspaceFileNodeViewModel> Children { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFolderClosed))]
    [NotifyPropertyChangedFor(nameof(IsFolderOpen))]
    private bool _isExpanded;
}

public partial class GitChangeFileViewModel : ViewModelBase
{
    public string RelativePath { get; init; } = string.Empty;
    public string? OriginalRelativePath { get; init; }
    public string FullPath { get; init; } = string.Empty;
    public string StatusCode { get; init; } = string.Empty;
    public string StatusLabel { get; init; } = string.Empty;
    public bool IsUntracked { get; init; }
    public bool HasStagedChange { get; init; }
    public bool HasWorkingTreeChange { get; init; }
    public bool CanStage => HasWorkingTreeChange || IsUntracked;
    public string StatusGlyph
    {
        get
        {
            if (IsUntracked) return "A";
            var indexStatus = StatusCode.Length > 0 ? StatusCode[0] : ' ';
            var workTreeStatus = StatusCode.Length > 1 ? StatusCode[1] : ' ';
            var status = indexStatus is not (' ' or '?') ? indexStatus : workTreeStatus;
            return status is ' ' or '?' ? "M" : status.ToString();
        }
    }
    public string FileName => Path.GetFileName(RelativePath);
    public string DirectoryName
    {
        get
        {
            var directory = Path.GetDirectoryName(RelativePath);
            return string.IsNullOrWhiteSpace(directory) ? "." : directory;
        }
    }
}

public enum WorkspaceEditorMode
{
    Edit,
    Preview,
    Diff,
    Image
}

public partial class WorkspaceEditorTabViewModel : ViewModelBase, IDisposable
{
    private bool _suppressLocalEdit;
    private string _savedText = string.Empty;
    private Bitmap? _image;

    public string FullPath { get; init; } = string.Empty;
    public string RelativePath { get; init; } = string.Empty;
    public string FileName => Path.GetFileName(FullPath);
    public bool IsMarkdown => string.Equals(Path.GetExtension(FullPath), ".md", StringComparison.OrdinalIgnoreCase)
                              || string.Equals(Path.GetExtension(FullPath), ".markdown", StringComparison.OrdinalIgnoreCase);
    public bool IsImage => ImageExtensions.Contains(Path.GetExtension(FullPath));
    public bool CanEdit => !IsImage;
    public bool CanPreview => IsMarkdown;
    [ObservableProperty]
    private bool _canDiff;

    [ObservableProperty]
    private string _text = string.Empty;

    public ObservableCollection<WorkspaceDiffLine> DiffLines { get; } = new();

    [ObservableProperty]
    private int _diffAddedCount;

    [ObservableProperty]
    private int _diffRemovedCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditMode))]
    [NotifyPropertyChangedFor(nameof(CanModify))]
    private WorkspaceEditorMode _mode;

    public bool IsEditMode => Mode == WorkspaceEditorMode.Edit;
    public bool CanModify => IsEditMode && IsDirty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanModify))]
    private bool _isDirty;

    [ObservableProperty]
    private DateTime _lastLocalEditAt;

    [ObservableProperty]
    private DateTime _lastExternalChangeAt;

    public Bitmap? Image
    {
        get => _image;
        set => SetProperty(ref _image, value);
    }

    partial void OnTextChanged(string value)
    {
        if (_suppressLocalEdit) return;
        LastLocalEditAt = DateTime.UtcNow;
        IsDirty = true;
    }

    public void ReplaceFromDisk(string text, DateTime changedAt)
    {
        _suppressLocalEdit = true;
        Text = text;
        _suppressLocalEdit = false;
        _savedText = text;
        LastExternalChangeAt = changedAt;
        IsDirty = false;
    }

    public void MarkSaved()
    {
        _savedText = Text;
        IsDirty = false;
    }

    public void CancelEdits()
    {
        _suppressLocalEdit = true;
        Text = _savedText;
        _suppressLocalEdit = false;
        IsDirty = false;
    }

    public void SetDiff(IReadOnlyList<WorkspaceDiffLine> lines)
    {
        DiffLines.Clear();
        foreach (var line in lines) DiffLines.Add(line);
        DiffAddedCount = lines.Count(line => line.IsAdded);
        DiffRemovedCount = lines.Count(line => line.IsRemoved);
    }

    public void Dispose()
    {
        Image?.Dispose();
        Image = null;
    }

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp"
    };
}

public partial class WorkspaceWorkbenchViewModel : ViewModelBase, IDisposable
{
    private readonly WorkspaceOperationCoordinator _operations;
    private readonly IPlatformPathService _pathService;
    private readonly IUserInteractionService _interaction;
    private readonly ILogger _logger = Log.ForContext<WorkspaceWorkbenchViewModel>();
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _refreshDebounce;
    private bool _refreshFilesPending;
    private bool _refreshGitStatePending;
    private bool _suppressGitChangeSelectionOpen;
    private int _gitChangeSelectionVersion;
    private WorkspaceProfile? _workspace;
    private string? _repositoryRoot;
    private string _workspaceRepositoryPathspec = ".";

    public WorkspaceWorkbenchViewModel(
        WorkspaceOperationCoordinator operations,
        IPlatformPathService pathService,
        IUserInteractionService interaction)
    {
        _operations = operations;
        _pathService = pathService;
        _interaction = interaction;
    }

    public ObservableCollection<WorkspaceFileNodeViewModel> Files { get; } = new();
    public ObservableCollection<WorkspaceEditorTabViewModel> EditorTabs { get; } = new();
    public ObservableCollection<GitChangeFileViewModel> GitChanges { get; } = new();

    [ObservableProperty]
    private WorkspaceEditorTabViewModel? _selectedEditorTab;

    [ObservableProperty]
    private WorkspaceFileNodeViewModel? _selectedFile;

    [ObservableProperty]
    private bool _isEditorVisible;

    [ObservableProperty]
    private bool _isLoadingFiles;

    [ObservableProperty]
    private string _workspaceName = "全局对话";

    [ObservableProperty]
    private string _statusText = "未绑定工作区";

    [ObservableProperty]
    private bool _isRenameVisible;

    [ObservableProperty]
    private string _renameName = string.Empty;

    [ObservableProperty]
    private bool _hasGitRepository;

    [ObservableProperty]
    private string _currentBranchName = string.Empty;

    [ObservableProperty]
    private bool _isReviewVisible;

    [ObservableProperty]
    private GitChangeFileViewModel? _selectedGitChange;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand))]
    [NotifyCanExecuteChangedFor(nameof(CommitAndPushCommand))]
    private string _commitMessage = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand))]
    [NotifyCanExecuteChangedFor(nameof(CommitAndPushCommand))]
    private bool _hasStagedChanges;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand))]
    [NotifyCanExecuteChangedFor(nameof(CommitAndPushCommand))]
    private bool _isGitBusy;

    [ObservableProperty]
    private string _gitStatusText = string.Empty;

    public bool HasWorkspace => _workspace != null;
    public bool HasEditorTabs => EditorTabs.Count > 0;
    public int GitChangeCount => GitChanges.Count;

    partial void OnSelectedGitChangeChanged(GitChangeFileViewModel? value)
    {
        if (_suppressGitChangeSelectionOpen) return;

        var selectionVersion = ++_gitChangeSelectionVersion;
        if (value != null) _ = OpenGitChangeAsync(value, selectionVersion);
    }

    public async Task SetWorkspaceAsync(WorkspaceProfile? workspace)
    {
        if (_workspace?.Id == workspace?.Id) return;
        await PersistStateAsync();
        DisposeWatcher();
        foreach (var tab in EditorTabs) tab.Dispose();
        EditorTabs.Clear();
        IsEditorVisible = false;
        Files.Clear();
        GitChanges.Clear();
        _workspace = workspace;
        _repositoryRoot = null;
        _workspaceRepositoryPathspec = ".";
        WorkspaceName = workspace?.Name ?? "全局对话";
        StatusText = workspace == null ? "全局对话不使用工作区文件" : workspace.DirectoryPath;
        HasGitRepository = false;
        CurrentBranchName = string.Empty;
        IsReviewVisible = false;
        SelectedGitChange = null;
        CommitMessage = string.Empty;
        HasStagedChanges = false;
        GitStatusText = string.Empty;
        OnPropertyChanged(nameof(GitChangeCount));
        OnPropertyChanged(nameof(HasWorkspace));
        OnPropertyChanged(nameof(HasEditorTabs));
        if (workspace == null || !Directory.Exists(workspace.DirectoryPath)) return;
        await RefreshFilesAsync();
        await RefreshRepositoryStateAsync();
        await RestoreStateAsync();
        StartWatcher(workspace.DirectoryPath);
    }

    [RelayCommand]
    private async Task ToggleReviewAsync()
    {
        if (!HasGitRepository) return;
        IsReviewVisible = !IsReviewVisible;
        if (!IsReviewVisible) return;
        await RefreshRepositoryStateAsync();
    }

    [RelayCommand]
    private void CloseReview() => IsReviewVisible = false;

    [RelayCommand]
    private async Task RefreshWorkbenchAsync()
    {
        await RefreshFilesAsync();
        await RefreshRepositoryStateAsync();
    }

    [RelayCommand]
    private async Task RefreshFilesAsync()
    {
        if (_workspace == null || !Directory.Exists(_workspace.DirectoryPath)) return;
        IsLoadingFiles = true;
        try
        {
            var expandedPaths = new HashSet<string>(
                EnumerateNodes(Files)
                    .Where(node => node.IsDirectory && node.IsExpanded)
                    .Select(node => node.RelativePath),
                PathComparer);
            var selectedPath = SelectedFile?.RelativePath;
            var nodes = await Task.Run(
                () => BuildTree(_workspace.DirectoryPath, _workspace.DirectoryPath, 0, expandedPaths));
            ReconcileNodes(Files, nodes);
            SelectedFile = selectedPath == null
                ? null
                : EnumerateNodes(Files).FirstOrDefault(
                    node => string.Equals(node.RelativePath, selectedPath, PathComparison));
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "刷新工作区文件树失败: {Workspace}", _workspace.DirectoryPath);
            StatusText = "无法读取工作区文件";
        }
        finally
        {
            IsLoadingFiles = false;
        }
    }

    private async Task RefreshRepositoryStateAsync()
    {
        if (_workspace == null || !Directory.Exists(_workspace.DirectoryPath))
        {
            HasGitRepository = false;
            CurrentBranchName = string.Empty;
            GitChanges.Clear();
            HasStagedChanges = false;
            OnPropertyChanged(nameof(GitChangeCount));
            return;
        }

        _repositoryRoot = null;
        var repositoryCheck = await RunGitAsync("rev-parse", "--show-toplevel");
        HasGitRepository = repositoryCheck.ExitCode == 0;
        if (!HasGitRepository)
        {
            CurrentBranchName = string.Empty;
            GitChanges.Clear();
            HasStagedChanges = false;
            IsReviewVisible = false;
            OnPropertyChanged(nameof(GitChangeCount));
            return;
        }
        _repositoryRoot = Path.GetFullPath(repositoryCheck.Output.Trim());
        var prefix = await RunGitAsync("rev-parse", "--show-prefix");
        _workspaceRepositoryPathspec = prefix.ExitCode == 0
            ? prefix.Output.Trim().TrimEnd('/')
            : ".";
        if (string.IsNullOrWhiteSpace(_workspaceRepositoryPathspec)) _workspaceRepositoryPathspec = ".";

        var branch = await RunGitAsync("branch", "--show-current");
        CurrentBranchName = branch.Output.Trim();
        if (string.IsNullOrWhiteSpace(CurrentBranchName))
        {
            var head = await RunGitAsync("rev-parse", "--short", "HEAD");
            CurrentBranchName = head.ExitCode == 0 ? $"HEAD@{head.Output.Trim()}" : "HEAD";
        }

        var status = await RunGitAsync(
            "status",
            "--porcelain=v1",
            "-z",
            "--untracked-files=all",
            "--",
            _workspaceRepositoryPathspec);
        if (status.ExitCode != 0)
        {
            GitStatusText = string.IsNullOrWhiteSpace(status.Error) ? "无法读取 Git 状态" : status.Error.Trim();
            return;
        }

        var selectedPath = SelectedGitChange?.RelativePath;
        var changes = ParseGitStatus(status.Output, _repositoryRoot);
        _suppressGitChangeSelectionOpen = true;
        try
        {
            GitChanges.Clear();
            foreach (var change in changes) GitChanges.Add(change);
            SelectedGitChange = selectedPath == null
                ? null
                : GitChanges.FirstOrDefault(change => string.Equals(change.RelativePath, selectedPath, PathComparison));
        }
        finally
        {
            _suppressGitChangeSelectionOpen = false;
        }
        HasStagedChanges = changes.Any(change => change.HasStagedChange);
        GitStatusText = changes.Count == 0 ? "工作树干净" : $"{changes.Count} 个文件有更改";
        OnPropertyChanged(nameof(GitChangeCount));
    }

    [RelayCommand]
    private async Task StageAllAsync()
    {
        if (!HasGitRepository || GitChanges.Count == 0) return;
        await RunGitOperationAsync(
            ["add", "-A", "--", _workspaceRepositoryPathspec],
            "已暂存全部更改");
    }

    [RelayCommand]
    private async Task StageFileAsync(GitChangeFileViewModel? change)
    {
        if (change == null || !change.CanStage) return;
        var arguments = new List<string> { "add", "-A", "--", change.RelativePath };
        if (!string.IsNullOrWhiteSpace(change.OriginalRelativePath))
            arguments.Add(change.OriginalRelativePath);
        await RunGitOperationAsync(arguments, $"已暂存 {change.RelativePath}");
    }

    [RelayCommand]
    private async Task RestoreFileAsync(GitChangeFileViewModel? change)
    {
        if (change == null || _workspace == null) return;
        if (!await _interaction.ConfirmAsync(
                "还原文件",
                $"确定还原“{change.RelativePath}”吗？当前未提交的更改将丢失。",
                "还原",
                "取消",
                showDontAskAgain: false)) return;

        IsGitBusy = true;
        try
        {
            if (change.IsUntracked)
            {
                DeleteUntrackedPath(change.FullPath, GetWorkspaceSecurityRoot());
            }
            else if (!await RestoreTrackedChangeAsync(change))
            {
                return;
            }
            GitStatusText = $"已还原 {change.RelativePath}";
            await RefreshFilesAsync();
            await RefreshRepositoryStateAsync();
            await RefreshOpenTabGitStateAsync();
        }
        finally
        {
            IsGitBusy = false;
        }
    }

    [RelayCommand]
    private async Task RestoreAllAsync()
    {
        if (_workspace == null || GitChanges.Count == 0) return;
        if (!await _interaction.ConfirmAsync(
                "还原全部更改",
                $"确定还原全部 {GitChanges.Count} 个文件吗？所有未提交的更改将丢失。",
                "全部还原",
                "取消",
                showDontAskAgain: false)) return;

        IsGitBusy = true;
        try
        {
            foreach (var change in GitChanges.ToList())
            {
                if (change.IsUntracked)
                {
                    DeleteUntrackedPath(change.FullPath, GetWorkspaceSecurityRoot());
                }
                else if (!await RestoreTrackedChangeAsync(change))
                {
                    return;
                }
            }
            GitStatusText = "已还原全部更改";
            await RefreshFilesAsync();
            await RefreshRepositoryStateAsync();
            await RefreshOpenTabGitStateAsync();
        }
        finally
        {
            IsGitBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCommit))]
    private async Task CommitAsync() => await CommitCoreAsync(pushAfterCommit: false);

    [RelayCommand(CanExecute = nameof(CanCommit))]
    private async Task CommitAndPushAsync() => await CommitCoreAsync(pushAfterCommit: true);

    private bool CanCommit() =>
        HasGitRepository
        && HasStagedChanges
        && !IsGitBusy
        && !string.IsNullOrWhiteSpace(CommitMessage);

    private async Task CommitCoreAsync(bool pushAfterCommit)
    {
        IsGitBusy = true;
        try
        {
            var commit = await RunGitAsync("commit", "-m", CommitMessage.Trim());
            if (commit.ExitCode != 0)
            {
                GitStatusText = BuildGitFailure("提交失败", commit);
                return;
            }

            if (pushAfterCommit)
            {
                var push = await RunGitAsync("push");
                if (push.ExitCode != 0)
                {
                    GitStatusText = BuildGitFailure("提交成功，但推送失败", push);
                    await RefreshRepositoryStateAsync();
                    return;
                }
            }

            GitStatusText = pushAfterCommit ? "已提交并推送" : "提交成功";
            CommitMessage = string.Empty;
            await RefreshRepositoryStateAsync();
            await RefreshOpenTabGitStateAsync();
        }
        finally
        {
            IsGitBusy = false;
        }
    }

    private async Task RunGitOperationAsync(IReadOnlyList<string> arguments, string successMessage)
    {
        IsGitBusy = true;
        try
        {
            var result = await RunGitAsync(arguments.ToArray());
            GitStatusText = result.ExitCode == 0 ? successMessage : BuildGitFailure("Git 操作失败", result);
            await RefreshRepositoryStateAsync();
        }
        finally
        {
            IsGitBusy = false;
        }
    }

    private async Task<bool> RestoreTrackedChangeAsync(GitChangeFileViewModel change)
    {
        if (_workspace == null || _repositoryRoot == null) return false;
        var paths = new[] { change.RelativePath, change.OriginalRelativePath }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .Distinct(PathComparer);

        foreach (var path in paths)
        {
            var existsInHead = await RunGitAsync("cat-file", "-e", $"HEAD:{path}");
            if (existsInHead.ExitCode == 0)
            {
                var restore = await RunGitAsync(
                    "restore",
                    "--source=HEAD",
                    "--staged",
                    "--worktree",
                    "--",
                    path);
                if (restore.ExitCode != 0)
                {
                    GitStatusText = BuildGitFailure("还原失败", restore);
                    return false;
                }
                continue;
            }

            // A path absent from HEAD is a staged addition or the destination
            // side of a rename/copy. Remove it from the index and working tree.
            var unstage = await RunGitAsync("rm", "--cached", "-r", "--ignore-unmatch", "--", path);
            if (unstage.ExitCode != 0)
            {
                GitStatusText = BuildGitFailure("还原失败", unstage);
                return false;
            }
            var fullPath = Path.GetFullPath(
                Path.Combine(_repositoryRoot, path.Replace('/', Path.DirectorySeparatorChar)));
            DeleteUntrackedPath(fullPath, GetWorkspaceSecurityRoot());
        }

        return true;
    }

    private async Task OpenGitChangeAsync(GitChangeFileViewModel change, int selectionVersion)
    {
        if (_workspace == null || !IsCurrentGitChangeSelection(change, selectionVersion)) return;
        var tab = EditorTabs.FirstOrDefault(
            candidate => string.Equals(candidate.FullPath, change.FullPath, StringComparison.Ordinal));
        if (tab == null)
        {
            tab = new WorkspaceEditorTabViewModel
            {
                FullPath = change.FullPath,
                RelativePath = change.RelativePath
            };
            if (tab.IsImage && File.Exists(change.FullPath))
            {
                tab.Image = new Bitmap(change.FullPath);
                tab.Mode = WorkspaceEditorMode.Image;
                tab.CanDiff = true;
                StatusText = "二进制文件不支持文本 Diff";
            }
            else
            {
                var current = File.Exists(change.FullPath)
                    ? await File.ReadAllTextAsync(change.FullPath)
                    : string.Empty;
                var changedAt = File.Exists(change.FullPath)
                    ? File.GetLastWriteTimeUtc(change.FullPath)
                    : DateTime.UtcNow;
                tab.ReplaceFromDisk(current, changedAt);
                tab.CanDiff = true;
                var head = await ReadHeadVersionAsync(change.RelativePath);
                if (!IsCurrentGitChangeSelection(change, selectionVersion))
                {
                    tab.Dispose();
                    return;
                }
                tab.SetDiff(WorkspaceDiffBuilder.Build(head ?? string.Empty, current));
                tab.Mode = WorkspaceEditorMode.Diff;
            }
            if (!IsCurrentGitChangeSelection(change, selectionVersion))
            {
                tab.Dispose();
                return;
            }
            EditorTabs.Add(tab);
            OnPropertyChanged(nameof(HasEditorTabs));
        }
        else if (!tab.IsImage)
        {
            if (!tab.IsDirty)
            {
                var current = File.Exists(change.FullPath)
                    ? await File.ReadAllTextAsync(change.FullPath)
                    : string.Empty;
                var changedAt = File.Exists(change.FullPath)
                    ? File.GetLastWriteTimeUtc(change.FullPath)
                    : DateTime.UtcNow;
                if (!IsCurrentGitChangeSelection(change, selectionVersion)) return;
                tab.ReplaceFromDisk(current, changedAt);
            }
            await RefreshDiffAsync(tab);
            if (!IsCurrentGitChangeSelection(change, selectionVersion)) return;
            tab.Mode = WorkspaceEditorMode.Diff;
        }

        if (!IsCurrentGitChangeSelection(change, selectionVersion)) return;
        SelectedEditorTab = tab;
        IsEditorVisible = true;
        await PersistStateAsync();
    }

    private bool IsCurrentGitChangeSelection(GitChangeFileViewModel change, int selectionVersion) =>
        selectionVersion == _gitChangeSelectionVersion
        && SelectedGitChange != null
        && string.Equals(SelectedGitChange.RelativePath, change.RelativePath, PathComparison);

    [RelayCommand]
    private async Task OpenFileAsync(WorkspaceFileNodeViewModel? node)
    {
        node ??= SelectedFile;
        if (node == null || node.IsDirectory || _workspace == null) return;
        var existing = EditorTabs.FirstOrDefault(tab => string.Equals(tab.FullPath, node.FullPath, StringComparison.Ordinal));
        if (existing != null)
        {
            SelectedEditorTab = existing;
            IsEditorVisible = true;
            return;
        }

        var tab = new WorkspaceEditorTabViewModel
        {
            FullPath = node.FullPath,
            RelativePath = node.RelativePath
        };
        if (tab.IsImage)
        {
            tab.Image = new Bitmap(node.FullPath);
            tab.Mode = WorkspaceEditorMode.Image;
        }
        else
        {
            var info = new FileInfo(node.FullPath);
            if (info.Length > 5 * 1024 * 1024)
            {
                StatusText = "文件超过 5 MB，未在内置编辑器中打开";
                return;
            }
            tab.ReplaceFromDisk(await File.ReadAllTextAsync(node.FullPath), info.LastWriteTimeUtc);
            tab.CanDiff = await HasUncommittedChangesAsync(node.RelativePath);
            tab.Mode = tab.IsMarkdown ? WorkspaceEditorMode.Preview : WorkspaceEditorMode.Edit;
        }
        EditorTabs.Add(tab);
        SelectedEditorTab = tab;
        IsEditorVisible = true;
        OnPropertyChanged(nameof(HasEditorTabs));
        await PersistStateAsync();
    }

    [RelayCommand]
    private async Task SaveFileAsync(WorkspaceEditorTabViewModel? tab)
    {
        tab ??= SelectedEditorTab;
        if (tab == null || tab.IsImage || _workspace == null) return;
        var root = Path.GetFullPath(_workspace.DirectoryPath);
        EnsureInsideWorkspace(root, tab.FullPath);
        StatusText = "等待工作区写入队列…";
        await _operations.RunAsync(_workspace.Id, async cancellationToken =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(tab.FullPath)!);
            await File.WriteAllTextAsync(tab.FullPath, tab.Text, cancellationToken);
        });
        tab.MarkSaved();
        StatusText = "已保存 " + tab.RelativePath;
        tab.CanDiff = await HasUncommittedChangesAsync(tab.RelativePath);
        if (tab.CanDiff)
        {
            await RefreshDiffAsync(tab);
            tab.Mode = WorkspaceEditorMode.Diff;
        }
    }

    [RelayCommand]
    private void CancelFileEdits(WorkspaceEditorTabViewModel? tab)
    {
        tab ??= SelectedEditorTab;
        if (tab == null || !tab.IsEditMode || !tab.IsDirty) return;
        tab.CancelEdits();
        StatusText = "已取消对 " + tab.RelativePath + " 的修改";
    }

    [RelayCommand]
    private async Task RefreshDiffAsync(WorkspaceEditorTabViewModel? tab)
    {
        tab ??= SelectedEditorTab;
        if (tab == null || tab.IsImage || _workspace == null) return;
        tab.CanDiff = await HasUncommittedChangesAsync(tab.RelativePath);
        if (!tab.CanDiff)
        {
            tab.SetDiff([]);
            if (tab.Mode == WorkspaceEditorMode.Diff) tab.Mode = WorkspaceEditorMode.Edit;
            return;
        }

        var head = await ReadHeadVersionAsync(tab.RelativePath);
        tab.SetDiff(WorkspaceDiffBuilder.Build(head ?? string.Empty, tab.Text));
        tab.Mode = WorkspaceEditorMode.Diff;
    }

    [RelayCommand]
    private void SetEditorMode(string? mode)
    {
        if (SelectedEditorTab == null || !Enum.TryParse<WorkspaceEditorMode>(mode, true, out var parsed)) return;
        if (parsed == WorkspaceEditorMode.Edit && !SelectedEditorTab.CanEdit) return;
        if (parsed == WorkspaceEditorMode.Preview && !SelectedEditorTab.CanPreview) return;
        if (parsed == WorkspaceEditorMode.Diff && !SelectedEditorTab.CanDiff) return;
        SelectedEditorTab.Mode = parsed;
        if (parsed == WorkspaceEditorMode.Diff) _ = RefreshDiffAsync(SelectedEditorTab);
        _ = PersistStateAsync();
    }

    [RelayCommand]
    private async Task CloseEditorTabAsync(WorkspaceEditorTabViewModel? tab)
    {
        if (tab == null) return;
        if (tab.IsDirty && !await _interaction.ConfirmAsync("关闭文件", $"“{tab.FileName}”包含未保存内容，仍要关闭吗？", "关闭", "取消")) return;
        if (SelectedGitChange != null
            && string.Equals(SelectedGitChange.RelativePath, tab.RelativePath, PathComparison))
        {
            SelectedGitChange = null;
        }
        var index = EditorTabs.IndexOf(tab);
        EditorTabs.Remove(tab);
        tab.Dispose();
        SelectedEditorTab = EditorTabs.Count == 0 ? null : EditorTabs[Math.Clamp(index, 0, EditorTabs.Count - 1)];
        if (EditorTabs.Count == 0) IsEditorVisible = false;
        OnPropertyChanged(nameof(HasEditorTabs));
        await PersistStateAsync();
    }

    [RelayCommand]
    private void CloseEditorPane() => IsEditorVisible = false;

    [RelayCommand]
    private async Task CopyRelativePathAsync(WorkspaceFileNodeViewModel? node)
        => await CopyToClipboardAsync(node?.RelativePath);

    [RelayCommand]
    private async Task CopyAbsolutePathAsync(WorkspaceFileNodeViewModel? node)
        => await CopyToClipboardAsync(node?.FullPath);

    [RelayCommand]
    private async Task DeleteFileAsync(WorkspaceFileNodeViewModel? node)
    {
        if (node == null || _workspace == null) return;
        if (!await _interaction.ConfirmAsync("移到废纸篓", $"确定删除“{node.RelativePath}”吗？", "删除", "取消")) return;
        var root = Path.GetFullPath(_workspace.DirectoryPath);
        EnsureInsideWorkspace(root, node.FullPath);
        EnsureLinkTargetInsideWorkspace(root, node.FullPath);
        try
        {
            await _operations.RunAsync(_workspace.Id, _ =>
            {
                var trash = GetTrashDirectory(root);
                Directory.CreateDirectory(trash);
                var destination = GetUniqueDestination(trash, node.Name);
                if (node.IsDirectory) Directory.Move(node.FullPath, destination);
                else File.Move(node.FullPath, destination);
                return Task.CompletedTask;
            });
            StatusText = "已移到系统废纸篓，可恢复";
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "移到系统废纸篓失败: {Path}", node.FullPath);
            if (!await _interaction.ConfirmAsync("永久删除", "系统废纸篓不可用。永久删除无法恢复，是否继续？", "永久删除", "取消")) return;
            await _operations.RunAsync(_workspace.Id, _ =>
            {
                if (node.IsDirectory) Directory.Delete(node.FullPath, recursive: true);
                else File.Delete(node.FullPath);
                return Task.CompletedTask;
            });
            StatusText = "已永久删除";
        }
        await RefreshFilesAsync();
    }

    [RelayCommand]
    private void BeginRenameFile(WorkspaceFileNodeViewModel? node)
    {
        if (node == null) return;
        SelectedFile = node;
        RenameName = node.Name;
        IsRenameVisible = true;
    }

    [RelayCommand]
    private async Task CommitRenameFileAsync()
    {
        await RenameFileAsync(RenameName);
        IsRenameVisible = false;
    }

    [RelayCommand]
    private async Task RenameFileAsync(string? newName)
    {
        var node = SelectedFile;
        if (node == null || _workspace == null || string.IsNullOrWhiteSpace(newName)) return;
        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return;
        var destination = Path.Combine(Path.GetDirectoryName(node.FullPath)!, newName.Trim());
        var root = Path.GetFullPath(_workspace.DirectoryPath);
        EnsureInsideWorkspace(root, destination);
        await _operations.RunAsync(_workspace.Id, _ =>
        {
            if (node.IsDirectory) Directory.Move(node.FullPath, destination);
            else File.Move(node.FullPath, destination);
            return Task.CompletedTask;
        });
        // 打开的 Tab 不追随移动；保存时会按原路径新建，符合工作区编辑器约定。
        await RefreshFilesAsync();
    }

    private void StartWatcher(string path)
    {
        _watcher = new FileSystemWatcher(path)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true
        };
        _watcher.Changed += OnExternalFileChanged;
        _watcher.Created += OnExternalFileChanged;
        _watcher.Renamed += OnExternalFileChanged;
        _watcher.Deleted += OnExternalFileChanged;
    }

    private void OnExternalFileChanged(object sender, FileSystemEventArgs e)
    {
        var changedAt = DateTime.UtcNow;
        var isGitMetadata = IsGitMetadataPath(e.FullPath);
        var refreshGitState = HasGitRepository;
        var refreshFiles = !isGitMetadata && e.ChangeType != WatcherChangeTypes.Changed;
        Dispatcher.UIThread.Post(async () =>
        {
            var tab = EditorTabs.FirstOrDefault(candidate => string.Equals(candidate.FullPath, e.FullPath, StringComparison.Ordinal));
            // 删除/移动例外：Tab 保持原状，之后保存会重建原路径。
            if (tab != null && e.ChangeType != WatcherChangeTypes.Deleted && File.Exists(tab.FullPath))
            {
                try
                {
                    if (tab.IsImage)
                    {
                        tab.Image?.Dispose();
                        tab.Image = new Bitmap(tab.FullPath);
                    }
                    else if (changedAt >= tab.LastLocalEditAt)
                    {
                        tab.ReplaceFromDisk(await File.ReadAllTextAsync(tab.FullPath), changedAt);
                        tab.CanDiff = await HasUncommittedChangesAsync(tab.RelativePath);
                        if (tab.CanDiff)
                        {
                            await RefreshDiffAsync(tab);
                            tab.Mode = WorkspaceEditorMode.Diff;
                        }
                        else if (tab.Mode == WorkspaceEditorMode.Diff)
                        {
                            tab.SetDiff([]);
                            tab.Mode = WorkspaceEditorMode.Edit;
                        }
                    }
                }
                catch (IOException)
                {
                    ScheduleRefresh(refreshFiles, refreshGitState);
                }
            }
            if (refreshFiles || refreshGitState)
            {
                ScheduleRefresh(refreshFiles, refreshGitState);
            }
        });
    }

    private void ScheduleRefresh(bool refreshFiles, bool refreshGitState)
    {
        _refreshFilesPending |= refreshFiles;
        _refreshGitStatePending |= refreshGitState;
        _refreshDebounce?.Cancel();
        _refreshDebounce?.Dispose();
        var cts = new CancellationTokenSource();
        _refreshDebounce = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(250, cts.Token);
                Dispatcher.UIThread.Post(async () =>
                {
                    var shouldRefreshFiles = _refreshFilesPending;
                    var shouldRefreshGitState = _refreshGitStatePending;
                    _refreshFilesPending = false;
                    _refreshGitStatePending = false;
                    if (shouldRefreshFiles) await RefreshFilesAsync();
                    if (shouldRefreshGitState)
                    {
                        await RefreshRepositoryStateAsync();
                        await RefreshOpenTabGitStateAsync();
                    }
                });
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private async Task RefreshOpenTabGitStateAsync()
    {
        foreach (var tab in EditorTabs.Where(tab => !tab.IsImage).ToList())
        {
            tab.CanDiff = await HasUncommittedChangesAsync(tab.RelativePath);
            if (tab.Mode != WorkspaceEditorMode.Diff) continue;
            if (tab.CanDiff) await RefreshDiffAsync(tab);
            else
            {
                tab.SetDiff([]);
                tab.Mode = WorkspaceEditorMode.Edit;
            }
        }
    }

    private bool IsGitMetadataPath(string path)
    {
        if (_workspace == null) return false;
        var gitPath = Path.GetFullPath(Path.Combine(_workspace.DirectoryPath, ".git"));
        var candidate = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return candidate.Equals(gitPath, comparison)
               || candidate.StartsWith(gitPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison);
    }

    private static IEnumerable<WorkspaceFileNodeViewModel> EnumerateNodes(
        IEnumerable<WorkspaceFileNodeViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in EnumerateNodes(node.Children)) yield return child;
        }
    }

    private static void ReconcileNodes(
        ObservableCollection<WorkspaceFileNodeViewModel> current,
        IReadOnlyList<WorkspaceFileNodeViewModel> desired)
    {
        for (var desiredIndex = 0; desiredIndex < desired.Count; desiredIndex++)
        {
            var desiredNode = desired[desiredIndex];
            var existingIndex = -1;
            for (var currentIndex = desiredIndex; currentIndex < current.Count; currentIndex++)
            {
                var candidate = current[currentIndex];
                if (candidate.IsDirectory == desiredNode.IsDirectory
                    && string.Equals(candidate.RelativePath, desiredNode.RelativePath, PathComparison))
                {
                    existingIndex = currentIndex;
                    break;
                }
            }

            if (existingIndex < 0)
            {
                current.Insert(desiredIndex, desiredNode);
                continue;
            }

            if (existingIndex != desiredIndex)
            {
                current.Move(existingIndex, desiredIndex);
            }

            var existingNode = current[desiredIndex];
            if (existingNode.IsDirectory)
            {
                ReconcileNodes(existingNode.Children, desiredNode.Children);
            }
        }

        while (current.Count > desired.Count)
        {
            current.RemoveAt(current.Count - 1);
        }
    }

    private static List<WorkspaceFileNodeViewModel> BuildTree(
        string root,
        string directory,
        int depth,
        IReadOnlySet<string>? expandedPaths = null)
    {
        if (depth > 10) return [];
        var result = new List<WorkspaceFileNodeViewModel>();
        IEnumerable<string> entries;
        try { entries = Directory.EnumerateFileSystemEntries(directory); }
        catch { return result; }
        foreach (var entry in entries.Where(path => Path.GetFileName(path) is not ".git" and not ".athena-trash")
                     .OrderByDescending(Directory.Exists).ThenBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
        {
            var isDirectory = Directory.Exists(entry);
            var relativePath = Path.GetRelativePath(root, entry);
            var node = new WorkspaceFileNodeViewModel
            {
                Name = Path.GetFileName(entry),
                FullPath = entry,
                RelativePath = relativePath,
                IsDirectory = isDirectory,
                IsExpanded = isDirectory && expandedPaths?.Contains(relativePath) == true
            };
            if (isDirectory)
            {
                foreach (var child in BuildTree(root, entry, depth + 1, expandedPaths)) node.Children.Add(child);
            }
            result.Add(node);
        }
        return result;
    }

    private async Task<bool> HasUncommittedChangesAsync(string relativePath)
    {
        if (_workspace == null || !HasGitRepository) return false;
        var result = await RunGitAsync(
            "status",
            "--porcelain=v1",
            "--untracked-files=normal",
            "--",
            relativePath);
        return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.Output);
    }

    private async Task<string?> ReadHeadVersionAsync(string relativePath)
    {
        if (_workspace == null || !HasGitRepository) return null;
        var normalized = relativePath.Replace(Path.DirectorySeparatorChar, '/');
        // Materialize the committed blob with the same attributes/filters Git applies to
        // the working tree. This avoids presenting EOL or clean/smudge conversion as a
        // user-authored, uncommitted change.
        var result = await RunGitAsync("cat-file", "--filters", $"--path={normalized}", $"HEAD:{normalized}");
        return result.ExitCode == 0 ? result.Output : string.Empty;
    }

    private async Task<(int ExitCode, string Output, string Error)> RunGitAsync(params string[] arguments)
    {
        if (_workspace == null) return (-1, string.Empty, string.Empty);
        try
        {
            var start = new ProcessStartInfo("git")
            {
                WorkingDirectory = _repositoryRoot ?? _workspace.DirectoryPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            if (process == null) return (-1, string.Empty, "无法启动 Git");
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return (process.ExitCode, await outputTask, await errorTask);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Git command failed to start");
            return (-1, string.Empty, ex.Message);
        }
    }

    private static IReadOnlyList<GitChangeFileViewModel> ParseGitStatus(string output, string workspaceRoot)
    {
        var result = new List<GitChangeFileViewModel>();
        var records = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < records.Length; index++)
        {
            var record = records[index];
            if (record.Length < 4) continue;
            var x = record[0];
            var y = record[1];
            var relativePath = record[3..];
            string? originalRelativePath = null;
            if ((x is 'R' or 'C') && index + 1 < records.Length)
            {
                originalRelativePath = records[++index]; // -z emits the original path next.
            }

            var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
            var isUntracked = x == '?' && y == '?';
            result.Add(new GitChangeFileViewModel
            {
                RelativePath = relativePath,
                OriginalRelativePath = originalRelativePath,
                FullPath = Path.GetFullPath(Path.Combine(workspaceRoot, normalized)),
                StatusCode = new string([x, y]),
                StatusLabel = DescribeGitStatus(x, y),
                IsUntracked = isUntracked,
                HasStagedChange = !isUntracked && x != ' ' && x != '?',
                HasWorkingTreeChange = isUntracked || (y != ' ' && y != '?')
            });
        }

        return result
            .OrderBy(change => change.DirectoryName, PathComparer)
            .ThenBy(change => change.FileName, PathComparer)
            .ToList();
    }

    private static string DescribeGitStatus(char indexStatus, char workTreeStatus)
    {
        if (indexStatus == '?' && workTreeStatus == '?') return "未跟踪";
        if (indexStatus == 'U' || workTreeStatus == 'U') return "有冲突";
        if (indexStatus == 'R' || workTreeStatus == 'R') return "已重命名";
        if (indexStatus == 'C' || workTreeStatus == 'C') return "已复制";
        if (indexStatus == 'D' || workTreeStatus == 'D') return "已删除";
        if (indexStatus == 'A') return workTreeStatus == ' ' ? "已暂存新增" : "新增";
        if (indexStatus != ' ') return workTreeStatus == ' ' ? "已暂存" : "部分暂存";
        return "已修改";
    }

    private static void DeleteUntrackedPath(string fullPath, string workspaceRoot)
    {
        var root = Path.GetFullPath(workspaceRoot);
        EnsureInsideWorkspace(root, fullPath);
        EnsureLinkTargetInsideWorkspace(root, fullPath);
        if (Directory.Exists(fullPath)) Directory.Delete(fullPath, recursive: true);
        else if (File.Exists(fullPath)) File.Delete(fullPath);
    }

    private static string BuildGitFailure(
        string prefix,
        (int ExitCode, string Output, string Error) result)
    {
        var detail = string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error;
        detail = detail.Trim();
        return string.IsNullOrWhiteSpace(detail) ? prefix : $"{prefix}：{detail}";
    }

    private string GetWorkspaceSecurityRoot()
    {
        if (_repositoryRoot == null || _workspaceRepositoryPathspec == ".")
            return _repositoryRoot ?? _workspace?.DirectoryPath ?? string.Empty;
        return Path.Combine(
            _repositoryRoot,
            _workspaceRepositoryPathspec.Replace('/', Path.DirectorySeparatorChar));
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private async Task CopyToClipboardAsync(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var clipboard = TopLevel.GetTopLevel(desktop.MainWindow)?.Clipboard;
            if (clipboard != null) await clipboard.SetTextAsync(value);
        }
    }

    private string StatePath => Path.Combine(_pathService.GetAppDataDirectory(), "WorkspaceEditor", $"{_workspace?.Id}.json");

    private async Task PersistStateAsync()
    {
        if (_workspace == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            var state = new WorkspaceEditorState
            {
                IsEditorVisible = IsEditorVisible,
                SelectedPath = SelectedEditorTab?.FullPath,
                Tabs = EditorTabs.Select(tab => new WorkspaceEditorTabState { FullPath = tab.FullPath, Mode = tab.Mode }).ToList()
            };
            await File.WriteAllTextAsync(StatePath, JsonSerializer.Serialize(state));
        }
        catch (Exception ex) { _logger.Debug(ex, "保存编辑器工作区状态失败"); }
    }

    private async Task RestoreStateAsync()
    {
        if (!File.Exists(StatePath)) return;
        try
        {
            var state = JsonSerializer.Deserialize<WorkspaceEditorState>(await File.ReadAllTextAsync(StatePath));
            if (state == null) return;
            IsEditorVisible = state.IsEditorVisible;
            foreach (var tabState in state.Tabs.Where(tab => File.Exists(tab.FullPath)))
            {
                var node = new WorkspaceFileNodeViewModel
                {
                    Name = Path.GetFileName(tabState.FullPath),
                    FullPath = tabState.FullPath,
                    RelativePath = Path.GetRelativePath(_workspace!.DirectoryPath, tabState.FullPath)
                };
                await OpenFileAsync(node);
                if (SelectedEditorTab == null) continue;
                var canRestoreMode = tabState.Mode switch
                {
                    WorkspaceEditorMode.Edit => SelectedEditorTab.CanEdit,
                    WorkspaceEditorMode.Preview => SelectedEditorTab.CanPreview,
                    WorkspaceEditorMode.Diff => SelectedEditorTab.CanDiff,
                    WorkspaceEditorMode.Image => SelectedEditorTab.IsImage,
                    _ => false
                };
                if (canRestoreMode) SelectedEditorTab.Mode = tabState.Mode;
            }
            SelectedEditorTab = EditorTabs.FirstOrDefault(tab => tab.FullPath == state.SelectedPath) ?? EditorTabs.FirstOrDefault();
            if (EditorTabs.Count == 0) IsEditorVisible = false;
        }
        catch (Exception ex) { _logger.Debug(ex, "恢复编辑器工作区状态失败"); }
    }

    private static void EnsureInsideWorkspace(string root, string path)
    {
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedPath = Path.GetFullPath(path);
        if (!normalizedPath.StartsWith(normalizedRoot, StringComparison.Ordinal))
            throw new InvalidOperationException("目标路径超出工作区范围。");
    }

    private static void EnsureLinkTargetInsideWorkspace(string root, string path)
    {
        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        var target = info.ResolveLinkTarget(true);
        if (target != null) EnsureInsideWorkspace(root, target.FullName);
    }

    private static string GetTrashDirectory(string workspaceRoot)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS()) return Path.Combine(profile, ".Trash");
        if (OperatingSystem.IsLinux()) return Path.Combine(profile, ".local", "share", "Trash", "files");
        // Windows 的跨平台运行时没有可靠的 Recycle Bin API；退回工作区可恢复目录。
        return Path.Combine(workspaceRoot, ".athena-trash");
    }

    private static string GetUniqueDestination(string directory, string name)
    {
        var candidate = Path.Combine(directory, name);
        if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        return Path.Combine(directory, $"{stem}-{DateTime.Now:yyyyMMddHHmmssfff}{extension}");
    }

    private void DisposeWatcher()
    {
        if (_watcher == null) return;
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _watcher = null;
    }

    public void Dispose()
    {
        DisposeWatcher();
        foreach (var tab in EditorTabs) tab.Dispose();
        _refreshDebounce?.Cancel();
        _refreshDebounce?.Dispose();
    }

    private sealed class WorkspaceEditorState
    {
        public bool IsEditorVisible { get; set; }
        public string? SelectedPath { get; set; }
        public List<WorkspaceEditorTabState> Tabs { get; set; } = [];
    }

    private sealed class WorkspaceEditorTabState
    {
        public string FullPath { get; set; } = string.Empty;
        public WorkspaceEditorMode Mode { get; set; }
    }
}
