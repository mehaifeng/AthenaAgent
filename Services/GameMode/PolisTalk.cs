using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Athena.UI.Services.GameMode;

/// <summary>城里一个人的性子（2026-10-13，用户：NPC 要有各自的性格）。四样都从种子挑，同一个人永远同一副脾气。</summary>
public sealed record PolisPersona(string Trade, string Temperament, string Manner, string Pastime);

/// <summary>
/// 说话的人在这座城里是谁：名字、男女、年纪、性子；<see cref="Calling"/> 是他的身份（一位居民托付过的那件事）。
/// 职业居民（2026-10-13）用 <see cref="Portrait"/>：他的职业与他管的那摊事、他的性子，写好的一段，代替按种子挑的性子。
/// </summary>
public sealed record PolisSpeaker(string Name, bool Female, PolisCitizenAge Age, PolisPersona Persona, string Calling, string? Portrait = null);

/// <summary>这座城此刻的样子（提示词里的世界观）：城名、是不是神殿、白天还是夜里、多少居民、有哪些文件夹（建筑）。</summary>
public sealed record PolisWorldFacts(string CityName, bool Sanctuary, bool Night, int Population, IReadOnlyList<string> Buildings);

/// <summary>和他说过的话：你说的、他答的（一来一回为一轮）。</summary>
public sealed record PolisTalkTurn(string You, string Them);

/// <summary>一次说话调用要发的东西：系统提示与依次的消息（role 为 user / assistant）。</summary>
public sealed record PolisTalkPrompt(string System, IReadOnlyList<(string Role, string Text)> Messages);

/// <summary>
/// 城里的人怎么说话（2026-10-13，用户：和 NPC 说话时，他的上下文要有游戏世界的信息和他自己的性格，而不是只按他那场会话回答——
/// 所以能闲聊；回复出现在他头顶的气泡里）。纯函数：性子从种子挑，世界观、身份与记忆拼成提示词，流式输出里推理块之外的字是"看得见的"。
///
/// 规则（<c>Athena.Archive.Tests</c> 的 <c>polis talk</c> 逐条断言）：
/// 1. 性子按种子从四张表里各挑一样（职业、脾气、说话习惯、闲时爱做的事），中英两份表一一对应——换界面语言还是同一副脾气。
///    职业避开十位职业居民的行当（抄写员、铁匠、水手……），他们靠这个在远处就被认出来，居民不和他们撞行。
/// 2. 世界观只有城名、白天黑夜、人口、建筑（文件夹）名——没有任何消息正文；记忆只来自他那场会话里你看得见的片段
///    （<see cref="PolisCitizenAnswers.SelectExcerpts"/>），工具、推理一概不进。
/// 3. 说话的规矩：口语、一到三句、不超过 <see cref="MaxReplyChars"/> 字、用对方用的语言；可以闲聊；说到他那件事只凭记忆，
///    记忆里没有的就说记不清；不说自己是 AI，不用 Markdown、列表、表情。
/// 4. 记住的来回最多 <see cref="MaxHistoryTurns"/> 轮（旧的先忘）。
/// 5. 城里的人自己聊（2026-10-13，用户：NPC 之间的对话只用模型，不预置台词）：一人一句，各一次调用——先开口的那句、接话的那句
///    （带着对方刚说的）。这是没人按回车也会发生的调用，所以只带世界观、两人的名字与性子、那件事办到哪一步（<see cref="AmbientCalling"/>），
///    <b>不带会话标题、不带任何原话</b>；一句不超过 <see cref="MaxAmbientChars"/> 字。
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

    // —— 规则 1：性子的四张表（中、英一一对应） ——
    private static readonly (string Zh, string En)[] Trades =
    {
        ("陶工", "potter"), ("织工", "weaver"), ("渔夫", "fisher"), ("面包师", "baker"), ("葡萄农", "vine grower"), ("鞋匠", "cobbler"),
        ("石匠", "mason"), ("造船匠", "boatwright"), ("药草师", "herbalist"), ("牧羊人", "shepherd"), ("乐师", "musician"), ("香料商", "spice trader"),
        ("木匠", "carpenter"), ("养蜂人", "beekeeper"),
    };

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

    /// <summary>四张表各有几样（测试核对两种语言一一对应）。</summary>
    public static (int Trades, int Temperaments, int Manners, int Pastimes) TableSizes => (Trades.Length, Temperaments.Length, Manners.Length, Pastimes.Length);

    public static bool IsChinese(string language) => language.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    /// <summary>规则 1：种子 → 性子。四样用种子不同的几段位，彼此不相关。</summary>
    public static PolisPersona PersonaFor(uint seed, string language)
    {
        var zh = IsChinese(language);
        string Pick((string Zh, string En)[] table, int shift)
        {
            var row = table[(int)(((seed >> shift) ^ (seed * 2654435761u >> (shift + 3))) % (uint)table.Length)];
            return zh ? row.Zh : row.En;
        }
        return new PolisPersona(Pick(Trades, 3), Pick(Temperaments, 9), Pick(Manners, 15), Pick(Pastimes, 21));
    }

    /// <summary>一位居民的身份：他托雅典娜办过的那件事（标题、哪天开始、办到哪一步、常去哪儿）。只有标题、日期、文件夹名和数字。</summary>
    public static string CitizenCalling(PolisCitizen citizen, string language)
    {
        ArgumentNullException.ThrowIfNull(citizen);
        var zh = IsChinese(language);
        var date = citizen.StartedAt.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var title = string.IsNullOrWhiteSpace(citizen.Title) ? (zh ? "（没有标题）" : "(untitled)") : citizen.Title.Trim();
        var outcome = citizen.Outcome switch
        {
            PolisCitizenOutcome.Running => zh ? "雅典娜正在办" : "Athena is working on it now",
            PolisCitizenOutcome.Scheduled => zh ? "那是一件到时辰就办的定时差事" : "it is a scheduled errand that runs on the hour",
            PolisCitizenOutcome.Interrupted => zh ? "做到一半停下了" : "it stopped halfway",
            PolisCitizenOutcome.Completed => zh ? $"已经办完，来回了 {citizen.Turns} 回" : $"it is done, after {citizen.Turns} rounds",
            _ => zh ? "还没真正开头" : "it has not really started",
        };
        var where = citizen.Folders.Count == 0 ? null : string.Join(zh ? "、" : ", ", citizen.Folders);
        return zh
            ? $"你托雅典娜办过一件事——「{title}」，{date} 开始，{outcome}{(where == null ? string.Empty : $"，那阵子常在「{where}」一带")}。"
            : $"You once entrusted Athena with something - \"{title}\", begun on {date}; {outcome}{(where == null ? string.Empty : $"; in those days you were often around \"{where}\"")}.";
    }

    /// <summary>
    /// 规则 5：闲聊时的身份——只说他托雅典娜办过一件事、办到哪一步，<b>不带标题、日期、文件夹</b>（没人按回车也会发出去的调用，少带为好）。
    /// </summary>
    public static string AmbientCalling(PolisCitizen citizen, string language)
    {
        ArgumentNullException.ThrowIfNull(citizen);
        var zh = IsChinese(language);
        return citizen.Outcome switch
        {
            PolisCitizenOutcome.Running => zh ? "你托雅典娜办的一件事，她正在办。" : "Athena is working on something you entrusted to her.",
            PolisCitizenOutcome.Scheduled => zh ? "雅典娜替你按时辰办一件差事。" : "Athena runs an errand for you on a schedule.",
            PolisCitizenOutcome.Interrupted => zh ? "你托雅典娜办的一件事做到一半停下了。" : "Something you entrusted to Athena stopped halfway.",
            PolisCitizenOutcome.Completed => zh ? "你托雅典娜办的一件事已经办完了。" : "Something you entrusted to Athena is done.",
            _ => zh ? "你想托雅典娜办一件事，还没真正开头。" : "You meant to entrust Athena with something, but it has not really started.",
        };
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
                ? "这里是神殿岛，一座古希腊式的小城，住着那些不属于任何工作区的人。"
                : $"这里是古希腊式的城邦「{world.CityName}」。城就是用户的一个工作区：每座建筑是一个文件夹");
            if (!world.Sanctuary) sb.Append(buildings.Count == 0 ? "。" : $"（{string.Join("、", buildings)}……）。");
            sb.Append("城中央是广场，有讲台和日晷，常有人在讲台前辩论；还有市集、港口（船按时出海打听消息）、神庙、圆形的图书馆和橄榄林（每办完一件托付就多一棵橄榄树）。");
            sb.Append("雅典娜是这座城的守护女神，就住在城里，替城的主人（用户）办事；城里的人敬重她，也常托她办事。");
            sb.Append(CultureInfo.InvariantCulture, $"城里住着 {world.Population} 位居民。此刻是{(world.Night ? "夜里，家家户户点着灯" : "白天")}。");
            sb.Append("跟你说话的是这座城的主人，大家叫他「你」，不知道他的名字。");
            return sb.ToString();
        }
        var en = new StringBuilder();
        en.Append(world.Sanctuary
            ? "This is the sanctuary island, a small ancient Greek town where the people who belong to no workspace live. "
            : $"This is the ancient Greek polis \"{world.CityName}\". The city is one of the user's workspaces: every building is a folder"
              + (buildings.Count == 0 ? ". " : $" ({string.Join(", ", buildings)}...). "));
        en.Append("At its centre is the agora with a speaker's platform and a sundial, where people often debate; there are also a market, a harbour (a boat sails out on schedule to gather news), a temple, a round library and an olive grove (one more olive tree for every finished commission). ");
        en.Append("Athena is the city's guardian goddess; she lives in the city and works for its owner (the user). The townsfolk respect her and often ask her for help. ");
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
            ? $"你是这座城里的{(speaker.Female ? "女子" : "男子")}「{speaker.Name}」，{age}，以{p.Trade}为生。你性子{p.Temperament}，{p.Manner}，闲时爱{p.Pastime}。{speaker.Calling}"
            : $"You are {speaker.Name}, a {(speaker.Female ? "woman" : "man")} of this city, {age}, who works as a {p.Trade}. You are {p.Temperament}, {p.Manner}, and in your free time you enjoy {p.Pastime}. {speaker.Calling}";
    }

    /// <summary>
    /// 规则 3：说话的规矩。<paramref name="remembers"/> 为真（一位居民）时多一条：说到他托付过的那件事只凭记得的；
    /// 职业居民没有托付过的事，说起自己的行当照实说。谁都只是聊天——不能替雅典娜办事、也不答应替谁办事（城里没有一个人能动工具）。
    /// </summary>
    public static string RulesText(string language, bool remembers = true) => IsChinese(language)
        ? $"像城里的人在街上被人搭话那样口语交谈：一到三句，不超过 {MaxReplyChars} 个字；对方用什么语言你就用什么语言。"
          + (remembers
              ? "可以闲聊城里的事、天气、你的生活和你的行当。说到你托付过的那件事，只凭下面「你记得的」那几段；那里没有的就说记不清了，不要编，不要猜。"
              : "可以闲聊城里的事、天气、你的生活；说起你的行当和你管的那摊事就照实说，不知道的不要编。")
          + "你只是在聊天：不能替雅典娜办事，也不答应替谁办事；真有事要办，就请对方去找雅典娜。"
          + "不要说自己是 AI、模型或一段对话，不要提「片段」「记录」这类词；不要用 Markdown、列表、引号包住整句或表情符号。只说你这一句话本身。"
        : $"Talk like a townsperson stopped in the street: one to three spoken sentences, at most {MaxReplyChars} characters; answer in the language you are spoken to in. "
          + (remembers
              ? "You may chat about the city, the weather, your life and your trade. About the thing you entrusted to Athena, rely only on the passages under \"What you remember\"; if they do not say, say you cannot quite remember - never invent or guess. "
              : "You may chat about the city, the weather and your life; speak truthfully about your trade and what you look after, and never invent what you do not know. ")
          + "You are only chatting: you cannot do anything on Athena's behalf and you do not promise to do anything for anyone; if there is real work to do, send them to Athena. "
          + "Never say you are an AI, a model or a conversation, and do not mention passages or records; no Markdown, lists, quotes around the whole reply or emoji. Say only your own line.";

    /// <summary>"你记得的"：那场会话里挑出来的几段（空的也写明，免得模型以为漏了）。</summary>
    public static string MemoryText(IReadOnlyList<PolisCitizenExcerpt> excerpts, string language)
    {
        ArgumentNullException.ThrowIfNull(excerpts);
        var zh = IsChinese(language);
        var sb = new StringBuilder();
        sb.AppendLine(zh ? "你记得的（那件事里你和雅典娜说过的话）：" : "What you remember (what you and Athena said about it):");
        if (excerpts.Count == 0) sb.AppendLine(zh ? "（几乎什么都不记得了）" : "(almost nothing)");
        for (var i = 0; i < excerpts.Count; i++)
        {
            var e = excerpts[i];
            var who = e.Role == "user" ? (zh ? "你" : "You") : (zh ? "雅典娜" : "Athena");
            sb.Append(CultureInfo.InvariantCulture, $"[{i + 1}] {who}：").AppendLine(e.Text);
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// 你和一位居民说话的提示词：系统提示 = 世界观 + 他是谁 + 规矩 + 他记得的；消息 = 之前的来回（最多 <see cref="MaxHistoryTurns"/> 轮）+ 你这一句。
    /// <paramref name="memory"/> 为 null 是职业居民（没有托付过的事，也就没有"你记得的"那一段）。
    /// </summary>
    public static PolisTalkPrompt BuildChat(
        PolisWorldFacts world,
        PolisSpeaker speaker,
        IReadOnlyList<PolisCitizenExcerpt>? memory,
        IReadOnlyList<PolisTalkTurn> history,
        string line,
        string language)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(line);
        var system = memory == null
            ? string.Join("\n\n", WorldText(world, language), SpeakerText(speaker, language), RulesText(language, remembers: false))
            : string.Join("\n\n", WorldText(world, language), SpeakerText(speaker, language), RulesText(language), MemoryText(memory, language));
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
    /// 规则 5：两位居民在街上碰上了，<paramref name="speaker"/> 说一句。<paramref name="heard"/> 为 null 是他先开口；不然是对方刚说的那句，他接一句。
    /// 系统提示 = 世界观 + 他是谁 + 碰上了谁 + 闲聊的规矩；消息只有一条（先开口时是一句提示，接话时是对方的原话）。语言用界面语言。
    /// </summary>
    public static PolisTalkPrompt BuildAmbientLine(PolisWorldFacts world, PolisSpeaker speaker, PolisSpeaker other, string? heard, string language)
    {
        ArgumentNullException.ThrowIfNull(other);
        var zh = IsChinese(language);
        var o = other.Persona;
        var meeting = zh
            ? $"你在街上碰到了城里的{(other.Female ? "女子" : "男子")}「{other.Name}」（{o.Trade}，性子{o.Temperament}），站下来闲聊两句。"
            : $"You run into {other.Name}, a {(other.Female ? "woman" : "man")} of the city ({o.Trade}, {o.Temperament}), and stop for a word.";
        var rules = zh
            ? $"只说你这一句话本身：口语，一句，不超过 {MaxAmbientChars} 个字，用中文。聊城里的事、天气、各自的营生或家常，带一点你自己的性子。"
              + "不要说自己是 AI，不要用 Markdown、引号、表情符号，不要写出说话人的名字。"
            : $"Say only your own line: spoken, one sentence, at most {MaxAmbientChars} characters, in English. Talk about the city, the weather, your trades or daily life, in your own manner. "
              + "Never say you are an AI; no Markdown, quotes, emoji, and do not write the speaker's name.";
        var system = string.Join("\n\n", WorldText(world, language), SpeakerText(speaker, language), meeting, rules);
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
