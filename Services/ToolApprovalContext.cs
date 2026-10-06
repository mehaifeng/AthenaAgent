using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Athena.UI.Services;

/// <summary>
/// 当前工具执行流所处的审批环境（AsyncLocal）。决定 chokepoint 在需要人工确认时，
/// 走「弹窗询问用户」还是「无人值守的静态策略」。
///
/// - Interactive：主对话循环。有 UI，可弹审批窗。
/// - NonInteractive：并发子代理。无 UI，破坏性/敏感按静态策略处理，绝不弹窗。
/// - Trusted：知识库定期整理。只对 <see cref="TrustedRoutineGrant"/> 里的工具自动放行，
///   文件操作限定在授权目录内，绝不弹窗。
///
/// 通过 AsyncLocal 在 await 链中向下游传播，无需给 FunctionRegistry.ExecuteAsync 增加参数，
/// 因此三条调用路径（主对话 / 子代理 / 维护）统一走同一个 chokepoint 而互不串扰。
/// 仿 <see cref="Athena.UI.Services.SubAgents.ToolExecutionContext"/> 的作用域范式。
/// </summary>
public static class ToolApprovalContext
{
    public enum ExecutionMode
    {
        /// <summary>未显式设置。chokepoint 视为无人值守（fail-safe），敏感/破坏性默认拒绝。</summary>
        Unset,
        /// <summary>主对话循环。有 UI，可弹审批窗。</summary>
        Interactive,
        /// <summary>并发子代理。无 UI；敏感工具按 SubAgentsInheritApproval 处理，破坏性一律拒绝。</summary>
        NonInteractive,
        /// <summary>
        /// 第一方、用户已显式开启的后台例程（知识库定期整理）。审批闸门只对授权
        /// （<see cref="TrustedRoutineGrant"/>）里的工具自动放行，文件路径由 FileSystemService
        /// 限定在授权目录内。
        /// </summary>
        Trusted
    }

    private static readonly AsyncLocal<ExecutionMode> _mode = new();
    private static readonly AsyncLocal<string?> _delegatedTask = new();
    private static readonly AsyncLocal<TrustedRoutineGrant?> _trustedGrant = new();

    /// <summary>当前执行流的审批模式；未设置时为 <see cref="ExecutionMode.Unset"/>。</summary>
    public static ExecutionMode CurrentMode => _mode.Value;

    /// <summary>Trusted 作用域的授权；其它模式下为 null。</summary>
    public static TrustedRoutineGrant? CurrentTrustedGrant => _trustedGrant.Value;

    /// <summary>
    /// 这次工具调用服务的任务。主对话里是用户最近的请求（见 <see cref="DescribeTask"/>），
    /// 自动审批模型据此判断调用是否与任务相称；其它路径为 null。
    /// </summary>
    public static string? CurrentDelegatedTask => _delegatedTask.Value;

    // 用户常整段粘贴日志或代码，审批只需要知道「要做什么」，所以两条各自截断。
    private const int LatestTaskMaxChars = 1000;
    private const int PreviousTaskMaxChars = 300;

    /// <summary>
    /// 主对话的委托任务：最近两条非空用户消息，各自截断；没有用户消息时为 null。
    /// 只取最新一条不够——「继续」「好的」这类跟进会把任务本身丢掉。
    /// 自 2026-07-19 自动审批上线起，主对话从没把任务传进来过，审批模型的提示词要求它
    /// 「对照委托任务判断」，拿到的却始终是 null。
    /// </summary>
    public static string? DescribeTask(IEnumerable<string?> userMessagesOldestFirst)
    {
        string? latest = null;
        string? previous = null;
        foreach (var message in userMessagesOldestFirst)
        {
            if (string.IsNullOrWhiteSpace(message)) continue;
            previous = latest;
            latest = message.Trim();
        }

        if (latest is null) return null;
        var task = "Latest user message: " + Truncate(latest, LatestTaskMaxChars);
        return previous is null
            ? task
            : task + "\nPrevious user message: " + Truncate(previous, PreviousTaskMaxChars);
    }

    private static string Truncate(string text, int maxChars)
    {
        if (text.Length <= maxChars) return text;
        // 不把代理对切成两半：半个 emoji 进了 JSON 载荷就是一个非法字符。
        var cut = char.IsHighSurrogate(text[maxChars - 1]) ? maxChars - 1 : maxChars;
        return text[..cut] + "…";
    }

    /// <summary>进入交互式作用域（主对话循环）。Dispose 时恢复先前值。</summary>
    public static IDisposable EnterInteractive(string? delegatedTask = null) => Enter(ExecutionMode.Interactive, delegatedTask);

    /// <summary>进入无人值守作用域（并发子代理）。Dispose 时恢复先前值。</summary>
    public static IDisposable EnterNonInteractive() => Enter(ExecutionMode.NonInteractive);

    /// <summary>
    /// 进入受信任的第一方例程作用域（知识库定期整理）。必须带授权：信任只覆盖授权里的工具和目录，
    /// 没有「只给信任、不给限定」的入口。Dispose 时恢复先前值。
    /// </summary>
    public static IDisposable EnterTrusted(TrustedRoutineGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        return Enter(ExecutionMode.Trusted, delegatedTask: null, grant);
    }

    private static IDisposable Enter(ExecutionMode mode, string? delegatedTask = null, TrustedRoutineGrant? grant = null)
    {
        var previous = _mode.Value;
        var previousTask = _delegatedTask.Value;
        var previousGrant = _trustedGrant.Value;
        _mode.Value = mode;
        _delegatedTask.Value = delegatedTask;
        _trustedGrant.Value = grant;
        return new Scope(previous, previousTask, previousGrant);
    }

    private sealed class Scope : IDisposable
    {
        private readonly ExecutionMode _previous;
        private readonly string? _previousTask;
        private readonly TrustedRoutineGrant? _previousGrant;
        private bool _disposed;

        public Scope(ExecutionMode previous, string? previousTask, TrustedRoutineGrant? previousGrant)
        {
            _previous = previous;
            _previousTask = previousTask;
            _previousGrant = previousGrant;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _mode.Value = _previous;
            _delegatedTask.Value = _previousTask;
            _trustedGrant.Value = _previousGrant;
        }
    }
}

/// <summary>
/// 受信任后台例程的授权：能调用哪些工具、文件操作限定在哪个目录。审批闸门对 Trusted 自动放行，
/// 所以这份授权就是该例程全部的能力边界。此前 Trusted 既不看工具名也不看路径：
/// FunctionRegistry.ExecuteAsync 只按名字找执行器，模型输出一个没声明过的工具名
/// （execute_terminal_command、modify_self_configuration）照样执行；文件工具则能删改知识库之外的
/// 任意文件。整理 Agent 读的记忆文件可能来自网页，其中被注入的指令会借它的手完成这些事。
/// </summary>
public sealed class TrustedRoutineGrant
{
    private readonly HashSet<string> _tools;

    public TrustedRoutineGrant(string routine, IEnumerable<string> tools, string confinementRoot)
    {
        if (string.IsNullOrWhiteSpace(routine))
            throw new ArgumentException("A trusted routine must be named.", nameof(routine));
        ArgumentNullException.ThrowIfNull(tools);
        // 相对路径会按进程当前目录展开，限定到哪里就成了偶然。
        if (string.IsNullOrWhiteSpace(confinementRoot) || !Path.IsPathFullyQualified(confinementRoot))
            throw new ArgumentException("A trusted routine must be confined to an absolute directory.", nameof(confinementRoot));

        Routine = routine;
        ToolNames = tools.Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.Ordinal).ToArray();
        if (ToolNames.Count == 0)
            throw new ArgumentException("A trusted routine must be granted at least one tool.", nameof(tools));
        // 与 FunctionRegistry 的执行器字典同一种比较：授权的就是这几个确切的名字。
        _tools = new HashSet<string>(ToolNames, StringComparer.Ordinal);
        ConfinementRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(confinementRoot));
    }

    /// <summary>例程名，写进审计日志与拒绝信息。</summary>
    public string Routine { get; }

    /// <summary>授权的工具，保持声明顺序（拒绝信息原样列出）。</summary>
    public IReadOnlyList<string> ToolNames { get; }

    /// <summary>文件操作的限定目录（规范化的绝对路径，不带末尾分隔符）。</summary>
    public string ConfinementRoot { get; }

    public bool Allows(string functionName) => _tools.Contains(functionName);
}
