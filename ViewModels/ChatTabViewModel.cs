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
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

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
    private readonly ILocalizationService? _localizationService;
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
    [NotifyCanExecuteChangedFor(nameof(SendMessageCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartInlineEditCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmInlineEditCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelInlineEditCommand))]
    [NotifyCanExecuteChangedFor(nameof(RegenerateResponseCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteMessageCommand))]
    private bool _isCompressing;

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

    public ChatTabViewModel() : this(null, null, null, null, null, null, null, null) { }

    public ChatTabViewModel(
        IChatService? chatService,
        IConfigService? configService,
        IConversationHistoryService? historyService,
        IPromptService? promptService,
        ITaskScheduler? taskScheduler,
        IFunctionRegistry? functionRegistry,
        ITokenService? tokenService,
        ILocalizationService? localizationService)
    {
        _chatService = chatService;
        _configService = configService;
        _historyService = historyService;
        _promptService = promptService;
        _taskScheduler = taskScheduler;
        _functionRegistry = functionRegistry;
        _tokenService = tokenService;
        _localizationService = localizationService;

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

        // 在发送前检查是否需要压缩上下文
        if (_tokenService != null && _configService != null)
        {
            var config = _configService.Load();
            if (config.AutoCompress && _tokenService.CurrentTokens > config.CompressionThreshold && !IsCompressing)
            {
                _logger.Information("检测到 Token 超过阈值 ({Tokens} > {Threshold})，触发自动压缩", _tokenService.CurrentTokens, config.CompressionThreshold);
                await InternalCompressContextAsync();
            }
        }

        var userContent = InputText;
        InputText = string.Empty;

        Messages.Add(new ChatMessage { Role = "user", Content = userContent, Timestamp = DateTime.Now });
        await GetAiResponseAsync(userContent);
    }

    private bool CanSendMessage() => !IsSending && !IsCompressing && !string.IsNullOrWhiteSpace(InputText);

    private bool CanModifyMessages() => !IsSending && !IsCompressing;

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
        
        // 检查该消息后面是否有后续消息
        int msgIndex = Messages.IndexOf(message);
        bool hasSubsequentMessages = msgIndex >= 0 && msgIndex < Messages.Count - 1;
        
        if (hasSubsequentMessages)
        {
            // 检查用户偏好设置
            var config = _configService?.Load();
            if (config?.SkipEditConfirm == true)
            {
                // 用户选择了跳过确认，直接清理后续消息
                while (Messages.Count > msgIndex + 1)
                {
                    Messages.RemoveAt(msgIndex + 1);
                }
            }
            else
            {
                // 弹窗确认
                var vm = new ConfirmDialogViewModel
                {
                    Title = "编辑确认",
                    Message = "编辑此消息将删除后续所有消息并重新生成回答，是否继续？",
                    ConfirmText = "是",
                    CancelText = "否"
                };
                
                var dialog = new Views.ConfirmDialog(vm);
                await dialog.ShowDialog(GetMainWindow());
                
                if (vm.Result != true) return;
                
                // 如果用户勾选了"不再询问"，保存偏好
                if (vm.ShouldNotAskAgain && _configService != null)
                {
                    var cfg = await _configService.LoadAsync();
                    cfg.SkipEditConfirm = true;
                    await _configService.SaveAsync(cfg);
                }
                
                // 清理后续消息（与 Regenerate 相同的逻辑）
                while (Messages.Count > msgIndex + 1)
                {
                    Messages.RemoveAt(msgIndex + 1);
                }
            }
        }
        
        if (!string.IsNullOrWhiteSpace(newContent))
        {
            message.Content = newContent;
            message.IsEditing = false;
            
            if (hasSubsequentMessages)
            {
                // 编辑后重新生成
                UpdateConversationContext();
                await GetAiResponseAsync(message.Content, addToContext: false);
            }
            else
            {
                UpdateConversationContext();
            }
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

        // 检查是否是最新消息
        bool isNotLatest = msgIndex < Messages.Count - 1;
        
        if (isNotLatest)
        {
            // 检查用户偏好设置
            var config = _configService?.Load();
            if (config?.SkipRegenerateConfirm == true)
            {
                // 用户选择了跳过确认，直接清理后续消息
                // （清理逻辑在后面统一处理）
            }
            else
            {
                // 弹窗确认
                var vm = new ConfirmDialogViewModel
                {
                    Title = "重新生成确认",
                    Message = "重新生成将删除该消息之后的所有内容，是否继续？",
                    ConfirmText = "是",
                    CancelText = "否"
                };
                
                var dialog = new Views.ConfirmDialog(vm);
                await dialog.ShowDialog(GetMainWindow());
                
                if (vm.Result != true) return;
                
                // 如果用户勾选了"不再询问"，保存偏好
                if (vm.ShouldNotAskAgain && _configService != null)
                {
                    var cfg = await _configService.LoadAsync();
                    cfg.SkipRegenerateConfirm = true;
                    await _configService.SaveAsync(cfg);
                }
            }
        }

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
        
        int msgIndex = Messages.IndexOf(message);
        
        // 删除 tool 消息时，向上级联删除对应的 assistant tool_calls 消息
        if (message.Role == "tool")
        {
            // 向上找到对应的 assistant 消息
            for (int i = msgIndex - 1; i >= 0; i--)
            {
                if (Messages[i].Role == "assistant" && !string.IsNullOrEmpty(Messages[i].ToolCallsJson))
                {
                    // 删除这条 assistant 及其后所有 tool 消息
                    while (Messages.Count > i)
                    {
                        Messages.RemoveAt(i);
                    }
                    UpdateConversationContext();
                    UpdateContextTokensDisplay();
                    UpdateBubbleButtonVisibility();
                    return;
                }
            }
        }
        
        // 级联删除：如果删除的是带工具调用的助手消息，也要删除其后的工具结果
        if (message.Role == "assistant" && !string.IsNullOrEmpty(message.ToolCallsJson))
        {
            while (msgIndex + 1 < Messages.Count && Messages[msgIndex + 1].Role == "tool")
            {
                Messages.RemoveAt(msgIndex + 1);
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
            if (App.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) 
            {
                var clipboard = TopLevel.GetTopLevel(desktop.MainWindow)?.Clipboard;
                clipboard?.SetTextAsync(message.Content?? string.Empty);
                _logger.Debug("Copying message content to clipboard");
            }
        }
    }

    /// <summary>
    /// 获取主窗口用于弹窗
    /// </summary>
    private Window? GetMainWindow()
    {
        if (App.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            return desktop.MainWindow;
        }
        return null;
    }

    [RelayCommand]
    private void AttachFile()
    {
        // File attachment logic
    }

    /// <summary>
    /// 处理来自调度器的主动消息
    /// </summary>
    public async Task ProcessProactiveMessageAsync(string intent)
    {
        if (_chatService == null || _promptService == null || IsSending || IsCompressing)
        {
            _logger.Warning("忽略主动消息触发：当前正忙或服务未初始化 (IsSending={IsSending})", IsSending);
            return;
        }

        _logger.Information("开始处理主动消息逻辑: {Intent}", intent);

        // 构造主动触发指令
        var proactivePrompt = _promptService.GetProactiveMessagePrompt(intent, DateTime.Now);
        
        // 重要：为了绕过大多数 LLM API 不允许以 System 消息结尾或纯 System 消息序列的限制，
        // 我们将主动指令作为一条“隐藏的用户消息”注入。
        var triggerMsg = new ChatMessage
        {
            Role = "user",
            Content = proactivePrompt,
            IsHidden = true, // 在 UI 中不可见
            Timestamp = DateTime.Now
        };
        
        Messages.Add(triggerMsg);

        // 确保上下文包含这条新消息
        UpdateConversationContext();

        // 触发 AI 响应（addToContext 为 false 因为我们已经手动添加到 Messages 列表并更新了 Context）
        await GetAiResponseAsync(string.Empty, addToContext: false);
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
                                var callingTemplate = _localizationService?.GetString("Tool.Calling") ?? "Calling: {0}...";
                                assistantMsg.ToolExecutionSummary = string.Format(callingTemplate, string.Join(", ", names));
                            }
                            else assistantMsg.ToolExecutionSummary = _localizationService?.GetString("Tool.CallingTool") ?? "Calling tool...";
                        }
                        catch { assistantMsg.ToolExecutionSummary = _localizationService?.GetString("Tool.CallingTool") ?? "Calling tool..."; }
                    }
                    else if (msg.Role == "tool")
                    {
                        Messages.Add(msg);

                        // 工具执行完毕，等待大模型下一步指示
                        var defaultToolName = _localizationService?.GetString("Tool.DefaultName") ?? "Tool";
                        var name = string.IsNullOrEmpty(msg.ToolName) ? defaultToolName : msg.ToolName;
                        var completeTemplate = _localizationService?.GetString("Tool.CallComplete") ?? "{0} completed, continuing...";
                        assistantMsg.ToolExecutionSummary = string.Format(completeTemplate, name);
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

            // 回复结束触发图标闪烁
            if (string.IsNullOrEmpty(assistantMsg.ToolCallsJson) && !string.IsNullOrEmpty(assistantMsg.Content))
            {
                App.StartTrayFlashing();
            }
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
        
        // 赋予当前的压缩摘要（如果有）
        if (_tokenService != null && !string.IsNullOrEmpty(_tokenService.CompressionPreview))
        {
            _currentContext.SetSummary(_tokenService.CompressionPreview);
        }
        else
        {
            _currentContext.SetSummary(null);
        }

        foreach (var msg in Messages)
        {
            // 已被压缩归档的消息不再进入发送给大模型的 context.messages 列表
            if (msg.IsCompressed) continue;

            if (msg.Role == "user") 
            {
                _currentContext.AddUserMessage(msg.Content);
            }
            else if (msg.Role == "assistant") 
            {
                // 仅添加有内容的助手消息
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
        foreach (var msg in Messages) 
        { 
            msg.CanEdit = false; 
            msg.CanRegenerate = false; 
        }
        
        // 发送中或压缩中，所有操作按钮不可用
        if (IsSending || IsCompressing || Messages.Count == 0) return;

        foreach (var msg in Messages)
        {
            // 已归档的消息不可编辑或重新生成
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
        IsCompressing = true;
        try
        {
            var messagesList = Messages.ToList();
            var summary = await _historyService.CompressContextAsync(messagesList);
            if (summary != null)
            {
                // 更新 TokenService 中的预览，让用户在设置页能看到
                if (_tokenService != null)
                {
                    _tokenService.CompressionPreview = summary;
                }

                // 更新对话上下文并重新计算 Token
                UpdateConversationContext();
                UpdateContextTokensDisplay();

                _logger.Information("UI 上下文压缩显示已更新");
            }
        }
        finally
        {
            IsCompressing = false;
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
