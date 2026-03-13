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

    public ObservableCollection<ChatMessage> Messages { get; }
    public ConversationContext ConversationContext { get; }

    [ObservableProperty] private string _inputText = string.Empty;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SendMessageCommand))] private bool _isSending;
    [ObservableProperty] private string _currentConversationId = string.Empty;
    private string _loadedMessagesHash = string.Empty;
    [ObservableProperty] private int _contextTokens;
    [ObservableProperty] private int _contextTokensThreshold = 4000;
    public string ContextTokensInfo => $"{ContextTokens} / {MaxContextTokens} tokens";
    public bool IsNearCompressionThreshold => ContextTokens > MaxContextTokens * 0.8;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ContextTokensInfo))]
    [NotifyPropertyChangedFor(nameof(IsNearCompressionThreshold))]
    [NotifyPropertyChangedFor(nameof(InputPlaceholder))]
    [NotifyCanExecuteChangedFor(nameof(SendMessageCommand))]
    private int _maxContextTokens = 8000;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InputPlaceholder))]
    [NotifyCanExecuteChangedFor(nameof(SendMessageCommand))]
    private bool _autoCompress = true;

    public string InputPlaceholder => (ContextTokens >= MaxContextTokens && !AutoCompress) 
        ? "Chat.MaxContextReached" 
        : "Chat.InputPlaceholder";

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

    [ObservableProperty] private bool _showHeartbeatButton = true;
    [ObservableProperty] private string _currentTheme = "Dark";

    #endregion

    public event EventHandler? SwitchToTasksTabRequested;
    public event EventHandler<(int Current, string Preview)>? TokensInfoChanged;

    public ChatTabViewModel() : this(null, null, null, null, null) { }

    public ChatTabViewModel(IChatService? chatService, IConfigService? configService, IConversationHistoryService? historyService, IPromptService? promptService, ITaskScheduler? taskScheduler)
    {
        _chatService = chatService;
        _configService = configService;
        _historyService = historyService;
        _promptService = promptService;
        _taskScheduler = taskScheduler;

        Messages = new ObservableCollection<ChatMessage>();
        Messages.CollectionChanged += (s, e) => UpdateBubbleButtonVisibility();
        ConversationContext = new ConversationContext();

        if (_taskScheduler != null) _taskScheduler.ProactiveMessageTriggered += OnProactiveMessageTriggered;
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
            if (_promptService != null) ConversationContext.SetMainPersona(_promptService.GetPrompt(PromptType.MainPersona));
        }
    }

    #region Chat Commands

    [RelayCommand(CanExecute = nameof(CanSendMessage))]
    private async Task SendMessageAsync()
    {
        if (string.IsNullOrWhiteSpace(InputText) || IsSending) return;
        if (_chatService == null) { AddErrorMessage("聊天服务未初始化"); return; }

        var userMessageContent = InputText.Trim();
        InputText = string.Empty;
        IsSending = true;

        var timestampPrefix = $"[{DateTime.Now:yyMMdd HH:mm}] ";
        var enrichedContent = timestampPrefix + userMessageContent;

        Messages.Add(new ChatMessage { Role = "user", Content = enrichedContent, Timestamp = DateTime.Now });

        if (AutoCompress && ConversationContext.NeedsCompression(ContextTokensThreshold))
        {
            await InternalCompressContextAsync(isAuto: true);
        }

        await GetAiResponseAsync(enrichedContent);
    }

    private bool CanSendMessage() => !IsSending && (ContextTokens < MaxContextTokens || AutoCompress);

    private async Task GetAiResponseAsync(string? userMessageContent, bool addToContext = true)
    {
        var aiMessage = new ChatMessage { Role = "assistant", Content = string.Empty, Timestamp = DateTime.Now, IsLoading = true };
        Messages.Add(aiMessage);
        _cancellationTokenSource = new CancellationTokenSource();

        try
        {
            bool isFirstChunk = true;
            await foreach (var chunk in _chatService!.StreamMessageAsync(userMessageContent ?? "", ConversationContext, _cancellationTokenSource.Token,
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
                }), addToContext))
            {
                if (isFirstChunk && !string.IsNullOrEmpty(chunk)) { aiMessage.IsLoading = false; isFirstChunk = false; }
                aiMessage.Content += chunk;
            }
            aiMessage.IsLoading = false;
        }
        catch (OperationCanceledException) { aiMessage.Content += "\n[已取消]"; aiMessage.IsLoading = false; }
        catch (Exception ex) { aiMessage.Content = $"发生错误: {ex.Message}"; aiMessage.IsLoading = false; _logger.Error(ex, "发送消息错误"); }
        finally { IsSending = false; _cancellationTokenSource?.Dispose(); _cancellationTokenSource = null; UpdateContextTokensDisplay(); UpdateBubbleButtonVisibility(); }
    }

    [RelayCommand] private void CancelSend() => _cancellationTokenSource?.Cancel();
    [RelayCommand] 
    private void ToggleTheme() 
    {
        CurrentTheme = CurrentTheme == "Dark" ? "Light" : "Dark";
        App.SetTheme(CurrentTheme);
        _logger.Information("ChatTab 触发主题切换: {Theme}", CurrentTheme);
    }
    [RelayCommand] private void SwitchToTasksTab() => SwitchToTasksTabRequested?.Invoke(this, EventArgs.Empty);
    [RelayCommand] private async Task AttachFileAsync() => await Task.CompletedTask;

    [RelayCommand]
    private void ClearChat()
    {
        Messages.Clear();
        ConversationContext.Reset();
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
        if (topLevel?.Clipboard != null) await topLevel.Clipboard.SetTextAsync(message.Content);
    }

    [RelayCommand]
    private void StartInlineEdit(ChatMessage? message)
    {
        if (message == null) return;
        message.EditContent = message.Content;
        message.IsEditing = true;
    }

    [RelayCommand]
    private async Task ConfirmInlineEdit(ChatMessage? message)
    {
        if (message == null || !message.IsEditing) return;
        var newContent = message.EditContent.Trim();
        if (!string.IsNullOrWhiteSpace(newContent))
        {
            message.Content = newContent;
            message.IsEditing = false;
            var index = Messages.IndexOf(message);
            while (Messages.Count > index + 1) Messages.RemoveAt(index + 1);
            UpdateConversationContext();
            await GetAiResponseAsync(newContent, addToContext: false);
        }
        else message.IsEditing = false;
    }

    [RelayCommand] private void CancelInlineEdit(ChatMessage? message) { if (message != null) message.IsEditing = false; }

    [RelayCommand]
    private async Task RegenerateResponseAsync(ChatMessage? message)
    {
        if (message == null || _chatService == null) return;
        Messages.Remove(message);
        UpdateConversationContext();
        var lastUserMsg = Messages.LastOrDefault(m => m.Role == "user");
        if (lastUserMsg != null) await GetAiResponseAsync(lastUserMsg.Content, addToContext: false);
    }

    [RelayCommand]
    private void DeleteMessage(ChatMessage? message)
    {
        if (message == null) return;
        
        // 级联删除：如果删除的是带工具调用的助手消息，也要删除其后的工具结果
        if (message.Role == "assistant" && !string.IsNullOrEmpty(message.ToolCallsJson))
        {
            var index = Messages.IndexOf(message);
            while (index + 1 < Messages.Count && Messages[index + 1].Role == "tool")
            {
                Messages.RemoveAt(index + 1);
            }
        }
        
        Messages.Remove(message);
        UpdateConversationContext();
        UpdateContextTokensDisplay();
        UpdateBubbleButtonVisibility();
    }

    [RelayCommand]
    public async Task NewConversationAsync()
    {
        if (Messages.Count > 0 && _historyService != null) await SaveCurrentConversationAsync();
        Messages.Clear();
        ConversationContext.Reset();
        CurrentConversationId = string.Empty;
        UpdateContextTokensDisplay();
    }

    #endregion

    #region Context Internal Methods

    public async Task InternalCompressContextAsync(bool isAuto = false)
    {
        if (_historyService == null) return;
        try
        {
            var uncompressed = Messages.Where(m => !m.IsCompressed && (m.Role == "user" || m.Role == "assistant" || m.Role == "tool")).ToList();
            if (uncompressed.Count <= 1) return;

            int keepCount = isAuto ? ConversationContext.CalculateKeepCount(ContextTokensThreshold) : 1;
            
            // 确保保留集不以 tool 角色开始（工具链原子性）
            while (keepCount < uncompressed.Count)
            {
                var firstToKeep = uncompressed[uncompressed.Count - keepCount];
                if (firstToKeep.Role == "tool") keepCount++;
                else if (firstToKeep.Role == "assistant" && !string.IsNullOrEmpty(firstToKeep.ToolCallsJson)) break;
                else break;
            }

            var eligibleToCompress = uncompressed.Take(uncompressed.Count - keepCount).ToList();
            if (eligibleToCompress.Count == 0) return;

            var chatMessages = eligibleToCompress.Select(m => new ChatMessage { Role = m.Role, Content = m.Content, ToolCallId = m.ToolCallId, ToolCallsJson = m.ToolCallsJson }).ToList();
            var summary = await _historyService.CompressContextAsync(chatMessages, 0);

            if (!string.IsNullOrEmpty(summary))
            {
                foreach (var msg in eligibleToCompress) msg.IsCompressed = true;
                ConversationContext.SetSummary(summary);
                UpdateConversationContext();
                UpdateContextTokensDisplay();
            }
        }
        catch (Exception ex) { _logger.Error(ex, "压缩上下文失败"); }
    }

    public void InternalUndoCompression()
    {
        foreach (var msg in Messages) msg.IsCompressed = false;
        ConversationContext.SetSummary(null);
        UpdateConversationContext();
        UpdateContextTokensDisplay();
    }

    #endregion

    #region Helpers

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
        foreach (var msg in Messages) { msg.CanEdit = false; msg.CanRegenerate = false; }
        if (IsSending) return;
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

    private void AddErrorMessage(string content) => Messages.Add(new ChatMessage { Role = "error", Content = content, Timestamp = DateTime.Now });

    public async Task LoadHistoryConversationAsync(ConversationHistoryItem item)
    {
        if (item == null) return;
        Messages.Clear();
        ConversationContext.Reset();
        foreach (var msg in item.Messages) Messages.Add(msg);
        if (!string.IsNullOrEmpty(item.ContextSummary)) ConversationContext.SetSummary(item.ContextSummary);
        CurrentConversationId = item.Id;
        _loadedMessagesHash = ComputeMessagesHash(item.Messages.ToList());
        UpdateConversationContext();
        UpdateContextTokensDisplay();
        UpdateBubbleButtonVisibility();
        await Task.CompletedTask;
    }

    public void NotifyHistoryDeleted(string id) { if (CurrentConversationId == id) _loadedMessagesHash = string.Empty; }

    private async Task LoadLatestHistoryAsync()
    {
        if (_historyService == null) return;
        try {
            var historyItems = await _historyService.LoadAllAsync();
            if (historyItems.Count > 0) await LoadHistoryConversationAsync(historyItems.First());
        } catch (Exception ex) { _logger.Error(ex, "加载历史失败"); }
    }

    public async Task SaveCurrentConversationAsync()
    {
        if (_historyService == null || Messages.Count == 0) return;
        try {
            var messagesToSave = Messages.ToList();
            if (messagesToSave.Count == 0) return;
            var currentHash = ComputeMessagesHash(messagesToSave);
            var forceGenerateSummary = currentHash != _loadedMessagesHash;
            var item = await _historyService.CreateFromMessagesAsync(new ObservableCollection<ChatMessage>(messagesToSave), forceGenerateSummary);
            if (!string.IsNullOrEmpty(CurrentConversationId)) item.Id = CurrentConversationId;
            item.ContextSummary = ConversationContext.Summary;
            await _historyService.SaveAsync(item);
            CurrentConversationId = item.Id;
            _loadedMessagesHash = currentHash;
        } catch (Exception ex) { _logger.Error(ex, "保存对话失败"); }
    }

    private static string ComputeMessagesHash(List<ChatMessage> messages) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("|", messages.Select(m => $"{m.Role}:{m.Content}:{m.IsCompressed}")))));

    private async void OnProactiveMessageTriggered(object? sender, ProactiveMessageEventArgs e)
    {
        if (IsSending) return;
        try {
            var hb = new ChatMessage { Role = "assistant", Content = string.Empty, Timestamp = DateTime.Now, IsLoading = true, IsHeartbeat = true };
            Avalonia.Threading.Dispatcher.UIThread.Post(() => Messages.Add(hb));
            ConversationContext.AddSystemMessage(_promptService?.GetProactiveMessagePrompt(e.Intent, DateTime.Now) ?? $"意图: {e.Intent}");
            Avalonia.Threading.Dispatcher.UIThread.Post(() => IsSending = true);
            _cancellationTokenSource = new CancellationTokenSource();
            bool isFirst = true;
            await foreach (var chunk in _chatService!.StreamMessageAsync("", ConversationContext, _cancellationTokenSource.Token)) {
                if (isFirst && !string.IsNullOrEmpty(chunk)) { Avalonia.Threading.Dispatcher.UIThread.Post(() => hb.IsLoading = false); isFirst = false; }
                hb.Content += chunk;
            }
            Avalonia.Threading.Dispatcher.UIThread.Post(() => hb.IsLoading = false);
        } catch (Exception ex) { _logger.Error(ex, "主动消息错误"); }
        finally { Avalonia.Threading.Dispatcher.UIThread.Post(() => IsSending = false); _cancellationTokenSource?.Dispose(); _cancellationTokenSource = null; UpdateBubbleButtonVisibility(); }
    }

    #endregion
}
