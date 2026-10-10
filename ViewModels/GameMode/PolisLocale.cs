using System;
using System.Collections.Generic;
using System.Linq;

namespace Athena.UI.ViewModels.GameMode;

/// <summary>
/// 页面要的词条：旁白台词库（<c>Polis.Line.*</c>，"|" 分隔多个变体，做法和宠物台词库一样）、页面界面词条
/// （<c>Polis.Ui.*</c>）、建筑类型的叫法（<c>Polis.Place.*</c>）与公共建筑的名字（<c>Polis.Public.*</c>）。
/// 都在 locale 文件里；进入游戏、切换界面语言时由 C# 推给页面。这里的中文是 locale 缺词条时的回退，
/// 与页面自带的默认表（narration.mjs / game.mjs / world.mjs）一一对应。
/// </summary>
public static class PolisLocale
{
    /// <summary>（页面里的键, locale 键, 中文回退）</summary>
    public static IReadOnlyList<(string JsKey, string Key, string Fallback)> Lines { get; } = new[]
    {
        ("turn", "Polis.Line.Turn", "交给我吧|好，我来办|这就去办"),
        ("read.file", "Polis.Line.ReadFile", "我去{place}翻翻《{name}》|去{place}找《{name}》"),
        ("read.file.at", "Polis.Line.ReadFileAt", "在{place}查查《{name}》|打开《{name}》看看"),
        ("read.dir", "Polis.Line.ReadDir", "我去{place}看看|去{place}里找找"),
        ("read.dir.at", "Polis.Line.ReadDirAt", "在{place}里找找|翻翻{place}里有什么"),
        ("write.file", "Polis.Line.WriteFile", "去{place}写《{name}》|我去誊写《{name}》"),
        ("write.file.at", "Polis.Line.WriteFileAt", "我在誊写《{name}》|在{place}落笔写《{name}》"),
        ("write.dir", "Polis.Line.WriteDir", "去{place}收拾一下|{place}要搭个脚手架"),
        ("write.dir.at", "Polis.Line.WriteDirAt", "我在{place}收拾一下|{place}搭起了脚手架"),
        ("terminal", "Polis.Line.Terminal", "去锻炉敲打一下|我去锻炉一趟"),
        ("terminal.at", "Polis.Line.TerminalAt", "炉火点上了|炉火正旺"),
        ("web", "Polis.Line.Web", "去港口放船|我去港口一趟"),
        ("web.at", "Polis.Line.WebAt", "船出海打听消息去了|放船出海，去外面问问"),
        ("memory.recall", "Polis.Line.MemoryRecall", "去图书馆翻翻旧卷|看看我记过些什么"),
        ("memory.write", "Polis.Line.MemoryWrite", "这件事我记下了"),
        ("subagents", "Polis.Line.Subagents", "我叫上了{count}位侍女|侍女们，分头去吧"),
        ("workshop", "Polis.Line.Workshop", "去工坊忙一下"),
        ("outside", "Polis.Line.Outside", "我出城一趟|这一样东西在城外"),
        ("failure", "Polis.Line.Failure", "这条路走不通，我换一条"),
        ("deliver", "Polis.Line.Deliver", "做好了，你看看|给你，刚做好的"),
        ("approval", "Polis.Line.Approval", "这一步需要你点头|等你点个头，我再动手"),
        ("interrupted", "Polis.Line.Interrupted", "上次在这里停下了，要接着做吗？"),
    };

    public static IReadOnlyList<(string JsKey, string Key, string Fallback)> Ui { get; } = new[]
    {
        ("dismiss", "Polis.Ui.Dismiss", "知道了"),
        ("noticeBoard", "Polis.Ui.NoticeBoard", "广场公告板"),
        ("fogTitle", "Polis.Ui.FogTitle", "找不到这座城邦的文件夹"),
        ("fogBody", "Polis.Ui.FogBody", "文件夹被移动、改名或删除了。重新定位它，城邦和存档都还在。"),
        ("relocate", "Polis.Ui.Relocate", "重新定位"),
        ("read", "Polis.Ui.Read", "读"),
        ("open", "Polis.Ui.Open", "打开"),
        ("accept", "Polis.Ui.Accept", "收下"),
        ("return", "Polis.Ui.Return", "退回"),
        ("kindScroll", "Polis.Ui.KindScroll", "卷轴"),
        ("kindLedger", "Polis.Ui.KindLedger", "账册"),
        ("kindPainting", "Polis.Ui.KindPainting", "彩绘板"),
        ("kindAnswer", "Polis.Ui.KindAnswer", "回答"),
        ("statePending", "Polis.Ui.StatePending", "等你收下"),
        ("stateAccepted", "Polis.Ui.StateAccepted", "已收下"),
        ("stateReturned", "Polis.Ui.StateReturned", "已退回"),
        ("stateLost", "Polis.Ui.StateLost", "找不到它指向的文件了"),
        ("stateModified", "Polis.Ui.StateModified", "交付之后被改过"),
        ("founding", "Polis.Ui.Founding", "这是你的城邦，每座建筑都是你的一个文件夹"),
        ("close", "Polis.Ui.Close", "收起"),
        ("followAthena", "Polis.Ui.FollowAthena", "镜头：跟着雅典娜"),
        ("followPlayer", "Polis.Ui.FollowPlayer", "镜头：跟着你"),
        ("followFree", "Polis.Ui.FollowFree", "镜头：自由"),
    };

    public static IReadOnlyList<(string JsKey, string Key, string Fallback)> Places { get; } = new[]
    {
        ("stoaLibrary", "Polis.Place.StoaLibrary", "书库"),
        ("house", "Polis.Place.House", "民居"),
        ("workshop", "Polis.Place.Workshop", "作坊"),
        ("sculptureGarden", "Polis.Place.SculptureGarden", "雕塑园"),
        ("treasury", "Polis.Place.Treasury", "金库"),
        ("warehouse", "Polis.Place.Warehouse", "仓库"),
        ("agora", "Polis.Place.Agora", "广场"),
        ("market", "Polis.Place.Market", "市集"),
    };

    public static IReadOnlyList<(string JsKey, string Key, string Fallback)> PublicNames { get; } = new[]
    {
        ("agora", "Polis.Public.Agora", "广场"),
        ("temple", "Polis.Public.Temple", "神庙"),
        ("library", "Polis.Public.Library", "图书馆"),
        ("forge", "Polis.Public.Forge", "锻炉"),
        ("harbor", "Polis.Public.Harbor", "港口"),
        ("market", "Polis.Public.Market", "市集"),
        ("gate", "Polis.Public.Gate", "城门"),
        ("sanctuary", "Polis.Public.Sanctuary", "雅典娜的神殿"),
    };

    /// <summary>全部 locale 键（测试核对中英两份 locale 都有这些词条）。</summary>
    public static IEnumerable<string> AllKeys => Lines.Concat(Ui).Concat(Places).Concat(PublicNames).Select(e => e.Key);

    /// <summary>按当前语言组装给页面的 <c>init</c> 表。</summary>
    public static Dictionary<string, Dictionary<string, string>> BuildTables(Func<string, string, string> localize)
    {
        ArgumentNullException.ThrowIfNull(localize);
        static Dictionary<string, string> Table(IEnumerable<(string JsKey, string Key, string Fallback)> entries, Func<string, string, string> l)
            => entries.ToDictionary(e => e.JsKey, e => l(e.Key, e.Fallback), StringComparer.Ordinal);
        return new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal)
        {
            ["lines"] = Table(Lines, localize),
            ["ui"] = Table(Ui, localize),
            ["places"] = Table(Places, localize),
            ["publicNames"] = Table(PublicNames, localize),
        };
    }
}
