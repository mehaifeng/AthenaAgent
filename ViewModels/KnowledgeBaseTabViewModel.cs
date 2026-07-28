using Athena.UI.Models;
using Athena.UI.Services;
using Athena.UI.Services.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Ursa.Controls;

namespace Athena.UI.ViewModels;

/// <summary>
/// 知识库 Tab 视图模型
/// 管理 AthenaData/KnowledgeBase 目录下的 Markdown 文件
/// </summary>
public partial class KnowledgeBaseTabViewModel : ViewModelBase
{
    private readonly IFileSystemService? _fileSystemService;
    private readonly IPlatformPathService? _pathService;
    private readonly IKnowledgeBaseService? _knowledgeBaseService;
    private readonly ILocalizationService? _localizationService;
    private readonly IUserInteractionService? _userInteractionService;
    private readonly IKnowledgeBaseMaintenanceService? _maintenanceService;
    private readonly AppConfigurationSession? _configurationSession;
    private readonly ILogger _logger = Log.ForContext<KnowledgeBaseTabViewModel>();

    public ObservableCollection<KnowledgeFileNode> Files { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedFileDisplayPath))]
    private KnowledgeFileNode? _selectedFile;

    public string SelectedFileDisplayPath => SelectedFile?.FullPath ?? GetString("Knowledge.SelectFile", "Select a file to view");

    [ObservableProperty]
    private string _editingFileContent = string.Empty;

    [ObservableProperty]
    private bool _isEditingFile;

    [ObservableProperty]
    private string _newFolderName = string.Empty;

    [ObservableProperty]
    private string _newFileName = string.Empty;

    [ObservableProperty]
    private AppConfig _config = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunKnowledgeMaintenanceCommand))]
    private bool _isRunningMaintenance;

    [ObservableProperty]
    private string _maintenanceStatus = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RebuildVectorIndexCommand))]
    private bool _isRebuildingVectorIndex;

    [ObservableProperty]
    private string _vectorIndexStatus = string.Empty;

    private bool CanRunKnowledgeMaintenance => !IsRunningMaintenance;
    private bool CanRebuildVectorIndex => !IsRebuildingVectorIndex;

    private string _rootPath = string.Empty;

    public KnowledgeBaseTabViewModel() : this(null, null, null, null, null, null, null) { }

    public KnowledgeBaseTabViewModel(
        IFileSystemService? fileSystemService, 
        IPlatformPathService? pathService,
        IKnowledgeBaseService? knowledgeBaseService,
        ILocalizationService? localizationService = null,
        IUserInteractionService? userInteractionService = null,
        IKnowledgeBaseMaintenanceService? maintenanceService = null,
        AppConfigurationSession? configurationSession = null)
    {
        _fileSystemService = fileSystemService;
        _pathService = pathService;
        _knowledgeBaseService = knowledgeBaseService;
        _localizationService = localizationService;
        _userInteractionService = userInteractionService;
        _maintenanceService = maintenanceService;
        _configurationSession = configurationSession;

        if (_configurationSession != null)
        {
            Config = _configurationSession.Current;
            _configurationSession.CurrentChanged += OnCurrentConfigChanged;
        }

        if (_maintenanceService != null)
        {
            _maintenanceService.StateChanged += OnMaintenanceStateChanged;
            IsRunningMaintenance = _maintenanceService.IsRunning;
            UpdateMaintenanceStatus();
        }
        
        if (_pathService != null)
        {
            _rootPath = _pathService.GetKnowledgeBaseDirectory();
        }
    }

    private void OnCurrentConfigChanged(object? sender, AppConfig config) => Config = config;

    [RelayCommand(CanExecute = nameof(CanRunKnowledgeMaintenance))]
    private async Task RunKnowledgeMaintenanceAsync()
    {
        if (_maintenanceService == null)
        {
            MaintenanceStatus = GetString("Status.ServiceNotInitialized", "Service not initialized");
            return;
        }

        IsRunningMaintenance = true;
        MaintenanceStatus = GetString("Config.KbMaintenanceRunning", "Organizing knowledge base…");
        try
        {
            await _maintenanceService.RunNowAsync();
            UpdateMaintenanceStatus();
        }
        catch (Exception ex)
        {
            MaintenanceStatus = ex.Message;
        }
        finally
        {
            IsRunningMaintenance = false;
        }
    }

    private void OnMaintenanceStateChanged(object? sender, EventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            IsRunningMaintenance = _maintenanceService?.IsRunning ?? false;
            UpdateMaintenanceStatus();
        });
    }

    private void UpdateMaintenanceStatus()
    {
        var state = _maintenanceService?.State;
        if (state?.LastRunUtc == null)
        {
            MaintenanceStatus = GetString("Config.KbMaintenanceNeverRun", "Never organized");
            return;
        }

        var time = state.LastRunUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        var conclusion = state.LastOutcome switch
        {
            "Succeeded" => string.Format(
                GetString("Config.KbMaintenanceDoneMerged", "Processed {0} potential duplicate group(s)"),
                state.LastMergedGroups),
            "NoDuplicates" => GetString("Config.KbMaintenanceNoDup", "No duplicate files found"),
            "Failed" => GetString("Config.KbMaintenanceFailed", "Organization failed; see logs"),
            "Skipped" => GetString("Config.KbMaintenanceSkipped", "Skipped because Embedding is not configured"),
            _ => state.LastOutcome ?? string.Empty
        };
        MaintenanceStatus = $"{time} · {conclusion}";
    }

    [RelayCommand(CanExecute = nameof(CanRebuildVectorIndex))]
    private async Task RebuildVectorIndexAsync()
    {
        if (_knowledgeBaseService == null)
        {
            VectorIndexStatus = GetString("Status.ServiceNotInitialized", "Service not initialized");
            return;
        }

        IsRebuildingVectorIndex = true;
        VectorIndexStatus = GetString("Config.VectorIndexRebuilding", "Rebuilding vector index…");
        try
        {
            if (_configurationSession != null) await _configurationSession.SaveNowAsync();
            var result = await _knowledgeBaseService.RebuildVectorIndexAsync();
            VectorIndexStatus = result.IsFullyIndexed
                ? GetString("Config.VectorIndexRebuilt", "Vector index rebuilt")
                : string.Format(
                    GetString(
                        "Config.VectorIndexRebuildIncomplete",
                        "Vector index incomplete: {0}/{1} chunks indexed. Check the embedding model or provider response."),
                    result.VectorCount,
                    result.ChunkCount);
        }
        catch (Exception ex)
        {
            VectorIndexStatus = string.Format(
                GetString("Config.VectorIndexRebuildFailed", "Failed to rebuild vector index: {0}"),
                ex.Message);
        }
        finally
        {
            IsRebuildingVectorIndex = false;
        }
    }

    /// <summary>
    /// 在视图 Loaded 时调用，确保目录存在并刷新树
    /// </summary>
    public async Task InitializeAsync()
    {
        if (string.IsNullOrEmpty(_rootPath)) return;

        if (!Directory.Exists(_rootPath))
        {
            Directory.CreateDirectory(_rootPath);
            _logger.Information("初始化: 创建知识库根目录 {Path}", _rootPath);
        }

        await RefreshFilesAsync();
    }

    partial void OnSelectedFileChanged(KnowledgeFileNode? value)
    {
        if (value == null || value.IsDirectory)
        {
            EditingFileContent = string.Empty;
            IsEditingFile = false;
            return;
        }
        _ = LoadFileContentAsync(value.FullPath);
    }

    private async Task LoadFileContentAsync(string absolutePath)
    {
        if (_fileSystemService == null) return;
        try
        {
            var content = await _fileSystemService.ReadFileAsync(absolutePath);
            EditingFileContent = content ?? "[ FILE_CONTENT_EMPTY ]";
            IsEditingFile = false;  // 加载后默认只读/预览模式
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "加载文件失败: {File}", absolutePath);
            EditingFileContent = string.Format(GetString("Knowledge.LoadFailed", "Failed to load: {0}"), ex.Message);
        }
    }

    [RelayCommand]
    public async Task RefreshFilesAsync()
    {
        if (string.IsNullOrEmpty(_rootPath)) return;

        if (!Directory.Exists(_rootPath))
        {
            Directory.CreateDirectory(_rootPath);
        }

        Files.Clear();
        try
        {
            var rootNode = new KnowledgeFileNode 
            { 
                Name = "KnowledgeBase", 
                IsDirectory = true, 
                IsExpanded = true, 
                FullPath = _rootPath 
            };

            await BuildFileTreeAsync(rootNode, _rootPath);

            foreach (var child in rootNode.Children)
            {
                Files.Add(child);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "构建文件树失败");
        }
    }

    private Task BuildFileTreeAsync(KnowledgeFileNode parentNode, string currentPath)
    {
        var dirInfo = new DirectoryInfo(currentPath);

        // 先添加目录，递归保留树状结构
        foreach (var dir in dirInfo.GetDirectories().OrderBy(d => d.Name))
        {
            var dirNode = new KnowledgeFileNode
            {
                Name = dir.Name,
                IsDirectory = true,
                FullPath = dir.FullName,
                IsExpanded = false
            };
            parentNode.Children.Add(dirNode);
            // 递归
            _ = BuildFileTreeAsync(dirNode, dir.FullName);
        }

        // 再添加文件，过滤出只包含 .md 的文件
        foreach (var file in dirInfo.GetFiles("*.md").OrderBy(f => f.Name))
        {
            var fileNode = new KnowledgeFileNode
            {
                Name = file.Name,
                IsDirectory = false,
                FullPath = file.FullName
            };
            parentNode.Children.Add(fileNode);
        }

        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task NewFolderAsync()
    {
        if (string.IsNullOrWhiteSpace(NewFolderName)) return;
        try
        {
            var baseDir = SelectedFile?.IsDirectory == true ? SelectedFile.FullPath : _rootPath;
            var newDirPath = Path.Combine(baseDir, NewFolderName);
            Directory.CreateDirectory(newDirPath);
            
            // 在新文件夹下创建一个 README.md 引导
            if (_fileSystemService != null)
            {
                await _fileSystemService.WriteFileAsync(Path.Combine(newDirPath, "README.md"), $"# {NewFolderName}\nCreated at {DateTime.Now}");
            }

            NewFolderName = string.Empty;
            await RefreshFilesAsync();
        }
        catch (Exception ex) { _logger.Error(ex, "创建文件夹失败"); }
    }

    [RelayCommand]
    private async Task NewFileAsync()
    {
        if (_fileSystemService == null || string.IsNullOrWhiteSpace(NewFileName)) return;
        try
        {
            var baseDir = SelectedFile?.IsDirectory == true ? SelectedFile.FullPath : _rootPath;
            var fileName = NewFileName.Trim();
            // 默认后缀
            if (!Path.HasExtension(fileName)) fileName += ".md";
            
            var fullPath = Path.Combine(baseDir, fileName);
            await _fileSystemService.WriteFileAsync(fullPath, $"# {Path.GetFileNameWithoutExtension(fileName)}\n\nCreated at {DateTime.Now}");
            
            NewFileName = string.Empty;
            await RefreshFilesAsync();
        }
        catch (Exception ex) { _logger.Error(ex, "创建文件失败"); }
    }

    [RelayCommand]
    private async Task DeleteFileAsync()
    {
        if (SelectedFile == null) return;

        var name = SelectedFile.Name;
        var msgTemplate = _localizationService?.GetString("Dialog.ConfirmDeleteFile") ?? "Are you sure you want to delete \"{0}\"? This cannot be undone.";
        var message = string.Format(msgTemplate, name);
        var title = _localizationService?.GetString("Dialog.Title.Warning") ?? "Warning";

        var result = await MessageBox.ShowAsync(
            message: message,
            title: title,
            button: MessageBoxButton.YesNo,
            icon: MessageBoxIcon.Warning);

        if (result != MessageBoxResult.Yes) return;

        try
        {
            if (SelectedFile.IsDirectory)
            {
                Directory.Delete(SelectedFile.FullPath, true);
            }
            else
            {
                await _fileSystemService!.DeleteFileAsync(SelectedFile.FullPath);
            }
            
            EditingFileContent = string.Empty;
            IsEditingFile = false;
            await RefreshFilesAsync();
        }
        catch (Exception ex) { _logger.Error(ex, "删除失败"); }
    }

    [RelayCommand]
    private async Task ViewFileAsync()
    {
        if (SelectedFile == null || SelectedFile.IsDirectory) return;
        await LoadFileContentAsync(SelectedFile.FullPath);
    }

    [RelayCommand]
    private async Task SaveFileAsync()
    {
        if (_fileSystemService == null || SelectedFile == null || SelectedFile.IsDirectory) return;
        try
        {
            await _fileSystemService.WriteFileAsync(SelectedFile.FullPath, EditingFileContent);
            IsEditingFile = false;
            
            // 如果是知识库目录下的文件，触发知识库刷新向量（可选）
            if (_knowledgeBaseService != null && SelectedFile.FullPath.Contains("KnowledgeBase"))
            {
                await _knowledgeBaseService.RefreshVectorCacheAsync();
            }
        }
        catch (Exception ex) { _logger.Error(ex, "保存文件失败"); }
    }

    [RelayCommand]
    private void CancelEdit() { IsEditingFile = false; EditingFileContent = string.Empty; }

    [RelayCommand]
    private void ToggleEditMode()
    {
        if (SelectedFile == null || SelectedFile.IsDirectory) return;
        IsEditingFile = !IsEditingFile;
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        if (_userInteractionService == null || _fileSystemService == null) return;
        var files = await _userInteractionService.PickFilesAsync(
            GetString("Knowledge.ImportPickerTitle", "Select Markdown files to import"),
            "Markdown Files", ["*.md"], true);
        
        if (files.Count == 0) return;
        
        var baseDir = SelectedFile?.IsDirectory == true ? SelectedFile.FullPath : _rootPath;

        foreach (var file in files)
        {
            try 
            { 
                var content = await File.ReadAllTextAsync(file);
                await _fileSystemService.WriteFileAsync(Path.Combine(baseDir, Path.GetFileName(file)), content);
            }
            catch (Exception ex) { _logger.Error(ex, "导入失败: {File}", file); }
        }
        await RefreshFilesAsync();
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (_userInteractionService == null) return;
        var targetPath = await _userInteractionService.PickFolderAsync(GetString("Knowledge.ExportPickerTitle", "Select export folder"));
        if (string.IsNullOrWhiteSpace(targetPath)) return;
        try
        {
            // 简单实现：将 KnowledgeBase 整个复制过去
            CopyDirectory(_rootPath, Path.Combine(targetPath, "KnowledgeBase_Export"));
        }
        catch (Exception ex) { _logger.Error(ex, "导出失败"); }
    }

    private static void CopyDirectory(string sourceDir, string destinationDir)
    {
        var dir = new DirectoryInfo(sourceDir);
        if (!dir.Exists) return;

        Directory.CreateDirectory(destinationDir);

        foreach (FileInfo file in dir.GetFiles("*.md"))
        {
            string targetFilePath = Path.Combine(destinationDir, file.Name);
            file.CopyTo(targetFilePath, true);
        }

        foreach (DirectoryInfo subDir in dir.GetDirectories())
        {
            string newDestinationDir = Path.Combine(destinationDir, subDir.Name);
            CopyDirectory(subDir.FullName, newDestinationDir);
        }
    }

    private string GetString(string key, string fallback)
        => _localizationService?.GetString(key, fallback) ?? fallback;
}
