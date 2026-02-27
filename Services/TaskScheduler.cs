using Athena.UI.Models;
using Athena.UI.Services.Interfaces;
using Serilog;
using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
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
    private readonly string _dataDir;
    private readonly string _tasksFilePath;
    private readonly Timer _timer;
    private readonly ILogger _logger;
    private bool _isRunning;
    private bool _disposed;

    /// <summary>
    /// 共享的任务集合
    /// </summary>
    public ObservableCollection<ScheduledTask> Tasks { get; }

    /// <summary>
    /// 主动消息触发事件
    /// </summary>
    public event EventHandler<ProactiveMessageEventArgs>? ProactiveMessageTriggered;

    public TaskScheduler(ILogger logger)
    {
        _logger = logger;

        // 初始化数据目录
        _dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Athena"
        );
        Directory.CreateDirectory(_dataDir);
        _tasksFilePath = Path.Combine(_dataDir, "scheduled_tasks.json");

        // 初始化任务集合
        Tasks = new ObservableCollection<ScheduledTask>();

        // 从文件加载任务
        LoadTasksFromFile();

        // 初始化定时器（每分钟检查一次）
        _timer = new Timer(CheckTasks, null, Timeout.Infinite, Timeout.Infinite);

        _logger.Information("TaskScheduler initialized with {Count} tasks", Tasks.Count);
    }

    /// <summary>
    /// 启动调度器
    /// </summary>
    public void Start()
    {
        if (_isRunning) return;

        _isRunning = true;
        // 立即检查一次，然后每分钟检查
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
            throw new ArgumentNullException(nameof(task));

        // 确保有唯一 ID
        if (string.IsNullOrEmpty(task.Id))
            task.Id = Guid.NewGuid().ToString();

        // 添加到集合
        Tasks.Add(task);

        // 持久化
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
        await SaveTasksToFileAsync();

        _logger.Information("Task cancelled: {TaskId}", taskId);
        return true;
    }

    /// <summary>
    /// 获取所有待执行的任务
    /// </summary>
    public Task<List<ScheduledTask>> GetUpcomingTasksAsync()
    {
        var upcoming = Tasks
            .Where(t => !t.IsExecuted && t.TriggerTime > DateTime.Now)
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
        await SaveTasksToFileAsync();
        _logger.Information("All tasks cleared");
    }

    /// <summary>
    /// 定时检查任务
    /// </summary>
    private async void CheckTasks(object? state)
    {
        if (!_isRunning) return;

        try
        {
            var now = DateTime.Now;
            var tasksToTrigger = Tasks
                .Where(t => t.TriggerTime <= now && !t.IsExecuted)
                .OrderBy(t => t.TriggerTime)
                .ToList();

            foreach (var task in tasksToTrigger)
            {
                await ExecuteTaskAsync(task);
            }

            if (tasksToTrigger.Any())
            {
                await SaveTasksToFileAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error checking tasks");
        }
    }

    /// <summary>
    /// 执行任务
    /// </summary>
    private async Task ExecuteTaskAsync(ScheduledTask task)
    {
        _logger.Information("Executing task: {TaskId} - {Intent}", task.Id, task.Intent);

        try
        {
            // 触发事件，让 MainWindow 处理主动消息
            ProactiveMessageTriggered?.Invoke(this, new ProactiveMessageEventArgs
            {
                TaskId = task.Id,
                Intent = task.Intent,
                TriggeredAt = DateTime.Now,
                Task = task
            });

            // 处理循环任务
            if (!string.IsNullOrEmpty(task.Recurrence) && task.Recurrence != "none")
            {
                task.TriggerTime = CalculateNextTrigger(task.TriggerTime, task.Recurrence);
                task.IsExecuted = false;
                _logger.Information("Recurring task rescheduled: {TaskId} to {NextTrigger}",
                    task.Id, task.TriggerTime);
            }
            else
            {
                // 一次性任务标记为已执行
                task.IsExecuted = true;
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error executing task: {TaskId}", task.Id);
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// 计算下次触发时间
    /// </summary>
    private DateTime CalculateNextTrigger(DateTime lastTrigger, string recurrence)
    {
        return recurrence.ToLower() switch
        {
            "daily" => lastTrigger.AddDays(1),
            "weekly" => lastTrigger.AddDays(7),
            var s when s.StartsWith("every ") => ParseCustomRecurrence(lastTrigger, s),
            _ => lastTrigger
        };
    }

    /// <summary>
    /// 解析自定义循环周期
    /// </summary>
    private DateTime ParseCustomRecurrence(DateTime lastTrigger, string recurrence)
    {
        // 解析 "every 3 days", "every 2 hours" 等
        var parts = recurrence.Split(' ');
        if (parts.Length >= 3 && int.TryParse(parts[1], out var amount))
        {
            var unit = parts[2].ToLower();
            return unit switch
            {
                "hours" or "hour" => lastTrigger.AddHours(amount),
                "days" or "day" => lastTrigger.AddDays(amount),
                "weeks" or "week" => lastTrigger.AddDays(amount * 7),
                _ => lastTrigger
            };
        }
        return lastTrigger;
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

            if (tasks != null)
            {
                Tasks.Clear();
                foreach (var data in tasks)
                {
                    // 只加载未执行的或循环任务
                    if (!data.IsExecuted || (data.Recurrence != "none" && !string.IsNullOrEmpty(data.Recurrence)))
                    {
                        Tasks.Add(new ScheduledTask
                        {
                            Id = data.Id,
                            TriggerTime = data.TriggerTime,
                            Intent = data.Intent,
                            Recurrence = data.Recurrence,
                            IsExecuted = data.IsExecuted,
                            CreatedAt = data.CreatedAt
                        });
                    }
                }
                _logger.Information("Loaded {Count} tasks from file", Tasks.Count);
            }
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
                Recurrence = t.Recurrence,
                IsExecuted = t.IsExecuted,
                CreatedAt = t.CreatedAt
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
        public string Recurrence { get; set; } = "none";
        public bool IsExecuted { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public void Dispose()
    {
        if (_disposed) return;

        Stop();
        _timer?.Dispose();
        _disposed = true;

        _logger.Information("TaskScheduler disposed");
    }
}
