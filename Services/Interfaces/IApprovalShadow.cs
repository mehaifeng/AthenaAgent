using Athena.UI.Models;

namespace Athena.UI.Services.Interfaces;

/// <summary>
/// 审批弹窗的影子评估：用户被询问的那一刻，在后台让决策模型给同一次调用打分，
/// 分数与用户最终的选择写进同一条日志。它只收集数据，从不参与裁决——
/// <see cref="Begin"/> 与 <see cref="IApprovalShadowRun.Complete"/> 都立即返回、不抛异常。
/// </summary>
public interface IApprovalShadow
{
    /// <summary>开始给一次即将弹窗询问的调用打分；跑不了（没有可用连接等）时记日志并返回 null。</summary>
    IApprovalShadowRun? Begin(ApprovalShadowCall call);
}

public interface IApprovalShadowRun
{
    /// <summary>登记用户的决定；分数到达后写一条合并日志。只认第一次调用。</summary>
    void Complete(string decision);
}

/// <param name="DelegatedTask">主对话里用户最近的请求（<c>ToolApprovalContext.DescribeTask</c>），可能为 null。</param>
/// <param name="WorkspaceId">当前会话的工作区；目录在后台解析，审批路径不等它。</param>
public sealed record ApprovalShadowCall(
    string FunctionName,
    string ArgumentsJson,
    ToolRisk Risk,
    string? DelegatedTask,
    string? WorkspaceId);
