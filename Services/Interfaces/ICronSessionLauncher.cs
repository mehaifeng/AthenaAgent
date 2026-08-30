using Athena.UI.Models;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.Interfaces;

/// <summary>一次 cron 会话启动的结果。</summary>
public sealed class CronSessionLaunchResult
{
    public bool Succeeded { get; init; }

    /// <summary>该次运行应当被写入的终态。绝不会在没有实际执行时给出 Succeeded。</summary>
    public CronRunState State { get; init; } = CronRunState.Failed;

    public string? ConversationId { get; init; }

    public string? HistoryId { get; init; }

    public string? Note { get; init; }

    public string? Error { get; init; }

    /// <summary>任务绑定的工作区不存在，会话落到了全局分组。</summary>
    public bool WorkspaceFellBack { get; init; }

    /// <summary>会话宿主（主窗口会话树）尚未就绪，本次没有真正执行过任何东西。</summary>
    public bool HostUnavailable { get; init; }

    public static CronSessionLaunchResult NotReady()
        => new()
        {
            HostUnavailable = true,
            State = CronRunState.Failed,
            Error = "The conversation tree is not ready yet, so nothing was executed."
        };
}

/// <summary>
/// 会话树一侧的接入点，由 <c>MainWindowViewModel</c> 实现。
/// 之所以拆出来，是为了断开 DI 环：worker → launcher → host（运行期注入），
/// 调度侧任何一层都不需要知道主窗口的存在。
/// </summary>
public interface ICronSessionHost
{
    /// <summary>
    /// 在 UI 线程创建一个全新会话，绑定到任务指定的工作区（不存在时落入全局分组），
    /// 插入会话树，并且**绝不改变当前选中项**。
    /// </summary>
    Task<CronSessionAttachment> AttachScheduledSessionAsync(CronTask task, CronTaskRunRecord run);
}

/// <summary>
/// 宿主创建好的会话句柄。launcher 拿着它注入指令、执行并回写状态。
/// </summary>
public sealed class CronSessionAttachment
{
    public required string ConversationId { get; init; }

    public required string HistoryId { get; init; }

    public bool WorkspaceFellBack { get; init; }

    /// <summary>在这个会话里跑一次计划指令。</summary>
    public required System.Func<string, CancellationToken, Task<TaskExecutionResult>> RunInstructionAsync { get; init; }

    /// <summary>把运行结果反映到会话树条目（未读点 / 失败态 / 标题生成）。</summary>
    public required System.Action<CronRunState, bool> ReportOutcome { get; init; }
}

/// <summary>
/// cron 触发与会话之间的唯一桥梁。执行器只认这个接口，不认 ViewModel。
/// </summary>
public interface ICronSessionLauncher
{
    /// <summary>宿主是否已挂载（主窗口、工作区与会话树就绪后才为 true）。</summary>
    bool IsReady { get; }

    void AttachHost(ICronSessionHost host);

    void DetachHost(ICronSessionHost host);

    Task<CronSessionLaunchResult> LaunchAsync(CronTask task, CronTaskRunRecord run, CancellationToken cancellationToken);
}
