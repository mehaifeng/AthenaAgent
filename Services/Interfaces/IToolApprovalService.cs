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
    /// 需交互时弹窗（仅 Interactive 模式）或按无人值守策略裁决 → 审计记录。
    /// </summary>
    Task<ToolApprovalDecision> EvaluateAsync(string functionName, string argumentsJson, CancellationToken cancellationToken);
}

/// <summary>Automatic 审批模式下，用无工具模型对一次调用作 fail-closed 裁决。</summary>
public interface IAiToolApprovalEvaluator
{
    Task<ToolApprovalDecision> EvaluateAsync(ToolApprovalRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// 审批弹窗展示器（UI 层实现）。把服务层的策略与 UI 解耦：服务只依赖此接口，
/// 具体在 UI 线程弹 <c>ToolApprovalDialog</c> 的实现由 ViewModels 层提供。
/// </summary>
public interface IToolApprovalPrompter
{
    /// <summary>在 UI 线程弹出审批窗并等待用户决策。返回用户选择的范围。</summary>
    Task<ToolApprovalScope> PromptAsync(ToolApprovalRequest request, CancellationToken cancellationToken);
}
