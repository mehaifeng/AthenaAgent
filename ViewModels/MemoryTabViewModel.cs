using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Athena.UI.ViewModels;

public partial class MemoryTabViewModel : ViewModelBase
{
    private readonly IKnowledgeBaseService? _knowledgeBaseService;
    private readonly ILogger _logger = Log.ForContext<MemoryTabViewModel>();

    public ObservableCollection<KnowledgeFileNode> KnowledgeFiles { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedFileDisplayPath))]
    private KnowledgeFileNode? _selectedKnowledgeFile;

    public string SelectedFileDisplayPath => SelectedKnowledgeFile?.FullPath ?? "Select a file to view";

    [ObservableProperty]
    private string _editingFileContent = string.Empty;

    [ObservableProperty]
    private bool _isEditingFile;

    [ObservableProperty]
    private string _newFolderName = string.Empty;

    [ObservableProperty]
    private string _newFileName = string.Empty;

    public MemoryTabViewModel() : this(null) { }

    public MemoryTabViewModel(IKnowledgeBaseService? knowledgeBaseService)
    {
        _knowledgeBaseService = knowledgeBaseService;
        LoadKnowledgeFilesAsync().ConfigureAwait(false);
    }

    partial void OnSelectedKnowledgeFileChanged(KnowledgeFileNode? value)
    {
        if (value == null || value.IsDirectory)
        {
            EditingFileContent = string.Empty;
            IsEditingFile = false;
            return;
        }
        _ = LoadFileContentAsync(value.FullPath);
    }

    private async Task LoadFileContentAsync(string filePath)
    {
        if (_knowledgeBaseService == null) return;
        try
        {
            var content = await _knowledgeBaseService.ReadFileAsync(filePath);
            EditingFileContent = content ?? "文件内容为空";
            IsEditingFile = true;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "加载文件失败: {File}", filePath);
            EditingFileContent = $"加载失败: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task RefreshKnowledgeBaseAsync() => await LoadKnowledgeFilesAsync();

    [RelayCommand]
    private async Task NewFolderAsync()
    {
        if (_knowledgeBaseService == null || string.IsNullOrWhiteSpace(NewFolderName)) return;
        try
        {
            var folderPath = NewFolderName.Trim('/');
            await _knowledgeBaseService.CreateFileAsync($"{folderPath}/README.md", $"# {folderPath}\n\n此文件夹用于存储相关知识。\n");
            NewFolderName = string.Empty;
            await LoadKnowledgeFilesAsync();
        }
        catch (Exception ex) { _logger.Error(ex, "创建文件夹失败"); }
    }

    [RelayCommand]
    private async Task NewFileAsync()
    {
        if (_knowledgeBaseService == null || string.IsNullOrWhiteSpace(NewFileName)) return;
        try
        {
            var fileName = NewFileName.Trim();
            if (!fileName.EndsWith(".md")) fileName += ".md";
            var filePath = SelectedKnowledgeFile?.IsDirectory == true ? $"{SelectedKnowledgeFile.FullPath}/{fileName}" : fileName;
            await _knowledgeBaseService.CreateFileAsync(filePath, $"# {Path.GetFileNameWithoutExtension(fileName)}\n\n");
            NewFileName = string.Empty;
            await LoadKnowledgeFilesAsync();
        }
        catch (Exception ex) { _logger.Error(ex, "创建文件失败"); }
    }

    [RelayCommand]
    private async Task DeleteFileAsync()
    {
        if (_knowledgeBaseService == null || SelectedKnowledgeFile == null) return;
        try
        {
            if (SelectedKnowledgeFile.IsDirectory) await _knowledgeBaseService.DeleteDirectoryAsync(SelectedKnowledgeFile.FullPath);
            else await _knowledgeBaseService.DeleteFileAsync(SelectedKnowledgeFile.FullPath);
            EditingFileContent = string.Empty;
            IsEditingFile = false;
            await LoadKnowledgeFilesAsync();
        }
        catch (Exception ex) { _logger.Error(ex, "删除失败"); }
    }

    [RelayCommand]
    private async Task ViewFileAsync()
    {
        if (SelectedKnowledgeFile == null || SelectedKnowledgeFile.IsDirectory) return;
        await LoadFileContentAsync(SelectedKnowledgeFile.FullPath);
    }

    [RelayCommand]
    private async Task SaveFileAsync()
    {
        if (_knowledgeBaseService == null || SelectedKnowledgeFile == null) return;
        try
        {
            await _knowledgeBaseService.ReplaceFileAsync(SelectedKnowledgeFile.FullPath, EditingFileContent);
            IsEditingFile = false;
        }
        catch (Exception ex) { _logger.Error(ex, "保存文件失败"); }
    }

    [RelayCommand]
    private void CancelEdit() { IsEditingFile = false; EditingFileContent = string.Empty; }

    [RelayCommand]
    private async Task ImportAsync()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
        var storageProvider = desktop.MainWindow?.StorageProvider;
        if (storageProvider == null || _knowledgeBaseService == null) return;
        var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择要导入的 Markdown 文件",
            AllowMultiple = true,
            FileTypeFilter = new[] { new FilePickerFileType("Markdown Files") { Patterns = new[] { "*.md" } } }
        });
        if (files.Count == 0) return;
        foreach (var file in files)
        {
            try { var content = await File.ReadAllTextAsync(file.Path.LocalPath); await _knowledgeBaseService.CreateFileAsync(file.Name, content); }
            catch (Exception ex) { _logger.Error(ex, "导入失败: {File}", file.Name); }
        }
        await LoadKnowledgeFilesAsync();
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
        var storageProvider = desktop.MainWindow?.StorageProvider;
        if (storageProvider == null || _knowledgeBaseService == null) return;
        var folder = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "选择导出目录" });
        if (folder.Count == 0) return;
        var targetPath = folder[0].Path.LocalPath;
        try
        {
            var files = await _knowledgeBaseService.ListFilesAsync();
            foreach (var relPath in files)
            {
                var content = await _knowledgeBaseService.ReadFileAsync(relPath);
                if (content == null) continue;
                var fullTarget = Path.Combine(targetPath, relPath);
                Directory.CreateDirectory(Path.GetDirectoryName(fullTarget)!);
                await File.WriteAllTextAsync(fullTarget, content);
            }
        }
        catch (Exception ex) { _logger.Error(ex, "导出失败"); }
    }

    private async Task LoadKnowledgeFilesAsync()
    {
        KnowledgeFiles.Clear();
        if (_knowledgeBaseService == null) return;
        try
        {
            var directories = await _knowledgeBaseService.ListDirectoriesAsync();
            var files = await _knowledgeBaseService.ListFilesAsync();
            var rootNode = new KnowledgeFileNode { Name = "Knowledge Base", IsDirectory = true, IsExpanded = true, FullPath = "" };
            var directoryNodes = new Dictionary<string, KnowledgeFileNode>();

            foreach (var dirPath in directories)
            {
                var parts = dirPath.Split('/');
                var currentPath = "";
                for (int i = 0; i < parts.Length; i++)
                {
                    var dirName = parts[i];
                    var parentPath = currentPath;
                    currentPath = string.IsNullOrEmpty(currentPath) ? dirName : $"{currentPath}/{dirName}";
                    if (!directoryNodes.ContainsKey(currentPath))
                    {
                        var dirNode = new KnowledgeFileNode { Name = dirName, IsDirectory = true, IsExpanded = true, FullPath = currentPath };
                        directoryNodes[currentPath] = dirNode;
                        if (string.IsNullOrEmpty(parentPath)) rootNode.Children.Add(dirNode);
                        else if (directoryNodes.TryGetValue(parentPath, out var parentNode)) parentNode.Children.Add(dirNode);
                    }
                }
            }
            foreach (var filePath in files.OrderBy(f => f))
            {
                var parts = filePath.Split('/');
                var fileName = parts[^1];
                var fileNode = new KnowledgeFileNode { Name = fileName, IsDirectory = false, FullPath = filePath };
                var fileDirPath = string.Join("/", parts[..^1]);
                if (string.IsNullOrEmpty(fileDirPath)) rootNode.Children.Add(fileNode);
                else if (directoryNodes.TryGetValue(fileDirPath, out var fileDirNode)) fileDirNode.Children.Add(fileNode);
            }
            foreach (var child in rootNode.Children) KnowledgeFiles.Add(child);
        }
        catch (Exception ex) { _logger.Error(ex, "加载知识库失败"); }
    }
}
