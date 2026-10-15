using System;
using System.Collections.Generic;
using System.Linq;

namespace Athena.UI.Services.GameMode;

/// <summary>关系的五档（第三轮需求 1，2026-10-13）。</summary>
public enum PolisRelationBand
{
    /// <summary>厌恶（≤ −60）：转过身去，不开口，隔着门说两句就请你走。</summary>
    Hostile,
    /// <summary>冷淡（−59…−25）：只看你一眼；叫他出来他不出来。</summary>
    Cool,
    /// <summary>寻常（−24…24）。</summary>
    Neutral,
    /// <summary>友好（25…59）：挥手；隔着门说到头会出门来谈。</summary>
    Friendly,
    /// <summary>亲近（≥ 60）。</summary>
    Close
}

/// <summary>改变关系的事。分量与上限见 <see cref="PolisRelations"/> 的规则 3。</summary>
public enum PolisRelationEvent
{
    /// <summary>你和他（当面或隔着门）说了一句，他好好答了。</summary>
    Chat,
    /// <summary>你在旁边自己说话，他接了一句。</summary>
    Overheard,
    /// <summary>你敲门请他出来，他出来了。</summary>
    CalledOut,
    /// <summary>你敲门请他出来，他没出来（不加不减，记下来：之后 30 秒内再敲算"敲得太勤"）。</summary>
    Refused,
    /// <summary>隔着门把他说烦了，他请你走（三分钟里你再敲他不应）。</summary>
    SentAway,
    /// <summary>你敲了他的门（不加不减，记下来算"敲得太勤"）。</summary>
    Knock,
    /// <summary>敲得太勤。</summary>
    Pestered
}

/// <summary>
/// 关系里的一个人（第四轮，2026-10-15 起居民从建筑来）：城里的人（<c>n:…</c>）、职业居民（<c>resident:&lt;名字&gt;</c>）或者雅典娜（<c>athena</c>）。
/// 起点都从这些真实的数推（规则 2）：<see cref="Temperament"/> 是 <see cref="PolisTalk.PersonaFor"/> 那张脾气表里的位置（职业居民与雅典娜为 -1，
/// 用 <see cref="FixedBase"/>）；<see cref="Building"/> 是他住的地方（页面的站点名，<c>b:&lt;文件夹&gt;</c> 或 market），<see cref="PlotX"/> /
/// <see cref="PlotZ"/> 是那块地（摊位与职业居民没有）；<see cref="Trade"/> 是职业的键；<see cref="IdleDays"/> 是他那座楼多少天没人动过
/// （城的主人有没有照看他那条街；村舍、摊位为 null）。
/// </summary>
public sealed record PolisRelationPerson(
    string Id,
    uint Seed,
    int Temperament,
    string? Building,
    int? PlotX,
    int? PlotZ,
    string? Trade,
    PolisCitizenAge Age,
    int? IdleDays,
    int? FixedBase = null)
{
    public bool IsResident => Id.StartsWith(PolisIntents.ResidentSpeakerPrefix, StringComparison.Ordinal);
    public bool IsAthena => string.Equals(Id, PolisRelations.AthenaId, StringComparison.Ordinal);
}

/// <summary>一个人此刻怎么看你，以及由它推出的行为参数（规则 4），连同两条最主要的原因（页面本地化的键）。</summary>
public sealed record PolisRelationView(int Score, PolisRelationBand Band, int Patience, bool Opens, bool ComesOut, IReadOnlyList<string> Why);

/// <summary>账本里的一笔：哪件事、什么时候、改了多少（最近一小时的留着，算上限与"敲得太勤"）。</summary>
public sealed record PolisRelationMark(PolisRelationEvent Event, DateTimeOffset At, double Delta);

/// <summary>一个人对你的变化：累计的改变（存盘时的值）与它的时刻（之后按半衰期淡去），最近一小时的几笔。</summary>
public sealed record PolisPlayerTie(string Id, double Delta, DateTimeOffset At, IReadOnlyList<PolisRelationMark> Recent);

/// <summary>两个人之间的变化（有方向：<see cref="Ab"/> 是 A 对 B、<see cref="Ba"/> 是 B 对 A）。A 的 Id 排在前面（序数）。</summary>
public sealed record PolisPairTie(string A, string B, double Ab, double Ba, DateTimeOffset At);

/// <summary>关系的账本：只有事件带来的变化（起点不存，按真实的数现推）。</summary>
public sealed record PolisRelationLedger(IReadOnlyList<PolisPlayerTie> You, IReadOnlyList<PolisPairTie> Pairs)
{
    public static PolisRelationLedger Empty { get; } = new(Array.Empty<PolisPlayerTie>(), Array.Empty<PolisPairTie>());
}

/// <summary>
/// 人物关系（第三轮需求 1，2026-10-13，用户：NPC 对玩家、NPC 对 NPC，对一个人的好恶直接影响对话和行为）。纯函数，
/// <c>Athena.Archive.Tests</c> 的 <c>polis relations</c> 逐条断言。
///
/// 规则：
/// 1. 关系值 −100…100，五档（<see cref="BandOf"/>）：亲近 ≥ 60、友好 ≥ 25、寻常 > −25、冷淡 > −60、其余厌恶。
/// 2. <b>起点按真实的数推，不存</b>（第四轮起居民从建筑来）。居民对你：脾气的"暖度"（<see cref="Warmth"/>，热心肠 +20 … 爱发牢骚 −15）；
///    他那座楼两周内有人动过 +8（城的主人在照看他那条街），半年多没人动 −8；无业的人 −4；再加一点按种子的个人差异（±6）。
///    职业居民按人设各有一个起点（<see cref="ResidentBase"/>）。两人之间（有方向）：住同一处 +30、邻居（地块挨着）+8、同行 +6、
///    "缘分"（两人种子的对称哈希，−30…+30）、年纪差一档 −5、两种脾气合不合（<see cref="Compatibility"/>）、再加他自己暖度的一半。
///    对雅典娜（第四轮需求 8：不是所有 NPC 对雅典娜都是尊敬的）：敬神心（按种子 −35…+35）、脾气（多疑、爱发牢骚的不服，热心肠、沉稳的敬重）、
///    职业（乐师、陶工、织工偏敬重，无业的、商贩偏不服）、年纪（年长的敬重、年轻的不服）（<see cref="BaseToAthena"/>）；职业居民各有一个起点
///    （<see cref="ResidentAthenaBase"/>）。雅典娜对谁都和气（+35 加对方暖度的三成）。
/// 3. <b>只有事件带来的变化记账</b>（<see cref="PolisRelationLedger"/>，存全局的 relations.json）：聊一句 +2（每人每小时最多 +6）、
///    接了你的话 +1（每小时最多 +2）、请出来了 +1（每小时最多 +2）、说烦了 −4、敲得太勤 −3（每小时最多 −6）；对你的累计变化夹在 ±60 之内。
///    两位居民闲聊一次彼此 +1，两人之间因此累计最多 +15。变化随时间淡去（半衰期 <see cref="HalfLife"/>）。
/// 4. 行为参数：隔着门的耐心 1–5 轮（关系越好越耐心，慢性子、沉稳、热心肠多一轮，急性子、爱发牢骚、有点多疑少一轮）；
///    叫他出来——寻常及以上答应（<see cref="PolisRelationView.Opens"/>）；隔着门说到头——友好及以上出门来谈（<see cref="PolisRelationView.ComesOut"/>），
///    其余请你走。
/// 5. 敲得太勤：两分钟里第三次敲同一个人的门，或者他回绝、打发你之后 30 秒内又敲；打发你之后三分钟里他不应门（<see cref="Ignoring"/>）。
/// </summary>
public static class PolisRelations
{
    public const int Min = -100;
    public const int Max = 100;
    public const int CloseAt = 60;
    public const int FriendlyAt = 25;
    public const int CoolBelow = -25;
    public const int HostileAt = -60;
    public const double MaxPlayerDelta = 60;
    public const double MaxAmbientPairDelta = 15;
    public static readonly TimeSpan HalfLife = TimeSpan.FromDays(30);
    public static readonly TimeSpan PesterWindow = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan RefusalWindow = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan IgnoreAfterSentAway = TimeSpan.FromMinutes(3);
    /// <summary>总览里每人列几个朋友、几个不和的人。</summary>
    public const int MaxTies = 3;

    /// <summary>规则 2：十二种脾气的"暖度"（与 <see cref="PolisTalk"/> 的脾气表同一个次序）。</summary>
    public static IReadOnlyList<int> Warmth { get; } = new[] { 12, 0, -6, 8, 4, 20, -12, 14, 4, -6, -15, 8 };

    /// <summary>规则 4：脾气让耐心多一轮或少一轮。</summary>
    private static readonly int[] PatienceAdjust = { 0, 0, 0, 0, 1, 1, -1, 0, 1, -1, -1, 0 };

    /// <summary>健谈（0–1）：几个人一起接你的话时，谁先开口（页面的 pickResponders）。</summary>
    public static IReadOnlyList<double> Talkativeness { get; } = new[] { 0.8, 0.2, 0.5, 0.9, 0.5, 0.7, 0.4, 0.7, 0.35, 0.6, 0.7, 0.8 };

    /// <summary>职业居民对你的起点（按各自的人设：女祭司、图书管理员温和，账房挑剔）。</summary>
    public static int ResidentBase(PolisResidentKind kind) => kind switch
    {
        PolisResidentKind.Priestess => 28,
        PolisResidentKind.Librarian => 22,
        PolisResidentKind.Herald => 18,
        PolisResidentKind.Scribe => 16,
        PolisResidentKind.Sailor => 12,
        PolisResidentKind.Painter => 12,
        PolisResidentKind.Surveyor => 10,
        PolisResidentKind.Smith => 6,
        PolisResidentKind.Artisan => 6,
        PolisResidentKind.Accountant => -4,
        _ => 0,
    };

    /// <summary>雅典娜在关系里的 Id（和她闲聊、她怎么看谁、谁怎么看她）。</summary>
    public const string AthenaId = "athena";

    /// <summary>雅典娜这个人（没有脾气、没有楼）。</summary>
    public static PolisRelationPerson AthenaPerson { get; } = new(AthenaId, PolisCitizens.SeedOf(AthenaId), -1, null, null, null, null, PolisCitizenAge.Adult, null, 40);

    /// <summary>职业居民对雅典娜的起点（女祭司最敬她，账房只认账）。</summary>
    public static int ResidentAthenaBase(PolisResidentKind kind) => kind switch
    {
        PolisResidentKind.Priestess => 70,
        PolisResidentKind.Herald => 45,
        PolisResidentKind.Librarian => 40,
        PolisResidentKind.Scribe => 35,
        PolisResidentKind.Painter => 30,
        PolisResidentKind.Sailor => 25,
        PolisResidentKind.Surveyor => 25,
        PolisResidentKind.Artisan => 20,
        PolisResidentKind.Smith => 15,
        PolisResidentKind.Accountant => 5,
        _ => 20,
    };

    /// <summary>职业居民在关系里的那个人（种子按名字取，脾气没有，用起点）。</summary>
    public static PolisRelationPerson ResidentPerson(PolisResidentKind kind)
    {
        var slug = PolisResidents.Slug(kind);
        return new PolisRelationPerson(PolisIntents.ResidentSpeakerPrefix + slug, PolisCitizens.SeedOf("resident:" + slug), -1, null, null, null, null,
            PolisResidents.AgeOf(kind), null, ResidentBase(kind));
    }

    /// <summary>城里的一个人在关系里的那个人。<paramref name="dwelling"/> 是他的住所（给地块和那座楼多久没人动过）。</summary>
    public static PolisRelationPerson CitizenPerson(PolisCitizen citizen, PolisDwelling? dwelling, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(citizen);
        var folder = dwelling != null && !dwelling.Stall && !dwelling.Cottage;
        int? idle = folder ? Math.Max(0, (int)Math.Floor((now - dwelling!.LastModified).TotalDays)) : null;
        return new PolisRelationPerson(citizen.Id, citizen.Seed, PolisTalk.TemperamentIndex(citizen.Seed), citizen.Home,
            dwelling == null || dwelling.Stall ? null : dwelling.PlotX, dwelling == null || dwelling.Stall ? null : dwelling.PlotZ,
            citizen.Trade, citizen.Age, idle);
    }

    public static PolisRelationBand BandOf(int score) => score >= CloseAt ? PolisRelationBand.Close
        : score >= FriendlyAt ? PolisRelationBand.Friendly
        : score > CoolBelow ? PolisRelationBand.Neutral
        : score > HostileAt ? PolisRelationBand.Cool
        : PolisRelationBand.Hostile;

    /// <summary>页面与提示词用的档名（小写英文）。</summary>
    public static string BandKey(PolisRelationBand band) => band switch
    {
        PolisRelationBand.Close => "close",
        PolisRelationBand.Friendly => "friendly",
        PolisRelationBand.Neutral => "neutral",
        PolisRelationBand.Cool => "cool",
        _ => "hostile",
    };

    /// <summary>规则 3：一笔变化到 <paramref name="now"/> 还剩多少（半衰期淡去；将来的时刻不放大）。</summary>
    public static double Decayed(double delta, DateTimeOffset at, DateTimeOffset now)
    {
        var days = Math.Max(0, (now - at).TotalDays);
        return delta * Math.Pow(0.5, days / HalfLife.TotalDays);
    }

    private static int Clamp(double v) => (int)Math.Round(Math.Clamp(v, Min, Max), MidpointRounding.AwayFromZero);

    private static int WarmthOf(PolisRelationPerson p) => p.Temperament >= 0 && p.Temperament < Warmth.Count ? Warmth[p.Temperament] : 0;

    /// <summary>规则 2：他对你的起点，连同各项的分量（"原因"按分量挑）。</summary>
    public static (int Score, IReadOnlyList<(string Why, int Weight)> Parts) BaseToPlayer(PolisRelationPerson person)
    {
        ArgumentNullException.ThrowIfNull(person);
        var parts = new List<(string, int)>();
        if (person.FixedBase is int fixedBase)
        {
            parts.Add(("nature", fixedBase));
        }
        else
        {
            var warmth = WarmthOf(person);
            if (warmth != 0) parts.Add((warmth > 0 ? "warm" : "prickly", warmth));
            // 城的主人照不照看他那条街：他那座楼两周内有人动过，街上热闹；半年多没人动，冷冷清清
            if (person.IdleDays is int idle)
            {
                if (idle <= 14) parts.Add(("busy", 8));
                else if (idle >= 180) parts.Add(("neglected", -8));
            }
            if (string.Equals(person.Trade, PolisTrades.Idle, StringComparison.Ordinal)) parts.Add(("idle", -4));
            // 个人差异（±6，按种子）：同样的会话、同样的脾气，人和人也不全一样
            parts.Add(("self", (int)(person.Seed % 13) - 6));
        }
        return (Clamp(parts.Sum(p => p.Item2)), parts);
    }

    /// <summary>"缘分"：两人种子的对称哈希，−30…+30（谁先谁后都一样）。</summary>
    public static int Chemistry(uint a, uint b)
    {
        var lo = Math.Min(a, b);
        var hi = Math.Max(a, b);
        var h = unchecked((lo * 2654435761u) ^ (hi * 2246822519u) ^ 0x9E3779B9u);
        h ^= h >> 15;
        h = unchecked(h * 2246822519u);
        h ^= h >> 13;
        return (int)(h % 61) - 30;
    }

    /// <summary>两种脾气合不合（对称的一小张表，其余为 0；同一种脾气 +4）。</summary>
    public static int Compatibility(int a, int b)
    {
        if (a < 0 || b < 0) return 0;
        if (a == b) return 4;
        static bool Is(int x, int y, int p, int q) => (x == p && y == q) || (x == q && y == p);
        if (Is(a, b, 9, 4)) return -8;      // 急性子 × 慢性子
        if (Is(a, b, 10, 0) || Is(a, b, 10, 7)) return -6;   // 爱发牢骚 × 爽朗 / 乐天
        if (Is(a, b, 3, 1)) return -5;      // 爱打趣 × 腼腆
        if (Is(a, b, 2, 3)) return -4;      // 较真 × 爱打趣
        if (Is(a, b, 6, 5)) return -3;      // 有点多疑 × 热心肠
        if (Is(a, b, 5, 1) || Is(a, b, 8, 4) || Is(a, b, 11, 3)) return 5;   // 热心肠 × 腼腆、沉稳 × 慢性子、好奇 × 爱打趣
        return 0;
    }

    private static int AgeRank(PolisCitizenAge age) => age switch { PolisCitizenAge.Young => 0, PolisCitizenAge.Adult => 1, _ => 2 };

    /// <summary>规则 2：<paramref name="a"/> 对 <paramref name="b"/> 的起点（任一方是雅典娜时见 <see cref="BaseToAthena"/>）。</summary>
    public static int BasePair(PolisRelationPerson a, PolisRelationPerson b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (b.IsAthena) return BaseToAthena(a).Score;
        if (a.IsAthena) return Clamp(35 + WarmthOf(b) * 0.3);
        double v = Chemistry(a.Seed, b.Seed);
        var sameHome = a.Building != null && string.Equals(a.Building, b.Building, StringComparison.Ordinal);
        if (sameHome) v += 30;
        else if (a.PlotX is int ax && a.PlotZ is int az && b.PlotX is int bx && b.PlotZ is int bz && Math.Max(Math.Abs(ax - bx), Math.Abs(az - bz)) <= 1) v += 8;
        if (a.Trade != null && string.Equals(a.Trade, b.Trade, StringComparison.Ordinal) && a.Trade != PolisTrades.Idle) v += 6;
        v -= 5 * Math.Abs(AgeRank(a.Age) - AgeRank(b.Age));
        v += Compatibility(a.Temperament, b.Temperament);
        v += WarmthOf(a) * 0.5;
        if (a.FixedBase is int fa) v += fa * 0.25;   // 职业居民待人的样子（温和的女祭司对谁都好一点）
        return Clamp(v);
    }

    /// <summary>
    /// 规则 2：他对雅典娜的起点，连同各项的分量（"原因"按分量挑）。职业居民按人设（<see cref="ResidentAthenaBase"/>）；
    /// 城里的人：敬神心（按种子，−35…+35）、脾气、职业、年纪。
    /// </summary>
    public static (int Score, IReadOnlyList<(string Why, int Weight)> Parts) BaseToAthena(PolisRelationPerson person)
    {
        ArgumentNullException.ThrowIfNull(person);
        var parts = new List<(string, int)>();
        if (person.IsResident && PolisResidents.TryParse(person.Id[PolisIntents.ResidentSpeakerPrefix.Length..], out var kind))
        {
            parts.Add(("nature", ResidentAthenaBase(kind)));
            return (Clamp(parts.Sum(p => p.Item2)), parts);
        }
        var piety = (int)((person.Seed >> 23) % 71) - 35;
        parts.Add((piety >= 0 ? "pious" : "doubting", piety));
        var temper = person.Temperament switch
        {
            6 => -12,   // 有点多疑
            10 => -10,  // 爱发牢骚
            9 => -5,    // 急性子
            3 => -3,    // 爱打趣
            5 => 8,     // 热心肠
            8 => 5,     // 沉稳
            11 => 4,    // 好奇
            _ => 0,
        };
        if (temper != 0) parts.Add((temper > 0 ? "reverent" : "defiant", temper));
        var trade = person.Trade switch
        {
            "musician" => 8,
            "potter" => 6,
            "weaver" => 4,
            PolisTrades.Merchant => -4,
            PolisTrades.Idle => -10,
            _ => 0,
        };
        if (trade != 0) parts.Add(("trade", trade));
        var age = person.Age switch { PolisCitizenAge.Old => 8, PolisCitizenAge.Young => -4, _ => 0 };
        if (age != 0) parts.Add(("age", age));
        return (Clamp(parts.Sum(p => p.Item2)), parts);
    }

    /// <summary>他此刻怎么看雅典娜：起点加上和她闲聊攒下的（与两人之间同一本账）。返回分数、档与两条最主要的原因。</summary>
    public static (int Score, PolisRelationBand Band, IReadOnlyList<string> Why) ToAthena(PolisRelationPerson person, PolisRelationLedger ledger, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(person);
        var (score, _) = Between(person, AthenaPerson, ledger, now);
        var why = BaseToAthena(person).Parts
            .OrderByDescending(p => Math.Abs(p.Weight)).ThenBy(p => p.Why, StringComparer.Ordinal)
            .Take(2).Select(p => p.Why).ToList();
        return (score, BandOf(score), why);
    }

    private static PolisPlayerTie? Find(PolisRelationLedger ledger, string id)
        => ledger.You.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.Ordinal));

    /// <summary>账本里他对你的变化，到此刻还剩多少。</summary>
    public static double PlayerDelta(PolisRelationLedger ledger, string id, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        var tie = Find(ledger, id);
        return tie == null ? 0 : Decayed(tie.Delta, tie.At, now);
    }

    /// <summary>规则 1–4：他此刻怎么看你。</summary>
    public static PolisRelationView ToPlayer(PolisRelationPerson person, PolisRelationLedger ledger, DateTimeOffset now)
    {
        var (score0, parts) = BaseToPlayer(person);
        var delta = PlayerDelta(ledger, person.Id, now);
        var score = Clamp(score0 + delta);
        var band = BandOf(score);
        var why = parts.Where(p => p.Why != "self").Select(p => (p.Why, Weight: (double)p.Weight)).ToList();
        if (delta >= 1.5) why.Add(("chats", delta));
        if (delta <= -1.5) why.Add(("annoyed", delta));
        var top = why.OrderByDescending(w => Math.Abs(w.Weight)).ThenBy(w => w.Why, StringComparer.Ordinal).Take(2).Select(w => w.Why).ToList();
        var adjust = person.Temperament >= 0 && person.Temperament < PatienceAdjust.Length ? PatienceAdjust[person.Temperament] : 0;
        var patience = Math.Clamp(1 + (int)Math.Round((score + 100) / 50.0, MidpointRounding.AwayFromZero) + adjust, 1, 5);
        return new PolisRelationView(score, band, patience, Opens: score > CoolBelow, ComesOut: score >= FriendlyAt, top);
    }

    /// <summary>两人之间此刻的关系（a 对 b、b 对 a）。</summary>
    public static (int Ab, int Ba) Between(PolisRelationPerson a, PolisRelationPerson b, PolisRelationLedger ledger, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        var (first, second, swapped) = string.CompareOrdinal(a.Id, b.Id) <= 0 ? (a.Id, b.Id, false) : (b.Id, a.Id, true);
        var tie = ledger.Pairs.FirstOrDefault(p => p.A == first && p.B == second);
        double ab = 0, ba = 0;
        if (tie != null)
        {
            var x = Decayed(tie.Ab, tie.At, now);
            var y = Decayed(tie.Ba, tie.At, now);
            (ab, ba) = swapped ? (y, x) : (x, y);
        }
        return (Clamp(BasePair(a, b) + ab), Clamp(BasePair(b, a) + ba));
    }

    /// <summary>规则 3：每件事的分量。</summary>
    public static double DeltaOf(PolisRelationEvent e) => e switch
    {
        PolisRelationEvent.Chat => 2,
        PolisRelationEvent.Overheard => 1,
        PolisRelationEvent.CalledOut => 1,
        PolisRelationEvent.SentAway => -4,
        PolisRelationEvent.Pestered => -3,
        _ => 0,
    };

    /// <summary>规则 3：每件事每人每小时最多改多少（绝对值；0 是不限）。</summary>
    public static double HourlyCap(PolisRelationEvent e) => e switch
    {
        PolisRelationEvent.Chat => 6,
        PolisRelationEvent.Overheard => 2,
        PolisRelationEvent.CalledOut => 2,
        PolisRelationEvent.Pestered => 6,
        _ => 0,
    };

    /// <summary>
    /// 规则 3：记一笔（他对你）。返回新的账本与这一笔实际改了多少（到了每小时的上限、或者累计夹到 ±60，就少改或不改）。
    /// 最近一小时以外的几笔顺手清掉。
    /// </summary>
    public static (PolisRelationLedger Ledger, double Applied) Apply(PolisRelationLedger ledger, string id, PolisRelationEvent e, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentException.ThrowIfNullOrEmpty(id);
        var tie = Find(ledger, id);
        var recent = (tie?.Recent ?? Array.Empty<PolisRelationMark>()).Where(m => now - m.At <= TimeSpan.FromHours(1)).ToList();
        var current = tie == null ? 0 : Decayed(tie.Delta, tie.At, now);
        var want = DeltaOf(e);
        var cap = HourlyCap(e);
        if (cap > 0 && want != 0)
        {
            var used = Math.Abs(recent.Where(m => m.Event == e).Sum(m => m.Delta));
            var room = Math.Max(0, cap - used);
            want = Math.Sign(want) * Math.Min(Math.Abs(want), room);
        }
        // 夹到 ±60：先夹这一笔再加（不在加完之后相减——淡去的零头会让"+1"变成 1.0000000000000009）
        var applied = Math.Clamp(want, -MaxPlayerDelta - current, MaxPlayerDelta - current);
        var next = current + applied;
        recent.Add(new PolisRelationMark(e, now, applied));
        var updated = new PolisPlayerTie(id, next, now, recent);
        var you = ledger.You.Where(t => !string.Equals(t.Id, id, StringComparison.Ordinal)).Append(updated).ToList();
        return (ledger with { You = you }, applied);
    }

    /// <summary>规则 3：两位居民闲聊了一次，彼此 +1（两人之间因闲聊累计最多 +15）。</summary>
    public static PolisRelationLedger ApplyAmbient(PolisRelationLedger ledger, string a, string b, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        if (string.Equals(a, b, StringComparison.Ordinal)) return ledger;
        var (first, second) = string.CompareOrdinal(a, b) <= 0 ? (a, b) : (b, a);
        var tie = ledger.Pairs.FirstOrDefault(p => p.A == first && p.B == second);
        var ab = tie == null ? 0 : Decayed(tie.Ab, tie.At, now);
        var ba = tie == null ? 0 : Decayed(tie.Ba, tie.At, now);
        var updated = new PolisPairTie(first, second, Math.Min(MaxAmbientPairDelta, ab + 1), Math.Min(MaxAmbientPairDelta, ba + 1), now);
        var pairs = ledger.Pairs.Where(p => !(p.A == first && p.B == second)).Append(updated).ToList();
        return ledger with { Pairs = pairs };
    }

    /// <summary>规则 5：这一下敲门算不算敲得太勤（在记下这一次之前问）。</summary>
    public static bool IsPestering(PolisRelationLedger ledger, string id, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        var recent = Find(ledger, id)?.Recent ?? Array.Empty<PolisRelationMark>();
        var knocks = recent.Count(m => m.Event == PolisRelationEvent.Knock && now - m.At <= PesterWindow);
        var snubbed = recent.Any(m => m.Event is PolisRelationEvent.Refused or PolisRelationEvent.SentAway && now - m.At <= RefusalWindow);
        return knocks >= 2 || snubbed;
    }

    /// <summary>规则 5：他打发了你，三分钟里不应门。</summary>
    public static bool Ignoring(PolisRelationLedger ledger, string id, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        var recent = Find(ledger, id)?.Recent ?? Array.Empty<PolisRelationMark>();
        return recent.Any(m => m.Event == PolisRelationEvent.SentAway && now - m.At <= IgnoreAfterSentAway);
    }

    /// <summary>
    /// 存盘前收拾：<paramref name="keep"/> 说不要了的人（拆掉的楼里的人、删掉的工作区的城、第四轮之前按会话 Id 记的旧账）清掉；
    /// 淡到不足半分、最近一小时也没事的清掉；每人最近的几笔只留一小时内的。
    /// </summary>
    public static PolisRelationLedger Prune(PolisRelationLedger ledger, Func<string, bool> keep, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(keep);
        var you = ledger.You
            .Where(t => keep(t.Id))
            .Select(t => t with { Recent = t.Recent.Where(m => now - m.At <= TimeSpan.FromHours(1)).ToList() })
            .Where(t => Math.Abs(Decayed(t.Delta, t.At, now)) >= 0.5 || t.Recent.Count > 0)
            .ToList();
        var pairs = ledger.Pairs
            .Where(p => keep(p.A) && keep(p.B))
            .Where(p => Math.Abs(Decayed(p.Ab, p.At, now)) >= 0.5 || Math.Abs(Decayed(p.Ba, p.At, now)) >= 0.5)
            .ToList();
        return new PolisRelationLedger(you, pairs);
    }

    /// <summary>
    /// 第四轮的收拾规则：雅典娜、十位职业居民留着；城里的人（<c>n:&lt;城&gt;:…</c>）——属于此刻这座城的，只留 <paramref name="cityPeople"/> 里有的；
    /// 属于别的城的，那座城还在（<paramref name="cityTags"/>）就留着（这座城看不到那边的人，不能替它判断谁搬走了）；其余（旧的会话 Id、删掉的工作区）不留。
    /// </summary>
    public static Func<string, bool> KeepRule(string currentCityTag, IReadOnlySet<string> cityPeople, IReadOnlySet<string> cityTags)
    {
        ArgumentNullException.ThrowIfNull(cityPeople);
        ArgumentNullException.ThrowIfNull(cityTags);
        return id =>
        {
            if (string.Equals(id, AthenaId, StringComparison.Ordinal)) return true;
            if (id.StartsWith(PolisIntents.ResidentSpeakerPrefix, StringComparison.Ordinal))
                return PolisResidents.TryParse(id[PolisIntents.ResidentSpeakerPrefix.Length..], out _);
            var tag = PolisCitizens.CityTagOf(id);
            if (tag == null) return false;
            return string.Equals(tag, currentCityTag, StringComparison.Ordinal) ? cityPeople.Contains(id) : cityTags.Contains(tag);
        };
    }

    /// <summary>
    /// 推给页面的那一份（规则见类注释）：<c>you</c> 是每个人怎么看你（含耐心、答不答应出来、出不出门、原因、健谈）；
    /// <c>pairs</c> 是 <paramref name="street"/> 里的人两两之间（行为用：闲聊挑人、几个人接话）；<c>ties</c> 是每个人最好的几位与最不和的几位（总览用）。
    /// 只有 Id、数与键，没有任何消息正文。
    /// </summary>
    public static object Payload(IReadOnlyList<PolisRelationPerson> people, IReadOnlySet<string> street, PolisRelationLedger ledger, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(people);
        ArgumentNullException.ThrowIfNull(street);
        var you = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var p in people)
        {
            if (p.IsAthena) continue;
            var v = ToPlayer(p, ledger, now);
            you[p.Id] = new
            {
                score = v.Score,
                band = BandKey(v.Band),
                patience = v.Patience,
                opens = v.Opens,
                comesOut = v.ComesOut,
                why = v.Why,
                talk = p.Temperament >= 0 && p.Temperament < Talkativeness.Count ? Talkativeness[p.Temperament] : 0.5,
            };
        }
        var pairs = new List<object>();
        var ties = new Dictionary<string, (List<(string Id, int Score)> All, int Dummy)>(StringComparer.Ordinal);
        foreach (var p in people) ties[p.Id] = (new List<(string, int)>(), 0);
        for (var i = 0; i < people.Count; i++)
        {
            for (var j = i + 1; j < people.Count; j++)
            {
                var (ab, ba) = Between(people[i], people[j], ledger, now);
                ties[people[i].Id].All.Add((people[j].Id, ab));
                ties[people[j].Id].All.Add((people[i].Id, ba));
                if (street.Contains(people[i].Id) && street.Contains(people[j].Id)) pairs.Add(new object[] { people[i].Id, people[j].Id, ab, ba });
            }
        }
        var tieView = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var (id, (all, _)) in ties)
        {
            var friends = all.Where(t => t.Score >= FriendlyAt).OrderByDescending(t => t.Score).ThenBy(t => t.Id, StringComparer.Ordinal).Take(MaxTies)
                .Select(t => new object[] { t.Id, t.Score }).ToList();
            var rivals = all.Where(t => t.Score <= CoolBelow).OrderBy(t => t.Score).ThenBy(t => t.Id, StringComparer.Ordinal).Take(MaxTies)
                .Select(t => new object[] { t.Id, t.Score }).ToList();
            tieView[id] = new { friends, rivals };
        }
        // 第四轮需求 8：每个人怎么看雅典娜（分数、档、两条原因）——总览里多一行，闲聊挑雅典娜时按它
        var athena = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var p in people)
        {
            if (p.IsAthena) continue;
            var (score, band, why) = ToAthena(p, ledger, now);
            athena[p.Id] = new { score, band = BandKey(band), why };
        }
        return new { you, pairs, ties = tieView, athena };
    }
}
