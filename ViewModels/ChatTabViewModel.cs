using Athena.UI.Models;
using Athena.UI.Services;
using Athena.UI.Services.Interfaces;
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

public partial class ChatTabViewModel : ViewModelBase
{
    private readonly IChatService? _chatService;
    private readonly IConfigService? _configService;
    private readonly IConversationHistoryService? _historyService;
    private readonly IPromptService? _promptService;
    private readonly ITaskScheduler? _taskScheduler;
    private CancellationTokenSource? _cancellationTokenSource;
    private readonly ILogger _logger = Log.ForContext<ChatTabViewModel>();

    #region Properties

    /// <summary>
    /// 聊天消息列表
    /// </summary>
    public ObservableCollection<ChatMessage> Messages { get; }

    /// <summary>
    /// 对话上下文
    /// </summary>
    public ConversationContext ConversationContext { get; }

    /// <summary>
    /// 输入文本
    /// </summary>
    [ObservableProperty]
    private string _inputText = string.Empty;

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
    /// 当前对话 ID
    /// </summary>
    [ObservableProperty]
    private string _currentConversationId = string.Empty;

    /// <summary>
    /// 加载的消息哈希值，用于检测是否有修改
    /// </summary>
    private string _loadedMessagesHash = string.Empty;

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

    /// <summary>
    /// 是否显示心跳按钮
    /// </summary>
    [ObservableProperty]
    private bool _showHeartbeatButton = true;

    /// <summary>
    /// 当前主题
    /// </summary>
    [ObservableProperty]
    private string _currentTheme = "Dark";

    #endregion

    #region Events

    /// <summary>
    /// 请求切换到任务标签页事件
    /// </summary>
    public event EventHandler? SwitchToTasksTabRequested;

    #endregion

    /// <summary>
    /// 默认构造函数（用于设计时）
    /// </summary>
    public ChatTabViewModel() : this(null, null, null, null, null)
    {
    }

    /// <summary>
    /// 依赖注入构造函数
    /// </summary>
    public ChatTabViewModel(
        IChatService? chatService,
        IConfigService? configService,
        IConversationHistoryService? historyService,
        IPromptService? promptService,
        ITaskScheduler? taskScheduler)
    {
        _chatService = chatService;
        _configService = configService;
        _historyService = historyService;
        _promptService = promptService;
        _taskScheduler = taskScheduler;

        Messages = new ObservableCollection<ChatMessage>();
        ConversationContext = new ConversationContext();

        // 订阅任务触发事件
        if (_taskScheduler != null)
        {
            _taskScheduler.ProactiveMessageTriggered += OnProactiveMessageTriggered;
        }

        // 初始化加载
        InitializeAsync().ConfigureAwait(false);
    }

    private async Task InitializeAsync()
    {
        await LoadSettingsAsync();
        await LoadLatestHistoryAsync();
    }

    private async Task LoadSettingsAsync()
    {
        if (_configService != null)
        {
            var config = await _configService.LoadAsync();
            ShowHeartbeatButton = config.ShowHeartbeatButton;
            CurrentTheme = config.Theme;
            ContextTokensThreshold = config.CompressionThreshold;
        }
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
            return;
        }

        var userMessageContent = InputText.Trim();
        InputText = string.Empty;
        IsSending = true;

        _logger.Information("用户发送消息: {Message}", userMessageContent);

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
                await CompressContextAsync();
            }
        }

        // 构建带时间戳的消息用于发送给 AI
        var enrichedContent = $"""
            [当前时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss dddd}]
            {userMessageContent}
            """;

        await GetAiResponseAsync(enrichedContent);
    }

    private async Task GetAiResponseAsync(string userMessageContent)
    {
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
            bool isFirstChunk = true;
            await foreach (var chunk in _chatService!.StreamMessageAsync(
                userMessageContent,
                ConversationContext,
                _cancellationTokenSource.Token))
            {
                if (isFirstChunk && !string.IsNullOrEmpty(chunk))
                {
                    aiMessage.IsLoading = false;
                    isFirstChunk = false;
                }
                aiMessage.Content += chunk;
            }
            aiMessage.IsLoading = false;
        }
        catch (OperationCanceledException)
        {
            aiMessage.Content += "\n[已取消]";
            aiMessage.IsLoading = false;
        }
        catch (Exception ex)
        {
            aiMessage.Content = $"发生错误: {ex.Message}";
            aiMessage.IsLoading = false;
            _logger.Error(ex, "发送消息时发生错误");
        }
        finally
        {
            IsSending = false;
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
            UpdateContextTokensDisplay();
        }
    }

    [RelayCommand]
    private void CancelSend()
    {
        _cancellationTokenSource?.Cancel();
    }

    [RelayCommand]
    private void ToggleTheme()
    {
        CurrentTheme = CurrentTheme == "Dark" ? "Light" : "Dark";
        // TODO: 通知 MainWindowViewModel 切换全局主题
    }

    [RelayCommand]
    private void ClearChat()
    {
        Messages.Clear();
        ConversationContext.Clear();
        AddSystemMessage("对话已清空。");
        UpdateContextTokensDisplay();
    }

    [RelayCommand]
    private async Task AttachFileAsync()
    {
        // TODO: 实现文件选择
        await Task.CompletedTask;
    }

    [RelayCommand(CanExecute = nameof(CanEditOrDelete))]
    private void StartInlineEdit(ChatMessage? message)
    {
        if (message == null) return;
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
    }

    [RelayCommand]
    private void ConfirmInlineEdit(ChatMessage? message)
    {
        if (message == null || !message.IsEditing) return;
        var newContent = message.EditContent.Trim();
        if (!string.IsNullOrWhiteSpace(newContent))
        {
            message.Content = newContent;
        }
        message.IsEditing = false;
        message.EditContent = string.Empty;
        UpdateConversationContext();
    }

    [RelayCommand]
    private void CancelInlineEdit(ChatMessage? message)
    {
        if (message == null) return;
        message.IsEditing = false;
        message.EditContent = string.Empty;
    }

    [RelayCommand(CanExecute = nameof(CanEditOrDelete))]
    private void DeleteMessage(ChatMessage? message)
    {
        if (message == null) return;
        Messages.Remove(message);
        UpdateConversationContext();
    }

    [RelayCommand(CanExecute = nameof(CanEditOrDelete))]
    private async Task RegenerateResponseAsync(ChatMessage? message)
    {
        if (message == null || message.Role != "assistant" || _chatService == null) return;
        var index = Messages.IndexOf(message);
        if (index <= 0) return;

        string? userContent = null;
        for (int i = index - 1; i >= 0; i--)
        {
            if (Messages[i].Role == "user")
            {
                userContent = Messages[i].Content;
                break;
            }
        }
        if (string.IsNullOrEmpty(userContent)) return;

        message.Content = string.Empty;
        message.IsLoading = true;
        IsSending = true;
        _cancellationTokenSource = new CancellationTokenSource();

        var tempContext = new ConversationContext();
        for (int i = 0; i < index; i++)
        {
            var msg = Messages[i];
            if (msg.Role == "user") tempContext.AddUserMessage(msg.Content);
            else if (msg.Role == "assistant") tempContext.AddAssistantMessage(msg.Content);
        }

        try
        {
            bool isFirstChunk = true;
            await foreach (var chunk in _chatService.StreamMessageAsync(userContent, tempContext, _cancellationTokenSource.Token))
            {
                if (isFirstChunk && !string.IsNullOrEmpty(chunk))
                {
                    message.IsLoading = false;
                    isFirstChunk = false;
                }
                message.Content += chunk;
            }
            message.IsLoading = false;
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
        }
        finally
        {
            IsSending = false;
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
            UpdateConversationContext();
        }
    }

    [RelayCommand]
    public async Task NewConversationAsync()
    {
        if (Messages.Count > 0 && _historyService != null)
        {
            await SaveCurrentConversationAsync();
        }
        Messages.Clear();
        ConversationContext.Clear();
        CurrentConversationId = string.Empty;
        AddSystemMessage("新对话已开始。请问有什么可以帮助您的？");
        UpdateContextTokensDisplay();
    }

    [RelayCommand]
    private void SwitchToTasksTab()
    {
        SwitchToTasksTabRequested?.Invoke(this, EventArgs.Empty);
    }

    #endregion

    #region Context Commands

    [RelayCommand]
    public async Task CompressContextAsync()
    {
        if (_historyService == null) return;
        try
        {
            var config = _configService != null ? await _configService.LoadAsync() : null;
            var threshold = config?.CompressionThreshold ?? 4000;
            var allMessages = ConversationContext.Messages.ToList();
            var keepRecentCount = ConversationContext.CalculateKeepCount(threshold);

            if (allMessages.Count <= keepRecentCount) return;

            var recentMessages = allMessages.TakeLast(keepRecentCount).ToList();
            var chatMessages = allMessages.Select(m => new ChatMessage { Role = m.Role, Content = m.Content }).ToList();
            var summary = await _historyService.CompressContextAsync(chatMessages, keepRecentCount);

            ConversationContext.Clear();
            if (!string.IsNullOrEmpty(summary)) ConversationContext.AddSystemMessage(summary);
            foreach (var msg in recentMessages)
            {
                if (msg.Role == "user") ConversationContext.AddUserMessage(msg.Content);
                else if (msg.Role == "assistant") ConversationContext.AddAssistantMessage(msg.Content);
            }
            UpdateContextTokensDisplay();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "压缩上下文失败");
        }
    }

    [RelayCommand]
    public void ClearContext()
    {
        Messages.Clear();
        ConversationContext.Clear();
        AddSystemMessage("上下文已清除。");
        UpdateContextTokensDisplay();
    }

    #endregion

    #region Helper Methods

    public void UpdateContextTokensDisplay()
    {
        ContextTokens = ConversationContext.EstimatedTokenCount;
        OnPropertyChanged(nameof(ContextTokensInfo));
        OnPropertyChanged(nameof(IsNearCompressionThreshold));
        OnPropertyChanged(nameof(CompressionPreview));
    }

    private void UpdateConversationContext()
    {
        ConversationContext.Clear();
        foreach (var msg in Messages)
        {
            if (msg.Role == "user") ConversationContext.AddUserMessage(msg.Content);
            else if (msg.Role == "assistant") ConversationContext.AddAssistantMessage(msg.Content);
        }
    }

    private void AddSystemMessage(string content)
    {
        Messages.Add(new ChatMessage { Role = "system", Content = content, Timestamp = DateTime.Now });
    }

    private void AddErrorMessage(string content)
    {
        Messages.Add(new ChatMessage { Role = "error", Content = content, Timestamp = DateTime.Now });
    }

    public async Task LoadHistoryConversationAsync(ConversationHistoryItem item)
    {
        if (item == null) return;
        
        // 如果有当前对话且未保存，可以考虑先保存（由调用者决定或自动）
        // 这里直接加载覆盖
        Messages.Clear();
        foreach (var msg in item.Messages)
        {
            Messages.Add(msg);
        }
        
        CurrentConversationId = item.Id;
        _loadedMessagesHash = ComputeMessagesHash(item.Messages.ToList());
        UpdateConversationContext();
        UpdateContextTokensDisplay();
        
        AddSystemMessage($"已加载对话: {item.Summary}");
        await Task.CompletedTask;
    }

    private async Task LoadLatestHistoryAsync()
    {
        if (_historyService == null)
        {
            AddSystemMessage("雅典娜 AI 助手已启动。请问有什么可以帮助您的？");
            return;
        }
        try
        {
            var historyItems = await _historyService.LoadAllAsync();
            if (historyItems.Count > 0)
            {
                var latestItem = historyItems.First();
                foreach (var msg in latestItem.Messages) Messages.Add(msg);
                CurrentConversationId = latestItem.Id;
                _loadedMessagesHash = ComputeMessagesHash(latestItem.Messages);
                UpdateConversationContext();
                UpdateContextTokensDisplay();
            }
            else
            {
                AddSystemMessage("雅典娜 AI 助手已启动。请问有什么可以帮助您的？");
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "加载最新历史记录失败");
            AddSystemMessage("雅典娜 AI 助手已启动。请问有什么可以帮助您的？");
        }
    }

    public async Task SaveCurrentConversationAsync()
    {
        if (_historyService == null || Messages.Count == 0) return;
        try
        {
            var messagesToSave = Messages.Where(m => m.Role == "user" || m.Role == "assistant").ToList();
            if (messagesToSave.Count == 0) return;
            var currentHash = ComputeMessagesHash(messagesToSave);
            var forceGenerateSummary = currentHash != _loadedMessagesHash;

            var item = await _historyService.CreateFromMessagesAsync(new ObservableCollection<ChatMessage>(messagesToSave), forceGenerateSummary);
            if (!string.IsNullOrEmpty(CurrentConversationId)) item.Id = CurrentConversationId;

            await _historyService.SaveAsync(item);
            CurrentConversationId = item.Id;
            _loadedMessagesHash = currentHash;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "保存对话失败");
        }
    }

    private static string ComputeMessagesHash(List<ChatMessage> messages)
    {
        if (messages == null || messages.Count == 0) return string.Empty;
        var content = string.Join("|", messages.Select(m => $"{m.Role}:{m.Content}"));
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        var hash = sha.ComputeHash(bytes);
        return Convert.ToHexString(hash);
    }

    private async void OnProactiveMessageTriggered(object? sender, ProactiveMessageEventArgs e)
    {
        if (IsSending) return;
        try
        {
            var heartbeatMessage = new ChatMessage { Role = "assistant", Content = string.Empty, Timestamp = DateTime.Now, IsLoading = true, IsHeartbeat = true };
            Avalonia.Threading.Dispatcher.UIThread.Post(() => Messages.Add(heartbeatMessage));

            var systemPrompt = _promptService?.GetProactiveMessagePrompt(e.Intent, DateTime.Now) ?? $"意图: {e.Intent}";
            ConversationContext.AddSystemMessage(systemPrompt);

            Avalonia.Threading.Dispatcher.UIThread.Post(() => IsSending = true);
            _cancellationTokenSource = new CancellationTokenSource();

            bool isFirstChunk = true;
            await foreach (var chunk in _chatService!.StreamMessageAsync("", ConversationContext, _cancellationTokenSource.Token))
            {
                if (isFirstChunk && !string.IsNullOrEmpty(chunk))
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => heartbeatMessage.IsLoading = false);
                    isFirstChunk = false;
                }
                heartbeatMessage.Content += chunk;
            }
            Avalonia.Threading.Dispatcher.UIThread.Post(() => heartbeatMessage.IsLoading = false);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "处理主动消息时发生错误");
        }
        finally
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => IsSending = false);
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
        }
    }

    #endregion
}
