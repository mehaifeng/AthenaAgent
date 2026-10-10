using System;
using System.IO;

namespace Athena.UI.Services.Interfaces;

/// <summary>监听的状态。<see cref="Unavailable"/>：监视器建不起来，外部改动只能靠下一次整体刷新 / 重扫发现。</summary>
public enum WorkspaceWatchState
{
    Idle,
    Watching,
    Unavailable
}

/// <summary>共享监视器报告的一次改动。<see cref="OldFullPath"/> 只在改名时有值。</summary>
public sealed record WorkspaceFileChange(
    string WorkspaceId,
    string Root,
    WatcherChangeTypes ChangeType,
    string FullPath,
    string? OldFullPath,
    DateTime OccurredAtUtc);

/// <summary>
/// 一批合并后的监视器错误：缓冲区溢出、inotify 撞上限、FSEvents 要求重扫……丢掉的事件补不回来，
/// 订阅者收到它就该把整个工作区重读一遍（工作台整树刷新，游戏整城重扫）。
/// </summary>
public sealed class WorkspaceWatcherErrorEventArgs(string workspaceId, string root, int errorCount, Exception firstError) : EventArgs
{
    public string WorkspaceId { get; } = workspaceId;
    public string Root { get; } = root;
    public int ErrorCount { get; } = errorCount;
    public Exception FirstError { get; } = firstError;
}

/// <summary>
/// 当前工作区唯一的递归文件监视器（设计稿 11.2）。工作台和游戏都订阅它，不再各开一个递归监听：
/// 大仓库上每多一个递归监听，就多一份 inotify watch / FSEvents 流，撞上平台上限的概率随之翻倍。
/// 由工作台在 <c>SetWorkspaceAsync</c> 里切换（那是外壳"当前作用域"的唯一入口），其余订阅者按
/// <see cref="WorkspaceFileChange.WorkspaceId"/> 认领自己的事件。
/// 事件在监视器自己的线程上引发，订阅者自行回到 UI 线程；签名里没有任何 Avalonia 类型。
/// </summary>
public interface IWorkspaceWatcherService : IDisposable
{
    string? WorkspaceId { get; }

    string? Root { get; }

    WorkspaceWatchState State { get; }

    /// <summary>
    /// 把监视器切到这个工作区；两个参数任一为空则停下。从不抛出：建不起来时写一条 Warning、
    /// 状态变成 <see cref="WorkspaceWatchState.Unavailable"/>，调用方据此提示"不会自动刷新"。
    /// </summary>
    WorkspaceWatchState Watch(string? workspaceId, string? root);

    /// <summary>一次文件 / 目录的增删改名。</summary>
    event EventHandler<WorkspaceFileChange>? Changed;

    /// <summary>一批监视器错误（已合并成一次，只写一条 Warning）。</summary>
    event EventHandler<WorkspaceWatcherErrorEventArgs>? ErrorsDropped;

    /// <summary><see cref="State"/> 或所监视的工作区变了。</summary>
    event EventHandler? StateChanged;
}
