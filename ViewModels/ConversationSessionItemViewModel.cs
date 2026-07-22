using Athena.UI.Models;
using Athena.UI.Services;
using Athena.UI.Services.Interfaces;
using Avalonia.Threading;
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

    public ConversationSessionItemViewModel(
        ChatTabViewModel chat,
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

    public ChatTabViewModel Chat { get; }

    public WorkspaceProfile? Workspace { get; }

    public string HistoryId { get; }

    public string ConversationId => Chat.ConversationId;

    public string WorkspaceId => Workspace?.Id ?? string.Empty;

    [ObservableProperty]
    private string _title = "新对话";

    [ObservableProperty]
    private bool _isPinned;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(StatusGlyph))]
    private bool _hasUnreadCompletion;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(StatusGlyph))]
    private bool _wasInterrupted;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(StatusGlyph))]
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

    public string StatusText => IsWaitingForApproval
        ? "等待审批"
        : IsQueued
        ? "排队中"
        : IsRunning
            ? "运行中"
            : WasInterrupted
                ? "已中断"
                : HasUnreadCompletion
                    ? "已完成"
                    : string.Empty;

    public string StatusGlyph => IsWaitingForApproval ? "◆" : IsQueued || IsRunning ? "●" : WasInterrupted ? "!" : HasUnreadCompletion ? "✓" : string.Empty;

    partial void OnIsPinnedChanged(bool value) => ScheduleSave();

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

    private void OnChatPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ChatTabViewModel.IsSending) or nameof(ChatTabViewModel.IsQueued))
        {
            if (e.PropertyName == nameof(ChatTabViewModel.IsSending) && !Chat.IsSending && !IsSelected)
            {
                HasUnreadCompletion = true;
            }
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(IsQueued));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(StatusGlyph));
        }

        if (e.PropertyName == nameof(ChatTabViewModel.InputText)) ScheduleSave();
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
            RuntimeStatus = Chat.IsSending ? "interrupted" : "idle"
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
    }
}

public partial class WorkspaceConversationGroupViewModel : ViewModelBase
{
    public WorkspaceConversationGroupViewModel(WorkspaceProfile? workspace)
    {
        Workspace = workspace;
        IsExpanded = true;
    }

    public WorkspaceProfile? Workspace { get; }

    public string Name => Workspace?.Name ?? "全局对话";

    public string DirectoryPath => Workspace?.DirectoryPath ?? string.Empty;

    public System.Collections.ObjectModel.ObservableCollection<ConversationSessionItemViewModel> Conversations { get; } = new();

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isSearchMatch = true;
}
