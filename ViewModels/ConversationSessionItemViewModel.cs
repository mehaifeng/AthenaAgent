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

namespace Athena.UI.ViewModels;

public partial class ConversationSessionItemViewModel : ViewModelBase, IDisposable
{
    private readonly IConversationArchiveStore? _store;
    private CancellationTokenSource? _saveDebounce;

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
        if (!string.IsNullOrWhiteSpace(title)) Title = title.Trim();
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

    public async Task PersistNowAsync()
    {
        if (_store == null || (Chat.Messages.Count == 0 && string.IsNullOrWhiteSpace(Chat.InputText))) return;
        var item = new ConversationHistoryItem
        {
            Id = HistoryId,
            ConversationId = Chat.ConversationId,
            Summary = Title,
            CreatedAt = Chat.Messages.FirstOrDefault()?.Timestamp ?? UpdatedAt,
            UpdatedAt = UpdatedAt,
            MessageCount = Chat.Messages.Count(ConversationArchiveStore.IsCountableMessage),
            Messages = ConversationPersistenceHelper.CloneMessages(Chat.Messages),
            WorkspaceId = Workspace?.Id,
            Draft = Chat.InputText,
            IsPinned = IsPinned,
            RuntimeStatus = Chat.IsSending ? "interrupted" : "idle",
            ForkedFromConversationId = ForkedFromConversationId,
            ForkedFromHistoryId = ForkedFromHistoryId
        };
        await _store.SaveAsync(item);
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
                await PersistNowAsync();
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
        _saveDebounce?.Cancel();
        _saveDebounce?.Dispose();
        _saveDebounce = null;
        Chat.Dispose();
    }
}

public partial class WorkspaceConversationGroupViewModel : ViewModelBase
{
    public WorkspaceConversationGroupViewModel(WorkspaceProfile? workspace)
    {
        Workspace = workspace;
        Name = workspace?.Name ?? "全局对话";
        IsExpanded = true;
    }

    public WorkspaceProfile? Workspace { get; }

    public bool IsWorkspace => Workspace != null;

    [ObservableProperty]
    private string _name;

    public string DirectoryPath => Workspace?.DirectoryPath ?? string.Empty;

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
        if (IsWorkspace) RevealRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void RequestCopyPath()
    {
        if (IsWorkspace) CopyPathRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void RequestDelete()
    {
        if (IsWorkspace) DeleteRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;
}
