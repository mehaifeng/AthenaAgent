using Athena.UI.Models;
using Athena.UI.Services;
using Athena.UI.Services.Interfaces;
using Athena.UI.Services.Preview;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
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

    [ObservableProperty]
    private bool _isRenaming;

    [ObservableProperty]
    private bool _isRenamePending;

    [ObservableProperty]
    private string _renameText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRenameError))]
    private string _renameError = string.Empty;

    public bool HasRenameError => !string.IsNullOrEmpty(RenameError);

    partial void OnRenameTextChanged(string value)
    {
        if (IsRenaming) RenameError = string.Empty;
    }
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
    public bool CanUnstage => HasStagedChange;
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
    Image,
    Binary,
    // 注意：该枚举经 JSON 数字序列化持久化，新值只能追加在末尾，
    // 任何中间插入都会破坏既有存档的语义（见 RestoreStateAsync 的兼容兜底）。
    Office
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
    public bool IsOffice => OfficePreviewTypes.IsPreviewable(FullPath);
    public bool IsOfficeLegacy => OfficePreviewTypes.IsLegacyOffice(FullPath);

    /// <summary>Office 预览的 NativeWebView 加载地址（仅 Office 模式下非空）。</summary>
    public string? PreviewUrl { get; set; }

    /// <summary>Office 预览服务器会话 ID，关闭 tab 时用于释放只读会话。</summary>
    public string? PreviewSessionId { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    [NotifyPropertyChangedFor(nameof(CanPreview))]
    private bool _isBinary;

    public bool CanEdit => !IsImage && !IsBinary && !IsOffice;
    public bool CanPreview => IsMarkdown && !IsBinary;

    private bool _canDiff;

    /// <summary>Office 文件是二进制预览，不提供文本 Diff（任何路径设置 CanDiff 对 Office 均无效）。</summary>
    public bool CanDiff
    {
        get => _canDiff && !IsOffice;
        set
        {
            if (_canDiff == value) return;
            _canDiff = value;
            OnPropertyChanged();
        }
    }

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
    private readonly ICommitMessageGenerator? _commitMessageGenerator;
    private readonly ILocalizationService? _localizationService;
    private readonly ILogger _logger = Log.ForContext<WorkspaceWorkbenchViewModel>();
    private const long MaxEditableFileSize = 5 * 1024 * 1024;
    private const long MaxOfficePreviewFileSize = 100L * 1024 * 1024;
    private readonly OfficePreviewHost? _previewHost;
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _refreshDebounce;
    private CancellationTokenSource? _gitChangeOpenCts;
    private readonly SemaphoreSlim _repositoryRefreshGate = new(1, 1);
    private bool _refreshFilesPending;
    private bool _refreshGitStatePending;
    private int _gitChangeSelectionVersion;
    private WorkspaceProfile? _workspace;
    private WorkspaceFileNodeViewModel? _renamingFile;
    private string? _repositoryRoot;
    private string _workspaceRepositoryPathspec = ".";
    private string _probedGitUserName = string.Empty;
    private string _probedGitUserEmail = string.Empty;
    private bool _disposed;

    public WorkspaceWorkbenchViewModel(
        WorkspaceOperationCoordinator operations,
        IPlatformPathService pathService,
        IUserInteractionService interaction,
        ICommitMessageGenerator? commitMessageGenerator = null,
        ILocalizationService? localizationService = null,
        OfficePreviewHost? previewHost = null)
    {
        _operations = operations;
        _pathService = pathService;
        _interaction = interaction;
        _commitMessageGenerator = commitMessageGenerator;
        _localizationService = localizationService;
        _previewHost = previewHost;
        if (_localizationService != null)
        {
            _localizationService.LanguageChanged += OnLanguageChanged;
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (_workspace == null)
        {
            WorkspaceName = L("MainWindow.Launcher.GlobalChat", "Global chat");
            StatusText = L("Workspace.Status.GlobalChatNoFiles", "Global chat does not use workspace files");
        }

        // 重新解析 git 状态：一次性刷新 GitChanges 标签、GitStatusText 与 CommitTargetTitle。
        _ = RefreshRepositoryStateAsync();
    }

    private string L(string key, string fallback)
        => _localizationService?.GetString(key, fallback) ?? fallback;

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
    private string _workspaceName = string.Empty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCommitEnabled))]
    [NotifyPropertyChangedFor(nameof(IsGenerateEnabled))]
    private bool _hasGitRepository;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CommitTargetTitle))]
    private string _currentBranchName = string.Empty;

    [ObservableProperty]
    private bool _isReviewVisible;

    [ObservableProperty]
    private GitChangeFileViewModel? _selectedGitChange;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand))]
    [NotifyCanExecuteChangedFor(nameof(CommitAndPushCommand))]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommitMessageCommand))]
    [NotifyPropertyChangedFor(nameof(IsCommitEnabled))]
    [NotifyPropertyChangedFor(nameof(IsGenerateEnabled))]
    private string _commitMessage = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand))]
    [NotifyCanExecuteChangedFor(nameof(CommitAndPushCommand))]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommitMessageCommand))]
    [NotifyPropertyChangedFor(nameof(IsCommitEnabled))]
    [NotifyPropertyChangedFor(nameof(IsGenerateEnabled))]
    private bool _hasStagedChanges;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand))]
    [NotifyCanExecuteChangedFor(nameof(CommitAndPushCommand))]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommitMessageCommand))]
    [NotifyPropertyChangedFor(nameof(IsCommitEnabled))]
    [NotifyPropertyChangedFor(nameof(IsGenerateEnabled))]
    private bool _isGitBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommitMessageCommand))]
    [NotifyPropertyChangedFor(nameof(IsGenerateEnabled))]
    private bool _isGeneratingCommitMessage;

    [ObservableProperty]
    private bool _missingGitIdentity;

    [ObservableProperty]
    private bool _isIdentityConfiguring;

    [ObservableProperty]
    private string _gitUserName = string.Empty;

    [ObservableProperty]
    private string _gitUserEmail = string.Empty;

    [ObservableProperty]
    private string _gitStatusText = string.Empty;

    public bool HasWorkspace => _workspace != null;
    public bool HasEditorTabs => EditorTabs.Count > 0;
    public int GitChangeCount => GitChanges.Count;
    public int StagedChangeCount => GitChanges.Count(change => change.HasStagedChange);
    public string CommitTargetTitle
    {
        get
        {
            var branch = string.IsNullOrWhiteSpace(CurrentBranchName) ? "HEAD" : CurrentBranchName;
            if (GitChanges.Count == 0) return branch;
            if (StagedChangeCount == 0) return string.Format(L("Workspace.Commit.Format.Changes", "{0} change(s)"), $"{branch} · {GitChanges.Count}");
            if (StagedChangeCount == GitChanges.Count) return string.Format(L("Workspace.Commit.Format.AllStaged", "{0} staged"), $"{branch} · {StagedChangeCount}");
            return string.Format(L("Workspace.Commit.Format.PartialStaged", "{0}/{1} staged"), $"{branch} · {StagedChangeCount}", GitChanges.Count);
        }
    }
    public bool IsCommitEnabled => CanCommit();
    public bool IsGenerateEnabled => CanGenerateCommitMessage();
    public bool HasUncommittedChanges => GitChanges.Count > 0;

    [RelayCommand]
    private async Task OpenGitChangeAsync(GitChangeFileViewModel? change)
    {
        if (change == null) return;

        var selectionVersion = ++_gitChangeSelectionVersion;
        var previous = Interlocked.Exchange(ref _gitChangeOpenCts, null);
        previous?.Cancel();

        var cts = new CancellationTokenSource();
        _gitChangeOpenCts = cts;
        await OpenGitChangeTrackedAsync(change, selectionVersion, cts);
    }

    public async Task SetWorkspaceAsync(WorkspaceProfile? workspace)
    {
        if (_workspace?.Id == workspace?.Id) return;
        await PersistStateAsync();
        DisposeWatcher();
        CancelScheduledRefresh();
        CancelRenameFile(_renamingFile);
        _previewHost?.ReleaseAll();
        foreach (var tab in EditorTabs) tab.Dispose();
        EditorTabs.Clear();
        SelectedEditorTab = null;
        IsEditorVisible = false;
        Files.Clear();
        GitChanges.Clear();
        _workspace = workspace;
        _repositoryRoot = null;
        _workspaceRepositoryPathspec = ".";
        WorkspaceName = workspace?.Name ?? L("MainWindow.Launcher.GlobalChat", "Global chat");
        StatusText = workspace == null
            ? L("Workspace.Status.GlobalChatNoFiles", "Global chat does not use workspace files")
            : workspace.DirectoryPath;
        HasGitRepository = false;
        CurrentBranchName = string.Empty;
        IsReviewVisible = false;
        SelectedGitChange = null;
        CommitMessage = string.Empty;
        HasStagedChanges = false;
        GitStatusText = string.Empty;
        MissingGitIdentity = false;
        IsIdentityConfiguring = false;
        GitUserName = string.Empty;
        GitUserEmail = string.Empty;
        IsGeneratingCommitMessage = false;
        OnPropertyChanged(nameof(GitChangeCount));
        OnPropertyChanged(nameof(StagedChangeCount));
        OnPropertyChanged(nameof(CommitTargetTitle));
        OnPropertyChanged(nameof(HasUncommittedChanges));
        OnPropertyChanged(nameof(IsCommitEnabled));
        OnPropertyChanged(nameof(IsGenerateEnabled));
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
            _logger.Warning(ex, "Failed to refresh workspace file tree: {Workspace}", _workspace.DirectoryPath);
            StatusText = L("Workspace.Status.ReadingFilesFailed", "Could not read workspace files");
        }
        finally
        {
            IsLoadingFiles = false;
        }
    }

    private async Task RefreshRepositoryStateAsync()
    {
        await _repositoryRefreshGate.WaitAsync();
        try
        {
            await RefreshRepositoryStateCoreAsync();
        }
        finally
        {
            _repositoryRefreshGate.Release();
        }
    }

    private async Task RefreshRepositoryStateCoreAsync()
    {
        if (_workspace == null || !Directory.Exists(_workspace.DirectoryPath))
        {
            HasGitRepository = false;
            CurrentBranchName = string.Empty;
            GitChanges.Clear();
            HasStagedChanges = false;
            MissingGitIdentity = false;
            OnPropertyChanged(nameof(GitChangeCount));
            OnPropertyChanged(nameof(StagedChangeCount));
            OnPropertyChanged(nameof(CommitTargetTitle));
            OnPropertyChanged(nameof(HasUncommittedChanges));
            OnPropertyChanged(nameof(IsCommitEnabled));
            OnPropertyChanged(nameof(IsGenerateEnabled));
            return;
        }

        if (_repositoryRoot == null)
        {
            var repositoryCheck = await RunGitAsync("rev-parse", "--show-toplevel");
            HasGitRepository = repositoryCheck.ExitCode == 0;
            if (!HasGitRepository)
            {
                CurrentBranchName = string.Empty;
                GitChanges.Clear();
                HasStagedChanges = false;
                IsReviewVisible = false;
                MissingGitIdentity = false;
                OnPropertyChanged(nameof(GitChangeCount));
                OnPropertyChanged(nameof(StagedChangeCount));
                OnPropertyChanged(nameof(CommitTargetTitle));
                OnPropertyChanged(nameof(HasUncommittedChanges));
                OnPropertyChanged(nameof(IsCommitEnabled));
                OnPropertyChanged(nameof(IsGenerateEnabled));
                return;
            }
            _repositoryRoot = Path.GetFullPath(repositoryCheck.Output.Trim());
            var prefix = await RunGitAsync("rev-parse", "--show-prefix");
            _workspaceRepositoryPathspec = prefix.ExitCode == 0
                ? prefix.Output.Trim().TrimEnd('/')
                : ".";
            if (string.IsNullOrWhiteSpace(_workspaceRepositoryPathspec)) _workspaceRepositoryPathspec = ".";
        }
        HasGitRepository = true;

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
            GitStatusText = string.IsNullOrWhiteSpace(status.Error)
                ? L("Workspace.Status.ReadingGitFailed", "Could not read Git status")
                : status.Error.Trim();
            return;
        }

        var selectedPath = SelectedGitChange?.RelativePath;
        var changes = ParseGitStatus(status.Output, _repositoryRoot);
        var previousCount = GitChanges.Count;
        ReconcileGitChanges(GitChanges, changes);
        var selectedChange = selectedPath == null
            ? null
            : GitChanges.FirstOrDefault(change => string.Equals(change.RelativePath, selectedPath, PathComparison));
        if (!ReferenceEquals(SelectedGitChange, selectedChange))
            SelectedGitChange = selectedChange;
        HasStagedChanges = changes.Any(change => change.HasStagedChange);
        GitStatusText = changes.Count == 0
            ? L("Workspace.Status.WorkingTreeClean", "Working tree clean")
            : string.Format(L("Workspace.Status.FilesChanged", "{0} file(s) have changes"), changes.Count);
        if (previousCount != GitChanges.Count)
            OnPropertyChanged(nameof(GitChangeCount));
        OnPropertyChanged(nameof(StagedChangeCount));
        OnPropertyChanged(nameof(CommitTargetTitle));
        OnPropertyChanged(nameof(HasUncommittedChanges));
        OnPropertyChanged(nameof(IsCommitEnabled));
        OnPropertyChanged(nameof(IsGenerateEnabled));
        CommitCommand.NotifyCanExecuteChanged();
        CommitAndPushCommand.NotifyCanExecuteChanged();
        GenerateCommitMessageCommand.NotifyCanExecuteChanged();
        await RefreshGitIdentityAsync();
    }

    private async Task RefreshGitIdentityAsync()
    {
        if (_repositoryRoot == null)
        {
            MissingGitIdentity = false;
            return;
        }

        var name = await RunGitAsync("config", "user.name");
        var email = await RunGitAsync("config", "user.email");
        var nameOk = name.ExitCode == 0 && !string.IsNullOrWhiteSpace(name.Output);
        var emailOk = email.ExitCode == 0 && !string.IsNullOrWhiteSpace(email.Output);
        _probedGitUserName = nameOk ? name.Output.Trim() : string.Empty;
        _probedGitUserEmail = emailOk ? email.Output.Trim() : string.Empty;
        MissingGitIdentity = !(nameOk && emailOk);
    }

    [RelayCommand]
    private async Task StageAllAsync()
    {
        if (!HasGitRepository || GitChanges.Count == 0) return;
        await RunGitOperationAsync(
            ["add", "-A", "--", _workspaceRepositoryPathspec],
            L("Workspace.Status.StagedAll", "All changes staged"));
    }

    [RelayCommand]
    private async Task StageFileAsync(GitChangeFileViewModel? change)
    {
        if (change == null || !change.CanStage) return;
        var arguments = new List<string> { "add", "-A", "--", change.RelativePath };
        if (!string.IsNullOrWhiteSpace(change.OriginalRelativePath))
            arguments.Add(change.OriginalRelativePath);
        await RunGitOperationAsync(arguments, string.Format(L("Workspace.Status.StagedFile", "Staged {0}"), change.RelativePath));
    }

    [RelayCommand]
    private async Task UnstageFileAsync(GitChangeFileViewModel? change)
    {
        if (change == null || !change.HasStagedChange) return;
        var arguments = new List<string> { "restore", "--staged", "--", change.RelativePath };
        if (!string.IsNullOrWhiteSpace(change.OriginalRelativePath))
            arguments.Add(change.OriginalRelativePath);
        await RunGitOperationAsync(arguments, string.Format(L("Workspace.Status.UnstagedFile", "Unstaged {0}"), change.RelativePath));
    }

    [RelayCommand]
    private async Task UnstageAllAsync()
    {
        if (!HasGitRepository || GitChanges.Count == 0) return;
        await RunGitOperationAsync(
            ["restore", "--staged", "--", _workspaceRepositoryPathspec],
            L("Workspace.Status.UnstagedAll", "All changes unstaged"));
    }

    [RelayCommand]
    private async Task RestoreFileAsync(GitChangeFileViewModel? change)
    {
        if (change == null || _workspace == null) return;
        if (!await _interaction.ConfirmAsync(
                L("Workspace.Confirm.Restore.Title", "Restore file"),
                string.Format(L("Workspace.Confirm.Restore.Message", "Restore \"{0}\"? Uncommitted changes will be lost."), change.RelativePath),
                L("Workspace.Confirm.Restore.Yes", "Restore"),
                L("Workspace.Confirm.Restore.No", "Cancel"),
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
            GitStatusText = string.Format(L("Workspace.Status.RestoredFile", "Restored {0}"), change.RelativePath);
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
                L("Workspace.Confirm.RestoreAll.Title", "Restore all changes"),
                string.Format(L("Workspace.Confirm.RestoreAll.Message", "Restore all {0} files? All uncommitted changes will be lost."), GitChanges.Count),
                L("Workspace.Confirm.RestoreAll.Yes", "Restore all"),
                L("Common.Cancel", "Cancel"),
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
            GitStatusText = L("Workspace.Status.RestoredAll", "All changes restored");
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
        && HasUncommittedChanges
        && !IsGitBusy
        && !string.IsNullOrWhiteSpace(CommitMessage);

    private async Task CommitCoreAsync(bool pushAfterCommit)
    {
        if (MissingGitIdentity)
        {
            GitStatusText = L("Workspace.Status.IdentityNotConfigured", "Git user identity not configured; cannot commit. Please set it first.");
            IsIdentityConfiguring = true;
            return;
        }

        IsGitBusy = true;
        try
        {
            // 没有任何已暂存更改时，先把工作区改动全部暂存，保证「提交」总能提交当前改动。
            if (!HasStagedChanges)
            {
                var stage = await RunGitAsync("add", "-A", "--", _workspaceRepositoryPathspec);
                if (stage.ExitCode != 0)
                {
                    GitStatusText = BuildGitFailure(L("Workspace.Git.Error.Stage", "Stage failed"), stage);
                    return;
                }
            }

            var commit = await RunGitAsync("commit", "-m", CommitMessage.Trim());
            if (commit.ExitCode != 0)
            {
                GitStatusText = BuildGitFailure(L("Workspace.Git.Error.Commit", "Commit failed"), commit);
                return;
            }

            if (pushAfterCommit)
            {
                var push = await RunGitAsync("push");
                if (push.ExitCode != 0)
                {
                    GitStatusText = BuildGitFailure(L("Workspace.Git.Error.CommitButPushFailed", "Commit succeeded, but push failed"), push);
                    await RefreshRepositoryStateAsync();
                    return;
                }
            }

            GitStatusText = pushAfterCommit
                ? L("Workspace.Status.CommitAndPushSuccess", "Committed and pushed")
                : L("Workspace.Status.CommitSuccess", "Commit succeeded");
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
            GitStatusText = result.ExitCode == 0 ? successMessage : BuildGitFailure(L("Workspace.Git.Error.Generic", "Git operation failed"), result);
            await RefreshRepositoryStateAsync();
        }
        finally
        {
            IsGitBusy = false;
        }
    }

    [RelayCommand]
    private void ConfigureIdentity()
    {
        IsIdentityConfiguring = !IsIdentityConfiguring;
        if (!IsIdentityConfiguring) return;
        if (string.IsNullOrWhiteSpace(GitUserName)) GitUserName = _probedGitUserName;
        if (string.IsNullOrWhiteSpace(GitUserEmail)) GitUserEmail = _probedGitUserEmail;
    }

    [RelayCommand]
    private async Task SaveGitIdentityAsync()
    {
        if (_repositoryRoot == null) return;
        if (string.IsNullOrWhiteSpace(GitUserName) || string.IsNullOrWhiteSpace(GitUserEmail))
        {
            GitStatusText = L("Workspace.Status.IdentityRequired", "Please fill in name and email");
            return;
        }

        IsGitBusy = true;
        try
        {
            var name = await RunGitAsync("config", "user.name", GitUserName.Trim());
            var email = await RunGitAsync("config", "user.email", GitUserEmail.Trim());
            if (name.ExitCode != 0 || email.ExitCode != 0)
            {
                GitStatusText = BuildGitFailure(L("Workspace.Git.Error.IdentitySave", "Identity save failed"), name.ExitCode != 0 ? name : email);
                return;
            }
            MissingGitIdentity = false;
            IsIdentityConfiguring = false;
            GitStatusText = L("Workspace.Status.IdentitySaved", "Git identity saved");
        }
        finally
        {
            IsGitBusy = false;
        }
    }

    private const int CommitMessageDiffBudget = 8000;

    [RelayCommand(CanExecute = nameof(CanGenerateCommitMessage))]
    private async Task GenerateCommitMessageAsync()
    {
        if (_repositoryRoot == null || _commitMessageGenerator == null) return;
        IsGeneratingCommitMessage = true;
        GitStatusText = L("Workspace.Status.GeneratingCommitMessage", "Generating commit message…");
        try
        {
            // 有已暂存更改时基于暂存区生成（与提交语义一致）；否则基于工作区生成。
            var diffScope = HasStagedChanges ? "--cached" : null;
            var statArgs = new List<string> { "diff" };
            if (diffScope != null) statArgs.Add(diffScope);
            statArgs.AddRange(["--stat", "--", _workspaceRepositoryPathspec]);
            var diffArgs = new List<string> { "diff" };
            if (diffScope != null) diffArgs.Add(diffScope);
            diffArgs.AddRange(["--", _workspaceRepositoryPathspec]);

            var stat = await RunGitAsync(statArgs.ToArray());
            var diff = await RunGitAsync(diffArgs.ToArray());
            var statText = stat.ExitCode == 0 ? stat.Output : string.Empty;
            var diffText = TruncateGitDiff(diff.ExitCode == 0 ? diff.Output : string.Empty, CommitMessageDiffBudget);
            if (string.IsNullOrWhiteSpace(statText) && string.IsNullOrWhiteSpace(diffText))
            {
                GitStatusText = L("Workspace.Status.NoStagedForGeneration", "No staged changes to generate from");
                return;
            }
            var message = await _commitMessageGenerator.GenerateAsync(CurrentBranchName, statText, diffText);
            if (string.IsNullOrWhiteSpace(message))
            {
                GitStatusText = L("Workspace.Status.GenerationFailed", "Failed to generate commit message, please enter one manually");
                return;
            }
            CommitMessage = message;
            GitStatusText = L("Workspace.Status.GenerationSucceeded", "Generated commit message — please review before committing");
        }
        finally
        {
            IsGeneratingCommitMessage = false;
        }
    }

    private bool CanGenerateCommitMessage() =>
        _commitMessageGenerator != null
        && HasGitRepository
        && HasUncommittedChanges
        && !IsGitBusy
        && !IsGeneratingCommitMessage;

    private string TruncateGitDiff(string diff, int budget)
    {
        if (string.IsNullOrEmpty(diff) || diff.Length <= budget) return diff;
        var span = diff.AsSpan(0, budget);
        var lastNewLine = span.LastIndexOf('\n');
        var cut = lastNewLine > 0 ? lastNewLine : budget;
        return diff[..cut] + "\n" + L("Workspace.Git.DiffTruncated", "…(diff truncated, generation may be incomplete)");
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
                    GitStatusText = BuildGitFailure(L("Workspace.Git.Error.Restore", "Restore failed"), restore);
                    return false;
                }
                continue;
            }

            // A path absent from HEAD is a staged addition or the destination
            // side of a rename/copy. Remove it from the index and working tree.
            var unstage = await RunGitAsync("rm", "--cached", "-r", "--ignore-unmatch", "--", path);
            if (unstage.ExitCode != 0)
            {
                GitStatusText = BuildGitFailure(L("Workspace.Git.Error.Restore", "Restore failed"), unstage);
                return false;
            }
            var fullPath = Path.GetFullPath(
                Path.Combine(_repositoryRoot, path.Replace('/', Path.DirectorySeparatorChar)));
            DeleteUntrackedPath(fullPath, GetWorkspaceSecurityRoot());
        }

        return true;
    }

    private async Task OpenGitChangeTrackedAsync(
        GitChangeFileViewModel change,
        int selectionVersion,
        CancellationTokenSource cts)
    {
        try
        {
            await OpenGitChangeCoreAsync(change, selectionVersion, cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        finally
        {
            Interlocked.CompareExchange(ref _gitChangeOpenCts, null, cts);
            cts.Dispose();
        }
    }

    private async Task OpenGitChangeCoreAsync(
        GitChangeFileViewModel change,
        int selectionVersion,
        CancellationToken cancellationToken)
    {
        if (_workspace == null || !IsCurrentGitChangeSelection(change, selectionVersion, cancellationToken)) return;
        var tab = EditorTabs.FirstOrDefault(
            candidate => string.Equals(candidate.RelativePath, change.RelativePath, PathComparison));
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
                StatusText = L("Workspace.Status.BinaryDiffUnsupported", "Binary file does not support text diff");
            }
            else if (tab.IsOffice && File.Exists(change.FullPath))
            {
                if (!OpenOfficePreview(tab)) return;
            }
            else
            {
                var currentExists = File.Exists(change.FullPath);
                if (currentExists && new FileInfo(change.FullPath).Length > MaxEditableFileSize)
                {
                    StatusText = L("Workspace.Status.FileTooLarge", "File exceeds 5 MB, not opened in built-in editor");
                    return;
                }

                // 二进制判定必须优先于整文件读取：大体积二进制文件只需嗅探前 8KB，
                // 不应先把整个文件解码成字符串、再把整个 HEAD blob 物化后才判定。
                if ((currentExists && IsProbablyBinary(change.FullPath))
                    || await IsHeadBlobOversizedAsync(change.RelativePath))
                {
                    if (!IsCurrentGitChangeSelection(change, selectionVersion, cancellationToken))
                    {
                        tab.Dispose();
                        return;
                    }
                    tab.IsBinary = true;
                    tab.Mode = WorkspaceEditorMode.Binary;
                    StatusText = L("Workspace.Status.BinaryDiffUnsupported", "Binary file does not support text diff");
                }
                else
                {
                    var current = currentExists
                        ? await File.ReadAllTextAsync(change.FullPath, cancellationToken)
                        : string.Empty;
                    var changedAt = currentExists
                        ? File.GetLastWriteTimeUtc(change.FullPath)
                        : DateTime.UtcNow;
                    if (!IsCurrentGitChangeSelection(change, selectionVersion, cancellationToken))
                    {
                        tab.Dispose();
                        return;
                    }
                    tab.ReplaceFromDisk(current, changedAt);

                    var head = await ReadHeadVersionAsync(change.RelativePath);
                    if (!IsCurrentGitChangeSelection(change, selectionVersion, cancellationToken))
                    {
                        tab.Dispose();
                        return;
                    }
                    if (IsProbablyBinaryText(head))
                    {
                        tab.IsBinary = true;
                        tab.Mode = WorkspaceEditorMode.Binary;
                        StatusText = L("Workspace.Status.BinaryDiffUnsupported", "Binary file does not support text diff");
                    }
                    else
                    {
                        tab.CanDiff = true;
                        tab.SetDiff(WorkspaceDiffBuilder.Build(head ?? string.Empty, current));
                        tab.Mode = WorkspaceEditorMode.Diff;
                    }
                }
            }
            if (!IsCurrentGitChangeSelection(change, selectionVersion, cancellationToken))
            {
                tab.Dispose();
                return;
            }
            EditorTabs.Add(tab);
            OnPropertyChanged(nameof(HasEditorTabs));
        }
        else if (!tab.IsImage && !tab.IsBinary && !tab.IsOffice)
        {
            if (!tab.IsDirty)
            {
                var current = File.Exists(change.FullPath)
                    ? await File.ReadAllTextAsync(change.FullPath, cancellationToken)
                    : string.Empty;
                var changedAt = File.Exists(change.FullPath)
                    ? File.GetLastWriteTimeUtc(change.FullPath)
                    : DateTime.UtcNow;
                if (!IsCurrentGitChangeSelection(change, selectionVersion, cancellationToken)) return;
                tab.ReplaceFromDisk(current, changedAt);
            }
            await RefreshDiffAsync(tab);
            if (!IsCurrentGitChangeSelection(change, selectionVersion, cancellationToken)) return;
            tab.Mode = WorkspaceEditorMode.Diff;
        }

        if (!IsCurrentGitChangeSelection(change, selectionVersion, cancellationToken)) return;
        SelectedEditorTab = tab;
        IsEditorVisible = true;
        await PersistStateAsync();
    }

    private bool IsCurrentGitChangeSelection(
        GitChangeFileViewModel change,
        int selectionVersion,
        CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested
        && selectionVersion == _gitChangeSelectionVersion
        && SelectedGitChange != null
        && string.Equals(SelectedGitChange.RelativePath, change.RelativePath, PathComparison);

    [RelayCommand]
    private async Task OpenFileAsync(WorkspaceFileNodeViewModel? node)
    {
        await OpenFileAsync(node, activate: true, persist: true);
    }

    private async Task OpenFileAsync(
        WorkspaceFileNodeViewModel? node,
        bool activate,
        bool persist)
    {
        node ??= SelectedFile;
        if (node == null || node.IsDirectory || _workspace == null) return;
        var existing = EditorTabs.FirstOrDefault(tab => string.Equals(tab.FullPath, node.FullPath, StringComparison.Ordinal));
        if (existing != null)
        {
            if (activate)
            {
                SelectedEditorTab = existing;
                IsEditorVisible = true;
            }
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
        else if (tab.IsOffice)
        {
            // Office 预览判定先于文本大小上限与二进制嗅探：docx/xlsx/pptx/pdf 走 WebView 预览
            if (!OpenOfficePreview(tab)) return;
        }
        else if (tab.IsOfficeLegacy)
        {
            FallbackToBinaryPlaceholder(tab, "Workspace.Status.OfficeFormatUnsupported", "This format cannot be previewed (supports .docx/.xlsx/.pptx/.pdf)");
        }
        else
        {
            var info = new FileInfo(node.FullPath);
            if (info.Length > MaxEditableFileSize)
            {
                StatusText = L("Workspace.Status.FileTooLarge", "File exceeds 5 MB, not opened in built-in editor");
                return;
            }
            if (IsProbablyBinary(node.FullPath))
            {
                tab.IsBinary = true;
                tab.Mode = WorkspaceEditorMode.Binary;
                StatusText = L("Workspace.Status.BinaryDiffUnsupported", "Binary file does not support text diff");
            }
            else
            {
                tab.ReplaceFromDisk(await File.ReadAllTextAsync(node.FullPath), info.LastWriteTimeUtc);
                tab.CanDiff = await HasUncommittedChangesAsync(node.RelativePath);
                tab.Mode = tab.IsMarkdown ? WorkspaceEditorMode.Preview : WorkspaceEditorMode.Edit;
            }
        }
        EditorTabs.Add(tab);
        if (activate)
        {
            SelectedEditorTab = tab;
            IsEditorVisible = true;
        }
        OnPropertyChanged(nameof(HasEditorTabs));
        if (persist) await PersistStateAsync();
    }

    /// <summary>
    /// 注册 Office 预览会话并切换到 Office 模式。
    /// 预览服务器按请求现读磁盘，因此外部修改后刷新页面即可看到最新内容，
    /// 无需在此缓存文件内容。返回 false 表示文件过大未打开（调用方应中止）；
    /// 组件不可用时回退为二进制占位并照常打开 tab。
    /// </summary>
    private bool OpenOfficePreview(WorkspaceEditorTabViewModel tab)
    {
        var type = OfficePreviewTypes.PreviewType(tab.FullPath);
        if (type == null || _previewHost == null)
        {
            MarkOfficePreviewFailed(tab);
            return true;
        }
        if (new FileInfo(tab.FullPath).Length > MaxOfficePreviewFileSize)
        {
            StatusText = L("Workspace.Status.OfficeFileTooLarge", "File too large to preview");
            return false;
        }
        var sessionId = _previewHost.RegisterSession(tab.FullPath);
        // 主题取打开时刻的值（前端页面不跟随运行时主题切换）
        var isDark = Equals(Application.Current?.RequestedThemeVariant, ThemeVariant.Dark);
        var lang = _localizationService?.CurrentLanguage ?? "zh-CN";
        tab.PreviewSessionId = sessionId;
        tab.PreviewUrl = _previewHost.BuildPreviewUrl(sessionId, type, isDark ? "dark" : "light", lang, tab.FileName);
        tab.Mode = WorkspaceEditorMode.Office;
        return true;
    }

    /// <summary>WebView 创建/加载失败时由视图层调用：回退为二进制占位。</summary>
    public void MarkOfficePreviewFailed(WorkspaceEditorTabViewModel tab)
        => FallbackToBinaryPlaceholder(tab, "Workspace.Status.OfficePreviewUnavailable", "Preview component unavailable, showing placeholder");

    private void FallbackToBinaryPlaceholder(WorkspaceEditorTabViewModel tab, string messageKey, string fallback)
    {
        tab.IsBinary = true;
        tab.Mode = WorkspaceEditorMode.Binary;
        StatusText = L(messageKey, fallback);
    }

    [RelayCommand]
    private async Task SaveFileAsync(WorkspaceEditorTabViewModel? tab)
    {
        tab ??= SelectedEditorTab;
        if (tab == null || tab.IsImage || tab.IsBinary || _workspace == null) return;
        var root = Path.GetFullPath(_workspace.DirectoryPath);
        EnsureInsideWorkspace(root, tab.FullPath);
        StatusText = L("Workspace.Status.SavingQueue", "Waiting for workspace write queue…");
        await _operations.RunAsync(_workspace.Id, async cancellationToken =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(tab.FullPath)!);
            await File.WriteAllTextAsync(tab.FullPath, tab.Text, cancellationToken);
        });
        tab.MarkSaved();
        StatusText = string.Format(L("Workspace.Status.Saved", "Saved {0}"), tab.RelativePath);
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
        StatusText = string.Format(L("Workspace.Status.EditCancelled", "Discarded edits to {0}"), tab.RelativePath);
    }

    [RelayCommand]
    private async Task RefreshDiffAsync(WorkspaceEditorTabViewModel? tab)
    {
        tab ??= SelectedEditorTab;
        if (tab == null || tab.IsImage || tab.IsBinary || _workspace == null) return;
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
        if (SelectedEditorTab.IsOffice) return; // Office 预览为只读展示，不可切换模式
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
        if (tab.IsDirty && !await _interaction.ConfirmAsync(
                L("Workspace.Confirm.CloseDirty.Title", "Close file"),
                string.Format(L("Workspace.Confirm.CloseDirty.Message", "\"{0}\" has unsaved changes. Close anyway?"), tab.FileName),
                L("Workspace.Confirm.CloseDirty.Yes", "Close"),
                L("Common.Cancel", "Cancel"))) return;
        if (SelectedGitChange != null
            && string.Equals(SelectedGitChange.RelativePath, tab.RelativePath, PathComparison))
        {
            SelectedGitChange = null;
        }
        var index = EditorTabs.IndexOf(tab);
        EditorTabs.Remove(tab);
        if (tab.PreviewSessionId != null) _previewHost?.ReleaseSession(tab.PreviewSessionId);
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
        if (!await _interaction.ConfirmAsync(
                L("Workspace.Confirm.Trash.Title", "Move to trash"),
                string.Format(L("Workspace.Confirm.Trash.Message", "Delete \"{0}\"?"), node.RelativePath),
                L("Workspace.Confirm.Trash.Yes", "Delete"),
                L("Common.Cancel", "Cancel"))) return;
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
            StatusText = L("Workspace.Status.TrashedSuccess", "Moved to system trash, recoverable");
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to move to system trash: {Path}", node.FullPath);
            if (!await _interaction.ConfirmAsync(
                L("Workspace.Confirm.Permanent.Title", "Permanent delete"),
                L("Workspace.Confirm.Permanent.Message", "System trash unavailable. Permanent delete cannot be undone, continue?"),
                L("Workspace.Confirm.Permanent.Yes", "Permanently delete"),
                L("Common.Cancel", "Cancel"))) return;
            await _operations.RunAsync(_workspace.Id, _ =>
            {
                if (node.IsDirectory) Directory.Delete(node.FullPath, recursive: true);
                else File.Delete(node.FullPath);
                return Task.CompletedTask;
            });
            StatusText = L("Workspace.Status.PermanentlyDeleted", "Permanently deleted");
        }
        await RefreshFilesAsync();
    }

    [RelayCommand]
    private void BeginRenameFile(WorkspaceFileNodeViewModel? node)
    {
        if (node == null || node.IsRenamePending) return;
        if (_renamingFile != null && !ReferenceEquals(_renamingFile, node))
            CancelRenameFile(_renamingFile);
        SelectedFile = node;
        node.RenameError = string.Empty;
        node.RenameText = node.Name;
        node.IsRenaming = true;
        _renamingFile = node;
    }

    [RelayCommand]
    private void CancelRenameFile(WorkspaceFileNodeViewModel? node)
    {
        node ??= _renamingFile;
        if (node == null || node.IsRenamePending) return;
        node.IsRenaming = false;
        node.RenameError = string.Empty;
        node.RenameText = node.Name;
        if (ReferenceEquals(_renamingFile, node)) _renamingFile = null;
    }

    [RelayCommand]
    private async Task CommitRenameFileAsync(WorkspaceFileNodeViewModel? node)
    {
        node ??= _renamingFile;
        if (node == null || !node.IsRenaming || node.IsRenamePending) return;
        node.IsRenamePending = true;
        try
        {
            if (await TryRenameFileAsync(node, node.RenameText))
            {
                node.IsRenaming = false;
                node.RenameError = string.Empty;
                if (ReferenceEquals(_renamingFile, node)) _renamingFile = null;
            }
        }
        finally
        {
            node.IsRenamePending = false;
        }
    }

    private async Task<bool> TryRenameFileAsync(WorkspaceFileNodeViewModel node, string? newName)
    {
        if (_workspace == null) return false;

        var trimmedName = newName?.Trim() ?? string.Empty;
        if (trimmedName.Length == 0)
            return RejectRename(node, L("Workspace.Rename.Empty", "Name cannot be empty"));
        if (trimmedName is "." or ".."
            || trimmedName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return RejectRename(node, L("Workspace.Rename.InvalidChars", "Name contains invalid characters"));
        if (string.Equals(trimmedName, node.Name, StringComparison.Ordinal))
            return true;

        string destination;
        string root;
        try
        {
            destination = Path.GetFullPath(
                Path.Combine(Path.GetDirectoryName(node.FullPath)!, trimmedName));
            root = Path.GetFullPath(_workspace.DirectoryPath);
            EnsureInsideWorkspace(root, destination);
        }
        catch (Exception ex) when (ex is ArgumentException
                                   or NotSupportedException
                                   or PathTooLongException
                                   or InvalidOperationException)
        {
            _logger.Warning(ex, "Workspace rename failed: invalid name {Path} -> {Name}", node.FullPath, trimmedName);
            return RejectRename(node, L("Workspace.Rename.Invalid", "Invalid name"));
        }

        try
        {
            var destinationExists = false;
            await _operations.RunAsync(_workspace.Id, _ =>
            {
                if (File.Exists(destination) || Directory.Exists(destination))
                {
                    destinationExists = true;
                    return Task.CompletedTask;
                }
                if (node.IsDirectory) Directory.Move(node.FullPath, destination);
                else File.Move(node.FullPath, destination);
                return Task.CompletedTask;
            });
            if (destinationExists) return RejectRename(node, string.Format(L("Workspace.Rename.Exists", "\"{0}\" already exists"), trimmedName));
        }
        catch (IOException ex)
        {
            _logger.Warning(ex, "Workspace rename failed: {Source} -> {Destination}", node.FullPath, destination);
            var message = File.Exists(destination) || Directory.Exists(destination)
                ? string.Format(L("Workspace.Rename.Exists", "\"{0}\" already exists"), trimmedName)
                : L("Workspace.Rename.InUse", "Rename failed; the file may be in use");
            return RejectRename(node, message);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.Warning(ex, "Workspace rename permission denied: {Source} -> {Destination}", node.FullPath, destination);
            return RejectRename(node, L("Workspace.Rename.NoPermission", "No permission to rename this item"));
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Unexpected error during workspace rename: {Source} -> {Destination}", node.FullPath, destination);
            return RejectRename(node, L("Workspace.Rename.UnexpectedFailed", "Rename failed"));
        }

        StatusText = string.Format(L("Workspace.Status.RenamedTo", "Renamed to {0}"), trimmedName);
        // 打开的 Tab 不追随移动；保存时会按原路径新建，符合工作区编辑器约定。
        var renamedRelativePath = Path.GetRelativePath(root, destination);
        await RefreshFilesAsync();
        SelectedFile = EnumerateNodes(Files).FirstOrDefault(
            candidate => string.Equals(candidate.RelativePath, renamedRelativePath, PathComparison));
        return true;
    }

    private bool RejectRename(WorkspaceFileNodeViewModel node, string message)
    {
        node.RenameError = message;
        StatusText = message;
        return false;
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
        var refreshGitState = HasGitRepository
                              && (!isGitMetadata || IsRelevantGitMetadataPath(e.FullPath));
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
                    else if (tab.IsOffice)
                    {
                        // Office 预览由服务器按请求现读磁盘，外部修改后刷新页面即可看到最新内容，
                        // 保持 Office 模式，不翻牌为二进制占位。
                    }
                    else if (changedAt >= tab.LastLocalEditAt)
                    {
                        if (IsProbablyBinary(tab.FullPath))
                        {
                            tab.IsBinary = true;
                            tab.SetDiff([]);
                            tab.Mode = WorkspaceEditorMode.Binary;
                        }
                        else
                        {
                            if (tab.IsBinary) tab.IsBinary = false;
                            tab.ReplaceFromDisk(await File.ReadAllTextAsync(tab.FullPath), changedAt);
                            tab.CanDiff = await HasUncommittedChangesAsync(tab.RelativePath);
                            if (tab.CanDiff)
                            {
                                await RefreshDiffAsync(tab);
                                tab.Mode = WorkspaceEditorMode.Diff;
                            }
                            else if (tab.Mode is WorkspaceEditorMode.Diff or WorkspaceEditorMode.Binary)
                            {
                                tab.SetDiff([]);
                                tab.Mode = WorkspaceEditorMode.Edit;
                            }
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
        var cts = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _refreshDebounce, cts);
        previous?.Cancel();
        _ = RunScheduledRefreshAsync(cts);
    }

    private async Task RunScheduledRefreshAsync(CancellationTokenSource cts)
    {
        var cancellationToken = cts.Token;
        try
        {
            await Task.Delay(250, cancellationToken);
            await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (cancellationToken.IsCancellationRequested
                    || !ReferenceEquals(
                        Interlocked.CompareExchange(ref _refreshDebounce, null, cts),
                        cts))
                {
                    return;
                }

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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to refresh workspace state");
        }
        finally
        {
            Interlocked.CompareExchange(ref _refreshDebounce, null, cts);
            cts.Dispose();
        }
    }

    private void CancelScheduledRefresh()
    {
        var cts = Interlocked.Exchange(ref _refreshDebounce, null);
        cts?.Cancel();
        _refreshFilesPending = false;
        _refreshGitStatePending = false;
    }

    private async Task RefreshOpenTabGitStateAsync()
    {
        // Office 预览不参与文本 Diff，跳过其 git 状态查询
        foreach (var tab in EditorTabs.Where(tab => !tab.IsImage && !tab.IsOffice).ToList())
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

    private bool IsRelevantGitMetadataPath(string path)
    {
        if (_workspace == null) return false;
        var gitPath = Path.GetFullPath(Path.Combine(_workspace.DirectoryPath, ".git"));
        var relative = Path.GetRelativePath(gitPath, Path.GetFullPath(path))
            .Replace(Path.DirectorySeparatorChar, '/');
        return relative is "HEAD" or "index" or "packed-refs"
               || relative.StartsWith("refs/", StringComparison.Ordinal)
               || relative.StartsWith("logs/refs/", StringComparison.Ordinal);
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

    private static void ReconcileGitChanges(
        ObservableCollection<GitChangeFileViewModel> current,
        IReadOnlyList<GitChangeFileViewModel> desired)
    {
        for (var desiredIndex = 0; desiredIndex < desired.Count; desiredIndex++)
        {
            var desiredChange = desired[desiredIndex];
            var existingIndex = -1;
            for (var currentIndex = desiredIndex; currentIndex < current.Count; currentIndex++)
            {
                if (string.Equals(
                        current[currentIndex].RelativePath,
                        desiredChange.RelativePath,
                        PathComparison))
                {
                    existingIndex = currentIndex;
                    break;
                }
            }

            if (existingIndex < 0)
            {
                current.Insert(desiredIndex, desiredChange);
                continue;
            }

            if (existingIndex != desiredIndex)
                current.Move(existingIndex, desiredIndex);

            if (!GitChangesEqual(current[desiredIndex], desiredChange))
                current[desiredIndex] = desiredChange;
        }

        while (current.Count > desired.Count)
            current.RemoveAt(current.Count - 1);
    }

    private static bool GitChangesEqual(
        GitChangeFileViewModel left,
        GitChangeFileViewModel right) =>
        string.Equals(left.RelativePath, right.RelativePath, PathComparison)
        && string.Equals(left.OriginalRelativePath, right.OriginalRelativePath, PathComparison)
        && string.Equals(left.FullPath, right.FullPath, PathComparison)
        && string.Equals(left.StatusCode, right.StatusCode, StringComparison.Ordinal)
        && string.Equals(left.StatusLabel, right.StatusLabel, StringComparison.Ordinal)
        && left.IsUntracked == right.IsUntracked
        && left.HasStagedChange == right.HasStagedChange
        && left.HasWorkingTreeChange == right.HasWorkingTreeChange;

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

    private static bool IsProbablyBinary(string path)
    {
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> buffer = stackalloc byte[8192];
            var read = stream.Read(buffer);
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] == 0) return true;
            }
            return false;
        }
        catch
        {
            // If the file cannot be read, fall back to text handling.
            return false;
        }
    }

    private static bool IsProbablyBinaryText(string? text)
        => text != null && text.IndexOf('\0') >= 0;

    /// <summary>
    /// 判断 HEAD 侧的 blob 是否体积过大而应视为二进制/不可 Diff，
    /// 避免 <c>git cat-file --filters</c> 把整个大体积 blob 物化进内存。
    /// </summary>
    private async Task<bool> IsHeadBlobOversizedAsync(string relativePath)
    {
        if (_workspace == null || !HasGitRepository) return false;
        var normalized = relativePath.Replace(Path.DirectorySeparatorChar, '/');
        var result = await RunGitAsync("cat-file", "-s", $"HEAD:{normalized}");
        if (result.ExitCode != 0) return false;
        return long.TryParse(result.Output.Trim(), out var size) && size > MaxEditableFileSize;
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
            start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
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

    private IReadOnlyList<GitChangeFileViewModel> ParseGitStatus(string output, string workspaceRoot)
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

    private string DescribeGitStatus(char indexStatus, char workTreeStatus)
    {
        if (indexStatus == '?' && workTreeStatus == '?') return L("Workspace.Git.Status.Untracked", "Untracked");
        if (indexStatus == 'U' || workTreeStatus == 'U') return L("Workspace.Git.Status.Conflict", "Conflict");
        if (indexStatus == 'R' || workTreeStatus == 'R') return L("Workspace.Git.Status.Renamed", "Renamed");
        if (indexStatus == 'C' || workTreeStatus == 'C') return L("Workspace.Git.Status.Copied", "Copied");
        if (indexStatus == 'D' || workTreeStatus == 'D') return L("Workspace.Git.Status.Deleted", "Deleted");
        if (indexStatus == 'A') return workTreeStatus == ' '
            ? L("Workspace.Git.Status.StagedAdded", "Staged addition")
            : L("Workspace.Git.Status.Added", "Added");
        if (indexStatus != ' ') return workTreeStatus == ' '
            ? L("Workspace.Git.Status.Staged", "Staged")
            : L("Workspace.Git.Status.PartiallyStaged", "Partially staged");
        return L("Workspace.Git.Status.Modified", "Modified");
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
        return string.IsNullOrWhiteSpace(detail) ? prefix : $"{prefix}: {detail}";
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
        catch (Exception ex) { _logger.Debug(ex, "Failed to save editor workspace state"); }
    }

    private async Task RestoreStateAsync()
    {
        if (!File.Exists(StatePath)) return;
        try
        {
            var state = JsonSerializer.Deserialize<WorkspaceEditorState>(await File.ReadAllTextAsync(StatePath));
            if (state == null) return;
            foreach (var tabState in state.Tabs.Where(tab => File.Exists(tab.FullPath)))
            {
                var node = new WorkspaceFileNodeViewModel
                {
                    Name = Path.GetFileName(tabState.FullPath),
                    FullPath = tabState.FullPath,
                    RelativePath = Path.GetRelativePath(_workspace!.DirectoryPath, tabState.FullPath)
                };
                await OpenFileAsync(node, activate: false, persist: false);
                var tab = EditorTabs.FirstOrDefault(
                    candidate => string.Equals(candidate.FullPath, tabState.FullPath, PathComparison));
                if (tab == null) continue;
                var canRestoreMode = tabState.Mode switch
                {
                    WorkspaceEditorMode.Edit => tab.CanEdit,
                    WorkspaceEditorMode.Preview => tab.CanPreview,
                    WorkspaceEditorMode.Diff => tab.CanDiff,
                    WorkspaceEditorMode.Image => tab.IsImage,
                    WorkspaceEditorMode.Binary => tab.IsBinary,
                    WorkspaceEditorMode.Office => tab.IsOffice,
                    _ => false
                };
                if (!canRestoreMode) continue;
                if (tabState.Mode == WorkspaceEditorMode.Diff)
                    await RefreshDiffAsync(tab);
                else
                    tab.Mode = tabState.Mode;
            }
            SelectedEditorTab = EditorTabs.FirstOrDefault(
                                    tab => string.Equals(tab.FullPath, state.SelectedPath, PathComparison))
                                ?? EditorTabs.FirstOrDefault();
            IsEditorVisible = state.IsEditorVisible && EditorTabs.Count > 0;
        }
        catch (Exception ex) { _logger.Debug(ex, "Failed to restore editor workspace state"); }
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
        if (_disposed) return;
        _disposed = true;
        if (_localizationService != null)
        {
            _localizationService.LanguageChanged -= OnLanguageChanged;
        }
        DisposeWatcher();
        _previewHost?.ReleaseAll();
        foreach (var tab in EditorTabs) tab.Dispose();
        CancelScheduledRefresh();
        var gitChangeOpen = Interlocked.Exchange(ref _gitChangeOpenCts, null);
        gitChangeOpen?.Cancel();
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
