using Athena.UI.Models;
using Athena.UI.Services;
using Athena.UI.Services.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace Athena.UI.ViewModels;

public partial class ConversationSessionItemViewModel : ViewModelBase, IDisposable, IConversationCompressionCommitter
{
    private readonly IConversationArchiveStore? _store;
    private readonly ILocalizationService? _localizationService;
    private string _newConversationTitle;
    private CancellationTokenSource? _saveDebounce;
    private readonly SemaphoreSlim _persistGate = new(1, 1);
    private bool _metadataTrackingEnabled;

    /// <summary>手动重命名/分支等显式指定标题后，不再静默覆盖（粘性）。</summary>
    private bool _autoTitleDisabled;

    /// <summary>当前消息内容已生成过静默标题；消息一旦变更即失效，下次切换会话时重新生成。</summary>
    private bool _titleGeneratedForContent;

    public event EventHandler? DeleteRequested;
    public event EventHandler? ForkRequested;
    public event EventHandler? ExportRequested;
    public event EventHandler? PinChanged;
    public event EventHandler<MessageForkRequestedEventArgs>? MessageForkRequested;

    public ConversationSessionItemViewModel(
        MainConversationViewModel chat,
        WorkspaceProfile? workspace,
        IConversationArchiveStore? store,
        string? historyId = null,
        ILocalizationService? localizationService = null)
    {
        Chat = chat;
        Workspace = workspace;
        HistoryId = string.IsNullOrWhiteSpace(historyId) ? Guid.NewGuid().ToString() : historyId;
        _store = store;
        _localizationService = localizationService;
        _newConversationTitle = L("Session.Title.NewConversation", "New chat");
        Chat.PropertyChanged += OnChatPropertyChanged;
        Chat.Messages.CollectionChanged += OnMessagesChanged;
        Chat.PersistenceStateChanged += OnPersistenceStateChanged;
        Chat.MessageForkRequested += OnChatMessageForkRequested;
        Chat.AttachCompressionCommitter(this);
        Dispatcher.UIThread.Post(() => _metadataTrackingEnabled = true);
        if (_localizationService != null)
        {
            _localizationService.LanguageChanged += OnLanguageChanged;
        }
    }

    private string L(string key, string fallback)
        => _localizationService?.GetString(key, fallback) ?? fallback;

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        var previousPlaceholder = _newConversationTitle;
        _newConversationTitle = L("Session.Title.NewConversation", "New chat");
        if (Title == previousPlaceholder && Title != _newConversationTitle)
        {
            Title = _newConversationTitle;
        }

        // 归档进度/失败文本是在事件到达时固化的快照，这里按状态重新取词。
        if (IsArchivePending && !string.IsNullOrWhiteSpace(ArchiveStatusText))
        {
            ArchiveStatusText = L("History.PendingStatus", "Summarizing");
        }
        else if (IsArchiveFailed && !string.IsNullOrWhiteSpace(ArchiveStatusText))
        {
            ArchiveStatusText = L("Chat.Archive.RetryLater", "Failed to archive the previous chat; will retry later.");
        }

        OnPropertyChanged(nameof(PinActionText));
        OnPropertyChanged(nameof(StatusText));
    }

    public MainConversationViewModel Chat { get; }

    public WorkspaceProfile? Workspace { get; }

    public string HistoryId { get; }

    public string ConversationId => Chat.ConversationId;

    public string WorkspaceId => Workspace?.Id ?? string.Empty;

    public string? ForkedFromConversationId { get; init; }

    public string? ForkedFromHistoryId { get; init; }

    public bool IsForked => !string.IsNullOrWhiteSpace(ForkedFromConversationId);

    /// <summary>
    /// 分支深度：0=非分支，1=直接分支（父非分支），2=分支的分支，以此类推。
    /// 由会话树构建与分支创建时计算，驱动左侧树的分支图标与索引编号。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowForkIcon))]
    [NotifyPropertyChangedFor(nameof(HasForkBadge))]
    [NotifyPropertyChangedFor(nameof(ForkBadgeText))]
    private int _forkDepth;

    /// <summary>是否显示分支图标（任何分支会话都显示，图标旁再视深度追加编号）。</summary>
    public bool ShowForkIcon => ForkDepth > 0 || IsForked;

    /// <summary>分支的分支（深度 ≥ 2）才显示索引编号。</summary>
    public bool HasForkBadge => ForkDepth > 1;

    /// <summary>图标右侧的索引编号文本，如 (1)、(2)……，n = ForkDepth − 1。</summary>
    public string? ForkBadgeText => HasForkBadge ? $"({ForkDepth - 1})" : null;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PinActionText))]
    private bool _isPinned;

    public string PinActionText => IsPinned
        ? L("Session.Pin.Unpin", "Unpin")
        : L("Session.Pin.Pin", "Pin");

    partial void OnTitleChanged(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            Title = _newConversationTitle;
        }
    }

    /// <summary>切换会话时是否需要在后台静默生成/更新标题。</summary>
    public bool ShouldGenerateTitleSilently => !_autoTitleDisabled && !_titleGeneratedForContent;

    /// <summary>静默标题已生成完毕（消息变更时由 <see cref="OnMessagesChanged"/> 重置）。</summary>
    public void MarkSilentTitleGenerated() => _titleGeneratedForContent = true;

    /// <summary>显式指定标题（手动重命名/分支命名）后调用：此后不再静默覆盖。</summary>
    public void DisableSilentTitleGeneration() => _autoTitleDisabled = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(StatusGlyph))]
    [NotifyPropertyChangedFor(nameof(HasStatusIndicator))]
    [NotifyPropertyChangedFor(nameof(ShowCompletionIndicator))]
    private bool _hasUnreadCompletion;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(StatusGlyph))]
    [NotifyPropertyChangedFor(nameof(HasStatusIndicator))]
    [NotifyPropertyChangedFor(nameof(ShowInterruptedIndicator))]
    [NotifyPropertyChangedFor(nameof(ShowCompletionIndicator))]
    private bool _wasInterrupted;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(StatusGlyph))]
    [NotifyPropertyChangedFor(nameof(HasStatusIndicator))]
    [NotifyPropertyChangedFor(nameof(ShowArchivePendingIndicator))]
    [NotifyPropertyChangedFor(nameof(ShowArchiveFailedIndicator))]
    [NotifyPropertyChangedFor(nameof(ShowInterruptedIndicator))]
    [NotifyPropertyChangedFor(nameof(ShowCompletionIndicator))]
    private bool _isArchivePending;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(StatusGlyph))]
    [NotifyPropertyChangedFor(nameof(HasStatusIndicator))]
    [NotifyPropertyChangedFor(nameof(ShowArchivePendingIndicator))]
    [NotifyPropertyChangedFor(nameof(ShowArchiveFailedIndicator))]
    [NotifyPropertyChangedFor(nameof(ShowInterruptedIndicator))]
    [NotifyPropertyChangedFor(nameof(ShowCompletionIndicator))]
    private bool _isArchiveFailed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private string _archiveStatusText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(StatusGlyph))]
    [NotifyPropertyChangedFor(nameof(HasStatusIndicator))]
    [NotifyPropertyChangedFor(nameof(ShowWaitingIndicator))]
    [NotifyPropertyChangedFor(nameof(ShowQueuedIndicator))]
    [NotifyPropertyChangedFor(nameof(ShowRunningIndicator))]
    [NotifyPropertyChangedFor(nameof(ShowArchivePendingIndicator))]
    [NotifyPropertyChangedFor(nameof(ShowArchiveFailedIndicator))]
    [NotifyPropertyChangedFor(nameof(ShowInterruptedIndicator))]
    [NotifyPropertyChangedFor(nameof(ShowCompletionIndicator))]
    private bool _isWaitingForApproval;

    [ObservableProperty]
    private DateTime _updatedAt = DateTime.Now;

    [ObservableProperty]
    private bool _isRenaming;

    [ObservableProperty]
    private string _renameText = string.Empty;

    [ObservableProperty]
    private bool _isSearchMatch = true;

    public bool IsRunning => Chat.IsSending && !Chat.IsQueued;

    public bool IsQueued => Chat.IsQueued;

    public bool HasStatusIndicator =>
        IsWaitingForApproval || IsQueued || IsRunning || IsArchivePending || IsArchiveFailed || WasInterrupted || HasUnreadCompletion;

    public bool ShowWaitingIndicator => IsWaitingForApproval;
    public bool ShowQueuedIndicator => !IsWaitingForApproval && IsQueued;
    public bool ShowRunningIndicator => !IsWaitingForApproval && !IsQueued && IsRunning;
    public bool ShowArchivePendingIndicator => !IsWaitingForApproval && !IsQueued && !IsRunning && IsArchivePending;
    public bool ShowArchiveFailedIndicator => !IsWaitingForApproval && !IsQueued && !IsRunning && !IsArchivePending && IsArchiveFailed;
    public bool ShowInterruptedIndicator =>
        !IsWaitingForApproval && !IsQueued && !IsRunning && !IsArchivePending && !IsArchiveFailed && WasInterrupted;
    public bool ShowCompletionIndicator =>
        !IsWaitingForApproval && !IsQueued && !IsRunning && !IsArchivePending && !IsArchiveFailed && !WasInterrupted && HasUnreadCompletion;

    public string StatusText => IsWaitingForApproval
        ? L("Session.Status.WaitingForApproval", "Awaiting approval")
        : IsQueued
            ? L("Session.Status.Queued", "Queued")
            : IsRunning
                ? L("Session.Status.Running", "Running")
                : IsArchivePending
                    ? string.IsNullOrWhiteSpace(ArchiveStatusText)
                        ? L("Session.Status.Archiving", "Archiving…")
                        : ArchiveStatusText
                    : IsArchiveFailed
                        ? string.IsNullOrWhiteSpace(ArchiveStatusText)
                            ? L("Session.Status.ArchiveFailed", "Archive failed; retry pending")
                            : ArchiveStatusText
                        : WasInterrupted
                            ? L("Session.Status.Interrupted", "Interrupted")
                            : HasUnreadCompletion
                                ? L("Session.Status.Completed", "Completed")
                                : string.Empty;

    public string StatusGlyph => IsWaitingForApproval
        ? "◆"
        : IsQueued || IsRunning || IsArchivePending
            ? "●"
            : IsArchiveFailed || WasInterrupted
                ? "!"
                : HasUnreadCompletion
                    ? "✓"
                    : string.Empty;

    public void SetArchivePending(string statusText)
    {
        ArchiveStatusText = statusText;
        IsArchiveFailed = false;
        IsArchivePending = true;
    }

    public void SetArchiveCompleted()
    {
        IsArchivePending = false;
        IsArchiveFailed = false;
        ArchiveStatusText = string.Empty;
    }

    public void SetArchiveFailed(string statusText)
    {
        ArchiveStatusText = statusText;
        IsArchivePending = false;
        IsArchiveFailed = true;
    }

    partial void OnIsPinnedChanged(bool value)
    {
        if (!_metadataTrackingEnabled) return;
        Chat.MarkPersistenceMetadataChanged();
        ScheduleSave();
        PinChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnIsSelectedChanged(bool value)
    {
        if (value) HasUnreadCompletion = false;
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusGlyph));
    }

    [RelayCommand]
    private void TogglePinned() => IsPinned = !IsPinned;

    [RelayCommand]
    private void Stop() => Chat.StopResponseCommand.Execute(null);

    [RelayCommand]
    private void Rename(string? title)
    {
        if (!string.IsNullOrWhiteSpace(title))
        {
            Title = title.Trim();
            // 手动重命名是用户显式选择，静默标题生成不再覆盖。
            _autoTitleDisabled = true;
            Chat.MarkPersistenceMetadataChanged();
        }
        ScheduleSave();
    }

    [RelayCommand]
    private void StartRename()
    {
        RenameText = Title;
        IsRenaming = true;
    }

    [RelayCommand]
    private void CommitRename()
    {
        Rename(RenameText);
        IsRenaming = false;
    }

    [RelayCommand]
    private void RequestDelete() => DeleteRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void RequestFork() => ForkRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void RequestExport() => ExportRequested?.Invoke(this, EventArgs.Empty);

    private void OnChatPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainConversationViewModel.IsSending) or nameof(MainConversationViewModel.IsQueued))
        {
            if (e.PropertyName == nameof(MainConversationViewModel.IsSending) && !Chat.IsSending && !IsSelected)
            {
                HasUnreadCompletion = true;
            }
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(IsQueued));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(StatusGlyph));
            OnPropertyChanged(nameof(HasStatusIndicator));
            OnPropertyChanged(nameof(ShowQueuedIndicator));
            OnPropertyChanged(nameof(ShowRunningIndicator));
            OnPropertyChanged(nameof(ShowArchivePendingIndicator));
            OnPropertyChanged(nameof(ShowArchiveFailedIndicator));
            OnPropertyChanged(nameof(ShowInterruptedIndicator));
            OnPropertyChanged(nameof(ShowCompletionIndicator));
        }

        if (e.PropertyName == nameof(MainConversationViewModel.InputText)) ScheduleSave();
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdatedAt = DateTime.Now;
        if (Title == _newConversationTitle)
        {
            var firstUserMessage = Chat.Messages.FirstOrDefault(message => message.Role == "user" && !string.IsNullOrWhiteSpace(message.Content));
            if (firstUserMessage != null)
            {
                Title = firstUserMessage.Content.Length > 32
                    ? firstUserMessage.Content[..32] + "…"
                    : firstUserMessage.Content;
            }
        }
        // 消息内容已变化，此前生成的静默标题失效，下次切换会话时重新生成。
        _titleGeneratedForContent = false;
        ScheduleSave();
    }

    private void OnPersistenceStateChanged(object? sender, EventArgs e)
    {
        UpdatedAt = DateTime.Now;
        _ = PersistObservedAsync();
    }

    private void OnChatMessageForkRequested(object? sender, MessageForkRequestedEventArgs e) =>
        MessageForkRequested?.Invoke(this, e);

    private async Task PersistObservedAsync()
    {
        try
        {
            await PersistNowAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Conversation persistence failed: {ex}");
        }
    }

    public async Task PersistNowAsync()
    {
        if (_store == null || (Chat.Messages.Count == 0 && string.IsNullOrWhiteSpace(Chat.InputText))) return;
        ConversationPersistenceSnapshot snapshot;
        if (Dispatcher.UIThread.CheckAccess())
        {
            snapshot = Chat.CapturePersistenceSnapshot(HistoryId, Title, UpdatedAt, IsPinned, Workspace?.Id);
        }
        else
        {
            snapshot = await Dispatcher.UIThread.InvokeAsync(
                () => Chat.CapturePersistenceSnapshot(HistoryId, Title, UpdatedAt, IsPinned, Workspace?.Id));
        }

        var item = ToHistoryItem(snapshot);
        // 快照已在 UI 线程捕获完成，后续 SQLite 写入无需再回 UI 线程；
        // 加上 ConfigureAwait(false) 避免保存批次串行钉在 UI 线程上。
        await _persistGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _store.SaveAsync(item).ConfigureAwait(false);
        }
        finally
        {
            _persistGate.Release();
        }
    }

    public async Task<CompressionCommitResult> CommitCompressionAsync(
        CompressionTransition transition,
        CancellationToken cancellationToken = default)
    {
        if (_store == null)
            return CompressionCommitResult.Failed(
                CompressionCommitStatus.PersistenceUnavailable,
                Chat.Revision,
                "Conversation persistence is unavailable; compression was not applied.");

        var commitUpdatedAt = DateTime.Now;
        (ConversationPersistenceSnapshot? Snapshot, string Error) prepared;
        ConversationHistoryItem? item = null;
        await _persistGate.WaitAsync(cancellationToken);
        try
        {
            // The immutable after-snapshot belongs inside the per-session writer gate. A queued
            // ordinary save or a tool-result publication must not make a pre-captured transition
            // stale while it is waiting for the durable writer.
            cancellationToken.ThrowIfCancellationRequested();
            prepared = Dispatcher.UIThread.CheckAccess()
                ? Chat.PrepareCompressionCommitSnapshot(
                    transition, HistoryId, Title, commitUpdatedAt, IsPinned, Workspace?.Id)
                : await Dispatcher.UIThread.InvokeAsync(() => Chat.PrepareCompressionCommitSnapshot(
                    transition, HistoryId, Title, commitUpdatedAt, IsPinned, Workspace?.Id));
            if (prepared.Snapshot == null)
                return CompressionCommitResult.Failed(
                    CompressionCommitStatus.Stale,
                    Chat.Revision,
                    prepared.Error);

            item = ToHistoryItem(prepared.Snapshot);
            await _store.SaveAsync(item);
        }
        catch (Exception ex)
        {
            return CompressionCommitResult.Failed(
                ex is ConversationRevisionConflictException
                    ? CompressionCommitStatus.Stale
                    : CompressionCommitStatus.PersistenceFailed,
                Chat.Revision,
                ex.Message);
        }
        finally
        {
            _persistGate.Release();
        }

        var committedSnapshot = prepared.Snapshot!;
        var published = Dispatcher.UIThread.CheckAccess()
            ? Chat.PublishCompressionCommit(transition, committedSnapshot.Revision, HistoryId)
            : await Dispatcher.UIThread.InvokeAsync(() =>
                Chat.PublishCompressionCommit(transition, committedSnapshot.Revision, HistoryId));
        if (!published)
        {
            // The durable snapshot is authoritative. If publication raced with an unexpected
            // local mutation, reload exactly what was committed rather than leaving split state.
            if (Dispatcher.UIThread.CheckAccess()) Chat.RestorePersistedConversation(item!);
            else await Dispatcher.UIThread.InvokeAsync(() => Chat.RestorePersistedConversation(item!));
        }
        UpdatedAt = commitUpdatedAt;
        return CompressionCommitResult.Committed(committedSnapshot.Revision);
    }

    public async Task<CompressionCommitResult> CommitUndoCompressionAsync(
        CompressionUndoTransition transition,
        CancellationToken cancellationToken = default)
    {
        if (_store == null)
            return CompressionCommitResult.Failed(
                CompressionCommitStatus.PersistenceUnavailable,
                Chat.Revision,
                "Conversation persistence is unavailable; compression undo was not applied.");

        var commitUpdatedAt = DateTime.Now;
        (ConversationPersistenceSnapshot? Snapshot, string Error) prepared;
        ConversationHistoryItem? item = null;
        await _persistGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            prepared = Dispatcher.UIThread.CheckAccess()
                ? Chat.PrepareCompressionUndoSnapshot(
                    transition, HistoryId, Title, commitUpdatedAt, IsPinned, Workspace?.Id)
                : await Dispatcher.UIThread.InvokeAsync(() => Chat.PrepareCompressionUndoSnapshot(
                    transition, HistoryId, Title, commitUpdatedAt, IsPinned, Workspace?.Id));
            if (prepared.Snapshot == null)
                return CompressionCommitResult.Failed(
                    CompressionCommitStatus.Stale,
                    Chat.Revision,
                    prepared.Error);

            item = ToHistoryItem(prepared.Snapshot);
            await _store.SaveAsync(item);
        }
        catch (Exception ex)
        {
            return CompressionCommitResult.Failed(
                ex is ConversationRevisionConflictException
                    ? CompressionCommitStatus.Stale
                    : CompressionCommitStatus.PersistenceFailed,
                Chat.Revision,
                ex.Message);
        }
        finally
        {
            _persistGate.Release();
        }

        var committedSnapshot = prepared.Snapshot!;
        var published = Dispatcher.UIThread.CheckAccess()
            ? Chat.PublishCompressionUndo(transition, committedSnapshot.Revision, HistoryId)
            : await Dispatcher.UIThread.InvokeAsync(() =>
                Chat.PublishCompressionUndo(transition, committedSnapshot.Revision, HistoryId));
        if (!published)
        {
            if (Dispatcher.UIThread.CheckAccess()) Chat.RestorePersistedConversation(item!);
            else await Dispatcher.UIThread.InvokeAsync(() => Chat.RestorePersistedConversation(item!));
        }
        UpdatedAt = commitUpdatedAt;
        return CompressionCommitResult.Committed(committedSnapshot.Revision);
    }

    internal static ConversationHistoryItem ToHistoryItem(ConversationPersistenceSnapshot snapshot)
    {
        return new ConversationHistoryItem
        {
            SchemaVersion = snapshot.SchemaVersion,
            Revision = snapshot.Revision,
            Id = snapshot.HistoryId!,
            ConversationId = snapshot.ConversationId,
            Summary = snapshot.Title,
            CreatedAt = snapshot.CreatedAt,
            UpdatedAt = snapshot.UpdatedAt,
            MessageCount = snapshot.Messages.Count(ConversationArchiveStore.IsCountableMessage),
            Messages = snapshot.Messages,
            ContextSummary = snapshot.ContextSummary,
            OrphanedLegacySummary = snapshot.OrphanedLegacySummary,
            CompressionHistory = snapshot.CompressionHistory,
            WorkspaceId = snapshot.WorkspaceId,
            Draft = snapshot.Draft,
            IsPinned = snapshot.IsPinned,
            RuntimeStatus = snapshot.RuntimeStatus,
            ForkedFromConversationId = snapshot.ForkedFromConversationId,
            ForkedFromHistoryId = snapshot.ForkedFromHistoryId,
            ForkedAtMessageId = snapshot.ForkedAtMessageId
        };
    }

    private void ScheduleSave()
    {
        _saveDebounce?.Cancel();
        _saveDebounce?.Dispose();
        var cts = new CancellationTokenSource();
        _saveDebounce = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(450, cts.Token);
                await PersistObservedAsync();
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    public void Dispose()
    {
        if (_localizationService != null)
        {
            _localizationService.LanguageChanged -= OnLanguageChanged;
        }
        Chat.PropertyChanged -= OnChatPropertyChanged;
        Chat.Messages.CollectionChanged -= OnMessagesChanged;
        Chat.PersistenceStateChanged -= OnPersistenceStateChanged;
        Chat.MessageForkRequested -= OnChatMessageForkRequested;
        _saveDebounce?.Cancel();
        _saveDebounce?.Dispose();
        _saveDebounce = null;
        _persistGate.Dispose();
        Chat.Dispose();
    }
}

public partial class WorkspaceConversationGroupViewModel : ViewModelBase
{
    /// <summary>注入全局对话的"历史目录"回退路径，菜单"在文件夹中显示"/"复制路径"会用到它。</summary>
    private readonly string _globalDirectoryPath;
    private readonly ILocalizationService? _localizationService;
    private bool _disposed;

    public WorkspaceConversationGroupViewModel(
        WorkspaceProfile? workspace,
        string globalDirectoryPath = "",
        ILocalizationService? localizationService = null)
    {
        Workspace = workspace;
        _globalDirectoryPath = globalDirectoryPath;
        _localizationService = localizationService;
        Name = workspace?.Name ?? L("MainWindow.Launcher.GlobalChat", "Global chat");
        IsExpanded = true;
        if (_localizationService != null)
        {
            _localizationService.LanguageChanged += OnLanguageChanged;
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (Workspace == null)
        {
            Name = L("MainWindow.Launcher.GlobalChat", "Global chat");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_localizationService != null)
        {
            _localizationService.LanguageChanged -= OnLanguageChanged;
        }
    }

    private string L(string key, string fallback)
        => _localizationService?.GetString(key, fallback) ?? fallback;

    public WorkspaceProfile? Workspace { get; }

    public bool IsWorkspace => Workspace != null;

    [ObservableProperty]
    private string _name;

    public string DirectoryPath => Workspace?.DirectoryPath ?? _globalDirectoryPath;

    public System.Collections.ObjectModel.ObservableCollection<ConversationSessionItemViewModel> Conversations { get; } = new();

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isSearchMatch = true;

    [ObservableProperty]
    private bool _isRenaming;

    [ObservableProperty]
    private string _renameText = string.Empty;

    public event EventHandler? RenameCommitted;
    public event EventHandler? RevealRequested;
    public event EventHandler? CopyPathRequested;
    public event EventHandler? ContextSettingsRequested;
    public event EventHandler? DeleteRequested;

    [RelayCommand]
    private void StartRename()
    {
        if (!IsWorkspace) return;
        RenameText = Name;
        IsRenaming = true;
    }

    [RelayCommand]
    private void CommitRename()
    {
        if (!IsWorkspace || string.IsNullOrWhiteSpace(RenameText))
        {
            IsRenaming = false;
            return;
        }

        Name = RenameText.Trim();
        Workspace!.Name = Name;
        IsRenaming = false;
        RenameCommitted?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void RequestReveal()
    {
        // 全局对话也有自己的目录（AthenaData/history），菜单的"在文件夹中显示"应当对所有 group 都可用。
        RevealRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void RequestCopyPath()
    {
        CopyPathRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void RequestContextSettings()
    {
        if (IsWorkspace) ContextSettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void RequestDelete()
    {
        if (IsWorkspace) DeleteRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;
}
