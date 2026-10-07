using System;

namespace Athena.UI.Models;

/// <summary>
/// 工具风险分级。决定某次工具调用默认是否需要人工审批。
/// - ReadOnly：只读 / 无副作用（读文件、检索、查看配置），均衡模式下自动放行。
/// - AdditiveWrite：只新增、不替换也不删除——目标已存在即失败（Office 生成/编辑/转换写入新路径、建目录）。
///   既然无法毁掉任何既有内容，均衡模式下自动放行；严格模式仍逐次询问。
/// - Sensitive：写本地状态 / 外部副作用 / 花钱（改写既有文件、生图、浏览器、派发子代理），默认每次询问。
/// - Destructive：破坏性且不可逆（删除、危险终端命令），默认每次询问，弹窗走红色高危样式。
///
/// 追加新档位必须放在末尾：审计日志与配置按枚举数字序列化，插入中间会改变历史值的语义。
/// </summary>
public enum ToolRisk
{
    ReadOnly,
    Sensitive,
    Destructive,
    AdditiveWrite
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
    Strict,
    // 必须追加在末尾：历史配置按枚举数字序列化，插入中间会改变旧值语义。
    Automatic
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
    /// 会话内 / 永久放行的去重键，由 ToolApprovalKey 构造并已带上作用域：
    /// 终端按命令名、下载按主机名、文件写入按目标目录聚合，其余工具按函数名聚合。
    /// </summary>
    public string ApprovalKey { get; init; } = string.Empty;

    /// <summary>
    /// 该键的作用域（命令名 / 主机名 / 目录），用于在弹窗上说清「本会话」「始终」到底放行了多大一片。
    /// 无作用域（按函数名聚合）时为 null。
    /// </summary>
    public string? ApprovalScope { get; init; }
}

/// <summary>
/// 裁决出自谁。被拦下的调用给主模型的那句话按它分开写（见 ToolApprovalDenialMessage）：
/// 「用户拒绝了」只能用在用户真的拒绝时。审批模型自己的拒绝、它没能给出裁决、静态策略的拦截，
/// 各有各的正确应对——此前一律说成用户拒绝、请勿重试，主模型就会向用户转述一个没发生过的拒绝。
/// </summary>
public enum ToolApprovalSource
{
    /// <summary>配置、风险分级与无人值守的静态规则。未显式标注的裁决都属于这一类。</summary>
    Policy,
    /// <summary>用户在审批窗口里做的决定。</summary>
    User,
    /// <summary>本轮执行被取消（用户点了停止、子代理超时），调用没有机会被裁决。</summary>
    Cancelled,
    /// <summary>自动审批模型给出了放行或拒绝。</summary>
    JudgeVerdict,
    /// <summary>自动审批模型没能给出裁决，按安全策略拒绝。类别见 <see cref="ToolApprovalJudgeFailureKind"/>。</summary>
    JudgeFailure
}

/// <summary>自动审批没能给出裁决的原因类别。它决定主模型该不该重试。</summary>
public enum ToolApprovalJudgeFailureKind
{
    /// <summary>不是评判失败。</summary>
    None,
    /// <summary>
    /// 模型回了话，但不是可用的裁决：撞上输出上限被截断、正文为空、不是单个 JSON 对象、裁决值无法识别。
    /// 推理长度每次都不一样（实测同一请求时而 120 token、时而 1 024 token 仍未想完），同一请求再发一次多半就成了，
    /// 所以允许主模型重试一次。
    /// </summary>
    Output,
    /// <summary>
    /// 请求没能完成：超时、网络错误、限流、服务端错误。SDK 管线已经按自己的策略重试过，每次最多等满一个
    /// 超时（实测一次超时失败前后等了约 4 分钟），主模型立刻再发只会再等一轮。
    /// </summary>
    Transport,
    /// <summary>审批模型本身不可用：未配置、鉴权或额度问题、端点拒绝请求参数。重试不会改变结果，要用户改设置。</summary>
    Configuration
}

/// <summary>
/// 审批裁决结果。
/// </summary>
public sealed class ToolApprovalDecision
{
    /// <summary>
    /// 自动审批没能给出裁决时，<see cref="Reason"/> 的固定开头。日志取证按这个前缀把它和模型的裁决分开，
    /// 改掉它等于让新旧记录对不上；具体原因跟在后面的括号里。
    /// </summary>
    public const string JudgeFailurePrefix = "自动审批失败，已按安全策略拒绝";

    public required ToolApprovalScope Scope { get; init; }

    /// <summary>是否放行执行。</summary>
    public bool Approved => Scope != ToolApprovalScope.Deny;

    /// <summary>裁决来源说明，用于审计日志（如「配置 Off」「只读自动放行」「用户弹窗」「无人值守拒绝」）。</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>裁决出自谁。默认 <see cref="ToolApprovalSource.Policy"/>：没有显式标注的都是静态规则。</summary>
    public ToolApprovalSource Source { get; init; } = ToolApprovalSource.Policy;

    /// <summary>仅当 <see cref="Source"/> 为 <see cref="ToolApprovalSource.JudgeFailure"/> 时不为 None。</summary>
    public ToolApprovalJudgeFailureKind FailureKind { get; init; }

    public static ToolApprovalDecision Allow(ToolApprovalScope scope, string reason) =>
        new() { Scope = scope, Reason = reason };

    public static ToolApprovalDecision AllowOnce(string reason) =>
        new() { Scope = ToolApprovalScope.AllowOnce, Reason = reason };

    public static ToolApprovalDecision Deny(string reason) =>
        new() { Scope = ToolApprovalScope.Deny, Reason = reason };

    public static ToolApprovalDecision Cancelled(string reason) =>
        new() { Scope = ToolApprovalScope.Deny, Source = ToolApprovalSource.Cancelled, Reason = reason };

    /// <summary>自动审批模型的放行。理由前缀是日志取证的分类键，不要改。</summary>
    public static ToolApprovalDecision JudgeAllowed(string reason) =>
        new() { Scope = ToolApprovalScope.AllowOnce, Source = ToolApprovalSource.JudgeVerdict, Reason = "自动审批模型放行：" + reason };

    /// <summary>自动审批模型的拒绝。理由前缀是日志取证的分类键，不要改。</summary>
    public static ToolApprovalDecision JudgeDenied(string reason) =>
        new() { Scope = ToolApprovalScope.Deny, Source = ToolApprovalSource.JudgeVerdict, Reason = "自动审批模型拒绝：" + reason };

    /// <summary>自动审批没能给出裁决：按安全策略拒绝，并记下是哪一类故障。</summary>
    public static ToolApprovalDecision JudgeFailed(ToolApprovalJudgeFailureKind kind, string detail)
    {
        if (kind == ToolApprovalJudgeFailureKind.None)
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "A judge failure must name its kind.");
        return new()
        {
            Scope = ToolApprovalScope.Deny,
            Source = ToolApprovalSource.JudgeFailure,
            FailureKind = kind,
            Reason = $"{JudgeFailurePrefix}（{detail}）"
        };
    }
}
