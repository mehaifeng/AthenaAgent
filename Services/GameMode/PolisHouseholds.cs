using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Athena.UI.Services.GameMode;

/// <summary>一座村舍（第四轮）：钥匙稳定（<c>household:cottage01</c>…），名牌上写住在里面第一位的名字加"一家"。</summary>
public sealed record PolisHousehold(string Key, string Label, int Members, DateTimeOffset LastActive);

/// <summary>
/// 村舍（第四轮，2026-10-15；M-D 的"户与民居"改成这样）。居民从建筑来之后，没有文件夹可归的人不再有——会话聚成户的那一套删了；
/// 留下的是它的那本账：民居的地在同一本账上、永不挪位、不占文件夹的名额。现在用它盖两种村舍：
/// 神殿岛没有文件夹，固定 <see cref="SanctuaryCottages"/> 座；工作区的城住所不足 <see cref="MinDwellings"/> 处（文件夹的建筑加市集的摊位），
/// 补到这个数——用户说会话少的时候城太空、像鬼城，住所太少的城同样空。纯函数，<c>Athena.Archive.Tests</c> 的 <c>polis households</c> 覆盖。
///
/// 规则（每一条都有断言）：
/// 1. <b>几座</b>（<see cref="CottageCount"/>）：神殿岛 6 座；工作区 max(0, 4 − 文件夹的建筑 − 摊位)。文件夹多起来，多出来的村舍拆成空地。
/// 2. <b>钥匙稳定</b>：第 k 座是 <c>household:cottage</c> 加两位序号——同一座城的第 k 座永远是同一块地、同一户人。
/// 3. <b>地在同一本账上，永不挪位</b>（<see cref="Place"/>）：新的村舍按生长次序占下一块空地，已有的留在原地，不要了的标成空地；
///    文件夹那一遍（<see cref="PolisLayoutLedger.Update"/>）不动村舍的条目、也不把它们算进独立建筑的上限。
/// 4. <b>只在推给页面的城里</b>：C# 自己的城（委托板、归属、改名推断、外部改动）只有文件夹的建筑；村舍由 <see cref="WithHouseholds"/> 合进推给页面的那一份。
/// </summary>
public static class PolisHouseholds
{
    public const string KeyPrefix = PolisLayoutLedger.HouseholdKeyPrefix;
    public const int SanctuaryCottages = 6;
    public const int MinDwellings = 4;

    public static bool IsHouseholdKey(string? key) => PolisLayoutLedger.IsHouseholdEntry(key);

    /// <summary>规则 1：这座城要几座村舍。<paramref name="folderDwellings"/> 是文件夹的建筑与摊位的总数。</summary>
    public static int CottageCount(bool sanctuary, int folderDwellings)
        => sanctuary ? SanctuaryCottages : Math.Max(0, MinDwellings - Math.Max(0, folderDwellings));

    /// <summary>规则 2：第 <paramref name="index"/> 座村舍的钥匙（从 1 起）。</summary>
    public static string CottageKey(int index) => KeyPrefix + "cottage" + index.ToString("00", CultureInfo.InvariantCulture);

    /// <summary>规则 1、2：这座城的村舍（名牌先空着，等住进去的人有了名字再填，见 <see cref="Labeled"/>）。</summary>
    public static IReadOnlyList<PolisHousehold> Cottages(int count, DateTimeOffset lastActive)
        => Enumerable.Range(1, Math.Max(0, count)).Select(k => new PolisHousehold(CottageKey(k), string.Empty, 0, lastActive)).ToList();

    /// <summary>村舍的名牌与人数：住在里面的第一位（序号 0）的名字加"一家"（<paramref name="labelOf"/> 拼），人数照实数。</summary>
    public static IReadOnlyList<PolisHousehold> Labeled(IReadOnlyList<PolisHousehold> cottages, IReadOnlyList<PolisCitizen> citizens, Func<PolisCitizen, string> labelOf)
    {
        ArgumentNullException.ThrowIfNull(cottages);
        ArgumentNullException.ThrowIfNull(citizens);
        ArgumentNullException.ThrowIfNull(labelOf);
        return cottages.Select(h =>
        {
            var people = citizens.Where(c => string.Equals(c.Dwelling, h.Key, StringComparison.Ordinal)).OrderBy(c => c.Index).ToList();
            return people.Count == 0 ? h : h with { Label = labelOf(people[0]), Members = people.Count };
        }).ToList();
    }

    /// <summary>
    /// 规则 3：户的条目写进账本。已有的户回到原来那块地（解散过、又立起来的也回原地）；新的户按生长次序占下一块空地；
    /// 这一次没有的户标成空地（地块保留）。文件夹的条目一条不动。
    /// </summary>
    public static PolisLedger Place(PolisLedger ledger, IReadOnlyList<PolisHousehold> households)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(households);
        var wanted = households.Select(h => h.Key).ToHashSet(StringComparer.Ordinal);
        var entries = ledger.Entries
            .Select(e => IsHouseholdKey(e.Key) ? e with { Vacated = !wanted.Contains(e.Key) } : e)
            .ToList();
        var have = entries.Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
        var occupied = entries.Select(e => new PolisPlot(e.X, e.Z)).ToHashSet();
        var nextOrder = entries.Count == 0 ? 0 : entries.Max(e => e.Order) + 1;
        using var free = PolisLayoutLedger.GrowthOrder().Where(p => !occupied.Contains(p)).GetEnumerator();
        foreach (var h in households.Where(h => !have.Contains(h.Key)))
        {
            free.MoveNext();
            entries.Add(new PolisLedgerEntry(h.Key, free.Current.X, free.Current.Z, nextOrder++, Vacated: false));
        }
        return new PolisLedger(PolisLedger.CurrentSchemaVersion, entries.OrderBy(e => e.Order).ToList());
    }

    /// <summary>两本账本是不是一样（村舍的条目变了才需要重新推城、写存档）。</summary>
    public static bool SameLedger(PolisLedger a, PolisLedger b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return a.Entries.Count == b.Entries.Count && a.Entries.Zip(b.Entries).All(p => p.First == p.Second);
    }

    /// <summary>
    /// 后台核对是拿开始那一刻的账本算的；算的期间新立的户（加进了此刻的账本）不能被核对的结果冲掉。核对的条目全部保留，
    /// 此刻账本里户的条目照抄过去（核对那边有的，以此刻的"在不在"为准）；它的地被核对新分给了某个文件夹时，这一户不要——
    /// 下一次聚户会给它另找一块地（极少见：核对开始之后才立的户，又恰好撞上核对时新出现的文件夹）。
    /// </summary>
    public static PolisLedger MergeAfterReconcile(PolisLedger reconciled, PolisLedger current)
    {
        ArgumentNullException.ThrowIfNull(reconciled);
        ArgumentNullException.ThrowIfNull(current);
        var currentHouseholds = current.Entries.Where(e => IsHouseholdKey(e.Key)).ToDictionary(e => e.Key, StringComparer.Ordinal);
        var entries = reconciled.Entries
            .Select(e => IsHouseholdKey(e.Key) && currentHouseholds.TryGetValue(e.Key, out var now) ? e with { Vacated = now.Vacated } : e)
            .ToList();
        var keys = entries.Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
        var occupied = entries.Select(e => new PolisPlot(e.X, e.Z)).ToHashSet();
        foreach (var e in currentHouseholds.Values.OrderBy(e => e.Order))
        {
            if (keys.Contains(e.Key) || occupied.Contains(new PolisPlot(e.X, e.Z))) continue;
            entries.Add(e);
            keys.Add(e.Key);
            occupied.Add(new PolisPlot(e.X, e.Z));
        }
        return new PolisLedger(PolisLedger.CurrentSchemaVersion, entries.OrderBy(e => e.Order).ToList());
    }

    /// <summary>村舍的体量一律是 2（住 2–3 人，<see cref="PolisCitizens.HeadCount"/>）：人数由体量定，体量不能反过来由人数定。</summary>
    public const int CottageSize = 2;

    /// <summary>
    /// 规则 4：把村舍合进推给页面的那一份城——村舍是 <see cref="PolisBuildingKind.House"/>，名牌是"某某一家"，体量 <see cref="CottageSize"/>；
    /// 还立着的村舍那块地不再算空地。账本里没有条目的不画（写进账本之后再画）。
    /// </summary>
    public static PolisFixtureDocument WithHouseholds(PolisFixtureDocument city, IReadOnlyList<PolisHousehold> households, PolisLedger ledger, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(city);
        ArgumentNullException.ThrowIfNull(households);
        ArgumentNullException.ThrowIfNull(ledger);
        var plots = ledger.Entries.Where(e => IsHouseholdKey(e.Key) && !e.Vacated).ToDictionary(e => e.Key, StringComparer.Ordinal);
        var houses = households
            .Where(h => plots.ContainsKey(h.Key))
            .Select(h =>
            {
                var e = plots[h.Key];
                return new PolisFixtureBuilding(
                    h.Key, PolisBuildingKind.House, Math.Max(1, h.Members), 0, new Dictionary<string, int>(),
                    h.LastActive, PolisBuildings.StateFor(h.LastActive, now), CottageSize, Incomplete: false,
                    new PolisPlot(e.X, e.Z), e.Order)
                { Label = string.IsNullOrEmpty(h.Label) ? null : h.Label };
            })
            .ToList();
        var standing = houses.Select(h => h.Key).ToHashSet(StringComparer.Ordinal);
        return city with
        {
            Buildings = city.Buildings.Where(b => !IsHouseholdKey(b.Key)).Concat(houses).ToList(),
            Vacant = city.Vacant.Where(v => !standing.Contains(v.Key)).ToList(),
        };
    }
}
