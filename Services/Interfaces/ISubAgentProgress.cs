using Athena.UI.Models;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading;

namespace Athena.UI.Services.Interfaces;

/// <summary>
/// 一只子代理"猫头鹰"的实时进度面。
///
/// 这是服务层与展示层之间的那道缝：<see cref="SubAgents.SubAgentRunner"/> 需要一路回写
/// 状态、步数、日志，但它不该知道对面是个 ViewModel。此前 <see cref="ISubAgentOrchestrator"/>
/// 直接把 <c>ObservableCollection&lt;SubAgentViewModel&gt;</c> 写进签名——签名里带 ViewModel 的
/// 抽象永远不可能有非 UI 实现，等于在声明处就把解耦取消了。
///
/// 这里只列 runner 与编排器真正回写/读取的成员。猫头鹰的走位、朝向、帧动画
/// （CanvasX/CanvasY/RepositionWander 等）**不属于**本接口：那是纯展示态，
/// 留在 SubAgentViewModel 上，服务层看不见也不需要看见。
/// </summary>
public interface ISubAgentProgress : IDisposable, INotifyPropertyChanged
{
    string Title { get; set; }

    string AgentType { get; set; }

    SubAgentState State { get; set; }

    string CurrentAction { get; set; }

    int Step { get; set; }

    string ResultSummary { get; set; }

    string ErrorMessage { get; set; }

    /// <summary>该子代理的执行日志，供侧边 Dock 展开查看。</summary>
    ObservableCollection<SubAgentLogEntry> Log { get; }

    /// <summary>本只猫头鹰的取消源：联动批次令牌与自身超时上限。</summary>
    CancellationTokenSource? Cts { get; set; }

    DateTime TimeoutAt { get; set; }

    /// <summary>是否由用户手动停止（与超时/失败区分，决定摘要文案）。</summary>
    bool WasCancelledByUser { get; }

    /// <summary>请求迁往某个区域；展示层据此播放走位动画，服务层只表达意图。</summary>
    void RequestZone(SubAgentZone zone);
}

/// <summary>
/// 由展示层提供的猫头鹰工厂。编排器需要为每个任务创建一只，但不能 <c>new</c> 一个 ViewModel——
/// 那正是要断开的那条依赖。实现挂在 ViewModels 层，运行期由 DI 注入。
/// </summary>
public interface ISubAgentPresenterFactory
{
    ISubAgentProgress Create();
}
