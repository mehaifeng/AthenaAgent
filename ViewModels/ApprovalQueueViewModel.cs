using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using Athena.UI.Views;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.ViewModels;

/// <summary>全局、非模态审批队列。任意会话可等待，其他会话和设置窗口继续工作。</summary>
public sealed class ApprovalQueueViewModel : ViewModelBase, IToolApprovalPrompter
{
    private readonly IConversationSessionAccessor? _sessionAccessor;
    private readonly ILogger _logger;
    private ApprovalQueueWindow? _window;

    public ApprovalQueueViewModel(IConversationSessionAccessor? sessionAccessor, ILogger logger)
    {
        _sessionAccessor = sessionAccessor;
        _logger = logger.ForContext<ApprovalQueueViewModel>();
    }

    public ObservableCollection<ApprovalQueueItemViewModel> Pending { get; } = new();

    public Task<ToolApprovalScope> PromptAsync(ToolApprovalRequest request, CancellationToken cancellationToken)
    {
        var item = new ApprovalQueueItemViewModel(
            request,
            _sessionAccessor?.CurrentConversationId,
            Complete);
        Dispatcher.UIThread.Post(() =>
        {
            Pending.Add(item);
            EnsureWindow();
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
        Pending.Remove(item);
        if (Pending.Count == 0 && _window != null)
        {
            _window.Hide();
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
        }
        if (!_window.IsVisible) _window.Show();
        _window.Activate();
        _logger.Information("全局审批队列新增请求，待处理={Count}", Pending.Count);
    }
}

public partial class ApprovalQueueItemViewModel : ViewModelBase
{
    private readonly Action<ApprovalQueueItemViewModel, ToolApprovalScope> _complete;

    public ApprovalQueueItemViewModel(
        ToolApprovalRequest request,
        string? conversationId,
        Action<ApprovalQueueItemViewModel, ToolApprovalScope> complete)
    {
        Request = request;
        ConversationId = conversationId ?? "未知会话";
        _complete = complete;
    }

    public ToolApprovalRequest Request { get; }
    public string ConversationId { get; }
    public string Title => Request.Summary;
    public string RiskText => Request.IsDestructive ? "高风险" : Request.Risk.ToString();
    public TaskCompletionSource<ToolApprovalScope> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    [RelayCommand] private void AllowOnce() => _complete(this, ToolApprovalScope.AllowOnce);
    [RelayCommand] private void AllowSession() => _complete(this, ToolApprovalScope.AllowForSession);
    [RelayCommand] private void AllowAlways() => _complete(this, ToolApprovalScope.AllowAlways);
    [RelayCommand] private void Deny() => _complete(this, ToolApprovalScope.Deny);
}
