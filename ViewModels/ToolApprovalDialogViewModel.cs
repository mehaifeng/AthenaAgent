using System;
using Athena.UI.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Athena.UI.ViewModels;

/// <summary>
/// 工具审批弹窗 ViewModel。展示待执行工具的风险、摘要、参数（终端命令展示完整命令行），
/// 提供「拒绝 / 仅本次 / 本会话 / 永久」四档决策。
/// </summary>
public partial class ToolApprovalDialogViewModel : ObservableObject
{
    public ToolApprovalDialogViewModel()
    {
    }

    public ToolApprovalDialogViewModel(ToolApprovalRequest request, Func<string, string, string> localize)
    {
        FunctionName = request.FunctionName;
        Summary = request.Summary;
        PrettyArguments = request.PrettyArguments;
        CommandLine = request.CommandLine ?? string.Empty;
        IsTerminal = request.IsTerminal;
        IsDestructive = request.IsDestructive;
        RiskReason = request.RiskReason ?? string.Empty;
        HasRiskReason = !string.IsNullOrWhiteSpace(RiskReason);
        HasArguments = !IsTerminal && !string.IsNullOrWhiteSpace(PrettyArguments);

        RiskLabel = request.Risk switch
        {
            ToolRisk.ReadOnly => localize("Dialog.ToolApproval.Risk.ReadOnly", "只读"),
            ToolRisk.Sensitive => localize("Dialog.ToolApproval.Risk.Sensitive", "敏感"),
            ToolRisk.Destructive => localize("Dialog.ToolApproval.Risk.Destructive", "破坏性"),
            _ => string.Empty
        };
    }

    [ObservableProperty] private string _functionName = string.Empty;
    [ObservableProperty] private string _riskLabel = string.Empty;
    [ObservableProperty] private string _riskReason = string.Empty;
    [ObservableProperty] private bool _hasRiskReason;
    [ObservableProperty] private string _summary = string.Empty;
    [ObservableProperty] private string _prettyArguments = string.Empty;
    [ObservableProperty] private bool _hasArguments;
    [ObservableProperty] private string _commandLine = string.Empty;
    [ObservableProperty] private bool _isTerminal;
    [ObservableProperty] private bool _isDestructive;

    /// <summary>用户决策结果。窗口关闭后由调用方读取；null 视为拒绝。</summary>
    public ToolApprovalScope? Result { get; private set; }

    public Action? RequestClose { get; set; }

    [RelayCommand]
    private void Deny() => Close(ToolApprovalScope.Deny);

    [RelayCommand]
    private void AllowOnce() => Close(ToolApprovalScope.AllowOnce);

    [RelayCommand]
    private void AllowForSession() => Close(ToolApprovalScope.AllowForSession);

    [RelayCommand]
    private void AllowAlways() => Close(ToolApprovalScope.AllowAlways);

    private void Close(ToolApprovalScope scope)
    {
        Result = scope;
        RequestClose?.Invoke();
    }
}
