using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Athena.UI.Services.GameMode;

/// <summary>一位居民多大：年轻、壮年、年长（按种子：三成年轻、五成壮年、两成年长）。</summary>
public enum PolisCitizenAge
{
    Young,
    Adult,
    Old
}

/// <summary>
/// 一处住所（第四轮，2026-10-15）：一座文件夹的建筑、市集上合并成摊位的一个文件夹（<see cref="Stall"/>，家在市集），
/// 或者一座村舍（<see cref="Cottage"/>，<c>household:</c> 开头的键——神殿岛和住所太少的城补上的）。
/// <see cref="PlotX"/> / <see cref="PlotZ"/> 是账本里的地块（永不挪位）：人的身份按它算，所以文件夹改名人不变。
/// </summary>
public sealed record PolisDwelling(
    string Key,
    PolisBuildingKind Kind,
    int SizeClass,
    PolisBuildingState State,
    int PlotX,
    int PlotZ,
    int Order,
    DateTimeOffset LastModified,
    bool Stall = false,
    bool Cottage = false)
{
    /// <summary>页面的站点名：摊位在市集，其余是 <c>b:&lt;键&gt;</c>。</summary>
    public string Home => Stall ? "market" : "b:" + Key;
}

/// <summary>
/// 城里的一个人（第四轮，2026-10-15，用户：NPC 不再和会话绑定——会话少的时候城太空，像鬼城）。他住在一处住所里
/// （<see cref="Dwelling"/>，<see cref="Home"/> 是页面的站点名），是那里的第 <see cref="Index"/> 位；长相、男女、年纪、职业、脾气都从
/// <see cref="Seed"/> 来，名字在同一座城里不重（<see cref="NameIndex"/> / <see cref="NameRound"/>，<see cref="PolisCitizens.AssignNames"/>）。
/// <see cref="OnStreet"/>：白天在街上（最多 <see cref="PolisCitizens.StreetCap"/> 位），其余的在屋里、敲门叫得出来；
/// <see cref="NightOwl"/>：夜里还出门的少数几位。没有任何会话的东西。
/// </summary>
public sealed record PolisCitizen(
    string Id,
    uint Seed,
    bool Female,
    PolisCitizenAge Age,
    string Trade,
    string Home,
    string Dwelling,
    int Index,
    int DwellingOrder,
    bool Cottage,
    bool OnStreet,
    bool NightOwl,
    int NameIndex = 0,
    int NameRound = 0);

/// <summary>这座城的人口：一共几位，白天街上几位，屋里几位（街上满了）。</summary>
public sealed record PolisCensus(int Total, int OnStreet, int Indoors);

/// <summary>
/// 城里的人从建筑来（第四轮，2026-10-15，用户：城仍和工作区绑定；第一次进城加载的那一刻 NPC 就定下来，每座建筑分配 1–4 人，
/// 人数不设上限，街上的人数仍按 32 的上限）。纯函数，<c>Athena.Archive.Tests</c> 的 <c>polis citizens</c> 覆盖。
///
/// 规则（每一条都有断言）：
/// 1. <b>每处住所 1–4 人</b>（<see cref="HeadCount"/>）：按住所的种子与体量——体量 1 住 1–2 人、2 住 2–3 人、3 与 4 住 3–4 人；
///    市集的摊位与废墟各 1 人。
/// 2. <b>身份稳定</b>：Id 是 <c>n:&lt;城的 8 位哈希&gt;:&lt;人的 12 位哈希&gt;</c>，按城的键（工作区 Id 或 sanctuary）、地块坐标
///    （摊位按"市集 + 文件夹名"）和序号算——地块永不挪位，文件夹改名人还是那个人；种子是同一份哈希的头四个字节（小端），最低位是男女
///    （页面的 art/citizens.mjs 用同一条）。算不存：同一座城、同样的建筑，永远是同一群人——"第一次进城就定下来"靠的是确定性，不是存档。
/// 3. <b>名字</b>：完整的希腊名，同城不重（<see cref="AssignNames"/>，先来后到按住所的生长次序与序号）。
/// 4. <b>上街</b>：最多 <see cref="StreetCap"/> 位，按住所最近动过的先后轮流取——每处先出一位，再轮第二位，免得一座楼的人占满一条街。
/// 5. <b>夜游</b>：街上的人里按种子约八分之一，按上街的次序最多 <see cref="MaxNightOwls"/> 位；夜里只有他们在外面（页面）。
/// 6. <b>田和牧场有人</b>（2026-10-15，用户：保底规则）：城北的田与牧场每座城都有，职业却是按种子抽的——神殿岛十来个人，约五分之一的城
///    一个牧人都没有。所以街上没有农夫（或牧人）时，从街上挑一位改行（<see cref="GuaranteedTrades"/>，<see cref="GuaranteeTrades"/>）：
///    先挑无业的，没有就挑街上人数最多的那一行；同一行里按 Id 的序数取第一位，同一座城每次进来都是同一个人。市集的摊主不动（摊主一律是商贩），
///    已经是农夫或牧人的不动。城变了（多盖一座楼、上街的人换了），改行的那位可能跟着换。
/// </summary>
public static class PolisCitizens
{
    /// <summary>规则 6：街上至少各有一位的职业（城北的田与牧场每座城都有）。</summary>
    public static IReadOnlyList<string> GuaranteedTrades { get; } = new[] { "farmer", "herder" };

    /// <summary>街上同时露面的居民上限（用户定的 32）。人口不设上限，上限只在街上。</summary>
    public const int StreetCap = 32;
    public const int MaxPerDwelling = 4;
    /// <summary>夜里还出门的人：约八分之一，每座城最多四位。</summary>
    public const int NightOwlOneIn = 8;
    public const int MaxNightOwls = 4;
    /// <summary>希腊名表男女各多少个（<c>Polis.Citizen.NamesMale</c> / <c>NamesFemale</c>，中英两份一一对应）。</summary>
    public const int NamesPerSex = 120;
    /// <summary>城里一个人的 Id 的开头（职业居民是 <c>resident:</c>，雅典娜是 <c>athena</c>）。</summary>
    public const string IdPrefix = "n:";

    /// <summary>一段文字的 SHA-256 前四个字节（小端）。</summary>
    public static uint SeedOf(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(text), hash);
        return BinaryPrimitives.ReadUInt32LittleEndian(hash);
    }

    /// <summary>男女由种子的最低位定（页面的 citizens.mjs 用同一条规则，名字和长相对得上）。</summary>
    public static bool IsFemale(uint seed) => (seed & 1) == 1;

    public static bool IsCitizenId(string? id) => id != null && id.StartsWith(IdPrefix, StringComparison.Ordinal) && id.Length > IdPrefix.Length;

    /// <summary>城的 8 位哈希（Id 的第二段）：城的键是工作区 Id，神殿岛是 sanctuary。</summary>
    public static string CityTag(string cityKey)
    {
        ArgumentNullException.ThrowIfNull(cityKey);
        return SeedOf("polis-city\n" + cityKey).ToString("x8", CultureInfo.InvariantCulture);
    }

    /// <summary>一个人的 Id 属于哪座城（城的 8 位哈希）；不是城里的人返回 null。</summary>
    public static string? CityTagOf(string? id)
    {
        if (!IsCitizenId(id)) return null;
        var rest = id![IdPrefix.Length..];
        var colon = rest.IndexOf(':');
        return colon == 8 ? rest[..8] : null;
    }

    /// <summary>住所在城里的位置（人的身份按它算）：摊位按"市集 + 文件夹名"，其余按地块坐标。</summary>
    public static string PlaceKey(PolisDwelling dwelling)
    {
        ArgumentNullException.ThrowIfNull(dwelling);
        return dwelling.Stall
            ? "market/" + dwelling.Key
            : string.Create(CultureInfo.InvariantCulture, $"plot/{dwelling.PlotX},{dwelling.PlotZ}");
    }

    /// <summary>规则 1：这处住所住几个人。</summary>
    public static int HeadCount(string cityKey, PolisDwelling dwelling)
    {
        ArgumentNullException.ThrowIfNull(dwelling);
        if (dwelling.Stall || dwelling.State == PolisBuildingState.Ruin) return 1;
        var seed = SeedOf("polis-dwelling\n" + cityKey + "\n" + PlaceKey(dwelling));
        var (lo, hi) = Math.Clamp(dwelling.SizeClass, 1, 4) switch
        {
            1 => (1, 2),
            2 => (2, 3),
            _ => (3, 4),
        };
        return lo + (int)((seed >> 5) % (uint)(hi - lo + 1));
    }

    private static (string Id, uint Seed) Identity(string cityKey, string tag, PolisDwelling dwelling, int index)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"polis-person\n{cityKey}\n{PlaceKey(dwelling)}\n{index}")), hash);
        var seed = BinaryPrimitives.ReadUInt32LittleEndian(hash);
        return (IdPrefix + tag + ":" + Convert.ToHexString(hash[..6]).ToLowerInvariant(), seed);
    }

    public static PolisCitizenAge AgeOf(uint seed) => ((seed >> 12) % 10) switch
    {
        < 3 => PolisCitizenAge.Young,
        < 8 => PolisCitizenAge.Adult,
        _ => PolisCitizenAge.Old,
    };

    /// <summary>
    /// 这座城的人（规则 1–5）。<paramref name="cityKey"/> 是工作区 Id（神殿岛是 sanctuary）；<paramref name="dwellings"/> 是城里的住所
    /// （<see cref="Dwellings"/>）。结果按住所的生长次序与序号排。
    /// </summary>
    public static IReadOnlyList<PolisCitizen> Derive(string cityKey, IReadOnlyList<PolisDwelling> dwellings)
    {
        ArgumentNullException.ThrowIfNull(cityKey);
        ArgumentNullException.ThrowIfNull(dwellings);
        var tag = CityTag(cityKey);
        var all = new List<PolisCitizen>();
        var ordered = dwellings.OrderBy(d => d.Order).ThenBy(d => d.Key, StringComparer.Ordinal).ToList();
        foreach (var d in ordered)
        {
            var count = HeadCount(cityKey, d);
            for (var i = 0; i < count; i++)
            {
                var (id, seed) = Identity(cityKey, tag, d, i);
                all.Add(new PolisCitizen(id, seed, IsFemale(seed), AgeOf(seed), PolisTrades.Pick(seed, d), d.Home, d.Key, i, d.Order, d.Cottage,
                    OnStreet: false, NightOwl: false));
            }
        }
        all = AssignNames(all).ToList();

        // 规则 4：按住所最近动过的先后轮流上街
        var byDwelling = all.GroupBy(c => c.Dwelling, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.OrderBy(c => c.Index).ToList(), StringComparer.Ordinal);
        var rotation = ordered
            .OrderByDescending(d => d.LastModified)
            .ThenBy(d => d.Order)
            .ThenBy(d => d.Key, StringComparer.Ordinal)
            .Where(d => byDwelling.ContainsKey(d.Key))
            .Select(d => byDwelling[d.Key])
            .ToList();
        var street = new List<string>();
        for (var round = 0; round < MaxPerDwelling && street.Count < StreetCap; round++)
        {
            foreach (var people in rotation)
            {
                if (street.Count >= StreetCap) break;
                if (round < people.Count) street.Add(people[round].Id);
            }
        }
        // 规则 6：街上没有农夫或牧人，就从街上挑一位改行
        var stallDwellings = ordered.Where(d => d.Stall).Select(d => d.Key).ToHashSet(StringComparer.Ordinal);
        all = GuaranteeTrades(all, street, stallDwellings).ToList();
        // 规则 5：夜游的人（街上的里按种子挑、按上街的次序取最多四位）
        var byId = all.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var owls = street.Where(id => (byId[id].Seed >> 17) % NightOwlOneIn == 0).Take(MaxNightOwls).ToHashSet(StringComparer.Ordinal);
        var onStreet = street.ToHashSet(StringComparer.Ordinal);
        return all.Select(c => c with { OnStreet = onStreet.Contains(c.Id), NightOwl = owls.Contains(c.Id) }).ToList();
    }

    /// <summary>
    /// 规则 6：街上（<paramref name="street"/>）没有 <see cref="GuaranteedTrades"/> 里的哪一行，就从街上挑一位改成那一行。
    /// 先挑无业的，没有就挑街上人数最多的那一行（同样多按职业表的次序）；同一行里按 Id 的序数取第一位。摊主与已经是保底职业的人不动。
    /// </summary>
    public static IReadOnlyList<PolisCitizen> GuaranteeTrades(IReadOnlyList<PolisCitizen> citizens, IReadOnlyList<string> street, IReadOnlySet<string> stallDwellings)
    {
        ArgumentNullException.ThrowIfNull(citizens);
        ArgumentNullException.ThrowIfNull(street);
        ArgumentNullException.ThrowIfNull(stallDwellings);
        var byId = citizens.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var changed = new Dictionary<string, string>(StringComparer.Ordinal);
        string TradeOf(string id) => changed.TryGetValue(id, out var t) ? t : byId[id].Trade;
        int Rank(string trade) { for (var i = 0; i < PolisTrades.Keys.Count; i++) if (PolisTrades.Keys[i] == trade) return i; return int.MaxValue; }
        foreach (var trade in GuaranteedTrades)
        {
            if (street.Any(id => TradeOf(id) == trade)) continue;
            var pool = street.Where(id => !stallDwellings.Contains(byId[id].Dwelling) && !GuaranteedTrades.Contains(TradeOf(id))).ToList();
            if (pool.Count == 0) continue;
            var idle = pool.Where(id => TradeOf(id) == PolisTrades.Idle).ToList();
            var candidates = idle.Count > 0
                ? idle
                : pool.GroupBy(TradeOf).OrderByDescending(g => g.Count()).ThenBy(g => Rank(g.Key)).First().ToList();
            changed[candidates.OrderBy(id => id, StringComparer.Ordinal).First()] = trade;
        }
        return changed.Count == 0 ? citizens : citizens.Select(c => changed.TryGetValue(c.Id, out var t) ? c with { Trade = t } : c).ToList();
    }

    /// <summary>
    /// 城里的住所：文件夹的建筑（推给页面的那一份里的村舍也在内，键以 <c>household:</c> 开头）与市集的摊位。
    /// 摊位没有自己的地块：次序排在所有建筑之后，最近动过的时刻借广场的。
    /// </summary>
    public static IReadOnlyList<PolisDwelling> Dwellings(PolisFixtureDocument city)
    {
        ArgumentNullException.ThrowIfNull(city);
        var list = city.Buildings
            .Select(b => new PolisDwelling(b.Key, b.Kind, b.SizeClass, b.State, b.Plot.X, b.Plot.Z, b.Order, b.LastModified,
                Cottage: PolisHouseholds.IsHouseholdKey(b.Key)))
            .ToList();
        var stallTime = city.Agora.LastModified ?? DateTimeOffset.MinValue;
        var order = list.Count == 0 ? 0 : list.Max(d => d.Order) + 1;
        foreach (var stall in city.Market ?? Array.Empty<string>())
            list.Add(new PolisDwelling(stall, PolisBuildingKind.House, 1, PolisBuildingState.Lived, 0, 0, order++, stallTime, Stall: true));
        return list;
    }

    public static PolisCensus Census(IReadOnlyList<PolisCitizen> citizens)
    {
        ArgumentNullException.ThrowIfNull(citizens);
        var street = citizens.Count(c => c.OnStreet);
        return new PolisCensus(citizens.Count, street, citizens.Count - street);
    }

    /// <summary>"|" 分隔的几种说法里按种子挑一种（同一个人永远说同一种）。</summary>
    public static string Pick(string variants, uint seed, int salt = 0)
    {
        ArgumentNullException.ThrowIfNull(variants);
        var list = variants.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (list.Length == 0) return string.Empty;
        return list[(int)((seed + (uint)salt * 2654435761u) % (uint)list.Length)];
    }

    /// <summary>
    /// 规则 3：一座城里的人起名字——完整的希腊名，同城不重，男女有别。按给的次序（住所的生长次序、序号）一位一位挑：先按种子挑首选名，
    /// 被占了就按种子定的步长往后找空着的；同性别的人多过名表，第二轮从头再分一遍，名字后面带序号（<c>Polis.Citizen.NameNth</c>）。
    /// 新盖一座楼只会给新来的人起名，不会改动老住户的名字。
    /// </summary>
    public static IReadOnlyList<PolisCitizen> AssignNames(IEnumerable<PolisCitizen> citizens, int namesPerSex = NamesPerSex)
    {
        ArgumentNullException.ThrowIfNull(citizens);
        if (namesPerSex <= 0) throw new ArgumentOutOfRangeException(nameof(namesPerSex));
        var list = citizens.ToList();
        var taken = new Dictionary<(bool Female, int Round), HashSet<int>>();
        var assigned = new Dictionary<string, (int Index, int Round)>(StringComparer.Ordinal);
        foreach (var c in list.OrderBy(c => c.DwellingOrder).ThenBy(c => c.Index).ThenBy(c => c.Id, StringComparer.Ordinal))
        {
            var preferred = (int)((c.Seed >> 8) % (uint)namesPerSex);
            // 步长与名表长度互质，绕一整圈才回到首选名：每个名字都试得到
            var stride = 1 + (int)((c.Seed >> 20) % (uint)Math.Max(1, namesPerSex - 1));
            while (Gcd(stride, namesPerSex) != 1) stride++;
            for (var round = 0; ; round++)
            {
                if (!taken.TryGetValue((c.Female, round), out var used))
                    taken[(c.Female, round)] = used = new HashSet<int>();
                if (used.Count >= namesPerSex) continue;
                var index = preferred;
                while (used.Contains(index)) index = (index + stride) % namesPerSex;
                used.Add(index);
                assigned[c.Id] = (index, round);
                break;
            }
        }
        return list.Select(c => assigned.TryGetValue(c.Id, out var n) ? c with { NameIndex = n.Index, NameRound = n.Round } : c).ToList();
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0) (a, b) = (b, a % b);
        return Math.Abs(a);
    }

    /// <summary>
    /// 居民的名字：希腊名表（男女各一份，"|" 分隔）里 <see cref="PolisCitizen.NameIndex"/> 那一个；第二轮起按 <paramref name="nthPattern"/>
    /// 带上序号（中文"{0}（{1}）"，{1} 从 2 起）。名表比 <see cref="NamesPerSex"/> 短（locale 缺词条）时取模，不抛。
    /// </summary>
    public static string NameFor(PolisCitizen citizen, string maleNames, string femaleNames, string nthPattern)
    {
        ArgumentNullException.ThrowIfNull(citizen);
        var names = (citizen.Female ? femaleNames : maleNames).Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (names.Length == 0) return string.Empty;
        var name = names[citizen.NameIndex % names.Length];
        return citizen.NameRound == 0
            ? name
            : string.Format(CultureInfo.CurrentCulture, nthPattern, name, citizen.NameRound + 1);
    }
}
