using Athena.UI.Models;
using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Athena.UI.Services.Interfaces;

/// <summary>
/// 主动消息事件参数
/// </summary>
public class ProactiveMessageEventArgs : EventArgs
{
    /// <summary>
    /// 任务 ID
    /// </summary>
    public string TaskId { get; set; } = string.Empty;

    /// <summary>
    /// 任务意图（AI 对自己的提醒）
    /// </summary>
    public string Intent { get; set; } = string.Empty;

    /// <summary>
    /// 触发时间
    /// </summary>
    public DateTime TriggeredAt { get; set; }

    /// <summary>
    /// 原始任务
    /// </summary>
    public ScheduledTask? Task { get; set; }
}

/// <summary>
/// 任务调度器接口
/// 管理计划任务，支持 UI 和 Function Calling 共享
/// </summary>
public interface ITaskScheduler
{
    /// <summary>
    /// 所有计划任务（共享集合，UI 可直接绑定）
    /// </summary>
    ObservableCollection<ScheduledTask> Tasks { get; }

    /// <summary>
    /// 安排新任务
    /// </summary>
    /// <param name="task">任务对象</param>
    Task ScheduleAsync(ScheduledTask task);

    /// <summary>
    /// 取消任务
    /// </summary>
    /// <param name="taskId">任务 ID</param>
    /// <returns>是否成功取消</returns>
    Task<bool> CancelAsync(string taskId);

    /// <summary>
    /// 获取所有待执行的任务
    /// </summary>
    Task<List<ScheduledTask>> GetUpcomingTasksAsync();

    /// <summary>
    /// 获取指定任务
    /// </summary>
    /// <param name="taskId">任务 ID</param>
    /// <returns>任务对象，不存在则返回 null</returns>
    ScheduledTask? GetTask(string taskId);

    /// <summary>
    /// 清除所有任务
    /// </summary>
    Task ClearAllAsync();

    /// <summary>
    /// 主动消息触发事件
    /// 当任务到期时触发，MainWindow 订阅此事件处理消息
    /// </summary>
    event EventHandler<ProactiveMessageEventArgs>? ProactiveMessageTriggered;

    /// <summary>
    /// 启动调度器
    /// </summary>
    void Start();

    /// <summary>
    /// 停止调度器
    /// </summary>
    void Stop();
}
