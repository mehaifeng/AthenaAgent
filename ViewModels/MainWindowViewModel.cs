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
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly IChatService? _chatService;
    private readonly IConfigService? _configService;
    private readonly ITaskScheduler? _taskScheduler;
    private readonly IConversationHistoryService? _historyService;
    private readonly IPromptService? _promptService;
    private readonly ILogService? _logService;
    private readonly IKnowledgeBaseService? _knowledgeBaseService;
    private readonly IEmbeddingService? _embeddingService;
    private CancellationTokenSource? _cancellationTokenSource;
    private readonly ILogger _logger = Log.ForContext<MainWindowViewModel>();

    #region Chat Properties

    /// <summary>
    /// 聊天消息列表
    /// </summary>
    public ObservableCollection<ChatMessage> Messages { get; }

    /// <summary>
    /// 对话上下文
    /// </summary>
    public ConversationContext ConversationContext { get; }

    /// <summary>
    /// 计划任务列表（引用共享服务）
    /// </summary>
    public ObservableCollection<ScheduledTask> ScheduledTasks =>
        _taskScheduler?.Tasks ?? _localScheduledTasks;

    /// <summary>
    /// 本地任务集合（设计时使用）
    /// </summary>
    private readonly ObservableCollection<ScheduledTask> _localScheduledTasks = new();

    /// <summary>
    /// 输入文本
    /// </summary>
    [ObservableProperty]
    private string _inputText = string.Empty;

    /// <summary>
    /// 是否显示心跳按钮
    /// </summary>
    [ObservableProperty]
    private bool _showHeartbeatButton = true;

    /// <summary>
    /// 是否正在发送消息
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartInlineEditCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteMessageCommand))]
    [NotifyCanExecuteChangedFor(nameof(RegenerateResponseCommand))]
    private bool _isSending;

    /// <summary>
    /// 判断是否可以编辑或删除消息
    /// </summary>
    private bool CanEditOrDelete() => !IsSending;

    /// <summary>
    /// 待触发任务数量
    /// </summary>
    [ObservableProperty]
    private int _pendingTaskCount;

    /// <summary>
    /// 当前主题
    /// </summary>
    [ObservableProperty]
    private string _currentTheme = "Dark";

    /// <summary>
    /// 用户是否正在滚动查看历史消息
    /// </summary>
    [ObservableProperty]
    private bool _isUserScrolling;

    /// <summary>
    /// 当前对话 ID
    /// </summary>
    [ObservableProperty]
    private string _currentConversationId = string.Empty;

    /// <summary>
    /// 加载的消息哈希值，用于检测是否有修改
    /// </summary>
    private string _loadedMessagesHash = string.Empty;

    /// <summary>
    /// 发送按钮文本
    /// </summary>
    public string SendButtonText => "[SEND]";

    /// <summary>
    /// 当前上下文使用的 tokens 数量
    /// </summary>
    [ObservableProperty]
    private int _contextTokens;

    /// <summary>
    /// 上下文 tokens 阈值
    /// </summary>
    [ObservableProperty]
    private int _contextTokensThreshold = 4000;

    /// <summary>
    /// 上下文 tokens 使用率文本
    /// </summary>
    public string ContextTokensInfo => $"{ContextTokens} / {ContextTokensThreshold} tokens";

    /// <summary>
    /// 是否接近压缩阈值（超过 80%）
    /// </summary>
    public bool IsNearCompressionThreshold => ContextTokens > ContextTokensThreshold * 0.8;

    /// <summary>
    /// 压缩预览文本
    /// </summary>
    public string CompressionPreview
    {
        get
        {
            var allMessages = ConversationContext.Messages.ToList();
            if (allMessages.Count == 0)
                return "No messages in context.";

            var threshold = ContextTokensThreshold;
            var keepCount = ConversationContext.CalculateKeepCount(threshold);
            var compressCount = allMessages.Count - keepCount;

            if (compressCount <= 0)
                return $"All {allMessages.Count} messages will be retained.\nNo compression needed.";

            var tokensToCompress = 0;
            for (int i = 0; i < compressCount; i++)
            {
                tokensToCompress += ConversationContext.EstimateTokens(allMessages[i].Content);
            }

            return $"Will compress: {compressCount} messages (~{tokensToCompress} tokens)\n" +
                   $"Will retain: {keepCount} recent messages";
        }
    }

    #endregion

    #region Tab Navigation Properties

    /// <summary>
    /// 当前选中的 Tab 索引
    /// </summary>
    [ObservableProperty]
    private int _selectedTabIndex;

    /// <summary>
    /// 是否显示Save和Reset按钮（仅在CONFIG页显示）
    /// </summary>
    [ObservableProperty]
    private bool _isShowConfigSaveReset;

    partial void OnSelectedTabIndexChanged(int value)
    {
        // CONFIG tab is at index 1
        IsShowConfigSaveReset = value == 1;
    }

    #endregion

    #region Config Properties

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

    #endregion

    #region History Properties

    /// <summary>
    /// 历史标签页 ViewModel
    /// </summary>
    public HistoryTabViewModel? HistoryTabViewModel { get; private set; }

    #endregion

    #region Knowledge Base Properties

    /// <summary>
    /// 知识库文件树
    /// </summary>
    public ObservableCollection<KnowledgeFileNode> KnowledgeFiles { get; }

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

    #endregion

    #region Log Properties

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
    /// 默认构造函数（用于设计时）
    /// </summary>
    public MainWindowViewModel() : this(null, null, null, null, null, null, null, null)
    {
    }

    /// <summary>
    /// 依赖注入构造函数
    /// </summary>
    public MainWindowViewModel(
        IChatService? chatService,
        IConfigService? configService,
        ITaskScheduler? taskScheduler,
        IConversationHistoryService? historyService,
        IPromptService? promptService,
        ILogService? logService,
        IKnowledgeBaseService? knowledgeBaseService,
        IEmbeddingService? embeddingService)
    {
        _chatService = chatService;
        _configService = configService;
        _taskScheduler = taskScheduler;
        _historyService = historyService;
        _promptService = promptService;
        _logService = logService;
        _knowledgeBaseService = knowledgeBaseService;
        _embeddingService = embeddingService;

        Messages = new ObservableCollection<ChatMessage>();
        ConversationContext = new ConversationContext();
        KnowledgeFiles = new ObservableCollection<KnowledgeFileNode>();
        LogEntries = new ObservableCollection<LogEntryViewModel>();

        // 订阅任务触发事件
        if (_taskScheduler != null)
        {
            _taskScheduler.ProactiveMessageTriggered += OnProactiveMessageTriggered;
            Log.Information("已订阅任务触发事件");
        }

        // 初始化历史标签页
        if (_historyService != null)
        {
            HistoryTabViewModel = new HistoryTabViewModel(_historyService);
            HistoryTabViewModel.LoadHistoryRequested += OnLoadHistoryRequested;
        }

        // 加载配置
        LoadSettingsAsync().ConfigureAwait(false);

        // 加载最新的历史记录
        LoadLatestHistoryAsync().ConfigureAwait(false);

        // 加载知识库文件
        LoadKnowledgeFilesAsync().ConfigureAwait(false);

        // 初始化日志时间范围（默认最近7天）
        LogEndTime = DateTime.Today.AddDays(1);
        LogStartTime = DateTime.Today.AddDays(-7);

        Log.Information("MainWindowViewModel 初始化完成");
    }

    #region Chat Commands

    /// <summary>
    /// 发送消息命令
    /// </summary>
    [RelayCommand]
    private async Task SendMessageAsync()
    {
        if (string.IsNullOrWhiteSpace(InputText) || IsSending)
            return;

        if (_chatService == null)
        {
            AddErrorMessage("聊天服务未初始化，请检查配置。");
            Log.Warning("尝试发送消息但聊天服务未初始化");
            return;
        }

        var userMessageContent = InputText.Trim();
        InputText = string.Empty;
        IsSending = true;
        IsUserScrolling = false; // 发送新消息时重置滚动状态

        Log.Information("用户发送消息: {Message}", userMessageContent);

        // 添加用户消息
        var userMessage = new ChatMessage
        {
            Role = "user",
            Content = userMessageContent,
            Timestamp = DateTime.Now
        };
        Messages.Add(userMessage);

        // 检查是否需要自动压缩上下文
        if (_configService != null)
        {
            var config = await _configService.LoadAsync();
            if (config.AutoCompress && ConversationContext.NeedsCompression(config.CompressionThreshold))
            {
                Log.Information("自动压缩上下文触发");
                await CompressContextAsync();
            }
        }

        // 构建带时间戳的消息用于发送给 AI（UI 显示原始内容）
        var enrichedContent = $"""
            [当前时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss dddd}]
            {userMessageContent}
            """;

        await GetAiResponseAsync(enrichedContent);
    }

    /// <summary>
    /// 获取 AI 响应
    /// </summary>
    private async Task GetAiResponseAsync(string userMessageContent)
    {
        // 添加加载中的 AI 消息
        var aiMessage = new ChatMessage
        {
            Role = "assistant",
            Content = string.Empty,
            Timestamp = DateTime.Now,
            IsLoading = true
        };
        Messages.Add(aiMessage);

        _cancellationTokenSource = new CancellationTokenSource();

        try
        {
            var responseLength = 0;

            // 流式接收响应
            await foreach (var chunk in _chatService!.StreamMessageAsync(
                userMessageContent,
                ConversationContext,
                _cancellationTokenSource.Token))
            {
                aiMessage.Content += chunk;
                responseLength += chunk.Length;
            }

            aiMessage.IsLoading = false;

            Log.Information("AI 响应完成，响应长度: {Length} 字符", responseLength);
        }
        catch (OperationCanceledException)
        {
            aiMessage.Content += "\n[已取消]";
            aiMessage.IsLoading = false;
            Log.Information("用户取消了消息发送");
        }
        catch (Exception ex)
        {
            aiMessage.Content = $"发生错误: {ex.Message}";
            aiMessage.IsLoading = false;
            Log.Error(ex, "发送消息时发生错误");
        }
        finally
        {
            IsSending = false;
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;

            // 更新 tokens 显示
            UpdateContextTokensDisplay();
        }
    }

    /// <summary>
    /// 取消发送命令
    /// </summary>
    [RelayCommand]
    private void CancelSend()
    {
        _cancellationTokenSource?.Cancel();
        Log.Information("用户请求取消发送");
    }

    /// <summary>
    /// 切换主题命令
    /// </summary>
    [RelayCommand]
    private void ToggleTheme()
    {
        CurrentTheme = CurrentTheme == "Dark" ? "Light" : "Dark";
        Log.Information("主题切换为: {Theme}", CurrentTheme);
        // TODO: 实际切换主题逻辑
    }

    /// <summary>
    /// 清空对话命令
    /// </summary>
    [RelayCommand]
    private void ClearChat()
    {
        var messageCount = Messages.Count;
        Messages.Clear();
        ConversationContext.Clear();
        AddSystemMessage("对话已清空。");
        Log.Information("对话已清空，清除 {Count} 条消息", messageCount);
    }

    /// <summary>
    /// 附件文件命令
    /// </summary>
    [RelayCommand]
    private async Task AttachFileAsync()
    {
        // TODO: 实现文件选择
        await Task.CompletedTask;
    }

    /// <summary>
    /// 开始内联编辑消息
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanEditOrDelete))]
    private void StartInlineEdit(ChatMessage? message)
    {
        if (message == null)
            return;

        // 取消其他消息的编辑状态
        foreach (var msg in Messages)
        {
            if (msg.IsEditing && msg != message)
            {
                msg.IsEditing = false;
                msg.EditContent = string.Empty;
            }
        }

        message.EditContent = message.Content;
        message.IsEditing = true;
        Log.Information("开始内联编辑消息: {Content}", message.Content);
    }

    /// <summary>
    /// 确认内联编辑
    /// </summary>
    [RelayCommand]
    private void ConfirmInlineEdit(ChatMessage? message)
    {
        if (message == null || !message.IsEditing)
            return;

        var oldContent = message.Content;
        var newContent = message.EditContent.Trim();

        if (string.IsNullOrWhiteSpace(newContent))
        {
            message.IsEditing = false;
            message.EditContent = string.Empty;
            return;
        }

        message.Content = newContent;
        message.IsEditing = false;
        message.EditContent = string.Empty;

        Log.Information("确认编辑消息: {OldContent} -> {NewContent}", oldContent, newContent);

        // 更新对话上下文
        UpdateConversationContext();
    }

    /// <summary>
    /// 取消内联编辑
    /// </summary>
    [RelayCommand]
    private void CancelInlineEdit(ChatMessage? message)
    {
        if (message == null)
            return;

        message.IsEditing = false;
        message.EditContent = string.Empty;
        Log.Information("取消内联编辑");
    }

    /// <summary>
    /// 删除消息命令
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanEditOrDelete))]
    private void DeleteMessage(ChatMessage? message)
    {
        if (message == null)
            return;

        var index = Messages.IndexOf(message);
        if (index < 0)
            return;

        Messages.RemoveAt(index);
        Log.Information("删除消息: {Content}", message.Content);

        // 更新对话上下文
        UpdateConversationContext();
    }

    /// <summary>
    /// 重新生成 AI 回复
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanEditOrDelete))]
    private async Task RegenerateResponseAsync(ChatMessage? message)
    {
        if (message == null || message.Role != "assistant" || _chatService == null)
            return;

        // 找到该 AI 消息对应的用户消息（向前查找最近一条用户消息）
        var index = Messages.IndexOf(message);
        if (index <= 0)
            return;

        string? userContent = null;
        for (int i = index - 1; i >= 0; i--)
        {
            if (Messages[i].Role == "user")
            {
                userContent = Messages[i].Content;
                break;
            }
        }

        if (string.IsNullOrEmpty(userContent))
            return;

        Log.Information("重新生成回复，基于用户消息: {Content}", userContent);

        // 清空当前 AI 消息内容，设置为加载状态
        message.Content = string.Empty;
        message.IsLoading = true;

        IsSending = true;
        _cancellationTokenSource = new CancellationTokenSource();

        // 重建上下文（只包含该消息之前的对话）
        var tempContext = new ConversationContext();
        for (int i = 0; i < index; i++)
        {
            var msg = Messages[i];
            if (msg.Role == "user")
                tempContext.AddUserMessage(msg.Content);
            else if (msg.Role == "assistant")
                tempContext.AddAssistantMessage(msg.Content);
        }

        try
        {
            await foreach (var chunk in _chatService.StreamMessageAsync(
                userContent, tempContext, _cancellationTokenSource.Token))
            {
                message.Content += chunk;
            }

            message.IsLoading = false;
            Log.Information("重新生成回复完成");
        }
        catch (OperationCanceledException)
        {
            message.Content += "\n[已取消]";
            message.IsLoading = false;
        }
        catch (Exception ex)
        {
            message.Content = $"重新生成失败: {ex.Message}";
            message.IsLoading = false;
            Log.Error(ex, "重新生成回复时发生错误");
        }
        finally
        {
            IsSending = false;
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
            UpdateConversationContext();
        }
    }

    /// <summary>
    /// 新建对话命令
    /// </summary>
    [RelayCommand]
    private async Task NewConversationAsync()
    {
        // 如果当前有消息，先保存
        if (Messages.Count > 0 && _historyService != null)
        {
            await SaveCurrentConversationAsync();
        }

        // 清空当前对话
        Messages.Clear();
        ConversationContext.Clear();
        CurrentConversationId = string.Empty;

        // 添加欢迎消息
        AddSystemMessage("新对话已开始。请问有什么可以帮助您的？");
        Log.Information("新建对话");
    }

    #endregion

    #region Tab Navigation Commands

    /// <summary>
    /// 切换到任务标签页命令
    /// </summary>
    [RelayCommand]
    private void SwitchToTasksTab()
    {
        SelectedTabIndex = 2; // TASKS tab index
    }

    #endregion

    #region Config Commands

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
            ConnectionStatus = message.TrimEnd().Replace("\n", " ");
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

            // 更新历史服务的次级模型配置
            if (_historyService is ConversationHistoryService historyService)
            {
                historyService.UpdateSecondaryConfig(Config);
            }
        }

        // 切换回聊天标签页
        SelectedTabIndex = 0;
        Log.Information("配置已保存");
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
        Log.Information("配置已重置");
    }

    #endregion

    #region Task Commands

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
            var activeWindow = desktop.MainWindow;

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
                _localScheduledTasks.Add(viewModel.Result);
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
            _localScheduledTasks.Clear();
        }
    }

    #endregion

    #region Knowledge Base Commands

    /// <summary>
    /// 刷新知识库文件树命令
    /// </summary>
    [RelayCommand]
    private async Task RefreshKnowledgeBaseAsync()
    {
        await LoadKnowledgeFilesAsync();
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

    #endregion

    #region Log Commands

    /// <summary>
    /// 搜索日志命令
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

    #region Context Commands

    /// <summary>
    /// 压缩对话上下文
    /// </summary>
    [RelayCommand]
    private async Task CompressContextAsync()
    {
        if (_historyService == null)
        {
            Log.Warning("无法压缩上下文：对话历史服务未初始化");
            return;
        }

        try
        {
            // 获取压缩阈值配置
            var config = _configService != null
                ? await _configService.LoadAsync()
                : null;
            var threshold = config?.CompressionThreshold ?? 4000;

            var allMessages = ConversationContext.Messages.ToList();

            // 根据阈值动态计算需要保留的消息数量
            var keepRecentCount = ConversationContext.CalculateKeepCount(threshold);

            if (allMessages.Count <= keepRecentCount)
            {
                Log.Information("上下文消息数不足，无需压缩");
                return;
            }

            var recentMessages = allMessages.TakeLast(keepRecentCount).ToList();
            var tokensBefore = ConversationContext.EstimatedTokenCount;

            // 转换为 ChatMessage 列表用于压缩
            var chatMessages = allMessages.Select(m => new ChatMessage
            {
                Role = m.Role,
                Content = m.Content
            }).ToList();

            // 使用次级模型生成摘要
            var summary = await _historyService.CompressContextAsync(chatMessages, keepRecentCount);

            // 重建上下文
            ConversationContext.Clear();
            if (!string.IsNullOrEmpty(summary))
            {
                ConversationContext.AddSystemMessage(summary);
            }

            foreach (var msg in recentMessages)
            {
                if (msg.Role == "user")
                    ConversationContext.AddUserMessage(msg.Content);
                else if (msg.Role == "assistant")
                    ConversationContext.AddAssistantMessage(msg.Content);
            }

            var tokensAfter = ConversationContext.EstimatedTokenCount;
            Log.Information("上下文压缩完成：{OldMessages} 条消息 → {NewMessages} 条，{OldTokens} tokens → {NewTokens} tokens",
                allMessages.Count, keepRecentCount + (string.IsNullOrEmpty(summary) ? 0 : 1),
                tokensBefore, tokensAfter);

            // 更新 tokens 显示
            UpdateContextTokensDisplay();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "压缩上下文失败");
        }
    }

    /// <summary>
    /// 清除上下文命令
    /// </summary>
    [RelayCommand]
    private void ClearContext()
    {
        var messageCount = Messages.Count;
        Messages.Clear();
        ConversationContext.Clear();
        AddSystemMessage("上下文已清除。");
        Log.Information("上下文已清除，清除 {Count} 条消息", messageCount);

        // 更新 tokens 显示
        UpdateContextTokensDisplay();
    }

    /// <summary>
    /// 更新上下文 tokens 显示
    /// </summary>
    public void UpdateContextTokensDisplay()
    {
        ContextTokens = ConversationContext.EstimatedTokenCount;
        OnPropertyChanged(nameof(ContextTokensInfo));
        OnPropertyChanged(nameof(IsNearCompressionThreshold));
        OnPropertyChanged(nameof(CompressionPreview));
    }

    #endregion

    #region About Commands

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

    #region Helper Methods

    /// <summary>
    /// 加载最新的历史记录
    /// </summary>
    private async Task LoadLatestHistoryAsync()
    {
        if (_historyService == null)
        {
            // 历史服务不可用，显示欢迎消息
            AddSystemMessage("雅典娜 AI 助手已启动。请问有什么可以帮助您的？");
            return;
        }

        try
        {
            var historyItems = await _historyService.LoadAllAsync();
            if (historyItems.Count > 0)
            {
                // 加载最新的历史记录
                var latestItem = historyItems.First();
                foreach (var msg in latestItem.Messages)
                {
                    Messages.Add(msg);
                }
                CurrentConversationId = latestItem.Id;
                _loadedMessagesHash = ComputeMessagesHash(latestItem.Messages);
                UpdateConversationContext();
                Log.Information("加载最新历史记录: {Id} - {Summary}", latestItem.Id, latestItem.Summary);
            }
            else
            {
                // 没有历史记录，显示欢迎消息
                AddSystemMessage("雅典娜 AI 助手已启动。请问有什么可以帮助您的？");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "加载最新历史记录失败");
            AddSystemMessage("雅典娜 AI 助手已启动。请问有什么可以帮助您的？");
        }
    }

    /// <summary>
    /// 处理主动消息触发
    /// </summary>
    private async void OnProactiveMessageTriggered(object? sender, ProactiveMessageEventArgs e)
    {
        // 如果正在发送消息，跳过此次触发
        if (IsSending)
        {
            Log.Information("主动消息触发但正在发送消息，跳过: {TaskId}", e.TaskId);
            return;
        }

        Log.Information("主动消息触发: {TaskId} - {Intent}", e.TaskId, e.Intent);

        try
        {
            // 添加心跳消息标记
            var heartbeatMessage = new ChatMessage
            {
                Role = "assistant",
                Content = string.Empty,
                Timestamp = DateTime.Now,
                IsLoading = true,
                IsHeartbeat = true
            };

            // 在 UI 线程上添加消息
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                Messages.Add(heartbeatMessage);
            });

            // 构建系统提示，让 AI 根据意图生成消息
            var systemPrompt = _promptService?.GetProactiveMessagePrompt(e.Intent, DateTime.Now)
                ?? $@"
你安排了一个主动消息现在发送。
你的意图是: {e.Intent}

根据对话历史和用户信息，生成一条自然、有上下文的消息。
不要提及这是一个计划任务或系统触发的消息。
";

            // 临时添加系统消息到上下文
            ConversationContext.AddSystemMessage(systemPrompt);

            Avalonia.Threading.Dispatcher.UIThread.Post(() => IsSending = true);
            _cancellationTokenSource = new CancellationTokenSource();

            var responseLength = 0;

            // 流式接收响应
            await foreach (var chunk in _chatService!.StreamMessageAsync(
                "", // 空用户消息，让 AI 根据系统提示生成
                ConversationContext,
                _cancellationTokenSource.Token))
            {
                heartbeatMessage.Content += chunk;
                responseLength += chunk.Length;
            }

            Avalonia.Threading.Dispatcher.UIThread.Post(() => heartbeatMessage.IsLoading = false);

            Log.Information("主动消息完成，响应长度: {Length} 字符", responseLength);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理主动消息时发生错误");
        }
        finally
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => IsSending = false);
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
        }
    }

    private async Task LoadSettingsAsync()
    {
        if (_configService != null)
        {
            Config = await _configService.LoadAsync();
            ShowHeartbeatButton = Config.ShowHeartbeatButton;
            CurrentTheme = Config.Theme;

            // 初始化上下文 tokens 阈值
            ContextTokensThreshold = Config.CompressionThreshold;

            // 更新历史服务的次级模型配置
            if (_historyService is ConversationHistoryService historyService)
            {
                historyService.UpdateSecondaryConfig(Config);
            }

            Log.Debug("配置加载完成: Theme={Theme}, ShowHeartbeat={ShowHeartbeat}",
                Config.Theme, Config.ShowHeartbeatButton);
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
            _logger.Warning("知识库服务未初始化");
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
        }
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
    /// 更新对话上下文（根据当前消息列表重建）
    /// </summary>
    private void UpdateConversationContext()
    {
        ConversationContext.Clear();
        foreach (var msg in Messages)
        {
            if (msg.Role == "user")
            {
                ConversationContext.AddUserMessage(msg.Content);
            }
            else if (msg.Role == "assistant")
            {
                ConversationContext.AddAssistantMessage(msg.Content);
            }
        }
        Log.Debug("对话上下文已更新");
    }

    /// <summary>
    /// 添加系统消息
    /// </summary>
    private void AddSystemMessage(string content)
    {
        Messages.Add(new ChatMessage
        {
            Role = "system",
            Content = content,
            Timestamp = DateTime.Now
        });
    }

    /// <summary>
    /// 添加错误消息
    /// </summary>
    private void AddErrorMessage(string content)
    {
        Messages.Add(new ChatMessage
        {
            Role = "error",
            Content = content,
            Timestamp = DateTime.Now
        });
        Log.Error("显示错误消息: {Message}", content);
    }

    /// <summary>
    /// 处理加载历史对话请求
    /// </summary>
    private async void OnLoadHistoryRequested(object? sender, ConversationHistoryItem item)
    {
        Log.Information("请求加载历史对话: {Id}", item.Id);
        await LoadHistoryConversationAsync(item);
        // 切换回聊天标签页
        SelectedTabIndex = 0;
    }

    /// <summary>
    /// 保存当前对话
    /// </summary>
    public async Task SaveCurrentConversationAsync()
    {
        if (_historyService == null || Messages.Count == 0)
            return;

        try
        {
            // 过滤掉系统消息
            var messagesToSave = Messages.Where(m => m.Role == "user" || m.Role == "assistant").ToList();
            if (messagesToSave.Count == 0)
                return;

            // 计算当前消息的哈希值
            var currentHash = ComputeMessagesHash(messagesToSave);

            // 判断是否需要生成新摘要（消息有变化或是新对话）
            var forceGenerateSummary = currentHash != _loadedMessagesHash;

            var item = await _historyService.CreateFromMessagesAsync(
                new ObservableCollection<ChatMessage>(messagesToSave),
                forceGenerateSummary);

            // 如果是更新现有对话
            if (!string.IsNullOrEmpty(CurrentConversationId))
            {
                item.Id = CurrentConversationId;
            }

            await _historyService.SaveAsync(item);
            CurrentConversationId = item.Id;
            _loadedMessagesHash = currentHash;
            Log.Information("对话已保存: {Id} - {Summary}", item.Id, item.Summary);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "保存对话失败");
        }
    }

    /// <summary>
    /// 加载历史对话
    /// </summary>
    public async Task LoadHistoryConversationAsync(ConversationHistoryItem item)
    {
        if (item == null)
            return;

        // 如果当前有消息，先保存
        if (Messages.Count > 0 && _historyService != null)
        {
            await SaveCurrentConversationAsync();
        }

        // 加载历史消息
        Messages.Clear();
        ConversationContext.Clear();

        foreach (var msg in item.Messages)
        {
            Messages.Add(msg);
        }

        CurrentConversationId = item.Id;
        _loadedMessagesHash = ComputeMessagesHash(item.Messages);
        UpdateConversationContext();

        Log.Information("加载历史对话: {Id} - {Summary}", item.Id, item.Summary);
    }

    /// <summary>
    /// 计算消息列表的哈希值
    /// </summary>
    private static string ComputeMessagesHash(List<ChatMessage> messages)
    {
        if (messages == null || messages.Count == 0)
            return string.Empty;

        var content = string.Join("|", messages.Select(m => $"{m.Role}:{m.Content}"));
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        var hash = sha.ComputeHash(bytes);
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// 检查是否有当前对话需要保存
    /// </summary>
    public bool HasUnsavedChanges => Messages.Count > 0 && string.IsNullOrEmpty(CurrentConversationId);

    #endregion
}
