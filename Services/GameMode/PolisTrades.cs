using System;
using System.Collections.Generic;
using System.Linq;

namespace Athena.UI.Services.GameMode;

/// <summary>
/// 一门职业：页面认的键、中英文名、他平时在哪儿干什么（提示词里那一句），以及分配的分量。
/// </summary>
public sealed record PolisTrade(string Key, string Zh, string En, string ZhWork, string EnWork, int Weight);

/// <summary>
/// 职业表（第四轮需求 5，2026-10-15，用户：先预先确定需要创建的职业，这些职业会分配给 NPC，也会指导新游戏资产的创建——
/// 农田、捕鱼、纺织、畜牧等等，允许有无业的人）。页面的 <c>art/crafts.mjs</c> 按同样的键挑他干的活；一张表在
/// <c>Docs/GameMode_M6_Plan_CN.md</c>。职业不和十位职业居民的行当撞（<see cref="PolisTalk.ResidentTrades"/>）：他们在远处就靠行当被认出来。
///
/// 怎么分（<see cref="Pick"/>）：按种子加权挑；建筑的类型让某些职业更常见（作坊多工匠、仓库多商贩和磨坊工、雕塑园多石匠和陶工、
/// 柱廊书库多乐师、村舍多农夫和牧人），市集的摊主一律是商贩。无业约占一成。
/// </summary>
public static class PolisTrades
{
    public const string Idle = "idle";
    public const string Merchant = "merchant";

    public static IReadOnlyList<PolisTrade> All { get; } = new[]
    {
        new PolisTrade("farmer", "农夫", "farmer", "在城北的田里种麦子和菜", "grows wheat and vegetables in the fields north of the city", 10),
        new PolisTrade("herder", "牧人", "herder", "在城北的牧场放羊", "keeps sheep on the pasture north of the city", 6),
        new PolisTrade("fisher", "渔夫", "fisher", "在港口的栈桥上钓鱼", "fishes from the harbour pier", 7),
        new PolisTrade("weaver", "织工", "weaver", "在家门前的立式织机上织布", "weaves cloth on the upright loom by the door", 7),
        new PolisTrade("spinner", "纺纱工", "spinner", "拿着纺锤纺线", "spins thread with a spindle", 6),
        new PolisTrade("potter", "陶工", "potter", "在家门前拉坯做陶罐", "throws pots on the wheel by the door", 7),
        new PolisTrade("baker", "面包师", "baker", "在家门前的面槽里揉面", "kneads dough in the trough by the door", 7),
        new PolisTrade("miller", "磨坊工", "miller", "用石臼研磨谷物和香料", "grinds grain and spices in a stone mortar", 6),
        new PolisTrade("cobbler", "鞋匠", "cobbler", "缝补凉鞋", "stitches and mends sandals", 6),
        new PolisTrade("mason", "石匠", "mason", "在家门前琢石料", "dresses stone blocks by the door", 6),
        new PolisTrade("carpenter", "木匠", "carpenter", "在家门前的锯木架上锯木头", "saws timber on the sawhorse by the door", 6),
        new PolisTrade("musician", "乐师", "musician", "在广场上弹里拉琴", "plays the lyre in the agora", 5),
        new PolisTrade(Merchant, "商贩", "merchant", "在市集上吆喝买卖", "calls out wares at the market", 6),
        new PolisTrade(Idle, "无业的人", "person without work", "没有正经营生，打打零工、四处闲逛", "has no steady work, takes odd jobs and wanders about", 9),
    };

    /// <summary>
    /// 一座城里这一行最多几位（2026-10-15，用户：放牧的最多 2 人、种田的最多 3 人，站位不同）。城北只有一块羊圈、至多三块田，
    /// 人多了只能挤在同一个站位上。没写的职业不限。超出的由 <see cref="PolisCitizens.CapTrades"/> 改抽别的行当。
    /// </summary>
    public static IReadOnlyDictionary<string, int> Caps { get; } = new Dictionary<string, int>(StringComparer.Ordinal) { ["farmer"] = 3, ["herder"] = 2 };

    private static readonly Dictionary<string, PolisTrade> ByKeyMap = All.ToDictionary(t => t.Key, StringComparer.Ordinal);

    public static IReadOnlyList<string> Keys { get; } = All.Select(t => t.Key).ToList();

    public static bool IsKnown(string? key) => key != null && ByKeyMap.ContainsKey(key);

    public static PolisTrade ByKey(string key) => ByKeyMap.TryGetValue(key, out var t) ? t : ByKeyMap[Idle];

    /// <summary>建筑类型让哪些职业更常见（乘在分量上，没写的乘 1）。</summary>
    private static double Bias(PolisDwelling dwelling, string trade)
    {
        if (dwelling.Cottage) return trade switch { "farmer" or "herder" => 3, "spinner" => 2, "baker" => 1.5, _ => 1 };
        return dwelling.Kind switch
        {
            PolisBuildingKind.Workshop => trade switch { "potter" or "carpenter" or "mason" => 3, "cobbler" or "weaver" => 2, _ => 1 },
            PolisBuildingKind.Warehouse => trade switch { Merchant or "miller" => 3, "baker" => 2, _ => 1 },
            PolisBuildingKind.Treasury => trade switch { Merchant => 3, "miller" => 1.5, _ => 1 },
            PolisBuildingKind.SculptureGarden => trade switch { "mason" => 3, "potter" or "musician" => 2, _ => 1 },
            PolisBuildingKind.StoaLibrary => trade switch { "musician" => 3, "weaver" or "spinner" => 1.5, _ => 1 },
            _ => trade switch { "farmer" or "herder" or "spinner" => 1.5, _ => 1 },
        };
    }

    /// <summary>
    /// 按种子与住所挑一门职业（市集的摊主一律是商贩）。同一个种子、同一处住所永远是同一门。<paramref name="exclude"/> 里的职业不挑
    /// （<see cref="PolisCitizens.CapTrades"/> 给满了额的人改行用）。
    /// </summary>
    public static string Pick(uint seed, PolisDwelling dwelling, IReadOnlySet<string>? exclude = null)
    {
        ArgumentNullException.ThrowIfNull(dwelling);
        if (dwelling.Stall) return Merchant;
        var weights = All.Select(t => exclude?.Contains(t.Key) == true ? 0 : t.Weight * Bias(dwelling, t.Key)).ToList();
        var total = weights.Sum();
        // 用种子的高几位，与男女（最低位）、年纪（>> 12）不相关
        var x = ((seed >> 20) ^ (seed * 2654435761u >> 9)) % 100_000 / 100_000.0 * total;
        for (var i = 0; i < All.Count; i++)
        {
            if (x < weights[i]) return All[i].Key;
            x -= weights[i];
        }
        return All[^1].Key;
    }
}
