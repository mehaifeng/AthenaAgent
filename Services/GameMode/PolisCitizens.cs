using Athena.UI.Models;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Athena.UI.Services.GameMode;

/// <summary>一位居民多大：按那场会话开始了多久（30 天内年轻，半年内壮年，再久年长）。</summary>
public enum PolisCitizenAge
{
    Young,
    Adult,
    Old
}

/// <summary>那场会话走到了哪一步（居民自己说的那一句按它挑）。</summary>
public enum PolisCitizenOutcome
{
    /// <summary>还在办（会话正在跑或排着队）。</summary>
    Running,
    /// <summary>定时任务开的会话。</summary>
    Scheduled,
    /// <summary>做到一半停下了（被打断）。</summary>
    Interrupted,
    /// <summary>至少完成了一回合。</summary>
    Completed,
    /// <summary>还没有完成的回合。</summary>
    Open
}

/// <summary>推导一位居民要的东西：会话的快照，和它的消息（只读工具调用的路径，正文一概不读）。</summary>
public sealed record PolisCitizenSource(PolisCommission Commission, IReadOnlyList<ChatMessage> Messages);

/// <summary>
/// 一位居民（M-B，2026-10-13）：一场会话就是城里的一个人。<see cref="Seed"/> 决定他的长相（页面的 art/citizens.mjs）、
/// 名字与说话的口吻；<see cref="Home"/> 是他住的地方——那场会话碰过最多的顶层文件夹（<c>b:&lt;文件夹&gt;</c>），
/// 没碰过文件夹的住在公共地方（广场一圈、市集、图书馆、神庙、港口）；<see cref="Folders"/> 是他常去的文件夹（最多三处）。
/// 只有名字、文件夹名、数字和日期，没有任何消息正文。<see cref="NameIndex"/> / <see cref="NameRound"/> 是他在希腊名表里的位置
/// （<see cref="PolisCitizens.AssignNames"/>：同一座城里不重名）。
/// </summary>
public sealed record PolisCitizen(
    string ConversationId,
    uint Seed,
    bool Female,
    string Title,
    string Epithet,
    string Home,
    IReadOnlyList<string> Folders,
    PolisCitizenAge Age,
    bool Elder,
    bool OnStreet,
    int Turns,
    PolisCitizenOutcome Outcome,
    int AgeDays,
    DateTimeOffset UpdatedAt,
    DateTimeOffset StartedAt,
    int NameIndex = 0,
    int NameRound = 0);

/// <summary>这座城的人口：一共几位，街上几位，屋里几位（街上满了），几位老居民（一年多没出门，名字刻在家门口的碑上）。</summary>
public sealed record PolisCensus(int Total, int OnStreet, int Indoors, int Elders);

/// <summary>
/// 居民即会话（M-B，用户 2026-10-12 定的方向 ①）：每场会话一位居民，从会话树推导、从不另存——删掉一场会话，他就不在了。
/// 街上同时最多 <see cref="StreetCap"/> 位（用户定的 32），按最近动过的先上街；其余的在屋里，人口里数得到；
/// 一年多没动过的是老居民，不上街，名字刻在他家门口的碑上。纯函数，<c>Athena.Archive.Tests</c> 的 <c>polis citizens</c> 覆盖。
/// </summary>
public static class PolisCitizens
{
    /// <summary>街上同时露面的居民上限（用户 2026-10-12 定：32）。人口不设上限，上限只在街上。</summary>
    public const int StreetCap = 32;
    public static readonly TimeSpan YoungFor = TimeSpan.FromDays(30);
    public static readonly TimeSpan AdultFor = TimeSpan.FromDays(180);
    /// <summary>这么久没动过的会话是老居民：不上街，名字刻在碑上。</summary>
    public static readonly TimeSpan ElderAfter = TimeSpan.FromDays(365);
    /// <summary>称号最长几个字：汉字 6 个，拉丁字母 12 个（名牌上放得下）。</summary>
    public const int MaxEpithetCjk = 6;
    public const int MaxEpithetLatin = 12;
    /// <summary>"常去的文件夹"最多列几处。</summary>
    public const int MaxFolders = 3;
    /// <summary>
    /// 希腊名表男女各多少个（<c>Polis.Citizen.NamesMale</c> / <c>NamesFemale</c>，中英两份一一对应——换界面语言，同一个人还是同一个名字）。
    /// <c>Athena.Archive.Tests</c> 核对两份 locale 的名表都正好这么长、没有重名。
    /// </summary>
    public const int NamesPerSex = 120;

    /// <summary>没有文件夹可归的居民住的公共地方（站点名与页面的 layout.sitePoint 一致），按种子分摊。</summary>
    public static IReadOnlyList<string> PublicHomes { get; } = new[]
    {
        "agora:0", "agora:1", "agora:2", "agora:3", "agora:4", "agora:5", "market", "library", "temple", "harbor"
    };

    /// <summary>默认的会话标题：没有称号可取，只用名字。</summary>
    private static readonly string[] UntitledTitles = { "新会话", "新对话", "新委托", "New conversation", "New chat", "Untitled" };

    /// <summary>标题开头的这些字不是"那件事"本身（"帮我整理合同" 的称号是 "整理合同"）。长的在前，免得 "请" 先吃掉 "请帮我" 的一半。</summary>
    private static readonly string[] LeadingFillers =
    {
        "请帮我", "帮我把", "麻烦你", "能不能", "我想要", "帮忙", "帮我", "麻烦", "如何", "怎么", "怎样", "为什么", "我想", "我要", "可以", "关于", "请你", "请", "把",
    };

    /// <summary>会话 Id 的 SHA-256 前四个字节（小端）：同一场会话永远是同一个人。</summary>
    public static uint SeedOf(string conversationId)
    {
        ArgumentNullException.ThrowIfNull(conversationId);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(conversationId), hash);
        return BinaryPrimitives.ReadUInt32LittleEndian(hash);
    }

    /// <summary>男女由种子的最低位定（页面的 citizens.mjs 用同一条规则，名字和长相对得上）。</summary>
    public static bool IsFemale(uint seed) => (seed & 1) == 1;

    /// <summary>
    /// 称号：标题里"那件事"的头一个词——去掉开头的标点和"帮我""请"这一类客套，取第一段连续的汉字（最多 6 个）或第一个拉丁词（最多 12 个字母）。
    /// 默认标题（新会话……）没有称号。
    /// </summary>
    public static string Epithet(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;
        var t = title.Trim();
        if (UntitledTitles.Any(u => string.Equals(u, t, StringComparison.OrdinalIgnoreCase))) return string.Empty;
        var i = 0;
        for (var guard = 0; guard < 8; guard++)
        {
            while (i < t.Length && !IsCjk(t[i]) && !char.IsLetterOrDigit(t[i])) i++;
            var rest = t[i..];
            var filler = LeadingFillers.FirstOrDefault(f => rest.StartsWith(f, StringComparison.Ordinal) && rest.Length > f.Length);
            if (filler == null) break;
            i += filler.Length;
        }
        if (i >= t.Length) return string.Empty;
        var sb = new StringBuilder();
        if (IsCjk(t[i]))
        {
            while (i < t.Length && IsCjk(t[i]) && sb.Length < MaxEpithetCjk) sb.Append(t[i++]);
        }
        else
        {
            while (i < t.Length && char.IsLetterOrDigit(t[i]) && sb.Length < MaxEpithetLatin) sb.Append(t[i++]);
        }
        return sb.ToString();
    }

    private static bool IsCjk(char c) => c is >= '一' and <= '鿿' or >= '㐀' and <= '䶿';

    /// <summary>
    /// 这座城的居民。<paramref name="workspaceRoot"/> 为 null 是神殿（全局对话）：没有文件夹，人人住公共地方。
    /// <paramref name="buildingKeys"/> 是城里独立建筑的顶层文件夹，<paramref name="marketKeys"/> 是并进市集成排摊位的那些——住在摊位的人家在市集。
    /// 结果按会话最后一次动过的时刻排（新的在前）。
    /// </summary>
    public static IReadOnlyList<PolisCitizen> Derive(
        IEnumerable<PolisCitizenSource> sources,
        string? workspaceRoot,
        string? homeDirectory,
        IReadOnlySet<string> buildingKeys,
        IReadOnlySet<string> marketKeys,
        DateTimeOffset now,
        bool ignoreCase = true)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(buildingKeys);
        ArgumentNullException.ThrowIfNull(marketKeys);
        var all = new List<PolisCitizen>();
        foreach (var source in sources)
        {
            var c = source.Commission;
            var seed = SeedOf(c.ConversationId);
            var folders = workspaceRoot == null
                ? Array.Empty<string>()
                : TouchedFolders(source.Messages, workspaceRoot, homeDirectory, buildingKeys, marketKeys, ignoreCase);
            var home = folders.Count == 0
                ? PublicHomes[(int)((seed >> 4) % (uint)PublicHomes.Count)]
                : marketKeys.Contains(folders[0]) && !buildingKeys.Contains(folders[0]) ? "market" : "b:" + folders[0];
            var started = now - c.StartedAt;
            var age = started < YoungFor ? PolisCitizenAge.Young : started < AdultFor ? PolisCitizenAge.Adult : PolisCitizenAge.Old;
            var outcome = c.Running ? PolisCitizenOutcome.Running
                : c.Scheduled ? PolisCitizenOutcome.Scheduled
                : c.Interrupted ? PolisCitizenOutcome.Interrupted
                : c.CompletedTurns > 0 ? PolisCitizenOutcome.Completed
                : PolisCitizenOutcome.Open;
            var elder = now - c.UpdatedAt > ElderAfter && !c.Running;
            all.Add(new PolisCitizen(
                c.ConversationId, seed, IsFemale(seed), c.Title, Epithet(c.Title), home,
                folders.Take(MaxFolders).ToList(), age, elder, OnStreet: false, c.CompletedTurns, outcome,
                Math.Max(0, (int)Math.Floor((now - c.UpdatedAt).TotalDays)), c.UpdatedAt, c.StartedAt));
        }

        var ordered = AssignNames(all)
            .OrderByDescending(c => c.UpdatedAt)
            .ThenBy(c => c.ConversationId, StringComparer.Ordinal)
            .ToList();
        var street = ordered.Where(c => !c.Elder).Take(StreetCap).Select(c => c.ConversationId).ToHashSet(StringComparer.Ordinal);
        return ordered.Select(c => street.Contains(c.ConversationId) ? c with { OnStreet = true } : c).ToList();
    }

    public static PolisCensus Census(IReadOnlyList<PolisCitizen> citizens)
    {
        ArgumentNullException.ThrowIfNull(citizens);
        var street = citizens.Count(c => c.OnStreet);
        var elders = citizens.Count(c => c.Elder);
        return new PolisCensus(citizens.Count, street, citizens.Count - street - elders, elders);
    }

    /// <summary>
    /// 那场会话里成功的工具调用碰过的顶层文件夹，按次数从多到少（同样多的按先碰到的先排）。只看参数里的路径（<see cref="PolisToolTargets"/>），
    /// 落在工作区外、或者不是一座建筑 / 市集摊位的，不算。
    /// </summary>
    public static IReadOnlyList<string> TouchedFolders(
        IReadOnlyList<ChatMessage> messages,
        string workspaceRoot,
        string? homeDirectory,
        IReadOnlySet<string> buildingKeys,
        IReadOnlySet<string> marketKeys,
        bool ignoreCase = true)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var counts = new Dictionary<string, (int Count, int First)>(StringComparer.Ordinal);
        var order = 0;
        foreach (var entry in messages
                     .Where(m => string.Equals(m.Role, "assistant", StringComparison.Ordinal))
                     .SelectMany(m => m.Segments)
                     .Where(s => s.IsToolCallGroup)
                     .SelectMany(s => s.ToolCalls))
        {
            if (entry.Status != ToolCallStatus.Success) continue;
            var (place, path) = PolisToolTargets.ResolvePath(PolisToolTargets.ExtractTargetPath(entry.Arguments), workspaceRoot, homeDirectory, ignoreCase);
            if (place != "inside" || string.IsNullOrEmpty(path)) continue;
            var slash = path.IndexOf('/');
            var top = slash < 0 ? path : path[..slash];
            if (!buildingKeys.Contains(top) && !marketKeys.Contains(top)) continue;
            counts[top] = counts.TryGetValue(top, out var seen) ? (seen.Count + 1, seen.First) : (1, order);
            order++;
        }
        return counts.OrderByDescending(kv => kv.Value.Count).ThenBy(kv => kv.Value.First).Select(kv => kv.Key).ToList();
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
    /// 一座城里的人起名字（2026-10-13，用户：完整的希腊名，不拿会话标题拼，同一座城里不重名，男女有别）。
    /// 按会话开始的先后（最老的先挑；同时开始的按会话 Id），每人先按种子挑一个首选名，被占了就按种子定的步长往后找一个空着的——
    /// 新来的会话永远不会改动老居民的名字。同性别的人多过名表，第二轮从头再分一遍，名字后面带上序号（<c>Polis.Citizen.NameNth</c>）。
    /// 删掉一位老居民，曾经因为他而让开首选名的后来者会拿回首选名——名字从会话树推导、从不另存，这是唯一会变名字的时候。
    /// </summary>
    public static IReadOnlyList<PolisCitizen> AssignNames(IEnumerable<PolisCitizen> citizens, int namesPerSex = NamesPerSex)
    {
        ArgumentNullException.ThrowIfNull(citizens);
        if (namesPerSex <= 0) throw new ArgumentOutOfRangeException(nameof(namesPerSex));
        var list = citizens.ToList();
        var taken = new Dictionary<(bool Female, int Round), HashSet<int>>();
        var assigned = new Dictionary<string, (int Index, int Round)>(StringComparer.Ordinal);
        foreach (var c in list.OrderBy(c => c.StartedAt).ThenBy(c => c.ConversationId, StringComparer.Ordinal))
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
                assigned[c.ConversationId] = (index, round);
                break;
            }
        }
        return list.Select(c => assigned.TryGetValue(c.ConversationId, out var n) ? c with { NameIndex = n.Index, NameRound = n.Round } : c).ToList();
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

    /// <summary>预览里最多带多少条消息、多少字（新的优先；再多的只告诉页面前面还有几条）。</summary>
    public const int TranscriptMaxMessages = 300;
    public const int TranscriptMaxChars = 120_000;

    /// <summary>
    /// 那场会话的预览（2026-10-13，用户：居民的卡片上能点开看那场会话，只显示你和雅典娜说的话，工具一概不显示）。
    /// 只取气泡里看得见的正文（<see cref="PolisCitizenAnswers.VisibleText"/>：用户消息的内容、雅典娜的正文段；隐藏的工具调用载体、
    /// 工具结果、系统消息、推理、工具参数都不要），从新往旧取到条数或字数用完为止，再按原来的先后排回去。
    /// </summary>
    public static (IReadOnlyList<(string Role, string Text)> Entries, int Omitted) Transcript(
        IReadOnlyList<ChatMessage> messages,
        int maxMessages = TranscriptMaxMessages,
        int maxChars = TranscriptMaxChars)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var visible = messages
            .Where(m => !m.IsHidden && m.Role is ("user" or "assistant"))
            .Select(m => (m.Role, Text: PolisCitizenAnswers.VisibleText(m)))
            .Where(e => e.Text.Length > 0)
            .ToList();
        var kept = new List<(string Role, string Text)>();
        var chars = 0;
        for (var i = visible.Count - 1; i >= 0 && kept.Count < maxMessages; i--)
        {
            var text = visible[i].Text;
            if (chars + text.Length > maxChars)
            {
                // 最新的那一条也放不下：留它的开头，免得预览一条都没有
                if (kept.Count == 0) kept.Add((visible[i].Role, text[..maxChars]));
                break;
            }
            chars += text.Length;
            kept.Add(visible[i]);
        }
        kept.Reverse();
        return (kept, visible.Count - kept.Count);
    }

    /// <summary>
    /// 他说的那几句（2–3 句，按种子挑说法）：那件事办到哪一步了、他常去哪个文件夹（没有就说来回谈了几次）、过去多久了。
    /// <paramref name="localize"/> 给 (locale 键, 回退)；句子里只填文件夹名、数字——<b>正文永远不进台词</b>。
    /// </summary>
    public static IReadOnlyList<string> Lines(PolisCitizen citizen, Func<string, string, string> localize)
    {
        ArgumentNullException.ThrowIfNull(citizen);
        ArgumentNullException.ThrowIfNull(localize);
        string Line(string key, int salt, params object[] args)
            => string.Format(CultureInfo.CurrentCulture, Pick(localize(key, LineFallbacks[key]), citizen.Seed, salt), args);
        var lines = new List<string>();
        lines.Add(citizen.Outcome switch
        {
            PolisCitizenOutcome.Running => Line("Polis.Citizen.Line.Running", 1),
            PolisCitizenOutcome.Scheduled => Line("Polis.Citizen.Line.Scheduled", 1),
            PolisCitizenOutcome.Interrupted => Line("Polis.Citizen.Line.Interrupted", 1),
            PolisCitizenOutcome.Completed => Line("Polis.Citizen.Line.Completed", 1),
            _ => Line("Polis.Citizen.Line.Open", 1),
        });
        if (citizen.Folders.Count > 0)
            lines.Add(Line("Polis.Citizen.Line.Folder", 2, citizen.Folders[0]));
        else if (citizen.Turns > 1)
            lines.Add(Line("Polis.Citizen.Line.Turns", 2, citizen.Turns));
        lines.Add(citizen.AgeDays < 1
            ? Line("Polis.Citizen.Line.Today", 3)
            : Line("Polis.Citizen.Line.Days", 3, citizen.AgeDays));
        return lines;
    }

    /// <summary>他那几句的 locale 键与中文回退（"|" 分隔几种说法；{0} 是文件夹名或数字）。</summary>
    public static IReadOnlyDictionary<string, string> LineFallbacks { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Polis.Citizen.Line.Running"] = "雅典娜正替我办着事呢。|我的事还在办。",
        ["Polis.Citizen.Line.Scheduled"] = "我是到了时辰就来报信的。|我每到时辰来一趟。",
        ["Polis.Citizen.Line.Interrupted"] = "那件事做到一半停下了。|还差一点没做完。",
        ["Polis.Citizen.Line.Completed"] = "那件事已经办完了。|托付的事办妥了。",
        ["Polis.Citizen.Line.Open"] = "我的事还没开头。|还没说上几句呢。",
        ["Polis.Citizen.Line.Folder"] = "我常去「{0}」那边。|「{0}」那一片我熟。|我住在「{0}」附近。",
        ["Polis.Citizen.Line.Turns"] = "那件事我们来回说了 {0} 回。|前前后后谈了 {0} 次。",
        ["Polis.Citizen.Line.Today"] = "就是今天的事。|今天刚来过。",
        ["Polis.Citizen.Line.Days"] = "那是 {0} 天前的事了。|一晃 {0} 天了。",
    };
}
