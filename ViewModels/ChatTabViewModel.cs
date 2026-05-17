using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.ClientModel;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using Athena.UI.Services;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System.Collections.Specialized;

namespace Athena.UI.ViewModels;

public partial class ChatTabViewModel : ViewModelBase
{
    private enum TransitionStageResult
    {
        NotNeeded,
        Staged,
        Failed
    }

    private readonly IChatService? _chatService;
    private readonly IConfigService? _configService;
    private readonly IConversationHistoryService? _historyService;
    private readonly IPromptService? _promptService;
    private readonly ITaskScheduler? _taskScheduler;
    private readonly IFunctionRegistry? _functionRegistry;
    private readonly ITokenService? _tokenService;
    private readonly ILocalizationService? _localizationService;
    private readonly IAttachmentStoreService? _attachmentStoreService;
    private readonly IAudioPlaybackService? _audioPlaybackService;
    private readonly IConversationArchiveService? _archiveService;
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
    [NotifyCanExecuteChangedFor(nameof(StopResponseCommand))]
    private bool _isSending;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NewConversationCommand))]
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
    [NotifyPropertyChangedFor(nameof(HasAttachmentStatusMessage))]
    private string _attachmentStatusMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBackgroundArchiveStatusMessage))]
    [NotifyPropertyChangedFor(nameof(HasBackgroundArchiveNeutralStatus))]
    [NotifyPropertyChangedFor(nameof(HasBackgroundArchiveErrorStatus))]
    private string _backgroundArchiveStatusMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBackgroundArchiveNeutralStatus))]
    [NotifyPropertyChangedFor(nameof(HasBackgroundArchiveErrorStatus))]
    private bool _isBackgroundArchiveError;

    [ObservableProperty]
    private string _currentTheme = "Dark";

    [ObservableProperty]
    private string _themeIcon = "Moon"; // "Moon"=当前Dark点一下切Light, "Sun"=当前Light点一下切Dark

    public ObservableCollection<ChatMessage> Messages { get; } = new();

    public ObservableCollection<ChatAttachment> PendingAttachments { get; } = new();

    public bool HasPendingAttachments => PendingAttachments.Count > 0;

    public bool HasAttachmentStatusMessage => !string.IsNullOrWhiteSpace(AttachmentStatusMessage);

    public bool HasBackgroundArchiveStatusMessage => !string.IsNullOrWhiteSpace(BackgroundArchiveStatusMessage);

    public bool HasBackgroundArchiveNeutralStatus => HasBackgroundArchiveStatusMessage && !IsBackgroundArchiveError;

    public bool HasBackgroundArchiveErrorStatus => HasBackgroundArchiveStatusMessage && IsBackgroundArchiveError;

    public string ContextTokensInfo => _tokenService?.TokenInfoText ?? "0 / 0 tokens";

    public ITokenService? TokenService => _tokenService;

    public string InputPlaceholder => "Chat.InputPlaceholder";

    public event EventHandler? SwitchToTasksTabRequested;

    private ConversationContext _currentContext = new();
    private CancellationTokenSource? _responseCts;
    private readonly SemaphoreSlim _conversationTransitionLock = new(1, 1);
    private int _conversationEpoch;

    // 记录当前加载的历史对话 ID，如果是新对话则为空
    private string? _currentHistoryId;

    // 记录加载历史时的初始签名，用于判断是否发生了修改
    private string? _initialConversationSignature;

    private DateTime _latestArchiveCaptureAt = DateTime.MinValue;

    public ChatTabViewModel() : this(null, null, null, null, null, null, null, null, null, null, null) { }

    public ChatTabViewModel(
        IChatService? chatService,
        IConfigService? configService,
        IConversationHistoryService? historyService,
        IPromptService? promptService,
        ITaskScheduler? taskScheduler,
        IFunctionRegistry? functionRegistry,
        ITokenService? tokenService,
        ILocalizationService? localizationService,
        IAttachmentStoreService? attachmentStoreService = null,
        IAudioPlaybackService? audioPlaybackService = null,
        IConversationArchiveService? archiveService = null)
    {
        _chatService = chatService;
        _configService = configService;
        _historyService = historyService;
        _promptService = promptService;
        _taskScheduler = taskScheduler;
        _functionRegistry = functionRegistry;
        _tokenService = tokenService;
        _localizationService = localizationService;
        _attachmentStoreService = attachmentStoreService;
        _audioPlaybackService = audioPlaybackService;
        _archiveService = archiveService;

        // Initialize from config
        if (_configService != null)
        {
            var config = _configService.Load();
            if (_tokenService != null) _tokenService.MaxTokens = config.MaxContextTokens;
            CurrentTheme = config.Theme;
            ThemeIcon = config.Theme == "Dark" ? "Moon" : "Sun";
        }

        // 监听全局主题变更（来自 ConfigTabView 或其他入口），同步按钮状态
        App.ThemeChanged += theme =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                CurrentTheme = theme;
                ThemeIcon = theme == "Dark" ? "Moon" : "Sun";
            });
        };

        Messages.CollectionChanged += (s, e) =>
        {
            UpdateContextTokensDisplay();
            UpdateBubbleButtonVisibility();
        };

        PendingAttachments.CollectionChanged += (s, e) =>
        {
            OnPropertyChanged(nameof(HasPendingAttachments));
            SendMessageCommand.NotifyCanExecuteChanged();
        };

        // 计算初始 Token（系统提示词和工具声明的基底开销）
        UpdateContextTokensDisplay();

        if (_archiveService != null)
        {
            _archiveService.ArchiveCompleted += OnArchiveCompleted;
            _archiveService.ArchiveFailed += OnArchiveFailed;
        }

        if (_audioPlaybackService != null)
        {
            _audioPlaybackService.PlaybackStateChanged += OnPlaybackStateChanged;
        }

        RestoreDraftIfNeeded();
    }

    [RelayCommand(CanExecute = nameof(CanSendMessage))]
    private async Task SendMessageAsync()
    {
        if (string.IsNullOrWhiteSpace(InputText) && PendingAttachments.Count == 0) return;

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
        var attachments = PendingAttachments.Select(CloneAttachmentForMessage).ToList();
        InputText = string.Empty;
        PendingAttachments.Clear();
        AttachmentStatusMessage = string.Empty;

        Messages.Add(new ChatMessage
        {
            Role = "user",
            Content = userContent,
            Attachments = new ObservableCollection<ChatAttachment>(attachments),
            Timestamp = DateTime.Now
        });

        UpdateConversationContext();
        await GetAiResponseAsync(userContent, addToContext: false);
    }

    private bool CanSendMessage() => !IsSending && !IsCompressing && (!string.IsNullOrWhiteSpace(InputText) || PendingAttachments.Count > 0);

    private bool CanStopResponse() => IsSending;

    private bool CanModifyMessages() => !IsSending && !IsCompressing;

    [RelayCommand(CanExecute = nameof(CanStopResponse))]
    private void StopResponse()
    {
        if (!IsSending) return;
        _logger.Information("用户请求停止当前回复");
        _responseCts?.Cancel();
    }

    [RelayCommand]
    private void ToggleTheme()
    {
        CurrentTheme = CurrentTheme == "Dark" ? "Light" : "Dark";
        ThemeIcon = CurrentTheme == "Dark" ? "Moon" : "Sun";
        App.SetTheme(CurrentTheme);
        if (_configService != null)
        {
            var config = _configService.Load();
            config.Theme = CurrentTheme;
            _ = _configService.SaveAsync(config);
        }
    }

    private bool CanStartNewConversation() => !IsResetting;

    [RelayCommand(CanExecute = nameof(CanStartNewConversation))]
    private async Task NewConversationAsync()
    {
        await _conversationTransitionLock.WaitAsync();
        try
        {
            IsResetting = true;
            BeginConversationTransition();

            var stagedSnapshot = await TryStageCurrentConversationForTransitionAsync();
            if (stagedSnapshot == TransitionStageResult.Failed)
            {
                return;
            }

            ResetConversationState();
        }
        finally
        {
            IsResetting = false;
            _conversationTransitionLock.Release();
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
                    DeleteMessageAttachments(Messages[msgIndex + 1]);
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
                var owner = GetMainWindow();
                if (owner == null) return;
                await dialog.ShowDialog(owner);

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
                    DeleteMessageAttachments(Messages[msgIndex + 1]);
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
                var owner = GetMainWindow();
                if (owner == null) return;
                await dialog.ShowDialog(owner);

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
            DeleteMessageAttachments(Messages[lastUserIndex + 1]);
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
                        DeleteMessageAttachments(Messages[i]);
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
                DeleteMessageAttachments(Messages[msgIndex + 1]);
                Messages.RemoveAt(msgIndex + 1);
            }
        }

        DeleteMessageAttachments(message);
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

    [RelayCommand]
    private async Task OpenImageAttachmentAsync(ChatAttachment? attachment)
    {
        if (attachment == null || !attachment.IsImage || string.IsNullOrWhiteSpace(attachment.StoredPath))
        {
            return;
        }

        var owner = GetMainWindow();
        if (owner == null)
        {
            return;
        }

        var window = new Views.ImagePreviewWindow(attachment);
        await window.ShowDialog(owner);
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
    private async Task AttachFileAsync()
    {
        if (_attachmentStoreService == null)
        {
            AttachmentStatusMessage = GetString("Chat.Attach.ServiceUnavailable", "Attachment service is unavailable.");
            return;
        }

        var owner = GetMainWindow();
        var storageProvider = owner?.StorageProvider;
        if (storageProvider == null)
        {
            AttachmentStatusMessage = GetString("Chat.Attach.NoStorageProvider", "File picker is unavailable.");
            return;
        }

        var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = GetString("Chat.Attach.SelectImages", "Select images"),
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType(GetString("Chat.Attach.ImageFiles", "Image files"))
                {
                    Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.gif"],
                    MimeTypes = ["image/png", "image/jpeg", "image/webp", "image/gif"],
                    AppleUniformTypeIdentifiers = ["public.png", "public.jpeg", "org.webmproject.webp", "com.compuserve.gif"]
                }
            ]
        });

        await AddStorageFilesAsync(files);
    }

    [RelayCommand]
    private void RemovePendingAttachment(ChatAttachment? attachment)
    {
        if (attachment == null) return;

        if (PendingAttachments.Remove(attachment))
        {
            _attachmentStoreService?.DeleteStoredAttachment(attachment);
            AttachmentStatusMessage = string.Empty;
        }
    }

    public async Task AddStorageFilesAsync(IEnumerable<IStorageFile> files)
    {
        if (_attachmentStoreService == null) return;

        var available = _attachmentStoreService.MaxPendingAttachments - PendingAttachments.Count;
        if (available <= 0)
        {
            AttachmentStatusMessage = string.Format(
                GetString("Chat.Attach.MaxCount", "You can attach up to {0} files."),
                _attachmentStoreService.MaxPendingAttachments);
            return;
        }

        try
        {
            var allFiles = files.ToList();
            var selected = allFiles.Take(available).ToList();
            var imported = await _attachmentStoreService.ImportFilesAsync(selected);
            foreach (var attachment in imported)
            {
                PendingAttachments.Add(attachment);
            }

            AttachmentStatusMessage = allFiles.Count > selected.Count
                ? string.Format(
                    GetString("Chat.Attach.MaxCount", "You can attach up to {0} files."),
                    _attachmentStoreService.MaxPendingAttachments)
                : string.Empty;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "添加附件失败");
            AttachmentStatusMessage = ToAttachmentErrorMessage(ex);
        }
    }

    public async Task AddClipboardBitmapAsync(Bitmap bitmap)
    {
        if (_attachmentStoreService == null) return;

        if (PendingAttachments.Count >= _attachmentStoreService.MaxPendingAttachments)
        {
            AttachmentStatusMessage = string.Format(
                GetString("Chat.Attach.MaxCount", "You can attach up to {0} files."),
                _attachmentStoreService.MaxPendingAttachments);
            return;
        }

        try
        {
            var attachment = await _attachmentStoreService.ImportBitmapAsync(
                bitmap,
                $"clipboard-{DateTime.Now:yyyyMMdd-HHmmss}.png");
            PendingAttachments.Add(attachment);
            AttachmentStatusMessage = string.Empty;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "粘贴图片失败");
            AttachmentStatusMessage = ToAttachmentErrorMessage(ex);
        }
    }

    /// <summary>
    /// 处理来自调度器的主动消息
    /// </summary>
    public async Task<TaskExecutionResult> ProcessProactiveMessageAsync(string intent)
    {
        if (_chatService == null || _promptService == null)
        {
            _logger.Warning("忽略主动消息触发：服务未初始化");
            return TaskExecutionResult.Failed("Chat service or prompt service is not available.");
        }

        if (IsSending || IsCompressing)
        {
            _logger.Warning("延后主动消息触发：当前正忙 (IsSending={IsSending}, IsCompressing={IsCompressing})", IsSending, IsCompressing);
            return TaskExecutionResult.Busy("Foreground chat is busy.");
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
        return await GetAiResponseAsync(string.Empty, addToContext: false);
    }

    private void BeginConversationTransition()
    {
        Interlocked.Increment(ref _conversationEpoch);
        _responseCts?.Cancel();
        FinalizePendingAssistantMessages();
        IsSending = false;
        UpdateConversationContext();
        UpdateContextTokensDisplay();
        UpdateBubbleButtonVisibility();
    }

    private ConversationArchiveSnapshot? CaptureArchiveSnapshotIfNeeded()
    {
        if (!IsConversationModified())
        {
            return null;
        }

        var messages = Messages
            .Where(ConversationPersistenceHelper.ShouldPersistMessage)
            .Select(ConversationPersistenceHelper.CloneMessage)
            .ToList();

        if (messages.Count == 0)
        {
            return null;
        }

        return new ConversationArchiveSnapshot
        {
            HistoryId = _currentHistoryId,
            ContextSummary = _tokenService?.CompressionPreview,
            Messages = messages,
            CapturedAt = DateTime.Now,
            ForceGenerateSummary = true
        };
    }

    private void ResetConversationState()
    {
        Messages.Clear();
        InputText = string.Empty;
        ClearPendingAttachments(deleteStoredFiles: true);
        _currentContext.Reset();
        _currentHistoryId = null;
        _initialConversationSignature = null;

        if (_tokenService != null)
        {
            _tokenService.CompressionPreview = string.Empty;
        }

        _historyService?.DeleteDraft();
        UpdateConversationContext();
        UpdateContextTokensDisplay();
        UpdateBubbleButtonVisibility();
    }

    private void FinalizePendingAssistantMessages()
    {
        foreach (var message in Messages.Where(m => m.Role == "assistant" && m.IsLoading).ToList())
        {
            var shouldRemove = !ConversationPersistenceHelper.ShouldPersistMessage(message);
            message.IsLoading = false;
            message.ToolExecutionSummary = string.Empty;

            if (shouldRemove)
            {
                DeleteMessageAttachments(message);
                Messages.Remove(message);
            }
        }
    }

    private bool IsCurrentConversationEpoch(int epoch)
    {
        return epoch == Volatile.Read(ref _conversationEpoch);
    }

    private async Task<TaskExecutionResult> GetAiResponseAsync(string input, bool addToContext = true)
    {
        if (_chatService == null)
        {
            return TaskExecutionResult.Failed("Chat service is not available.");
        }

        var epoch = Volatile.Read(ref _conversationEpoch);
        _responseCts?.Dispose();
        var responseCts = new CancellationTokenSource();
        _responseCts = responseCts;
        var cancellationToken = responseCts.Token;
        var requestContext = _currentContext.Clone();
        var outcome = TaskExecutionResult.Succeeded();

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
                requestContext,
                cancellationToken: cancellationToken,
                onMessageAdded: msg => {
                    if (!IsCurrentConversationEpoch(epoch))
                    {
                        return;
                    }

                    if (msg.Role == "assistant" && !string.IsNullOrEmpty(msg.ToolCallsJson))
                    {
                        // 真实的工具调用回合保存在隐藏消息里；主气泡保留已经流式显示给用户的正文，
                        // 仅追加工具执行状态，避免正文在流结束后突然消失。
                        msg.IsHidden = true;
                        Messages.Add(msg);

                        // Update the main bubble's status
                        assistantMsg.IsLoading = false;
                        assistantMsg.ReasoningContent = null;
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
                    else if (msg.Role == "assistant" && !string.IsNullOrEmpty(msg.ReasoningContent))
                    {
                        assistantMsg.ReasoningContent = msg.ReasoningContent;
                    }
                    else if (msg.Role == "assistant" && msg.Attachments.Count > 0)
                    {
                        foreach (var attachment in msg.Attachments.Select(CloneAttachmentForMessage))
                        {
                            assistantMsg.Attachments.Add(attachment);
                        }

                        if (!string.IsNullOrWhiteSpace(msg.OutputAudioReferenceId))
                        {
                            assistantMsg.OutputAudioReferenceId = msg.OutputAudioReferenceId;
                        }

                        assistantMsg.IsLoading = false;
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
                onContextCompressed: (summary, count) => {
                    if (!IsCurrentConversationEpoch(epoch))
                    {
                        return;
                    }

                    // 同步 UI 消息状态：标记前 count 条当前未压缩的消息为已压缩
                    int marked = 0;
                    foreach (var m in Messages)
                    {
                        if (!m.IsCompressed)
                        {
                            m.IsCompressed = true;
                            marked++;
                            if (marked >= count) break;
                        }
                    }
                    if (_tokenService != null) _tokenService.CompressionPreview = summary;
                    UpdateContextTokensDisplay();
                    _logger.Information("检测到中间压缩，UI 已同步标记 {Count} 条消息", count);
                },
                addToContext: addToContext))
            {
                if (!IsCurrentConversationEpoch(epoch))
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(contentDelta))
                {
                    assistantMsg.IsLoading = false; // 收到文字后停止 loading 动画
                    assistantMsg.ToolExecutionSummary = string.Empty; // 开始输出正式回复，隐藏工具调用状态
                    assistantMsg.Content += contentDelta;
                }
            }

            if (!IsCurrentConversationEpoch(epoch))
            {
                return TaskExecutionResult.Interrupted("Conversation context changed.");
            }

            UpdateConversationContext();

            // 回复结束触发图标闪烁
            if (string.IsNullOrEmpty(assistantMsg.ToolCallsJson) && !string.IsNullOrEmpty(assistantMsg.Content))
            {
                App.StartTrayFlashing();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (IsCurrentConversationEpoch(epoch))
            {
                _logger.Information("当前回复已停止");
            }
            outcome = TaskExecutionResult.Interrupted("Response was interrupted.");
        }
        catch (Exception ex)
        {
            if (!IsCurrentConversationEpoch(epoch))
            {
                return TaskExecutionResult.Interrupted("Conversation context changed.");
            }

            _logger.Error(ex, "Get AI response failed");
            assistantMsg.IsLoading = false;
            assistantMsg.ToolExecutionSummary = string.Empty;
            assistantMsg.Content = ToChatErrorMessage(ex);
            outcome = TaskExecutionResult.Failed(ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_responseCts, responseCts))
            {
                _responseCts = null;
            }

            responseCts.Dispose();

            if (IsCurrentConversationEpoch(epoch))
            {
                assistantMsg.IsLoading = false;
                assistantMsg.ToolExecutionSummary = string.Empty;

                // Cleanup the empty main assistant message if it didn't generate any text and didn't call tools directly
                if (string.IsNullOrWhiteSpace(assistantMsg.Content)
                    && string.IsNullOrEmpty(assistantMsg.ToolCallsJson)
                    && string.IsNullOrEmpty(assistantMsg.ReasoningContent))
                {
                    if (assistantMsg.Attachments.Count == 0)
                    {
                        Messages.Remove(assistantMsg);
                    }
                }

                UpdateConversationContext();
                IsSending = false;
                UpdateContextTokensDisplay();
                UpdateBubbleButtonVisibility();
            }

            await NotifySchedulerAvailabilityAsync();
        }

        return outcome;
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
                _currentContext.AddUserMessage(msg.Content, msg.Timestamp, msg.Attachments);
            }
            else if (msg.Role == "assistant")
            {
                // 仅添加有内容的助手消息
                if (!string.IsNullOrEmpty(msg.Content)
                    || !string.IsNullOrEmpty(msg.ToolCallsJson)
                    || !string.IsNullOrEmpty(msg.ReasoningContent)
                    || msg.Attachments.Count > 0
                    || !string.IsNullOrEmpty(msg.OutputAudioReferenceId))
                {
                    _currentContext.AddAssistantMessage(
                        msg.Content,
                        msg.ToolCallsJson,
                        msg.ReasoningContent,
                        msg.Attachments,
                        msg.OutputAudioReferenceId);
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
        var config = _configService?.Load();
        var functionCallingEnabled = config?.EnableFunctionCalling == true && _functionRegistry.HasFunctions;

        // 赋予上下文准确的初始估算
        var systemPrompt = _promptService.GetPrompt(PromptType.MainPersona);
        if (functionCallingEnabled)
        {
            systemPrompt = _promptService.GetPrompt(PromptType.ToolCallingPolicy) + "\n\n---\n\n" + systemPrompt;
        }

        _currentContext.SetMainPersona(systemPrompt);
        _currentContext.ToolsDeclarationTokenCount = functionCallingEnabled
            ? _functionRegistry.GetToolDeclarationTokenCount()
            : 0;

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
        if (_historyService == null || _configService == null) return;

        var config = _configService.Load();
        IsCompressing = true;
        try
        {
            var messagesList = Messages.ToList();
            var (summary, _) = await _historyService.CompressContextAsync(messagesList, config.KeepRecentRounds);
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

                _logger.Information("UI 上下文压缩显示已更新（按轮次压缩）");
            }
        }
        finally
        {
            IsCompressing = false;
            await NotifySchedulerAvailabilityAsync();
        }
    }

    private async Task NotifySchedulerAvailabilityAsync()
    {
        if (_taskScheduler == null || IsSending || IsCompressing)
        {
            return;
        }

        try
        {
            await _taskScheduler.RunDueTasksAsync();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "通知任务调度器重新检查到期任务失败");
        }
    }

    public void InternalUndoCompression()
    {
        // To be implemented in history service
    }

    public async Task LoadHistoryConversationAsync(ConversationHistoryItem item)
    {
        if (_historyService == null || item.IsArchivePlaceholder) return;

        await _conversationTransitionLock.WaitAsync();
        try
        {
            IsResetting = true;
            BeginConversationTransition();

            var stagedSnapshot = await TryStageCurrentConversationForTransitionAsync();
            if (stagedSnapshot == TransitionStageResult.Failed)
            {
                return;
            }

            var history = await _historyService.LoadByIdAsync(item.Id);
            if (history == null)
            {
                _logger.Warning("未找到要加载的历史对话: {Id}", item.Id);
                return;
            }

            ResetConversationState();
            _currentHistoryId = history.Id;
            if (_tokenService != null)
            {
                _tokenService.CompressionPreview = history.ContextSummary ?? string.Empty;
            }

            if (history.Messages != null)
            {
                foreach (var msg in history.Messages)
                {
                    ConversationPersistenceHelper.PrepareRestoredMessage(msg);
                    if (_attachmentStoreService != null)
                    {
                        await _attachmentStoreService.LoadPreviewsAsync(msg.Attachments);
                    }

                    Messages.Add(msg);
                }
            }

            _initialConversationSignature = CreateConversationSignature();
            UpdateConversationContext();
            UpdateContextTokensDisplay();
            UpdateBubbleButtonVisibility();
        }
        finally
        {
            IsResetting = false;
            _conversationTransitionLock.Release();
        }
    }

    private async Task<TransitionStageResult> TryStageCurrentConversationForTransitionAsync()
    {
        var snapshot = CaptureArchiveSnapshotIfNeeded();
        if (snapshot == null)
        {
            return TransitionStageResult.NotNeeded;
        }

        if (_archiveService == null)
        {
            SetBackgroundArchiveStatus(GetString("Chat.Archive.ErrorUnavailable", "Background archive service is unavailable."), isError: true);
            return TransitionStageResult.Failed;
        }

        try
        {
            _latestArchiveCaptureAt = snapshot.CapturedAt;
            await _archiveService.StageArchiveAsync(snapshot);
            SetBackgroundArchiveStatus(
                GetString("Chat.Archive.Saving", "Previous conversation is being saved in the background."),
                isError: false);
            return TransitionStageResult.Staged;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "写入待归档队列失败");
            SetBackgroundArchiveStatus(
                string.Format(
                    GetString("Chat.Archive.StageFailed", "Failed to queue the previous conversation: {0}"),
                    ex.Message),
                isError: true);
            return TransitionStageResult.Failed;
        }
    }

    public void NotifyHistoryDeleted(string id)
    {
        if (string.Equals(_currentHistoryId, id, StringComparison.Ordinal))
        {
            _currentHistoryId = null;
            _initialConversationSignature = null;
        }
    }

    public void PersistDraft()
    {
        if (_historyService == null) return;

        if (!HasConversationStateToPersist() || !IsConversationModified())
        {
            _historyService.DeleteDraft();
            return;
        }

        var snapshot = new ConversationDraftSnapshot
        {
            CurrentHistoryId = _currentHistoryId,
            InitialConversationSignature = _initialConversationSignature,
            ContextSummary = _tokenService?.CompressionPreview,
            Messages = Messages
                .Where(ConversationPersistenceHelper.ShouldPersistMessage)
                .Select(ConversationPersistenceHelper.CloneMessage)
                .ToList(),
            UpdatedAt = DateTime.Now
        };

        if (snapshot.Messages.Count == 0 && string.IsNullOrWhiteSpace(snapshot.ContextSummary))
        {
            _historyService.DeleteDraft();
            return;
        }

        _historyService.SaveDraft(snapshot);
    }

    private void RestoreDraftIfNeeded()
    {
        if (_historyService == null) return;

        var snapshot = _historyService.LoadDraft();
        if (snapshot == null)
        {
            return;
        }

        if ((snapshot.Messages == null || snapshot.Messages.Count == 0) && string.IsNullOrWhiteSpace(snapshot.ContextSummary))
        {
            _historyService.DeleteDraft();
            return;
        }

        Messages.Clear();
        _currentHistoryId = snapshot.CurrentHistoryId;
        _initialConversationSignature = snapshot.InitialConversationSignature;

        if (_tokenService != null)
        {
            _tokenService.CompressionPreview = snapshot.ContextSummary ?? string.Empty;
        }

        if (snapshot.Messages != null)
        {
            foreach (var msg in snapshot.Messages)
            {
                ConversationPersistenceHelper.PrepareRestoredMessage(msg);
                _ = _attachmentStoreService?.LoadPreviewsAsync(msg.Attachments);
                Messages.Add(msg);
            }
        }

        UpdateConversationContext();
        UpdateContextTokensDisplay();
        UpdateBubbleButtonVisibility();
        _logger.Information("已恢复主对话草稿，消息数: {Count}", Messages.Count);
    }

    private bool HasConversationStateToPersist()
    {
        return Messages.Any(ConversationPersistenceHelper.ShouldPersistMessage)
            || !string.IsNullOrWhiteSpace(_tokenService?.CompressionPreview);
    }

    private bool IsConversationModified()
    {
        if (!HasConversationStateToPersist())
        {
            return false;
        }

        if (string.IsNullOrEmpty(_currentHistoryId))
        {
            return true;
        }

        return !string.Equals(_initialConversationSignature, CreateConversationSignature(), StringComparison.Ordinal);
    }

    private string CreateConversationSignature()
    {
        var signatureModel = new
        {
            ContextSummary = _tokenService?.CompressionPreview ?? string.Empty,
            Messages = Messages
                .Where(ConversationPersistenceHelper.ShouldPersistMessage)
                .Select(msg => new
                {
                    msg.Role,
                    msg.Content,
                    msg.Timestamp,
                    msg.ToolCallId,
                    msg.ToolCallsJson,
                    msg.ReasoningContent,
                    msg.IsCompressed,
                    Attachments = msg.Attachments.Select(a => new
                    {
                        a.Id,
                        a.Kind,
                        a.FileName,
                        a.StoredPath,
                        a.MimeType,
                        a.SizeBytes,
                        a.Width,
                        a.Height
                    }).ToList()
                })
                .ToList()
        };

        return JsonSerializer.Serialize(signatureModel);
    }

    private void ClearPendingAttachments(bool deleteStoredFiles)
    {
        if (deleteStoredFiles)
        {
            foreach (var attachment in PendingAttachments.ToList())
            {
                _attachmentStoreService?.DeleteStoredAttachment(attachment);
            }
        }

        PendingAttachments.Clear();
        AttachmentStatusMessage = string.Empty;
    }

    private void OnArchiveCompleted(object? sender, ConversationArchiveResultEventArgs e)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            if (e.Snapshot.CapturedAt != _latestArchiveCaptureAt)
            {
                return;
            }

            await Task.Delay(2500);
            if (e.Snapshot.CapturedAt == _latestArchiveCaptureAt && !IsBackgroundArchiveError)
            {
                BackgroundArchiveStatusMessage = string.Empty;
                IsBackgroundArchiveError = false;
            }
        });
    }

    private void OnArchiveFailed(object? sender, ConversationArchiveResultEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (e.Snapshot.CapturedAt != _latestArchiveCaptureAt)
            {
                return;
            }

            SetBackgroundArchiveStatus(
                GetString("Chat.Archive.RetryLater", "Previous conversation save failed. It has been kept for retry."),
                isError: true);
        });
    }

    private void SetBackgroundArchiveStatus(string message, bool isError)
    {
        BackgroundArchiveStatusMessage = message;
        IsBackgroundArchiveError = isError;
    }

    private string GetString(string key, string defaultValue)
    {
        return _localizationService?.GetString(key, defaultValue) ?? defaultValue;
    }

    private string ToAttachmentErrorMessage(Exception ex)
    {
        if (ex.Message.Contains("too large", StringComparison.OrdinalIgnoreCase))
        {
            return GetString("Chat.Attach.ErrorTooLarge", "One of the selected images is too large.");
        }

        if (ex.Message.Contains("Unsupported", StringComparison.OrdinalIgnoreCase))
        {
            return GetString("Chat.Attach.ErrorUnsupported", "Only PNG, JPG, WEBP, and GIF images are supported for now.");
        }

        return string.Format(GetString("Chat.Attach.ErrorGeneric", "Failed to add attachment: {0}"), ex.Message);
    }

    private string ToChatErrorMessage(Exception ex)
    {
        if (_currentContext.Messages.Any(m => m.Attachments.Any(a => a.Kind == AttachmentKind.Image))
            && IsLikelyImageInputFailure(ex))
        {
            return GetString(
                "Chat.Error.ImageUnsupported",
                "The current model or endpoint does not support image input. Please switch the main model to a vision-capable model and try again.");
        }

        return $"Error: {ex.Message}";
    }

    private static bool IsLikelyImageInputFailure(Exception ex)
    {
        if (ex is ClientResultException clientException
            && clientException.Status is 400 or 415 or 422)
        {
            return true;
        }

        return ex.Message.Contains("image", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("vision", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("modal", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("unsupported", StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand]
    private void PlayAudioAttachment(ChatAttachment? attachment)
    {
        if (attachment == null || !attachment.IsAudio || _audioPlaybackService == null)
        {
            return;
        }

        if (_audioPlaybackService.Play(attachment))
        {
            UpdateAudioAttachmentStates(attachment.Id, true, attachment.Position, attachment.Duration);
        }
    }

    [RelayCommand]
    private void PauseAudioAttachment(ChatAttachment? attachment)
    {
        if (attachment == null || !attachment.IsAudio || _audioPlaybackService == null)
        {
            return;
        }

        _audioPlaybackService.Pause();
        UpdateAudioAttachmentStates(attachment.Id, false, attachment.Position, attachment.Duration);
    }

    [RelayCommand]
    private void SeekAudioAttachment(ChatAttachment? attachment)
    {
        if (attachment == null || !attachment.IsAudio || _audioPlaybackService == null)
        {
            return;
        }

        _audioPlaybackService.Seek(attachment.Position);
    }

    private void OnPlaybackStateChanged(object? sender, AudioPlaybackStateChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            UpdateAudioAttachmentStates(e.AttachmentId, e.IsPlaying, e.Position, e.Duration);
        });
    }

    private void UpdateAudioAttachmentStates(string? attachmentId, bool isPlaying, TimeSpan position, TimeSpan duration)
    {
        foreach (var attachment in Messages.SelectMany(m => m.Attachments).Where(a => a.IsAudio))
        {
            var selected = attachment.Id == attachmentId;
            attachment.IsPlaying = selected && isPlaying;
            if (selected)
            {
                if (duration > TimeSpan.Zero)
                {
                    attachment.Duration = duration;
                }

                attachment.Position = position;
            }
            else if (isPlaying)
            {
                attachment.IsPlaying = false;
            }
        }
    }

    private void DeleteMessageAttachments(ChatMessage message)
    {
        if (_attachmentStoreService == null)
        {
            return;
        }

        foreach (var attachment in message.Attachments.ToList())
        {
            _attachmentStoreService.DeleteStoredAttachment(attachment);
        }
    }

    private static ChatAttachment CloneAttachmentForMessage(ChatAttachment attachment)
    {
        return ConversationPersistenceHelper.CloneAttachment(attachment);
    }
}
