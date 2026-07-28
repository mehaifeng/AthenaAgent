using System;
using System.Threading;
using System.Threading.Tasks;
using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using Athena.UI.Views;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Serilog;

namespace Athena.UI.ViewModels;

/// <summary>
/// 审批弹窗展示器（UI 层实现）。在 UI 线程弹出 <see cref="ToolApprovalDialog"/> 并等待用户决策。
/// 用信号量串行化弹窗，避免同一时刻叠出多个审批窗。
/// </summary>
public class ToolApprovalPrompter : IToolApprovalPrompter, IDisposable
{
    private readonly ILocalizationService? _localizationService;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ToolApprovalPrompter(ILocalizationService? localizationService, ILogger logger)
    {
        _localizationService = localizationService;
        _logger = logger.ForContext<ToolApprovalPrompter>();
    }

    public async Task<ToolApprovalScope> PromptAsync(ToolApprovalRequest request, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await Dispatcher.UIThread.InvokeAsync(() => ShowDialogAsync(request, cancellationToken));
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ToolApprovalScope> ShowDialogAsync(ToolApprovalRequest request, CancellationToken cancellationToken)
    {
        var owner = GetMainWindow();
        if (owner == null)
        {
            // 没有可作 owner 的主窗口（异常情况）：安全起见拒绝。
            _logger.Warning("无主窗口可弹审批窗，默认拒绝 {Function}", request.FunctionName);
            return ToolApprovalScope.Deny;
        }

        string Localize(string key, string fallback) =>
            _localizationService?.GetString(key, fallback) ?? fallback;

        var vm = new ToolApprovalDialogViewModel(request, Localize);
        var dialog = new ToolApprovalDialog(vm);

        // 用户在别处点了「停止」时，关闭悬挂的审批窗并按拒绝处理。
        using var registration = cancellationToken.CanBeCanceled
            ? cancellationToken.Register(() => Dispatcher.UIThread.Post(() =>
            {
                try { dialog.Close(); } catch { /* 已关闭 */ }
            }))
            : default;

        await dialog.ShowDialog(owner);
        return vm.Result ?? ToolApprovalScope.Deny;
    }

    private static Avalonia.Controls.Window? GetMainWindow()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            return desktop.MainWindow;
        }
        return null;
    }

    public void Dispose() => _gate.Dispose();
}
