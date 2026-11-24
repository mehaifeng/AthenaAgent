using System.Linq;
using System.Collections.Generic;
using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.ViewModels;

/// <summary>Global approval queue presented above the conversation composer.</summary>
public sealed class ApprovalQueueViewModel : ViewModelBase, IToolApprovalPrompter
{
    // The queue shares one system notification which can be refreshed or withdrawn by key.
    private const string ApprovalNotificationKey = "athena.tool-approval";

    private readonly IConversationSessionAccessor? _sessionAccessor;
    private readonly ILocalizationService? _localization;
    private readonly ISystemNotificationService? _notifications;
    private readonly IAppForegroundProbe? _foregroundProbe;
    private readonly ILogger _logger;
    private bool _notificationPosted;
    private Task _notificationUpdate = Task.CompletedTask;

    public ApprovalQueueViewModel(
        IConversationSessionAccessor? sessionAccessor,
        ILogger logger,
        ILocalizationService? localization = null,
        ISystemNotificationService? notifications = null,
        IAppForegroundProbe? foregroundProbe = null)
    {
        _sessionAccessor = sessionAccessor;
        _logger = logger.ForContext<ApprovalQueueViewModel>();
        _localization = localization;
        _notifications = notifications;
        _foregroundProbe = foregroundProbe;
        if (_localization != null)
        {
            _localization.LanguageChanged += OnLanguageChanged;
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        foreach (var item in Pending)
        {
            item.RefreshLocalizedText();
        }
    }

    public ObservableCollection<ApprovalQueueItemViewModel> Pending { get; } = new();
    public bool HasPending => Pending.Count > 0;
    private string? _displayedConversationId;
    public IReadOnlyList<ApprovalQueueItemViewModel> VisiblePending => Pending
        .Where(item => item.Request.AllowsPersistentApproval || item.ConversationId == _displayedConversationId).ToArray();
    public bool HasVisiblePending => VisiblePending.Count > 0;

    internal void ShowConversation(string? id)
    {
        if (_displayedConversationId == id) return;
        _displayedConversationId = id;
        RefreshVisibleItems();
    }

    private void RefreshVisibleItems()
    {
        OnPropertyChanged(nameof(VisiblePending));
        OnPropertyChanged(nameof(HasVisiblePending));
    }

    private string L(string key, string fallback)
        => _localization?.GetString(key, fallback) ?? fallback;

    public async Task<ToolApprovalScope> PromptAsync(ToolApprovalRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var item = new ApprovalQueueItemViewModel(
            request,
            _sessionAccessor?.CurrentConversationId,
            L,
            (entry, scope) => Complete(entry, scope, cancellationToken));
        using var cancellation = cancellationToken.Register(() =>
            Dispatcher.UIThread.Post(() => Complete(item, ToolApprovalScope.Deny, cancellationToken)));
        Dispatcher.UIThread.Post(() =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                Complete(item, ToolApprovalScope.Deny, cancellationToken);
                return;
            }
            Pending.Add(item);
            OnPendingChanged();
            _logger.Information("Approval queue received new request; pending={Count}", Pending.Count);
        });
        return await item.Completion.Task.ConfigureAwait(false);
    }

    private void Complete(ApprovalQueueItemViewModel item, ToolApprovalScope result, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            if (!item.Completion.TrySetCanceled(cancellationToken)) return;
        }
        else
        {
            if (!item.Completion.TrySetResult(result)) return;
            _logger.Information(
                "ApprovalQueue user decision: Function={Function}, Scope={Scope}",
                item.Request.FunctionName, result);
        }
        if (Pending.Remove(item)) OnPendingChanged();
    }

    private void OnPendingChanged()
    {
        OnPropertyChanged(nameof(HasPending));
        RefreshVisibleItems();
        if (HasPending && _foregroundProbe?.IsForeground != true) PostApprovalNotification();
        else WithdrawApprovalNotification();
    }

    /// <summary>
    /// 发出/刷新审批通知。审批是三个通知场景里唯一**阻塞流程**的那个：
    /// 没人回应，这一轮就一直挂着——审批没有超时，只等有人作答或停止本轮——所以用 Critical——
    /// 在支持的桌面上它不会自己消失。
    /// </summary>
    private void PostApprovalNotification()
    {
        if (_notifications == null || Pending.Count == 0) return;

        var pendingCount = Pending.Count;
        var headline = Pending[0].Title;
        var body = pendingCount > 1
            ? string.Format(
                CultureInfo.CurrentCulture,
                L("Notification.Approval.BodyMultiple", "{0} (and {1} more waiting)"),
                headline,
                pendingCount - 1)
            : headline;

        var request = new SystemNotificationRequest
        {
            Title = L("Notification.Approval.Title", "Athena needs your approval"),
            Body = body,
            Urgency = SystemNotificationUrgency.Critical,
            Key = ApprovalNotificationKey
        };

        _notificationPosted = true;
        // Serialize updates so an in-flight show finishes before a replacement or withdrawal.
        _notificationUpdate = SafeNotifyAsync(_notificationUpdate, request);
    }

    internal void WithdrawApprovalNotification()
    {
        if (_notifications == null || !_notificationPosted) return;
        _notificationPosted = false;
        _notificationUpdate = SafeWithdrawAsync(_notificationUpdate);
    }

    private async Task SafeNotifyAsync(Task previous, SystemNotificationRequest request)
    {
        try
        {
            await previous.ConfigureAwait(false);
            await _notifications!.ShowAsync(request).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // ISystemNotificationService 本身已经吞异常，这里只是最后一道
            // 防止 async void 式的未观察异常拖垮进程的保险。
            _logger.Debug(ex, "Approval notification failed");
        }
    }

    private async Task SafeWithdrawAsync(Task previous)
    {
        try
        {
            await previous.ConfigureAwait(false);
            await _notifications!.WithdrawAsync(ApprovalNotificationKey).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Approval notification could not be withdrawn");
        }
    }
}

public partial class ApprovalQueueItemViewModel : ViewModelBase
{
    private readonly Action<ApprovalQueueItemViewModel, ToolApprovalScope> _complete;
    private readonly Func<string, string, string> _localize;

    public ApprovalQueueItemViewModel(
        ToolApprovalRequest request,
        string? conversationId,
        Func<string, string, string> localize,
        Action<ApprovalQueueItemViewModel, ToolApprovalScope> complete)
    {
        Request = request;
        ConversationId = conversationId ?? string.Empty;
        _localize = localize;
        _complete = complete;
    }

    public ToolApprovalRequest Request { get; }
    public string ConversationId { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConversationLabel))]
    private string? _conversationTitle;

    public string Title => string.IsNullOrWhiteSpace(Request.Summary) ? Request.FunctionName : Request.Summary;

    public string ConversationLabel => string.IsNullOrEmpty(ConversationId)
        ? _localize("Approval.ConversationUnknown", "Unknown conversation")
        : string.Format(_localize("Approval.ConversationPrefix", "Conversation: {0}"),
            string.IsNullOrWhiteSpace(ConversationTitle) ? ConversationId : ConversationTitle);

    public string? RiskText => (Request.Risk, Request.ReviewRisk) switch
    {
        (ToolRisk.ReadOnly, _) => _localize("Dialog.ToolApproval.Risk.ReadOnly", "Read-only"),
        (ToolRisk.AdditiveWrite, _) => _localize("Dialog.ToolApproval.Risk.AdditiveWrite", "New file only"),
        (ToolRisk.Sensitive, _) => _localize("Dialog.ToolApproval.Risk.Sensitive", "Sensitive"),
        (ToolRisk.Destructive, _) or (_, "high") => _localize("Approval.RiskHigh", "High risk"),
        (_, "low") => _localize("Dialog.ToolApproval.ReviewRisk.Low", "Low risk"),
        (_, "medium") => _localize("Dialog.ToolApproval.ReviewRisk.Medium", "Medium risk"),
        (_, "critical") => _localize("Dialog.ToolApproval.ReviewRisk.Critical", "Critical risk"),
        _ => null
    };

    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(ConversationLabel));
        OnPropertyChanged(nameof(RiskText));
    }

    public TaskCompletionSource<ToolApprovalScope> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    [RelayCommand] private void AllowOnce() => _complete(this, ToolApprovalScope.AllowOnce);
    [RelayCommand] private void AllowSession() => _complete(this, ToolApprovalScope.AllowForSession);
    [RelayCommand] private void AllowAlways() => _complete(this, ToolApprovalScope.AllowAlways);
    [RelayCommand] private void Deny() => _complete(this, ToolApprovalScope.Deny);
}
