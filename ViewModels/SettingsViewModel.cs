using Athena.UI.Models;
using Athena.UI.Services;
using Athena.UI.Services.Interfaces;
using Athena.UI.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;

namespace Athena.UI.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private readonly IConfigService? _configService;
    private readonly IChatService? _chatService;
    private readonly ILogService? _logService;
    private readonly ITaskScheduler? _taskScheduler;
    private readonly IKnowledgeBaseService? _knowledgeBaseService;
    private readonly IEmbeddingService? _embeddingService;
    private readonly IConversationHistoryService? _historyService;
    private readonly ILogger _logger = Log.ForContext<SettingsViewModel>();

    /// <summary>
    /// 请求关闭窗口事件
    /// </summary>
    public event EventHandler? RequestClose;

    /// <summary>
    /// 当前选中的 Tab 索引
    /// </summary>
    [ObservableProperty]
    private int _selectedTabIndex;

    /// <summary>
    /// 是否显示Save和Reset按钮
    /// </summary>
    [ObservableProperty]
    private bool _isShowConfigSaveReset = true;

    /// <summary>
    /// 应用配置
    /// </summary>
    [ObservableProperty]
    private AppConfig _config = new();

    /// <summary>
    /// 连接测试状态
    /// </summary>
    [ObservableProperty]
    private string _connectionStatus = string.Empty;

    /// <summary>
    /// 是否正在测试连接
    /// </summary>
    [ObservableProperty]
    private bool _isTestingConnection;

    /// <summary>
    /// 计划任务列表（引用共享服务）
    /// </summary>
    public ObservableCollection<ScheduledTask> ScheduledTasks =>
        _taskScheduler?.Tasks ?? _localTasks;

    /// <summary>
    /// 本地任务集合（设计时使用）
    /// </summary>
    private readonly ObservableCollection<ScheduledTask> _localTasks = new();

    /// <summary>
    /// 知识库文件树
    /// </summary>
    public ObservableCollection<KnowledgeFileNode> KnowledgeFiles { get; }

    #region 日志相关属性
    /// <summary>
    /// 检索日志文本
    /// </summary>
    [ObservableProperty]
    private string _searchLogText = string.Empty;

    /// <summary>
    /// 日志条目列表
    /// </summary>
    public ObservableCollection<LogEntryViewModel> LogEntries { get; }

    /// <summary>
    /// 日志开始时间
    /// </summary>
    [ObservableProperty]
    private DateTime? _logStartTime;

    /// <summary>
    /// 日志结束时间
    /// </summary>
    [ObservableProperty]
    private DateTime? _logEndTime;

    /// <summary>
    /// 当前页码
    /// </summary>
    [ObservableProperty]
    private int _currentPage = 1;

    /// <summary>
    /// 总日志数
    /// </summary>
    [ObservableProperty]
    private int _totalLogCount;

    /// <summary>
    /// 总页数
    /// </summary>
    [ObservableProperty]
    private int _totalPages;

    /// <summary>
    /// 是否有上一页
    /// </summary>
    [ObservableProperty]
    private bool _hasPrevPage;

    /// <summary>
    /// 是否有下一页
    /// </summary>
    [ObservableProperty]
    private bool _hasNextPage;

    /// <summary>
    /// 当前页信息
    /// </summary>
    public string CurrentPageInfo => $"Page {CurrentPage}/{TotalPages}";

    /// <summary>
    /// 日志级别筛选
    /// </summary>
    [ObservableProperty]
    private string _selectedLogLevel = "All";

    public ObservableCollection<string> LogLevels { get; } = new()
    {
        "All", "VERBOSE", "DEBUG", "INFORMATION", "WARNING", "ERROR", "FATAL"
    };

    /// <summary>
    /// 每页显示日志条数
    /// </summary>
    [ObservableProperty]
    private int _selectedLogPageSize = 50;

    public ObservableCollection<int> LogPageSizes { get; } = new()
    {
        20, 50, 100, 200
    };

    #endregion

    /// <summary>
    /// 可用的 AI 提供商
    /// </summary>
    public ObservableCollection<string> Providers { get; } = new()
    {
        "OpenAI", "Azure", "Custom"
    };

    /// <summary>
    /// 主题选项
    /// </summary>
    public ObservableCollection<string> Themes { get; } = new()
    {
        "Dark", "Light"
    };

    /// <summary>
    /// 历史标签页 ViewModel
    /// </summary>
    public HistoryTabViewModel? HistoryTabViewModel { get; private set; }

    /// <summary>
    /// 默认构造函数（用于设计时）
    /// </summary>
    public SettingsViewModel() : this(null, null, null, null, null, null, null)
    {
    }

    partial void OnSelectedTabIndexChanged(int value)
    {
        // CONFIG tab is at index 0
        IsShowConfigSaveReset = value == 0;
    }

    /// <summary>
    /// 依赖注入构造函数
    /// </summary>
    public SettingsViewModel(IConfigService? configService, IChatService? chatService, ILogService? logService, ITaskScheduler? taskScheduler, IKnowledgeBaseService? knowledgeBaseService, IEmbeddingService? embeddingService, IConversationHistoryService? historyService)
    {
        _configService = configService;
        _chatService = chatService;
        _logService = logService;
        _taskScheduler = taskScheduler;
        _knowledgeBaseService = knowledgeBaseService;
        _embeddingService = embeddingService;
        _historyService = historyService;

        KnowledgeFiles = new ObservableCollection<KnowledgeFileNode>();
        LogEntries = new ObservableCollection<LogEntryViewModel>();

        // 初始化历史标签页
        if (_historyService != null)
        {
            HistoryTabViewModel = new HistoryTabViewModel(_historyService);
            HistoryTabViewModel.LoadHistoryRequested += OnLoadHistoryRequested;
        }

        // 加载配置
        LoadConfigAsync().ConfigureAwait(false);

        // 加载真实知识库文件
        LoadKnowledgeFilesAsync().ConfigureAwait(false);

        // 初始化日志时间范围（默认最近7天）
        LogEndTime = DateTime.Today.AddDays(1);
        LogStartTime = DateTime.Today.AddDays(-7);
    }

    private async Task LoadConfigAsync()
    {
        if (_configService != null)
        {
            Config = await _configService.LoadAsync();
        }
    }

    /// <summary>
    /// 加载知识库文件树
    /// </summary>
    private async Task LoadKnowledgeFilesAsync()
    {
        KnowledgeFiles.Clear();

        if (_knowledgeBaseService == null)
        {
            _logger.Warning("知识库服务未初始化，使用示例数据");
            LoadSampleKnowledgeFiles();
            return;
        }

        try
        {
            // 先加载所有目录（确保空目录也显示）
            var directories = await _knowledgeBaseService.ListDirectoriesAsync();
            var files = await _knowledgeBaseService.ListFilesAsync();

            if (directories.Count == 0 && files.Count == 0)
            {
                _logger.Information("知识库为空");
                return;
            }

            // 构建树形结构
            var rootNode = new KnowledgeFileNode { Name = "Knowledge Base", IsDirectory = true, IsExpanded = true, FullPath = "" };
            var directoryNodes = new Dictionary<string, KnowledgeFileNode>();

            // 首先创建所有目录节点
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
                        var dirNode = new KnowledgeFileNode
                        {
                            Name = dirName,
                            IsDirectory = true,
                            IsExpanded = true,
                            FullPath = currentPath
                        };
                        directoryNodes[currentPath] = dirNode;

                        // 添加到父节点
                        if (string.IsNullOrEmpty(parentPath))
                        {
                            rootNode.Children.Add(dirNode);
                        }
                        else if (directoryNodes.TryGetValue(parentPath, out var parentNode))
                        {
                            parentNode.Children.Add(dirNode);
                        }
                    }
                }
            }

            // 然后添加文件节点
            foreach (var filePath in files.OrderBy(f => f))
            {
                var parts = filePath.Split('/');

                // 确保文件的目录存在
                for (int i = 0; i < parts.Length - 1; i++)
                {
                    var dirName = parts[i];
                    var parentPath = i > 0 ? string.Join("/", parts[..i]) : "";
                    var currentPath = string.IsNullOrEmpty(parentPath) ? dirName : $"{parentPath}/{dirName}";

                    if (!directoryNodes.ContainsKey(currentPath))
                    {
                        var dirNode = new KnowledgeFileNode
                        {
                            Name = dirName,
                            IsDirectory = true,
                            IsExpanded = true,
                            FullPath = currentPath
                        };
                        directoryNodes[currentPath] = dirNode;

                        if (string.IsNullOrEmpty(parentPath))
                        {
                            rootNode.Children.Add(dirNode);
                        }
                        else if (directoryNodes.TryGetValue(parentPath, out var parentNode))
                        {
                            parentNode.Children.Add(dirNode);
                        }
                    }
                }

                // 创建文件节点
                var fileName = parts[^1];
                var fileNode = new KnowledgeFileNode
                {
                    Name = fileName,
                    IsDirectory = false,
                    FullPath = filePath
                };

                // 添加到对应目录
                var fileDirPath = string.Join("/", parts[..^1]);
                if (string.IsNullOrEmpty(fileDirPath))
                {
                    rootNode.Children.Add(fileNode);
                }
                else if (directoryNodes.TryGetValue(fileDirPath, out var fileDirNode))
                {
                    fileDirNode.Children.Add(fileNode);
                }
            }

            // 将根节点的子节点添加到集合
            foreach (var child in rootNode.Children)
            {
                KnowledgeFiles.Add(child);
            }

            _logger.Information("加载知识库完成，共 {Dirs} 个目录，{Files} 个文件", directories.Count, files.Count);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "加载知识库文件失败");
            LoadSampleKnowledgeFiles();
        }
    }

    private void LoadSampleKnowledgeFiles()
    {
        KnowledgeFiles.Add(new KnowledgeFileNode { Name = "Characters", IsDirectory = true, IsExpanded = true });
        KnowledgeFiles.Add(new KnowledgeFileNode { Name = "user_profile.md", IsDirectory = false, FullPath = "Characters/user_profile.md" });
        KnowledgeFiles.Add(new KnowledgeFileNode { Name = "Memories", IsDirectory = true, IsExpanded = false });
        KnowledgeFiles.Add(new KnowledgeFileNode { Name = "important_events.md", IsDirectory = false, FullPath = "Memories/important_events.md" });
    }

    #region 知识库管理属性

    /// <summary>
    /// 选中的知识库文件节点
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedFileDisplayPath))]
    private KnowledgeFileNode? _selectedKnowledgeFile;

    /// <summary>
    /// 选中文件的显示路径（用于 UI 绑定，避免 null 错误）
    /// </summary>
    public string SelectedFileDisplayPath => SelectedKnowledgeFile?.FullPath ?? "Select a file to view";

    /// <summary>
    /// 当选中文件变化时自动加载内容
    /// </summary>
    partial void OnSelectedKnowledgeFileChanged(KnowledgeFileNode? value)
    {
        if (value == null || value.IsDirectory)
        {
            EditingFileContent = string.Empty;
            IsEditingFile = false;
            return;
        }

        // 自动加载文件内容
        _ = LoadFileContentAsync(value.FullPath);
    }

    /// <summary>
    /// 异步加载文件内容
    /// </summary>
    private async Task LoadFileContentAsync(string filePath)
    {
        if (_knowledgeBaseService == null) return;

        try
        {
            var content = await _knowledgeBaseService.ReadFileAsync(filePath);
            EditingFileContent = content ?? "文件内容为空";
            IsEditingFile = true;
            _logger.Debug("自动加载文件: {File}", filePath);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "加载文件失败: {File}", filePath);
            EditingFileContent = $"加载失败: {ex.Message}";
        }
    }

    /// <summary>
    /// 当前编辑的文件内容
    /// </summary>
    [ObservableProperty]
    private string _editingFileContent = string.Empty;

    /// <summary>
    /// 是否正在编辑文件
    /// </summary>
    [ObservableProperty]
    private bool _isEditingFile;

    /// <summary>
    /// 新建文件夹对话框输入
    /// </summary>
    [ObservableProperty]
    private string _newFolderName = string.Empty;

    /// <summary>
    /// 新建文件对话框输入
    /// </summary>
    [ObservableProperty]
    private string _newFileName = string.Empty;

    /// <summary>
    /// 当前上下文 Token 数量
    /// </summary>
    [ObservableProperty]
    private int _currentContextTokens;

    #endregion

    #region 日志命令

    /// <summary>
    /// 搜索日志命令
    /// 当有输入关键词时，在已过滤的日志中搜索匹配的条目
    /// 当输入框为空时，等同于刷新
    /// </summary>
    [RelayCommand]
    private async Task SearchLogsAsync()
    {
        CurrentPage = 1;
        // 如果搜索关键词为空或只有空格，等同于刷新
        if (string.IsNullOrWhiteSpace(SearchLogText))
        {
            await LoadLogsAsync();
        }
        else
        {
            await LoadLogsAsync(SearchLogText.Trim());
        }
    }

    /// <summary>
    /// 刷新日志命令
    /// </summary>
    [RelayCommand]
    private async Task RefreshLogsAsync()
    {
        await LoadLogsAsync();
    }

    /// <summary>
    /// 上一页命令
    /// </summary>
    [RelayCommand]
    private async Task PrevPageAsync()
    {
        if (CurrentPage > 1)
        {
            CurrentPage--;
            await LoadLogsAsync();
        }
    }

    /// <summary>
    /// 下一页命令
    /// </summary>
    [RelayCommand]
    private async Task NextPageAsync()
    {
        if (CurrentPage < TotalPages)
        {
            CurrentPage++;
            await LoadLogsAsync();
        }
    }

    /// <summary>
    /// 清除日志命令
    /// </summary>
    [RelayCommand]
    private async Task ClearLogsAsync()
    {
        if (_logService != null)
        {
            await _logService.ClearAllLogsAsync();
            LogEntries.Clear();
            TotalLogCount = 0;
            TotalPages = 0;
            HasPrevPage = false;
            HasNextPage = false;
            OnPropertyChanged(nameof(CurrentPageInfo));
        }
    }

    /// <summary>
    /// 导出日志命令
    /// </summary>
    [RelayCommand]
    private async Task ExportLogsAsync()
    {
        if (_logService != null)
        {
            var filePath = await _logService.ExportLogsAsync(LogStartTime, LogEndTime);
            // TODO: 显示导出成功提示
        }
    }

    /// <summary>
    /// 加载日志
    /// </summary>
    /// <param name="searchKeyword">搜索关键词（可选）</param>
    private async Task LoadLogsAsync(string? searchKeyword = null)
    {
        if (_logService == null) return;

        var queryParams = new LogQueryParams
        {
            StartTime = LogStartTime,
            EndTime = LogEndTime,
            Level = SelectedLogLevel,
            Page = CurrentPage,
            PageSize = SelectedLogPageSize,
            SearchKeyword = searchKeyword
        };

        var result = await _logService.QueryLogsAsync(queryParams);

        LogEntries.Clear();
        foreach (var entry in result.Entries)
        {
            LogEntries.Add(new LogEntryViewModel(entry));
        }

        TotalLogCount = result.TotalCount;
        TotalPages = result.TotalPages;
        HasPrevPage = CurrentPage > 1;
        HasNextPage = CurrentPage < TotalPages;
        OnPropertyChanged(nameof(CurrentPageInfo));
    }

    #endregion

    #region AI 配置命令

    /// <summary>
    /// 测试 API 连接命令
    /// </summary>
    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        if (_chatService == null)
        {
            ConnectionStatus = "服务未初始化";
            return;
        }

        if (string.IsNullOrWhiteSpace(Config.ApiKey))
        {
            ConnectionStatus = "请先输入 API Key";
            return;
        }

        IsTestingConnection = true;
        ConnectionStatus = "测试中...";

        try
        {
            // 临时更新配置以测试
            _chatService.UpdateConfig(Config);
            var (success, message) = await _chatService.TestConnectionAsync();
            ConnectionStatus = message.TrimEnd().Replace("\n"," ");
        }
        finally
        {
            IsTestingConnection = false;
        }
    }

    /// <summary>
    /// 保存配置命令
    /// </summary>
    [RelayCommand]
    private async Task SaveConfigAsync()
    {
        if (_configService != null)
        {
            await _configService.SaveAsync(Config);

            // 同步更新 ChatService 配置
            _chatService?.UpdateConfig(Config);

            // 同步更新 EmbeddingService 配置
            if (_embeddingService is OpenAIEmbeddingService embeddingService)
            {
                embeddingService.UpdateConfig(Config);
            }
        }

        // 请求关闭窗口
        RequestClose?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 重置配置命令
    /// </summary>
    [RelayCommand]
    private async Task ResetConfigAsync()
    {
        Config = new AppConfig();
        if (_configService != null)
        {
            await _configService.SaveAsync(Config);
        }
    }

    #endregion

    #region 其他命令

    /// <summary>
    /// 创建新任务命令
    /// </summary>
    [RelayCommand]
    private async Task CreateTaskAsync()
    {
        var viewModel = new CreateTaskDialogViewModel();
        var dialog = new CreateTaskDialog(viewModel);

        // 获取当前活动的窗口作为 Owner
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 找到当前活动的窗口
            var activeWindow = desktop.Windows.FirstOrDefault(w => w.IsActive)
                              ?? desktop.MainWindow;

            if (activeWindow != null)
            {
                await dialog.ShowDialog(activeWindow);
            }
            else
            {
                dialog.Show();
            }
        }
        else
        {
            dialog.Show();
        }

        // 如果确认创建，通过服务添加任务
        if (viewModel.IsConfirmed && viewModel.Result != null)
        {
            if (_taskScheduler != null)
            {
                await _taskScheduler.ScheduleAsync(viewModel.Result);
            }
            else
            {
                // 设计时回退
                _localTasks.Add(viewModel.Result);
            }
        }
    }

    /// <summary>
    /// 清除所有任务命令
    /// </summary>
    [RelayCommand]
    private async Task ClearAllTasksAsync()
    {
        if (_taskScheduler != null)
        {
            await _taskScheduler.ClearAllAsync();
        }
        else
        {
            _localTasks.Clear();
        }
    }

    /// <summary>
    /// 创建知识库文件夹命令
    /// </summary>
    [RelayCommand]
    private async Task NewFolderAsync()
    {
        if (_knowledgeBaseService == null || string.IsNullOrWhiteSpace(NewFolderName))
        {
            _logger.Warning("无法创建文件夹：服务未初始化或名称为空");
            return;
        }

        try
        {
            // 在知识库中创建一个 README.md 文件来确保目录存在
            var folderPath = NewFolderName.Trim('/');
            var readmePath = $"{folderPath}/README.md";
            await _knowledgeBaseService.CreateFileAsync(readmePath, $"# {folderPath}\n\n此文件夹用于存储相关知识。\n");

            _logger.Information("创建文件夹: {Folder}", folderPath);
            NewFolderName = string.Empty;

            // 刷新文件树
            await LoadKnowledgeFilesAsync();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "创建文件夹失败");
        }
    }

    /// <summary>
    /// 创建知识库文件命令
    /// </summary>
    [RelayCommand]
    private async Task NewFileAsync()
    {
        if (_knowledgeBaseService == null || string.IsNullOrWhiteSpace(NewFileName))
        {
            _logger.Warning("无法创建文件：服务未初始化或名称为空");
            return;
        }

        try
        {
            var fileName = NewFileName.Trim();
            if (!fileName.EndsWith(".md"))
            {
                fileName += ".md";
            }

            // 如果有选中的目录，在其中创建
            var filePath = fileName;
            if (SelectedKnowledgeFile != null && SelectedKnowledgeFile.IsDirectory)
            {
                filePath = $"{SelectedKnowledgeFile.FullPath}/{fileName}";
            }

            var content = $"# {Path.GetFileNameWithoutExtension(fileName)}\n\n";
            await _knowledgeBaseService.CreateFileAsync(filePath, content);

            _logger.Information("创建文件: {File}", filePath);
            NewFileName = string.Empty;

            // 刷新文件树
            await LoadKnowledgeFilesAsync();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "创建文件失败");
        }
    }

    /// <summary>
    /// 删除选中的知识库文件或目录命令
    /// </summary>
    [RelayCommand]
    private async Task DeleteFileAsync()
    {
        if (_knowledgeBaseService == null || SelectedKnowledgeFile == null)
        {
            return;
        }

        try
        {
            if (SelectedKnowledgeFile.IsDirectory)
            {
                // 删除目录及其所有内容
                await _knowledgeBaseService.DeleteDirectoryAsync(SelectedKnowledgeFile.FullPath);
                _logger.Information("删除目录: {Directory}", SelectedKnowledgeFile.FullPath);
            }
            else
            {
                await _knowledgeBaseService.DeleteFileAsync(SelectedKnowledgeFile.FullPath);
                _logger.Information("删除文件: {File}", SelectedKnowledgeFile.FullPath);
            }

            // 清空编辑状态
            EditingFileContent = string.Empty;
            IsEditingFile = false;

            // 刷新文件树
            await LoadKnowledgeFilesAsync();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "删除失败: {Path}", SelectedKnowledgeFile.FullPath);
        }
    }

    /// <summary>
    /// 查看知识库文件内容命令
    /// </summary>
    [RelayCommand]
    private async Task ViewFileAsync()
    {
        if (_knowledgeBaseService == null || SelectedKnowledgeFile == null || SelectedKnowledgeFile.IsDirectory)
        {
            return;
        }

        try
        {
            var content = await _knowledgeBaseService.ReadFileAsync(SelectedKnowledgeFile.FullPath);
            EditingFileContent = content ?? "文件内容为空";
            IsEditingFile = true;
            _logger.Debug("查看文件: {File}", SelectedKnowledgeFile.FullPath);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "读取文件失败");
            EditingFileContent = $"读取失败: {ex.Message}";
        }
    }

    /// <summary>
    /// 保存编辑的文件内容命令
    /// </summary>
    [RelayCommand]
    private async Task SaveFileAsync()
    {
        if (_knowledgeBaseService == null || SelectedKnowledgeFile == null || string.IsNullOrEmpty(EditingFileContent))
        {
            return;
        }

        try
        {
            await _knowledgeBaseService.ReplaceFileAsync(SelectedKnowledgeFile.FullPath, EditingFileContent);
            _logger.Information("保存文件: {File}", SelectedKnowledgeFile.FullPath);
            IsEditingFile = false;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "保存文件失败");
        }
    }

    /// <summary>
    /// 取消编辑文件命令
    /// </summary>
    [RelayCommand]
    private void CancelEdit()
    {
        IsEditingFile = false;
        EditingFileContent = string.Empty;
    }

    /// <summary>
    /// 刷新知识库文件树命令
    /// </summary>
    [RelayCommand]
    private async Task RefreshKnowledgeBaseAsync()
    {
        await LoadKnowledgeFilesAsync();
    }

    /// <summary>
    /// 导入知识库命令
    /// </summary>
    [RelayCommand]
    private async Task ImportAsync()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;

        var storageProvider = desktop.MainWindow?.StorageProvider;
        if (storageProvider == null)
            return;

        var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择要导入的 Markdown 文件",
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Markdown Files") { Patterns = new[] { "*.md" } },
                new FilePickerFileType("All Files") { Patterns = new[] { "*.*" } }
            }
        });

        if (files.Count == 0 || _knowledgeBaseService == null)
            return;

        var importedCount = 0;
        foreach (var file in files)
        {
            try
            {
                var fileName = file.Name;
                var content = await File.ReadAllTextAsync(file.Path.LocalPath);
                await _knowledgeBaseService.CreateFileAsync(fileName, content);
                importedCount++;
                _logger.Information("导入文件: {File}", fileName);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "导入文件失败: {File}", file.Name);
            }
        }

        // 刷新文件树
        await LoadKnowledgeFilesAsync();
        _logger.Information("导入完成，共导入 {Count} 个文件", importedCount);
    }

    /// <summary>
    /// 导出知识库命令
    /// </summary>
    [RelayCommand]
    private async Task ExportAsync()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;

        var storageProvider = desktop.MainWindow?.StorageProvider;
        if (storageProvider == null || _knowledgeBaseService == null)
            return;

        var folder = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择导出目录",
            AllowMultiple = false
        });

        if (folder.Count == 0)
            return;

        var targetPath = folder[0].Path.LocalPath;

        try
        {
            var files = await _knowledgeBaseService.ListFilesAsync();
            var exportedCount = 0;

            foreach (var relativePath in files)
            {
                try
                {
                    var content = await _knowledgeBaseService.ReadFileAsync(relativePath);
                    if (content == null) continue;

                    // 创建目标文件路径
                    var targetFilePath = Path.Combine(targetPath, relativePath);
                    var targetDir = Path.GetDirectoryName(targetFilePath);

                    if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
                    {
                        Directory.CreateDirectory(targetDir);
                    }

                    await File.WriteAllTextAsync(targetFilePath, content);
                    exportedCount++;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "导出文件失败: {File}", relativePath);
                }
            }

            _logger.Information("导出完成，共导出 {Count} 个文件到 {Path}", exportedCount, targetPath);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "导出知识库失败");
        }
    }

    /// <summary>
    /// 压缩上下文命令
    /// </summary>
    [RelayCommand]
    private async Task CompressContextAsync()
    {
        // 触发压缩事件，由 MainWindowViewModel 处理
        CompressContextRequested?.Invoke(this, EventArgs.Empty);
        await Task.CompletedTask;
    }

    /// <summary>
    /// 清除上下文命令
    /// </summary>
    [RelayCommand]
    private void ClearContext()
    {
        // 触发清除事件，由 MainWindowViewModel 处理
        ClearContextRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 请求压缩上下文事件
    /// </summary>
    public event EventHandler? CompressContextRequested;

    /// <summary>
    /// 请求清除上下文事件
    /// </summary>
    public event EventHandler? ClearContextRequested;

    /// <summary>
    /// 请求加载历史对话事件
    /// </summary>
    public event EventHandler<ConversationHistoryItem>? LoadHistoryRequested;

    /// <summary>
    /// 处理历史标签页的加载请求
    /// </summary>
    private void OnLoadHistoryRequested(object? sender, ConversationHistoryItem item)
    {
        LoadHistoryRequested?.Invoke(this, item);
    }

    /// <summary>
    /// 检查更新命令
    /// </summary>
    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        // TODO: 实现检查更新
        await Task.CompletedTask;
    }

    /// <summary>
    /// 打开文档命令
    /// </summary>
    [RelayCommand]
    private void OpenDocumentation()
    {
        // TODO: 打开文档链接
    }

    /// <summary>
    /// 打开 GitHub 命令
    /// </summary>
    [RelayCommand]
    private void OpenGitHub()
    {
        // TODO: 打开 GitHub 链接
    }

    #endregion
}

/// <summary>
/// 日志条目 ViewModel（用于显示）
/// </summary>
public class LogEntryViewModel
{
    private readonly LogEntry _entry;

    public LogEntryViewModel(LogEntry entry)
    {
        _entry = entry;
    }

    public DateTime Timestamp => _entry.Timestamp;
    public string Level => _entry.Level;
    public string Message => _entry.Message;
    public string? Exception => _entry.Exception;

    /// <summary>
    /// 根据日志级别返回颜色
    /// </summary>
    public IBrush LevelColor => _entry.Level.ToUpper() switch
    {
        "VERBOSE" => Brushes.Gray,
        "DEBUG" => Brushes.Gray,
        "INFORMATION" => Brushes.Green,
        "WARNING" => Brushes.Orange,
        "ERROR" => Brushes.Red,
        "FATAL" => Brushes.Red,
        _ => Brushes.White
    };
}
