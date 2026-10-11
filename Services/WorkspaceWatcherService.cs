using Athena.UI.Services.Interfaces;
using Serilog;
using System;
using System.IO;
using System.Threading.Tasks;

namespace Athena.UI.Services;

/// <summary>
/// 当前工作区唯一的递归文件监视器，工作台与游戏模式共用（设计稿 11.2）。从工作台里提出来时原样保留了
/// PR #28 的三条规则：先订阅、后启用；错误合并成一条 Warning；建不起来只降级、不抛出。
/// </summary>
public sealed class WorkspaceWatcherService : IWorkspaceWatcherService
{
    /// <summary>
    /// 第一条错误之后再等这么久才上报：Linux 撞上 inotify watch 上限时每个加不上的目录各报一次 Error，
    /// 大仓库能有上千条，逐条写 Warning 会淹掉日志，逐条整树重扫会把机器拖死。窗口内的错误只计数。
    /// </summary>
    internal static readonly TimeSpan ErrorCoalescingWindow = TimeSpan.FromMilliseconds(150);

    private readonly ILogger _logger = Log.ForContext<WorkspaceWatcherService>();
    private readonly object _gate = new();
    private FileSystemWatcher? _watcher;
    private string? _workspaceId;
    private string? _root;
    private WorkspaceWatchState _state = WorkspaceWatchState.Idle;
    // 每换一次监视器加一：旧监视器停下之前的最后几声（错误、改动）一律作废。
    private int _generation;
    private int _burstCount;
    private Exception? _burstFirst;
    private bool _disposed;

    /// <summary>
    /// 文件监视器的构造缝，生产路径就是 <c>new FileSystemWatcher(path)</c>。无头测试借它换上能手动引发
    /// <see cref="FileSystemWatcher.Error"/> 的监视器，或一个直接抛出的工厂，运行期出错和启动失败两条路径因此都有断言。
    /// </summary>
    internal Func<string, FileSystemWatcher> WatcherFactory { get; init; } = static path => new FileSystemWatcher(path);

    public string? WorkspaceId
    {
        get { lock (_gate) return _workspaceId; }
    }

    public string? Root
    {
        get { lock (_gate) return _root; }
    }

    public WorkspaceWatchState State
    {
        get { lock (_gate) return _state; }
    }

    public event EventHandler<WorkspaceFileChange>? Changed;

    public event EventHandler<WorkspaceWatcherErrorEventArgs>? ErrorsDropped;

    public event EventHandler? StateChanged;

    public WorkspaceWatchState Watch(string? workspaceId, string? root)
    {
        var stop = string.IsNullOrWhiteSpace(workspaceId) || string.IsNullOrWhiteSpace(root);
        FileSystemWatcher? previous;
        lock (_gate)
        {
            if (_disposed) return WorkspaceWatchState.Idle;
            previous = DetachWatcherLocked();
            _workspaceId = stop ? null : workspaceId;
            _root = stop ? null : root;
            _state = WorkspaceWatchState.Idle;
        }
        DisposeWatcher(previous);

        if (stop)
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
            return WorkspaceWatchState.Idle;
        }

        var path = root!;
        FileSystemWatcher? watcher = null;
        try
        {
            watcher = WatcherFactory(path);
            lock (_gate) _watcher = watcher;
            watcher.IncludeSubdirectories = true;
            watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size;
            watcher.Changed += OnFileSystemEvent;
            watcher.Created += OnFileSystemEvent;
            watcher.Deleted += OnFileSystemEvent;
            watcher.Renamed += OnFileSystemEvent;
            watcher.Error += OnWatcherError;
            // 先订阅、后启用：macOS（FSEventStreamStart 失败）和 Linux（初次递归添加 inotify watch 撞上
            // max_user_watches，每个加不上的目录各报一次）都在这个 setter 内部同步引发 Error，
            // 晚一步订阅就一条也收不到（.NET 10.0.1 反编译核实，见 PR #28）。
            watcher.EnableRaisingEvents = true;
            lock (_gate)
            {
                if (ReferenceEquals(_watcher, watcher)) _state = WorkspaceWatchState.Watching;
            }
        }
        catch (Exception ex)
        {
            // 构造时目录已不存在（加载期间被删）、inotify 实例数上限（max_user_instances）、
            // FSEvents / CreateFile 失败，都落在这里。
            lock (_gate)
            {
                if (ReferenceEquals(_watcher, watcher)) DetachWatcherLocked();
                _state = WorkspaceWatchState.Unavailable;
            }
            DisposeWatcher(watcher);
            _logger.Warning(ex, "Workspace file watcher could not start; the workbench and the polis will not follow external changes: {Workspace}", path);
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
        return State;
    }

    private void OnFileSystemEvent(object sender, FileSystemEventArgs e)
    {
        string workspaceId;
        string root;
        lock (_gate)
        {
            if (!ReferenceEquals(sender, _watcher) || _workspaceId == null || _root == null) return;
            workspaceId = _workspaceId;
            root = _root;
        }
        Changed?.Invoke(this, new WorkspaceFileChange(
            workspaceId,
            root,
            e.ChangeType,
            e.FullPath,
            (e as RenamedEventArgs)?.OldFullPath,
            DateTime.UtcNow));
    }

    /// <summary>
    /// 监视器丢了事件（Windows 缓冲区溢出、Linux inotify 队列溢出、macOS FSEvents 要求重扫）或出了别的错。
    /// 运行期在监视器自己的线程上引发，启动阶段在调用 <see cref="Watch"/> 的线程上同步引发；一律不在这里干活。
    /// </summary>
    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        var exception = e.GetException();
        int generation;
        lock (_gate)
        {
            // 工作区已经换过：旧监视器停下之前的最后一声，它的工作区已经不在屏幕上了。
            if (!ReferenceEquals(sender, _watcher)) return;
            if (_burstCount++ > 0) return;
            _burstFirst = exception;
            generation = _generation;
        }
        _ = ReportBurstAsync(generation);
    }

    private async Task ReportBurstAsync(int generation)
    {
        await Task.Delay(ErrorCoalescingWindow).ConfigureAwait(false);
        int errorCount;
        Exception? firstError;
        string? workspaceId;
        string? root;
        lock (_gate)
        {
            // 攒下这些错误的监视器在上报之前就被换掉了（Watch / Dispose 已把计数清零）。
            if (generation != _generation) return;
            errorCount = _burstCount;
            firstError = _burstFirst;
            workspaceId = _workspaceId;
            root = _root;
            _burstCount = 0;
            _burstFirst = null;
        }
        if (errorCount == 0 || firstError == null || workspaceId == null || root == null) return;

        _logger.Warning(
            firstError,
            "Workspace file watcher reported {ErrorCount} error(s); events may have been dropped, refreshing the whole workspace: {Workspace}",
            errorCount,
            root);
        try
        {
            ErrorsDropped?.Invoke(this, new WorkspaceWatcherErrorEventArgs(workspaceId, root, errorCount, firstError));
        }
        catch (Exception ex)
        {
            // 这里跑在线程池上，订阅者抛出的异常没有人接，只会变成未观察的任务异常。
            _logger.Error(ex, "A workspace watcher error subscriber failed: {Workspace}", root);
        }
    }

    /// <summary>在锁内摘下当前监视器并作废它攒下的错误；真正的 Dispose 在锁外做（它会等监视器线程）。</summary>
    private FileSystemWatcher? DetachWatcherLocked()
    {
        _generation++;
        _burstCount = 0;
        _burstFirst = null;
        var watcher = _watcher;
        _watcher = null;
        return watcher;
    }

    private void DisposeWatcher(FileSystemWatcher? watcher)
    {
        if (watcher == null) return;
        watcher.Changed -= OnFileSystemEvent;
        watcher.Created -= OnFileSystemEvent;
        watcher.Deleted -= OnFileSystemEvent;
        watcher.Renamed -= OnFileSystemEvent;
        watcher.Error -= OnWatcherError;
        try
        {
            watcher.EnableRaisingEvents = false;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // 启动失败的监视器停用时还可能再抛一次同样的错；它已经被摘下，没有什么可做的。
            _logger.Debug(ex, "Stopping a workspace file watcher failed");
        }
        watcher.Dispose();
    }

    public void Dispose()
    {
        FileSystemWatcher? watcher;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            watcher = DetachWatcherLocked();
            _workspaceId = null;
            _root = null;
            _state = WorkspaceWatchState.Idle;
        }
        DisposeWatcher(watcher);
    }
}
