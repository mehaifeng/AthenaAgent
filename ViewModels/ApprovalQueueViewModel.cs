using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using Athena.UI.Views;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.ViewModels;

/// <summary>Global, non-modal approval queue. Any session can wait; other sessions and the settings window keep working.</summary>
public sealed class ApprovalQueueViewModel : ViewModelBase, IToolApprovalPrompter
{
    /// <summary>
    /// 整个审批队列共用一条系统通知。三个待审批弹三条提示只是噪音——
    /// 它们本来就会收进同一个窗口，用户要做的也是同一个动作：回来看一眼。
    /// 固定的 Key 让后续的 Show 就地替换、清空时能撤回。
    /// </summary>
    private const string ApprovalNotificationKey = "athena.tool-approval";

    private readonly IConversationSessionAccessor? _sessionAccessor;
    private readonly ILocalizationService? _localization;
    private readonly ISystemNotificationService? _notifications;
    private readonly IAppForegroundProbe? _foregroundProbe;
    private readonly ILogger _logger;
    private ApprovalQueueWindow? _window;

    /// <summary>已经发出过通知；用于避免对着空队列反复调用撤回。</summary>
    private bool _notificationPosted;

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

    private string L(string key, string fallback)
        => _localization?.GetString(key, fallback) ?? fallback;

    public Task<ToolApprovalScope> PromptAsync(ToolApprovalRequest request, CancellationToken cancellationToken)
    {
        var item = new ApprovalQueueItemViewModel(
            request,
            _sessionAccessor?.CurrentConversationId,
            L,
            Complete);
        Dispatcher.UIThread.Post(() =>
        {
            Pending.Add(item);
            // 前台状态必须在 EnsureWindow 之前读：它会 Activate 审批窗口，
            // 而在应用已经被激活之后再问「人在不在」，答案永远是「在」。
            var wasForeground = _foregroundProbe?.IsForeground ?? false;
            EnsureWindow();
            if (!wasForeground) PostApprovalNotification();
        });
        if (cancellationToken.CanBeCanceled)
        {
            cancellationToken.Register(() => Dispatcher.UIThread.Post(() => Complete(item, ToolApprovalScope.Deny)));
        }
        return item.Completion.Task;
    }

    private void Complete(ApprovalQueueItemViewModel item, ToolApprovalScope result)
    {
        if (!item.Completion.TrySetResult(result)) return;
        _logger.Information(
            "ApprovalQueue user decision: Function={Function}, Scope={Scope}",
            item.Request?.FunctionName, result);
        Pending.Remove(item);
        if (Pending.Count == 0)
        {
            _window?.Hide();
            WithdrawApprovalNotification();
        }
        else
        {
            // 队列里还剩东西，通知得跟着改口——否则用户看到的还是那条
            // 指向已经处理完的调用的旧提示。
            PostApprovalNotification();
        }
    }

    private void EnsureWindow()
    {
        if (_window == null)
        {
            _window = new ApprovalQueueWindow
            {
                DataContext = this
            };
            _window.Closing += (_, e) =>
            {
                if (Pending.Count > 0)
                {
                    e.Cancel = true;
                    _window.Hide();
                }
            };
            // 人已经站到窗口前面了，那条「快回来看」的通知就该消失。
            _window.Activated += (_, _) => WithdrawApprovalNotification();
        }
        if (!_window.IsVisible) _window.Show();
        _window.Activate();
        _logger.Information("Approval queue received new request; pending={Count}", Pending.Count);
    }

    /// <summary>
    /// 发出/刷新审批通知。审批是三个通知场景里唯一**阻塞流程**的那个：
    /// 没人回应，这一轮就一直挂到超时，所以用 Critical——
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
        // 不 await：通知后端要拉起一个子进程，而这里跑在 UI 线程上，
        // 审批窗口的弹出不该等一次 osascript / PowerShell 启动。
        _ = SafeNotifyAsync(request);
    }

    private void WithdrawApprovalNotification()
    {
        if (_notifications == null || !_notificationPosted) return;
        _notificationPosted = false;
        _ = SafeWithdrawAsync();
    }

    private async Task SafeNotifyAsync(SystemNotificationRequest request)
    {
        try
        {
            await _notifications!.ShowAsync(request).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // ISystemNotificationService 本身已经吞异常，这里只是最后一道
            // 防止 async void 式的未观察异常拖垮进程的保险。
            _logger.Debug(ex, "Approval notification failed");
        }
    }

    private async Task SafeWithdrawAsync()
    {
        try
        {
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

    public string Title => Request.Summary;

    public string ConversationIdLabel => string.IsNullOrEmpty(ConversationId)
        ? _localize("Approval.ConversationUnknown", "Unknown conversation")
        : string.Format(_localize("Approval.ConversationPrefix", "Conversation: {0}"), ConversationId);

    public string RiskText => Request.IsDestructive
        ? _localize("Approval.RiskHigh", "High risk")
        : Request.Risk.ToString();

    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(ConversationIdLabel));
        OnPropertyChanged(nameof(RiskText));
    }

    public TaskCompletionSource<ToolApprovalScope> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    [RelayCommand] private void AllowOnce() => _complete(this, ToolApprovalScope.AllowOnce);
    [RelayCommand] private void AllowSession() => _complete(this, ToolApprovalScope.AllowForSession);
    [RelayCommand] private void AllowAlways() => _complete(this, ToolApprovalScope.AllowAlways);
    [RelayCommand] private void Deny() => _complete(this, ToolApprovalScope.Deny);
}