using System;
using System.Collections.Generic;
using System.Linq;

namespace Athena.UI.Services.GameMode;

/// <summary>海图要知道的一个工作区：身份、名字、创建时间，以及文件夹还在不在。</summary>
public sealed record PolisIslandSource(string WorkspaceId, string Name, DateTimeOffset CreatedAt, bool FolderMissing);

/// <summary>
/// 海图上的一座岛（= 一座城邦，设计稿 9.1）。<see cref="Order"/> 是它的位次：神殿是 0，工作区按创建时间从 1 起排。
/// <see cref="Running"/>：有会话在跑，岛上亮灯（光属于雅典娜，她在哪干活哪里就亮）。
/// <see cref="WaitingForApproval"/>：有一步在等你点头——"轮到你了"，海图上带点缀色。
/// </summary>
public sealed record PolisIsland(
    string Id,
    string Name,
    int Order,
    bool IsSanctuary,
    bool Running,
    bool WaitingForApproval,
    int Commissions,
    bool Current,
    bool FolderMissing);

/// <summary>
/// 群岛海图（设计稿 9.1、9.3）：一座岛一座城，按工作区的创建时间排定位置，保持稳定；神殿（全局对话）在正中。
/// 选岛 = 选中那个工作区最近的会话——游戏不另存"当前会话"，走的是会话树的同一条路径（9.2）。纯函数。
/// </summary>
public static class PolisIslands
{
    /// <summary>神殿那座岛的 Id（没有工作区的会话都在那里）。</summary>
    public const string SanctuaryId = "sanctuary";

    public static IReadOnlyList<PolisIsland> Build(
        IReadOnlyList<PolisIslandSource> workspaces,
        IReadOnlyList<PolisCommission> commissions,
        string? currentIslandId)
    {
        ArgumentNullException.ThrowIfNull(workspaces);
        ArgumentNullException.ThrowIfNull(commissions);
        var byIsland = commissions.ToLookup(c => string.IsNullOrEmpty(c.WorkspaceId) ? SanctuaryId : c.WorkspaceId!, StringComparer.Ordinal);
        PolisIsland Island(string id, string name, int order, bool sanctuary, bool missing)
        {
            var mine = byIsland[id].ToList();
            return new PolisIsland(id, name, order, sanctuary,
                mine.Any(c => c.Running), mine.Any(c => c.WaitingForApproval), mine.Count,
                string.Equals(id, currentIslandId ?? SanctuaryId, StringComparison.Ordinal), missing);
        }

        var islands = new List<PolisIsland> { Island(SanctuaryId, string.Empty, 0, sanctuary: true, missing: false) };
        var order = 1;
        foreach (var w in workspaces
                     .GroupBy(w => w.WorkspaceId, StringComparer.Ordinal).Select(g => g.First())
                     .OrderBy(w => w.CreatedAt)
                     .ThenBy(w => w.WorkspaceId, StringComparer.Ordinal))
        {
            islands.Add(Island(w.WorkspaceId, w.Name, order++, sanctuary: false, w.FolderMissing));
        }
        return islands;
    }

    /// <summary>选岛要选中的会话：那座岛上最近更新的委托；同一时刻按会话 Id。没有委托返回 null（调用方新建一份）。</summary>
    public static string? MostRecentConversation(string islandId, IReadOnlyList<PolisCommission> commissions)
    {
        ArgumentNullException.ThrowIfNull(commissions);
        return commissions
            .Where(c => string.Equals(string.IsNullOrEmpty(c.WorkspaceId) ? SanctuaryId : c.WorkspaceId, islandId, StringComparison.Ordinal))
            .OrderByDescending(c => c.UpdatedAt)
            .ThenBy(c => c.ConversationId, StringComparer.Ordinal)
            .Select(c => c.ConversationId)
            .FirstOrDefault();
    }
}
