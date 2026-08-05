using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using Athena.UI.Views;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Serilog;
using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.ViewModels;

/// <summary>Global, non-modal approval queue. Any session can wait; other sessions and the settings window keep working.</summary>
public sealed class ApprovalQueueViewModel : ViewModelBase, IToolApprovalPrompter
{
    private readonly IConversationSessionAccessor? _sessionAccessor;
    private readonly ILocalizationService? _localization;
    private readonly ILogger _logger;
    private ApprovalQueueWindow? _window;

    public ApprovalQueueViewModel(
        IConversationSessionAccessor? sessionAccessor,
        ILogger logger,
        ILocalizationService? localization = null)
    {
        _sessionAccessor = sessionAccessor;
        _logger = logger.ForContext<ApprovalQueueViewModel>();
        _localization = localization;
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
        _logger.Information(
            "ApprovalQueue user decision: Function={Function}, Scope={Scope}",
            item.Request?.FunctionName, result);
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
        _logger.Information("Approval queue received new request; pending={Count}", Pending.Count);
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