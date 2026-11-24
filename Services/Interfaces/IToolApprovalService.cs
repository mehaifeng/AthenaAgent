using System.Threading;
using System.Threading.Tasks;
using Athena.UI.Models;

namespace Athena.UI.Services.Interfaces;

/// <summary>
/// 工具审批服务（策略大脑 + 审计）。由 FunctionRegistry 这个唯一 chokepoint 在执行前调用，
/// 三条工具执行路径（主对话 / 并发子代理 / 知识库维护）都无法绕过。
/// </summary>
public interface IToolApprovalService
{
    /// <summary>
    /// 按当前审批模式裁决一次工具调用，必要时请求用户确认，并记录审计结果。
    /// </summary>
    Task<ToolApprovalDecision> EvaluateAsync(
        string functionName, string argumentsJson, CancellationToken cancellationToken, string? toolDescription = null);
}

/// <summary>自动评审模式下，由独立的无工具模型裁决一次调用。</summary>
public interface IAiToolApprovalEvaluator
{
    Task<ToolApprovalDecision> EvaluateAsync(ToolApprovalRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// 交互审批请求的 UI 接口。桌面端将请求交给主窗体的审批队列。
/// </summary>
public interface IToolApprovalPrompter
{
    /// <summary>提交审批请求并等待用户选择。调用被取消时结束等待。</summary>
    Task<ToolApprovalScope> PromptAsync(ToolApprovalRequest request, CancellationToken cancellationToken);
}
