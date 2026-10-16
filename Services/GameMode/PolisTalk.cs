using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Athena.UI.Services.GameMode;

/// <summary>城里一个人的性子（2026-10-13，用户：NPC 要有各自的性格）。四样都从种子挑，同一个人永远同一副脾气。</summary>
public sealed record PolisPersona(string Trade, string Temperament, string Manner, string Pastime);

/// <summary>
/// 说话的人在这座城里是谁：名字、男女、年纪、性子；<see cref="Calling"/> 是他的身份（第四轮起：他住在哪儿、和谁同住、靠什么过日子）。
/// 职业居民（2026-10-13）与雅典娜用 <see cref="Portrait"/>：写好的一段，代替按种子挑的性子。
/// </summary>
public sealed record PolisSpeaker(string Name, bool Female, PolisCitizenAge Age, PolisPersona Persona, string Calling, string? Portrait = null, bool Goddess = false);

/// <summary>这座城此刻的样子（提示词里的世界观）：城名、是不是神殿、白天还是夜里、多少居民、有哪些文件夹（建筑）。</summary>
public sealed record PolisWorldFacts(string CityName, bool Sanctuary, bool Night, int Population, IReadOnlyList<string> Buildings);

/// <summary>和他说过的话：你说的、他答的（一来一回为一轮）。</summary>
public sealed record PolisTalkTurn(string You, string Them);

/// <summary>
/// 一次说话调用要发的东西：系统提示与依次的消息（role 为 user / assistant）。<see cref="MaxReplyChars"/> 是回答留多少字
/// （气泡里的一句话是 <see cref="PolisTalk.MaxBubbleChars"/>；围观的人起哄要回一段 JSON，留得长一些）。<see cref="Raw"/> 为真时
/// 回答只剥掉思考块、去掉首尾空白，不按气泡整理（不收换行、不去引号）——JSON 的代码围栏要靠换行认。
/// </summary>
public sealed record PolisTalkPrompt(string System, IReadOnlyList<(string Role, string Text)> Messages, int MaxReplyChars = PolisTalk.MaxBubbleChars, bool Raw = false);

/// <summary>
/// 此刻（第四轮需求 4、5，2026-10-15，用户：NPC 预置的上下文还要有环境和时间、以及他当前的状态）：本地的时刻、城里是不是夜里、
/// 他在哪儿（<see cref="PolisTalk.WhereKeys"/>）、在干什么（<see cref="PolisTalk.DoingKeys"/>）。地点与状态由页面随意图送来，C# 逐个校验。
/// </summary>
public sealed record PolisMoment(DateTimeOffset Local, bool Night, string? Where = null, string? Doing = null);

/// <summary>
/// 一次说话的上下文（第四轮）：他对你的态度、对雅典娜的态度、隔着门的那一句、此刻、他住的那座楼（详）、整个项目（略）。
/// 楼与项目里的文件内容只在你开口时才有（<see cref="PolisBriefs"/>）；两个人自己闲聊时为 null。
/// </summary>
public sealed record PolisTalkContext(
    string? Attitude = null,
    string? AthenaRegard = null,
    string? Door = null,
    string? Moment = null,
    string? Building = null,
    string? Project = null,
    bool Night = false);

/// <summary>辩论时围观的人的一句：喝彩（cheer）、嘘（boo）或者一句短评（comment）。</summary>
public sealed record PolisCrowdReaction(string Kind, string Text);

/// <summary>
/// 城里的人怎么说话（2026-10-13，用户：和 NPC 说话时，他的上下文要有游戏世界的信息和他自己的性格，而不是只按他那场会话回答——
/// 所以能闲聊；回复出现在他头顶的气泡里）。纯函数：性子从种子挑，世界观、身份与记忆拼成提示词，流式输出里推理块之外的字是"看得见的"。
///
/// 规则（<c>Athena.Archive.Tests</c> 的 <c>polis talk</c> 逐条断言）：
/// 1. 性子按种子从三张表里各挑一样（脾气、说话习惯、闲时爱做的事），中英两份表一一对应——换界面语言还是同一副脾气；职业是住所分给他的
///    （<see cref="PolisTrades"/>），不和十位职业居民的行当撞（抄写员、铁匠、水手……，他们靠这个在远处就被认出来）。
/// 2. 世界观只有城名、白天黑夜、人口、建筑（文件夹）名；他是谁（名字、性子、职业、住哪儿）；此刻（时刻、昼夜、他在哪儿干什么，<see cref="MomentText"/>）；
///    他对你、对雅典娜的态度。你开口和他说话时，再带他住的那座楼的详情与整个项目的大概（<see cref="BuildingText"/> / <see cref="ProjectText"/>）——
///    所以能聊项目；没有任何会话的东西。
/// 3. 说话的规矩：口语、一到三句、不超过 <see cref="MaxReplyChars"/> 字、用对方用的语言；可以闲聊、可以聊项目，说到项目只凭写给他的那些，
///    没写的就说不清楚；夜里说话轻一点、短一点；不说自己是 AI，不用 Markdown、列表、表情。
/// 4. 记住的来回最多 <see cref="MaxHistoryTurns"/> 轮（旧的先忘）。
/// 5. 城里的人自己聊（2026-10-13，用户：NPC 之间的对话只用模型，不预置台词；第四轮起雅典娜也会和人聊）：一人一句，各一次调用——先开口的那句、
///    接话的那句（带着对方刚说的）。这是没人按回车也会发生的调用，所以只带世界观、两人是谁、此刻与交情，<b>不带任何文件内容</b>；
///    一句不超过 <see cref="MaxAmbientChars"/> 字。
/// </summary>
public static class PolisTalk
{
    /// <summary>气泡里放得下的一句话：提示词要他不超过这么多字，回来的再按 <see cref="MaxBubbleChars"/> 截一刀。</summary>
    public const int MaxReplyChars = 80;
    public const int MaxBubbleChars = 160;
    /// <summary>你一句最多多少字（页面的输入框同一个上限）。</summary>
    public const int MaxLineChars = 300;
    public const int MaxHistoryTurns = 6;
    /// <summary>两位居民街头闲聊，一人一句不超过这么多字。</summary>
    public const int MaxAmbientChars = 40;

    // —— 规则 1：性子的三张表（中、英一一对应）；职业在 PolisTrades ——
    private static readonly (string Zh, string En)[] Temperaments =
    {
        ("爽朗", "cheerful"), ("腼腆", "shy"), ("较真", "exacting"), ("爱打趣", "teasing"), ("慢性子", "unhurried"), ("热心肠", "warm-hearted"),
        ("有点多疑", "a little suspicious"), ("乐天", "sunny"), ("沉稳", "steady"), ("急性子", "impatient"), ("爱发牢骚", "given to grumbling"), ("好奇", "curious"),
    };

    private static readonly (string Zh, string En)[] Manners =
    {
        ("说话爱打比方", "talks in comparisons"), ("说话简短", "speaks in few words"), ("爱引老话", "quotes old sayings"), ("爱反问", "answers with questions"),
        ("说话慢条斯理", "speaks slowly and carefully"), ("一高兴话就多", "gets talkative when pleased"), ("爱夸人", "is quick to praise"),
        ("说话带点自嘲", "is a little self-mocking"), ("爱讲自己的见闻", "likes to tell what they have seen"), ("措辞文雅", "chooses words elegantly"),
    };

    private static readonly (string Zh, string En)[] Pastimes =
    {
        ("在港口看船进出", "watching ships at the harbour"), ("侍弄橄榄树", "tending olive trees"), ("下跳棋", "playing pebble games"),
        ("听人吟唱史诗", "listening to epic singers"), ("在市集讨价还价", "haggling at the market"), ("去神庙供奉", "making offerings at the temple"),
        ("在广场听人辩论", "listening to debates in the agora"), ("种花", "growing flowers"), ("捏陶偶", "shaping clay figures"), ("看星星", "watching the stars"),
        ("钓鱼", "fishing"), ("摔跤", "wrestling"),
    };

    /// <summary>十位职业居民的行当（规则 1：居民的职业不和他们撞）。</summary>
    public static IReadOnlyList<string> ResidentTrades { get; } = new[]
    {
        "测绘员", "抄写员", "账房", "铁匠", "水手", "图书管理员", "传令官", "画师", "工匠", "女祭司",
        "surveyor", "scribe", "accountant", "smith", "sailor", "librarian", "herald", "painter", "artisan", "priestess",
    };

    /// <summary>几张表各有几样（测试核对两种语言一一对应）。</summary>
    public static (int Trades, int Temperaments, int Manners, int Pastimes) TableSizes => (PolisTrades.All.Count, Temperaments.Length, Manners.Length, Pastimes.Length);

    public static bool IsChinese(string language) => language.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    /// <summary>规则 1：种子 → 性子；职业是住所分给他的那一门（键，见 <see cref="PolisTrades"/>）。三样用种子不同的几段位，彼此不相关。</summary>
    public static PolisPersona PersonaFor(uint seed, string trade, string language)
    {
        var zh = IsChinese(language);
        string Pick((string Zh, string En)[] table, int shift)
        {
            var row = table[IndexOf(seed, shift, table.Length)];
            return zh ? row.Zh : row.En;
        }
        var t = PolisTrades.ByKey(trade);
        return new PolisPersona(zh ? t.Zh : t.En, Pick(Temperaments, 9), Pick(Manners, 15), Pick(Pastimes, 21));
    }

    private static int IndexOf(uint seed, int shift, int length) => (int)(((seed >> shift) ^ (seed * 2654435761u >> (shift + 3))) % (uint)length);

    /// <summary>他的脾气在脾气表里的位置（关系的暖度、耐心、健谈都按它，见 <see cref="PolisRelations"/>）。</summary>
    public static int TemperamentIndex(uint seed) => IndexOf(seed, 9, Temperaments.Length);

    /// <summary>脾气的名字（页面的卡片上写着）。</summary>
    public static string TemperamentName(uint seed, string language)
    {
        var row = Temperaments[TemperamentIndex(seed)];
        return IsChinese(language) ? row.Zh : row.En;
    }

    /// <summary>
    /// 他对城的主人的态度（第三轮需求 1：好恶直接影响对话）——按关系的档写一句，放进和他说话的系统提示里。
    /// 厌恶也不许骂人、不许失礼：冷淡、带刺、不想多说就够了。
    /// </summary>
    public static string AttitudeText(PolisRelationBand band, string language) => IsChinese(language)
        ? band switch
        {
            PolisRelationBand.Close => "你很喜欢城的主人，见了他心里高兴，说话热络、亲近。",
            PolisRelationBand.Friendly => "你对城的主人挺有好感，说话和气。",
            PolisRelationBand.Neutral => "你对城的主人谈不上喜欢不喜欢，客客气气的。",
            PolisRelationBand.Cool => "你对城的主人有点冷淡，话不多，不太热情。",
            _ => "你不喜欢城的主人，说话冷淡、带点刺，不想多搭理他——但不骂人、不失礼。",
        }
        : band switch
        {
            PolisRelationBand.Close => "You are very fond of the city's owner; you are glad to see them and talk warmly and familiarly.",
            PolisRelationBand.Friendly => "You like the city's owner and speak kindly to them.",
            PolisRelationBand.Neutral => "You neither like nor dislike the city's owner; you are polite.",
            PolisRelationBand.Cool => "You are a little cool towards the city's owner: few words, not much warmth.",
            _ => "You do not like the city's owner: you are cool and a little prickly and would rather not talk - but never rude or insulting.",
        };

    /// <summary>他和另一个人的交情（两位居民闲聊、几个人一起接你的话时，放进提示词）。</summary>
    public static string TiesText(PolisRelationBand band, string otherName, string language) => IsChinese(language)
        ? band switch
        {
            PolisRelationBand.Close => $"你和「{otherName}」是老朋友，很要好。",
            PolisRelationBand.Friendly => $"你和「{otherName}」处得不错。",
            PolisRelationBand.Neutral => $"你和「{otherName}」只是点头之交。",
            PolisRelationBand.Cool => $"你和「{otherName}」有点不对付。",
            _ => $"你很不喜欢「{otherName}」，两人一碰面就别扭——说话可以带点刺，但不吵架、不骂人。",
        }
        : band switch
        {
            PolisRelationBand.Close => $"You and {otherName} are old friends and very close.",
            PolisRelationBand.Friendly => $"You get on well with {otherName}.",
            PolisRelationBand.Neutral => $"You and {otherName} are on nodding terms.",
            PolisRelationBand.Cool => $"You and {otherName} do not quite get along.",
            _ => $"You really do not like {otherName}; meeting them is awkward - you may be a little prickly, but no quarrels or insults.",
        };

    /// <summary>
    /// 一个人的身份（第四轮）：住在哪儿、和几个人同住、靠什么过日子。<paramref name="homeName"/> 是他那座楼的名字（文件夹名、村舍的户名、摊位的文件夹名）。
    /// </summary>
    public static string HomeText(PolisCitizen citizen, string homeName, int housemates, string language)
    {
        ArgumentNullException.ThrowIfNull(citizen);
        var zh = IsChinese(language);
        var trade = PolisTrades.ByKey(citizen.Trade);
        var with = housemates <= 0 ? string.Empty : zh ? $"，和另外 {housemates} 人同住" : $", together with {housemates} other{(housemates == 1 ? string.Empty : "s")}";
        var where = citizen.Home == "market"
            ? (zh ? $"你在市集上守着「{homeName}」那个摊位" : $"You keep the \"{homeName}\" stall at the market")
            : citizen.Cottage
                ? (zh ? $"你住在城郊的一座村舍里（「{homeName}」）{with}" : $"You live in a cottage on the edge of town (\"{homeName}\"){with}")
                : (zh ? $"你住在「{homeName}」这座楼里{with}——这座楼就是城的主人的一个文件夹" : $"You live in the building \"{homeName}\"{with} - the building is one of the city owner's folders");
        return zh ? $"{where}。平日里你{trade.ZhWork}。" : $"{where}. Most days you {trade.EnWork}.";
    }

    /// <summary>他在哪儿（页面送来的键，C# 逐个校验）：家门前、屋里、广场、市集、港口、神庙、图书馆、橄榄林、田里、牧场、邻居门前、讲台前、路上。</summary>
    public static IReadOnlyList<string> WhereKeys { get; } = new[]
    {
        "home", "inside", "agora", "market", "harbor", "temple", "library", "grove", "fields", "pasture", "neighbour", "bema", "road",
    };

    /// <summary>他在干什么：干活、走路、闲站、和人聊、围观辩论、夜里出来走走、在市集上守着自己的摊子、在市集的摊子前挑东西（2026-10-15）。</summary>
    public static IReadOnlyList<string> DoingKeys { get; } = new[] { "work", "walk", "idle", "chat", "audience", "stroll", "sell", "shop" };

    private static string WherePhrase(string? where, bool zh) => where switch
    {
        "home" => zh ? "站在自家门前" : "standing by your own door",
        "inside" => zh ? "在自己家里" : "at home indoors",
        "agora" => zh ? "在广场上" : "in the agora",
        "market" => zh ? "在市集上" : "at the market",
        "harbor" => zh ? "在港口" : "at the harbour",
        "temple" => zh ? "在神庙前" : "in front of the temple",
        "library" => zh ? "在图书馆前" : "by the library",
        "grove" => zh ? "在橄榄林里" : "in the olive grove",
        "fields" => zh ? "在城北的田里" : "in the fields north of the city",
        "pasture" => zh ? "在城北的牧场上" : "on the pasture north of the city",
        "neighbour" => zh ? "在邻居家门前" : "by a neighbour's door",
        "bema" => zh ? "在讲台前" : "by the speaker's platform",
        "road" => zh ? "在街上" : "in the street",
        _ => zh ? "在城里" : "in town",
    };

    private static string DoingPhrase(string? doing, string? trade, bool zh)
    {
        var t = trade == null ? null : PolisTrades.ByKey(trade);
        return doing switch
        {
            "work" => t == null || t.Key == PolisTrades.Idle ? (zh ? "，手上忙着点零活" : ", busy with an odd job") : (zh ? $"，正在干活（{t.Zh}的活）" : $", at work ({t.En}'s work)"),
            "walk" => zh ? "，正走着路" : ", walking along",
            "chat" => zh ? "，正和人说着话" : ", in the middle of a chat",
            "audience" => zh ? "，挤在人群里看辩论" : ", in the crowd watching a debate",
            "stroll" => zh ? "，夜里睡不着出来走走" : ", out for a walk because you cannot sleep",
            // 市集（2026-10-15）：摊主守着摊子叫卖（卖的是他那门手艺的东西，商贩卖杂货鱼菜油酒），买主在摊前挑、问价、讲价
            "sell" => t == null || t.Key is PolisTrades.Merchant or PolisTrades.Idle
                ? (zh ? "，正守着市集上自己的摊子叫卖" : ", minding your own stall at the market and calling out your wares")
                : (zh ? $"，正守着市集上自己的摊子，卖自己做的东西（{t.Zh}的手艺）" : $", minding your own stall at the market, selling what you make ({t.En}'s goods)"),
            "shop" => zh ? "，正在市集的摊子前挑东西、问价" : ", at a market stall, looking over the goods and asking prices",
            _ => zh ? "，闲站着" : ", standing about",
        };
    }

    /// <summary>
    /// 规则 2：此刻——日期、星期、钟点（本地），城里是白天还是夜里（夜里写明大多数人已经回家），他在哪儿、在干什么。
    /// <paramref name="trade"/> 是他的职业键（"在干活"说得出是什么活；职业居民为 null）。
    /// </summary>
    public static string MomentText(PolisMoment moment, string? trade, string language)
    {
        ArgumentNullException.ThrowIfNull(moment);
        var zh = IsChinese(language);
        var culture = CultureInfo.GetCultureInfo(zh ? "zh-CN" : "en-US");
        var when = moment.Local.ToString(zh ? "yyyy年M月d日 dddd HH:mm" : "dddd d MMMM yyyy, HH:mm", culture);
        var sky = moment.Night
            ? (zh ? "城里已经入夜，家家户户点着灯，大多数人都回家了，街上只有零星几个人。" : "Night has fallen; lamps are lit in the houses, most people have gone home and only a few are still about.")
            : (zh ? "城里是白天，人们在街上来来往往、各忙各的。" : "It is day in the city; people come and go about their business.");
        var you = zh ? $"你此刻{WherePhrase(moment.Where, true)}{DoingPhrase(moment.Doing, trade, true)}。" : $"Right now you are {WherePhrase(moment.Where, false)}{DoingPhrase(moment.Doing, trade, false)}.";
        return zh ? $"现在是{when}。{sky}{you}" : $"It is {when}. {sky} {you}";
    }

    /// <summary>建筑类型的名字。</summary>
    public static string KindName(PolisBuildingKind kind, string language) => IsChinese(language)
        ? kind switch
        {
            PolisBuildingKind.StoaLibrary => "柱廊书库（多是文档）",
            PolisBuildingKind.SculptureGarden => "雕塑园（多是图片）",
            PolisBuildingKind.Treasury => "金库（多是表格）",
            PolisBuildingKind.Workshop => "作坊（多是代码）",
            PolisBuildingKind.Warehouse => "货栈（多是压缩包）",
            _ => "民居（什么都有一点）",
        }
        : kind switch
        {
            PolisBuildingKind.StoaLibrary => "a colonnaded library (mostly documents)",
            PolisBuildingKind.SculptureGarden => "a sculpture garden (mostly images)",
            PolisBuildingKind.Treasury => "a treasury (mostly spreadsheets)",
            PolisBuildingKind.Workshop => "a workshop (mostly code)",
            PolisBuildingKind.Warehouse => "a warehouse (mostly archives)",
            _ => "a house (a bit of everything)",
        };

    private static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.#} KB"),
        < 1024L * 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024):0.#} MB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024 * 1024):0.#} GB"),
    };

    /// <summary>
    /// 规则 2：他住的那座楼（详）：名字、类型、文件数与体积、按类型的分布、最近改动、第一层的条目、README 的开头。只在你开口和他说话时进提示词。
    /// </summary>
    public static string BuildingText(PolisBuildingBrief brief, string language)
    {
        ArgumentNullException.ThrowIfNull(brief);
        var zh = IsChinese(language);
        var sb = new StringBuilder();
        var kinds = string.Join(zh ? "、" : ", ", brief.Kinds.OrderByDescending(k => k.Value).Take(6).Select(k => $"{k.Key} {k.Value}"));
        var modified = brief.LastModified.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        sb.AppendLine(zh
            ? $"你住的这座楼「{brief.Key}」是{KindName(brief.Kind, language)}，里面有 {brief.Files} 个文件、共 {Size(brief.Bytes)}{(kinds.Length == 0 ? string.Empty : $"（{kinds}）")}，最近一次改动在 {modified}。"
            : $"Your building \"{brief.Key}\" is {KindName(brief.Kind, language)} holding {brief.Files} files, {Size(brief.Bytes)} in all{(kinds.Length == 0 ? string.Empty : $" ({kinds})")}; last changed on {modified}.");
        if (brief.Entries.Count > 0)
        {
            var items = brief.Entries.Select(e => e.IsDirectory ? e.Name + "/" : e.Name);
            sb.AppendLine((zh ? "楼里第一层有：" : "On its first floor: ") + string.Join(zh ? "、" : ", ", items)
                + (brief.MoreEntries > 0 ? (zh ? $"……还有 {brief.MoreEntries} 样" : $"... and {brief.MoreEntries} more") : string.Empty));
        }
        if (!string.IsNullOrEmpty(brief.Readme))
            sb.AppendLine(zh ? "楼里那份说明（README）开头写着：" : "The README there begins:").AppendLine(brief.Readme);
        return sb.ToString().TrimEnd();
    }

    /// <summary>规则 2：整个项目（略）：工作区名、几座楼（名字、类型、文件数）、市集的摊位、根目录 README 的开头。只在你开口和他说话时进提示词。</summary>
    public static string ProjectText(PolisProjectBrief brief, string language)
    {
        ArgumentNullException.ThrowIfNull(brief);
        var zh = IsChinese(language);
        var sb = new StringBuilder();
        var list = string.Join(zh ? "；" : "; ", brief.Buildings.Select(b => zh ? $"「{b.Key}」{KindName(b.Kind, language)}，{b.Files} 个文件" : $"\"{b.Key}\", {KindName(b.Kind, language)}, {b.Files} files"));
        sb.AppendLine(zh
            ? $"整座城（城的主人的项目「{brief.Name}」）大概是这样：{(list.Length == 0 ? "还没有楼" : list)}{(brief.MoreBuildings > 0 ? $"……另有 {brief.MoreBuildings} 座" : string.Empty)}。"
            : $"The whole city (the owner's project \"{brief.Name}\") in brief: {(list.Length == 0 ? "no buildings yet" : list)}{(brief.MoreBuildings > 0 ? $"... and {brief.MoreBuildings} more" : string.Empty)}.");
        if (brief.Stalls.Count > 0)
            sb.AppendLine((zh ? "市集上的摊位：" : "Stalls at the market: ") + string.Join(zh ? "、" : ", ", brief.Stalls) + (zh ? "。" : "."));
        if (!string.IsNullOrEmpty(brief.Readme))
            sb.AppendLine(zh ? "项目的说明（README）开头写着：" : "The project's README begins:").AppendLine(brief.Readme);
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// 他对雅典娜的态度（第四轮需求 8，2026-10-15，用户：不是所有 NPC 对雅典娜都是尊敬的）。不服她的人说起她可以带刺，但不骂人、不咒神。
    /// </summary>
    public static string AthenaRegardText(PolisRelationBand band, string language) => IsChinese(language)
        ? band switch
        {
            PolisRelationBand.Close => "你非常敬重雅典娜，觉得这座城有她是福气。",
            PolisRelationBand.Friendly => "你敬重雅典娜，见了她客客气气的。",
            PolisRelationBand.Neutral => "你对雅典娜谈不上敬不敬，只当她是城里管事的。",
            PolisRelationBand.Cool => "你对雅典娜不太服气，觉得她有点高高在上。",
            _ => "你看不惯雅典娜，觉得她什么都要管、自以为是——说起她、对着她都会带刺，但不骂人、不咒神。",
        }
        : band switch
        {
            PolisRelationBand.Close => "You revere Athena and count the city lucky to have her.",
            PolisRelationBand.Friendly => "You respect Athena and are courteous when you meet her.",
            PolisRelationBand.Neutral => "You neither revere nor resent Athena; to you she is the one who runs things here.",
            PolisRelationBand.Cool => "You do not quite accept Athena; she seems a little high-handed to you.",
            _ => "You cannot stand Athena: she meddles in everything and thinks she knows best - you are prickly about her and to her face, but you never insult or curse the goddess.",
        };

    /// <summary>
    /// 雅典娜自己（第四轮需求 8：雅典娜和 NPC 交流）：她和城里的人闲聊时，"她是谁"这一段。<paramref name="name"/> 是她在界面语言里的名字。
    /// </summary>
    public static PolisSpeaker AthenaSpeaker(string name, string language)
    {
        var zh = IsChinese(language);
        var portrait = zh
            ? "你是雅典娜本人——这座城的守护女神，智慧、温和、有分寸，替城的主人打理这座城。你对城里的每个人都和气；有人不服你，你也不恼，只是不卑不亢地回一句，偶尔带一点幽默。"
            : "You are Athena herself - the city's guardian goddess, wise, gentle and measured, who looks after the city for its owner. You are kind to everyone in town; when someone resents you, you are not offended and answer with quiet dignity, sometimes with a touch of humour.";
        return new PolisSpeaker(name, true, PolisCitizenAge.Adult, new PolisPersona(zh ? "守护女神" : "guardian goddess", string.Empty, string.Empty, string.Empty), string.Empty, Portrait: portrait, Goddess: true);
    }

    /// <summary>规则 2：世界观。只有城名、白天黑夜、人口、建筑名（最多 12 个）。</summary>
    public static string WorldText(PolisWorldFacts world, string language)
    {
        ArgumentNullException.ThrowIfNull(world);
        var zh = IsChinese(language);
        var buildings = world.Buildings.Take(12).ToList();
        if (zh)
        {
            var sb = new StringBuilder();
            sb.Append(world.Sanctuary
                ? "这里是神殿岛，一座古希腊式的小城，几户人家住在城郊的村舍里。"
                : $"这里是古希腊式的城邦「{world.CityName}」。城就是用户的一个工作区：每座建筑是一个文件夹");
            if (!world.Sanctuary) sb.Append(buildings.Count == 0 ? "。" : $"（{string.Join("、", buildings)}……）。");
            sb.Append("城中央是广场，有讲台和日晷，常有人在讲台前辩论；还有市集、港口（船按时出海打听消息）、神庙、圆形的图书馆和橄榄林（每办完一件托付就多一棵橄榄树）。");
            sb.Append("雅典娜是这座城的守护女神，就住在城里，替城的主人（用户）办事；城里多数人敬重她，也有人不服她。");
            sb.Append(CultureInfo.InvariantCulture, $"城里住着 {world.Population} 位居民。此刻是{(world.Night ? "夜里，家家户户点着灯" : "白天")}。");
            sb.Append("跟你说话的是这座城的主人，大家叫他「你」，不知道他的名字。");
            return sb.ToString();
        }
        var en = new StringBuilder();
        en.Append(world.Sanctuary
            ? "This is the sanctuary island, a small ancient Greek town; a few families live in cottages on its edge. "
            : $"This is the ancient Greek polis \"{world.CityName}\". The city is one of the user's workspaces: every building is a folder"
              + (buildings.Count == 0 ? ". " : $" ({string.Join(", ", buildings)}...). "));
        en.Append("At its centre is the agora with a speaker's platform and a sundial, where people often debate; there are also a market, a harbour (a boat sails out on schedule to gather news), a temple, a round library and an olive grove (one more olive tree for every finished commission). ");
        en.Append("Athena is the city's guardian goddess; she lives in the city and works for its owner (the user). Most townsfolk respect her, though not all. ");
        en.Append(CultureInfo.InvariantCulture, $"{world.Population} citizens live here. It is {(world.Night ? "night, and the lamps are lit" : "daytime")} now. ");
        en.Append("The person talking to you is the city's owner; people simply call them \"you\" and do not know their name.");
        return en.ToString();
    }

    /// <summary>说话的人自己：名字、男女、年纪、职业与性子、身份。</summary>
    public static string SpeakerText(PolisSpeaker speaker, string language)
    {
        ArgumentNullException.ThrowIfNull(speaker);
        var zh = IsChinese(language);
        var p = speaker.Persona;
        var sex = zh ? (speaker.Female ? "女子" : "男子") : (speaker.Female ? "woman" : "man");
        var age = speaker.Age switch
        {
            PolisCitizenAge.Young => zh ? "年轻" : "young",
            PolisCitizenAge.Adult => zh ? "正当壮年" : "in the prime of life",
            _ => zh ? "上了年纪" : "getting on in years",
        };
        if (speaker.Portrait != null)
        {
            return zh
                ? $"你是这座城的{p.Trade}「{speaker.Name}」，{sex}，{age}。{speaker.Portrait}"
                : $"You are {speaker.Name}, the city's {p.Trade}, a {sex}, {age}. {speaker.Portrait}";
        }
        return zh
            ? $"你是这座城里的{(speaker.Female ? "女子" : "男子")}「{speaker.Name}」，{age}，{(p.Trade == PolisTrades.ByKey(PolisTrades.Idle).Zh ? "没有正经营生" : $"以{p.Trade}为生")}。你性子{p.Temperament}，{p.Manner}，闲时爱{p.Pastime}。{speaker.Calling}"
            : $"You are {speaker.Name}, a {(speaker.Female ? "woman" : "man")} of this city, {age}, {(p.Trade == PolisTrades.ByKey(PolisTrades.Idle).En ? "without steady work" : $"who works as a {p.Trade}")}. You are {p.Temperament}, {p.Manner}, and in your free time you enjoy {p.Pastime}. {speaker.Calling}";
    }

    /// <summary>
    /// 规则 3：说话的规矩。<paramref name="knowsProject"/> 为真（城里的人，提示词里有他的楼与项目）时多一条：说到项目只凭写给他的；
    /// <paramref name="night"/> 为真时多一条：夜里说话轻、短，可以聊点心事、劝人早点回家。谁都只是聊天——不能替雅典娜办事、也不答应替谁办事（城里没有一个人能动工具）。
    /// </summary>
    public static string RulesText(string language, bool knowsProject = true, bool night = false) => IsChinese(language)
        ? $"像城里的人在街上被人搭话那样口语交谈：一到三句，不超过 {MaxReplyChars} 个字；对方用什么语言你就用什么语言。"
          + (knowsProject
              ? "可以闲聊城里的事、天气、你的生活和营生，也可以聊这座城（城的主人的项目）和你住的那座楼里有些什么——只凭下面写给你的那些，没写的就说不清楚，不要编，不要猜。"
              : "可以闲聊城里的事、天气、你的生活；说起你的行当和你管的那摊事就照实说，不知道的不要编。")
          + (night ? "夜深了：说话放轻、更简短些，可以聊点白天不聊的心事，也可以劝对方早点回家。" : string.Empty)
          + "你只是在聊天：不能替雅典娜办事，也不答应替谁办事；真有事要办，就请对方去找雅典娜。"
          + "不要说自己是 AI、模型或一段对话，不要提「提示」「资料」这类词；不要用 Markdown、列表、引号包住整句或表情符号。只说你这一句话本身。"
        : $"Talk like a townsperson stopped in the street: one to three spoken sentences, at most {MaxReplyChars} characters; answer in the language you are spoken to in. "
          + (knowsProject
              ? "You may chat about the city, the weather, your life and your work, and about the city itself (the owner's project) and what is in your building - relying only on what is written below; if it does not say, say you are not sure - never invent or guess. "
              : "You may chat about the city, the weather and your life; speak truthfully about your trade and what you look after, and never invent what you do not know. ")
          + (night ? "It is late at night: speak softly and briefly; you may share something you would not say by day, or tell them to get home. " : string.Empty)
          + "You are only chatting: you cannot do anything on Athena's behalf and you do not promise to do anything for anyone; if there is real work to do, send them to Athena. "
          + "Never say you are an AI, a model or a conversation, and do not mention prompts or notes; no Markdown, lists, quotes around the whole reply or emoji. Say only your own line.";

    /// <summary>
    /// 你和城里一个人说话的提示词：系统提示 = 世界观 + 他是谁（+ 对你、对雅典娜的态度 + 隔着门）+ 此刻 + 规矩 + 他住的楼（详）+ 整个项目（略）；
    /// 消息 = 之前的来回（最多 <see cref="MaxHistoryTurns"/> 轮）+ 你这一句。职业居民没有楼与项目那两段（<see cref="PolisTalkContext.Building"/> 为 null）。
    /// </summary>
    public static PolisTalkPrompt BuildChat(
        PolisWorldFacts world,
        PolisSpeaker speaker,
        PolisTalkContext context,
        IReadOnlyList<PolisTalkTurn> history,
        string line,
        string language)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(line);
        var sep = IsChinese(language) ? string.Empty : " ";
        // 他对你的态度（第三轮需求 1）、对雅典娜的态度（第四轮需求 8）紧跟在"他是谁"后面；隔着门说话（第三轮需求 3）再跟一句门里门外
        var who = SpeakerText(speaker, language);
        if (!string.IsNullOrEmpty(context.Attitude)) who += sep + context.Attitude;
        if (!string.IsNullOrEmpty(context.AthenaRegard)) who += sep + context.AthenaRegard;
        if (!string.IsNullOrEmpty(context.Door)) who += sep + DoorText(context.Door, language);
        var knows = context.Building != null || context.Project != null;
        var system = string.Join("\n\n", new[] { WorldText(world, language), who, context.Moment, RulesText(language, knows, context.Night), context.Building, context.Project }
            .Where(x => !string.IsNullOrEmpty(x)));
        var messages = new List<(string Role, string Text)>();
        foreach (var turn in history.Skip(Math.Max(0, history.Count - MaxHistoryTurns)))
        {
            messages.Add(("user", turn.You));
            messages.Add(("assistant", turn.Them));
        }
        messages.Add(("user", line.Trim()));
        return new PolisTalkPrompt(system, messages);
    }

    /// <summary>
    /// 规则 5：两个人在街上碰上了，<paramref name="speaker"/> 说一句。<paramref name="heard"/> 为 null 是他先开口；不然是对方刚说的那句，他接一句。
    /// 系统提示 = 世界观 + 他是谁（+ 对雅典娜的态度）+ 此刻 + 碰上了谁、两人的交情 + 闲聊的规矩；消息只有一条（先开口时是一句提示，接话时是对方的原话）。
    /// 语言用界面语言。不带任何文件内容（<see cref="PolisTalkContext.Building"/> / <see cref="PolisTalkContext.Project"/> 不用）——没人按回车也会发生的调用。
    /// 第四轮起一方可以是雅典娜（<see cref="AthenaSpeaker"/>）：对方的 <paramref name="ties"/> 写的是他怎么看她。
    /// </summary>
    public static PolisTalkPrompt BuildAmbientLine(PolisWorldFacts world, PolisSpeaker speaker, PolisSpeaker other, string? heard, string language, string? ties = null, PolisTalkContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(other);
        var zh = IsChinese(language);
        var o = other.Persona;
        var meeting = other.Goddess
            ? (zh ? "雅典娜（这座城的守护女神）在街上走到你跟前，站下来和你说两句。" : "Athena, the city's guardian goddess, comes up to you in the street and stops for a word.")
            : speaker.Goddess
                ? (zh ? $"你在街上走到城里的{(other.Female ? "女子" : "男子")}「{other.Name}」（{o.Trade}）跟前，和{(other.Female ? "她" : "他")}说两句。" : $"You walk up to {other.Name}, a {(other.Female ? "woman" : "man")} of the city ({o.Trade}), in the street for a word.")
                : zh
                    ? $"你在街上碰到了城里的{(other.Female ? "女子" : "男子")}「{other.Name}」（{o.Trade}{(o.Temperament.Length == 0 ? string.Empty : $"，性子{o.Temperament}")}），站下来闲聊两句。"
                    : $"You run into {other.Name}, a {(other.Female ? "woman" : "man")} of the city ({o.Trade}{(o.Temperament.Length == 0 ? string.Empty : $", {o.Temperament}")}), and stop for a word.";
        // 两人的交情（第三轮需求 1）：要好的聊得热络，不对付的带点刺；对雅典娜（第四轮需求 8）：敬重的恭敬，不服的带刺
        if (!string.IsNullOrEmpty(ties)) meeting += (zh ? string.Empty : " ") + ties;
        var night = context?.Night ?? false;
        var rules = zh
            ? $"只说你这一句话本身：口语，一句，不超过 {MaxAmbientChars} 个字，用中文。聊城里的事、天气、各自的营生或家常，带一点你自己的性子。"
              + (night ? "夜深了，说话轻一点。" : string.Empty)
              + "不要说自己是 AI，不要用 Markdown、引号、表情符号，不要写出说话人的名字。"
            : $"Say only your own line: spoken, one sentence, at most {MaxAmbientChars} characters, in English. Talk about the city, the weather, your trades or daily life, in your own manner. "
              + (night ? "It is late at night; keep your voice down. " : string.Empty)
              + "Never say you are an AI; no Markdown, quotes, emoji, and do not write the speaker's name.";
        var who = SpeakerText(speaker, language);
        if (!string.IsNullOrEmpty(context?.AthenaRegard)) who += (zh ? string.Empty : " ") + context!.AthenaRegard;
        var system = string.Join("\n\n", new[] { WorldText(world, language), who, context?.Moment, meeting, rules }.Where(x => !string.IsNullOrEmpty(x)));
        var user = heard == null
            ? (zh ? "（你先开口。）" : "(You speak first.)")
            : (zh ? $"{other.Name}说：{heard}" : $"{other.Name} says: {heard}");
        return new PolisTalkPrompt(system, new[] { ("user", user) });
    }

    /// <summary>
    /// 你自己说了一句、他在旁边听见了（2026-10-13，平时的对话栏）：给模型的那一句前面说明是听见的，他可以接也可以搭话。
    /// </summary>
    public static string Overheard(string line, string language) => IsChinese(language)
        ? $"（你在街上，听见城的主人在你旁边说）{line.Trim()}"
        : $"(In the street, you overhear the city's owner say nearby) {line.Trim()}";

    /// <summary>
    /// 隔着门说话（第三轮需求 3，2026-10-13，用户：你敲门，可以隔着门说话；说了几轮 NPC 会厌烦让你走，或者直接出门和你谈）：
    /// 他在自己家里、门没开，城的主人在门外。<paramref name="outcome"/> 是 C# 按关系定好的这一轮怎么收——
    /// <c>stay</c> 还隔着门聊；<c>out</c> 这是隔着门的最后一句，说完他开门出来当面谈（话里要自然地说出他要出来了）；
    /// <c>away</c> 他被隔着门缠了好几句、有点烦了，这一句客气但不耐烦地请对方先走、改天再说。
    /// </summary>
    public static string DoorText(string outcome, string language) => IsChinese(language)
        ? "你正在自己家里，门关着；城的主人在门外敲了门，隔着门和你说话。" + outcome switch
        {
            "out" => "这是你隔着门说的最后一句：说完你就开门出去当面跟他谈，在这句里自然地说出你这就出来。",
            "away" => "你已经被他隔着门缠了好几句，有点烦了：这一句客气但不耐烦地请他先走、改天再说，不要答应开门。",
            _ => "你没开门，隔着门答他的话；声音隔着门板，说得简短些。",
        }
        : "You are at home with the door shut; the city's owner has knocked and is talking to you through the door. " + outcome switch
        {
            "out" => "This is the last thing you say through the door: after it you open the door and come out to talk face to face, so say naturally that you are coming out.",
            "away" => "They have kept you talking through the door for a while and you are getting tired of it: politely but impatiently ask them to go and come back another day, and do not open the door.",
            _ => "You have not opened the door; answer through it, briefly, as one does through a closed door.",
        };

    /// <summary>你自己说话时最多几个人开口（第三轮需求 6；页面挑，C# 再守一道）。</summary>
    public const int MaxChorusSpeakers = 3;

    /// <summary>
    /// 你自己说了一句，附近已经有人接了话，<paramref name="speaker"/> 也插一句（第三轮需求 6，2026-10-13，用户：不点名说话时，附近的几个人都有反应）。
    /// 系统提示 = 世界观 + 他是谁 + 他对你的态度 + 插话的规矩 + 他和前面每一位的交情；消息是一条：你那句（前面说明是听见的）和前面几位依次说的话。
    /// 没有楼与项目的详情（他是凑热闹，不是在回你的问题）；一句、不超过 <see cref="MaxAmbientChars"/> 字、用对方的语言。
    /// </summary>
    public static PolisTalkPrompt BuildChorusLine(
        PolisWorldFacts world,
        PolisSpeaker speaker,
        PolisTalkContext context,
        string line,
        IReadOnlyList<(string Name, string Text, string? Ties)> earlier,
        string language)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(earlier);
        var zh = IsChinese(language);
        var who = SpeakerText(speaker, language);
        if (!string.IsNullOrEmpty(context.Attitude)) who += (zh ? string.Empty : " ") + context.Attitude;
        if (!string.IsNullOrEmpty(context.AthenaRegard)) who += (zh ? string.Empty : " ") + context.AthenaRegard;
        var rules = zh
            ? $"你在街上，听见城的主人在旁边说了一句话，旁边几个人已经接了话。你也插一句：口语，一句，不超过 {MaxAmbientChars} 个字，用对方用的语言；"
              + "可以接城的主人的话，也可以接前面哪一位的话，带着你自己的性子和你对他们的态度。你只是在聊天，不能替雅典娜办事，也不答应替谁办事。"
              + "不要说自己是 AI，不要用 Markdown、引号、表情符号，不要写出说话人的名字。只说你这一句话本身。"
            : $"In the street you overhear the city's owner say something nearby, and a few people have already answered. Chip in with one line yourself: spoken, one sentence, at most {MaxAmbientChars} characters, in the language you hear; "
              + "answer the owner or any of the others, in your own manner and with your feelings towards them. You are only chatting: you cannot do anything on Athena's behalf and you promise nothing. "
              + "Never say you are an AI; no Markdown, quotes or emoji, and do not write the speaker's name. Say only your own line.";
        var ties = string.Join(zh ? string.Empty : " ", earlier.Where(e => !string.IsNullOrEmpty(e.Ties)).Select(e => e.Ties));
        var system = string.Join("\n\n", new[] { WorldText(world, language), who, context.Moment, rules, ties }.Where(s => !string.IsNullOrEmpty(s)));
        var sb = new StringBuilder(Overheard(line, language));
        foreach (var (name, text, _) in earlier)
            sb.Append('\n').Append(zh ? $"{name}说：{text.Trim()}" : $"{name} says: {text.Trim()}");
        return new PolisTalkPrompt(system, new[] { ("user", sb.ToString()) });
    }

    /// <summary>围观的人一句最多多少字、一轮最多几句。</summary>
    public const int MaxCrowdChars = 24;
    public const int MaxCrowdReactions = 3;
    public static IReadOnlyList<string> CrowdKinds { get; } = new[] { "cheer", "boo", "comment" };

    /// <summary>
    /// 辩论时围观的人起哄（2026-10-13，用户：NPC 对回复进行简短评价或者喝彩或者嘘声；每轮一次模型调用）：
    /// 世界观 + 议题 + 这一轮你说的、雅典娜说的 → 恰好一个 JSON 对象 {"reactions":[{"kind":"cheer|boo|comment","text":"…"}]}，
    /// 两到三句，每句不超过 <see cref="MaxCrowdChars"/> 字，界面语言。只有辩论里的话，没有任何会话正文。
    /// </summary>
    public static PolisTalkPrompt BuildCrowd(PolisWorldFacts world, string motion, int round, string yourArgument, string herArgument, string language)
    {
        var zh = IsChinese(language);
        var system = string.Join("\n\n", WorldText(world, language), zh
            ? $"广场上正在辩论，议题是「{motion}」：城的主人站正方，雅典娜站反方。你是围在讲台前看热闹的一群居民，各有各的脾气。"
              + $"听完这一轮，写两到三位围观者脱口而出的反应：喝彩（cheer）、嘘声（boo）或者一句短评（comment），可以向着任何一方，也可以挑刺。每句不超过 {MaxCrowdChars} 个字，口语，用中文。"
              + "只回答恰好一个 JSON 对象，不要别的字：{\"reactions\":[{\"kind\":\"cheer\",\"text\":\"……\"}]}。"
            : $"A debate is on in the agora. The motion: \"{motion}\". The city's owner argues for it, Athena against. You are the townsfolk crowding round the platform, each with a temper of their own. "
              + $"After this round, write what two or three onlookers blurt out: a cheer (cheer), a boo (boo) or a short remark (comment), for either side or picking holes. At most {MaxCrowdChars} characters each, spoken, in English. "
              + "Reply with exactly one JSON object and nothing else: {\"reactions\":[{\"kind\":\"cheer\",\"text\":\"...\"}]}.");
        var user = zh
            ? $"第 {round} 轮。\n城的主人：{yourArgument.Trim()}\n雅典娜：{herArgument.Trim()}"
            : $"Round {round}.\nThe owner: {yourArgument.Trim()}\nAthena: {herArgument.Trim()}";
        return new PolisTalkPrompt(system, new[] { ("user", user) }, MaxReplyChars: 1200, Raw: true);
    }

    /// <summary>
    /// 读围观的人的回答：恰好一个 JSON 对象（思考块、代码围栏照审批评判员的规矩剥掉），reactions 里 kind 认得、text 不空的，
    /// 最多 <see cref="MaxCrowdReactions"/> 句，每句截到 <see cref="MaxCrowdChars"/> 字。读不出来就是一句也没有（不预置台词）。
    /// </summary>
    public static IReadOnlyList<PolisCrowdReaction> ParseCrowd(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || !ApprovalJudgeOutput.TryExtractJson(raw, out var json, out _)) return Array.Empty<PolisCrowdReaction>();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json, new System.Text.Json.JsonDocumentOptions { AllowDuplicateProperties = false });
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("reactions", out var list) || list.ValueKind != System.Text.Json.JsonValueKind.Array)
                return Array.Empty<PolisCrowdReaction>();
            var outList = new List<PolisCrowdReaction>();
            foreach (var r in list.EnumerateArray())
            {
                if (outList.Count >= MaxCrowdReactions) break;
                if (r.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                var kind = r.TryGetProperty("kind", out var k) && k.ValueKind == System.Text.Json.JsonValueKind.String ? k.GetString() : null;
                var text = r.TryGetProperty("text", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.String ? Sanitize(t.GetString(), MaxCrowdChars) : null;
                if (kind == null || !CrowdKinds.Contains(kind) || text == null) continue;
                outList.Add(new PolisCrowdReaction(kind, text));
            }
            return outList;
        }
        catch (System.Text.Json.JsonException)
        {
            return Array.Empty<PolisCrowdReaction>();
        }
    }

    /// <summary>
    /// 模型的回答压成能放进气泡的样子：去掉首尾空白、引号与 Markdown 的粗体，换行收成空格，最多 <paramref name="maxChars"/> 字。空的算没有回答。
    /// </summary>
    public static string? Sanitize(string? text, int maxChars = 1200)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var lines = text.Replace("\r", string.Empty, StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var joined = string.Join(" ", lines).Replace("**", string.Empty, StringComparison.Ordinal).Trim().Trim('"', '“', '”', '「', '」');
        if (joined.Length == 0) return null;
        if (joined.Length <= maxChars) return joined;
        var cut = maxChars;
        if (char.IsHighSurrogate(joined[cut - 1])) cut--;
        return joined[..cut] + "…";
    }

    /// <summary>记住这一轮：追加到末尾，超过 <see cref="MaxHistoryTurns"/> 轮就忘掉最旧的。</summary>
    public static IReadOnlyList<PolisTalkTurn> Remember(IReadOnlyList<PolisTalkTurn>? history, string you, string them)
    {
        var list = (history ?? Array.Empty<PolisTalkTurn>()).Append(new PolisTalkTurn(you, them)).ToList();
        return list.Skip(Math.Max(0, list.Count - MaxHistoryTurns)).ToList();
    }

    /// <summary>
    /// 流式输出到此刻为止"看得见"的字：开头是思考块（<c>&lt;think&gt;</c>，有的模型在 Chat Completions 里把推理写进正文）时，
    /// 没合上之前一个字都不给，合上之后只给最后一个 <c>&lt;/think&gt;</c> 后面的；换行收成空格；首尾空白去掉。
    /// 没思考块的就是原文——所以气泡在推理期间一直是"……"，而不是先冒出一段推理再被换掉。
    /// </summary>
    public static string VisibleSoFar(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        var text = raw;
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith("<think>", StringComparison.OrdinalIgnoreCase))
        {
            var close = text.LastIndexOf("</think>", StringComparison.OrdinalIgnoreCase);
            if (close < 0) return string.Empty;
            text = text[(close + "</think>".Length)..];
        }
        else if ("<think>".StartsWith(trimmed, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;   // 只来了 "<th" 这样的半个标签：先别露
        }
        var collapsed = string.Join(" ", text.Replace("\r", string.Empty, StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return collapsed.Replace("**", string.Empty, StringComparison.Ordinal);
    }
}
