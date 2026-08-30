using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using Serilog;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.Cron;

/// <summary>
/// 把一次已领取的 cron 运行变成一个真正跑起来的新会话。
///
/// 这里刻意不知道 MainWindowViewModel 的存在：会话树那一半由运行期挂载的
/// <see cref="ICronSessionHost"/> 提供，从而让依赖方向保持
/// worker → launcher → host，不成环。
///
/// 失败语义是硬要求：宿主未就绪、创建会话抛异常、执行返回非成功——
/// 任何一种都不允许被记成 Succeeded。
/// </summary>
public sealed class CronSessionLauncher : ICronSessionLauncher
{
    private readonly ILogger _logger;
    private ICronSessionHost? _host;

    public CronSessionLauncher(ILogger logger)
    {
        _logger = logger.ForContext<CronSessionLauncher>();
    }

    public bool IsReady => _host != null;

    public void AttachHost(ICronSessionHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _logger.Information("Cron session host attached");
    }

    public void DetachHost(ICronSessionHost host)
    {
        if (ReferenceEquals(_host, host))
        {
            _host = null;
            _logger.Information("Cron session host detached");
        }
    }

    public async Task<CronSessionLaunchResult> LaunchAsync(CronTask task, CronTaskRunRecord run, CancellationToken cancellationToken)
    {
        var host = _host;
        if (host == null)
        {
            _logger.Warning("Cron run {RunId} for task {TaskId} could not start: no session host attached", run.RunId, task.Id);
            return CronSessionLaunchResult.NotReady();
        }

        CronSessionAttachment attachment;
        try
        {
            attachment = await host.AttachScheduledSessionAsync(task, run);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to create a session for cron run {RunId} on task {TaskId}", run.RunId, task.Id);
            return new CronSessionLaunchResult { Succeeded = false, State = CronRunState.Failed, Error = ex.Message };
        }

        if (attachment.WorkspaceFellBack)
        {
            _logger.Warning(
                "Cron task {TaskId} targets workspace {WorkspaceId}, which no longer exists; the new session was placed in the global group",
                task.Id, task.WorkspaceId);
        }

        try
        {
            var result = await attachment.RunInstructionAsync(task.Instruction, cancellationToken);
            var succeeded = result.Outcome == TaskExecutionOutcome.Succeeded;
            var state = result.Outcome switch
            {
                TaskExecutionOutcome.Succeeded => CronRunState.Succeeded,
                TaskExecutionOutcome.Interrupted => CronRunState.Interrupted,
                _ => CronRunState.Failed
            };

            attachment.ReportOutcome(state, task.NotifyOnCompletion);

            return new CronSessionLaunchResult
            {
                Succeeded = succeeded,
                State = state,
                ConversationId = attachment.ConversationId,
                HistoryId = attachment.HistoryId,
                WorkspaceFellBack = attachment.WorkspaceFellBack,
                Note = result.Note,
                Error = succeeded ? null : result.Note ?? state.ToString()
            };
        }
        catch (OperationCanceledException)
        {
            attachment.ReportOutcome(CronRunState.Interrupted, task.NotifyOnCompletion);
            return new CronSessionLaunchResult
            {
                Succeeded = false,
                State = CronRunState.Interrupted,
                ConversationId = attachment.ConversationId,
                HistoryId = attachment.HistoryId,
                WorkspaceFellBack = attachment.WorkspaceFellBack,
                Error = "The cron run was cancelled."
            };
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Cron run {RunId} on task {TaskId} threw while executing", run.RunId, task.Id);
            attachment.ReportOutcome(CronRunState.Failed, task.NotifyOnCompletion);
            return new CronSessionLaunchResult
            {
                Succeeded = false,
                State = CronRunState.Failed,
                ConversationId = attachment.ConversationId,
                HistoryId = attachment.HistoryId,
                WorkspaceFellBack = attachment.WorkspaceFellBack,
                Error = ex.Message
            };
        }
    }
}
