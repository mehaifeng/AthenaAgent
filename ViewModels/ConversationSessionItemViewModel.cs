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
    private CancellationTokenSource? _saveDebounce;
    private readonly SemaphoreSlim _persistGate = new(1, 1);
    private bool _metadataTrackingEnabled;

    public event EventHandler? DeleteRequested;
    public event EventHandler? ForkRequested;
    public event EventHandler? ExportRequested;
    public event EventHandler? PinChanged;

    public ConversationSessionItemViewModel(
        MainConversationViewModel chat,
        WorkspaceProfile? workspace,
        IConversationArchiveStore? store,
        string? historyId = null)
    {
        Chat = chat;
        Workspace = workspace;
        HistoryId = string.IsNullOrWhiteSpace(historyId) ? Guid.NewGuid().ToString() : historyId;
        _store = store;
        Chat.PropertyChanged += OnChatPropertyChanged;
        Chat.Messages.CollectionChanged += OnMessagesChanged;
        Chat.PersistenceStateChanged += OnPersistenceStateChanged;
        Chat.AttachCompressionCommitter(this);
        Dispatcher.UIThread.Post(() => _metadataTrackingEnabled = true);
    }

    public MainConversationViewModel Chat { get; }

    public WorkspaceProfile? Workspace { get; }

    public string HistoryId { get; }

    public string ConversationId => Chat.ConversationId;

    public string WorkspaceId => Workspace?.Id ?? string.Empty;

    public string? ForkedFromConversationId { get; init; }

    public string? ForkedFromHistoryId { get; init; }

    public bool IsForked => !string.IsNullOrWhiteSpace(ForkedFromConversationId);

    [ObservableProperty]
    private string _title = "新对话";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PinActionText))]
    private bool _isPinned;

    public string PinActionText => IsPinned ? "取消置顶" : "置顶";

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
        ? "等待审批"
        : IsQueued
            ? "排队中"
            : IsRunning
                ? "运行中"
                : IsArchivePending
                    ? string.IsNullOrWhiteSpace(ArchiveStatusText) ? "正在归档" : ArchiveStatusText
                    : IsArchiveFailed
                        ? string.IsNullOrWhiteSpace(ArchiveStatusText) ? "归档失败，等待重试" : ArchiveStatusText
                        : WasInterrupted
                            ? "已中断"
                            : HasUnreadCompletion
                                ? "已完成"
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
        if (Title == "新对话")
        {
            var firstUserMessage = Chat.Messages.FirstOrDefault(message => message.Role == "user" && !string.IsNullOrWhiteSpace(message.Content));
            if (firstUserMessage != null)
            {
                Title = firstUserMessage.Content.Length > 32
                    ? firstUserMessage.Content[..32] + "…"
                    : firstUserMessage.Content;
            }
        }
        ScheduleSave();
    }

    private void OnPersistenceStateChanged(object? sender, EventArgs e)
    {
        UpdatedAt = DateTime.Now;
        _ = PersistObservedAsync();
    }

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

    private static ConversationHistoryItem ToHistoryItem(ConversationPersistenceSnapshot snapshot)
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
        Chat.PropertyChanged -= OnChatPropertyChanged;
        Chat.Messages.CollectionChanged -= OnMessagesChanged;
        Chat.PersistenceStateChanged -= OnPersistenceStateChanged;
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

    public WorkspaceConversationGroupViewModel(WorkspaceProfile? workspace, string globalDirectoryPath = "")
    {
        Workspace = workspace;
        _globalDirectoryPath = globalDirectoryPath;
        Name = workspace?.Name ?? "全局对话";
        IsExpanded = true;
    }

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
