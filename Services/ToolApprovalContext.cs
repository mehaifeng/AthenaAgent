using System;
using System.Collections.Generic;
using System.Threading;

namespace Athena.UI.Services;

/// <summary>
/// 当前工具执行流所处的审批环境（AsyncLocal）。决定 chokepoint 在需要人工确认时，
/// 走「弹窗询问用户」还是「无人值守的静态策略」。
///
/// - Interactive：主对话循环。有 UI，可弹审批窗。
/// - NonInteractive：后台无 UI 路径（并发子代理、知识库定期整理）。破坏性/敏感按静态策略处理，绝不弹窗。
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
        /// 第一方、沙箱化、用户已显式开启的后台例程（知识库定期整理）。
        /// 其工具集受限且仅作用于 AthenaData 沙箱，审批闸门自动放行以免破坏例程功能。
        /// </summary>
        Trusted
    }

    private static readonly AsyncLocal<ExecutionMode> _mode = new();
    private static readonly AsyncLocal<string?> _delegatedTask = new();

    /// <summary>当前执行流的审批模式；未设置时为 <see cref="ExecutionMode.Unset"/>。</summary>
    public static ExecutionMode CurrentMode => _mode.Value;

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

    /// <summary>进入受信任的第一方例程作用域（知识库定期整理），审批自动放行。Dispose 时恢复先前值。</summary>
    public static IDisposable EnterTrusted() => Enter(ExecutionMode.Trusted);

    private static IDisposable Enter(ExecutionMode mode, string? delegatedTask = null)
    {
        var previous = _mode.Value;
        var previousTask = _delegatedTask.Value;
        _mode.Value = mode;
        _delegatedTask.Value = delegatedTask;
        return new Scope(previous, previousTask);
    }

    private sealed class Scope : IDisposable
    {
        private readonly ExecutionMode _previous;
        private readonly string? _previousTask;
        private bool _disposed;

        public Scope(ExecutionMode previous, string? previousTask)
        {
            _previous = previous;
            _previousTask = previousTask;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _mode.Value = _previous;
            _delegatedTask.Value = _previousTask;
        }
    }
}
