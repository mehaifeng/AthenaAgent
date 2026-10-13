using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Athena.UI.Services.GameMode;

/// <summary>一户人家：同一类事的几场会话（或同一个月里开的几场）住在一起。<see cref="Key"/> 稳定：取立户那一场会话的 Id。</summary>
public sealed record PolisHousehold(
    string Key,
    string FounderId,
    string Label,
    IReadOnlyList<string> Members,
    DateTimeOffset FoundedAt,
    DateTimeOffset LastActive);

/// <summary>
/// 户与民居（M-D，2026-10-13，方案的最后一个里程碑）。没有文件夹可归的居民（那场会话从没碰过城里的任何文件夹，神殿里的全局对话都是这样）
/// 不再住广场一圈、市集、神庙这些公共地方，而是聚成户、盖一座民居住进去。纯函数，<c>Athena.Archive.Tests</c> 的 <c>polis households</c> 覆盖。
///
/// 规则（每一条都有断言）：
/// 1. <b>按先后聚户</b>：居民按会话开始的时刻排，一位一位来——标题和某一户立户那位的标题足够像（分词的 Jaccard ≥ 0.3，分词与问话同一套），
///    就进最像的那一户；不像任何一户，就进同一个月立的、还有空的最近那一户；都没有就自己立一户。每户最多 6 人，最多 16 户（再多的仍住公共地方）。
///    只往后加不回头：新开一场会话只会进一户或者立新户，前面的人住哪户不变。
/// 2. <b>户的钥匙稳定</b>：<c>household:</c> 加立户那一场会话 Id 的前 8 个字母数字（同城撞了就再长一点）。立户那场会话被删了，这一户换下一位当家，
///    换了钥匙——旧民居那块地留作空地，和删掉一个文件夹一样。
/// 3. <b>民居的地在同一本账上，永不挪位</b>：户的条目和文件夹的条目在一本账本里（<see cref="PolisLayoutLedger"/>），按同一个生长次序占下一块空地；
///    文件夹那一遍（<see cref="PolisLayoutLedger.Update"/>）不动户的条目、也不把民居算进独立建筑的上限；解散的户留下空地。
/// 4. <b>民居只在推给页面的城里</b>：C# 自己的城（委托板、归属、改名推断、外部改动）只有文件夹的建筑；户由 <see cref="WithHouseholds"/> 合进推给页面的那一份。
/// </summary>
public static class PolisHouseholds
{
    public const string KeyPrefix = PolisLayoutLedger.HouseholdKeyPrefix;
    public const int MaxMembers = 6;
    public const int MaxHouseholds = 16;
    public const double SimilarityThreshold = 0.3;

    public static bool IsHouseholdKey(string? key) => PolisLayoutLedger.IsHouseholdEntry(key);

    /// <summary>
    /// 没有文件夹可归：那场会话没碰过城里的任何文件夹。按 <see cref="PolisCitizen.Folders"/> 判断，不按家在哪——
    /// "market" 既是公共地方之一、又是市集摊位文件夹的家，按家判断会把按种子分到市集的人当成摊主（测试踩过）。
    /// </summary>
    public static bool IsHomeless(PolisCitizen citizen)
    {
        ArgumentNullException.ThrowIfNull(citizen);
        return citizen.Folders.Count == 0;
    }

    private sealed class Builder
    {
        public required string Key { get; init; }
        public required PolisCitizen Founder { get; init; }
        public required IReadOnlySet<string> FounderTokens { get; init; }
        public required string Month { get; init; }
        public List<PolisCitizen> Members { get; } = new();
    }

    /// <summary>
    /// 规则 1、2：把没有文件夹可归的居民聚成户。<paramref name="labelOf"/> 给立户那一位起户名（"整理合同一家"，由视图模型按语言拼）。
    /// 返回按立户先后排的户；有文件夹可归的居民不在任何一户里。
    /// </summary>
    public static IReadOnlyList<PolisHousehold> Derive(IReadOnlyList<PolisCitizen> citizens, Func<PolisCitizen, string> labelOf)
    {
        ArgumentNullException.ThrowIfNull(citizens);
        ArgumentNullException.ThrowIfNull(labelOf);
        var households = new List<Builder>();
        var usedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in citizens.Where(IsHomeless).OrderBy(c => c.StartedAt).ThenBy(c => c.ConversationId, StringComparer.Ordinal))
        {
            // 默认标题（新会话……，没有称号）不比像不像：几十场"新会话"彼此一模一样，按像不像会全挤进几户，按月份落户才分得开
            var tokens = string.IsNullOrEmpty(c.Epithet) ? new HashSet<string>(StringComparer.Ordinal) : PolisCitizenAnswers.Tokens(c.Title);
            var month = c.StartedAt.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            Builder? best = null;
            var bestScore = 0.0;
            foreach (var h in households)
            {
                if (h.Members.Count >= MaxMembers) continue;
                var score = Jaccard(tokens, h.FounderTokens);
                if (score >= SimilarityThreshold && score > bestScore) { best = h; bestScore = score; }
            }
            best ??= households.LastOrDefault(h => h.Members.Count < MaxMembers && h.Month == month);
            if (best == null)
            {
                if (households.Count >= MaxHouseholds) continue;   // 户满了：这一位仍住公共地方
                best = new Builder { Key = KeyFor(c.ConversationId, usedKeys), Founder = c, FounderTokens = tokens, Month = month };
                households.Add(best);
            }
            best.Members.Add(c);
        }
        return households.Select(h => new PolisHousehold(
            h.Key,
            h.Founder.ConversationId,
            labelOf(h.Founder),
            h.Members.Select(m => m.ConversationId).ToList(),
            h.Founder.StartedAt,
            h.Members.Max(m => m.UpdatedAt))).ToList();
    }

    /// <summary>两组分词的 Jaccard：交集 / 并集；两边都空的算 0（默认标题"新会话"彼此不算像，按月份落户）。</summary>
    public static double Jaccard(IReadOnlySet<string> a, IReadOnlySet<string> b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.Count == 0 || b.Count == 0) return 0;
        var intersection = a.Count(b.Contains);
        return intersection / (double)(a.Count + b.Count - intersection);
    }

    /// <summary>规则 2：<c>household:</c> 加立户那场会话 Id 的前 8 个字母数字（小写）；同城撞了就再长四个，直到不撞。</summary>
    public static string KeyFor(string conversationId, ISet<string>? used = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        var clean = new StringBuilder();
        foreach (var ch in conversationId.ToLowerInvariant())
        {
            if (ch is (>= 'a' and <= 'z') or (>= '0' and <= '9')) clean.Append(ch);
        }
        // Id 里字母数字不够（不该发生）：拿它的种子补上，钥匙照样稳定
        if (clean.Length < 8) clean.Append(PolisCitizens.SeedOf(conversationId).ToString("x8", CultureInfo.InvariantCulture));
        for (var length = 8; ; length += 4)
        {
            var key = KeyPrefix + clean.ToString(0, Math.Min(length, clean.Length));
            if (used == null || used.Add(key) || length >= clean.Length) return key;
        }
    }

    /// <summary>规则 1：户里的居民住进自己的民居（<c>b:household:…</c>）；不在任何一户里的人，家照旧。</summary>
    public static IReadOnlyList<PolisCitizen> Rehome(IReadOnlyList<PolisCitizen> citizens, IReadOnlyList<PolisHousehold> households)
    {
        ArgumentNullException.ThrowIfNull(citizens);
        ArgumentNullException.ThrowIfNull(households);
        var homeOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var h in households) foreach (var member in h.Members) homeOf[member] = "b:" + h.Key;
        return citizens.Select(c => homeOf.TryGetValue(c.ConversationId, out var home) ? c with { Home = home } : c).ToList();
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

    /// <summary>两本账本是不是一样（户的条目变了才需要重新推城、写存档）。</summary>
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

    /// <summary>民居的体量按户里的人数：1–2 人一层、3–4 人两层、5–6 人三层（和文件夹的建筑同一套 1–4 的体量档）。</summary>
    public static int SizeClassFor(int members) => members <= 2 ? 1 : members <= 4 ? 2 : 3;

    /// <summary>
    /// 规则 4：把户的民居合进推给页面的那一份城——民居是 <see cref="PolisBuildingKind.House"/>，名牌是户名，体量按人数、新旧按户里最近动过的那场会话；
    /// 还住着人的户那块地不再算空地。账本里没有条目的户不画（下一次聚户写进账本之后再画）。
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
                    h.Key, PolisBuildingKind.House, h.Members.Count, 0, new Dictionary<string, int>(),
                    h.LastActive, PolisBuildings.StateFor(h.LastActive, now), SizeClassFor(h.Members.Count), Incomplete: false,
                    new PolisPlot(e.X, e.Z), e.Order)
                { Label = h.Label };
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
