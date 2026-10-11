using Athena.UI.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Athena.UI.Services.GameMode;

/// <summary>
/// 一份委托（= 一个会话，设计稿 9.1）在游戏眼里的样子：只有身份、时间和几个状态，没有任何正文。
/// 视图模型从会话树上的会话投影出它（服务层的签名不认识视图模型，评审规则 4），橄榄林、传令官、海图、委托板都读它。
/// </summary>
public sealed record PolisCommission(
    string ConversationId,
    string? WorkspaceId,
    string Title,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt,
    int CompletedTurns,
    bool Running,
    bool WaitingForApproval,
    bool Interrupted,
    string? CronTaskId)
{
    /// <summary>定时任务开的会话（每次触发一个全新的会话）。</summary>
    public bool Scheduled => !string.IsNullOrEmpty(CronTaskId);
}

/// <summary>从会话的渲染模型读委托的几个数（不读正文）。</summary>
public static class PolisCommissions
{
    /// <summary>
    /// 完成了几回合：可见的助手气泡里已经停止流式输出的那些（成功、停止、报错都算——和交付是同一个时刻）。
    /// 隐藏的工具调用载体不算。
    /// </summary>
    public static int CompletedTurns(IReadOnlyList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        return messages.Count(m => IsAssistantBubble(m) && !m.IsStreaming && !m.IsLoading);
    }

    /// <summary>委托开始的时刻：第一条消息的时间；还没有消息时由调用方给回退值。</summary>
    public static DateTimeOffset StartedAt(IReadOnlyList<ChatMessage> messages, DateTimeOffset fallback)
    {
        ArgumentNullException.ThrowIfNull(messages);
        return messages.Count == 0 ? fallback : new DateTimeOffset(messages[0].Timestamp);
    }

    /// <summary>可见的助手气泡（不是隐藏的工具调用载体）。与事件推导用的是同一个判断。</summary>
    public static bool IsAssistantBubble(ChatMessage message)
        => string.Equals(message.Role, "assistant", StringComparison.Ordinal)
           && !message.IsHidden
           && string.IsNullOrEmpty(message.ToolCallsJson);
}

/// <summary>橄榄树的树龄：完成的回合越多，树越老。</summary>
public enum PolisTreeStage
{
    /// <summary>一回合：树苗。</summary>
    Sapling,
    /// <summary>2–4 回合：小树。</summary>
    Young,
    /// <summary>5–9 回合：成树。</summary>
    Grown,
    /// <summary>10 回合以上：老树。</summary>
    Ancient
}

/// <summary>橄榄林里的一棵树：一份完成过的委托。<see cref="Index"/> 是它在林子里的位次（种下的先后）。</summary>
public sealed record PolisTree(
    string ConversationId,
    string Title,
    DateTimeOffset PlantedAt,
    int Index,
    PolisTreeStage Stage,
    int Turns,
    bool Interrupted,
    bool Scheduled);

/// <summary>
/// 橄榄林（设计稿 7.2）：每完成一次委托，林中长出一棵橄榄树（橄榄树是雅典娜送给雅典的礼物）。
/// 全部从会话推导，不另存（10.1"从会话推导：橄榄林——不存"）：一份委托只要交付过一回合就有一棵树，
/// 回合越多树越老；按委托开始的先后排位次，同一时刻按会话 Id——结果与会话树的顺序、加载顺序都无关。
/// 删掉一个会话，它后面的树各往前挪一格：林子没有账本，这是"不另存"的代价，比存一份可能和会话对不上的名单好。
/// </summary>
public static class PolisGrove
{
    public static PolisTreeStage StageFor(int turns) => turns switch
    {
        <= 1 => PolisTreeStage.Sapling,
        <= 4 => PolisTreeStage.Young,
        <= 9 => PolisTreeStage.Grown,
        _ => PolisTreeStage.Ancient
    };

    /// <summary>这座城的林子：只看属于它的委托（神殿 = 没有工作区的委托）。</summary>
    public static IReadOnlyList<PolisTree> Derive(IEnumerable<PolisCommission> commissions, string? workspaceId)
    {
        ArgumentNullException.ThrowIfNull(commissions);
        return commissions
            .Where(c => string.Equals(c.WorkspaceId ?? string.Empty, workspaceId ?? string.Empty, StringComparison.Ordinal))
            .Where(c => c.CompletedTurns > 0)
            .OrderBy(c => c.StartedAt)
            .ThenBy(c => c.ConversationId, StringComparer.Ordinal)
            .Select((c, i) => new PolisTree(c.ConversationId, c.Title, c.StartedAt, i, StageFor(c.CompletedTurns), c.CompletedTurns, c.Interrupted, c.Scheduled))
            .ToList();
    }
}
