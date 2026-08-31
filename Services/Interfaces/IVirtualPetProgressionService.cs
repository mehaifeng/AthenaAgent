using Athena.UI.Models;
using System;
using System.Threading.Tasks;

namespace Athena.UI.Services.Interfaces;

/// <summary>
/// 宠物养成状态的唯一所有者。全应用一个实例：每个会话都有自己的 VirtualPetViewModel，
/// 但窗口上只有一只宠物，成长记录必须活在会话之上，切会话不能把等级清零。
///
/// 签名里只出现域类型（见 CLAUDE.md「Review Rules」第 4 条），投影成可绑定属性是 ViewModel 的活儿。
/// </summary>
public interface IVirtualPetProgressionService
{
    /// <summary>任一宠物的养成状态发生变化。参数是变化后的快照。</summary>
    event EventHandler<VirtualPetSnapshot>? SnapshotChanged;

    /// <summary>读取（必要时创建）某只宠物的当前快照。</summary>
    VirtualPetSnapshot GetSnapshot(string slug);

    /// <summary>
    /// 按真实经过的时间推进心情/精力，并重新判定需求。调用频率不敏感：
    /// 距上次推进不足 <see cref="VirtualPetProgressionRules.MinTickInterval"/> 时直接返回缓存快照。
    /// </summary>
    /// <param name="busy">宠物此刻是否在忙（思考/工具/子代理），决定精力是消耗还是恢复。</param>
    VirtualPetSnapshot Advance(string slug, bool busy);

    /// <summary>用户主动互动一次。冷却中会被拒绝，但仍返回快照供 UI 反馈。</summary>
    PetInteractionResult Interact(string slug, PetInteractionKind kind);

    /// <summary>一轮对话结束。工具调用数只在这里汇总一次，避免每次调用都写盘。</summary>
    VirtualPetSnapshot RecordConversationCompleted(string slug, bool succeeded, int toolCalls);

    /// <summary>一次工具调用失败。只影响心情，不影响经验。</summary>
    VirtualPetSnapshot RecordToolFailure(string slug);

    /// <summary>把尚未落盘的改动立刻写出去。退出流程调用。</summary>
    Task FlushAsync();
}
