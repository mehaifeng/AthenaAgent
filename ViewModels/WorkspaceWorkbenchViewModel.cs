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
    public ObservableCollection<WorkspaceFileNodeViewModel> Children { get; } = new();

    [ObservableProperty]
    private bool _isExpanded;
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
    private Bitmap? _image;

    public string FullPath { get; init; } = string.Empty;
    public string RelativePath { get; init; } = string.Empty;
    public string FileName => Path.GetFileName(FullPath);
    public bool IsMarkdown => string.Equals(Path.GetExtension(FullPath), ".md", StringComparison.OrdinalIgnoreCase)
                              || string.Equals(Path.GetExtension(FullPath), ".markdown", StringComparison.OrdinalIgnoreCase);
    public bool IsImage => ImageExtensions.Contains(Path.GetExtension(FullPath));
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
    private WorkspaceEditorMode _mode;

    [ObservableProperty]
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
        LastExternalChangeAt = changedAt;
        IsDirty = false;
    }

    public void MarkSaved() => IsDirty = false;

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
    private WorkspaceProfile? _workspace;

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

    [ObservableProperty]
    private WorkspaceEditorTabViewModel? _selectedEditorTab;

    [ObservableProperty]
    private WorkspaceFileNodeViewModel? _selectedFile;

    [ObservableProperty]
    private bool _isEditorVisible = true;

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

    public bool HasWorkspace => _workspace != null;
    public bool HasEditorTabs => EditorTabs.Count > 0;

    public async Task SetWorkspaceAsync(WorkspaceProfile? workspace)
    {
        if (_workspace?.Id == workspace?.Id) return;
        await PersistStateAsync();
        DisposeWatcher();
        foreach (var tab in EditorTabs) tab.Dispose();
        EditorTabs.Clear();
        Files.Clear();
        _workspace = workspace;
        WorkspaceName = workspace?.Name ?? "全局对话";
        StatusText = workspace == null ? "全局对话不使用工作区文件" : workspace.DirectoryPath;
        OnPropertyChanged(nameof(HasWorkspace));
        OnPropertyChanged(nameof(HasEditorTabs));
        if (workspace == null || !Directory.Exists(workspace.DirectoryPath)) return;
        await RefreshFilesAsync();
        await RestoreStateAsync();
        StartWatcher(workspace.DirectoryPath);
    }

    [RelayCommand]
    private async Task RefreshFilesAsync()
    {
        if (_workspace == null || !Directory.Exists(_workspace.DirectoryPath)) return;
        IsLoadingFiles = true;
        try
        {
            var nodes = await Task.Run(() => BuildTree(_workspace.DirectoryPath, _workspace.DirectoryPath, 0));
            Files.Clear();
            foreach (var node in nodes) Files.Add(node);
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
            tab.CanDiff = await IsGitTrackedAsync(node.RelativePath);
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
        tab.CanDiff = await IsGitTrackedAsync(tab.RelativePath);
        if (tab.CanDiff)
        {
            await RefreshDiffAsync(tab);
            tab.Mode = WorkspaceEditorMode.Diff;
        }
    }

    [RelayCommand]
    private async Task RefreshDiffAsync(WorkspaceEditorTabViewModel? tab)
    {
        tab ??= SelectedEditorTab;
        if (tab == null || tab.IsImage || _workspace == null) return;
        var head = await ReadHeadVersionAsync(tab.RelativePath);
        tab.SetDiff(WorkspaceDiffBuilder.Build(head ?? string.Empty, tab.Text));
        tab.CanDiff = head != null;
        if (tab.CanDiff) tab.Mode = WorkspaceEditorMode.Diff;
    }

    [RelayCommand]
    private void SetEditorMode(string? mode)
    {
        if (SelectedEditorTab == null || !Enum.TryParse<WorkspaceEditorMode>(mode, true, out var parsed)) return;
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
        var index = EditorTabs.IndexOf(tab);
        EditorTabs.Remove(tab);
        tab.Dispose();
        SelectedEditorTab = EditorTabs.Count == 0 ? null : EditorTabs[Math.Clamp(index, 0, EditorTabs.Count - 1)];
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
                        tab.CanDiff = await IsGitTrackedAsync(tab.RelativePath);
                        if (tab.CanDiff)
                        {
                            await RefreshDiffAsync(tab);
                            tab.Mode = WorkspaceEditorMode.Diff;
                        }
                    }
                }
                catch (IOException)
                {
                    ScheduleRefresh();
                }
            }
            ScheduleRefresh();
        });
    }

    private void ScheduleRefresh()
    {
        _refreshDebounce?.Cancel();
        _refreshDebounce?.Dispose();
        var cts = new CancellationTokenSource();
        _refreshDebounce = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(250, cts.Token);
                await Dispatcher.UIThread.InvokeAsync(RefreshFilesAsync);
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private static List<WorkspaceFileNodeViewModel> BuildTree(string root, string directory, int depth)
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
            var node = new WorkspaceFileNodeViewModel
            {
                Name = Path.GetFileName(entry),
                FullPath = entry,
                RelativePath = Path.GetRelativePath(root, entry),
                IsDirectory = isDirectory
            };
            if (isDirectory)
            {
                foreach (var child in BuildTree(root, entry, depth + 1)) node.Children.Add(child);
            }
            result.Add(node);
        }
        return result;
    }

    private async Task<bool> IsGitTrackedAsync(string relativePath)
    {
        if (_workspace == null || !Directory.Exists(Path.Combine(_workspace.DirectoryPath, ".git"))) return false;
        var result = await RunGitAsync("ls-files", "--error-unmatch", "--", relativePath);
        return result.ExitCode == 0;
    }

    private async Task<string?> ReadHeadVersionAsync(string relativePath)
    {
        if (_workspace == null || !Directory.Exists(Path.Combine(_workspace.DirectoryPath, ".git"))) return null;
        var normalized = relativePath.Replace(Path.DirectorySeparatorChar, '/');
        var result = await RunGitAsync("show", $"HEAD:{normalized}");
        return result.ExitCode == 0 ? result.Output : string.Empty;
    }

    private async Task<(int ExitCode, string Output)> RunGitAsync(params string[] arguments)
    {
        if (_workspace == null) return (-1, string.Empty);
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = _workspace.DirectoryPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start);
        if (process == null) return (-1, string.Empty);
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, output);
    }

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
                if (SelectedEditorTab != null) SelectedEditorTab.Mode = tabState.Mode;
            }
            SelectedEditorTab = EditorTabs.FirstOrDefault(tab => tab.FullPath == state.SelectedPath) ?? EditorTabs.FirstOrDefault();
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
        public bool IsEditorVisible { get; set; } = true;
        public string? SelectedPath { get; set; }
        public List<WorkspaceEditorTabState> Tabs { get; set; } = [];
    }

    private sealed class WorkspaceEditorTabState
    {
        public string FullPath { get; set; } = string.Empty;
        public WorkspaceEditorMode Mode { get; set; }
    }
}
