using Athena.UI.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Athena.UI.Services.Interfaces;

/// <summary>
/// 知识库定期整理服务：按配置的间隔在后台合并去重知识库文件，
/// 也支持手动"立即整理"。运行态持久化，独立于用户配置。
/// </summary>
public interface IKnowledgeBaseMaintenanceService
{
    /// <summary>启动后台计时器（应用启动时调用一次）。</summary>
    void Start();

    /// <summary>停止后台计时器。</summary>
    void Stop();

    /// <summary>当前运行态（上次运行时间、结果、简报）。</summary>
    KnowledgeMaintenanceState State { get; }

    /// <summary>是否有一轮整理正在进行。</summary>
    bool IsRunning { get; }

    /// <summary>运行态变化时触发（供 UI 刷新）。</summary>
    event EventHandler? StateChanged;

    /// <summary>
    /// 立即运行一轮整理（手动触发或到期自动触发）。
    /// 若已有整理在跑则直接返回当前状态。
    /// </summary>
    Task<KnowledgeMaintenanceState> RunNowAsync(CancellationToken cancellationToken = default);
}
