using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.Cron;

/// <summary>
/// cron 的执行泵。由应用生命周期显式启停，且必须等到主窗口、工作区与会话树就绪之后再启动——
/// 提前启动只会让第一批到期任务撞上一个还没有会话树的宿主。
///
/// 三条硬性行为：
/// - 检查对齐整分钟。默认的"Start 时刻起每 60 秒"会稳定漂在某个秒位上，让 "0 9 * * *" 实际在 9:00:37 触发。
/// - 并发上限固定 2，且严格 FIFO：一个单独的泵按入队顺序取出，拿到令牌才启动。
/// - 状态全程落盘（Queued → Running → 终态）。宿主不可用、抛异常、被取消，都不允许写成 Succeeded。
/// </summary>
public sealed class CronExecutionWorker : IDisposable
{
    /// <summary>同时运行的 cron 会话上限。会话本身还要再服从全局会话并发协调器。</summary>
    public const int MaxConcurrentRuns = 2;

    private readonly ICronTaskService _taskService;
    private readonly ICronSessionLauncher _launcher;
    private readonly ISystemClock _clock;
    private readonly ILogger _logger;

    private readonly SemaphoreSlim _slots = new(MaxConcurrentRuns, MaxConcurrentRuns);
    private readonly SemaphoreSlim _queueSignal = new(0);
    private readonly SemaphoreSlim _checkGate = new(1, 1);
    private readonly ConcurrentQueue<CronTaskClaim> _queue = new();

    private CancellationTokenSource? _lifetime;
    private Timer? _timer;
    private Task? _pump;
    private int _activeRuns;
    private bool _disposed;

    public CronExecutionWorker(
        ICronTaskService taskService,
        ICronSessionLauncher launcher,
        ISystemClock clock,
        ILogger logger)
    {
        _taskService = taskService;
        _launcher = launcher;
        _clock = clock;
        _logger = logger.ForContext<CronExecutionWorker>();
    }

    public bool IsRunning => _lifetime is { IsCancellationRequested: false };

    public int ActiveRunCount => Volatile.Read(ref _activeRuns);

    public int PendingRunCount => _queue.Count;

    /// <summary>没有排队也没有在跑。测试用它判断一批触发是否已经全部落定。</summary>
    public bool IsIdle => _queue.IsEmpty && ActiveRunCount == 0;

    /// <summary>
    /// 启动执行泵。先把上次退出时遗留的 Queued/Running 收敛成 Interrupted，再对齐到下一个整分钟开始检查。
    /// </summary>
    public async Task StartAsync()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(CronExecutionWorker));
        if (IsRunning) return;

        var reconciled = await _taskService.ReconcileOrphanedRunsAsync(_clock.UtcNow);
        if (reconciled > 0)
        {
            _logger.Information("Converged {Count} in-flight cron run(s) from the previous session to interrupted", reconciled);
        }

        _lifetime = new CancellationTokenSource();
        var token = _lifetime.Token;
        _pump = Task.Run(() => PumpAsync(token), CancellationToken.None);

        var firstDelay = TimeUntilNextWholeMinute(_clock.UtcNow);
        _timer = new Timer(_ => _ = SafeRunDueChecksAsync(), null, firstDelay, TimeSpan.FromMinutes(1));
        _logger.Information(
            "Cron execution worker started; first aligned check in {Delay:0.###}s, concurrency limit {Limit}",
            firstDelay.TotalSeconds, MaxConcurrentRuns);
    }

    /// <summary>
    /// 停止执行泵。在途的运行会被取消，其终态写为 Interrupted；队列中尚未启动的运行同样标记为 Interrupted，
    /// 绝不留下永远停在 Queued 的幽灵记录。
    /// </summary>
    public async Task StopAsync()
    {
        if (!IsRunning) return;

        var lifetime = _lifetime;
        _lifetime = null;
        _timer?.Dispose();
        _timer = null;

        lifetime?.Cancel();
        _queueSignal.Release();

        var pump = _pump;
        _pump = null;
        if (pump != null)
        {
            try { await pump; }
            catch (OperationCanceledException) { /* 预期内 */ }
        }

        var now = _clock.UtcNow;
        while (_queue.TryDequeue(out var pending))
        {
            await _taskService.CompleteRunAsync(
                pending.Task.Id,
                pending.Run.RunId,
                CronRunState.Interrupted,
                now,
                note: "The app stopped before this queued run started.");
        }

        lifetime?.Dispose();
        _logger.Information("Cron execution worker stopped");
    }

    /// <summary>
    /// 领取一次到期检查。公开是为了让测试用可注入时钟精确驱动，而不必等真实的一分钟。
    /// </summary>
    public async Task<int> RunDueChecksAsync()
    {
        await _checkGate.WaitAsync();
        try
        {
            var now = _clock.UtcNow;
            var claims = await _taskService.ClaimDueAsync(now);
            foreach (var claim in claims)
            {
                _queue.Enqueue(claim);
                _queueSignal.Release();
            }

            return claims.Count;
        }
        finally
        {
            _checkGate.Release();
        }
    }

    /// <summary>
    /// 立即运行一次（`run_task_now` 与任务卡片的"立即运行"）。刻意不改变 cron 的下一次计划，
    /// 也不会因 RunOnce 而停用任务。
    /// </summary>
    public async Task<CronTaskClaim?> RunNowAsync(string taskId)
    {
        // 泵没在跑就不要领取：入队的运行会永远停在 Queued，而下一次启动的 reconcile
        // 又会把它判成 Interrupted，等于凭空造出一条从未发生过的失败记录。
        if (!IsRunning)
        {
            _logger.Warning("Refused a manual run of cron task {TaskId}: the execution worker is not running", taskId);
            return null;
        }

        var claim = await _taskService.ClaimManualAsync(taskId, _clock.UtcNow);
        if (claim == null) return null;

        _queue.Enqueue(claim);
        _queueSignal.Release();
        return claim;
    }

    private async Task SafeRunDueChecksAsync()
    {
        try
        {
            await RunDueChecksAsync();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Cron due-task check failed");
        }
    }

    private async Task PumpAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await _queueSignal.WaitAsync(token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (token.IsCancellationRequested) return;
            if (!_queue.TryDequeue(out var claim)) continue;

            try
            {
                await _slots.WaitAsync(token);
            }
            catch (OperationCanceledException)
            {
                // 泵被停掉了：把这条重新入队，交给 StopAsync 统一收敛为 Interrupted。
                _queue.Enqueue(claim);
                return;
            }

            Interlocked.Increment(ref _activeRuns);
            _ = ExecuteClaimAsync(claim, token).ContinueWith(
                _ =>
                {
                    Interlocked.Decrement(ref _activeRuns);
                    _slots.Release();
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                System.Threading.Tasks.TaskScheduler.Default);
        }
    }

    private async Task ExecuteClaimAsync(CronTaskClaim claim, CancellationToken token)
    {
        var task = claim.Task;
        var run = claim.Run;

        try
        {
            await _taskService.MarkRunStartedAsync(task.Id, run.RunId, _clock.UtcNow);

            var result = await _launcher.LaunchAsync(task, run, token);

            await _taskService.CompleteRunAsync(
                task.Id,
                run.RunId,
                result.State,
                _clock.UtcNow,
                result.ConversationId,
                result.HistoryId,
                result.WorkspaceFellBack
                    ? $"Workspace '{task.WorkspaceId}' was not found; the session was created in the global group."
                    : result.Note,
                result.Error);
        }
        catch (Exception ex)
        {
            // 走到这里说明连状态机本身都失败了；必须写成 Failed，绝不能留在 Running。
            _logger.Error(ex, "Cron run {RunId} on task {TaskId} failed outside the launcher", run.RunId, task.Id);
            try
            {
                await _taskService.CompleteRunAsync(
                    task.Id, run.RunId, CronRunState.Failed, _clock.UtcNow, error: ex.Message);
            }
            catch (Exception writeBackFailure)
            {
                _logger.Error(writeBackFailure, "Failed to write back the terminal state of cron run {RunId}", run.RunId);
            }
        }
    }

    /// <summary>距离下一个整分钟的时长。已经正好落在整分钟上时等一整分钟，避免同一分钟连查两次。</summary>
    public static TimeSpan TimeUntilNextWholeMinute(DateTimeOffset now)
    {
        var remainder = now.UtcTicks % TimeSpan.TicksPerMinute;
        return TimeSpan.FromTicks(remainder == 0 ? TimeSpan.TicksPerMinute : TimeSpan.TicksPerMinute - remainder);
    }

    /// <summary>
    /// 同步释放：只取消并回收资源，**绝不** sync-over-async 地等 <see cref="StopAsync"/>。
    /// 容器释放通常发生在 UI 线程上，而 StopAsync 内部的 await 续延要投进同一个 dispatcher 队列——
    /// 阻塞等待它就是一个死锁。优雅停机走 <see cref="StopAsync"/>（应用退出时显式 await），
    /// 这里遗留下来的 Queued/Running 记录由下次启动的 ReconcileOrphanedRunsAsync 收敛为 Interrupted。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        var lifetime = _lifetime;
        _lifetime = null;
        try
        {
            lifetime?.Cancel();
            lifetime?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Cron execution worker did not cancel cleanly");
        }

        _timer?.Dispose();
        _timer = null;
        _slots.Dispose();
        _queueSignal.Dispose();
        _checkGate.Dispose();
    }
}
