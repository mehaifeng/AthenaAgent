using Athena.UI.Models;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.Interfaces;

/// <summary>
/// 子代理编排器：主模型通过 dispatch_subagents 工具调用，按批并行派生隔离上下文的子代理，
/// 只把合并摘要回传主上下文，同时把每只猫头鹰的实时状态暴露给 UI。
/// </summary>
public interface ISubAgentOrchestrator
{
    /// <summary>
    /// 当前活动 / 已完成的子代理集合，供 UI（侧边 Dock + 猫头鹰小镇）绑定。
    /// 元素的运行期类型是展示层的 SubAgentViewModel，但本层只认进度面：
    /// 抽象一旦在签名里点名 ViewModel，就再也不可能有非 UI 实现。
    /// </summary>
    ObservableCollection<ISubAgentProgress> ActiveAgents { get; }

    /// <summary>并行执行一批子代理任务，阻塞到全部结束，返回合并后的摘要文本。</summary>
    Task<string> DispatchBatchAsync(SubAgentTaskInput[] tasks, CancellationToken cancellationToken);

    /// <summary>移除集合中所有已结束（完成/出错/取消）的子代理。</summary>
    void ClearCompleted();
}
