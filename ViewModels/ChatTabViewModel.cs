using Athena.UI.Models;
using Athena.UI.Services;
using Athena.UI.Services.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
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
    /// 聊天消息列表 (UI套)
    /// </summary>
    public ObservableCollection<ChatMessage> Messages { get; }

    /// <summary>
    /// 对话上下文 (逻辑套)
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
    [NotifyCanExecuteChangedFor(nameof(SendMessageCommand))]
    private bool _isSending;

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
    public string ContextTokensInfo => $"{ContextTokens} / {MaxContextTokens} tokens";

    /// <summary>
    /// 是否接近压缩阈值（超过 80%）
    /// </summary>
    public bool IsNearCompressionThreshold => ContextTokens > MaxContextTokens * 0.8;

    /// <summary>
    /// 最大上下文限制
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ContextTokensInfo))]
    [NotifyPropertyChangedFor(nameof(IsNearCompressionThreshold))]
    [NotifyPropertyChangedFor(nameof(InputPlaceholder))]
    [NotifyCanExecuteChangedFor(nameof(SendMessageCommand))]
    private int _maxContextTokens = 8000;

    /// <summary>
    /// 是否自动压缩
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InputPlaceholder))]
    [NotifyCanExecuteChangedFor(nameof(SendMessageCommand))]
    private bool _autoCompress = true;

    /// <summary>
    /// 输入框占位符
    /// </summary>
    public string InputPlaceholder => (ContextTokens >= MaxContextTokens && !AutoCompress) 
        ? "Chat.MaxContextReached" 
        : "Chat.InputPlaceholder";

    /// <summary>
    /// 压缩预览文本
    /// </summary>
    public string CompressionPreview
    {
        get
        {
            if (ConversationContext.Summary != null)
                return $"[SUMMARY]: {ConversationContext.Summary}";
            
            var uncompressedMessages = Messages.Where(m => !m.IsCompressed && (m.Role == "user" || m.Role == "assistant" || m.Role == "tool")).ToList();
            if (uncompressedMessages.Count <= 1)
                return "No eligible messages to compress.";

            return $"Eligible for manual compression: {uncompressedMessages.Count - 1} messages.";
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

    public event EventHandler? SwitchToTasksTabRequested;
    
    /// <summary>
    /// 当 Token 信息变化时触发，用于同步到配置页
    /// </summary>
    public event EventHandler<(int Current, string Preview)>? TokensInfoChanged;

    #endregion

    public ChatTabViewModel() : this(null, null, null, null, null) { }

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
        Messages.CollectionChanged += (s, e) => UpdateBubbleButtonVisibility();
        
        ConversationContext = new ConversationContext();

        if (_taskScheduler != null)
        {
            _taskScheduler.ProactiveMessageTriggered += OnProactiveMessageTriggered;
        }

        InitializeAsync().ConfigureAwait(false);
    }

    public async Task RefreshSettingsAsync()
    {
        await LoadSettingsAsync();
        UpdateContextTokensDisplay();
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
            MaxContextTokens = config.MaxContextTokens;
            AutoCompress = config.AutoCompress;
            
            if (_promptService != null)
            {
                ConversationContext.SetMainPersona(_promptService.GetPrompt(PromptType.MainPersona));
            }
        }
    }

    #region Chat Commands

    [RelayCommand(CanExecute = nameof(CanSendMessage))]
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

        var timestampPrefix = $"[{DateTime.Now:yyMMdd HH:mm}] ";
        var enrichedContent = timestampPrefix + userMessageContent;

        _logger.Information("用户发送消息: {Message}", enrichedContent);

        var userMessage = new ChatMessage
        {
            Role = "user",
            Content = enrichedContent,
            Timestamp = DateTime.Now
        };
        Messages.Add(userMessage);

        if (AutoCompress && ConversationContext.NeedsCompression(ContextTokensThreshold))
        {
            await InternalCompressContextAsync();
        }

        await GetAiResponseAsync(enrichedContent);
    }

    private bool CanSendMessage()
    {
        if (IsSending) return false;
        if (ContextTokens >= MaxContextTokens && !AutoCompress) return false;
        return true;
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
                _cancellationTokenSource.Token,
                msg => Avalonia.Threading.Dispatcher.UIThread.Post(() => 
                {
                    if (msg.Role == "assistant" && !string.IsNullOrEmpty(msg.ToolCallsJson))
                    {
                        aiMessage.Content = msg.Content;
                        aiMessage.ToolCallsJson = msg.ToolCallsJson;
                        aiMessage.IsLoading = false;
                    }
                    else if (msg.Role == "tool")
                    {
                        var index = Messages.IndexOf(aiMessage);
                        Messages.Insert(index >= 0 ? index : Messages.Count, msg);
                    }
                })))
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
            UpdateBubbleButtonVisibility();
        }
    }

    [RelayCommand]
    private void CancelSend() => _cancellationTokenSource?.Cancel();

    [RelayCommand]
    private void ToggleTheme()
    {
        CurrentTheme = CurrentTheme == "Dark" ? "Light" : "Dark";
    }

    [RelayCommand]
    private void SwitchToTasksTab()
    {
        SwitchToTasksTabRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private async Task AttachFileAsync()
    {
        // TODO: 实现文件选择
        await Task.CompletedTask;
    }

    [RelayCommand]
    private void ClearChat()
    {
        Messages.Clear();
        ConversationContext.Reset();
        AddSystemMessage("对话已清空。");
        UpdateContextTokensDisplay();
    }

    [RelayCommand]
    private async Task CopyMessageAsync(ChatMessage? message)
    {
        if (message == null || string.IsNullOrEmpty(message.Content)) return;
        
        var topLevel = Avalonia.Application.Current?.ApplicationLifetime switch
        {
            Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop => desktop.MainWindow,
            Avalonia.Controls.ApplicationLifetimes.ISingleViewApplicationLifetime singleView => Avalonia.Controls.TopLevel.GetTopLevel(singleView.MainView),
            _ => null
        };

        if (topLevel?.Clipboard != null)
        {
            await topLevel.Clipboard.SetTextAsync(message.Content);
            _logger.Information("消息内容已复制到剪贴板");
        }
    }

    [RelayCommand]
    private void StartInlineEdit(ChatMessage? message)
    {
        if (message == null) return;
        message.EditContent = message.Content;
        message.IsEditing = true;
    }

    [RelayCommand]
    private async Task ConfirmInlineEditCommand(ChatMessage? message)
    {
        if (message == null || !message.IsEditing) return;
        var newContent = message.EditContent.Trim();
        if (!string.IsNullOrWhiteSpace(newContent))
        {
            message.Content = newContent;
            message.IsEditing = false;
            
            var index = Messages.IndexOf(message);
            while (Messages.Count > index + 1)
            {
                Messages.RemoveAt(index + 1);
            }
            
            UpdateConversationContext();
            await GetAiResponseAsync(newContent);
        }
        else
        {
            message.IsEditing = false;
        }
    }

    [RelayCommand]
    private void CancelInlineEdit(ChatMessage? message)
    {
        if (message == null) return;
        message.IsEditing = false;
    }

    [RelayCommand]
    private async Task RegenerateResponseAsync(ChatMessage? message)
    {
        if (message == null || _chatService == null) return;
        
        Messages.Remove(message);
        UpdateConversationContext();
        
        var lastUserMsg = Messages.LastOrDefault(m => m.Role == "user");
        if (lastUserMsg != null)
        {
            await GetAiResponseAsync(lastUserMsg.Content);
        }
    }

    [RelayCommand]
    private void DeleteMessage(ChatMessage? message)
    {
        if (message == null) return;
        Messages.Remove(message);
        UpdateConversationContext();
        UpdateContextTokensDisplay();
        UpdateBubbleButtonVisibility();
    }

    [RelayCommand]
    public async Task NewConversationAsync()
    {
        if (Messages.Count > 0 && _historyService != null)
        {
            await SaveCurrentConversationAsync();
        }
        Messages.Clear();
        ConversationContext.Reset();
        CurrentConversationId = string.Empty;
        UpdateContextTokensDisplay();
    }

    #endregion

    #region Context Internal Methods (Used by ConfigViewModel or Auto-Comp)

    public async Task InternalCompressContextAsync()
    {
        if (_historyService == null) return;
        try
        {
            var uncompressed = Messages.Where(m => !m.IsCompressed && (m.Role == "user" || m.Role == "assistant" || m.Role == "tool")).ToList();
            if (uncompressed.Count <= 1) return;

            var eligibleToCompress = uncompressed.Take(uncompressed.Count - 1).ToList();
            
            var chatMessages = eligibleToCompress.Select(m => new ChatMessage 
            { 
                Role = m.Role, 
                Content = m.Content,
                ToolCallId = m.ToolCallId,
                ToolCallsJson = m.ToolCallsJson
            }).ToList();
            
            var summary = await _historyService.CompressContextAsync(chatMessages, 0);

            if (!string.IsNullOrEmpty(summary))
            {
                foreach (var msg in eligibleToCompress) msg.IsCompressed = true;
                ConversationContext.SetSummary(summary);
                UpdateConversationContext();
                UpdateContextTokensDisplay();
                _logger.Information("非破坏性压缩完成。摘要: {Summary}", summary);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "压缩上下文失败");
        }
    }

    public void InternalUndoCompression()
    {
        foreach (var msg in Messages) msg.IsCompressed = false;
        ConversationContext.SetSummary(null);
        UpdateConversationContext();
        UpdateContextTokensDisplay();
        _logger.Information("已撤回压缩，恢复全量上下文。");
    }

    #endregion

    #region Helper Methods

    public void UpdateContextTokensDisplay()
    {
        ContextTokens = ConversationContext.EstimatedTokenCount;
        OnPropertyChanged(nameof(ContextTokensInfo));
        OnPropertyChanged(nameof(IsNearCompressionThreshold));
        OnPropertyChanged(nameof(CompressionPreview));
        OnPropertyChanged(nameof(InputPlaceholder));
        SendMessageCommand.NotifyCanExecuteChanged();
        
        TokensInfoChanged?.Invoke(this, (ContextTokens, CompressionPreview));
    }

    private void UpdateConversationContext()
    {
        ConversationContext.Clear();
        foreach (var msg in Messages.Where(m => !m.IsCompressed))
        {
            if (msg.Role == "user") ConversationContext.AddUserMessage(msg.Content);
            else if (msg.Role == "assistant") ConversationContext.AddAssistantMessage(msg.Content, msg.ToolCallsJson);
            else if (msg.Role == "system") ConversationContext.AddSystemMessage(msg.Content);
            else if (msg.Role == "tool") ConversationContext.AddToolMessage(msg.Content, msg.ToolCallId);
        }
    }

    private void UpdateBubbleButtonVisibility()
    {
        if (Messages.Count == 0) return;

        foreach (var msg in Messages)
        {
            msg.CanEdit = false;
            msg.CanRegenerate = false;
        }

        if (IsSending) return;

        // 仅针对非压缩消息且处于正确位置的消息开放编辑/重新生成
        var lastMsg = Messages.Last();
        if (!lastMsg.IsCompressed && lastMsg.Role == "assistant")
        {
            lastMsg.CanRegenerate = true;
            if (Messages.Count >= 2)
            {
                var prevMsg = Messages[Messages.Count - 2];
                if (!prevMsg.IsCompressed && prevMsg.Role == "user") prevMsg.CanEdit = true;
            }
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
        Messages.Clear();
        ConversationContext.Reset();
        
        foreach (var msg in item.Messages) Messages.Add(msg);
        
        // 恢复上下文摘要
        if (!string.IsNullOrEmpty(item.ContextSummary))
        {
            ConversationContext.SetSummary(item.ContextSummary);
        }
        
        CurrentConversationId = item.Id;
        _loadedMessagesHash = ComputeMessagesHash(item.Messages.ToList());
        
        UpdateConversationContext();
        UpdateContextTokensDisplay();
        UpdateBubbleButtonVisibility();
        
        AddSystemMessage($"已加载对话: {item.Summary}");
        await Task.CompletedTask;
    }

    public void NotifyHistoryDeleted(string id)
    {
        if (CurrentConversationId == id)
        {
            _logger.Information("当前加载的对话已在历史记录中删除: {Id}", id);
            _loadedMessagesHash = string.Empty;
        }
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
            if (historyItems.Count > 0) await LoadHistoryConversationAsync(historyItems.First());
            else AddSystemMessage("雅典娜 AI 助手已启动。请问有什么可以帮助您的？");
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
            var messagesToSave = Messages.ToList();
            if (messagesToSave.Count == 0) return;
            
            var currentHash = ComputeMessagesHash(messagesToSave);
            var forceGenerateSummary = currentHash != _loadedMessagesHash;
            
            var item = await _historyService.CreateFromMessagesAsync(new ObservableCollection<ChatMessage>(messagesToSave), forceGenerateSummary);
            if (!string.IsNullOrEmpty(CurrentConversationId)) item.Id = CurrentConversationId;
            
            // 保存当前的上下文摘要
            item.ContextSummary = ConversationContext.Summary;

            await _historyService.SaveAsync(item);
            CurrentConversationId = item.Id;
            _loadedMessagesHash = currentHash;
        }
        catch (Exception ex) { _logger.Error(ex, "保存对话失败"); }
    }

    private static string ComputeMessagesHash(List<ChatMessage> messages)
    {
        if (messages == null || messages.Count == 0) return string.Empty;
        var content = string.Join("|", messages.Select(m => $"{m.Role}:{m.Content}:{m.IsCompressed}"));
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
        catch (Exception ex) { _logger.Error(ex, "处理主动消息时发生错误"); }
        finally
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => IsSending = false);
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
            UpdateBubbleButtonVisibility();
        }
    }

    #endregion
}
