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
    private readonly ISystemAudioService? _systemAudioService;
    // Cancels in-flight system (afplay/aplay/powershell) playback so Stop can
    // actually terminate the external process — pausing the libvlc player does
    // nothing for system-provider audio.
    private CancellationTokenSource? _systemAudioCts;
    // Tracks which attachment is currently playing. When a new clip starts, a
    // new ID is written. The stale finally block of the *previous* clip checks
    // this before clearing state — preventing it from undoing the new clip's
    // IsPlaying=true.
    private string? _playingAttachmentId;
    private readonly IConversationArchiveService? _archiveService;
    private readonly IImageGenerationSessionService? _imageGenerationSessionService;
    private readonly IDocumentParserService? _documentParserService;
    private readonly IScreenCaptureService? _screenCaptureService;
    // Tracks in-flight document parsing tasks so send can be gated and parsing
    // can be cancelled when the conversation is reset.
    private readonly Dictionary<string, CancellationTokenSource> _documentParseCts = new();
    private readonly ILogger _logger = Log.ForContext<ChatTabViewModel>();

    // 工具轮封口后置位，使下一段阶段性正文另起一个 Markdown 段（工具组置顶后不再天然分隔相邻文本）。
    private bool _forceNewAssistantTextSegment;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendMessageCommand))]
    private string _inputText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendMessageCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartInlineEditCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmInlineEditCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelInlineEditCommand))]
    [NotifyCanExecuteChangedFor(nameof(RegenerateResponseCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopResponseCommand))]
    [NotifyCanExecuteChangedFor(nameof(UndoCompressionCommand))]
    [NotifyPropertyChangedFor(nameof(CanToggleRawContext))]
    private bool _isSending;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NewConversationCommand))]
    [NotifyPropertyChangedFor(nameof(CanToggleRawContext))]
    private bool _isResetting;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendMessageCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartInlineEditCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmInlineEditCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelInlineEditCommand))]
    [NotifyCanExecuteChangedFor(nameof(RegenerateResponseCommand))]
    [NotifyCanExecuteChangedFor(nameof(UndoCompressionCommand))]
    [NotifyPropertyChangedFor(nameof(CanToggleRawContext))]
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

    // 调试：原始上下文（发送给主模型的 raw 消息）视图开关与内容。
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendMessageCommand))]
    private bool _isRawContextView;

    /// <summary>调试：原始上下文按消息拆分的条目（避免单一大文本框选择卡顿）。</summary>
    public ObservableCollection<RawContextEntry> RawContextEntries { get; } = new();

    /// <summary>仅当对话流处于「完成」态（非发送/压缩/解析/重置）时，才允许切换 raw 视图。</summary>
    public bool CanToggleRawContext => !IsSending && !IsCompressing && !HasParsingAttachments && !IsResetting;

    public ObservableCollection<ChatMessage> Messages { get; } = new();

    public ObservableCollection<ChatAttachment> PendingAttachments { get; } = new();

    public bool HasPendingAttachments => PendingAttachments.Count > 0;

    public bool HasParsingAttachments => PendingAttachments.Any(a => a.IsParsing);

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
    private string _conversationId = Guid.NewGuid().ToString("N");

    // 当前会话的上下文压缩摘要（唯一真源；TokenService.CompressionPreview 仅作设置页展示镜像）。
    private string? _activeContextSummary;

    // 会话内压缩撤销栈：每次压缩入栈一个检查点，撤销时弹出还原。切换/重置会话时清空。
    private readonly Stack<CompressionCheckpoint> _compressionHistory = new();

    private sealed record CompressionCheckpoint(string? PreviousSummary, IReadOnlyList<ChatMessage> Batch);

    private DateTime _latestArchiveCaptureAt = DateTime.MinValue;

    public ChatTabViewModel() : this(null, null, null, null, null, null, null, null, null, null, null, null, null, null) { }

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
        ISystemAudioService? systemAudioService = null,
        IConversationArchiveService? archiveService = null,
        IImageGenerationSessionService? imageGenerationSessionService = null,
        IDocumentParserService? documentParserService = null,
        IScreenCaptureService? screenCaptureService = null)
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
        _systemAudioService = systemAudioService;
        _archiveService = archiveService;
        _imageGenerationSessionService = imageGenerationSessionService;
        _documentParserService = documentParserService;
        _screenCaptureService = screenCaptureService;

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

        // 先让出 UI 线程跑一次渲染，确保用户气泡立即出现，再去做后续较重的请求准备
        // （BuildMessages / token 估算 / 读取配置等），避免发送后约 1s 才看到气泡。
        await Task.Yield();

        await GetAiResponseAsync(userContent, addToContext: false);
    }

    private bool CanSendMessage() => !IsSending && !IsCompressing && !HasParsingAttachments && !IsRawContextView && (!string.IsNullOrWhiteSpace(InputText) || PendingAttachments.Count > 0);

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

    /// <summary>
    /// 调试：进入「原始上下文」视图时，即时构建一次发送给主模型的 raw 消息快照。
    /// </summary>
    partial void OnIsRawContextViewChanged(bool value)
    {
        if (value)
        {
            RefreshRawContext();
        }
    }

    private void RefreshRawContext()
    {
        RawContextEntries.Clear();

        if (_chatService == null)
        {
            RawContextEntries.Add(new RawContextEntry
            {
                Header = "error",
                Text = GetString("Chat.Raw.ServiceUnavailable", "Chat service is unavailable.")
            });
            return;
        }

        try
        {
            foreach (var entry in _chatService.BuildRawContext(_currentContext))
            {
                RawContextEntries.Add(entry);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "构建 raw 上下文失败");
            RawContextEntries.Add(new RawContextEntry { Header = "error", Text = "构建 raw 上下文失败: " + ex.Message });
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
                await ReconcileImageGenerationSessionAsync();
                await GetAiResponseAsync(message.Content, addToContext: false);
            }
            else
            {
                UpdateConversationContext();
                await ReconcileImageGenerationSessionAsync();
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
        await ReconcileImageGenerationSessionAsync();

        // 基于该干净的节点重新生成
        var lastUserMsg = Messages[lastUserIndex];
        await GetAiResponseAsync(lastUserMsg.Content, addToContext: false);
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

        var imageFilter = new FilePickerFileType(GetString("Chat.Attach.ImageFiles", "Image files"))
        {
            Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.gif"],
            MimeTypes = ["image/png", "image/jpeg", "image/webp", "image/gif"],
            AppleUniformTypeIdentifiers = ["public.png", "public.jpeg", "org.webmproject.webp", "com.compuserve.gif"]
        };

        // 纯文本 / 代码文件无需解析，始终可作为附件直接读入内容。
        var textPatterns = (_attachmentStoreService.SupportedTextExtensions ?? Array.Empty<string>())
            .Select(ext => "*" + ext)
            .ToArray();
        FilePickerFileType? textFilter = textPatterns.Length > 0
            ? new FilePickerFileType(GetString("Chat.Attach.TextFiles", "Text & code files"))
            {
                Patterns = textPatterns,
                MimeTypes = ["text/*"],
                AppleUniformTypeIdentifiers = ["public.text", "public.source-code"]
            }
            : null;

        var filters = new List<FilePickerFileType>();
        var documentParsingEnabled = _documentParserService?.IsEnabled == true;
        if (documentParsingEnabled)
        {
            // 启用文档解析后，附件按钮支持图片 + MinerU 支持的文档格式。
            var documentFilter = new FilePickerFileType(GetString("Chat.Attach.DocumentFiles", "Documents"))
            {
                Patterns = ["*.pdf", "*.doc", "*.docx", "*.ppt", "*.pptx", "*.xls", "*.xlsx"],
                MimeTypes =
                [
                    "application/pdf",
                    "application/msword",
                    "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                    "application/vnd.ms-powerpoint",
                    "application/vnd.openxmlformats-officedocument.presentationml.presentation",
                    "application/vnd.ms-excel",
                    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
                ],
                AppleUniformTypeIdentifiers = ["com.adobe.pdf", "com.microsoft.word.doc", "org.openxmlformats.wordprocessingml.document", "com.microsoft.powerpoint.ppt", "org.openxmlformats.presentationml.presentation", "com.microsoft.excel.xls", "org.openxmlformats.spreadsheetml.sheet"]
            };

            filters.Add(new FilePickerFileType(GetString("Chat.Attach.SupportedFiles", "Supported files"))
            {
                Patterns = [.. imageFilter.Patterns!, .. documentFilter.Patterns!, .. textPatterns]
            });
            filters.Add(imageFilter);
            filters.Add(documentFilter);
        }
        else
        {
            filters.Add(new FilePickerFileType(GetString("Chat.Attach.SupportedFiles", "Supported files"))
            {
                Patterns = [.. imageFilter.Patterns!, .. textPatterns]
            });
            filters.Add(imageFilter);
        }

        if (textFilter != null)
        {
            filters.Add(textFilter);
        }

        var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = GetString("Chat.Attach.SelectFiles", "Select files"),
            AllowMultiple = true,
            FileTypeFilter = filters
        });

        await AddStorageFilesAsync(files);
    }

    [RelayCommand]
    private void RemovePendingAttachment(ChatAttachment? attachment)
    {
        if (attachment == null) return;

        if (PendingAttachments.Remove(attachment))
        {
            CancelDocumentParsing(attachment);
            _attachmentStoreService?.DeleteStoredAttachment(attachment);
            AttachmentStatusMessage = string.Empty;
            OnPropertyChanged(nameof(HasParsingAttachments));
            OnPropertyChanged(nameof(CanToggleRawContext));
            SendMessageCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>
    /// 在后台将文档附件解析为 Markdown 文本（上传 + 轮询），并实时更新卡片状态。
    /// 解析完成前会阻止发送，确保附带的文档内容随消息一并送达 AI。
    /// </summary>
    private void StartDocumentParsing(ChatAttachment attachment)
    {
        if (_documentParserService == null || !_documentParserService.IsEnabled)
        {
            attachment.ParseState = DocumentParseState.Failed;
            attachment.ParseError = GetString("Chat.Attach.ParserDisabled", "Document parsing is not enabled.");
            return;
        }

        var cts = new CancellationTokenSource();
        _documentParseCts[attachment.Id] = cts;

        attachment.ParseState = DocumentParseState.Parsing;
        attachment.ParseError = string.Empty;
        OnPropertyChanged(nameof(HasParsingAttachments));
        OnPropertyChanged(nameof(CanToggleRawContext));
        SendMessageCommand.NotifyCanExecuteChanged();

        _ = Task.Run(async () =>
        {
            DocumentParseResult result;
            try
            {
                result = await _documentParserService.ParseAsync(attachment.StoredPath, attachment.FileName, cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                result = DocumentParseResult.Fail(ex.Message);
            }

            // 解析成功后，先在后台把 Markdown 落盘为 sidecar（供延迟读取），避免阻塞 UI 线程。
            string sidecarPath = string.Empty;
            if (result.Success && _attachmentStoreService != null)
            {
                try
                {
                    sidecarPath = await _attachmentStoreService.WriteParsedSidecarAsync(attachment, result.Markdown, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "写入解析 sidecar 失败: {File}", attachment.FileName);
                }
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (cts.IsCancellationRequested) return;

                if (result.Success)
                {
                    attachment.ExtractedText = result.Markdown;
                    attachment.EstimatedTokens = Models.ConversationContext.EstimateTokens(result.Markdown);
                    attachment.RetrievalPath = string.IsNullOrWhiteSpace(sidecarPath) ? attachment.StoredPath : sidecarPath;
                    attachment.ParseState = DocumentParseState.Parsed;
                    // 根据 token 预算决定内联还是延迟载入（延迟会清空内存中的全文）。
                    ApplyRetrievalMode(attachment);
                }
                else
                {
                    attachment.ParseState = DocumentParseState.Failed;
                    attachment.ParseError = result.ErrorMessage ?? GetString("Chat.Attach.ParseFailed", "Failed to parse document.");
                    AttachmentStatusMessage = string.Format(
                        GetString("Chat.Attach.ParseFailedNamed", "Failed to parse {0}: {1}"),
                        attachment.DisplayName,
                        attachment.ParseError);
                }

                _documentParseCts.Remove(attachment.Id);
                OnPropertyChanged(nameof(HasParsingAttachments));
                OnPropertyChanged(nameof(CanToggleRawContext));
                SendMessageCommand.NotifyCanExecuteChanged();
            });
        });
    }

    /// <summary>
    /// 单个附件可内联进上下文的 token 预算：约为上下文上限的 1/4，并设绝对上下限。
    /// 超过该预算的文本/文档转为“延迟载入”，仅注入指针由模型按需读取。
    /// </summary>
    private int InlineTokenBudget => Math.Clamp((_tokenService?.MaxTokens ?? 4000) / 4, 800, 8000);

    /// <summary>
    /// 依据 token 预算决定附件的取用方式；延迟载入会清空内存中的全文，仅保留指针。
    /// </summary>
    private void ApplyRetrievalMode(ChatAttachment attachment)
    {
        // 延迟载入依赖模型用文件系统工具按需读取；若工具调用被关闭，则无论多大都只能内联，
        // 否则模型将既看不到全文、也无法读取指针。
        var functionCallingEnabled = _configService?.Load().EnableFunctionCalling == true;

        if (!functionCallingEnabled || attachment.EstimatedTokens <= InlineTokenBudget)
        {
            attachment.RetrievalMode = AttachmentRetrievalMode.Inline;
            // 内联但内存中可能已无全文（如从延迟态切回），需要时从指针补回。
            if (string.IsNullOrEmpty(attachment.ExtractedText) && !string.IsNullOrWhiteSpace(attachment.RetrievalPath))
            {
                try
                {
                    if (System.IO.File.Exists(attachment.RetrievalPath))
                    {
                        attachment.ExtractedText = System.IO.File.ReadAllText(attachment.RetrievalPath);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "内联读取附件内容失败: {Path}", attachment.RetrievalPath);
                }
            }
        }
        else
        {
            attachment.RetrievalMode = AttachmentRetrievalMode.Deferred;
            attachment.ExtractedText = string.Empty;
        }
    }

    private void CancelDocumentParsing(ChatAttachment attachment)
    {
        if (_documentParseCts.TryGetValue(attachment.Id, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
            _documentParseCts.Remove(attachment.Id);
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
                if (attachment.IsDocument)
                {
                    StartDocumentParsing(attachment);
                }
                else if (attachment.Kind == AttachmentKind.Code)
                {
                    // 文本/代码在导入时已读入内容并估算 token，这里直接决定内联或延迟。
                    ApplyRetrievalMode(attachment);
                }
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

    private bool _isCapturingScreenshot;

    /// <summary>
    /// 调用系统原生截图工具（框选/裁剪/标注），完成后从剪贴板取回图片并作为待发送附件。
    /// </summary>
    /// <param name="mode">
    /// "keep" = 截图时保留本窗口（可截 AthenaAgent 自身）；其余值（含 null）= 截图时隐藏本窗口（截其它内容）。
    /// </param>
    [RelayCommand]
    private async Task CaptureScreenshotAsync(string? mode)
    {
        if (_isCapturingScreenshot) return;
        if (IsSending || IsCompressing) return;

        if (_screenCaptureService == null || !_screenCaptureService.IsSupported)
        {
            AttachmentStatusMessage = GetString("Chat.Screenshot.Unsupported", "Screenshot is not supported on this platform.");
            return;
        }

        if (_attachmentStoreService != null && PendingAttachments.Count >= _attachmentStoreService.MaxPendingAttachments)
        {
            AttachmentStatusMessage = string.Format(
                GetString("Chat.Attach.MaxCount", "You can attach up to {0} files."),
                _attachmentStoreService.MaxPendingAttachments);
            return;
        }

        var clipboard = TopLevel.GetTopLevel(GetMainWindow())?.Clipboard;
        if (clipboard == null)
        {
            AttachmentStatusMessage = GetString("Chat.Screenshot.Failed", "Failed to capture screenshot.");
            return;
        }

        // 默认隐藏窗口；mode == "keep" 时保留窗口，从而可以截到 AthenaAgent 自身。
        var hideWindow = !string.Equals(mode, "keep", StringComparison.OrdinalIgnoreCase);

        _isCapturingScreenshot = true;
        var mainWindow = GetMainWindow();
        var previousState = mainWindow?.WindowState ?? WindowState.Normal;
        var minimized = false;

        void RestoreWindow()
        {
            if (!minimized || mainWindow == null) return;
            mainWindow.WindowState = previousState;
            mainWindow.Activate();
            minimized = false;
        }

        try
        {
            AttachmentStatusMessage = string.Empty;

            // 以"清空剪贴板 → 截图 → 读取新位图"判定结果，避免误取旧剪贴板内容。
            try { await clipboard.ClearAsync(); } catch { /* 某些平台清空可能失败，忽略 */ }

            // 隐藏模式：最小化窗口以免遮挡截图目标；保留模式：原样显示，让截图浮层覆盖在窗口之上。
            if (hideWindow && mainWindow != null)
            {
                mainWindow.WindowState = WindowState.Minimized;
                await Task.Delay(250); // 等待最小化动画完成
                minimized = true;
            }

            var launch = await _screenCaptureService.LaunchInteractiveAsync();

            if (launch == ScreenCaptureLaunchResult.Failed || launch == ScreenCaptureLaunchResult.Unsupported)
            {
                RestoreWindow();
                AttachmentStatusMessage = GetString("Chat.Screenshot.Failed", "Failed to capture screenshot.");
                return;
            }

            // 阻塞型（mac/linux，以及 Windows 监听覆盖层进程退出后）返回时截图交互已结束，可立即还原窗口；
            // 异步型（Windows 未捕获到覆盖层进程的回退路径）启动后立即返回，需保持隐藏直到取回图片后再还原。
            if (launch == ScreenCaptureLaunchResult.CompletedBlocking)
            {
                RestoreWindow();
            }

            // 阻塞型仅做少量重试以容忍剪贴板写入延迟；异步型需较长轮询直到用户完成或超时。
            var maxAttempts = launch == ScreenCaptureLaunchResult.LaunchedAsync ? 200 : 8;
            Bitmap? bitmap = null;
            for (var i = 0; i < maxAttempts; i++)
            {
                bitmap = await clipboard.TryGetBitmapAsync();
                if (bitmap != null) break;
                await Task.Delay(300);
            }

            RestoreWindow();

            if (bitmap == null)
            {
                // 用户取消截图或超时，静默返回（不视为错误）。
                return;
            }

            await AddClipboardBitmapAsync(bitmap);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "截图失败");
            RestoreWindow();
            AttachmentStatusMessage = GetString("Chat.Screenshot.Failed", "Failed to capture screenshot.");
        }
        finally
        {
            RestoreWindow();
            _isCapturingScreenshot = false;
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

    private async Task<ConversationArchiveSnapshot?> CaptureArchiveSnapshotIfNeededAsync()
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

        ImageGenerationSessionSnapshot? imageSessionSnapshot = null;
        if (_imageGenerationSessionService != null)
        {
            imageSessionSnapshot = await _imageGenerationSessionService.CreateSnapshotAsync(_conversationId);
        }

        return new ConversationArchiveSnapshot
        {
            ConversationId = _conversationId,
            HistoryId = _currentHistoryId,
            ContextSummary = _activeContextSummary,
            Messages = messages,
            ImageSession = imageSessionSnapshot,
            CapturedAt = DateTime.Now,
            ForceGenerateSummary = true
        };
    }

    private void ResetConversationState()
    {
        Messages.Clear();
        InputText = string.Empty;
        ClearPendingAttachments(deleteStoredFiles: true);
        if (_imageGenerationSessionService != null)
        {
            _imageGenerationSessionService.DeleteAsync(_conversationId).GetAwaiter().GetResult();
        }
        _currentContext.Reset();
        _conversationId = Guid.NewGuid().ToString("N");
        _currentContext.ConversationId = _conversationId;
        _currentHistoryId = null;
        _initialConversationSignature = null;

        _compressionHistory.Clear();
        SetActiveContextSummary(null);
        UndoCompressionCommand.NotifyCanExecuteChanged();

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
        requestContext.ConversationId = _conversationId;
        var outcome = TaskExecutionResult.Succeeded();

        IsSending = true;
        _forceNewAssistantTextSegment = false;
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
                        // 真实的工具调用回合保存在隐藏消息里，并插入到活动气泡之前，
                        // 保证最终回复气泡始终在 Messages 末尾、排在 tool_call/tool 之后。
                        msg.IsHidden = true;
                        InsertBeforeActiveBubble(assistantMsg, msg);

                        // 把本轮的阶段性正文固化为可见段并清空 Content（去重），保留所有可见段
                        CommitActiveBubbleRound(assistantMsg);

                        // 为本轮的每个工具调用追加一张「执行中」卡片
                        assistantMsg.IsLoading = false;
                        assistantMsg.ToolExecutionSummary = string.Empty;
                        AddToolCallEntries(assistantMsg, msg.ToolCallsJson);
                    }
                    else if (msg.Role == "assistant" && !string.IsNullOrEmpty(msg.ReasoningContent))
                    {
                        assistantMsg.ReasoningContent = msg.ReasoningContent;
                    }
                    else if (msg.Role == "assistant" && msg.Attachments.Count > 0)
                    {
                        var attachments = msg.Attachments
                            .Select(CloneAttachmentForMessage)
                            .ToList();

                        foreach (var attachment in attachments)
                        {
                            assistantMsg.Attachments.Add(attachment);
                        }

                        assistantMsg.NotifyAttachmentsChanged();

                        var generatedImageAttachments = attachments
                            .Where(attachment => attachment.IsImage)
                            .ToList();

                        if (generatedImageAttachments.Count > 0)
                        {
                            EnsureSegmentLayout(assistantMsg);

                            foreach (var attachment in generatedImageAttachments)
                            {
                                assistantMsg.Segments.Add(new ChatMessageSegment
                                {
                                    Kind = ChatMessageSegmentKind.GeneratedImage,
                                    AttachmentId = attachment.Id,
                                    Attachment = attachment
                                });
                            }

                            assistantMsg.NotifySegmentsChanged();
                        }

                        if (!string.IsNullOrWhiteSpace(msg.OutputAudioReferenceId))
                        {
                            assistantMsg.OutputAudioReferenceId = msg.OutputAudioReferenceId;
                        }

                        if (!string.IsNullOrWhiteSpace(msg.AudioErrorMessage))
                        {
                            assistantMsg.AudioErrorMessage = msg.AudioErrorMessage;
                        }

                        assistantMsg.IsLoading = false;

                        var audioAttachment = attachments.FirstOrDefault(attachment => attachment.IsAudio);
                        if (audioAttachment != null)
                        {
                            TryAutoPlayAssistantAudio(audioAttachment, assistantMsg);
                        }
                    }
                    else if (msg.Role == "assistant" && (!string.IsNullOrWhiteSpace(msg.AudioErrorMessage) || !string.IsNullOrWhiteSpace(msg.OutputAudioReferenceId)))
                    {
                        assistantMsg.OutputAudioReferenceId = msg.OutputAudioReferenceId;
                        assistantMsg.AudioErrorMessage = msg.AudioErrorMessage;
                        assistantMsg.IsLoading = false;
                    }
                    else if (msg.Role == "tool")
                    {
                        InsertBeforeActiveBubble(assistantMsg, msg);

                        // 把工具结果回填到对应卡片（按 ToolCallId 匹配），并标记成功/失败
                        CompleteToolCallEntry(assistantMsg, msg.ToolCallId, msg.ToolName, msg.Content);

                        // 工具执行完毕，等待大模型下一步指示——保留思考动画
                        assistantMsg.ToolExecutionSummary = string.Empty;
                        assistantMsg.IsLoading = true;
                    }
                },
                onContextCompressed: (summary, count) => {
                    if (!IsCurrentConversationEpoch(epoch))
                    {
                        return;
                    }

                    // 捕获压缩前摘要，供撤销还原
                    var previousSummary = _activeContextSummary;

                    // 同步 UI 消息状态：标记前 count 条当前未压缩的消息为已压缩，并收集引用入撤销栈
                    var batch = new List<ChatMessage>(count);
                    foreach (var m in Messages)
                    {
                        if (!m.IsCompressed)
                        {
                            m.IsCompressed = true;
                            batch.Add(m);
                            if (batch.Count >= count) break;
                        }
                    }
                    if (batch.Count > 0)
                    {
                        _compressionHistory.Push(new CompressionCheckpoint(previousSummary, batch));
                    }
                    SetActiveContextSummary(summary);
                    UpdateContextTokensDisplay();
                    UpdateBubbleButtonVisibility();
                    UndoCompressionCommand.NotifyCanExecuteChanged();
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
                    AppendAssistantMarkdownSegment(assistantMsg, contentDelta);
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

                // 输出结束（成功/停止/报错均经此）：自动收起工具调用组（露 3 个 + peek），尊重用户手动操作
                CollapseToolGroups(assistantMsg);

                // Cleanup the empty main assistant message if it didn't generate any text and didn't call tools directly
                if (string.IsNullOrWhiteSpace(assistantMsg.Content)
                    && string.IsNullOrEmpty(assistantMsg.ToolCallsJson)
                    && string.IsNullOrEmpty(assistantMsg.ReasoningContent))
                {
                    if (assistantMsg.Attachments.Count == 0 && string.IsNullOrWhiteSpace(assistantMsg.AudioErrorMessage))
                    {
                        Messages.Remove(assistantMsg);
                    }
                }

                UpdateConversationContext();
                await ReconcileImageGenerationSessionAsync();
                IsSending = false;
                UpdateContextTokensDisplay();
                UpdateBubbleButtonVisibility();
            }

            await NotifySchedulerAvailabilityAsync();
        }

        return outcome;
    }

    /// <summary>
    /// 设置当前会话的压缩摘要（唯一真源），并同步到 TokenService 的展示镜像。
    /// </summary>
    private void SetActiveContextSummary(string? summary)
    {
        _activeContextSummary = string.IsNullOrEmpty(summary) ? null : summary;
        if (_tokenService != null)
        {
            _tokenService.CompressionPreview = _activeContextSummary ?? string.Empty;
        }
    }

    private void UpdateConversationContext()
    {
        _currentContext.Clear();
        _currentContext.ConversationId = _conversationId;

        // 赋予当前的压缩摘要（如果有）——读会话级真源，而非 UI 单例
        _currentContext.SetSummary(string.IsNullOrEmpty(_activeContextSummary) ? null : _activeContextSummary);

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

    private async Task ReconcileImageGenerationSessionAsync()
    {
        if (_imageGenerationSessionService == null)
        {
            return;
        }

        var survivingAttachmentIds = Messages
            .SelectMany(msg => msg.Attachments)
            .Where(attachment => attachment.IsImage)
            .Select(attachment => attachment.Id)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        await _imageGenerationSessionService.ReconcileAsync(_conversationId, survivingAttachmentIds);
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
            var previousSummary = _activeContextSummary;
            var messagesList = Messages.ToList();
            var result = await _historyService.CompressContextAsync(messagesList, previousSummary, config.KeepRecentRounds);
            if (result.Summary != null && result.CompressedCount > 0)
            {
                // 入压缩撤销栈：捕获压缩前摘要与被压缩消息引用
                _compressionHistory.Push(new CompressionCheckpoint(previousSummary, result.CompressedMessages));

                SetActiveContextSummary(result.Summary);

                // 更新对话上下文并重新计算 Token
                UpdateConversationContext();
                UpdateContextTokensDisplay();
                UpdateBubbleButtonVisibility();
                UndoCompressionCommand.NotifyCanExecuteChanged();

                _logger.Information("UI 上下文压缩显示已更新（按轮次压缩，{Mode}）", result.UsedFallback ? "本地兜底" : "AI");
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

    [RelayCommand(CanExecute = nameof(CanUndoCompression))]
    private void UndoCompression() => InternalUndoCompression();

    private bool CanUndoCompression() => !IsSending && !IsCompressing && _compressionHistory.Count > 0;

    /// <summary>
    /// 撤销上一次上下文压缩：把该批被归档的消息重新激活，并恢复压缩前的摘要。
    /// 仅支持会话内（内存）撤销；切换/重置会话后栈清空。
    /// </summary>
    public bool InternalUndoCompression()
    {
        if (_compressionHistory.Count == 0 || IsSending || IsCompressing) return false;

        var checkpoint = _compressionHistory.Pop();
        foreach (var msg in checkpoint.Batch)
        {
            msg.IsCompressed = false; // 恢复可见，并重新进入发送上下文
        }
        SetActiveContextSummary(checkpoint.PreviousSummary);

        UpdateConversationContext();
        UpdateContextTokensDisplay();
        UpdateBubbleButtonVisibility();
        UndoCompressionCommand.NotifyCanExecuteChanged();

        _logger.Information("已撤销上一次上下文压缩，恢复 {Count} 条消息", checkpoint.Batch.Count);
        return true;
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
            _conversationId = string.IsNullOrWhiteSpace(history.ConversationId)
                ? Guid.NewGuid().ToString("N")
                : history.ConversationId;
            _currentContext.ConversationId = _conversationId;
            _currentHistoryId = history.Id;
            _compressionHistory.Clear();
            SetActiveContextSummary(history.ContextSummary);
            UndoCompressionCommand.NotifyCanExecuteChanged();

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

            await ReconcileImageGenerationSessionAsync();

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
        var snapshot = await CaptureArchiveSnapshotIfNeededAsync();
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
            ConversationId = _conversationId,
            CurrentHistoryId = _currentHistoryId,
            InitialConversationSignature = _initialConversationSignature,
            ContextSummary = _activeContextSummary,
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
        _conversationId = string.IsNullOrWhiteSpace(snapshot.ConversationId)
            ? Guid.NewGuid().ToString("N")
            : snapshot.ConversationId;
        _currentContext.ConversationId = _conversationId;
        _currentHistoryId = snapshot.CurrentHistoryId;
        _initialConversationSignature = snapshot.InitialConversationSignature;

        _compressionHistory.Clear();
        SetActiveContextSummary(snapshot.ContextSummary);
        UndoCompressionCommand.NotifyCanExecuteChanged();

        if (snapshot.Messages != null)
        {
            foreach (var msg in snapshot.Messages)
            {
                ConversationPersistenceHelper.PrepareRestoredMessage(msg);
                _ = _attachmentStoreService?.LoadPreviewsAsync(msg.Attachments);
                Messages.Add(msg);
            }
        }

        _ = ReconcileImageGenerationSessionAsync();

        UpdateConversationContext();
        UpdateContextTokensDisplay();
        UpdateBubbleButtonVisibility();
        _logger.Information("已恢复主对话草稿，消息数: {Count}", Messages.Count);
    }

    private bool HasConversationStateToPersist()
    {
        return Messages.Any(ConversationPersistenceHelper.ShouldPersistMessage)
            || !string.IsNullOrWhiteSpace(_activeContextSummary);
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
            ContextSummary = _activeContextSummary ?? string.Empty,
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
                    }).ToList(),
                    Segments = msg.Segments.Select(segment => new
                    {
                        segment.Kind,
                        segment.Text,
                        segment.AttachmentId,
                        ToolCalls = segment.ToolCalls.Select(t => new
                        {
                            t.ToolCallId,
                            t.Name,
                            t.Summary,
                            t.Arguments,
                            t.Result,
                            t.Status
                        }).ToList()
                    }).ToList()
                })
                .ToList()
        };

        return JsonSerializer.Serialize(signatureModel);
    }

    private void ClearPendingAttachments(bool deleteStoredFiles)
    {
        foreach (var attachment in PendingAttachments.ToList())
        {
            CancelDocumentParsing(attachment);
            if (deleteStoredFiles)
            {
                _attachmentStoreService?.DeleteStoredAttachment(attachment);
            }
        }

        PendingAttachments.Clear();
        AttachmentStatusMessage = string.Empty;
        OnPropertyChanged(nameof(HasParsingAttachments));
        OnPropertyChanged(nameof(CanToggleRawContext));
        SendMessageCommand.NotifyCanExecuteChanged();
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
    private void ToggleAudioPlayback(ChatAttachment? attachment)
    {
        if (attachment == null || !attachment.IsAudio)
        {
            return;
        }

        // Single toggle: if this clip is playing, stop it; otherwise start it.
        if (attachment.IsPlaying)
        {
            StopAudioPlayback();
            return;
        }

        // Only one clip plays at a time — tear down whatever is running first.
        StopAudioPlayback();

        if (attachment.UsesSystemAudioPlayback && _systemAudioService?.IsSupported == true)
        {
            // Fire-and-forget: the command must return immediately so that the
            // generated IAsyncRelayCommand keeps CanExecute == true while the
            // system process (afplay/aplay) is running — otherwise the Stop
            // button is greyed out for the entire playback duration.
            _ = PlaySystemAudioAttachmentAsync(attachment, null);
            return;
        }

        if (_audioPlaybackService?.Play(attachment) == true)
        {
            UpdateAudioPlayingState(attachment.Id, true);
        }
    }

    private void StopAudioPlayback()
    {
        if (_systemAudioCts != null)
        {
            _systemAudioCts.Cancel();
            _systemAudioCts.Dispose();
            _systemAudioCts = null;
        }

        _audioPlaybackService?.Stop();
        _playingAttachmentId = null;
        UpdateAudioPlayingState(null, false);
    }

    private void OnPlaybackStateChanged(object? sender, AudioPlaybackStateChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            UpdateAudioPlayingState(e.AttachmentId, e.IsPlaying);
        });
    }

    private void UpdateAudioPlayingState(string? attachmentId, bool isPlaying)
    {
        foreach (var attachment in Messages.SelectMany(m => m.Attachments).Where(a => a.IsAudio))
        {
            attachment.IsPlaying = isPlaying && attachment.Id == attachmentId;
        }
    }

    private void TryAutoPlayAssistantAudio(ChatAttachment attachment, ChatMessage message)
    {
        if (_audioPlaybackService == null || _configService == null)
        {
            return;
        }

        try
        {
            var config = _configService.Load();
            if (!config.ChatAudioEnabled || !config.ChatAudioAutoPlay)
            {
                return;
            }

            if (attachment.UsesSystemAudioPlayback && _systemAudioService?.IsSupported == true)
            {
                _ = PlaySystemAudioAttachmentAsync(attachment, message);
                return;
            }

            if (_audioPlaybackService.Play(attachment))
            {
                UpdateAudioPlayingState(attachment.Id, true);
            }
            else if (_systemAudioService?.IsSupported == true)
            {
                _ = PlaySystemAudioAttachmentAsync(attachment, message);
            }
            else
            {
                message.AudioErrorMessage = GetString("Chat.Audio.PlaybackUnavailable", "Audio playback is unavailable on this device.");
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "自动播放 assistant 音频失败");
            message.AudioErrorMessage = GetString("Chat.Audio.PlaybackFailed", "Failed to start audio playback.");
        }
    }

    private async Task PlaySystemAudioAttachmentAsync(ChatAttachment attachment, ChatMessage? message)
    {
        if (_systemAudioService?.IsSupported != true)
        {
            if (message != null)
            {
                message.AudioErrorMessage = GetString("Chat.Audio.PlaybackUnavailable", "Audio playback is unavailable on this device.");
            }
            return;
        }

        var cts = new CancellationTokenSource();
        var id = attachment.Id;
        _systemAudioCts = cts;
        _playingAttachmentId = id;
        UpdateAudioPlayingState(id, true);
        try
        {
            var result = await _systemAudioService.PlayFileAsync(attachment.StoredPath, cts.Token);
            if (!result.Success && !cts.IsCancellationRequested)
            {
                var owner = message ?? Messages.FirstOrDefault(m => m.Attachments.Contains(attachment));
                if (owner != null)
                {
                    owner.AudioErrorMessage = string.Format(
                        GetString("Chat.Audio.PlaybackFailedDetail", "Failed to play system audio: {0}"),
                        result.Message);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // User pressed Stop or a new clip took over — expected, swallow.
        }
        finally
        {
            if (_systemAudioCts == cts)
            {
                _systemAudioCts.Dispose();
                _systemAudioCts = null;
            }

            // Only clear the playing state if *this* clip is still the active
            // one. If a new clip started while we were cancelled/completing,
            // its IsPlaying=true must not be clobbered.
            if (_playingAttachmentId == id)
            {
                _playingAttachmentId = null;
                UpdateAudioPlayingState(id, false);
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

    private static void EnsureSegmentLayout(ChatMessage message)
    {
        if (message.Segments.Count > 0)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(message.Content))
        {
            message.Segments.Add(new ChatMessageSegment
            {
                Kind = ChatMessageSegmentKind.Markdown,
                Text = message.Content
            });
        }

        message.NotifySegmentsChanged();
    }

    private void AppendAssistantMarkdownSegment(ChatMessage message, string contentDelta)
    {
        if (message.Segments.Count == 0)
        {
            return;
        }

        // 工具轮刚封口：本段正文另起一个 Markdown 段，避免与上一轮文本拼接成一段
        if (_forceNewAssistantTextSegment)
        {
            _forceNewAssistantTextSegment = false;
            message.Segments.Add(new ChatMessageSegment
            {
                Kind = ChatMessageSegmentKind.Markdown,
                Text = contentDelta
            });
            message.NotifySegmentsChanged();
            return;
        }

        var lastSegment = message.Segments[^1];
        if (lastSegment.IsMarkdown)
        {
            lastSegment.Text += contentDelta;
            return;
        }

        message.Segments.Add(new ChatMessageSegment
        {
            Kind = ChatMessageSegmentKind.Markdown,
            Text = contentDelta
        });
        message.NotifySegmentsChanged();
    }

    // 为本轮工具调用追加「执行中」卡片（每个工具一张）。
    private static void AddToolCallEntries(ChatMessage message, string? toolCallsJson)
    {
        if (string.IsNullOrWhiteSpace(toolCallsJson))
        {
            return;
        }

        try
        {
            var toolCalls = System.Text.Json.Nodes.JsonNode.Parse(toolCallsJson)?.AsArray();
            if (toolCalls == null)
            {
                return;
            }

            var group = GetOrCreateToolCallGroup(message);

            foreach (var node in toolCalls)
            {
                if (node == null)
                {
                    continue;
                }

                var name = node["FunctionName"]?.ToString() ?? string.Empty;
                var id = node["Id"]?.ToString();
                var arguments = node["Arguments"]?.ToString();

                group.ToolCalls.Add(new ToolCallEntry
                {
                    ToolCallId = id,
                    Name = name,
                    Summary = ToolCallDisplay.Summarize(name, arguments),
                    Arguments = ToolCallDisplay.PrettyArguments(arguments),
                    Status = ToolCallStatus.Running
                });
            }

            // 流式期间展开整组，方便观察实时进度（除非用户已手动收起）
            if (!group.UserToggledGroup)
            {
                group.IsGroupExpanded = true;
            }

            message.NotifySegmentsChanged();
        }
        catch
        {
            // 工具调用 JSON 异常时跳过卡片渲染，不影响主流程
        }
    }

    // 一条消息内所有工具调用汇总进同一个组，并固定置顶（segment 索引 0），
    // 让全部工具调用集中在气泡顶部、阶段性回复文本依次排在其下方。
    private static ChatMessageSegment GetOrCreateToolCallGroup(ChatMessage message)
    {
        var group = message.Segments.FirstOrDefault(s => s.IsToolCallGroup);
        if (group == null)
        {
            group = new ChatMessageSegment { Kind = ChatMessageSegmentKind.ToolCallGroup };
            message.Segments.Insert(0, group);
        }

        return group;
    }

    // 工具结果回填到对应项：按 ToolCallId 匹配，标记成功/失败并填入结果预览。
    private static void CompleteToolCallEntry(ChatMessage message, string? toolCallId, string toolName, string? resultJson)
    {
        ToolCallEntry? entry = null;
        foreach (var seg in message.Segments)
        {
            if (!seg.IsToolCallGroup)
            {
                continue;
            }

            entry = seg.ToolCalls.FirstOrDefault(t =>
                !string.IsNullOrEmpty(t.ToolCallId) && t.ToolCallId == toolCallId);
            if (entry != null)
            {
                break;
            }
        }

        // 兜底：没有 Id 匹配时，取第一项仍在执行中的同名调用
        entry ??= message.Segments
            .Where(s => s.IsToolCallGroup)
            .SelectMany(s => s.ToolCalls)
            .FirstOrDefault(t => t.IsRunning && (string.IsNullOrEmpty(toolName) || t.Name == toolName));

        if (entry == null)
        {
            return;
        }

        var (success, preview) = ToolCallDisplay.ParseResult(resultJson);
        entry.Status = success ? ToolCallStatus.Success : ToolCallStatus.Failed;
        entry.Result = preview;
    }

    // 工具轮全部完成、开始/结束输出最终正文时收起工具组（尊重用户已手动展开/收起的意愿）。
    private static void CollapseToolGroups(ChatMessage message)
    {
        foreach (var seg in message.Segments)
        {
            if (seg.IsToolCallGroup && !seg.UserToggledGroup)
            {
                seg.IsGroupExpanded = false;
            }
        }
    }

    // 中间消息（tool_call / tool）插入到活动气泡之前，保证最终回复气泡始终在 Messages 末尾。
    private void InsertBeforeActiveBubble(ChatMessage active, ChatMessage msg)
    {
        int idx = Messages.IndexOf(active);
        if (idx < 0)
        {
            // 兜底：活动气泡已被清理（理论上不应发生于流式期间）。
            Messages.Add(msg);
        }
        else
        {
            Messages.Insert(idx, msg);
        }
    }

    // 工具轮封口：把本轮流式出的「阶段性正文」固化为可见 Markdown 段（避免随后续轮被覆盖），
    // 然后清空活动气泡的 Content/Reasoning。
    // 清空的原因仅是上下文去重——该正文已随隐藏的 tool_call 消息进入上下文（见 UpdateConversationContext），
    // 保留 Content 会让其重复计入；但可见的 Markdown 段必须保留，否则阶段性回复会从界面消失。
    private void CommitActiveBubbleRound(ChatMessage bubble)
    {
        if (!string.IsNullOrWhiteSpace(bubble.Content))
        {
            var last = bubble.Segments.LastOrDefault();
            if (last == null || !last.IsMarkdown)
            {
                // 首轮（流式时 Segments 还为空，AppendAssistantMarkdownSegment 会跳过建段）：补建一段承载前导正文
                bubble.Segments.Add(new ChatMessageSegment
                {
                    Kind = ChatMessageSegmentKind.Markdown,
                    Text = bubble.Content
                });
            }
            // 末尾已是 Markdown 段时，流式正文已逐字写入其中，无需重复添加
        }

        bubble.Content = string.Empty;
        bubble.ReasoningContent = null;
        // 下一段正文另起一段（工具组置顶后，相邻轮文本之间不再有工具段天然分隔）
        _forceNewAssistantTextSegment = true;
        bubble.NotifySegmentsChanged();
    }
}
