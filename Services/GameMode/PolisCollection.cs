using System;
using System.Collections.Generic;
using System.Linq;

namespace Athena.UI.Services.GameMode;

/// <summary>摆放的结果：新的存档，或者为什么不行（页面只会提供空位，这些拒绝是给越权或过时的请求的）。</summary>
public sealed record PolisCollectionResult(PolisSaveDocument Save, string? Rejection)
{
    public bool Ok => Rejection == null;

    public static PolisCollectionResult Reject(PolisSaveDocument save, string reason) => new(save, reason);
}

/// <summary>
/// 藏品与沙盒（设计稿 7.2、10.1）。收下的成果可以摆进三处公共场馆（<see cref="PolisVenue"/>），摆在哪、挪去哪、
/// 要不要撤下都由你决定；这一切只改存档，不碰工作区——"拆方块"永远不会变成删文件。
/// 规则只有几条：
/// <list type="bullet">
/// <item>只有收下的才能摆（待处理的先收下，退回的不算你的藏品）；</item>
/// <item>每处场馆 <see cref="SlotsPerVenue"/> 个位次，一个位次只放一件；满了就先撤下一件，不自动扩建；</item>
/// <item>遗失的藏品保留位次，在原地显示为空底座和名牌（11.3）；</item>
/// <item>移除一条记录只对遗失的藏品开放，而且只能是你点的——绝不自动删除。</item>
/// </list>
/// 纯函数：输入旧存档，输出新存档。
/// </summary>
public static class PolisCollection
{
    /// <summary>每处场馆的位次数：两排各 6 个，港前一块台地放得下，近看每件也分得清。</summary>
    public const int SlotsPerVenue = 12;

    /// <summary>页面协议里的场馆名（与存档里的枚举名一致，camelCase）。</summary>
    public static string Slug(PolisVenue venue) => venue switch
    {
        PolisVenue.PaintedStoa => "paintedStoa",
        PolisVenue.Archive => "archive",
        _ => "vault"
    };

    public static bool TryParseVenue(string? slug, out PolisVenue venue)
    {
        switch (slug)
        {
            case "paintedStoa": venue = PolisVenue.PaintedStoa; return true;
            case "archive": venue = PolisVenue.Archive; return true;
            case "vault": venue = PolisVenue.Vault; return true;
            default: venue = default; return false;
        }
    }

    /// <summary>每一类成果默认去的场馆：彩绘板挂进彩绘柱廊，账册收进金库，卷轴和回答放进书库（7.2）。</summary>
    public static PolisVenue DefaultVenue(PolisItemKind kind) => kind switch
    {
        PolisItemKind.Painting => PolisVenue.PaintedStoa,
        PolisItemKind.Ledger => PolisVenue.Vault,
        _ => PolisVenue.Archive
    };

    public static bool IsValid(PolisPlacement? placement)
        => placement != null && Enum.IsDefined(placement.Venue) && placement.Slot is >= 0 and < SlotsPerVenue;

    /// <summary>这处场馆现在空着的位次（从小到大）。</summary>
    public static IReadOnlyList<int> FreeSlots(IReadOnlyList<PolisItem> items, PolisVenue venue)
    {
        ArgumentNullException.ThrowIfNull(items);
        var taken = items.Where(i => i.Placement is { } p && p.Venue == venue).Select(i => i.Placement!.Slot).ToHashSet();
        return Enumerable.Range(0, SlotsPerVenue).Where(s => !taken.Contains(s)).ToList();
    }

    /// <summary>
    /// 摆一件藏品（或者把已经摆着的挪个地方）。<paramref name="slot"/> 为 null 时放进这处场馆第一个空位。
    /// 目标位次被别的藏品占着就拒绝——页面只提供空位；同一件挪回自己原来的位次是允许的空操作。
    /// </summary>
    public static PolisCollectionResult Place(PolisSaveDocument save, string itemId, PolisVenue venue, int? slot)
    {
        ArgumentNullException.ThrowIfNull(save);
        var item = save.Items.FirstOrDefault(i => string.Equals(i.Id, itemId, StringComparison.Ordinal));
        if (item == null) return PolisCollectionResult.Reject(save, "unknown item");
        if (item.State != PolisItemState.Accepted) return PolisCollectionResult.Reject(save, "only accepted items can be placed");
        if (!Enum.IsDefined(venue)) return PolisCollectionResult.Reject(save, "unknown venue");
        if (slot is { } requested && requested is < 0 or >= SlotsPerVenue) return PolisCollectionResult.Reject(save, "slot out of range");

        var others = save.Items.Where(i => !ReferenceEquals(i, item)).ToList();
        int target;
        if (slot is { } wanted)
        {
            if (others.Any(i => i.Placement is { } p && p.Venue == venue && p.Slot == wanted))
                return PolisCollectionResult.Reject(save, "slot is taken");
            target = wanted;
        }
        else
        {
            var free = FreeSlots(others, venue);
            if (free.Count == 0) return PolisCollectionResult.Reject(save, "venue is full");
            // 已经摆在这处场馆的，"放进这里"就是原地不动
            target = item.Placement is { } current && current.Venue == venue ? current.Slot : free[0];
        }

        var placement = new PolisPlacement(venue, target);
        return new PolisCollectionResult(Replace(save, item, item with { Placement = placement }), null);
    }

    /// <summary>撤下：回到"收着、没摆出来"。记录和处理状态都不变。</summary>
    public static PolisCollectionResult Unplace(PolisSaveDocument save, string itemId)
    {
        ArgumentNullException.ThrowIfNull(save);
        var item = save.Items.FirstOrDefault(i => string.Equals(i.Id, itemId, StringComparison.Ordinal));
        if (item == null) return PolisCollectionResult.Reject(save, "unknown item");
        if (item.Placement == null) return new PolisCollectionResult(save, null);
        return new PolisCollectionResult(Replace(save, item, item with { Placement = null }), null);
    }

    /// <summary>
    /// 移除一件藏品的记录。只对遗失的开放（11.3：保留记录和名字，显示为空底座，由你决定是否移除）——
    /// 文件还在的藏品要么摆着、要么收着，没有理由让它从城里消失。这是唯一会删存档记录的路径，只由你的点击触发。
    /// </summary>
    public static PolisCollectionResult Remove(PolisSaveDocument save, string itemId)
    {
        ArgumentNullException.ThrowIfNull(save);
        var item = save.Items.FirstOrDefault(i => string.Equals(i.Id, itemId, StringComparison.Ordinal));
        if (item == null) return PolisCollectionResult.Reject(save, "unknown item");
        if (!item.Lost) return PolisCollectionResult.Reject(save, "only lost items can be removed");
        return new PolisCollectionResult(save with { Items = save.Items.Where(i => !ReferenceEquals(i, item)).ToList() }, null);
    }

    /// <summary>
    /// 读档时的修补：场馆不认识、位次越界的摆放去掉；两件抢同一个位次时先来的（存档里靠前的）留下，后来的回到未摆放。
    /// 返回修补后的列表与修补了几件（读档结果里报告，日志里看得见）。
    /// </summary>
    public static (IReadOnlyList<PolisItem> Items, int Repaired) NormalizePlacements(IReadOnlyList<PolisItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var taken = new HashSet<(PolisVenue, int)>();
        var repaired = 0;
        var result = new List<PolisItem>(items.Count);
        foreach (var item in items)
        {
            if (item.Placement == null)
            {
                result.Add(item);
                continue;
            }
            if (!IsValid(item.Placement) || !taken.Add((item.Placement.Venue, item.Placement.Slot)))
            {
                repaired++;
                result.Add(item with { Placement = null });
                continue;
            }
            result.Add(item);
        }
        return (result, repaired);
    }

    private static PolisSaveDocument Replace(PolisSaveDocument save, PolisItem old, PolisItem updated)
        => save with { Items = save.Items.Select(i => ReferenceEquals(i, old) ? updated : i).ToList() };
}
