using System;
using System.Collections.Generic;
using System.Linq;

namespace Athena.UI.ViewModels.GameMode;

/// <summary>
/// 页面要的词条：页面界面词条（<c>Polis.Ui.*</c>）、建筑类型的叫法（<c>Polis.Place.*</c>）与公共建筑的名字
/// （<c>Polis.Public.*</c>）。都在 locale 文件里；进入游戏、切换界面语言时由 C# 推给页面。这里的中文是 locale
/// 缺词条时的回退，与页面自带的默认表（game.mjs / world.mjs）一一对应。
/// 旁白台词库（<c>Polis.Line.*</c>）连同直播一起拿掉了（2026-10-13）：雅典娜不再镜像回合，也就没有按工具类别的旁白。
/// </summary>
public static class PolisLocale
{
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
        // 视角与操作（2026-10-11：自由视角是缺省，WASD / 方向键走路；M3 第三轮：走近人或物件按 E 互动）
        ("viewLabel", "Polis.Ui.ViewLabel", "视角"),
        ("viewFree", "Polis.Ui.ViewFree", "自由"),
        ("viewPlayer", "Polis.Ui.ViewPlayer", "跟着你"),
        ("viewAthena", "Polis.Ui.ViewAthena", "跟着雅典娜"),
        ("viewFreeTip", "Polis.Ui.ViewFreeTip", "左键拖动转视角，右键拖动平移，滚轮缩放；你走远了镜头会跟上"),
        ("controlsHint", "Polis.Ui.ControlsHint", "WASD / 方向键走动（按住 Shift 跑）· 拖动转视角 · 走近人或物件按 E 互动 · Enter 说话（和雅典娜说话请走到她身边按 E）"),
        ("focusHint", "Polis.Ui.FocusHint", "点一下画面，就能用键盘走动"),
        ("athenaHere", "Polis.Ui.AthenaHere", "雅典娜在这边"),
        // 走近按 E（M3 第三轮：互动从鼠标点击改为走到跟前按交互键）
        ("interactKey", "Polis.Ui.InteractKey", "E"),
        ("interactTalk", "Polis.Ui.InteractTalk", "和雅典娜说话"),
        ("interactResident", "Polis.Ui.InteractResident", "和{name}说话"),
        ("interactItem", "Polis.Ui.InteractItem", "看看「{title}」"),
        ("interactPlace", "Polis.Ui.InteractPlace", "摆在这里"),
        ("interactTree", "Polis.Ui.InteractTree", "去「{title}」这份委托"),
        ("interactBoard", "Polis.Ui.InteractBoard", "看看委托板"),
        ("interactOracle", "Polis.Ui.InteractOracle", "看看每日神谕"),
        ("interactVenue", "Polis.Ui.InteractVenue", "进{venue}看看"),
        // 城里的对话栏（游戏盖住了输入框）
        ("talkName", "Polis.Ui.TalkName", "雅典娜"),
        ("talkToAthena", "Polis.Ui.TalkToAthena", "和雅典娜说话"),
        ("talkPlaceholder", "Polis.Ui.TalkPlaceholder", "想请雅典娜做什么？"),
        ("talkSend", "Polis.Ui.TalkSend", "托付"),
        ("talkStop", "Polis.Ui.TalkStop", "请她停下"),
        ("talkHint", "Polis.Ui.TalkHint", "Enter 托付 · Shift+Enter 换行 · Esc 收起"),
        ("talkIdle", "Polis.Ui.TalkIdle", "她在听"),
        ("talkBusy", "Polis.Ui.TalkBusy", "她在办你交代的事——可以先写下一件"),
        ("talkQueued", "Polis.Ui.TalkQueued", "排在后面，前一件做完就开始"),
        ("talkWaiting", "Polis.Ui.TalkWaiting", "她在等你点头：看看审批窗口"),
        ("talkCompressing", "Polis.Ui.TalkCompressing", "她在整理记忆，稍等一下"),
        ("talkAttachments", "Polis.Ui.TalkAttachments", "一并交给她：{names}"),
        // M2
        ("place", "Polis.Ui.Place", "摆放"),
        ("move", "Polis.Ui.Move", "换个地方"),
        ("takeDown", "Polis.Ui.TakeDown", "撤下"),
        ("remove", "Polis.Ui.Remove", "移除"),
        ("placeHint", "Polis.Ui.PlaceHint", "走到一个空底座前按 E，把《{title}》摆在那里"),
        ("placeDefault", "Polis.Ui.PlaceDefault", "放进{venue}"),
        ("cancel", "Polis.Ui.Cancel", "取消"),
        ("storedIn", "Polis.Ui.StoredIn", "摆在{venue}"),
        ("storedNowhere", "Polis.Ui.StoredNowhere", "收着，还没摆出来"),
        ("venueCount", "Polis.Ui.VenueCount", "{count} / {total} 个底座摆了藏品"),
        ("venueStored", "Polis.Ui.VenueStored", "收着的藏品"),
        ("venueEmpty", "Polis.Ui.VenueEmpty", "还没有藏品。收下的成果会摆到这里。"),
        ("placeHere", "Polis.Ui.PlaceHere", "摆进这里"),
        ("boardTitle", "Polis.Ui.BoardTitle", "委托板"),
        ("boardHint", "Polis.Ui.BoardHint", "这里有几件可以交给我的事："),
        ("takeSuggestion", "Polis.Ui.TakeSuggestion", "交给雅典娜"),
        ("oracleTitle", "Polis.Ui.OracleTitle", "每日神谕"),
        ("oracleNone", "Polis.Ui.OracleNone", "在神庙设立每日神谕：每天早上，雅典娜回顾这座城的变化，传令官把晨报送到你手上。设立时需要你在审批窗口里点头。"),
        ("requestOracle", "Polis.Ui.RequestOracle", "请雅典娜设立"),
        ("latestScroll", "Polis.Ui.LatestScroll", "看最近一卷"),
        // 职业居民的称呼：职业加希腊名（2026-10-13）
        ("residentTitle", "Polis.Ui.ResidentTitle", "{trade}{name}"),
        ("chart", "Polis.Ui.Chart", "海图"),
        ("chartTitle", "Polis.Ui.ChartTitle", "群岛海图"),
        ("chartHint", "Polis.Ui.ChartHint", "一座岛是一座城邦。点一座岛，驶向它最近的那份委托。"),
        ("chartRunning", "Polis.Ui.ChartRunning", "有委托在进行"),
        ("chartWaiting", "Polis.Ui.ChartWaiting", "等你点头"),
        ("chartHere", "Polis.Ui.ChartHere", "你在这里"),
        ("chartMissing", "Polis.Ui.ChartMissing", "找不到文件夹"),
        // M-B（2026-10-13）：居民即会话
        ("interactCitizen", "Polis.Ui.InteractCitizen", "和{name}说话"),
        ("citizenOpen", "Polis.Ui.CitizenOpen", "去看那份委托"),
        ("censusLine", "Polis.Ui.CensusLine", "这座城里住着 {total} 位居民，街上有 {street} 位"),
        ("interactStele", "Polis.Ui.InteractStele", "读碑上的名字"),
        ("steleTitle", "Polis.Ui.SteleTitle", "老居民"),
        ("steleBody", "Polis.Ui.SteleBody", "一年多没出门的居民，名字刻在这里："),
        // 你自己说话（2026-10-13：平时的对话栏；和雅典娜说话要走到她身边按 E）
        ("interactTalkWaiting", "Polis.Ui.InteractTalkWaiting", "和雅典娜说话（有一段写好的话等你）"),
        ("speakName", "Polis.Ui.SpeakName", "你"),
        ("speakIdle", "Polis.Ui.SpeakIdle", "说点什么，附近的人会听见"),
        ("speakAthenaBusy", "Polis.Ui.SpeakAthenaBusy", "雅典娜在办事——要和她说话，走到她身边按 E"),
        ("speakPlaceholder", "Polis.Ui.SpeakPlaceholder", "说点什么……"),
        ("speakSend", "Polis.Ui.SpeakSend", "说"),
        ("speakHint", "Polis.Ui.SpeakHint", "Enter 说 · 附近的人会接话 · 和雅典娜说话请走到她身边按 E"),
        ("speakCommandSoon", "Polis.Ui.SpeakCommandSoon", "（「/」命令还在路上）"),
        ("speakMentionSoon", "Polis.Ui.SpeakMentionSoon", "（「@」点名还在路上）"),
        ("athenaHasWords", "Polis.Ui.AthenaHasWords", "我这里有一段替你写好的话，过来找我。"),
        // 和一位居民聊天（2026-10-13，原来 M-C 的问话）
        ("citizenChat", "Polis.Ui.CitizenChat", "聊几句"),
        ("chatStatus", "Polis.Ui.ChatStatus", "和{name}聊天"),
        ("chatPlaceholder", "Polis.Ui.ChatPlaceholder", "对{name}说……"),
        ("chatWaiting", "Polis.Ui.ChatWaiting", "{name}在想……"),
        ("chatSend", "Polis.Ui.ChatSend", "说"),
        ("chatHint", "Polis.Ui.ChatHint", "Enter 说 · Esc 告辞"),
        ("askTimeout", "Polis.Ui.AskTimeout", "他想了想，没说出来。"),
        // 居民卡片上那场会话的信息与预览（2026-10-13）
        ("citizenTranscript", "Polis.Ui.CitizenTranscript", "看看那场会话"),
        ("citizenInfoCommission", "Polis.Ui.CitizenInfoCommission", "委托：{title}"),
        ("citizenInfoUntitled", "Polis.Ui.CitizenInfoUntitled", "（还没有标题）"),
        ("citizenInfoState", "Polis.Ui.CitizenInfoState", "{outcome} · 完成了 {turns} 回合"),
        ("citizenInfoDays", "Polis.Ui.CitizenInfoDays", "{days} 天前动过"),
        ("citizenInfoToday", "Polis.Ui.CitizenInfoToday", "今天动过"),
        ("citizenInfoFolders", "Polis.Ui.CitizenInfoFolders", "常去：{folders}"),
        ("outcomeRunning", "Polis.Ui.OutcomeRunning", "正在办"),
        ("outcomeScheduled", "Polis.Ui.OutcomeScheduled", "定时任务"),
        ("outcomeInterrupted", "Polis.Ui.OutcomeInterrupted", "做到一半停下了"),
        ("outcomeCompleted", "Polis.Ui.OutcomeCompleted", "办完了"),
        ("outcomeOpen", "Polis.Ui.OutcomeOpen", "还没开头"),
        ("transcriptTitle", "Polis.Ui.TranscriptTitle", "{name}的那场会话：{title}"),
        ("transcriptYou", "Polis.Ui.TranscriptYou", "你"),
        ("transcriptAthena", "Polis.Ui.TranscriptAthena", "雅典娜"),
        ("transcriptOmitted", "Polis.Ui.TranscriptOmitted", "（更早的 {count} 条没有列出）"),
        ("transcriptEmpty", "Polis.Ui.TranscriptEmpty", "这场会话里还没有说过话。"),
        // M-E（2026-10-13）：讲台前的辩论
        ("interactBema", "Polis.Ui.InteractBema", "上讲台辩论"),
        ("bemaTitle", "Polis.Ui.BemaTitle", "讲台"),
        ("bemaBody", "Polis.Ui.BemaBody", "提一个议题，你站正方，雅典娜站反方，辩三轮；街上的居民当陪审团。"),
        ("bemaRecord", "Polis.Ui.BemaRecord", "你 {wins} 胜 {losses} 负 {ties} 平"),
        ("bemaPropose", "Polis.Ui.BemaPropose", "提一个议题"),
        ("debateName", "Polis.Ui.DebateName", "辩论"),
        ("debateMotionStatus", "Polis.Ui.DebateMotionStatus", "你站正方，雅典娜站反方"),
        ("debateMotionPlaceholder", "Polis.Ui.DebateMotionPlaceholder", "提一个议题……（比如：周末应该完全不碰工作）"),
        ("debateArguePlaceholder", "Polis.Ui.DebateArguePlaceholder", "第 {round} / {rounds} 轮：说你的论点……"),
        ("debateArgueStatus", "Polis.Ui.DebateArgueStatus", "议题：{motion}"),
        ("debateSend", "Polis.Ui.DebateSend", "陈述"),
        ("debateHint", "Polis.Ui.DebateHint", "Enter 陈述 · Esc 不辩了"),
        ("debateAthenaThinking", "Polis.Ui.DebateAthenaThinking", "雅典娜在想她的反驳……"),
        ("debateJudging", "Polis.Ui.DebateJudging", "陪审团在商议……"),
        ("debateRound", "Polis.Ui.DebateRound", "第 {round} / {rounds} 轮"),
        ("debateYou", "Polis.Ui.DebateYou", "你"),
        ("debateAthena", "Polis.Ui.DebateAthena", "雅典娜"),
        ("debateVotes", "Polis.Ui.DebateVotes", "陪审团：正方 {you} 票，反方 {athena} 票"),
        ("debateYouWin", "Polis.Ui.DebateYouWin", "你赢了"),
        ("debateAthenaWins", "Polis.Ui.DebateAthenaWins", "雅典娜赢了"),
        ("debateTie", "Polis.Ui.DebateTie", "平局"),
        ("debateClose", "Polis.Ui.DebateClose", "结束"),
        // M-D（2026-10-13）：户与民居
        ("householdFounded", "Polis.Ui.HouseholdFounded", "「{name}」在城里安了家。"),
    };

    /// <summary>
    /// 辩论（M-E）里 C# 替人说的那几句：没开成、雅典娜说不出来（这一轮让给你）、裁判没给出裁决（算平局）。永远是一句话，不是错误框。
    /// </summary>
    public static IReadOnlyList<(string What, string Key, string Fallback)> Debate { get; } = new[]
    {
        ("Limited", "Polis.Debate.Limited", "这一个钟头辩得够多了，歇一会儿再来。"),
        ("AthenaNoModel", "Polis.Debate.AthenaNoModel", "（雅典娜没法开口：设置里还没给「主对话」选模型。这一轮让给你。）"),
        ("AthenaFailed", "Polis.Debate.AthenaFailed", "（雅典娜想了想，这一轮让给你。）"),
        ("JudgeNoModel", "Polis.Debate.JudgeNoModel", "陪审团没有发言人：设置里还没给「虚拟宠物与城里的居民」或「标题生成」选模型，算平局。"),
        ("NoVerdict", "Polis.Debate.NoVerdict", "陪审团没能给出裁决，算平局。"),
    };

    /// <summary>
    /// 问话（M-C）没有拿到回答时他说的那一句（C# 按 <see cref="Services.GameMode.CitizenAnswerStatus"/> 挑，推给页面的就是这句话）。
    /// 永远是一句话，不是错误框。
    /// </summary>
    public static IReadOnlyList<(string Status, string Key, string Fallback)> CitizenAsk { get; } = new[]
    {
        ("NoModel", "Polis.Citizen.Ask.NoModel", "要问话，先在设置的模型角色里给「虚拟宠物与城里的居民」或「标题生成」选一个模型。"),
        ("Busy", "Polis.Citizen.Ask.Busy", "我还在想上一个问题呢。"),
        ("Limited", "Polis.Citizen.Ask.Limited", "这一个钟头问得够多了，我有点累，过一阵再来吧。"),
        ("Failed", "Polis.Citizen.Ask.Failed", "我想了想，没说出来。"),
    };

    /// <summary>
    /// 居民（M-B，2026-10-13）：希腊名表（男女各一份，各 PolisCitizens.NamesPerSex 个，中英一一对应）、重名时的序号拼法（C# 用来起名字），跟你打招呼的话（页面挑）。
    /// 两位居民碰上时的闲聊 2026-10-13 起只用模型（<see cref="Services.GameMode.PolisTalk.BuildAmbientLine"/>），没有本地台词。
    /// </summary>
    public static IReadOnlyList<(string JsKey, string Key, string Fallback)> Citizen { get; } = new[]
    {
        ("namesMale", "Polis.Citizen.NamesMale", "德米特里俄斯|阿里斯通|克里同|斐德若|吕西斯|卡尔米德|欧绪弗洛|美诺|泰阿泰德|色诺芬|赫蒙|斐洛|阿尔基比亚德|阿伽通|埃斯基涅斯|阿那克萨戈拉|安提丰|阿波罗多洛斯|阿尔刻拉俄斯|阿尔基塔斯|阿里斯提德|阿里斯塔克斯|阿里斯提波|卡隆达斯|凯瑞丰|达蒙|德摩斯梯尼|狄奥多托斯|第欧根尼|狄翁|恩培多克勒|伊壁鸠鲁|埃拉托色尼|欧几里得|欧多克索斯|欧里庇得斯|格劳孔|高尔吉亚|赫卡泰俄斯|赫拉克利特|赫谟根尼|希庇亚斯|希波克拉底|伊克提诺斯|伊索克拉底|卡利亚斯|卡利克勒斯|克贝|客蒙|克里斯提尼|克里昂|克里底亚|克特西波|拉刻斯|莱昂|吕库古|吕山德|吕西阿斯|墨兰提俄斯|美勒托|美涅克塞努|米太亚德|米隆|尼基阿斯|尼各马可|保萨尼亚斯|伯里克利|斐多|菲狄亚斯|菲洛劳斯|福基翁|品达|玻勒马霍斯|波利克里托斯|普拉克西特列斯|普罗狄科|普罗泰戈拉|皮浪|西米亚斯|西蒙尼德斯|梭伦|索福克勒斯|斯彪西波|斯特西克鲁斯|泰勒斯|地米斯托克利|忒奥多洛斯|泰奥弗拉斯托斯|修昔底德|色拉叙布洛斯|色拉叙马霍斯|蒂迈欧|提蒙|克桑提波斯|芝诺|宙克西斯|安德罗克勒斯|卡利马科斯|欧迈尼斯|尼坎德罗斯|菲利波斯|斯特法诺斯|忒阿格涅斯|希厄隆|波吕克拉特斯|列奥尼达|克列奥门尼|伯拉西达|福尔米昂|科农|伊菲克拉特斯|卡布里亚斯|提摩透斯|伊巴密浓达|佩洛皮达斯|阿基米德|阿里斯托克塞诺斯|欧弗拉诺尔|阿佩莱斯|帕拉西奥斯"),
        ("namesFemale", "Polis.Citizen.NamesFemale", "菲莉丝|达芙妮|克莱奥|海伦娜|伊瑞涅|塔伊丝|狄俄提玛|阿斯帕西娅|墨利萨|卡丽丝|科林娜|米尔托|阿伽里斯忒|阿格诺狄刻|阿尔刻斯提斯|安德洛玛刻|安提戈涅|阿尼忒|阿里阿德涅|阿瑞忒|阿尔西诺伊|阿克西奥特亚|贝勒尼基|克洛伊|克律西斯|达摩|得伊阿涅拉|厄勒克特拉|厄尔庇尼刻|埃里娜|欧阿德涅|欧多基娅|欧妮刻|欧佛洛绪涅|欧律狄刻|格吕刻拉|哈耳摩尼亚|赫耳弥俄涅|希帕基娅|希帕提娅|伊俄卡斯忒|伊斯墨涅|卡利斯托|卡珊德拉|克莱奥布林娜|克莉奥帕特拉|库狄珀|拉伊丝|兰庇托|勒达|吕西斯特拉忒|墨伽拉|墨林娜|墨洛珀|墨特罗多拉|墨罗|密耳里涅|瑙西卡|涅埃拉|尼卡瑞忒|诺西丝|奥林匹娅丝|潘忒亚|珀涅罗珀|珀里克提俄涅|淮德拉|菲林娜|芙丽涅|普拉克西拉|罗多珀|萨福|忒勒西拉|忒阿诺|忒奥多忒|提玛瑞塔|克桑提珀|佐伊|阿伽忒|阿尔克墨涅|阿玛瑞丽丝|安图萨|阿尔基珀|阿塔兰忒|卡里克罗|德摩|多尔卡丝|欧部勒|欧克勒亚|伽勒涅|戈尔戈|赫洛|伊俄勒|卡利洛厄|克莱托|科瑞|吕山德拉|墨利忒|谟涅萨瑞忒|尼开亚|尼俄柏|俄诺涅|潘菲勒|菲莱妮丝|菲拉|波吕克塞娜|罗得|斯特拉托妮刻|塔利亚|忒奥法尼娅|忒萨勒|提曼德拉|克塞尼娅|阿格莱亚|安提俄珀|伊安忒|卡利俄珀|吕西珀|墨尔波墨涅|乌拉妮娅|菲罗墨拉"),
        // 同性别的人多过名表时，第二轮起名字后面带序号（{1} 从 2 起）
        ("nameNth", "Polis.Citizen.NameNth", "{0}（{1}）"),
        // M-D：户名（{0} 是立户那一位的名字）
        ("householdName", "Polis.Household.NamePattern", "{0}一家"),
        ("greet", "Polis.Citizen.Greet", "你好呀。|日安。|又见面了。|今天天气不错。|你也出来走走？"),
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
        ("paintedStoa", "Polis.Public.PaintedStoa", "彩绘柱廊"),
        ("archive", "Polis.Public.Archive", "书库"),
        ("vault", "Polis.Public.Vault", "金库"),
        ("grove", "Polis.Public.Grove", "橄榄林"),
    };

    /// <summary>
    /// 十位职业居民（2026-10-13 起不再和工具挂钩）：职业、固定的希腊名（男女分明，不与居民名表重名——测试核对）、卡片上他自己说的话
    /// （"|" 分隔几种说法，按日期轮换；都是城里的说法，不再是能力的介绍）、聊天时他的人设（他管的那摊事与性子，进提示词）。
    /// </summary>
    public static IReadOnlyList<(string Kind, string NameKey, string NameFallback, string GreekKey, string GreekFallback, string IntroKey, string IntroFallback, string PersonaKey, string PersonaFallback)> Residents { get; } = new[]
    {
        ("surveyor", "Polis.Resident.Surveyor.Name", "测绘员", "Polis.Resident.Surveyor.GreekName", "阿那克西曼德",
            "Polis.Resident.Surveyor.Intro", "这座城的每一座房子我都量过：哪一座最大、哪一座最久没人进、里面都放着些什么，问我就对了。|雅典娜要找什么东西，常常先来问我在哪一片。",
            "Polis.Resident.Surveyor.Persona", "你管着城的地图和清单：每座建筑（就是城的主人的每个文件夹）有多大、多旧、放着什么。你一丝不苟，说话爱带上数字和方位，至今还为量错过的那一回耿耿于怀。"),
        ("scribe", "Polis.Resident.Scribe.Name", "抄写员", "Polis.Resident.Scribe.GreekName", "卡德摩斯",
            "Polis.Resident.Scribe.Intro", "我在档案馆抄写文书：报告、说明、书信、清单，雅典娜口述，我落笔。|写到一半的东西别扔，拿来，我替你接着写顺。",
            "Polis.Resident.Scribe.Persona", "你在档案馆当抄写员，替雅典娜起草、誊写城里的文书、报告和书信。你年轻勤快，有点书呆子气，听到别人用词不准就忍不住要纠正一下。"),
        ("accountant", "Polis.Resident.Accountant.Name", "账房", "Polis.Resident.Accountant.GreekName", "帕西昂",
            "Polis.Resident.Accountant.Intro", "库房里的账册都归我管：哪一笔进、哪一笔出，哪一格对不上，我一眼就看得出来。|数目这东西，差一个铜板也是差。",
            "Polis.Resident.Accountant.Persona", "你在库房管账，城里的账册、表格、数目都经你的手。你上了年纪，精打细算，说话慢条斯理，三句不离数字，最见不得账对不上。"),
        ("smith", "Polis.Resident.Smith.Name", "铁匠", "Polis.Resident.Smith.GreekName", "赫菲斯提昂",
            "Polis.Resident.Smith.Intro", "锻炉这边是干粗活的地方：成百上千件东西要一样样过一遍，交给我，一锤一锤地来。|动真格的那几下我会先停下来问一声，砸坏了可就补不回来了。",
            "Polis.Resident.Smith.Persona", "你在锻炉打铁，城里成批的苦力活、要一样样过一遍的粗活都在锻炉办。你嗓门大，性子直，干活利落，最讨厌磨磨蹭蹭，但真要下重锤之前一定先确认一遍。"),
        ("sailor", "Polis.Resident.Sailor.Name", "水手", "Polis.Resident.Sailor.GreekName", "涅阿尔科斯",
            "Polis.Resident.Sailor.Intro", "船一出港，就是去外头打听消息：远方的新鲜事、别人家的价钱、要带回来的文书，我都捎得回来。|海上的事说不准，打发我出去之前，最好想清楚要打听什么。",
            "Polis.Resident.Sailor.Persona", "你是港口的水手，船按时出海，去外头打听消息、捎回远方的文书。你乐天，爱讲海上的见闻，讲着讲着就添油加醋，说到风浪就眉飞色舞。"),
        ("librarian", "Polis.Resident.Librarian.Name", "图书管理员", "Polis.Resident.Librarian.GreekName", "泽诺多托斯",
            "Polis.Resident.Librarian.Intro", "圆形图书馆里收着这座城的记忆：主人说过的偏好、要紧的事，记下一次，以后就不用再说。|想知道这里记着什么？问我。记错的，也可以改。",
            "Polis.Resident.Librarian.Persona", "你管着圆形图书馆，那里收着这座城的记忆——城的主人的偏好、说过的要紧事。你年长、温和、记性极好，爱引经据典，说话轻声细语。"),
        ("herald", "Polis.Resident.Herald.Name", "传令官", "Polis.Resident.Herald.GreekName", "塔尔提比俄斯",
            "Polis.Resident.Herald.Intro", "我站在讲台上：有人提议题，我主持辩论；到了时辰该办的差事，结果出来由我来报。|你不在城里时错过的那几件，回来我也一样报给你。",
            "Polis.Resident.Herald.Persona", "你是传令官，站在广场的讲台上主持辩论，按时辰报告那些定时差事的结果。你嗓音洪亮，讲究礼数，说话爱用郑重其事的开场白。"),
        ("painter", "Polis.Resident.Painter.Name", "画师", "Polis.Resident.Painter.GreekName", "卡吕普索",
            "Polis.Resident.Painter.Intro", "彩绘柱廊里挂的画都出自我手：配图、示意、封面，说清楚画什么、什么味道，我就画给你。|光从哪边来，颜色就跟着变，你注意过吗？",
            "Polis.Resident.Painter.Persona", "你是彩绘柱廊的画师，城里的配图、示意图、封面都由你画。你敏感，爱观察光线和颜色，说话常带着画面感，偶尔有点自顾自。"),
        ("artisan", "Polis.Resident.Artisan.Name", "工匠", "Polis.Resident.Artisan.GreekName", "代达罗斯",
            "Polis.Resident.Artisan.Intro", "我在锻炉旁凿石头、做器具和机关；城外的手艺和工具要接进城里来，也是我来装。|主人家的规矩（设置）我也会调，要紧的地方会先问过。",
            "Polis.Resident.Artisan.Persona", "你是锻炉旁的工匠，凿石头、做器具和机关，也负责把城外的手艺和工具接进城里来，调一调城的规矩。你手巧，话少，总在琢磨怎么做得更精巧。"),
        ("priestess", "Polis.Resident.Priestess.Name", "女祭司", "Polis.Resident.Priestess.GreekName", "忒弥斯托克勒亚",
            "Polis.Resident.Priestess.Intro", "神庙里每天清晨有一道神谕：雅典娜替主人看一眼城里的变化，写成一卷晨报。|想换个时辰、换些内容？去跟雅典娜说，神谕跟着改。",
            "Polis.Resident.Priestess.Persona", "你是神庙的女祭司，照看神庙，主持每天清晨的神谕（雅典娜替城的主人写的晨报）。你庄重沉静，说话带一点神秘，喜欢用预兆和比喻。"),
    };

    /// <summary>委托板（7.3）：每一种建议在板上的一句话与预填的请求。{0} 主语（文件夹名、委托标题、藏品名），{1} 数量，{2} 多少天没动。</summary>
    public static IReadOnlyList<(string Kind, string LineKey, string LineFallback, string PromptKey, string PromptFallback)> Board { get; } = new[]
    {
        ("ResumeCommission", "Polis.Board.ResumeCommission", "上次「{0}」停在半路，要接着做吗？", "Polis.Board.ResumeCommission.Prompt", "上次「{0}」做到一半停下了，请接着做完。"),
        ("FindLostItem", "Polis.Board.FindLostItem", "{0}找不到了，要不要找找它去了哪儿？", "Polis.Board.FindLostItem.Prompt", "之前交付的{0}找不到了，请在这个工作区里找找它是不是被挪到了别处、改了名字。"),
        ("TidyAgora", "Polis.Board.TidyAgora", "广场上散着 {1} 个文件，要不要归到文件夹里？", "Polis.Board.TidyAgora.Prompt", "工作区根目录下散着 {1} 个文件，请先列出来、按用途给出归类建议，等我确认之后再挪动。"),
        ("SortMixedBuilding", "Polis.Board.SortMixedBuilding", "「{0}」里混着 {1} 件东西，要不要分分类？", "Polis.Board.SortMixedBuilding.Prompt", "「{0}」里混着 {1} 个文件，请看看都有些什么，给出分类整理的方案，等我确认之后再动手。"),
        ("ReviewStaleBuilding", "Polis.Board.ReviewStaleBuilding", "「{0}」已经 {2} 天没动了，要不要清点一下？", "Polis.Board.ReviewStaleBuilding.Prompt", "「{0}」已经 {2} 天没动过了，请清点一下里面的 {1} 个文件：哪些还有用、哪些可以归档，列一张清单给我。"),
        ("CatalogImages", "Polis.Board.CatalogImages", "给「{0}」里的 {1} 张图片做个目录？", "Polis.Board.CatalogImages.Prompt", "请给「{0}」里的 {1} 张图片做一份目录：按主题分组，每张一句说明，写成一个 Markdown 文件放在「{0}」里。"),
        ("SummarizeSheets", "Polis.Board.SummarizeSheets", "汇总「{0}」里的 {1} 张表？", "Polis.Board.SummarizeSheets.Prompt", "请把「{0}」里的 {1} 张表格汇总一下：每张是什么、关键的数字是多少，写成一页摘要。"),
        ("SummarizeDocuments", "Polis.Board.SummarizeDocuments", "把「{0}」里的 {1} 份文档各写一句摘要？", "Polis.Board.SummarizeDocuments.Prompt", "请把「{0}」里的 {1} 份文档各写一句摘要，汇成一份清单。"),
        ("DescribeWorkshop", "Polis.Board.DescribeWorkshop", "给「{0}」写一份说明？", "Polis.Board.DescribeWorkshop.Prompt", "请看看「{0}」里有什么，写一份说明：它是做什么的、主要的部分在哪里、怎么上手。"),
        ("InspectArchives", "Polis.Board.InspectArchives", "看看「{0}」里的 {1} 个压缩包都装了什么？", "Polis.Board.InspectArchives.Prompt", "请看看「{0}」里的 {1} 个压缩包分别装了什么（不要解压到别处），列一张清单给我。"),
        ("RecapBusyBuilding", "Polis.Board.RecapBusyBuilding", "「{0}」这两天改得最多，要不要总结一下？", "Polis.Board.RecapBusyBuilding.Prompt", "「{0}」这两天改动最多，请总结一下最近改了什么、还有什么没做完。"),
        ("SetOracle", "Polis.Board.SetOracle", "在神庙设一个每日神谕", "Polis.Board.SetOracle.Prompt", ""),
        ("PlanEmptyCity", "Polis.Board.PlanEmptyCity", "这座城还空着，要不要先规划几个文件夹？", "Polis.Board.PlanEmptyCity.Prompt", "这个工作区还是空的。请问问我打算用它做什么，然后建议一套文件夹结构，等我确认之后再建。"),
        ("AskAnything", "Polis.Board.AskAnything", "有什么想问的，直接问我", "Polis.Board.AskAnything.Prompt", "我想请你帮我："),
        ("RememberPreference", "Polis.Board.RememberPreference", "让我记住你的偏好", "Polis.Board.RememberPreference.Prompt", "请记住我的偏好：（写下你希望她以后都照着做的事）"),
    };

    /// <summary>神谕（7.3）：神庙面板与预填的请求。请求里写明任务名与 workspaceId，模型照抄时神庙认得出来。</summary>
    public static IReadOnlyList<(string Key, string Fallback)> Oracle { get; } = new[]
    {
        ("Polis.Oracle.Set", "每日神谕已设立 · {0} · 下一卷：{1}"),
        ("Polis.Oracle.Paused", "每日神谕已暂停（在定时任务页里可以重新启用）"),
        ("Polis.Oracle.Request", "请用定时任务工具（create_task）为这个工作区设立「每日神谕」：每天早上 8 点运行（cron：0 8 * * *），任务名写「每日神谕」，workspaceId 填 {1}。任务内容：回顾工作区「{0}」最近一天的变化，写一卷简短的晨报给我——有什么新东西、有什么值得留意、今天可以先做什么。"),
        ("Polis.Oracle.RequestGlobal", "请用定时任务工具（create_task）设立「每日神谕」：每天早上 8 点运行（cron：0 8 * * *），任务名写「每日神谕」，不填 workspaceId。任务内容：每天早上给我一卷简短的晨报——今天值得留意的事、我可以先做什么。"),
        ("Polis.Oracle.ItemTitle", "每日神谕 · {0}"),
    };

    /// <summary>传令官（7.3、11.3）：每一种播报一句。{0} 主语，{1} 细节（失败原因）或数量；改名是 {0} 旧名、{1} 新名（沿用 M1 的词条）。</summary>
    public static IReadOnlyList<(string Kind, string Key, string Fallback)> Herald { get; } = new[]
    {
        ("CronSucceeded", "GameMode.Herald.CronSucceeded", "定时任务「{0}」跑完了，点这里看结果"),
        ("CronFailed", "GameMode.Herald.CronFailed", "定时任务「{0}」失败了：{1}"),
        ("CronSkipped", "GameMode.Herald.CronSkipped", "定时任务「{0}」到点时应用没开，这一次跳过了"),
        ("CronInterrupted", "GameMode.Herald.CronInterrupted", "定时任务「{0}」跑到一半被打断了"),
        ("OracleScroll", "GameMode.Herald.OracleScroll", "每日神谕送来了一卷，放在广场上等你"),
        ("CommissionInterrupted", "GameMode.Herald.CommissionInterrupted", "「{0}」上次停在半路，要接着做就点这里"),
        ("BuildingAdded", "GameMode.Report.Added", "「{0}」是新来的：城市边缘起了一座建筑"),
        ("BuildingRemoved", "GameMode.Report.Removed", "「{0}」不见了，留下一块空地"),
        ("BuildingRenamed", "GameMode.Report.Renamed", "「{0}」改名为「{1}」，原地换了牌匾"),
        ("BuildingGrew", "GameMode.Report.Grew", "「{0}」里多了 {1} 个文件"),
        ("BuildingShrank", "GameMode.Report.Shrank", "「{0}」里少了 {1} 个文件"),
        ("BuildingTouched", "GameMode.Report.Touched", "「{0}」里有文件被改过"),
        ("ItemLost", "GameMode.Report.ItemLost", "{0}找不到了"),
        ("ItemMoved", "GameMode.Report.ItemMoved", "{0}换了地方，已经找回"),
        ("ItemModified", "GameMode.Report.ItemModified", "{0}交付之后被改过"),
    };

    /// <summary>全部 locale 键（测试核对中英两份 locale 都有这些词条）。</summary>
    public static IEnumerable<string> AllKeys => Ui.Concat(Places).Concat(PublicNames).Select(e => e.Key)
        .Concat(Residents.SelectMany(r => new[] { r.NameKey, r.GreekKey, r.IntroKey, r.PersonaKey }))
        .Concat(Board.SelectMany(b => new[] { b.LineKey, b.PromptKey }))
        .Concat(Oracle.Select(o => o.Key))
        .Concat(Herald.Select(h => h.Key))
        .Concat(Citizen.Select(c => c.Key))
        .Concat(CitizenAsk.Select(c => c.Key))
        .Concat(Debate.Select(d => d.Key))
        .Distinct(StringComparer.Ordinal);

    /// <summary>按当前语言组装给页面的 <c>init</c> 表。</summary>
    public static Dictionary<string, Dictionary<string, string>> BuildTables(Func<string, string, string> localize)
    {
        ArgumentNullException.ThrowIfNull(localize);
        static Dictionary<string, string> Table(IEnumerable<(string JsKey, string Key, string Fallback)> entries, Func<string, string, string> l)
            => entries.ToDictionary(e => e.JsKey, e => l(e.Key, e.Fallback), StringComparer.Ordinal);
        return new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal)
        {
            ["ui"] = Table(Ui, localize),
            ["places"] = Table(Places, localize),
            ["publicNames"] = Table(PublicNames, localize),
            ["citizen"] = Table(Citizen, localize),
        };
    }
}
