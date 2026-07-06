using System;

namespace Athena.UI.Models;

/// <summary>
/// 工具风险分级。决定某次工具调用默认是否需要人工审批。
/// - ReadOnly：只读 / 无副作用（读文件、检索、查看配置），均衡模式下自动放行。
/// - Sensitive：写本地状态 / 外部副作用 / 花钱（写文件、生图、浏览器、派发子代理），默认每次询问。
/// - Destructive：破坏性且不可逆（删除、危险终端命令），默认每次询问，弹窗走红色高危样式。
/// </summary>
public enum ToolRisk
{
    ReadOnly,
    Sensitive,
    Destructive
}

/// <summary>
/// 全局审批模式。
/// - Off：全部自动放行（老行为，给完全信任的用户）。
/// - Balanced：ReadOnly 放行，Sensitive/Destructive 询问（默认，推荐）。
/// - Strict：全部工具都询问（含只读）。
/// </summary>
public enum ToolApprovalMode
{
    Off,
    Balanced,
    Strict
}

/// <summary>
/// 用户对一次审批请求做出的决策范围。
/// </summary>
public enum ToolApprovalScope
{
    /// <summary>拒绝本次调用。</summary>
    Deny,
    /// <summary>仅允许本次。</summary>
    AllowOnce,
    /// <summary>本会话内该工具（终端命令则为该命令）不再询问（内存态）。</summary>
    AllowForSession,
    /// <summary>永久放行该工具，写入配置。</summary>
    AllowAlways
}

/// <summary>
/// 一次工具调用的审批请求。由 chokepoint（FunctionRegistry）在执行前构造，
/// 交给审批服务裁决；需要交互时透传给弹窗展示。
/// </summary>
public sealed class ToolApprovalRequest
{
    public required string FunctionName { get; init; }
    public required ToolRisk Risk { get; init; }

    /// <summary>人类可读的一行摘要（复用 ToolCallDisplay.Summarize）。</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>美化后的参数 JSON（复用 ToolCallDisplay.PrettyArguments），弹窗「详情」展示。</summary>
    public string PrettyArguments { get; init; } = string.Empty;

    /// <summary>
    /// 终端命令专用：拼接后的完整命令行（command + arguments），
    /// 让用户看清真正要执行的命令，而不仅是函数名。非终端工具为空。
    /// </summary>
    public string? CommandLine { get; init; }

    /// <summary>高危原因说明（如「删除操作不可逆」「命令包含 sudo」），可空。</summary>
    public string? RiskReason { get; init; }

    public bool IsTerminal => !string.IsNullOrEmpty(CommandLine);
    public bool IsDestructive => Risk == ToolRisk.Destructive;

    /// <summary>
    /// 会话内 / 永久放行的去重键。终端命令按「命令名」聚合（同一 command 放行一次即可），
    /// 其余工具按函数名聚合。
    /// </summary>
    public string ApprovalKey { get; init; } = string.Empty;
}

/// <summary>
/// 审批裁决结果。
/// </summary>
public sealed class ToolApprovalDecision
{
    public required ToolApprovalScope Scope { get; init; }

    /// <summary>是否放行执行。</summary>
    public bool Approved => Scope != ToolApprovalScope.Deny;

    /// <summary>裁决来源说明，用于审计日志（如「配置 Off」「只读自动放行」「用户弹窗」「无人值守拒绝」）。</summary>
    public string Reason { get; init; } = string.Empty;

    public static ToolApprovalDecision Allow(ToolApprovalScope scope, string reason) =>
        new() { Scope = scope, Reason = reason };

    public static ToolApprovalDecision AllowOnce(string reason) =>
        new() { Scope = ToolApprovalScope.AllowOnce, Reason = reason };

    public static ToolApprovalDecision Deny(string reason) =>
        new() { Scope = ToolApprovalScope.Deny, Reason = reason };
}
