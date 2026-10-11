using System;
using System.Collections.Generic;
using System.Linq;

namespace Athena.UI.Services.GameMode;

/// <summary>公共建筑（设计稿 5.3）。它们的地块是固定保留的，账本从不把它们分给文件夹。</summary>
public enum PolisPublicSite
{
    Agora,
    Temple,
    Library,
    Forge,
    Harbor,
    Market
}

/// <summary>棋盘格上的一块地。广场在 (0, 0)；z 向南增大，z ≥ <see cref="PolisLayoutLedger.SeaStartsAtZ"/> 是海。</summary>
public sealed record PolisPlot(int X, int Z);

/// <summary>账本里的一条：哪个文件夹占哪块地、第几个落户、文件夹还在不在。</summary>
public sealed record PolisLedgerEntry(string Key, int X, int Z, int Order, bool Vacated);

public sealed record PolisLedger(int SchemaVersion, IReadOnlyList<PolisLedgerEntry> Entries)
{
    public const int CurrentSchemaVersion = 1;
    public static PolisLedger Empty { get; } = new(CurrentSchemaVersion, Array.Empty<PolisLedgerEntry>());
}

/// <summary>一次布局的结果。<see cref="Ledger"/> 是要存回去的新账本。</summary>
public sealed record PolisLayout(
    PolisLedger Ledger,
    IReadOnlyList<PolisLedgerEntry> Standing,
    IReadOnlyList<PolisLedgerEntry> Vacant,
    IReadOnlyList<string> MarketStalls);

/// <summary>
/// 布局账本（设计稿 5.2）：希波达摩斯式棋盘格，广场居中，公共建筑围着广场，文件夹按街区向外排开。
/// 规则只有三条：已有建筑永不挪位；新文件夹在城市边缘占一块新地；被删的文件夹留下空地（地块永不回收，网格永不收缩）。
/// 纯按路径哈希排列做不到第一条——删掉一个文件夹就可能让别的建筑换位置，空间记忆随之作废。
/// </summary>
public static class PolisLayoutLedger
{
    /// <summary>海从这一行开始。城市只向北、东、西生长，港口在南边的海岸上。</summary>
    public const int SeaStartsAtZ = 2;

    /// <summary>独立建筑的上限，超出的文件夹并进市集成排的摊位（设计稿 5.2 "阈值待定"，M0 先取 30）。</summary>
    public const int DefaultMaxIndependentBuildings = 30;

    public static IReadOnlyDictionary<PolisPublicSite, PolisPlot> PublicSites { get; } = new Dictionary<PolisPublicSite, PolisPlot>
    {
        [PolisPublicSite.Agora] = new(0, 0),
        [PolisPublicSite.Temple] = new(0, -1),
        [PolisPublicSite.Library] = new(-1, 0),
        [PolisPublicSite.Forge] = new(1, 0),
        [PolisPublicSite.Harbor] = new(0, 1),
        [PolisPublicSite.Market] = new(-1, 1),
    };

    private static readonly HashSet<PolisPlot> Reserved = new(PublicSites.Values);

    public static bool IsReserved(PolisPlot plot) => Reserved.Contains(plot);

    /// <summary>
    /// 地块的生长次序：一圈一圈向外（切比雪夫距离），每一圈从正北顺时针走一周；海里和公共建筑的地不算。
    /// 全程整数运算，不依赖浮点角度，在任何平台上次序都一样。
    /// </summary>
    public static IEnumerable<PolisPlot> GrowthOrder()
    {
        for (var r = 1; ; r++)
        {
            foreach (var plot in Ring(r))
            {
                if (plot.Z < SeaStartsAtZ && !IsReserved(plot)) yield return plot;
            }
        }
    }

    private static IEnumerable<PolisPlot> Ring(int r)
    {
        var bottom = Math.Min(r, SeaStartsAtZ - 1);
        for (var x = 0; x <= r; x++) yield return new(x, -r);                 // 北边一行的东半
        for (var z = -r + 1; z <= bottom; z++) yield return new(r, z);       // 东边一列，向南
        var bottomRowOnLand = r <= SeaStartsAtZ - 1;
        if (bottomRowOnLand)
        {
            for (var x = r - 1; x >= -r; x--) yield return new(x, r);         // 南边一行，向西
        }
        for (var z = bottomRowOnLand ? r - 1 : bottom; z >= -r; z--) yield return new(-r, z);   // 西边一列，向北
        for (var x = -r + 1; x <= -1; x++) yield return new(x, -r);          // 北边一行的西半
    }

    /// <summary>
    /// 文件夹改了名：账本条目换个名字，地块不动——建筑原地换牌匾（设计稿 11.2 / 11.3）。
    /// 新名字在账本里已经有一条（它以前住过别处、后来空了）时，不抢那块地也不把旧条目挤掉：
    /// 回到它自己的老地块，旧名字那块留作空地，两边的空间记忆都不作废。<paramref name="from"/> 不在账本里时原样返回。
    /// </summary>
    public static PolisLedger Rename(PolisLedger ledger, string from, string to)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentException.ThrowIfNullOrEmpty(from);
        ArgumentException.ThrowIfNullOrEmpty(to);
        if (string.Equals(from, to, StringComparison.Ordinal)) return ledger;
        var source = ledger.Entries.FirstOrDefault(e => string.Equals(e.Key, from, StringComparison.Ordinal));
        if (source == null) return ledger;
        var targetExists = ledger.Entries.Any(e => string.Equals(e.Key, to, StringComparison.Ordinal));
        var entries = ledger.Entries
            .Select(e => !ReferenceEquals(e, source)
                ? (targetExists && string.Equals(e.Key, to, StringComparison.Ordinal) ? e with { Vacated = false } : e)
                : targetExists ? e with { Vacated = true } : e with { Key = to })
            .ToList();
        return new PolisLedger(PolisLedger.CurrentSchemaVersion, entries);
    }

    /// <summary>
    /// 用这一次扫描到的顶层文件夹更新账本。
    /// <list type="bullet">
    /// <item>账本里已有的文件夹回到原地；不在了的标成空地，地块保留（同名文件夹以后回来，还落回原地）。</item>
    /// <item>新文件夹按"最近修改在前、同时按名字"的次序，依次占生长次序里的下一块空地——常用的离广场近。</item>
    /// <item>还在的独立建筑达到上限后，新文件夹不再占地，进市集当摊位；已经有地的永远不会被挤进市集。</item>
    /// <item>账本本身损坏（同一文件夹两条、两条占同一块地）时只保留落户更早的那一条。</item>
    /// </list>
    /// </summary>
    public static PolisLayout Update(
        PolisLedger previous,
        IReadOnlyList<PolisBuilding> present,
        int maxIndependentBuildings = DefaultMaxIndependentBuildings)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(present);
        if (maxIndependentBuildings < 0) throw new ArgumentOutOfRangeException(nameof(maxIndependentBuildings));

        var presentKeys = new HashSet<string>(present.Select(b => b.Key), StringComparer.Ordinal);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var occupied = new HashSet<PolisPlot>();
        var entries = new List<PolisLedgerEntry>();
        foreach (var entry in previous.Entries.OrderBy(e => e.Order))
        {
            var plot = new PolisPlot(entry.X, entry.Z);
            // 两项都先查再登记：只登记名字而丢掉条目，这个文件夹就再也分不到地了
            if (keys.Contains(entry.Key) || occupied.Contains(plot)) continue;
            keys.Add(entry.Key);
            occupied.Add(plot);
            entries.Add(entry with { Vacated = !presentKeys.Contains(entry.Key) });
        }

        var standingCount = entries.Count(e => !e.Vacated);
        var capacity = Math.Max(0, maxIndependentBuildings - standingCount);
        var nextOrder = entries.Count == 0 ? 0 : entries.Max(e => e.Order) + 1;
        var stalls = new List<string>();
        using var freePlots = GrowthOrder().Where(p => !occupied.Contains(p)).GetEnumerator();

        foreach (var building in present
            .Where(b => !keys.Contains(b.Key))
            .OrderByDescending(b => b.LastModifiedUtc)
            .ThenBy(b => b.Key, StringComparer.Ordinal))
        {
            if (!keys.Add(building.Key)) continue;   // 同一次扫描里重名（不该发生），只算一次
            if (capacity == 0)
            {
                stalls.Add(building.Key);
                continue;
            }

            freePlots.MoveNext();
            var plot = freePlots.Current;
            entries.Add(new PolisLedgerEntry(building.Key, plot.X, plot.Z, nextOrder++, Vacated: false));
            capacity--;
        }

        var ordered = entries.OrderBy(e => e.Order).ToList();
        return new PolisLayout(
            new PolisLedger(PolisLedger.CurrentSchemaVersion, ordered),
            ordered.Where(e => !e.Vacated).ToList(),
            ordered.Where(e => e.Vacated).ToList(),
            stalls.OrderBy(k => k, StringComparer.Ordinal).ToList());
    }
}
