using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using Serilog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services;

/// <summary>
/// 任务调度器实现
/// 管理计划任务，支持持久化和定时触发
/// </summary>
public class TaskScheduler : ITaskScheduler, IDisposable
{
    private readonly string _tasksFilePath;
    private readonly Timer _timer;
    private readonly ILogger _logger;
    private readonly IPlatformPathService? _pathService;
    private readonly IRecurrenceService _recurrenceService;
    private readonly SemaphoreSlim _checkLock = new(1, 1);
    private readonly HashSet<string> _waitingForegroundTaskIds = new(StringComparer.Ordinal);
    private bool _isRunning;
    private bool _disposed;
    private string? _activeForegroundTaskId;

    /// <summary>
    /// 共享的任务集合
    /// </summary>
    public ObservableCollection<ScheduledTask> Tasks { get; }

    /// <summary>
    /// 前台任务触发事件（主动消息）
    /// </summary>
    public event EventHandler<ProactiveMessageEventArgs>? ProactiveMessageTriggered;

    /// <summary>
    /// 后台任务触发事件（静默执行）
    /// </summary>
    public event EventHandler<BackgroundTaskEventArgs>? BackgroundTaskTriggered;

    public TaskScheduler(ILogger logger, IPlatformPathService? pathService = null, IRecurrenceService? recurrenceService = null)
    {
        _logger = logger;
        _pathService = pathService;
        _recurrenceService = recurrenceService ?? new RecurrenceService();

        if (_pathService != null)
        {
            _tasksFilePath = _pathService.GetTaskSchedulerFilePath();
        }
        else
        {
            var dataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Athena"
            );
            Directory.CreateDirectory(dataDir);
            _tasksFilePath = Path.Combine(dataDir, "scheduled_tasks.json");
        }

        var dir = Path.GetDirectoryName(_tasksFilePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        Tasks = new ObservableCollection<ScheduledTask>();
        LoadTasksFromFile();
        _timer = new Timer(CheckTasks, null, Timeout.Infinite, Timeout.Infinite);

        _logger.Information("TaskScheduler initialized at {Path} with {Count} tasks", _tasksFilePath, Tasks.Count);
    }

    /// <summary>
    /// 启动调度器
    /// </summary>
    public void Start()
    {
        if (_isRunning) return;

        _isRunning = true;
        _timer.Change(TimeSpan.Zero, TimeSpan.FromMinutes(1));
        _logger.Information("TaskScheduler started");
    }

    /// <summary>
    /// 停止调度器
    /// </summary>
    public void Stop()
    {
        if (!_isRunning) return;

        _isRunning = false;
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        _logger.Information("TaskScheduler stopped");
    }

    /// <summary>
    /// 安排新任务
    /// </summary>
    public async Task ScheduleAsync(ScheduledTask task)
    {
        if (task == null)
        {
            throw new ArgumentNullException(nameof(task));
        }

        task.RecurrenceRule = _recurrenceService.Normalize(task.RecurrenceRule);
        task.ScheduleBoundary = task.ScheduleBoundary == default ? task.TriggerTime : task.ScheduleBoundary;

        if (string.IsNullOrEmpty(task.Id))
        {
            task.Id = Guid.NewGuid().ToString();
        }

        var firstTrigger = _recurrenceService.GetFirstTriggerTime(task.ScheduleBoundary, task.RecurrenceRule, DateTime.Now);
        if (!firstTrigger.HasValue)
        {
            throw new InvalidOperationException("Task cannot be scheduled because its first trigger time is not in the future.");
        }

        task.TriggerTime = firstTrigger.Value;
        task.IsExecuted = false;
        Tasks.Add(task);

        await SaveTasksToFileAsync();

        _logger.Information("Task scheduled: {TaskId} at {TriggerTime} - {Intent}",
            task.Id, task.TriggerTime, task.Intent);
    }

    /// <summary>
    /// 取消任务
    /// </summary>
    public async Task<bool> CancelAsync(string taskId)
    {
        var task = Tasks.FirstOrDefault(t => t.Id == taskId);
        if (task == null)
        {
            _logger.Warning("Task not found for cancellation: {TaskId}", taskId);
            return false;
        }

        Tasks.Remove(task);
        _waitingForegroundTaskIds.Remove(taskId);
        if (string.Equals(_activeForegroundTaskId, taskId, StringComparison.Ordinal))
        {
            _activeForegroundTaskId = null;
        }

        await SaveTasksToFileAsync();

        _logger.Information("Task cancelled: {TaskId}", taskId);
        return true;
    }

    /// <summary>
    /// 获取所有活动任务
    /// </summary>
    public Task<List<ScheduledTask>> GetUpcomingTasksAsync()
    {
        var upcoming = Tasks
            .Where(t => !t.IsExecuted)
            .OrderBy(t => t.TriggerTime)
            .ToList();

        return Task.FromResult(upcoming);
    }

    /// <summary>
    /// 获取指定任务
    /// </summary>
    public ScheduledTask? GetTask(string taskId)
    {
        return Tasks.FirstOrDefault(t => t.Id == taskId);
    }

    /// <summary>
    /// 清除所有任务
    /// </summary>
    public async Task ClearAllAsync()
    {
        Tasks.Clear();
        _waitingForegroundTaskIds.Clear();
        _activeForegroundTaskId = null;
        await SaveTasksToFileAsync();
        _logger.Information("All tasks cleared");
    }

    public Task RunDueTasksAsync()
    {
        return CheckTasksCoreAsync(includeWaitingForegroundTasks: true);
    }

    public async Task CompleteTaskExecutionAsync(string taskId, TaskExecutionOutcome outcome, string? note = null)
    {
        var shouldRecheck = false;

        await _checkLock.WaitAsync();
        try
        {
            var task = GetTask(taskId);
            if (task == null)
            {
                _logger.Warning("Received completion for missing task {TaskId}", taskId);
                return;
            }

            if (string.Equals(_activeForegroundTaskId, taskId, StringComparison.Ordinal))
            {
                _activeForegroundTaskId = null;
            }

            ApplyForegroundCompletion(task, outcome, note, DateTime.Now);
            await SaveTasksToFileAsync();
            shouldRecheck = true;
        }
        finally
        {
            _checkLock.Release();
        }

        if (shouldRecheck)
        {
            await CheckTasksCoreAsync(includeWaitingForegroundTasks: true);
        }
    }

    private void CheckTasks(object? state)
    {
        _ = CheckTasksCoreAsync(includeWaitingForegroundTasks: false);
    }

    /// <summary>
    /// 定时检查任务
    /// </summary>
    private async Task CheckTasksCoreAsync(bool includeWaitingForegroundTasks)
    {
        if (!_isRunning || _disposed)
        {
            return;
        }

        if (!await _checkLock.WaitAsync(0))
        {
            _logger.Debug("Skip task check because a previous check is still running");
            return;
        }

        try
        {
            var now = DateTime.Now;
            var changed = false;

            var dueTasks = Tasks
                .Where(t => !t.IsExecuted && t.TriggerTime <= now)
                .OrderBy(t => t.TriggerTime)
                .ThenBy(t => t.RecurrenceRule.IsRecurring)
                .ToList();

            foreach (var task in dueTasks.Where(t => t.TaskType == Models.TaskType.Background))
            {
                await ExecuteBackgroundTaskAsync(task, now);
                changed = true;
            }

            var dueForeground = dueTasks
                .Where(t => t.TaskType == Models.TaskType.Proactive)
                .ToList();

            if (_activeForegroundTaskId != null)
            {
                changed |= HandleBusyForegroundTasks(dueForeground, now);
            }
            else
            {
                var candidate = SelectForegroundCandidate(dueForeground, includeWaitingForegroundTasks);
                if (candidate != null)
                {
                    await DispatchForegroundTaskAsync(candidate);
                    changed = true;
                }

                if (_activeForegroundTaskId != null)
                {
                    changed |= HandleBusyForegroundTasks(
                        dueForeground.Where(t => t.Id != candidate?.Id),
                        now);
                }
            }

            if (changed)
            {
                await SaveTasksToFileAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error checking tasks");
        }
        finally
        {
            _checkLock.Release();
        }
    }

    private ScheduledTask? SelectForegroundCandidate(IEnumerable<ScheduledTask> dueForeground, bool includeWaitingForegroundTasks)
    {
        return dueForeground
            .Where(t => includeWaitingForegroundTasks || !_waitingForegroundTaskIds.Contains(t.Id))
            .OrderBy(t => t.RecurrenceRule.IsRecurring)
            .ThenBy(t => t.TriggerTime)
            .FirstOrDefault();
    }

    private bool HandleBusyForegroundTasks(IEnumerable<ScheduledTask> dueForeground, DateTime now)
    {
        var changed = false;
        foreach (var task in dueForeground)
        {
            if (task.RecurrenceRule.IsRecurring)
            {
                var nextTrigger = _recurrenceService.GetNextTriggerTime(task.ScheduleBoundary, task.RecurrenceRule, now);
                if (nextTrigger.HasValue)
                {
                    task.TriggerTime = nextTrigger.Value;
                    task.IsExecuted = false;
                    task.LastExecutionAt = now;
                    task.LastExecutionOutcome = TaskExecutionOutcome.Busy.ToString();
                    task.LastExecutionNote = "Skipped while another foreground task was busy.";
                    _waitingForegroundTaskIds.Remove(task.Id);
                    changed = true;

                    _logger.Information("Skipped recurring foreground task {TaskId} while busy. Next trigger: {NextTrigger}",
                        task.Id, task.TriggerTime);
                }
            }
            else
            {
                _waitingForegroundTaskIds.Add(task.Id);
                _logger.Information("Deferred one-time foreground task {TaskId} while busy", task.Id);
            }
        }

        return changed;
    }

    private async Task DispatchForegroundTaskAsync(ScheduledTask task)
    {
        _waitingForegroundTaskIds.Remove(task.Id);
        _activeForegroundTaskId = task.Id;

        if (ProactiveMessageTriggered == null)
        {
            _logger.Warning("No proactive task subscriber registered for task {TaskId}", task.Id);
            _activeForegroundTaskId = null;
            ApplyForegroundCompletion(task, TaskExecutionOutcome.Failed, "No proactive task handler registered.", DateTime.Now);
            return;
        }

        _logger.Information("Dispatching foreground task: {TaskId} - {Intent}", task.Id, task.Intent);

        ProactiveMessageTriggered.Invoke(this, new ProactiveMessageEventArgs
        {
            TaskId = task.Id,
            Intent = task.Intent,
            TriggeredAt = DateTime.Now,
            Task = task
        });

        await Task.CompletedTask;
    }

    private async Task ExecuteBackgroundTaskAsync(ScheduledTask task, DateTime now)
    {
        _logger.Information("Executing background task: {TaskId} - {Intent}", task.Id, task.Intent);

        var outcome = TaskExecutionOutcome.Succeeded;
        string? note = null;

        try
        {
            BackgroundTaskTriggered?.Invoke(this, new BackgroundTaskEventArgs
            {
                TaskId = task.Id,
                Intent = task.Intent,
                TriggeredAt = now,
                Task = task
            });
        }
        catch (Exception ex)
        {
            outcome = TaskExecutionOutcome.Failed;
            note = ex.Message;
            _logger.Error(ex, "Error executing background task: {TaskId}", task.Id);
        }

        ApplyBackgroundCompletion(task, outcome, note, now);
        await Task.CompletedTask;
    }

    private void ApplyForegroundCompletion(ScheduledTask task, TaskExecutionOutcome outcome, string? note, DateTime completedAt)
    {
        task.LastExecutionAt = completedAt;
        task.LastExecutionOutcome = outcome.ToString();
        task.LastExecutionNote = note;

        if (outcome == TaskExecutionOutcome.Busy)
        {
            if (task.RecurrenceRule.IsRecurring)
            {
                var nextTrigger = _recurrenceService.GetNextTriggerTime(task.ScheduleBoundary, task.RecurrenceRule, completedAt);
                if (nextTrigger.HasValue)
                {
                    task.TriggerTime = nextTrigger.Value;
                }
                task.IsExecuted = false;
                _waitingForegroundTaskIds.Remove(task.Id);
            }
            else
            {
                _waitingForegroundTaskIds.Add(task.Id);
                task.IsExecuted = false;
            }

            return;
        }

        _waitingForegroundTaskIds.Remove(task.Id);

        if (task.RecurrenceRule.IsRecurring)
        {
            var nextTrigger = _recurrenceService.GetNextTriggerTime(task.ScheduleBoundary, task.RecurrenceRule, completedAt);
            if (nextTrigger.HasValue)
            {
                task.TriggerTime = nextTrigger.Value;
            }
            task.IsExecuted = false;
        }
        else
        {
            task.IsExecuted = true;
        }

        _logger.Information("Foreground task completed: {TaskId}, outcome={Outcome}, next={NextTrigger}",
            task.Id, outcome, task.IsExecuted ? "completed" : task.TriggerTime);
    }

    private void ApplyBackgroundCompletion(ScheduledTask task, TaskExecutionOutcome outcome, string? note, DateTime completedAt)
    {
        task.LastExecutionAt = completedAt;
        task.LastExecutionOutcome = outcome.ToString();
        task.LastExecutionNote = note;

        if (task.RecurrenceRule.IsRecurring)
        {
            var nextTrigger = _recurrenceService.GetNextTriggerTime(task.ScheduleBoundary, task.RecurrenceRule, completedAt);
            if (nextTrigger.HasValue)
            {
                task.TriggerTime = nextTrigger.Value;
            }
            task.IsExecuted = false;
        }
        else
        {
            task.IsExecuted = true;
        }
    }

    /// <summary>
    /// 从文件加载任务
    /// </summary>
    private void LoadTasksFromFile()
    {
        try
        {
            if (!File.Exists(_tasksFilePath))
            {
                _logger.Debug("No tasks file found, starting with empty collection");
                return;
            }

            var json = File.ReadAllText(_tasksFilePath);
            var tasks = JsonSerializer.Deserialize<List<ScheduledTaskData>>(json);

            if (tasks == null)
            {
                return;
            }

            Tasks.Clear();
            foreach (var data in tasks)
            {
                var taskType = Models.TaskType.Proactive;
                if (Enum.TryParse<Models.TaskType>(data.TaskType, ignoreCase: true, out var parsedTaskType))
                {
                    taskType = parsedTaskType;
                }

                var recurrenceRule = data.RecurrenceRule != null
                    ? _recurrenceService.Normalize(data.RecurrenceRule)
                    : _recurrenceService.MigrateLegacyRule(data.Recurrence, data.ScheduleBoundary ?? data.TriggerTime);

                var task = new ScheduledTask
                {
                    Id = data.Id,
                    TriggerTime = data.TriggerTime,
                    Intent = data.Intent,
                    RecurrenceRule = recurrenceRule,
                    IsExecuted = data.IsExecuted,
                    CreatedAt = data.CreatedAt,
                    TaskType = taskType,
                    ScheduleBoundary = data.ScheduleBoundary ?? data.TriggerTime,
                    LastExecutionAt = data.LastExecutionAt,
                    LastExecutionOutcome = data.LastExecutionOutcome,
                    LastExecutionNote = data.LastExecutionNote
                };

                if (!task.IsExecuted || task.RecurrenceRule.IsRecurring)
                {
                    Tasks.Add(task);
                }
            }

            _logger.Information("Loaded {Count} tasks from file", Tasks.Count);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error loading tasks from file");
        }
    }

    /// <summary>
    /// 保存任务到文件
    /// </summary>
    private async Task SaveTasksToFileAsync()
    {
        try
        {
            var tasksData = Tasks.Select(t => new ScheduledTaskData
            {
                Id = t.Id,
                TriggerTime = t.TriggerTime,
                Intent = t.Intent,
                RecurrenceRule = _recurrenceService.Normalize(t.RecurrenceRule),
                IsExecuted = t.IsExecuted,
                CreatedAt = t.CreatedAt,
                TaskType = t.TaskType.ToString(),
                ScheduleBoundary = t.ScheduleBoundary,
                LastExecutionAt = t.LastExecutionAt,
                LastExecutionOutcome = t.LastExecutionOutcome,
                LastExecutionNote = t.LastExecutionNote
            }).ToList();

            var options = new JsonSerializerOptions
            {
                WriteIndented = true
            };

            var json = JsonSerializer.Serialize(tasksData, options);
            await File.WriteAllTextAsync(_tasksFilePath, json);

            _logger.Debug("Saved {Count} tasks to file", tasksData.Count);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error saving tasks to file");
        }
    }

    /// <summary>
    /// 任务数据类（用于 JSON 序列化）
    /// </summary>
    private class ScheduledTaskData
    {
        public string Id { get; set; } = string.Empty;
        public DateTime TriggerTime { get; set; }
        public string Intent { get; set; } = string.Empty;
        public string? Recurrence { get; set; } = "none";
        public RecurrenceRule? RecurrenceRule { get; set; }
        public bool IsExecuted { get; set; }
        public DateTime CreatedAt { get; set; }
        public string TaskType { get; set; } = "Proactive";
        public DateTime? ScheduleBoundary { get; set; }
        public DateTime? LastExecutionAt { get; set; }
        public string? LastExecutionOutcome { get; set; }
        public string? LastExecutionNote { get; set; }
    }

    public void Dispose()
    {
        if (_disposed) return;

        Stop();
        _timer.Dispose();
        _checkLock.Dispose();
        _disposed = true;

        _logger.Information("TaskScheduler disposed");
    }
}
