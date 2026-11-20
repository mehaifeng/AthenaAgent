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
    /// 评估一次工具调用是否放行。内部完成：风险分级 → 配置/白名单/会话放行判定 →
    /// 需交互时请求人工确认（仅 Interactive 模式）或按无人值守策略裁决 → 审计记录。
    /// </summary>
    Task<ToolApprovalDecision> EvaluateAsync(string functionName, string argumentsJson, CancellationToken cancellationToken);
}

/// <summary>自动评审模式下，用独立的无工具模型评审一次调用；证据不足或技术故障交由闸门处理。</summary>
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
