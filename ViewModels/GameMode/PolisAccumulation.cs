using Athena.UI.Models;
using Athena.UI.Services.GameMode;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Athena.UI.ViewModels.GameMode;

/// <summary>
/// 积累系统在视图模型这一层的投影（M2）：把会话树上的会话投影成 <see cref="PolisCommission"/>（服务层的纯函数只认领域类型，
/// 评审规则 4），再把纯函数的结果（橄榄林、居民、委托板、神谕、海图、传令官）按界面语言拼成推给页面的那几份数据。
/// 规则都在 <c>Services/GameMode</c>，那里有断言；这里只有投影与文字，不存任何状态——<see cref="GameModeViewModel"/>
/// 拿结果去推送、去响应意图。从游戏视图模型里挪出来，是为了不把它越堆越大（评审规则 9）。
/// </summary>
public static class PolisAccumulation
{
    /// <summary>会话 → 委托快照（不读正文：只数完成了几回合、取第一条消息的时间）。</summary>
    public static IReadOnlyList<PolisCommission> ProjectCommissions(IEnumerable<ConversationSessionItemViewModel> sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        var list = new List<PolisCommission>();
        foreach (var s in sessions)
        {
            var messages = s.Chat.Messages.ToList();
            var updated = new DateTimeOffset(s.UpdatedAt);
            list.Add(new PolisCommission(
                s.ConversationId,
                string.IsNullOrEmpty(s.Workspace?.Id) ? null : s.Workspace!.Id,
                s.Title,
                PolisCommissions.StartedAt(messages, updated),
                updated,
                PolisCommissions.CompletedTurns(messages),
                s.IsRunning || s.IsQueued,
                s.IsWaitingForApproval,
                s.WasInterrupted,
                s.CreatedByCronTaskId));
        }
        return list;
    }

    /// <summary>一份快照的指纹：变了才重算居民、委托板这些（每两秒看一眼，不变就什么都不推）。</summary>
    public static string Fingerprint(IReadOnlyList<PolisCommission> commissions, params object?[] extra)
    {
        var builder = new StringBuilder();
        foreach (var c in commissions.OrderBy(c => c.ConversationId, StringComparer.Ordinal))
        {
            builder.Append(c.ConversationId).Append(':').Append(c.CompletedTurns).Append(c.Running ? 'r' : '-').Append(c.WaitingForApproval ? 'w' : '-')
                .Append(c.Interrupted ? 'i' : '-').Append(c.UpdatedAt.UtcTicks).Append(c.Title.Length).Append(';');
        }
        foreach (var e in extra) builder.Append('|').Append(e);
        return builder.ToString();
    }

    public static IReadOnlyList<object> GroveTrees(IReadOnlyList<PolisTree> trees)
        => trees.Select(t => (object)new
        {
            conversationId = t.ConversationId,
            title = t.Title,
            index = t.Index,
            stage = (int)t.Stage,
            turns = t.Turns,
            interrupted = t.Interrupted,
            scheduled = t.Scheduled
        }).ToList();

    /// <summary>
    /// 十位职业居民（2026-10-13 起每座城一开始就都在）：职业、希腊名、称呼（职业加名字）、他讲的话（按日期在几种说法里轮换），
    /// 以及你和他说过话没有。没有示范请求了——工具能力撤下了。
    /// </summary>
    public static IReadOnlyList<object> Residents(IReadOnlyCollection<string> met, Func<string, string, string> localize, DateTimeOffset now)
    {
        var day = now.LocalDateTime.DayOfYear;
        var titlePattern = localize("Polis.Ui.ResidentTitle", "{trade}{name}");
        return PolisResidents.All.Select(kind =>
        {
            var entry = ResidentEntry(kind);
            var variants = localize(entry.IntroKey, entry.IntroFallback).Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var trade = localize(entry.NameKey, entry.NameFallback);
            var greek = localize(entry.GreekKey, entry.GreekFallback);
            var slug = PolisResidents.Slug(kind);
            return (object)new
            {
                kind = slug,
                met = met.Contains(slug, StringComparer.Ordinal),
                name = titlePattern.Replace("{trade}", trade, StringComparison.Ordinal).Replace("{name}", greek, StringComparison.Ordinal),
                trade,
                greekName = greek,
                intro = variants.Length == 0 ? string.Empty : variants[day % variants.Length]
            };
        }).ToList();
    }

    /// <summary>一位职业居民在 locale 表里的那一行。</summary>
    public static (string Kind, string NameKey, string NameFallback, string GreekKey, string GreekFallback, string IntroKey, string IntroFallback, string PersonaKey, string PersonaFallback) ResidentEntry(PolisResidentKind kind)
        => PolisLocale.Residents.First(r => r.Kind == PolisResidents.Slug(kind));

    /// <summary>街上满了、在屋里的居民最多列这么多位（敲门叫得出来的那些；再多的只在人口里数得到）。</summary>
    public const int MaxIndoors = 400;

    /// <summary>
    /// 城里的人（第四轮，2026-10-15：从建筑来）推给页面的那一份：白天街上的（最多 <see cref="PolisCitizens.StreetCap"/> 位）与屋里的（敲门叫得出来），
    /// 每人带 Id、长相的种子、男女、名字、家（站点名）、年纪、职业（键与名字）、脾气、住处的名字（文件夹名、"某某一家"、摊位）、夜里出不出门；以及人口。
    /// 没有任何会话的东西。<paramref name="homeNames"/>：住所的键 → 名牌上的名字（村舍是户名）。
    /// </summary>
    public static object Citizens(IReadOnlyList<PolisCitizen> citizens, Func<string, string, string> localize, IReadOnlyDictionary<string, string> homeNames, string language)
    {
        ArgumentNullException.ThrowIfNull(citizens);
        ArgumentNullException.ThrowIfNull(localize);
        ArgumentNullException.ThrowIfNull(homeNames);
        var table = PolisLocale.Citizen.ToDictionary(c => c.JsKey, StringComparer.Ordinal);
        string L(string jsKey) => localize(table[jsKey].Key, table[jsKey].Fallback);
        var male = L("namesMale");
        var female = L("namesFemale");
        var nth = L("nameNth");
        var zh = PolisTalk.IsChinese(language);
        var census = PolisCitizens.Census(citizens);
        object Entry(PolisCitizen c)
        {
            var trade = PolisTrades.ByKey(c.Trade);
            return new
            {
                id = c.Id,
                seed = c.Seed,
                female = c.Female,
                name = PolisCitizens.NameFor(c, male, female, nth),
                home = c.Home,
                age = c.Age.ToString().ToLowerInvariant(),
                // 他的职业（第四轮需求 5）：页面按它挑他干的活、在哪儿干
                trade = c.Trade,
                tradeName = zh ? trade.Zh : trade.En,
                // 农夫、牧人的工位号（PolisCitizens 规则 7）：页面按它分田、分垄、分圈里的位置，两人不站一处
                workSlot = c.WorkSlot,
                temper = PolisTalk.TemperamentName(c.Seed, language),
                homeName = homeNames.TryGetValue(c.Dwelling, out var n) ? n : c.Dwelling,
                stall = c.Home == "market",
                cottage = c.Cottage,
                nightOwl = c.NightOwl,
            };
        }
        return new
        {
            street = citizens.Where(c => c.OnStreet).Select(Entry).ToList(),
            // 街上满了、在屋里的（敲他家的门叫得出来）
            indoors = citizens.Where(c => !c.OnStreet).Take(MaxIndoors).Select(Entry).ToList(),
            census = new { total = census.Total, street = census.OnStreet, indoors = census.Indoors }
        };
    }

    /// <summary>委托板：每条建议在板上的一句话，以及采纳时预填的请求（Id → 请求；神谕那一条走神谕的请求）。</summary>
    public static (IReadOnlyList<object> Lines, IReadOnlyDictionary<string, string> Prompts) Board(
        IReadOnlyList<PolisSuggestion> suggestions, Func<string, string, string> localize, string? oracleRequest)
    {
        var table = PolisLocale.Board.ToDictionary(b => b.Kind, StringComparer.Ordinal);
        var lines = new List<object>();
        var prompts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var s in suggestions)
        {
            var entry = table[s.Kind.ToString()];
            string Fill(string template) => string.Format(CultureInfo.CurrentCulture, template, s.Subject ?? string.Empty, s.Count, s.AgeDays);
            lines.Add(new { id = s.Id, line = Fill(localize(entry.LineKey, entry.LineFallback)) });
            prompts[s.Id] = s.Kind == PolisSuggestionKind.SetOracle && oracleRequest != null
                ? oracleRequest
                : Fill(localize(entry.PromptKey, entry.PromptFallback));
        }
        return (lines, prompts);
    }

    /// <summary>神谕的请求（预填进新委托的输入框；由你按发送，雅典娜调用 create_task 时审批照常弹出）。</summary>
    public static string OracleRequest(WorkspaceProfile? workspace, Func<string, string, string> localize)
    {
        var request = PolisLocale.Oracle.First(o => o.Key == "Polis.Oracle.Request");
        var global = PolisLocale.Oracle.First(o => o.Key == "Polis.Oracle.RequestGlobal");
        return workspace == null
            ? localize(global.Key, global.Fallback)
            : string.Format(CultureInfo.CurrentCulture, localize(request.Key, request.Fallback), workspace.Name, workspace.Id);
    }

    /// <summary>神庙面板的状态：设没设、什么时候、下一卷什么时候、最近一卷在哪次会话里。</summary>
    public static object Oracle(CronTask? oracle, Func<string, string, string> localize)
    {
        string L(string key) => localize(key, PolisLocale.Oracle.FirstOrDefault(o => o.Key == key).Fallback ?? key);
        if (oracle == null)
            return new { exists = false, title = localize("Polis.Ui.OracleTitle", "Daily oracle"), body = localize("Polis.Ui.OracleNone", "") };
        var latest = PolisOracle.LatestScroll(oracle);
        var next = oracle.NextOccurrence?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "—";
        var body = oracle.IsEnabled
            ? string.Format(CultureInfo.CurrentCulture, L("Polis.Oracle.Set"), oracle.CronExpression, next)
            : L("Polis.Oracle.Paused");
        return new { exists = true, title = oracle.Name, body, latestConversationId = latest?.ConversationId };
    }

    public static IReadOnlyList<object> Islands(IReadOnlyList<PolisIsland> islands)
        => islands.Select(i => (object)new
        {
            id = i.Id,
            name = i.Name,
            order = i.Order,
            sanctuary = i.IsSanctuary,
            running = i.Running,
            waiting = i.WaitingForApproval,
            commissions = i.Commissions,
            current = i.Current,
            missing = i.FolderMissing
        }).ToList();

    /// <summary>传令官的每一行：一句话，可点的带会话 Id（或神谕送来的那一卷的藏品 Id）。</summary>
    public static IReadOnlyList<object> HeraldLines(IReadOnlyList<PolisHeraldEntry> entries, Func<string, string, string> localize)
    {
        var table = PolisLocale.Herald.ToDictionary(h => h.Kind, StringComparer.Ordinal);
        return entries.Select(e =>
        {
            var entry = table[e.Kind.ToString()];
            var second = e.Kind is PolisHeraldKind.BuildingGrew or PolisHeraldKind.BuildingShrank ? e.Count.ToString(CultureInfo.CurrentCulture) : e.Detail ?? string.Empty;
            // 改名沿用 M1 的词条："「旧名」改名为「新名」"
            var (a, b) = e.Kind == PolisHeraldKind.BuildingRenamed ? (e.Detail ?? string.Empty, e.Subject ?? string.Empty) : (e.Subject ?? string.Empty, second);
            return (object)new
            {
                text = string.Format(CultureInfo.CurrentCulture, localize(entry.Key, entry.Fallback), a, b),
                conversationId = e.ConversationId,
                itemId = e.ItemId
            };
        }).ToList();
    }

    /// <summary>神谕送来的一卷的标题："每日神谕 · 10月11日"。</summary>
    public static string OracleItemTitle(DateTimeOffset at, Func<string, string, string> localize)
    {
        var entry = PolisLocale.Oracle.First(o => o.Key == "Polis.Oracle.ItemTitle");
        return string.Format(CultureInfo.CurrentCulture, localize(entry.Key, entry.Fallback), at.ToLocalTime().ToString("M", CultureInfo.CurrentCulture));
    }
}
