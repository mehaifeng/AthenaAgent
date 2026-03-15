using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using Athena.UI.Services;

namespace Athena.UI.ViewModels;

public partial class ChatTabViewModel : ViewModelBase
{
    private readonly IChatService? _chatService;
    private readonly IConfigService? _configService;
    private readonly IConversationHistoryService? _historyService;
    private readonly IPromptService? _promptService;
    private readonly ITaskScheduler? _taskScheduler;
    private readonly IFunctionRegistry? _functionRegistry;
    private readonly ITokenService? _tokenService;
    private readonly ILogger _logger = Log.ForContext<ChatTabViewModel>();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendMessageCommand))]
    private string _inputText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendMessageCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartInlineEditCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmInlineEditCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelInlineEditCommand))]
    [NotifyCanExecuteChangedFor(nameof(RegenerateResponseCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteMessageCommand))]
    private bool _isSending;

    [ObservableProperty]
    private bool _isResetting;

    [ObservableProperty]
    private string _currentTheme = "Dark";

    public ObservableCollection<ChatMessage> Messages { get; } = new();

    public string ContextTokensInfo => _tokenService?.TokenInfoText ?? "0 / 0 tokens";

    public ITokenService? TokenService => _tokenService;

    public string InputPlaceholder => "Chat.InputPlaceholder";

    public event EventHandler? SwitchToTasksTabRequested;

    private ConversationContext _currentContext = new();
    
    // 记录当前加载的历史对话 ID，如果是新对话则为空
    private string? _currentHistoryId;
    
    // 记录加载时的消息数量，用于判断是否发生了修改
    private int _initialMessageCount;

    public ChatTabViewModel() : this(null, null, null, null, null, null, null) { }

    public ChatTabViewModel(
        IChatService? chatService,
        IConfigService? configService,
        IConversationHistoryService? historyService,
        IPromptService? promptService,
        ITaskScheduler? taskScheduler,
        IFunctionRegistry? functionRegistry,
        ITokenService? tokenService)
    {
        _chatService = chatService;
        _configService = configService;
        _historyService = historyService;
        _promptService = promptService;
        _taskScheduler = taskScheduler;
        _functionRegistry = functionRegistry;
        _tokenService = tokenService;

        // Initialize from config
        if (_configService != null)
        {
            var config = _configService.Load();
            if (_tokenService != null) _tokenService.MaxTokens = config.MaxContextTokens;
            CurrentTheme = config.Theme;
        }

        Messages.CollectionChanged += (s, e) => 
        {
            UpdateContextTokensDisplay();
            UpdateBubbleButtonVisibility();
        };

        // 计算初始 Token（系统提示词和工具声明的基底开销）
        UpdateContextTokensDisplay();
    }

    [RelayCommand(CanExecute = nameof(CanSendMessage))]
    private async Task SendMessageAsync()
    {
        if (string.IsNullOrWhiteSpace(InputText)) return;

        var userContent = InputText;
        InputText = string.Empty;

        Messages.Add(new ChatMessage { Role = "user", Content = userContent, Timestamp = DateTime.Now });
        await GetAiResponseAsync(userContent);
    }

    private bool CanSendMessage() => !IsSending && !string.IsNullOrWhiteSpace(InputText);

    private bool CanModifyMessages() => !IsSending;

    [RelayCommand]
    private void ToggleTheme()
    {
        CurrentTheme = CurrentTheme == "Dark" ? "Light" : "Dark";
        App.SetTheme(CurrentTheme);
        if (_configService != null)
        {
            var config = _configService.Load();
            config.Theme = CurrentTheme;
            _ = _configService.SaveAsync(config);
        }
    }

    [RelayCommand]
    private async Task NewConversationAsync()
    {
        IsResetting = true;
        try
        {
            // 如果是从历史加载且未发生新对话，就不再重复保存为新历史
            bool isModified = string.IsNullOrEmpty(_currentHistoryId) || Messages.Count != _initialMessageCount;

            // 保存当前对话到历史记录
            if (Messages.Count > 0 && _historyService != null && isModified)
            {
                await _historyService.CreateFromMessagesAsync(Messages, forceGenerateSummary: true);
            }

            Messages.Clear();
            _currentContext.Reset();
            _currentHistoryId = null;
            _initialMessageCount = 0;
            UpdateConversationContext();
            UpdateContextTokensDisplay();
            await Task.Delay(300); // Visual feedback
        }
        finally
        {
            IsResetting = false;
        }
    }

    [RelayCommand]
    private void SwitchToTasksTab()
    {
        SwitchToTasksTabRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand(CanExecute = nameof(CanModifyMessages))]
    private void StartInlineEdit(ChatMessage? message)
    {
        if (message == null) return;
        message.EditContent = message.Content;
        message.IsEditing = true;
    }

    [RelayCommand(CanExecute = nameof(CanModifyMessages))]
    private async Task ConfirmInlineEdit(ChatMessage? message)
    {
        if (message == null || !message.IsEditing) return;
        var newContent = message.EditContent.Trim();
        if (!string.IsNullOrWhiteSpace(newContent))
        {
            message.Content = newContent;
            message.IsEditing = false;
            UpdateConversationContext();
            UpdateContextTokensDisplay();
            UpdateBubbleButtonVisibility();
        }
        else message.IsEditing = false;
        await Task.CompletedTask;
    }

    [RelayCommand(CanExecute = nameof(CanModifyMessages))] private void CancelInlineEdit(ChatMessage? message) { if (message != null) message.IsEditing = false; }

    [RelayCommand(CanExecute = nameof(CanModifyMessages))]
    private async Task RegenerateResponseAsync(ChatMessage? message)
    {
        if (message == null || _chatService == null) return;
        
        int msgIndex = Messages.IndexOf(message);
        if (msgIndex < 0) return;

        // 核心重塑逻辑：向上寻找距离该助手回复最近的用户提问
        int lastUserIndex = -1;
        for (int i = msgIndex - 1; i >= 0; i--)
        {
            if (Messages[i].Role == "user")
            {
                lastUserIndex = i;
                break;
            }
        }

        if (lastUserIndex == -1) return;

        // 彻底清空提问之后的所有上下文，包括工具结果等干扰项
        while (Messages.Count > lastUserIndex + 1)
        {
            Messages.RemoveAt(lastUserIndex + 1);
        }

        UpdateConversationContext();
        
        // 基于该干净的节点重新生成
        var lastUserMsg = Messages[lastUserIndex];
        await GetAiResponseAsync(lastUserMsg.Content, addToContext: false);
    }

    [RelayCommand(CanExecute = nameof(CanModifyMessages))]
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
    private void CopyMessage(ChatMessage? message)
    {
        if (message != null)
        {
            // Clipboard logic usually in View or via service
            _logger.Debug("Copying message content to clipboard");
        }
    }

    [RelayCommand]
    private void AttachFile()
    {
        // File attachment logic
    }

    private async Task GetAiResponseAsync(string input, bool addToContext = true)
    {
        if (_chatService == null) return;

        IsSending = true;
        var assistantMsg = new ChatMessage 
        { 
            Role = "assistant", 
            Content = string.Empty, 
            Timestamp = DateTime.Now,
            IsLoading = true 
        };
        Messages.Add(assistantMsg);

        try
        {
            await foreach (var contentDelta in _chatService.StreamMessageAsync(
                input, 
                _currentContext, 
                onMessageAdded: msg => {
                    if (msg.Role == "assistant" && !string.IsNullOrEmpty(msg.ToolCallsJson))
                    {
                        // Hide intermediate tool call records
                        msg.IsHidden = true;
                        msg.Content = string.Empty; // Prevent duplicate text
                        Messages.Add(msg);

                        // Update the main bubble's status
                        assistantMsg.IsLoading = false;
                        try 
                        {
                            var toolCalls = System.Text.Json.Nodes.JsonNode.Parse(msg.ToolCallsJson)?.AsArray();
                            if (toolCalls != null && toolCalls.Count > 0)
                            {
                                var names = toolCalls.Select(x => x?["FunctionName"]?.ToString()).Where(x => !string.IsNullOrEmpty(x));
                                assistantMsg.ToolExecutionSummary = $"正在调用: {string.Join(", ", names)}...";
                            }
                            else assistantMsg.ToolExecutionSummary = "正在调用工具...";
                        }
                        catch { assistantMsg.ToolExecutionSummary = "正在调用工具..."; }
                    }
                    else if (msg.Role == "tool")
                    {
                        Messages.Add(msg);
                        
                        // 工具执行完毕，等待大模型下一步指示
                        var name = string.IsNullOrEmpty(msg.ToolName) ? "工具" : msg.ToolName;
                        assistantMsg.ToolExecutionSummary = $"{name} 调用完毕，持续思考中...";
                    }
                },
                addToContext: addToContext))
            {
                if (!string.IsNullOrEmpty(contentDelta))
                {
                    assistantMsg.IsLoading = false; // 收到文字后停止 loading 动画
                    assistantMsg.ToolExecutionSummary = string.Empty; // 开始输出正式回复，隐藏工具调用状态
                    assistantMsg.Content += contentDelta;
                }
            }

            UpdateConversationContext();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Get AI response failed");
            assistantMsg.IsLoading = false;
            assistantMsg.ToolExecutionSummary = string.Empty;
            assistantMsg.Content = $"Error: {ex.Message}";
        }
        finally
        {
            assistantMsg.IsLoading = false;
            assistantMsg.ToolExecutionSummary = string.Empty;
            
            // Cleanup the empty main assistant message if it didn't generate any text and didn't call tools directly
            if (string.IsNullOrWhiteSpace(assistantMsg.Content) && string.IsNullOrEmpty(assistantMsg.ToolCallsJson))
            {
                Messages.Remove(assistantMsg);
            }
            
            IsSending = false;
            UpdateContextTokensDisplay();
            UpdateBubbleButtonVisibility();
        }
    }

    private void UpdateConversationContext()
    {
        _currentContext.Clear();
        foreach (var msg in Messages)
        {
            if (msg.Role == "user") 
            {
                _currentContext.AddUserMessage(msg.Content);
            }
            else if (msg.Role == "assistant") 
            {
                // Only add to context if it has text or tool calls to avoid API validation errors
                if (!string.IsNullOrEmpty(msg.Content) || !string.IsNullOrEmpty(msg.ToolCallsJson))
                {
                    _currentContext.AddAssistantMessage(msg.Content, msg.ToolCallsJson);
                }
            }
            else if (msg.Role == "tool") 
            {
                _currentContext.AddToolMessage(msg.Content, msg.ToolCallId);
            }
        }
    }

    public void UpdateContextTokensDisplay()
    {
        if (_tokenService == null || _promptService == null || _functionRegistry == null) return;
        
        // 赋予上下文准确的初始估算
        _currentContext.SetMainPersona(_promptService.GetPrompt(PromptType.MainPersona));
        _currentContext.ToolsDeclarationTokenCount = _functionRegistry.GetToolDeclarationTokenCount();
        
        int tokens = _currentContext.EstimatedTokenCount;

        _tokenService.CurrentTokens = tokens;
        
        OnPropertyChanged(nameof(ContextTokensInfo));
    }

    private void UpdateBubbleButtonVisibility()
    {
        foreach (var msg in Messages) { msg.CanEdit = false; msg.CanRegenerate = false; }
        if (IsSending || Messages.Count == 0) return;

        foreach (var msg in Messages)
        {
            if (!msg.IsCompressed)
            {
                if (msg.Role == "assistant") msg.CanRegenerate = true;
                if (msg.Role == "user") msg.CanEdit = true;
            }
        }
    }

    public async Task RefreshSettingsAsync()
    {
        if (_configService != null)
        {
            var config = await _configService.LoadAsync();
            if (_tokenService != null) _tokenService.MaxTokens = config.MaxContextTokens;
            UpdateContextTokensDisplay();
        }
    }

    public async Task InternalCompressContextAsync()
    {
        if (_historyService == null) return;
        var messagesList = Messages.ToList();
        var summary = await _historyService.CompressContextAsync(messagesList);
        if (summary != null)
        {
            UpdateConversationContext();
            UpdateContextTokensDisplay();
        }
    }

    public void InternalUndoCompression()
    {
        // To be implemented in history service
    }

    public async Task LoadHistoryConversationAsync(ConversationHistoryItem item)
    {
        if (_historyService == null) return;
        var history = await _historyService.LoadByIdAsync(item.Id);
        if (history != null)
        {
            Messages.Clear();
            _currentHistoryId = history.Id;
            
            if (history.Messages != null)
            {
                foreach (var msg in history.Messages)
                {
                    Messages.Add(msg);
                }
            }
            
            _initialMessageCount = Messages.Count;
            UpdateConversationContext();
            UpdateContextTokensDisplay();
        }
    }

    public void NotifyHistoryDeleted(string id)
    {
    }
}
