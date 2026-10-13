// 城邦这一局：场景、人物、镜头、成果、面板，以及每一帧的推进。两个驱动：夹具（普通浏览器里，测试与演示）和
// 实时（应用里的 NativeWebView，C# 推城邦、成果与对话栏的状态）。
//
// 雅典娜不镜像任何回合（2026-10-13 改向，见 athena.mjs）：页面收不到工具调用、推理和审批事件。她住在城里——在广场上
// 待着、走一走，你走近或者开口，她就停下面向你；你在远处开口，她走过来。一回合做完，成果直接出现在你脚下。
//
// 屏幕空间效果（光晕、她头顶的话、建筑与居民的名牌、橄榄树的名牌）必须用"这一帧真正用来渲染的"相机矩阵投影（设计稿 12.2）。
// 每帧的顺序固定为：推进 → 摆好人物和镜头 → 对齐矩阵 → 算屏幕空间位置 → 渲染 → 叠加层。
//
// M2：人物是可动的骨架（art/rig.mjs），动作来自纯函数的姿势（art/poses.mjs）；城里的一切经资产库（art/assets.mjs）造出来。
// 积累：收下的藏品摆在港前三处场馆的底座上（摆在哪由你选）、橄榄林一份委托一棵树、居民随能力出现、委托板三条建议、
// 神庙的每日神谕、传令官的报告、群岛海图——意图都是封闭集合里的那几条，C# 逐条校验；网页没有"批准"。
//
// 操作（controls.mjs）：WASD / 方向键走路；走近人或物件按 E 互动（鼠标只管镜头和界面上的按钮）；视角缺省是自由的——
// 左键拖动绕着转、右键拖动平移、滚轮缩放。和雅典娜说话在城里的对话栏（talk.mjs）：走到她跟前按 E、或者按 Enter。
import * as THREE from '../vendor/three.bundle.mjs';
import { OrbitControls } from '../vendor/three.bundle.mjs';
import { createLayout, pointAlong, polyLength, PLAYER_SPEED, SPEED, ATHENA_STRIDE } from './layout.mjs';
import {
  VIEW, VIEW_MODES, RUN_FACTOR, MOVE_KEYS, nextView, moveDirection, slide, orbitAngles, deadZoneShift, turnToward, isTypingTarget,
} from './controls.mjs';
import { createAthena, stepProgress, ATHENA } from './athena.mjs';
import { planCitizen, citizenAt, newScheduleClock, scheduleClock, holdSchedule, resumeSchedule, separate, assignTiers, pickChat, nextChatAfter, greetLine, TOWN } from './citizens.mjs';
import { debateStage, assignAudience, supportersCount, bubbleExcerpt, DEBATE } from './debate.mjs';
import { createRenderer } from './render.mjs';
import { createOverlay } from './overlay.mjs';
import { makeMaterials, setNightWindows } from './art/materials.mjs';
import { swellHeight } from './art/water.mjs';
import { createAssetLibrary } from './art/assets.mjs';
import { setClothWind } from './art/rig.mjs';
import { windAt } from './art/springs.mjs';
import { ONE_SHOT_MS, oneShotDone } from './art/poses.mjs';
import { buildWorld, PUBLIC_NAMES } from './world.mjs';
import { framePolicy, transitionArrival } from './frame-policy.mjs';
import { islandLayout, chartTransform, hitIsland, SLOTS_PER_VENUE } from './civic-layout.mjs';

const CAMERA = { distance: 300, elevation: 33, azimuth: 45 };
const BUBBLE_MS = 3600;
/**
 * 港口的船按自己的时刻表出海（2026-10-13）：原来只在雅典娜"取网页"时出海，直播拿掉之后船就永远泊着。
 * 每 periodMs 出海一趟、tripMs 回来——港口有动静，水手在船出海时停下收缆。按墙钟算，同一刻永远是同一个位置。
 */
export const BOAT_TRIP = Object.freeze({ periodMs: 180_000, tripMs: 20_000, offsetMs: 45_000 });
const VOYAGE_FADE_MS = 400;
const REVEAL_MS = 3200;
/** 你自己说话时，这么近以内（而且在画面里）的人会接一句（2026-10-13）。 */
export const SPEAK_RADIUS = 6;
/** 接你话的那一位站定朝你多久（等他想、他说完之后再留 HEARD_AFTER_MS）。 */
const HEARD_HOLD_MS = 30_000;
const HEARD_AFTER_MS = 5000;
/** 跑的速度（米 / 秒）：她走的速度除以 athena.mjs 的 runFactor。 */
const RUN_SPEED = SPEED / ATHENA.runFactor;
/** 你离她这么近，她就不再走开（闲逛暂停，athena.mjs 的 hold）。 */
const NEAR_ATHENA = 2.5;
/** 指针停在她身上时那块名牌的位置（她自己的坐标系里）：脚下，头顶留给旁白气泡。 */
export const ATHENA_HOVER_ANCHOR = Object.freeze(new THREE.Vector3(0, -0.6, 0));
const SAVE_PLAYER_MS = 700;    // 停下来这么久才记下位置（走的途中不发）
/** 交互键（物理键位）：走到人或物件跟前按它。 */
export const INTERACT_KEY = 'KeyE';

/** 页面自己的界面词条。实时模式下由 C# 按界面语言覆盖（locale 文件里的 Polis.Ui.*）。 */
export const UI_ZH = Object.freeze({
  dismiss: '知道了',
  noticeBoard: '广场公告板',
  fogTitle: '找不到这座城邦的文件夹',
  fogBody: '文件夹被移动、改名或删除了。重新定位它，城邦和存档都还在。',
  relocate: '重新定位',
  read: '读',
  open: '打开',
  accept: '收下',
  return: '退回',
  kindScroll: '卷轴',
  kindLedger: '账册',
  kindPainting: '彩绘板',
  kindAnswer: '回答',
  statePending: '等你收下',
  stateAccepted: '已收下',
  stateReturned: '已退回',
  stateLost: '找不到它指向的文件了',
  stateModified: '交付之后被改过',
  founding: '这是你的城邦，每座建筑都是你的一个文件夹',
  voyageCity: '驶向城邦',
  voyageSanctuary: '驶向雅典娜的神殿',
  close: '收起',
  // 视角（2026-10-11：自由视角是缺省）
  viewLabel: '视角',
  viewFree: '自由',
  viewPlayer: '跟着你',
  viewAthena: '跟着雅典娜',
  viewFreeTip: '左键拖动转视角，右键拖动平移，滚轮缩放；你走远了镜头会跟上',
  controlsHint: 'WASD / 方向键走动（按住 Shift 跑）· 拖动转视角 · 走近人或物件按 E 互动 · Enter 说话（和雅典娜说话请走到她身边按 E）',
  focusHint: '点一下画面，就能用键盘走动',
  athenaHere: '雅典娜在这边',
  // 走近按 E（M3 第三轮）：脚下那块提示
  interactKey: 'E',
  interactTalk: '和雅典娜说话',
  interactTalkWaiting: '和雅典娜说话（有一段写好的话等你）',
  interactResident: '和{name}说话',
  interactItem: '看看「{title}」',
  interactPlace: '摆在这里',
  interactTree: '去「{title}」这份委托',
  interactBoard: '看看委托板',
  interactOracle: '看看每日神谕',
  interactVenue: '进{venue}看看',
  // 和雅典娜说话（城里的对话栏）
  talkName: '雅典娜',
  talkToAthena: '和雅典娜说话',
  talkPlaceholder: '想请雅典娜做什么？',
  talkSend: '托付',
  talkStop: '请她停下',
  talkHint: 'Enter 托付 · Shift+Enter 换行 · Esc 收起',
  talkIdle: '她在听',
  talkBusy: '她在办你交代的事——可以先写下一件',
  talkQueued: '排在后面，前一件做完就开始',
  talkWaiting: '她在等你点头：看看审批窗口',
  talkCompressing: '她在整理记忆，稍等一下',
  talkAttachments: '一并交给她：{names}',
  // M2
  place: '摆放',
  move: '换个地方',
  takeDown: '撤下',
  remove: '移除',
  placeHint: '走到一个空底座前按 E，把《{title}》摆在那里',
  placeDefault: '放进{venue}',
  cancel: '取消',
  storedIn: '摆在{venue}',
  storedNowhere: '收着，还没摆出来',
  venueCount: '{count} / {total} 个底座摆了藏品',
  venueStored: '收着的藏品',
  venueEmpty: '还没有藏品。收下的成果会摆到这里。',
  placeHere: '摆进这里',
  boardTitle: '委托板',
  boardHint: '这里有几件可以交给我的事：',
  takeSuggestion: '交给雅典娜',
  oracleTitle: '每日神谕',
  oracleNone: '在神庙设立每日神谕：每天早上，雅典娜回顾这座城的变化，传令官把晨报送到你手上。设立时需要你在审批窗口里点头。',
  requestOracle: '请雅典娜设立',
  latestScroll: '看最近一卷',
  residentTitle: '{trade}{name}',
  chart: '海图',
  chartTitle: '群岛海图',
  chartHint: '一座岛是一座城邦。点一座岛，驶向它最近的那份委托。',
  chartRunning: '有委托在进行',
  chartWaiting: '等你点头',
  chartHere: '你在这里',
  chartMissing: '找不到文件夹',
  // 居民即会话（M-B）
  interactCitizen: '和{name}说话',
  citizenOpen: '去看那份委托',
  censusLine: '这座城里住着 {total} 位居民，街上有 {street} 位',
  interactStele: '读碑上的名字',
  steleTitle: '老居民',
  steleBody: '一年多没出门的居民，名字刻在这里：',
  // 你自己说话（2026-10-13：对话栏平时是你自己说话，附近的人会接；和雅典娜说话要走到她身边按 E）
  speakName: '你',
  speakIdle: '说点什么，附近的人会听见',
  speakAthenaBusy: '雅典娜在办事——要和她说话，走到她身边按 E',
  speakPlaceholder: '说点什么……',
  speakSend: '说',
  speakHint: 'Enter 说 · 附近的人会接话 · 和雅典娜说话请走到她身边按 E',
  speakCommandSoon: '（「/」命令还在路上）',
  speakMentionSoon: '（「@」点名还在路上）',
  athenaHasWords: '我这里有一段替你写好的话，过来找我。',
  // 和一位居民聊天（2026-10-13，原来 M-C 的问话：他的话出现在他头顶）
  citizenChat: '聊几句',
  chatStatus: '和{name}聊天',
  chatPlaceholder: '对{name}说……',
  chatWaiting: '{name}在想……',
  chatSend: '说',
  chatHint: 'Enter 说 · Esc 告辞',
  askTimeout: '他想了想，没说出来。',
  // 居民卡片上那场会话的信息与预览（2026-10-13）
  citizenTranscript: '看看那场会话',
  citizenInfoCommission: '委托：{title}',
  citizenInfoUntitled: '（还没有标题）',
  citizenInfoState: '{outcome} · 完成了 {turns} 回合',
  citizenInfoDays: '{days} 天前动过',
  citizenInfoToday: '今天动过',
  citizenInfoFolders: '常去：{folders}',
  outcomeRunning: '正在办',
  outcomeScheduled: '定时任务',
  outcomeInterrupted: '做到一半停下了',
  outcomeCompleted: '办完了',
  outcomeOpen: '还没开头',
  transcriptTitle: '{name}的那场会话：{title}',
  transcriptYou: '你',
  transcriptAthena: '雅典娜',
  transcriptOmitted: '（更早的 {count} 条没有列出）',
  transcriptEmpty: '这场会话里还没有说过话。',
  // 讲台前的辩论（M-E）
  interactBema: '上讲台辩论',
  bemaTitle: '讲台',
  bemaBody: '提一个议题，你站正方，雅典娜站反方，辩三轮；街上的居民当陪审团。',
  bemaRecord: '你 {wins} 胜 {losses} 负 {ties} 平',
  bemaPropose: '提一个议题',
  debateName: '辩论',
  debateMotionStatus: '你站正方，雅典娜站反方',
  debateMotionPlaceholder: '提一个议题……（比如：周末应该完全不碰工作）',
  debateArguePlaceholder: '第 {round} / {rounds} 轮：说你的论点……',
  debateArgueStatus: '议题：{motion}',
  debateSend: '陈述',
  debateHint: 'Enter 陈述 · Esc 不辩了',
  debateAthenaThinking: '雅典娜在想她的反驳……',
  debateJudging: '陪审团在商议……',
  debateRound: '第 {round} / {rounds} 轮',
  debateYou: '你',
  debateAthena: '雅典娜',
  debateVotes: '陪审团：正方 {you} 票，反方 {athena} 票',
  debateYouWin: '你赢了',
  debateAthenaWins: '雅典娜赢了',
  debateTie: '平局',
  debateClose: '结束',
  // 户与民居（M-D）
  householdFounded: '「{name}」在城里安了家。',
});

/** 说出去之后最多等多久（C# 那边一次调用 25 秒就放弃；多留 5 秒给排队和回传）。 */
export const ASK_TIMEOUT_MS = 30_000;

/**
 * 头顶的话留多久（2026-10-13：居民的回答出现在他头顶的气泡里，不在卡片上）：说完之后 4 秒，再按字数每字加 90 毫秒，最后 400 毫秒淡出。
 * 你还在和他聊（对话栏借给了他）时，他最近的那一句一直留着，直到下一句。
 */
export const SPEECH = Object.freeze({ holdMs: 4000, perCharMs: 90, fadeMs: 400, dotMs: 360 });

/** 一句话此刻的透明度：还在想、还在说（流式）是 1；说完之后按 SPEECH 留一阵再淡出。keep 为真时一直留着。 */
export function speechAlpha(saying, now, keep = false) {
  if (!saying) return 0;
  if (saying.typing || saying.doneAt == null || keep) return 1;
  const hold = SPEECH.holdMs + SPEECH.perCharMs * [...String(saying.text ?? '')].length;
  const left = saying.doneAt + hold - now;
  return Math.max(0, Math.min(1, left / SPEECH.fadeMs));
}

/** 还在想的那一会儿，气泡里是一串循环的点（"·" "··" "···"）。 */
export function thinkingDots(now) {
  return '·'.repeat(1 + (Math.floor(now / SPEECH.dotMs) % 3));
}

/** 居民打招呼与闲聊的话（"|" 分隔；闲聊是"甲说|乙答"一对）。实时模式下由 C# 按界面语言覆盖（Polis.Citizen.*）。 */
const fillText = (template, vars) => String(template ?? '').replace(/\{(\w+)\}/g, (_, k) => vars[k] ?? '');

/** 居民卡片上那场会话的信息（2026-10-13）：委托的标题、办到哪一步与完成了几回合、多久前动过、常去的文件夹。只有标题、数字和文件夹名。 */
export function citizenInfoLines(data, ui) {
  const outcome = ui[`outcome${String(data.outcome ?? 'open').replace(/^./, (c) => c.toUpperCase())}`] ?? ui.outcomeOpen;
  const lines = [
    fillText(ui.citizenInfoCommission, { title: data.title || ui.citizenInfoUntitled }),
    fillText(ui.citizenInfoState, { outcome, turns: data.turns ?? 0 }),
    data.days > 0 ? fillText(ui.citizenInfoDays, { days: data.days }) : ui.citizenInfoToday,
  ];
  if (data.folders?.length) lines.push(fillText(ui.citizenInfoFolders, { folders: data.folders.join('、') }));
  return lines;
}

/**
 * 那场会话的预览（C# 的 citizen-transcript）拼成给卷轴阅读器的 Markdown：每条一个小标题（你 / 雅典娜）加正文。
 * 只有你和雅典娜看得见的话——C# 那边已经把工具、推理拿掉了；字只经阅读器的 textContent 进 DOM。
 */
export function transcriptMarkdown(m, ui) {
  const entries = Array.isArray(m?.entries) ? m.entries : [];
  if (entries.length === 0) return ui.transcriptEmpty;
  const parts = [];
  if (m.omitted > 0) parts.push(`*${fillText(ui.transcriptOmitted, { count: m.omitted })}*`);
  for (const e of entries) parts.push(`#### ${e.role === 'user' ? ui.transcriptYou : ui.transcriptAthena}`, String(e.text ?? ''));
  return parts.join('\n\n');
}

/** 居民对你打招呼的话（本地的；两位居民之间的闲聊 2026-10-13 起只用模型，没有本地台词）。 */
export const CITIZEN_ZH = Object.freeze({
  greet: '你好呀。|日安。|又见面了。|今天天气不错。|你也出来走走？',
});

/**
 * @param {object} options
 * @param {URLSearchParams} options.params
 * @param {object} options.status window.__polis（测试读它）
 * @param {(type: string, fields?: object) => void} options.send 发一条意图给宿主（夹具模式里只记下）
 * @param {() => void} [options.onTalk] 走到雅典娜跟前按 E：打开对话栏（实时模式才有；夹具里没有人在听）
 * @param {(blocked: boolean) => void} [options.onUiBlocked] 画面中间被卡片、面板、海图、雾或过场占着（对话栏让开）
 * @param {(mode: string) => void} [options.onViewChange] 视角变了（按钮、V 键，或者拖动平移把镜头交还给你）
 * @param {() => boolean} [options.modalOpen] 页面上有别的东西挡着（卷轴阅读器）：这时方向键不走路
 * @param {number} [options.topInset] 顶上被 DOM 工具条占着的高度（实时模式左上角的视角与海图）：顶上的面板往下让
 * @param {string} [options.defaultView] 缺省视角：自由（你是主角）
 * @param {() => boolean} [options.talkOpen] 对话栏此刻是不是展开着（M3：你站着和她说话、她听着；托付之后你比划两秒）
 */
export function createGame({ params, status, send, onTalk = null, onUiBlocked = null, onViewChange = null, modalOpen = () => false, topInset = 0, defaultView = 'free', talkOpen = () => false, talkMode = () => 'talk', onChat = null, onChatReplied = null, onLendEnd = null, onDebate = null, onRead = null }) {
  const R = createRenderer({
    canvas: document.getElementById('scene'),
    ao: params.get('ao') !== '0', shadows: params.get('shadows') !== '0',
    grade: params.get('grade') === '1', accent: params.get('accent') !== '0',
  });
  const M = makeMaterials();
  const overlay = createOverlay(document.getElementById('overlay'));
  let assets = createAssetLibrary(M);
  status.assets = assets.report;

  let ui = { ...UI_ZH };
  let publicNames = { ...PUBLIC_NAMES };
  let citizenWords = { ...CITIZEN_ZH };

  // —— 城 ——
  let city = null;
  let cityKey = null;
  let layout = null;
  let world = null;
  let sanctuary = false;
  let fogInfo = null;
  const flashes = new Map();   // 建筑 key → 外部改动的闪动开始时间

  // —— 积累（C# 推来的，全部可以从存档与会话重新推导） ——
  let groveTrees = [];         // [{ conversationId, title, index, stage, turns, interrupted, scheduled }]
  let residents = [];          // [{ kind, met, name（职业加希腊名）, trade, greekName, intro }]：十位职业居民每座城都在（2026-10-13）
  // 居民即会话（M-B）：C# 推来街上的（≤ 32 位）、老居民（名字刻在碑上）与人口
  let citizens = { street: [], elders: [], census: null };
  let citizenTiers = new Map();      // id → 'A' | 'B' | 'C'（citizens.assignTiers）
  let chatTalk = null;               // 此刻在聊天的那一对（citizens.pickChat；说什么由 C# 的模型定，ambient-chat → talk → ambient-end）
  const chatCooldown = new Map();    // 一对人上次聊天的时刻
  let nextChatPickAt = 0;
  let greeting = null;               // { id, start, line }：此刻跟你打招呼的那一位
  const greetedAt = new Map();       // 每一位上次跟你打招呼的时刻
  // 和一位居民聊天（2026-10-13，原来 M-C 的问话）：对话栏借给了他（'typing' 你在写、'waiting' 说出去了等他答）；
  // 他的话在他头顶（sayings：id → { text, typing, at, doneAt }，流式的半句话边到边换），你的话在你头顶（mine）
  let convo = null;                  // { id, status, sentAt }
  const sayings = new Map();
  let mine = null;                   // { text, at }
  /** 对话栏开着、而且是在和雅典娜说话（托付；2026-10-13 起要走到她身边按 E——平时的栏是你自己说话）。 */
  const talkingToAthena = () => talkOpen() && talkMode() === 'talk' && !convo && !lentToDebate();
  // 你自己说话（2026-10-13）：接你话的那一位（他站定、转身朝你，直到说完再留一会儿）；有一段写好的话在雅典娜那里等你
  let heard = null;                  // { id, until }
  let athenaHasWords = false;
  // 讲台前的辩论（M-E）：C# 推来的这一场（没有就是 null）、战绩、舞台站位（这座城算一次）、围过来的陪审团
  let debate = null;                 // { state, id, motion, round, rounds, jury, transcript, verdict, record }
  let debateRecord = { wins: 0, losses: 0, ties: 0 };
  let stageInfo = null;              // debate.debateStage 的结果（换城清掉）
  const audienceOf = new Map();      // 居民 id → { index, from, to, ms, start, dist, supporter }
  let debateSeen = { id: null, athenaTurns: 0, lentRound: 0, gathered: false, doneAt: 0 };
  let debateNotice = null;           // { text, until }：没开成之类 C# 替人说的那一句
  let motionPending = false;         // 对话栏借给了"提一个议题"，还没收到这一场
  const debateActive = () => !!debate;
  /** 对话栏此刻借给了辩论（提议题、或者在辩）。 */
  const lentToDebate = () => motionPending || (!!debate && (debate.state === 'awaiting' || debate.state === 'athena'));
  let suggestions = [];        // [{ id, line }]
  let oracle = null;           // { exists, title, body, latestConversationId }
  let islands = [];            // 海图
  let panel = null;            // { type: 'resident' | 'board' | 'oracle' | 'venue' | 'chart', … }
  let placing = null;          // 正在摆放的藏品 id
  // 互动（M3 第三轮，用户：互动从鼠标点击改为控制角色接近目标、按交互键触发）：每一帧挑出你身边够得着的那一样（focus），
  // 脚下画一块"E 和雅典娜说话"；按 E 才触发。鼠标不再点城里的东西——叠加层上的按钮、海图、"雅典娜在这边"仍然是点的
  let focus = null;            // { type: 'athena' | 'resident' | 'item' | 'slot' | 'tree' | 'board' | 'oracle' | 'venue', id?, venue?, slot?, x, z }
  let forcedFocus = null;      // 测试与运动自检：不管远近，就当你站在它跟前（setHover）
  // 她头顶的话：本地的几句（奠基揭幕时那一句、揭幕之后委托板上的第一条建议），不来自任何回合
  let speech = [];             // [{ text, t, minMs }]（t：页面时钟，frame 的 now）
  let pendingBoardLine = null; // 揭幕之后要说第一条建议的时刻

  // —— 玩家：WASD / 方向键走路（真实时间，不随回放暂停）。点地面不走——鼠标只管互动 ——
  const player = { pos: { x: 2.6, z: 4.4 }, yaw: Math.PI * 0.85, walkClock: 0, moving: false, dirty: false, stoppedAt: 0 };
  /** 你身边一个她能站的地方（你在远处开口时她走过来站在这儿）：先试你右前方，撞墙就换个方向，都不行就站你这儿。 */
  const playerSite = () => {
    const offsets = [[0.9, 0.4], [-0.9, 0.4], [0.4, 0.9], [0.4, -0.9], [1.2, 0], [-1.2, 0], [0, 1.2], [0, -1.2]];
    for (const [dx, dz] of offsets) {
      const p = { x: player.pos.x + dx, z: player.pos.z + dz };
      if (!layout || layout.walkable(p)) return `pt:${p.x.toFixed(2)},${p.z.toFixed(2)}`;
    }
    return `pt:${player.pos.x.toFixed(2)},${player.pos.z.toFixed(2)}`;
  };
  const pressed = new Set();   // 按住的方向：'up' | 'down' | 'left' | 'right'
  let running = false;
  // —— M3 的动画状态：一次性的动作（接过、挥手）、托付的时刻（之后两秒多做"说"的手势）、看向谁 ——
  const anim = { playerOneShot: null, saidAt: -Infinity };
  const TALK_GESTURE_MS = 2600;
  const playerHead = new THREE.Vector3();
  const athenaChest = new THREE.Vector3();

  // —— 雅典娜（athena.mjs）：换城时整个重建。clock 是页面的时钟（毫秒，frame 的 now，只进不退） ——
  let athenaSim = null;
  let clock = 0;
  const createAthenaFor = (startAt) => createAthena({ travelMs: (a, b) => layout.travelMs(a, b), home: 'agora:0', startAt });

  // —— 成果：C# 推来的藏品。这座城装好之后才到的待收成果是刚做完的那一回合：直接出现在你脚下 ——
  const items = new Map();      // id → { item, object, display, collectedAt, spot, revealAt }
  let itemsPrimed = false;      // 这座城的第一份成果清单到过了没有（之后再来的新成果才算"刚交到你手上"）
  let card = null;              // 当前打开的成果卡片的 id
  let notices = [];
  let herald = null;
  let voyage = null;            // { startedAt, arriveAt, caption, fadeOutAt }
  let reveal = null;            // 第一次进入的俯瞰揭幕
  let lastInputAt = -Infinity;

  // —— 镜头：三种视角，缺省自由（controls.mjs）。左键拖动绕着转（自己处理：拖过几像素才算转，点击不会把镜头拧歪）；
  // 右键、或者按住 Shift / Ctrl / ⌘ 用左键拖动是平移（交给 OrbitControls——它在 enableRotate 为假时不处理左键的转动）；
  // 滚轮缩放。在"跟着谁"的视角里平移，镜头就交还给你（切到自由）。 ——
  const camera = R.camera;
  const target = new THREE.Vector3(0, 1, 0);
  const placeCamera = (center, az = CAMERA.azimuth, el = CAMERA.elevation) => {
    camera.position.copy(center).add(new THREE.Vector3().setFromSphericalCoords(CAMERA.distance, THREE.MathUtils.degToRad(90 - el), THREE.MathUtils.degToRad(az)));
  };
  placeCamera(target);
  camera.lookAt(target);
  const controls = new OrbitControls(camera, R.renderer.domElement);
  controls.target.copy(target);
  controls.enableRotate = false;
  controls.screenSpacePanning = true;
  controls.mouseButtons = { LEFT: THREE.MOUSE.ROTATE, MIDDLE: THREE.MOUSE.DOLLY, RIGHT: THREE.MOUSE.PAN };
  controls.minZoom = 0.4; controls.maxZoom = 6.5;   // M3（决定 4）：能推到近景——人物约 300 像素高（中景 2.6 是 130）
  controls.minPolarAngle = THREE.MathUtils.degToRad(90 - VIEW.maxElevation);
  controls.maxPolarAngle = THREE.MathUtils.degToRad(90 - VIEW.minElevation);
  controls.update();
  let follow = VIEW_MODES.includes(params.get('follow')) ? params.get('follow') : VIEW_MODES.includes(defaultView) ? defaultView : 'free';
  let dragButton = -1;
  let dragModified = false;   // 按下时按着 Shift / Ctrl / ⌘：左键拖动是平移
  let glide = null;   // 镜头被"请"去某处（摆放时看场馆）：{ to, until }
  // M3（决定 4）近景：对话栏开着（你和雅典娜说话）、居民的卡片开着，镜头推近到两个人中间、俯角放低；收起就退回原来的远近
  const CLOSE_UP = { zoom: 4.6, elevation: 26, tauMs: 380 };
  /** 陪审团判票与判完（M-E）：镜头看着舞台，远一点、高一点，两圈半圆的人都在画面里（截图审查：退回原来的远近，人只有几像素高）。 */
  const JURY_VIEW = { zoom: 2.4, elevation: 36 };
  let closeUp = null;    // { kind: 'talk' | 'resident', id, saved: { zoom, elevation } }
  // 退出近景之后往回走的目标：{ zoom, elevation }。退回要走两秒多，这期间你滚了滚轮，远近就归你（zoom 置空）、拖动转了镜头，俯角就归你
  // （elevation 置空）——否则每帧都把它拽回记住的值，滚轮像是失灵，只有往回滚、或者停手等它走完才"恢复"（2026-10-12）
  let restoring = null;
  controls.addEventListener('start', () => {
    if (dragButton === 2 || (dragButton === 0 && dragModified)) setFollow('free');
    lastInputAt = performance.now();
  });

  function setFollow(mode) {
    if (!VIEW_MODES.includes(mode)) return;
    glide = null;
    if (mode === follow) return;
    follow = mode;
    status.view = mode;
    onViewChange?.(mode);
  }
  status.view = follow;

  /** 镜头此刻的方位角（弧度，与 Spherical.theta 同一个量）：键盘走路"朝画面上方"按它算。 */
  const offsetTmp = new THREE.Vector3();
  function cameraAzimuth() {
    offsetTmp.copy(camera.position).sub(controls.target);
    return Math.atan2(offsetTmp.x, offsetTmp.z);
  }

  /** 绕着镜头的目标点转（拖动的像素）：方位角不限，仰角夹在 VIEW 的上下限之间。 */
  const spherical = new THREE.Spherical();
  function orbitBy(dxPx, dyPx) {
    offsetTmp.copy(camera.position).sub(controls.target);
    spherical.setFromVector3(offsetTmp);
    const next = orbitAngles(THREE.MathUtils.radToDeg(spherical.theta), 90 - THREE.MathUtils.radToDeg(spherical.phi), dxPx, dyPx);
    spherical.theta = THREE.MathUtils.degToRad(next.azimuth);
    spherical.phi = THREE.MathUtils.degToRad(90 - next.elevation);
    camera.position.copy(controls.target).add(offsetTmp.setFromSpherical(spherical));
    if (restoring) restoring.elevation = null;
    lastInputAt = performance.now();
  }

  /** 镜头的目标点挪到 (x, z)，角度与缩放不变（换城时看着你）。 */
  function centerCameraOn(x, z) {
    const delta = new THREE.Vector3(x, 1, z).sub(controls.target);
    controls.target.add(delta);
    camera.position.add(delta);
  }

  // —— 城的装载：整座重建，或者按座替换 ——
  function loadCity(doc, options = {}) {
    const keepPlayer = options.key != null && options.key === cityKey;
    if (world) world.dispose();
    // 从一座城换到另一座（不是第一次装城）：上一座城的居民、聊天、招呼都不算了（C# 随后推来这座城的居民）。
    // 第一次装城时不清：夹具模式先推积累、再装城
    const switchingCity = city != null && !keepPlayer;
    city = doc;
    cityKey = options.key ?? null;
    sanctuary = !!options.sanctuary;
    layout = createLayout({ ...doc, groveTrees: groveTrees.length });
    world = buildWorld(R.scene, doc, layout, M, { names: publicNames, sanctuary, assets });
    for (const record of items.values()) { record.object = null; record.display = null; }   // 旧城的物件随旧城拆掉
    neighbourSites = null;
    player.y = null;
    athenaY = null;
    athenaYaw = null;
    if (!keepPlayer) {
      const p = options.player;
      player.pos = p && Number.isFinite(p.x) && Number.isFinite(p.z) ? { x: p.x, z: p.z } : { x: 2.6, z: 4.4 };
      player.moving = false;
      player.dirty = false;
      pressed.clear();
      itemsPrimed = false;   // 新的一座城：它的第一份成果清单是"一直在那儿的"，不是刚交到你手上的
      if (switchingCity) {
        citizens = { street: [], elders: [], census: null };
        chatTalk = null;
        nextChatPickAt = 0;   // 换了城：上一座城"没配模型、不再挑"的结论不带过来（设置可能改过了）
        greeting = null;
        citizenTiers = new Map();
        if (convo) { convo = null; status.chatting = null; onLendEnd?.('chat'); }
        sayings.clear();
        mine = null;
        if (debate || motionPending) onLendEnd?.('debate');
        debate = null;
        motionPending = false;
        stageInfo = null;
        audienceOf.clear();
        debateSeen = { id: null, athenaTurns: 0, lentRound: 0, gathered: false, doneAt: 0 };
        status.debate = null;
      }
      // 存档里这座城的远近：靠岸时镜头推回的就是它（没有存档就推回出港前的远近）
      if (p && Number.isFinite(p.zoom)) { camera.zoom = p.zoom; camera.updateProjectionMatrix(); if (voyage) voyage.restoreZoom = p.zoom; }
      // 换了一座城：镜头先看着你（跟着雅典娜的视角下一帧自己会转过去）
      if (follow !== 'athena') centerCameraOn(player.pos.x, player.pos.z);
    }
    const startAt = Math.max(clock, options.now ?? clock);
    if (!keepPlayer || !athenaSim) { athenaSim = createAthenaFor(startAt); routes = new WeakMap(); athenaY = null; athenaYaw = null; }
    if (options.founding) {
      // 第一次进入这座城（5.4）：俯瞰揭幕，她说"这是你的城邦，每座建筑都是你的一个文件夹"，揭幕之后再说委托板上的第一条建议；
      // 之后从存档恢复，不再播
      reveal = { startedAt: performance.now() };
      speech = [];
      say(ui.founding, startAt, REVEAL_MS);
      pendingBoardLine = { t: startAt + REVEAL_MS + 400 };
    }
    applyAccumulation();
    status.city = { buildings: doc.buildings.length, vacant: (doc.vacant ?? []).length, market: (doc.market ?? []).length, sanctuary, key: cityKey };
  }

  /** 积累系统的现状画进这座城：树、居民、委托板的纸条、神谕的三足鼎、藏品的位次。 */
  function applyAccumulation() {
    if (!world) return;
    world.setGrove(groveTrees);
    world.setResidents(residents, (kind) => residents.find((r) => r.kind === kind)?.name ?? kind);
    world.setCitizens(citizens.street);
    world.setElders(elderGroups(citizens.elders));
    status.citizens = { street: world.citizens.size, census: citizens.census, steles: world.steles.size };
    world.setBoard(suggestions.length);
    world.setOracle(!!oracle?.exists);
    status.grove = { trees: world.groveTrees.length, terraces: world.groveTerraces.length };
    status.residents = [...world.residents.keys()];
  }

  /**
   * 同一座城的新版本（核对之后、或外部改动之后）：能按座替换就按座替换——
   * 新建筑的地块在当前这一圈之内、海岸线没变；否则整城重建（人物与时间线保留）。
   * origins：建筑 key → 'athena' | 'external'；外部改动的建筑名牌闪一下（没有光、没有角色动作）。
   */
  function updateCity(doc, origins = {}) {
    if (!city || !layout || !world || (doc.seaStartsAtZ ?? 2) !== (city.seaStartsAtZ ?? 2)) {
      loadCity(doc, { key: cityKey, sanctuary, now: clock });
      return 'rebuilt';
    }
    const before = new Map(city.buildings.map((b) => [b.key, b]));
    const after = new Map(doc.buildings.map((b) => [b.key, b]));
    const fits = doc.buildings.every((b) => layout.plots.has(`b:${b.key}`) || Math.max(Math.abs(b.plot.x), Math.abs(b.plot.z)) <= layout.ring);
    const moved = doc.buildings.some((b) => before.has(b.key) && (before.get(b.key).plot.x !== b.plot.x || before.get(b.key).plot.z !== b.plot.z));
    const marketChanged = JSON.stringify(doc.market ?? []) !== JSON.stringify(city.market ?? []);
    if (!fits || moved || marketChanged) {
      // 改名时 C# 已经把账本条目换了名字：同一块地换了一个 key，这也走整城重建（地块没动）
      const keep = { player: { ...player.pos }, time: clock };
      loadCity(doc, { key: cityKey, sanctuary, now: keep.time });
      player.pos = keep.player;
      markExternal(origins);
      return 'rebuilt';
    }
    let changed = 0;
    for (const [key, b] of after) {
      const old = before.get(key);
      if (old && sameBuilding(old, b)) continue;
      if (!old && !layout.plots.has(`b:${key}`)) layout.addBuildingPlot(b);
      world.replaceBuilding(b);
      changed++;
    }
    for (const [key] of before) {
      if (after.has(key)) continue;
      const v = (doc.vacant ?? []).find((x) => x.key === key) ?? { key, plot: before.get(key).plot };
      world.vacate(v);
      layout.plots.delete(`b:${key}`);
      changed++;
    }
    city = doc;
    markExternal(origins);
    status.city = { ...status.city, buildings: doc.buildings.length, vacant: (doc.vacant ?? []).length };
    return changed > 0 ? 'patched' : 'unchanged';
  }

  function sameBuilding(a, b) {
    return a.kind === b.kind && a.sizeClass === b.sizeClass && a.state === b.state && a.incomplete === b.incomplete && (a.label ?? null) === (b.label ?? null);
  }

  /**
   * 外部改动的建筑名牌闪一下；新立的一户（M-D，origin 'household'）也闪一下，雅典娜说一句"……在城里安了家"。
   * 城变了之后居民按新的地块重排日程：家是一座刚盖起来的民居的人，原来只能先住在广场上（layout 里还没有那座民居）。
   */
  function markExternal(origins) {
    const t = performance.now();
    for (const [key, origin] of Object.entries(origins ?? {})) {
      if (origin === 'external' || origin === 'household') flashes.set(`b:${key}`, t);
      if (origin === 'household') {
        const b = city?.buildings?.find((x) => x.key === key);
        say(fill(ui.householdFounded, { name: b?.label ?? key }), clock, 3000);
      }
    }
    replanCitizens();
  }

  /** 城的地块变了：家不在原先排日程时那块地上的居民（多半是刚有了民居的），下一帧重排；去处表也重新取。 */
  function replanCitizens() {
    neighbourSites = null;
    if (!world?.citizens) return;
    for (const c of world.citizens.values()) if (c.plan && c.plan.home.site !== c.data.home) c.plan = null;
  }

  // —— 每一步的路线（缓存） ——
  let routes = new WeakMap();
  const routeOf = (step) => {
    if (!routes.has(step)) routes.set(step, layout.route(step.from, step.to));
    return routes.get(step);
  };

  function facingAt(site) {
    if (site === 'agora' || site.startsWith('agora:') || site === 'harbor') return 0;
    if (site === 'gate') return Math.PI;
    const p = layout.sitePoint(site);
    if (site.startsWith('pt:')) return Math.atan2(player.pos.x - p.x, player.pos.z - p.z);
    const plot = layout.plots.get(site);
    return plot ? Math.atan2(plot.cx - p.x, plot.cz - p.z) : 0;
  }

  const ease = (f) => f * f * (3 - 2 * f);
  const safeSite = (site) => (site && (site === 'agora' || site === 'harbor' || site === 'gate' || site.startsWith('agora:') || site.startsWith('pt:') || layout.plots.has(site)) ? site : 'agora');

  /**
   * 雅典娜在时刻 now 的位置、朝向，以及她这一刻的动作（走 / 跑 / 停）。她没有光了：2026-10-12 去掉了始终存在的圣光，
   * 2026-10-13 去掉了瞬移（她唯一的那一道光）。pose.teleport 恒为 null、glow 恒为 0，留着这两个字段只是让旧的读法不出错。
   */
  function athenaPose(state, now) {
    const step = state.step;
    const pose = { x: 0, z: 0, yaw: 0, visible: true, glow: 0, teleport: null, action: 'idle', t: now, style: {} };
    if (!step) return pose;
    const t = now - step.start;
    if (step.kind === 'walk' || step.kind === 'run') {
      // 她走路（M3），远了就跑（2026-10-13 起没有瞬移）：步子的相位按走过的距离推进（起步和停步时慢下来，脚也慢下来，不打滑）。
      // 追着你跑（递卷轴）的那一段不缓起缓停：你还在走，她一直是跑着的
      const route = routeOf(step);
      const running = step.kind === 'run';
      const raw = stepProgress(step, now);
      const f = step.reason === 'chase' ? raw : ease(raw);
      const p = pointAlong(route, f);
      const speed = running ? RUN_SPEED : SPEED;
      Object.assign(pose, { x: p.x, z: p.z, yaw: p.dir, glow: 0, action: running ? 'run' : 'walk', t: ((polyLength(route) * f) / speed) * 1000, style: { speed, stride: running ? ATHENA_STRIDE * 1.5 : ATHENA_STRIDE } });
      return pose;
    }
    const at = safeSite(step.at ?? state.position);
    const p = layout.sitePoint(at);
    Object.assign(pose, { x: p.x, z: p.z, yaw: facingAt(at), t });
    return pose;
  }

  /** 留住她：你离她 2.5 米以内、或者对话栏开着——她不再走开（athena.mjs 的 hold）。 */
  function athenaHeld() {
    if (talkingToAthena() || debateActive()) return true;
    const a = world?.athena;
    return !!a && a.visible && Math.hypot(a.position.x - player.pos.x, a.position.z - player.pos.z) < NEAR_ATHENA;
  }

  function applyAthena(pose, line = null, dt = 16) {
    const a = world.athena;
    const ground = world.groundAt(pose.x, pose.z);
    athenaY = athenaY == null || pose.teleport ? ground : stepToward(athenaY, ground, dt);
    a.position.set(pose.x, athenaY + 0.012, pose.z);
    // 转身是转过去的，不是一帧跳过去的（时间常数 120 毫秒，和居民一样按比例转）；瞬移和第一帧直接摆好。
    // 2026-10-12，用户：雅典娜移动后偶发裙子剧烈抖动——她的朝向原来一帧跳过去：停步时从路的方向跳到目的地的朝向、
    // 走到路的拐角跳到下一段，最多 180°，裙子是布料模拟，被一帧甩过去要抖一秒多
    athenaYaw = athenaYaw == null || pose.teleport ? pose.yaw : turnToward(athenaYaw, pose.yaw, 1 - Math.exp(-dt / 120));
    a.rotation.y = athenaYaw;
    a.visible = pose.visible;
    // M3：停着的时候，旁白气泡在说就做"说"的手势，对话栏开着、你在旁边就侧耳听；离你近就看着你（做事和交付时不看）
    let action = pose.action;
    const style = { ...pose.style, seed: 7 };
    const dp = Math.hypot(pose.x - player.pos.x, pose.z - player.pos.z);
    if (action === 'idle' && !pose.teleport) {
      if (line && line.alpha > 0.3) { action = 'talk'; style.speaker = true; }
      else if (talkingToAthena() && dp < 7) { action = 'talk'; style.speaker = false; }
    }
    if (dp < 8 && !pose.teleport) style.lookAt = playerHead.set(player.pos.x, 1.5, player.pos.z);
    world.athenaFigure.pose(action, pose.t, style);
    status.athenaPose = action;
    // 光属于雅典娜：只在她化作一道光的那一下亮（第一版有一个 0.6 的底：站着脚下也有一圈光）。
    // 夜里她的光照得更亮、更远：夜城里只有她这一处亮
    const light = a.userData.light;
    if (!light) return;
    const boost = 1 + 1.4 * nightK;
    light.position.set(0, 0.45, 0.3);
    light.distance = 4.5 + 2.5 * nightK;
    light.intensity = pose.glow * 3 * boost;
  }

  /** 港口的船此刻在出海的哪一段（0..1），泊着时为 null（BOAT_TRIP：按墙钟的时刻表）。 */
  function boatTripAt(wall) {
    const phase = (((wall + BOAT_TRIP.offsetMs) % BOAT_TRIP.periodMs) + BOAT_TRIP.periodMs) % BOAT_TRIP.periodMs;
    return phase < BOAT_TRIP.tripMs ? phase / BOAT_TRIP.tripMs : null;
  }

  // —— 场景里随时间变化的东西：船、居民、成果 ——
  function applyEffects(wall, dt) {
    const boat = world.boat;
    const prevBoatZ = boat.position.z;
    boat.position.copy(world.boatHome);
    boat.rotation.set(0, 0, 0);
    const f = boatTripAt(wall);
    if (f != null) {
      const out = f < 0.42 ? ease(f / 0.42) : f < 0.58 ? 1 : 1 - ease((f - 0.58) / 0.42);
      boat.position.z += out * 32;
      boat.position.x += Math.sin(out * Math.PI) * 2.5;
      boat.rotation.y = f < 0.5 ? 0 : Math.PI;
    }
    // 船随浪起伏：船身中点、船头、船尾、两舷各取一点海面的高度（和海的顶点用同一组涌浪，art/water.mjs），抬高、俯仰、横摇
    const sec = wall / 1000, bx = boat.position.x, bz = boat.position.z;
    const hMid = swellHeight(bx, bz, sec);
    boat.position.y += hMid;
    boat.rotation.x = Math.atan2(swellHeight(bx, bz - 3.5, sec) - swellHeight(bx, bz + 3.5, sec), 7) * 1.4;
    boat.rotation.z = Math.atan2(swellHeight(bx - 1, bz, sec) - swellHeight(bx + 1, bz, sec), 2) * -1.4;
    const boatSpeed = dt > 0 ? ((boat.position.z - prevBoatZ) / dt) * 1000 : 0;
    M.water.userData.water.update({ time: sec, night: nightK, boat: { x: bx, z: bz, speed: Math.abs(boatSpeed) > 20 ? 0 : boatSpeed }, shores: world.waterShores });

    const residentSaying = animateResidents(wall, dt);
    placeItems();
    return residentSaying;
  }

  /**
   * 居民：多数时候待在自己的位置上——停一会儿、干一会儿自己的活；每隔一阵沿一小段路走出去再走回来。
   * 节拍按居民的名字错开，满城的人不会同时抬手。
   */
  const ropeTo = new THREE.Vector3();
  function animateResidents(wall, dt) {
    const said = [];
    for (const r of world.residents.values()) {
      const cycle = 26000;
      const t = (wall + r.phase * 3) % cycle;
      // 出门的落脚点由世界挑（一路上没有东西挡着，M3）；没有能走的方向就待在家里，只停和做事
      const out = r.out ?? r.home;
      const span = Math.hypot(out.x - r.home.x, out.z - r.home.z);
      const stays = span < 0.5;
      // 走路的相位按走过的距离推进（脚不打滑）：ease(f) 是走过的比例
      const walked = (f) => ((span * ease(f)) / 1.3) * 1000;
      let action = 'idle', at = t, x = r.home.x, z = r.home.z, yaw = r.home.yaw;
      if (r.station) { action = t < 4000 ? 'idle' : 'work'; at = action === 'work' ? t - 4000 : t; }   // 在工位上：大半时间在干活
      else if (stays) { action = t < 9000 || t >= 16000 ? 'idle' : 'work'; at = action === 'work' ? t - 9000 : t; }
      else if (t < 9000) { action = 'idle'; at = t; }
      else if (t < 16000) { action = 'work'; at = t - 9000; }
      else if (t < 18600) {
        const f = (t - 16000) / 2600;
        x = r.home.x + (out.x - r.home.x) * ease(f); z = r.home.z + (out.z - r.home.z) * ease(f);
        yaw = Math.atan2(out.x - r.home.x, out.z - r.home.z); action = 'walk'; at = walked(f);
      } else if (t < 21400) { action = 'idle'; x = out.x; z = out.z; yaw = Math.atan2(out.x - r.home.x, out.z - r.home.z) + Math.PI * 0.5; at = t; }
      else if (t < 24000) {
        const f = (t - 21400) / 2600;
        x = out.x + (r.home.x - out.x) * ease(f); z = out.z + (r.home.z - out.z) * ease(f);
        yaw = Math.atan2(r.home.x - out.x, r.home.z - out.z); action = 'walk'; at = walked(f);
      } else { action = 'idle'; at = t; }
      // 他的卡片开着、或者你在和他聊（对话栏借给了他）：站在原地，转身面向你，先挥一下手，然后一边比划一边讲（M3）
      const chatting = convo?.id === `resident:${r.kind}` || (heard?.id === `resident:${r.kind}` && wall < heard.until);
      const greeting = (panel?.type === 'resident' && panel.id === r.kind) || chatting;
      const style = { variant: r.work, speed: 1.3, stride: 1.3, seed: r.phase };
      // 收缆的水手：缆拴在泊着的船上；船出海去了，他就站在桥沿上等它回来（不朝着空海收缆）
      if (r.station?.rope === 'boat' && action === 'work') {
        if (world.boatMoored()) style.ropeTo = world.boatCleat(ropeTo);
        else action = 'idle';
      }
      if (greeting) {
        if (!r.greetAt) r.greetAt = wall;
        x = r.home.x; z = r.home.z;
        yaw = Math.atan2(player.pos.x - x, player.pos.z - z);
        const since = wall - r.greetAt;
        const saying = sayings.get(`resident:${r.kind}`);
        if (since < ONE_SHOT_MS.wave) { action = 'wave'; at = since; } else { action = 'talk'; at = since; style.speaker = !chatting || (!!saying && !saying.typing && (saying.doneAt == null || wall - saying.doneAt < 2500)); }
      } else {
        r.greetAt = null;
      }
      const dp = Math.hypot(player.pos.x - x, player.pos.z - z);
      if (dp < 4 && action !== 'work') style.lookAt = playerHead.set(player.pos.x, 1.5, player.pos.z);
      r.yawNow = r.yawNow == null ? yaw : turnToward(r.yawNow, yaw, 1 - Math.exp(-dt / 180));
      r.figure.root.position.set(x, r.home.y ?? world.groundAt(x, z) + 0.012, z);
      r.figure.root.rotation.y = r.yawNow;
      r.label.anchor.set(x, (r.home.y ?? 0) + (r.figure.root.userData.height ?? 1.75) + 0.55, z);   // 站在讲台、锻炉地坪上的，名牌跟着高上去
      r.action = action;
      // 分档（tierPeople，M-B）：画面外的不摆（视锥裁剪本来也不画他）；画面里离你不是最近那几位的，布料停着、手指脚趾藏起来
      const tier = citizenTiers.get(`resident:${r.kind}`) ?? 'A';
      if (tier === 'C' && !greeting) continue;
      style.cloth = tier === 'A' || greeting;
      r.figure.setDetail(tier === 'A' || greeting);
      r.figure.pose(action, at, style);
      // 他头顶的话（你和他聊天时他说的）：和居民一样，在想时是一串点，说完留一阵再淡出；你还在和他聊就一直留着
      const saying = sayings.get(`resident:${r.kind}`);
      if (saying) {
        const alpha = speechAlpha(saying, wall, chatting);
        if (alpha <= 0) sayings.delete(`resident:${r.kind}`);
        else said.push({ id: `resident:${r.kind}`, text: saying.typing ? thinkingDots(wall) : saying.text, alpha, typing: !!saying.typing });
      }
    }
    return said;
  }

  /** 老居民按家分组（一家一块碑）。 */
  function elderGroups(list) {
    const byHome = new Map();
    for (const e of list ?? []) {
      if (!e?.home || !e.name) continue;
      if (!byHome.has(e.home)) byHome.set(e.home, []);
      byHome.get(e.home).push(e.name);
    }
    return [...byHome].map(([home, names]) => ({ home, names }));
  }

  const hasSite = (s) => s === 'agora' || s.startsWith('agora:') || s === 'harbor' || s === 'gate' || !!layout?.plots.has(s);
  let neighbourSites = null;   // 城里各座建筑的站点（居民也去邻居家门口坐坐）；换城、城变了时清掉
  const viewTmp = new THREE.Vector3();
  /** 这一点在不在画面里（用上一帧的镜头——差一帧没关系，画面外的人本来就不摆不画）。 */
  const inView = (x, z) => {
    viewTmp.set(x, 1, z).project(camera);
    return Math.abs(viewTmp.x) < 1.15 && Math.abs(viewTmp.y) < 1.15;
  };
  /** 卡片收起后从站定的地方回到日程上的位置：这段偏移按 600 毫秒的时间常数消掉（不一帧跳回去）。 */
  const RELEASE_TAU_MS = 600;

  /**
   * 分档（M-B）：居民与十一位居民一起排——离你最近的 4 位全套（布料、弹簧骨、手指脚趾），画面里其余的布料停着、
   * 手指脚趾藏起来，画面外的不摆（居民连画都不画）。雅典娜和你不分档，永远全套。
   * 量出来的（`Docs/GameMode_Progress_CN.md` M-B 一节）：布料一位一帧约 2.3 毫秒，城里原来就有 13 位在推进布料，
   * 再加 32 位居民，无头 Chromium 从 27.6 掉到 12.6 帧——帧时间几乎全在布料上。
   * citizenPositions：[[id, {x, z}]]（这一帧日程给的位置）；十一位居民用他们上一帧的位置。
   */
  function tierPeople(citizenPositions) {
    const list = citizenPositions.map(([id, p]) => ({ id, distance: Math.hypot(p.x - player.pos.x, p.z - player.pos.z), inView: inView(p.x, p.z) }));
    for (const r of world.residents.values()) {
      const p = r.figure.root.position;
      list.push({ id: `resident:${r.kind}`, distance: Math.hypot(p.x - player.pos.x, p.z - player.pos.z), inView: inView(p.x, p.z) });
    }
    citizenTiers = assignTiers(list, citizenTiers);
  }

  // —— 讲台前的辩论（M-E）：舞台站位、陪审团、C# 推来的这一场 ——
  /** 这座城的舞台（讲台在广场上，每座城都有）：第一次用到时算一次，换城清掉。 */
  function stage() {
    if (stageInfo || !world?.agora?.userData.bema || !layout) return stageInfo;
    const b = world.agora.userData.bema;
    const o = world.agora.position;
    stageInfo = debateStage({ bema: { x: o.x + b.x, z: o.z + b.z }, center: { x: o.x, z: o.z }, walkable: layout.walkable });
    return stageInfo;
  }

  function audiencePos(aud, wall) {
    const f = ease(Math.min(1, Math.max(0, (wall - aud.start) / Math.max(1, aud.ms))));
    return { x: aud.from.x + (aud.to.x - aud.from.x) * f, z: aud.from.z + (aud.to.z - aud.from.z) * f };
  }

  /** 判票了：看得见、离舞台不远的居民走到陪审团的站位上（debate.assignAudience）。 */
  function gatherAudience(wall) {
    const st = stage();
    if (!st || !world) return;
    const candidates = [];
    for (const c of world.citizens.values()) {
      if (!c.figure?.root.visible || convo?.id === c.id || (panel?.type === 'citizen' && panel.id === c.id)) continue;
      const p = c.figure.root.position;
      if (Math.hypot(p.x - st.stage.x, p.z - st.stage.z) > DEBATE.gatherRadius) continue;
      candidates.push({ id: c.id, x: p.x, z: p.z });
    }
    for (const [id, a] of assignAudience(candidates, st.audience, layout.walkable, { stage: st.stage })) {
      audienceOf.set(id, { ...a, start: wall, dist: Math.hypot(a.to.x - a.from.x, a.to.z - a.from.z), supporter: false });
    }
    status.audience = audienceOf.size;
  }

  /** 判完了：按票数的比例，前面几位为你挥手。 */
  function markSupporters(votesForYou, jury) {
    const list = [...audienceOf.values()].sort((a, b) => a.index - b.index);
    const k = supportersCount(list.length, votesForYou, jury);
    list.forEach((a, i) => { a.supporter = i < k; });
    status.supporters = k;
  }

  /** 散场：陪审团从站位上慢慢回到各自的日程（和收起卡片一样的回位），雅典娜不再被留住。 */
  function teardownDebate(wall) {
    for (const [id, aud] of audienceOf) {
      const c = world?.citizens.get(id);
      if (!c?.at) continue;
      const p = audiencePos(aud, wall);
      c.release = { dx: p.x - (c.at.x + c.nudge.x), dz: p.z - (c.at.z + c.nudge.z), at: wall };
    }
    audienceOf.clear();
    status.audience = 0;
    status.supporters = 0;
    debateSeen = { id: null, athenaTurns: 0, lentRound: 0, gathered: false, doneAt: 0 };
  }

  /** 你在页面上说不辩了（Esc、"结束"、走远了、判完很久了）：告诉 C#，自己先散场（C# 随后推 idle）。 */
  function endDebateFromPage() {
    if (!debate && !motionPending) return;
    if (debate) send('debate-end');
    if (lentToDebate()) onLendEnd?.('debate');
    motionPending = false;
    debate = null;
    status.debate = null;
    teardownDebate(performance.now());
  }

  /** C# 推来的讲台现状。 */
  function setDebate(d) {
    if (d?.record) debateRecord = { wins: d.record.wins ?? 0, losses: d.record.losses ?? 0, ties: d.record.ties ?? 0 };
    if (d?.notice) debateNotice = { text: String(d.notice), until: performance.now() + 6000 };
    const next = d && d.state && d.state !== 'idle' ? d : null;
    const prev = debate;
    debate = next;
    status.debate = next ? { state: next.state, round: next.round, id: next.id, votesForYou: next.verdict?.votesForYou ?? null } : null;
    const wall = performance.now();
    if (!next) {
      if (prev) teardownDebate(wall);
      // 没开成（这一个钟头辩得够多了）：借出去的栏还回来
      if (motionPending) { motionPending = false; onLendEnd?.('debate'); }
      return;
    }
    if (next.id !== debateSeen.id) {
      if (prev) teardownDebate(wall);
      debateSeen = { id: next.id, athenaTurns: 0, lentRound: 0, gathered: false, doneAt: 0 };
      motionPending = false;
      // 她走到舞台的一侧（远了化作一道光过来），站定面对你
      const st = stage();
      if (st && athenaSim) athenaSim.goTo(`pt:${st.athena.x.toFixed(2)},${st.athena.z.toFixed(2)}`, clock);
    }
    const herTurns = (next.transcript ?? []).filter((t) => t.side === 'athena');
    if (herTurns.length > debateSeen.athenaTurns) {
      debateSeen.athenaTurns = herTurns.length;
      say(bubbleExcerpt(herTurns[herTurns.length - 1].text), clock, 3000);
    }
    if (next.state === 'awaiting' && debateSeen.lentRound !== next.round) {
      debateSeen.lentRound = next.round;
      onDebate?.({ stage: 'argue', round: next.round, rounds: next.rounds ?? 3, motion: next.motion ?? '', id: next.id });
    }
    if (next.state === 'judging' || next.state === 'done') onLendEnd?.('debate');
    if ((next.state === 'judging' || next.state === 'done') && !debateSeen.gathered) {
      debateSeen.gathered = true;
      gatherAudience(wall);
    }
    if (next.state === 'done' && !debateSeen.doneAt) {
      debateSeen.doneAt = wall;
      markSupporters(next.verdict?.votesForYou ?? 5, next.jury ?? 10);
    }
  }

  /** 顶上的辩论牌：议题、第几轮（或在商议 / 裁决）、最近一来一回、战绩。 */
  function debateBoardView() {
    const now = performance.now();
    if (!debate) {
      if (debateNotice && now < debateNotice.until) return { title: ui.bemaTitle, lines: [debateNotice.text], buttons: [] };
      return null;
    }
    const d = debate;
    const rounds = d.rounds ?? 3;
    const state = d.state === 'judging' ? ui.debateJudging
      : d.state === 'athena' ? ui.debateAthenaThinking
        : d.state === 'done' ? ''
          : fill(ui.debateRound, { round: d.round, rounds });
    const transcript = d.transcript ?? [];
    const lastYou = [...transcript].reverse().find((t) => t.side === 'you');
    const lastHer = [...transcript].reverse().find((t) => t.side === 'athena');
    const lines = [];
    if (lastYou) lines.push({ who: ui.debateYou, text: lastYou.text });
    if (lastHer && transcript.indexOf(lastHer) > transcript.indexOf(lastYou)) lines.push({ who: ui.debateAthena, text: lastHer.text });
    let verdict = null;
    if (d.state === 'done' && d.verdict) {
      const jury = d.jury ?? 10;
      const v = d.verdict.votesForYou ?? jury / 2;
      verdict = {
        headline: d.verdict.winner === 'you' ? ui.debateYouWin : d.verdict.winner === 'athena' ? ui.debateAthenaWins : ui.debateTie,
        votes: fill(ui.debateVotes, { you: v, athena: jury - v }),
        reason: d.verdict.reason ?? '',
        winner: d.verdict.winner,
      };
    }
    return {
      title: `${ui.debateName}：${d.motion ?? ''}`,
      state, lines, verdict,
      record: fill(ui.bemaRecord, debateRecord),
      buttons: [{ id: 'debate-close', label: ui.debateClose }],
    };
  }

  /**
   * 居民即会话（M-B）：日程（citizens.citizenAt）→ 避让（separate）→ 分档（assignTiers：离你最近的 4 位全套、画面里其余的
   * 布料停着、画面外的不摆不画）→ 闲聊与打招呼 → 摆姿势。人物一帧最多建两位（world.buildNextCitizen）：几十位一起建会卡住一帧。
   * 返回这一帧头顶要画的话（[{ id, text, alpha }]），frame 在镜头对齐之后投影。
   */
  function animateCitizens(wall, dt) {
    if (!world) return [];
    if (world.citizens.size === 0) { tierPeople([]); status.citizenTiers = null; status.chat = null; return []; }
    for (let k = 0; k < 2 && world.buildNextCitizen(); k++);
    const live = [];
    for (const c of world.citizens.values()) {
      if (!c.figure) continue;
      if (!c.plan) {
        neighbourSites ??= [...layout.plots.keys()].filter((k) => k.startsWith('b:'));
        c.plan = planCitizen({ id: c.id, seed: c.data.seed, home: c.data.home }, { sitePoint: layout.sitePoint, route: layout.route, walkable: layout.walkable, hasSite, neighbours: neighbourSites });
      }
      // 被叫住（卡片、问话）或当陪审团的这段时间不算进他的日程：松开后从站定的地方接着走，而不是滑到墙钟上本该到的地方
      c.schedule ??= newScheduleClock();
      if ((panel?.type === 'citizen' && panel.id === c.id) || convo?.id === c.id || (heard?.id === c.id && wall < heard.until) || audienceOf.has(c.id)
        || (chatTalk && (chatTalk.a === c.id || chatTalk.b === c.id))) holdSchedule(c.schedule, wall);
      else resumeSchedule(c.schedule, wall);
      c.at = citizenAt(c.plan, scheduleClock(c.schedule, wall));
      live.push(c);
    }
    // 避让：居民之间互相让；雅典娜与十一位居民是站在那儿的人（不被推），你是推不动的石头——挡在你面前的人侧身让开
    const people = live.map((c) => ({ id: c.id, x: c.at.x, z: c.at.z, nudge: c.nudge, movable: !((panel?.type === 'citizen' && panel.id === c.id) || convo?.id === c.id || audienceOf.has(c.id)) }));
    for (const r of world.residents.values()) people.push({ id: `resident:${r.kind}`, x: r.figure.root.position.x, z: r.figure.root.position.z, movable: false });
    if (world.athena.visible) people.push({ id: 'athena', x: world.athena.position.x, z: world.athena.position.z, movable: false });
    const nudges = separate(people, player.pos, layout.walkable);
    for (const c of live) c.nudge = nudges.get(c.id) ?? c.nudge;
    const posOf = (c) => {
      // 陪审团（M-E）：走到讲台前的站位、站在那儿，日程先放一边
      const aud = audienceOf.get(c.id);
      if (aud) return audiencePos(aud, wall);
      let x = c.at.x + c.nudge.x, z = c.at.z + c.nudge.z;
      if (c.hold) { x = c.hold.x; z = c.hold.z; }
      else if (c.release) {
        const k = Math.exp(-(wall - c.release.at) / RELEASE_TAU_MS);
        if (k < 0.01) c.release = null;
        else { x += c.release.dx * k; z += c.release.dz * k; }
      }
      return { x, z };
    };
    tierPeople(live.map((c) => [c.id, posOf(c)]));

    // 闲聊：半秒挑一次，全城同时只有一组；挑中了就请 C# 让他们说（ambient-chat）——先开口的那位头顶马上是一串点。
    // 聊着的时候两人站定（日程钟停着，见上面）；C# 说结束了（ambient-end）或者聊得太久（卡住了）就散
    if (chatTalk && wall - chatTalk.start > TOWN.chatMaxMs) endAmbient({ a: chatTalk.a, b: chatTalk.b, status: 'failed' });
    if (!chatTalk && wall >= nextChatPickAt) {
      nextChatPickAt = wall + 500;
      const candidates = live
        .filter((c) => c.at.action === 'idle' && !c.hold && convo?.id !== c.id && heard?.id !== c.id && citizenTiers.get(c.id) !== 'C' && greeting?.id !== c.id && !audienceOf.has(c.id))
        .map((c) => { const p = posOf(c); return { id: c.id, x: p.x, z: p.z, idleLeft: c.at.idleLeft, inView: true }; });
      const pick = pickChat(candidates, player.pos, chatCooldown, wall);
      if (pick) startAmbient(pick, wall);
    }
    // 打招呼：你走到一位闲着的居民跟前（2.5 米内），两分钟里没跟你打过招呼——他转身、挥手、说一句
    if (greeting && wall - greeting.start > TOWN.chatLineMs) greeting = null;
    if (!greeting) {
      let best = null;
      for (const c of live) {
        if (c.at.action !== 'idle' || c.at.idleLeft < TOWN.chatLineMs + 300 || citizenTiers.get(c.id) === 'C' || audienceOf.has(c.id)) continue;
        if (chatTalk && (chatTalk.a === c.id || chatTalk.b === c.id)) continue;
        const p = posOf(c);
        const d = Math.hypot(p.x - player.pos.x, p.z - player.pos.z);
        const last = greetedAt.get(c.id);
        if (d > TOWN.greetRadius || (last != null && wall - last < TOWN.greetCooldownMs)) continue;
        if (!best || d < best.d) best = { c, d };
      }
      if (best) {
        greeting = { id: best.c.id, start: wall, line: greetLine(best.c.data.seed, citizenWords.greet, wall) };
        greetedAt.set(best.c.id, wall);
      }
    }

    const bubbles = [];
    const counts = { A: 0, B: 0, C: 0 };
    for (const c of live) {
      const tier = citizenTiers.get(c.id) ?? 'C';
      c.tier = tier;
      counts[tier]++;
      const root = c.figure.root;
      // 卡片开着、或者你正在和他聊（对话栏借给了他）：他都站定、面朝你
      const asked = convo?.id === c.id || (heard?.id === c.id && wall < heard.until);
      const panelOpen = (panel?.type === 'citizen' && panel.id === c.id) || asked;
      // 卡片开着：他站定、面朝你；收起时记下站定的地方，再慢慢回到日程上的位置
      if (panelOpen && !c.hold) c.hold = posOf(c);
      if (!panelOpen && c.hold) {
        c.release = { dx: c.hold.x - (c.at.x + c.nudge.x), dz: c.hold.z - (c.at.z + c.nudge.z), at: wall };
        c.hold = null;
      }
      if (tier === 'C' && !panelOpen) { root.visible = false; continue; }
      root.visible = true;
      // 手指脚趾只给最近的几位（A 档）：远处的看不见，画它们却要四遍（阴影、主画面、AO、遮罩）
      c.figure.setDetail(tier === 'A' || panelOpen);
      const { x, z } = posOf(c);
      let action = c.at.action;
      let t = action === 'walk' ? c.at.walkT : wall + (c.data.seed % 9000);
      let yaw = c.at.yaw;
      const style = { speed: TOWN.speed, stride: 1.25, seed: c.data.seed % 9973, cloth: tier === 'A' || panelOpen };
      const greetingNow = greeting?.id === c.id;
      const inChat = !!chatTalk && (chatTalk.a === c.id || chatTalk.b === c.id);
      const aud = audienceOf.get(c.id);
      if (aud && !panelOpen) {
        // 陪审团：走过来（比平时快一点），到了面朝舞台；判完之后为你投票的人隔一会儿挥一次手
        const f = Math.min(1, Math.max(0, (wall - aud.start) / Math.max(1, aud.ms)));
        if (f < 1) {
          action = 'walk';
          yaw = Math.atan2(aud.to.x - aud.from.x, aud.to.z - aud.from.z);
          t = ((aud.dist * f) / DEBATE.walkSpeed) * 1000;
          style.speed = DEBATE.walkSpeed;
        } else {
          action = 'idle';
          yaw = aud.to.yaw;
          if (aud.supporter && debate?.state === 'done' && debateSeen.doneAt) {
            const phase = (wall - debateSeen.doneAt + aud.index * 330) % DEBATE.waveEveryMs;
            if (phase < ONE_SHOT_MS.wave) { action = 'wave'; t = phase; }
          }
          if (stageInfo) style.lookAt = playerHead.set(stageInfo.stage.x, 1.5, stageInfo.stage.z);
        }
      } else if (panelOpen || greetingNow) {
        yaw = Math.atan2(player.pos.x - x, player.pos.z - z);
        if (panelOpen && c.cardAt == null) c.cardAt = wall;
        const since = wall - (greetingNow ? greeting.start : c.cardAt);
        if (since < ONE_SHOT_MS.wave) { action = 'wave'; t = since; } else { action = 'talk'; t = since; style.speaker = !asked; }
        style.lookAt = playerHead.set(player.pos.x, 1.5, player.pos.z);
        // 聊天时他在想、在说：说话的手势；还在想（流式还没出字）时只是听着
        const saying = sayings.get(c.id);
        if (asked && saying && !saying.typing) style.speaker = saying.doneAt == null || wall - saying.doneAt < 2500;
        if (greetingNow && !saying) bubbles.push({ id: c.id, text: greeting.line, alpha: Math.max(0, Math.min(1, since / 150, (TOWN.chatLineMs - since) / 300)) });
      } else if (inChat) {
        // 面对面站着；正在说（流式出字、或刚说完两秒半内）的那位打手势，另一位听着
        const other = world.citizens.get(chatTalk.a === c.id ? chatTalk.b : chatTalk.a);
        if (other?.at) {
          const o = posOf(other);
          yaw = Math.atan2(o.x - x, o.z - z);
          style.lookAt = playerHead.set(o.x, 1.5, o.z);
        }
        action = 'talk';
        t = wall - chatTalk.start;
        const saying = sayings.get(c.id);
        style.speaker = !!saying && !saying.typing && (saying.doneAt == null || wall - saying.doneAt < 2500);
      } else {
        c.cardAt = null;
        if (action === 'idle' && Math.hypot(player.pos.x - x, player.pos.z - z) < 4) style.lookAt = playerHead.set(player.pos.x, 1.5, player.pos.z);
      }
      // 他头顶的话（你和他聊天时他说的）：在想时是一串循环的点，流式的字边到边换，说完留一阵再淡出；你还在和他聊就一直留着
      const saying = sayings.get(c.id);
      if (saying) {
        const alpha = speechAlpha(saying, wall, convo?.id === c.id);
        if (alpha <= 0) sayings.delete(c.id);
        else {
          for (let k = bubbles.length - 1; k >= 0; k--) if (bubbles[k].id === c.id) bubbles.splice(k, 1);
          bubbles.push({ id: c.id, text: saying.typing ? thinkingDots(wall) : saying.text, alpha, typing: !!saying.typing });
        }
      }
      c.yawNow = c.yawNow == null ? yaw : turnToward(c.yawNow, yaw, 1 - Math.exp(-dt / 180));
      const y = world.groundAt(x, z);
      root.position.set(x, y + 0.012, z);
      root.rotation.y = c.yawNow;
      c.figure.pose(action, t, style);
      c.label.anchor.set(x, y + (root.userData.height ?? 1.75) + 0.55, z);
    }
    status.citizenTiers = counts;
    status.chat = chatTalk ? { a: chatTalk.a, b: chatTalk.b } : null;
    status.greeting = greeting?.id ?? null;
    return bubbles;
  }

  /**
   * 成果在场景里的样子：待处理的带颜色和白环、放在你脚下（或广场南边一排）；收下的颜色褪掉，摆在场馆的底座上
   * （没摆的收着，城里看不见）；退回的变灰、沉下去；遗失的只剩底座和名牌——摆着的在原位，没摆的在广场南边。
   */
  function placeItems() {
    const wall = performance.now();
    let row = 0;
    if (world) for (const v of world.venues.values()) for (const s of v.slots) s.item = null;
    for (const record of [...items.values()].sort((a, b) => (a.item.createdAt ?? '').localeCompare(b.item.createdAt ?? ''))) {
      const { item } = record;
      const placement = item.placement && world?.venues.get(item.placement.venue);
      const slot = placement ? placement.slots[item.placement.slot] : null;
      if (slot) {
        // 陈列：在底座上（遗失的只剩底座，名牌照写）
        slot.item = item.id;
        if (record.object) record.object.visible = false;
        if (!record.display) {
          record.display = assets.object(`prop.display-${item.kind ?? 'scroll'}`);
          record.display.position.copy(slot.pedestal.position);
          placement.group.add(record.display);
        }
        record.display.position.copy(slot.pedestal.position);
        record.display.visible = !item.lost;
        continue;
      }
      if (record.display) { record.display.parent?.remove(record.display); record.display = null; }
      const visibleState = item.state === 'pending' || record.collectedAt != null;
      if (!visibleState && !item.lost) {
        if (record.object) record.object.visible = false;
        continue;
      }
      if (!record.object) {
        record.object = assets.object(`prop.item-${item.kind ?? 'scroll'}`);
        world.root.add(record.object);
        if (!record.spot) {
          // 不是刚刚交付的（重启后、离开期间交付的）：在广场南边排成一排等你
          record.spot = { x: -3 + (row % 6) * 1.6, z: 5.6 + Math.floor(row / 6) * 1.6 };
        }
      }
      row++;
      const o = record.object;
      o.position.set(record.spot.x, 0.01, record.spot.z);
      o.rotation.y = 0.5;
      o.visible = record.revealAt == null || wall >= record.revealAt;
      const pending = item.state === 'pending' && !item.lost && record.collectedAt == null;
      o.traverse((part) => { if (part.isMesh && part.userData.accent !== undefined) part.userData.accent = pending; });
      // 遗失：只剩底座（上面那件拿掉）
      if (o.userData.body) o.userData.body.visible = !item.lost;
      const ring = o.userData.ring;
      if (ring) {
        ring.visible = pending;
        ring.scale.setScalar(1 + 0.22 * Math.sin(wall / 260));
      }
      if (record.collectedAt != null) {
        const k = Math.min(1, (wall - record.collectedAt) / 1200);
        o.position.y = 0.01 - k * 0.3;
        o.visible = k < 1;
        if (k >= 1) record.collectedAt = null;
      }
    }
    // 城里此刻看得见的成果（测试用）
    status.shownItems = [...items.values()].filter((r) => r.object?.visible).map((r) => ({ id: r.item.id, x: +r.object.position.x.toFixed(2), z: +r.object.position.z.toFixed(2) }));
    if (world) world.showSlotMarkers(placing != null, () => true);
    if (placing && world) {
      const pulse = 1 + 0.18 * Math.sin(wall / 230);
      for (const v of world.venues.values()) for (const s of v.slots) if (s.marker.visible) s.marker.scale.setScalar(pulse);
    }
  }

  // —— 她头顶的话：一句接一句，每句至少停 minMs。只有本地的几句（奠基、揭幕之后委托板上的第一条建议），不跟着任何回合 ——
  function say(text, t, minMs = 2600) {
    if (!text) return;
    speech.push({ text, t, minMs });
    speech.sort((a, b) => a.t - b.t);
    status.said = text;   // 测试读：她最近要说的那一句
  }

  function currentLine(now) {
    // 奠基揭幕之后，她按委托板给出第一条建议（5.4）：建议来得比揭幕晚，就等它来了再说
    if (pendingBoardLine && suggestions.length > 0 && now >= pendingBoardLine.t) {
      say(suggestions[0].line, now, 3400);
      pendingBoardLine = null;
    }
    if (speech.length > 8) speech = speech.filter((c) => now - c.t < 60_000);
    const all = speech;
    let best = null;
    let free = -Infinity;
    for (const c of all) {
      const shown = Math.max(c.t, free);
      if (shown > now) break;
      best = { text: c.text, t: shown };
      free = shown + (c.minMs ?? 1200);
    }
    if (!best || now - best.t >= BUBBLE_MS) return null;
    const age = now - best.t;
    return { text: best.text, alpha: Math.min(1, age / 150, (BUBBLE_MS - age) / 400) };
  }

  // —— 玩家走路：按住的方向键相对镜头换算成地面上的方向，撞上房子、走到海边就沿着滑（controls.mjs、layout.walkable） ——
  /** 现在能不能走：过场、雾、海图、阅读器挡着的时候不走（方向键交给它们）。 */
  const movementAllowed = () => !!world && !voyage && !fogInfo && !chartOpen && !modalOpen();

  /** 脚下地面高度的变化：60 毫秒的时间常数（迈上锻炉的地坪是一步，不是一帧瞬移）。 */
  const stepToward = (y, target, dt) => (Math.abs(target - y) < 1e-4 ? target : y + (target - y) * (1 - Math.exp(-Math.max(0, dt) / 60)));
  let athenaY = null, athenaYaw = null;

  function updatePlayer(wall, dt) {
    const dir = movementAllowed() ? moveDirection(pressed, cameraAzimuth()) : null;
    const speed = PLAYER_SPEED * (running ? RUN_FACTOR : 1);
    let action = 'idle';
    const style = { speed, stride: running ? 2.2 : 2.0, seed: 1 };
    if (dir) {
      const step = (speed * Math.min(dt, 100)) / 1000;
      const next = slide(player.pos, { x: dir.x * step, z: dir.z * step }, layout.walkable);
      const moved = Math.hypot(next.x - player.pos.x, next.z - player.pos.z);
      player.yaw = turnToward(player.yaw, Math.atan2(dir.x, dir.z), 1 - Math.exp(-dt / 70));
      player.pos = next;
      player.stoppedAt = wall;
      // 顶着墙按住方向键：转过身去，但不原地踏步
      player.moving = moved > step * 0.2;
      if (player.moving) {
        player.dirty = true;
        // 步子的相位按走过的距离推进（不按时间）：贴着墙慢下来脚也慢下来，跑和走的周期都由步幅 / 速度算——脚不打滑（M3）
        player.walkClock += (moved / speed) * 1000;
        action = running ? 'run' : 'walk';
        anim.playerOneShot = null;
      }
    } else {
      player.moving = false;
      // 停下来一会儿才记下位置（存档里的"玩家进度"）；走的途中不发
      if (player.dirty && wall - player.stoppedAt >= SAVE_PLAYER_MS) {
        player.dirty = false;
        send('save-player', { x: +player.pos.x.toFixed(2), z: +player.pos.z.toFixed(2), zoom: +camera.zoom.toFixed(2) });
      }
    }
    // M3：站着的时候——刚接过成果就伸手接；对话栏开着就和她说话（托付之后比划两秒多，其余时间听）；离她近就看着她
    const athena = world.athena;
    const dp = athena.visible ? Math.hypot(athena.position.x - player.pos.x, athena.position.z - player.pos.z) : Infinity;
    const talking = talkingToAthena();
    let t = wall;
    if (action === 'idle') {
      if (anim.playerOneShot && !oneShotDone(anim.playerOneShot.action, wall - anim.playerOneShot.start)) {
        action = anim.playerOneShot.action;
        t = wall - anim.playerOneShot.start;
      } else {
        anim.playerOneShot = null;
        if (talking) { action = 'talk'; style.speaker = wall - anim.saidAt < TALK_GESTURE_MS; }
      }
      if (talking && dp < 9) player.yaw = turnToward(player.yaw, Math.atan2(athena.position.x - player.pos.x, athena.position.z - player.pos.z), 1 - Math.exp(-dt / 220));
    } else {
      t = player.walkClock;
    }
    if (dp < 7 && action !== 'run') style.lookAt = athenaChest.set(athena.position.x, 1.45, athena.position.z);
    // 脚下的地面（锻炉的地坪高 0.2 米）：迈上去用 60 毫秒，不是一帧跳上去
    player.y = player.y == null ? world.groundAt(player.pos.x, player.pos.z) : stepToward(player.y, world.groundAt(player.pos.x, player.pos.z), dt);
    world.player.position.set(player.pos.x, player.y + 0.012, player.pos.z);
    world.player.rotation.y = player.yaw;
    world.playerFigure.pose(action, t, style);
    status.player = { x: +player.pos.x.toFixed(2), z: +player.pos.z.toFixed(2), moving: player.moving };
    status.playerAction = action;
  }

  /** 镜头的仰角（度）。 */
  function cameraElevation() {
    offsetTmp.copy(camera.position).sub(controls.target);
    spherical.setFromVector3(offsetTmp);
    return 90 - THREE.MathUtils.radToDeg(spherical.phi);
  }

  /** 远近与仰角朝目标走一步（方位角不动，拖动转的视角保留）；为空的那一项不动。返回还差多少。 */
  function approachView(view, k) {
    let left = 0;
    if (view.zoom != null) {
      camera.zoom += (view.zoom - camera.zoom) * k;
      camera.updateProjectionMatrix();
      left += Math.abs(view.zoom - camera.zoom);
    }
    if (view.elevation != null) {
      offsetTmp.copy(camera.position).sub(controls.target);
      spherical.setFromVector3(offsetTmp);
      const el = 90 - THREE.MathUtils.radToDeg(spherical.phi);
      const next = THREE.MathUtils.clamp(el + (view.elevation - el) * k, VIEW.minElevation, VIEW.maxElevation);
      spherical.phi = THREE.MathUtils.degToRad(90 - next);
      camera.position.copy(controls.target).add(offsetTmp.setFromSpherical(spherical));
      left += Math.abs(view.elevation - next) / 10;
    }
    return left;
  }

  /** 此刻该不该是近景、看着谁：对话栏开着看你和雅典娜；居民的卡片开着看你和他。 */
  function closeUpWanted() {
    if (!world || voyage || reveal || fogInfo) return null;
    // 和一位居民（或职业居民）聊天：对话栏借给了他，镜头看你和他，不看她
    if (convo && world.citizens.get(convo.id)?.figure) return { kind: 'citizen', id: convo.id };
    if (convo?.id.startsWith('resident:') && world.residents.has(convo.id.slice(9))) return { kind: 'resident', id: convo.id.slice(9) };
    // 讲台前辩论时：对话栏借给了辩论，镜头看你和她（和平时开口一样的近景）；判票与判完看整个舞台和围过来的陪审团
    if (lentToDebate() && talkOpen()) return { kind: 'debate', id: null };
    if (debate && (debate.state === 'judging' || debate.state === 'done') && stageInfo) return { kind: 'jury', id: null };
    if (talkingToAthena()) return { kind: 'talk', id: null };
    if (panel?.type === 'resident' && world.residents.has(panel.id)) return { kind: 'resident', id: panel.id };
    if (panel?.type === 'citizen' && world.citizens.get(panel.id)?.figure) return { kind: 'citizen', id: panel.id };
    return null;
  }

  const closeUpFocus = new THREE.Vector3();
  function closeUpCamera(dt, snap) {
    const wanted = closeUpWanted();
    if (wanted && (!closeUp || closeUp.kind !== wanted.kind || closeUp.id !== wanted.id)) {
      // 退回的半路上又进近景（托付之后马上又开口）：记住的仍是退回的目标——你原来的远近与俯角。
      // 记下半路的镜头，收起时就退到半路、停在那儿回不去了（冒烟 zoomRestoredAfterClose 时好时坏就是它，2026-10-12）
      // 退回途中被你接手的那一项，记下的是此刻的值（你滚到 / 转到的地方）
      if (!closeUp) closeUp = { ...wanted, saved: { zoom: restoring?.zoom ?? camera.zoom, elevation: restoring?.elevation ?? cameraElevation() } };
      else closeUp = { ...wanted, saved: closeUp.saved };
      restoring = null;
      status.closeUp = wanted.kind;
    } else if (!wanted && closeUp) {
      restoring = { ...closeUp.saved };
      closeUp = null;
      status.closeUp = null;
    }
    const k = snap ? 1 : 1 - Math.exp(-dt / CLOSE_UP.tauMs);
    if (closeUp) {
      if (closeUp.kind === 'jury') {
        // 舞台中心（你走开超过 25 米这一场就散了，所以不必像近景那样防"太远"）
        closeUpFocus.set(stageInfo.stage.x, 1.1, stageInfo.stage.z);
      } else {
        const other = closeUp.kind === 'talk' || closeUp.kind === 'debate' ? world.athena.position
          : closeUp.kind === 'citizen' ? world.citizens.get(closeUp.id).figure.root.position
            : world.residents.get(closeUp.id).figure.root.position;
        const far = Math.hypot(other.x - player.pos.x, other.z - player.pos.z) > 12;
        closeUpFocus.set(far ? player.pos.x : (player.pos.x + other.x) / 2, 1.1, far ? player.pos.z : (player.pos.z + other.z) / 2);
      }
      const delta = closeUpFocus.sub(controls.target).multiplyScalar(k);
      controls.target.add(delta);
      camera.position.add(delta);
      approachView(closeUp.kind === 'jury' ? JURY_VIEW : CLOSE_UP, k);
      return true;
    }
    if (restoring) {
      if (restoring.zoom == null && restoring.elevation == null) restoring = null;
      else if (approachView(restoring, k) < 0.02) { approachView(restoring, 1); restoring = null; }
    }
    return false;
  }

  function followCamera(dt, pose, snap = false) {
    if (glide && performance.now() > glide.until) glide = null;
    if (closeUpCamera(dt, snap)) return;
    const want = glide
      ? glide.to.clone()
      : follow === 'player'
        ? new THREE.Vector3(player.pos.x, 1, player.pos.z)
        : follow === 'athena'
          ? pose.teleport ? new THREE.Vector3(pose.teleport.x, 1, pose.teleport.z) : new THREE.Vector3(pose.x, 1, pose.z)
          : null;
    if (!want) {
      if (player.moving) keepPlayerInView(dt, snap);
      return;
    }
    const k = snap ? 1 : 1 - Math.exp(-dt / 450);
    const delta = want.sub(controls.target).multiplyScalar(k);
    controls.target.add(delta);
    camera.position.add(delta);
  }

  /**
   * 自由视角：镜头不跟任何人，只在你走到画面中心那个方框之外时把画面挪一点（沿着镜头自己的右 / 上方向，和平移一样），
   * 让你留在画面里。先按目标点重新对准镜头再投影——这一帧 OrbitControls 还没 lookAt，用旧朝向投影会算错。
   */
  const right = new THREE.Vector3(), up = new THREE.Vector3();
  function keepPlayerInView(dt, snap) {
    camera.lookAt(controls.target);
    camera.updateMatrixWorld();
    const ndc = tmp.set(player.pos.x, 0.9, player.pos.z).project(camera);
    const shift = deadZoneShift(ndc);
    if (!shift) return;
    const k = snap ? 1 : 1 - Math.exp(-dt / 140);
    const halfW = (camera.right - camera.left) / 2 / camera.zoom, halfH = (camera.top - camera.bottom) / 2 / camera.zoom;
    right.set(1, 0, 0).applyQuaternion(camera.quaternion).multiplyScalar(shift.x * halfW * k);
    up.set(0, 1, 0).applyQuaternion(camera.quaternion).multiplyScalar(shift.y * halfH * k);
    const move = right.add(up);
    controls.target.add(move);
    camera.position.add(move);
  }

  /** 请镜头去看一处（摆放时看场馆）：几秒后交还给原来的跟随方式。 */
  function lookAt(x, z, ms = 4000) {
    if (follow !== 'free') setFollow('free');   // 先切（切换会清掉 glide），再请镜头过去
    glide = { to: new THREE.Vector3(x, 1, z), until: performance.now() + ms };
  }

  // —— 一帧：推进 → 摆好人物和镜头 → 对齐矩阵 → 算屏幕空间位置 → 渲染 → 叠加层 ——
  const tmp = new THREE.Vector3();
  let used = null;
  let regions = [];
  function frame(now, dt, wall, { snap = false, sync = true, script = null, hud = null } = {}) {
    if (!world) {
      overlay.draw({ time: wall, labels: [], bubble: null, edges: [], voyage: voyageFrame(wall), ui, chart: chartView(), topInset });
      syncUiBlocked();
      return null;
    }
    clock = Math.max(clock, now);
    setClothWind(windAt(wall));   // 海上吹来的风：全城的衣摆、面纱、斗篷都吃它（M3）
    stepNight(dt);
    updatePlayer(wall, dt);
    // 2026-10-13：远处开口不再把她叫过来——要和她说话，走到她身边（talk.openAthena）
    const state = athenaSim.update(now, { hold: athenaHeld() });
    const line = currentLine(now);
    const pose = athenaPose(state, now);
    applyAthena(pose, line, dt);
    // 居民先排（他们和十位职业居民一起分档，见 tierPeople），职业居民在 applyEffects 里按同一份分档摆
    const citizenSaying = animateCitizens(wall, dt);
    // 说出去太久没有回音（C# 那边一次调用 25 秒就放弃）：他想了想，没说出来——这一句放进他头顶，你接着说
    if (convo?.status === 'waiting' && wall - convo.sentAt > ASK_TIMEOUT_MS) {
      setTalk({ who: convo.id, text: ui.askTimeout, done: true, status: 'timeout' });
    }
    // 辩论（M-E）：你走开太远、或者判完很久了——散场
    if (debate) {
      const st = stage();
      const far = st && Math.hypot(player.pos.x - st.stage.x, player.pos.z - st.stage.z) > DEBATE.leaveDistance;
      const stale = debate.state === 'done' && debateSeen.doneAt && wall - debateSeen.doneAt > DEBATE.doneHoldMs;
      if (far || stale) endDebateFromPage();
    }
    const residentSaying = applyEffects(wall, dt);
    focus = pickFocus(pose);
    status.focus = focus ? { type: focus.type, id: focus.id ?? focus.venue ?? null, slot: focus.slot ?? null } : null;
    followCamera(dt, pose, snap);
    if (reveal) applyReveal(wall);
    else if (arrival) applyArrival(wall);
    if (script) script();          // 运动自检：在跟随之后、lookAt 之前按脚本挪动镜头
    controls.update();             // 内部调用 camera.lookAt——矩阵在这里变旧，下面必须重新对齐
    R.placeSun(controls.target);
    if (sync) R.syncMatrices();

    const glowWorld = pose.teleport ? pose.teleport.clone() : world.athena.userData.chest.clone().applyMatrix4(world.athena.matrixWorld);
    const headWorld = pose.teleport ? pose.teleport.clone().setY(1.95) : world.athena.userData.headTop.clone().applyMatrix4(world.athena.matrixWorld);
    const glowUv = R.projectUv(glowWorld);
    const feetUv = R.projectUv(tmp.set(glowWorld.x, 0, glowWorld.z));
    const topUv = R.projectUv(tmp.set(glowWorld.x, 1.9, glowWorld.z));
    const g = R.gradePass.uniforms;
    g.uGlowPos.value.set(glowUv.x, glowUv.y);
    g.uGlowRadius.value = Math.max(0.008, (Math.abs(topUv.y - feetUv.y) / 1) * (pose.teleport ? 0.55 : 0.75));
    g.uGlowStrength.value = pose.glow;
    g.uSeed.value = (now % 1000) / 1000;

    const bubbleCss = R.projectCss(headWorld);
    // 居民头顶的话（M-B）：和她的那一句一样按这一帧的矩阵投影（运动自检逐帧核对）
    // 你头顶的话（2026-10-13）：你对一位居民说的那一句，说完留一阵（他开口之后淡得快一点）
    // 他还在想（还没开口）时你那句一直留着；他开口之后按字数留一阵再淡出
    const heStarted = convo && sayings.get(convo.id) && !sayings.get(convo.id).typing;
    const mineAlpha = mine ? speechAlpha({ text: mine.text, doneAt: convo?.status === 'waiting' && !heStarted ? null : mine.at }, wall) : 0;
    if (mine && mineAlpha <= 0) mine = null;
    const sayingNow = [...citizenSaying, ...residentSaying, ...(mine ? [{ id: 'player', text: mine.text, alpha: mineAlpha }] : [])];
    const citizenBubbles = sayingNow.map((b) => {
      const root = b.id === 'player' ? world.player
        : b.id.startsWith('resident:') ? world.residents.get(b.id.slice(9)).figure.root
          : world.citizens.get(b.id).figure.root;
      const head = (root.userData.headTop?.clone() ?? new THREE.Vector3(0, 1.75, 0)).applyMatrix4(root.matrixWorld);
      return { ...b, world: head, ...R.projectCss(head) };
    });
    const flashNow = performance.now();
    // 名牌：建筑、公共建筑、市集摊位一直画；居民的只在还没结识、或者指针停在他身上时画（满城的名字太挤）；
    // 橄榄树的只在指针停在那棵树上时画；遗失的藏品在它的底座上挂一块名牌
    // 居民即会话（M-B）的名牌：只在离你 6 米内、或者你够得着他的时候画——三十多块名牌一起画，满屏都是字
    const citizenLabelShown = (l) => {
      const c = world.citizens.get(l.key.slice('citizen:'.length));
      if (!c?.figure?.root.visible) return false;
      if (focus?.type === 'citizen' && focus.id === c.id) return true;
      const p = c.figure.root.position;
      return Math.hypot(p.x - player.pos.x, p.z - player.pos.z) <= TOWN.labelRadius;
    };
    const anchored = world.labels.filter((l) => (l.kind !== 'resident' || l.unmet || (focus?.type === 'resident' && l.key === `resident:${focus.id}`))
      && (l.kind !== 'citizen' || citizenLabelShown(l)));
    if (focus?.type === 'tree') {
      const tree = world.groveTrees.find((t) => t.conversationId === focus.id);
      if (tree) anchored.push({ text: tree.title, kind: 'tree', key: `tree:${tree.conversationId}`, anchor: tree.top });
    }
    // 走到够得着的东西跟前：它脚下一块"E 和雅典娜说话"（头顶留给旁白气泡、名牌）。按当帧矩阵投影，和名牌一起进运动自检。
    // 对话栏开着（和雅典娜说话、或者借给了一位居民）时不画：按下的 E 是打进栏里的字
    if (focus && !uiBlocked && !talkOpen()) {
      const anchor = focus.type === 'athena' ? ATHENA_HOVER_ANCHOR.clone().applyMatrix4(world.athena.matrixWorld) : new THREE.Vector3(focus.x, 0.02, focus.z);
      anchored.push({ text: promptText(focus), kind: 'prompt', key: `prompt:${focus.type}:${focus.id ?? focus.venue ?? ''}:${focus.slot ?? ''}`, keyCap: ui.interactKey, anchor });
    }
    for (const record of items.values()) {
      const pl = record.item.placement && world.venues.get(record.item.placement.venue);
      if (!pl || !record.item.lost) continue;
      const s = pl.slots[record.item.placement.slot];
      if (s) anchored.push({ text: record.item.title ?? '', kind: 'lost', key: `lost:${record.item.id}`, anchor: s.world.clone().setY(1.25) });
    }
    const labels = anchored.map((l) => {
      const started = l.key ? flashes.get(l.key) : null;
      const flash = started != null ? Math.max(0, 1 - (flashNow - started) / 2400) : 0;
      return { ...l, ...R.projectCss(l.anchor), world: l.anchor.clone(), flash };
    });
    const edges = [];
    for (const record of items.values()) {
      if (!record.object || !record.object.visible || record.item.state !== 'pending' || record.item.lost) continue;
      const p = R.projectCss(tmp.copy(record.object.position));
      if (p.x < 0 || p.y < 0 || p.x > window.innerWidth || p.y > window.innerHeight) edges.push(p);
    }
    // 等审批不在城里显示（2026-10-13：她不再停在门槛前）——对话栏上的红封印就是"轮到你了"
    const offscreen = (p) => p.x < 0 || p.y < 0 || p.x > window.innerWidth || p.y > window.innerHeight;
    let athenaEdge = null;
    if (follow !== 'athena' && pose.visible && !reveal) {
      // 自由视角（缺省）下她常常在画面外：边缘一枚白色的标记指着她（光属于雅典娜，不用点缀色），点它就跟过去
      const p = R.projectCss(tmp.set(pose.x, 1, pose.z));
      if (offscreen(p)) athenaEdge = { x: p.x, y: p.y, busy: pose.action !== 'idle' || pose.glow > 0.25 };
    }
    used = { glowWorld, glowUv: { x: glowUv.x, y: glowUv.y }, headWorld, bubbleCss, labels, citizenBubbles };

    R.render();
    syncUiBlocked();
    regions = overlay.draw({
      time: wall,
      labels: labels.filter((l) => l.x > -60 && l.y > -20 && l.x < window.innerWidth + 60 && l.y < window.innerHeight + 20),
      bubble: line ? { text: line.text, x: bubbleCss.x, y: bubbleCss.y, alpha: line.alpha } : null,
      bubbles: citizenBubbles.filter((b) => b.alpha > 0 && b.x > -200 && b.y > -60 && b.x < window.innerWidth + 200 && b.y < window.innerHeight + 60),
      edges,
      athenaEdge: athenaEdge ? { ...athenaEdge, label: ui.athenaHere } : null,
      hud,
      notices: notices.length ? notices : null,
      herald,
      debate: herald ? null : debateBoardView(),
      fog: fogInfo,
      card: card ? cardView(card) : null,
      panel: panelView(),
      placing: placing ? placingView() : null,
      voyage: voyageFrame(wall),
      chart: chartView(),
      ui,
      topInset,
    });
    status.now = now;
    status.athena = state.step?.kind ?? null;
    status.athenaAt = state.position;
    status.frames = (status.frames ?? 0) + 1;
    status.athenaAction = pose.action;
    return state;
  }
  let uiBlocked = false;

  /** 画面中间被卡片、面板、海图、雾、过场或摆放提示占着的时候，对话栏让开（同一时间底部只有一块）。 */
  function syncUiBlocked() {
    const blocked = !world || !!(card || panel || chartOpen || placing || fogInfo || voyage);
    if (blocked === uiBlocked) return;
    uiBlocked = blocked;
    onUiBlocked?.(blocked);
  }

  // —— 航海过场：最短观感时长与上限（9.2）。新城完整就绪就靠岸；到了上限还没就绪，只要已经有一座（哪怕只有顶层建筑的）城，
  // 先靠岸显示已就绪的部分，其余陆续出现。 ——
  function voyageFrame(wall) {
    if (!voyage) return null;
    if (voyage.arriveAt == null) {
      const plan = transitionArrival(voyage.startedAt, voyage.readyAt, wall);
      if (plan.arriveAt != null && (!plan.partial || (world && voyage.cityArrived))) {
        voyage.arriveAt = plan.arriveAt;
        status.voyage = plan.partial ? 'arriving-partial' : 'arriving';
      }
    }
    const { startedAt, arriveAt, caption } = voyage;
    const fadeIn = Math.min(1, (wall - startedAt) / VOYAGE_FADE_MS);
    let alpha = fadeIn;
    // 出港：城还看得见的那 400 毫秒里，镜头往后拉一点（像船离岸、城往后退）——遮住之后镜头停在那儿，靠岸时再推回来
    if (fadeIn < 1 && voyage.departZoom != null && world) {
      camera.zoom = voyage.departZoom * (1 - 0.16 * ease(fadeIn));
      camera.updateProjectionMatrix();
    }
    if (arriveAt != null && wall >= arriveAt) {
      if (!voyage.settled && world && !reveal) {
        // 靠岸：新城从略远处推近到原来的远近（1.2 秒，先快后慢），和遮罩淡出同时开始
        voyage.settled = true;
        arrival = { startedAt: wall, to: voyage.restoreZoom ?? camera.zoom };
        camera.zoom = arrival.to * ARRIVAL.from;
        camera.updateProjectionMatrix();
      }
      alpha = Math.max(0, 1 - (wall - arriveAt) / ARRIVAL.fadeMs);
      if (alpha === 0) { voyage = null; status.voyage = null; return null; }
    }
    const progress = Math.min(1, (wall - startedAt) / Math.max(1600, (arriveAt ?? wall + 2000) - startedAt));
    return { alpha, progress, caption };
  }

  /** 靠岸之后镜头推近（arrival）：从 0.82 倍推到原来的缩放。 */
  const ARRIVAL = Object.freeze({ from: 0.82, ms: 1200, fadeMs: 650 });

  // —— 夜里（M3 第三轮：跟着应用的深 / 浅色主题）：nightK 朝 nightTarget 走（时间常数 380 毫秒，一秒多走完），
  // 灯光、天色、调色、叠加层一起换；第一次（打开页面时就是深色）直接到位，不从白天过渡 ——
  const NIGHT_TAU_MS = 380;
  let nightK = 0, nightTarget = 0;
  function stepNight(dt) {
    if (nightK === nightTarget) return;
    const k = 1 - Math.exp(-Math.max(0, dt) / NIGHT_TAU_MS);
    nightK += (nightTarget - nightK) * k;
    if (Math.abs(nightTarget - nightK) < 0.004) nightK = nightTarget;
    applyNight();
  }
  function applyNight() {
    R.setNight(nightK);
    setNightWindows(M, nightK);   // 窗里的灯火跟着夜色一起亮起来
    overlay.setNight(nightK > 0.5);
    if (fogInfo && R.scene.fog) R.scene.fog.color.set(nightK > 0.5 ? 0x1b1e23 : 0xd8d4cd);
    status.night = +nightK.toFixed(3);
  }
  let arrival = null;
  function applyArrival(wall) {
    const f = Math.min(1, (wall - arrival.startedAt) / ARRIVAL.ms);
    const k = 1 - Math.pow(1 - f, 3);
    camera.zoom = arrival.to * (ARRIVAL.from + (1 - ARRIVAL.from) * k);
    camera.updateProjectionMatrix();
    if (f >= 1) arrival = null;
  }

  function applyReveal(wall) {
    // 奠基揭幕：从高处俯瞰慢慢推近（镜头缩放从 0.45 推到 1），只播一次
    const f = Math.min(1, (wall - reveal.startedAt) / REVEAL_MS);
    camera.zoom = 0.45 + 0.55 * ease(f);
    camera.updateProjectionMatrix();
    if (f >= 1) reveal = null;
  }

  const fill = (template, vars) => String(template ?? '').replace(/\{(\w+)\}/g, (_, k) => vars[k] ?? '');
  const venueName = (venue) => publicNames[venue] ?? venue;
  const defaultVenue = (kind) => (kind === 'painting' ? 'paintedStoa' : kind === 'ledger' ? 'vault' : 'archive');

  function cardView(id) {
    const record = items.get(id);
    if (!record) return null;
    const { item } = record;
    const kindLabel = { scroll: ui.kindScroll, ledger: ui.kindLedger, painting: ui.kindPainting, answer: ui.kindAnswer }[item.kind] ?? ui.kindScroll;
    const stateLabel = item.lost ? ui.stateLost
      : item.modified ? `${{ pending: ui.statePending, accepted: ui.stateAccepted, returned: ui.stateReturned }[item.state] ?? ''} · ${ui.stateModified}`
        : { pending: ui.statePending, accepted: ui.stateAccepted, returned: ui.stateReturned }[item.state] ?? '';
    const where = item.state === 'accepted'
      ? (item.placement ? fill(ui.storedIn, { venue: venueName(item.placement.venue) }) : ui.storedNowhere)
      : null;
    return {
      title: item.title ?? '', kindLabel, stateLabel, where, state: item.state, path: item.path ?? null,
      placed: !!item.placement, lost: !!item.lost,
    };
  }

  function placingView() {
    const record = items.get(placing);
    if (!record) return null;
    const venue = defaultVenue(record.item.kind);
    return { text: fill(ui.placeHint, { title: (record.item.title ?? '').replace(/^《|》$/g, '') }), defaultLabel: fill(ui.placeDefault, { venue: venueName(venue) }) };
  }

  function panelView() {
    if (!panel) return null;
    switch (panel.type) {
      case 'resident': {
        const r = residents.find((x) => x.kind === panel.id);
        if (!r) return null;
        return { type: 'resident', bust: panel.id, expression: 'speak', title: r.name ?? panel.id, lines: [r.intro ?? ''], askable: !!onChat };
      }
      case 'citizen': {
        const c = world?.citizens.get(panel.id);
        if (!c) return null;
        const askable = !!onChat;
        // 那场会话的信息（标题、办到哪一步、多久前动过、常去哪儿）和人口；"看看那场会话"打开预览
        const lines = citizenInfoLines(c.data, ui);
        if (citizens.census) lines.push(fill(ui.censusLine, { total: citizens.census.total, street: citizens.census.street }));
        return { type: 'citizen', bust: c.look.bust, expression: 'speak', title: c.data.name ?? '', lines, askable };
      }
      case 'bema':
        return { type: 'bema', bust: 'herald', expression: 'speak', title: ui.bemaTitle, lines: [ui.bemaBody, fill(ui.bemaRecord, debateRecord)] };
      case 'stele': {
        const s = world?.steles.get(panel.id);
        if (!s) return null;
        return { type: 'stele', bust: 'librarian', expression: 'neutral', title: ui.steleTitle, lines: [ui.steleBody, s.names.slice(0, 40).join('、')] };
      }
      case 'board':
        return { type: 'board', bust: 'athena', expression: 'smile', title: ui.boardTitle, hint: ui.boardHint, suggestions };
      case 'oracle':
        return {
          type: 'oracle', bust: oracle?.exists ? 'priestess' : 'athena', expression: 'neutral', title: oracle?.title ?? ui.oracleTitle,
          lines: [oracle?.body ?? ui.oracleNone], exists: !!oracle?.exists, latest: oracle?.latestConversationId ?? null,
        };
      case 'venue': {
        const v = world?.venues.get(panel.id);
        if (!v) return null;
        const placed = v.slots.filter((s) => s.item).length;
        const stored = [...items.values()].filter((r) => r.item.state === 'accepted' && !r.item.placement).slice(0, 5).map((r) => ({ id: r.item.id, title: r.item.title ?? '' }));
        return { type: 'venue', bust: panel.id === 'paintedStoa' ? 'painter' : panel.id === 'vault' ? 'accountant' : 'scribe', expression: 'neutral',
          title: venueName(panel.id), lines: [fill(ui.venueCount, { count: placed, total: SLOTS_PER_VENUE })], stored, empty: ui.venueEmpty, storedTitle: ui.venueStored, placeHere: ui.placeHere };
      }
      default:
        return null;
    }
  }

  /** 群岛海图（2D，画在叠加层上）：开着时才算布局。 */
  let chartOpen = false;
  function chartView() {
    if (!chartOpen) return null;
    const layoutIslands = islandLayout(islands);
    const transform = chartTransform(layoutIslands, window.innerWidth, window.innerHeight);
    return { islands, layout: layoutIslands, transform, title: ui.chartTitle, hint: ui.chartHint, labels: { running: ui.chartRunning, waiting: ui.chartWaiting, here: ui.chartHere, missing: ui.chartMissing, sanctuary: publicNames.sanctuary } };
  }

  // —— 输入：鼠标 ——
  // 左键点一下只点叠加层上的按钮和海图（城里的东西走近按 E）；左键拖动是转镜头（超过 VIEW.dragPx 才算拖）；
  // 右键拖动、滚轮交给 OrbitControls。
  const canvas = R.renderer.domElement;
  let downAt = null;   // { x, y, lastX, lastY, rotating }
  canvas.addEventListener('pointerdown', (e) => {
    dragButton = e.button;
    dragModified = e.shiftKey || e.ctrlKey || e.metaKey;
    downAt = { x: e.clientX, y: e.clientY, lastX: e.clientX, lastY: e.clientY, rotating: false };
    lastInputAt = performance.now();
  });
  canvas.addEventListener('pointermove', (e) => {
    if (downAt) {
      if (dragButton === 0 && !dragModified && !chartOpen) {
        if (!downAt.rotating && Math.hypot(e.clientX - downAt.x, e.clientY - downAt.y) > VIEW.dragPx) {
          downAt.rotating = true;
          glide = null;
        }
        if (downAt.rotating) {
          orbitBy(e.clientX - downAt.lastX, e.clientY - downAt.lastY);
          downAt.lastX = e.clientX;
          downAt.lastY = e.clientY;
        }
      }
      updateCursor();
      return;
    }
    updateCursor();
  });
  canvas.addEventListener('pointerup', (e) => {
    const d = downAt;
    downAt = null;
    dragButton = -1;
    dragModified = false;
    lastInputAt = performance.now();
    updateCursor();
    if (!d || e.button !== 0 || d.rotating || Math.hypot(e.clientX - d.x, e.clientY - d.y) > VIEW.dragPx) return;
    click(e.clientX, e.clientY);
  });
  canvas.addEventListener('pointercancel', () => { downAt = null; dragButton = -1; dragModified = false; updateCursor(); });
  canvas.addEventListener('pointerleave', () => { if (!downAt) updateCursor(); });
  canvas.addEventListener('contextmenu', (e) => e.preventDefault());
  // 滚轮、中键拖动是 OrbitControls 在改远近：退出近景的退回就不再管远近（见 restoring）
  canvas.addEventListener('wheel', () => { if (restoring) restoring.zoom = null; }, { passive: true });
  canvas.addEventListener('pointerdown', (e) => { if (e.button === 1 && restoring) restoring.zoom = null; });

  function updateCursor() {
    const cursor = downAt?.rotating ? 'grabbing' : '';
    if (canvas.style.cursor !== cursor) canvas.style.cursor = cursor;
  }

  /**
   * 城里能互动的东西，各自的位置和够得着的距离（米）：雅典娜、居民、地上待收的成果与底座上的藏品（遗失的只剩底座）、
   * 摆放时的空底座、橄榄树、委托板、神庙（神谕）、三处场馆。priority 小的先挑（摆放时空底座最先）。
   */
  const REACH = Object.freeze({ slot: 1.5, item: 1.7, athena: 2.2, resident: 2.0, citizen: 2.0, board: 2.6, bema: 2.4, tree: 2.2, stele: 1.8, oracle: 3.2, venue: 3.2 });
  // 讲台排在成果之后、雅典娜之前：广场上总有居民在讲台边转悠，排在他们后面，走到台阶前按 E 常常打开的是哪位居民的卡片（冒烟踩过）
  const PRIORITY = Object.freeze({ slot: 0, item: 1, bema: 1.5, athena: 2, resident: 3, citizen: 3.5, board: 4, tree: 5, stele: 5.5, oracle: 6, venue: 7 });
  function interactables(pose) {
    const list = [];
    if (!world) return list;
    const add = (type, x, z, extra = {}) => list.push({ type, x, z, reach: REACH[type], priority: PRIORITY[type], ...extra });
    if (placing) {
      for (const v of world.venues.values()) for (const s of v.slots) if (!s.item) add('slot', s.world.x, s.world.z, { venue: v.venue, slot: s.index });
    }
    for (const record of items.values()) {
      const object = record.display?.visible ? record.display : record.object;
      if (!object || !object.visible) continue;
      const p = object.getWorldPosition(tmpPick);
      add('item', p.x, p.z, { id: record.item.id });
    }
    for (const v of world.venues.values()) for (const s of v.slots) {
      if (s.item && items.get(s.item)?.item.lost) add('item', s.world.x, s.world.z, { id: s.item });
    }
    if (onTalk && pose?.visible && !pose.teleport) add('athena', world.athena.position.x, world.athena.position.z);
    for (const r of world.residents.values()) add('resident', r.figure.root.position.x, r.figure.root.position.z, { id: r.kind });
    for (const c of world.citizens.values()) if (c.figure?.root.visible) add('citizen', c.figure.root.position.x, c.figure.root.position.z, { id: c.id });
    for (const s of world.steles.values()) add('stele', s.x, s.z, { id: s.home });
    // 讲台（M-E）：台阶前那一点（实时模式才有：夹具里没有人跟你辩）。在辩、在判的时候按 E 没有事可做，不给提示
    if (onDebate && stage() && (!debate || debate.state === 'awaiting')) add('bema', stageInfo.interact.x, stageInfo.interact.z, { id: 'bema' });
    for (const t of world.groveTrees) add('tree', t.base.x, t.base.z, { id: t.conversationId, title: t.title });
    if (world.agora?.userData.board) {
      const b = tmpPick.copy(world.agora.userData.board).add(world.agora.position);
      add('board', b.x, b.z + 0.6);
    }
    const temple = layout.plots.get('temple');
    if (temple && world.oracle) add('oracle', world.oracle.position.x, world.oracle.position.z);
    for (const v of world.venues.values()) add('venue', v.plot.cx, v.plot.cz - 1.2, { id: v.venue });
    return list;
  }
  const tmpPick = new THREE.Vector3();

  /** 你此刻够得着的那一样：离你近、在你面前的先挑。过场、雾、海图、阅读器挡着的时候什么也不挑。 */
  function pickFocus(pose) {
    if (!world || voyage || fogInfo || chartOpen || modalOpen()) return null;
    const list = interactables(pose);
    if (forcedFocus) return list.find((c) => c.type === forcedFocus.type && (forcedFocus.id == null || c.id === forcedFocus.id)) ?? null;
    const px = player.pos.x, pz = player.pos.z, fx = Math.sin(player.yaw), fz = Math.cos(player.yaw);
    let best = null, bestScore = Infinity;
    for (const c of list) {
      const dx = c.x - px, dz = c.z - pz, d = Math.hypot(dx, dz);
      if (d > c.reach) continue;
      const facing = d > 1e-3 ? (dx * fx + dz * fz) / d : 1;
      const score = d - 0.6 * facing + c.priority * 0.08;
      if (score < bestScore) { best = c; bestScore = score; }
    }
    return best;
  }

  /** 脚下那块提示上写什么。 */
  function promptText(target) {
    const clip = (str, n = 14) => { const t = String(str ?? ''); return t.length > n ? `${t.slice(0, n - 1)}…` : t; };
    switch (target.type) {
      case 'athena': return athenaHasWords ? ui.interactTalkWaiting : ui.interactTalk;
      case 'resident': return fill(ui.interactResident, { name: residents.find((r) => r.kind === target.id)?.name ?? target.id });
      case 'citizen': return fill(ui.interactCitizen, { name: clip(world?.citizens.get(target.id)?.data.name ?? '', 16) });
      case 'stele': return ui.interactStele;
      case 'bema': return ui.interactBema;
      case 'item': return fill(ui.interactItem, { title: clip((items.get(target.id)?.item.title ?? '').replace(/^《|》$/g, '')) });
      case 'slot': return ui.interactPlace;
      case 'tree': return fill(ui.interactTree, { title: clip(target.title) });
      case 'board': return ui.interactBoard;
      case 'oracle': return ui.interactOracle;
      case 'venue': return fill(ui.interactVenue, { venue: venueName(target.id) });
      default: return '';
    }
  }

  /** 触发一样东西（按 E；测试也直接调它）。 */
  function activate(target) {
    if (!target) return false;
    switch (target.type) {
      case 'slot': placeItem(placing, target.venue, target.slot); break;
      case 'item': card = target.id; panel = null; break;
      case 'athena': openTalk(); break;
      case 'resident': openResident(target.id); break;
      case 'citizen': openCitizen(target.id); break;
      case 'stele': openPanel({ type: 'stele', id: target.id }); break;
      case 'bema':
        // 正在等你说：直接把栏借过来；没有在辩：讲台的卡片（战绩、提一个议题）
        if (debate?.state === 'awaiting') onDebate?.({ stage: 'argue', round: debate.round, rounds: debate.rounds ?? 3, motion: debate.motion ?? '', id: debate.id });
        else if (!debate) openPanel({ type: 'bema' });
        break;
      case 'tree':
        // 一棵树 = 一份委托：走会话树的同一条路径（9.2）
        send('select-conversation', { conversationId: target.id });
        status.treeSelected = target.id;
        break;
      case 'board': openPanel({ type: 'board' }); break;
      case 'oracle': openPanel({ type: 'oracle' }); break;
      case 'venue': openPanel({ type: 'venue', id: target.id }); break;
      default: return false;
    }
    status.interacted = (status.interacted ?? 0) + 1;
    return true;
  }

  /**
   * 鼠标点一下：只认叠加层上的按钮（卡片、面板、"雅典娜在这边"）和海图上的岛；城里的东西不再是点的（走近按 E）。
   * 点空地收起成果卡片。
   */
  function click(x, y) {
    const hitId = overlay.hit(x, y);
    if (hitId) { onButton(hitId); return; }
    if (chartOpen) {
      const view = chartView();
      const id = hitIsland(view.layout, view.transform, x, y);
      if (id) selectIsland(id);
      return;
    }
    if (world && !fogInfo && !voyage) card = null;
  }

  /** 和雅典娜说话：卡片、面板先收起来（底部同一时间只有一块），对话栏展开。 */
  function openTalk() {
    if (!onTalk) return;
    card = null;
    panel = null;
    pressed.clear();
    syncUiBlocked();
    status.talkOpened = (status.talkOpened ?? 0) + 1;
    athenaHasWords = false;
    status.athenaHasWords = false;
    onTalk();
  }

  /**
   * 你自己说了一句（对话栏平时的样子，2026-10-13）：你的话出现在你头顶；附近（SPEAK_RADIUS 以内、在画面里）最近的那一位接一句——
   * 居民或职业居民，C# 的 chat 带着 overheard（他听见你在旁边说）。没人在旁边就只是你说了一句。以"/"开头的命令、以"@"开头的
   * 点名还没有：你头顶说一句"还在路上"，什么也不发。这是以后接命令与点名的地方（talk.parseBarInput）。
   */
  function speak(parsed) {
    if (!world || !parsed) return null;
    const wall = performance.now();
    if (parsed.kind !== 'say') {
      mine = { text: parsed.kind === 'command' ? ui.speakCommandSoon : ui.speakMentionSoon, at: wall };
      status.spoke = { kind: parsed.kind, responder: null };
      return null;
    }
    mine = { text: parsed.text, at: wall };
    const near = [];
    for (const c of world.citizens.values()) {
      if (!c.figure?.root.visible || citizenTiers.get(c.id) === 'C' || audienceOf.has(c.id)) continue;
      if (chatTalk && (chatTalk.a === c.id || chatTalk.b === c.id)) continue;
      const p = c.figure.root.position;
      near.push({ id: c.id, d: Math.hypot(p.x - player.pos.x, p.z - player.pos.z) });
    }
    for (const r of world.residents.values()) {
      const p = r.figure.root.position;
      if (!r.figure.root.visible) continue;
      near.push({ id: `resident:${r.kind}`, d: Math.hypot(p.x - player.pos.x, p.z - player.pos.z) });
    }
    const who = near.filter((n) => n.d <= SPEAK_RADIUS && inView(...posOfSpeaker(n.id))).sort((a, b) => a.d - b.d || (a.id < b.id ? -1 : 1))[0]?.id ?? null;
    status.spoke = { kind: 'say', responder: who };
    if (!who) return null;
    heard = { id: who, until: wall + HEARD_HOLD_MS };
    sayings.set(who, { text: '', typing: true, at: wall, doneAt: null });
    send('chat', { who, text: parsed.text, overheard: true });
    return who;
  }

  /** 一位说话的人此刻站在哪儿（居民或职业居民）：[x, z]。 */
  function posOfSpeaker(id) {
    const root = id.startsWith('resident:') ? world.residents.get(id.slice(9))?.figure.root : world.citizens.get(id)?.figure?.root;
    return root ? [root.position.x, root.position.z] : [Infinity, Infinity];
  }

  /**
   * 有一段替你写好、要托付给雅典娜的话在等你（委托板、神谕、退回；2026-10-13 起不自己展开，用户定的）：她在城里说一句，
   * 走到她跟前，脚下的提示写着"有一段写好的话"；按 E，栏带着这段话打开。
   */
  function prefillWaiting() {
    athenaHasWords = true;
    status.athenaHasWords = true;
    say(ui.athenaHasWords, clock, 3600);
  }

  // —— 输入：键盘走路 ——
  // 只认物理键位（MOVE_KEYS）；落在对话栏里的按键是字，不是脚步；输入法正在选字、按着 ⌘ / Ctrl / Alt 的组合键不算。
  window.addEventListener('keydown', (e) => {
    if (e.key === 'Shift') { running = true; return; }
    const dir = MOVE_KEYS[e.code];
    if (!dir || isTypingTarget(e.target) || e.isComposing || e.metaKey || e.ctrlKey || e.altKey) return;
    if (!movementAllowed()) return;
    e.preventDefault();   // 方向键不去滚动页面
    pressed.add(dir);
    running = e.shiftKey;
    lastInputAt = performance.now();
  });
  // 交互键 E（物理键位，开着中文输入法也一样）：开着卡片或面板就收起；否则触发你跟前的那一样
  window.addEventListener('keydown', (e) => {
    if (e.code !== INTERACT_KEY || e.repeat || isTypingTarget(e.target) || e.isComposing || e.metaKey || e.ctrlKey || e.altKey) return;
    if (!world || voyage || fogInfo || chartOpen || modalOpen()) return;
    e.preventDefault();
    lastInputAt = performance.now();
    if (placing && focus?.type !== 'slot') return;
    if (!placing && (card || panel)) { card = null; panel = null; return; }
    activate(focus);
  });
  // 松开一律认（哪怕焦点已经跑到对话栏里），否则会卡住一直走
  window.addEventListener('keyup', (e) => {
    if (e.key === 'Shift') running = false;
    const dir = MOVE_KEYS[e.code];
    if (dir) pressed.delete(dir);
  });
  window.addEventListener('blur', () => { pressed.clear(); running = false; });

  function openPanel(next) {
    panel = next;
    card = null;
    status.panel = next.type;
  }

  /** 挑中了一对：记下冷却，先开口的那位头顶马上是一串点，请 C# 让他们说（ambient-chat）。 */
  function startAmbient(pick, wall) {
    chatTalk = pick;
    chatCooldown.set(pick.key, wall);
    sayings.set(pick.a, { text: '', typing: true, at: wall, doneAt: null });
    send('ambient-chat', { a: pick.a, b: pick.b });
  }

  /**
   * C# 说这一对的闲聊结束了（ambient-end：{ a, b, status }）：散了，还在想的那位头顶的点收起来；按结局决定多久之后再挑下一对
   * （citizens.nextChatAfter：没配模型就这座城里不再挑——没有本地台词可退）。
   */
  function endAmbient(m) {
    if (!chatTalk || chatTalk.a !== m?.a || chatTalk.b !== m?.b) return;
    for (const id of [chatTalk.a, chatTalk.b]) if (sayings.get(id)?.typing) sayings.delete(id);
    chatTalk = null;
    nextChatPickAt = nextChatAfter(String(m.status ?? 'ok'), performance.now());
    status.ambient = { a: m.a, b: m.b, status: String(m.status ?? 'ok') };
  }

  /**
   * C# 推来的一句话（talk，2026-10-13）：{ who, text, done, status }。流式的半句话（done 为假）边到边换；说完的那一版定下来，
   * 你还在和他聊的话，对话栏回到"你在写"（onChatReplied → talk.resumeLent）。只认这座城里此刻有的居民。
   */
  function setTalk(m) {
    const id = m?.who;
    if (!world?.citizens.has(id) && !(typeof id === 'string' && id.startsWith('resident:') && world?.residents.has(id.slice(9)))) return;
    const now = performance.now();
    const text = String(m.text ?? '');
    const prev = sayings.get(id);
    const n = Number(m.n ?? 0);
    if (!m.done) {
      // 说完了才到的半句话（同一句——编号相同——攒批里排在后面）：不覆盖说完的那一版；空的半句话是新的一句开头了（头顶一串点）
      if (text.length > 0 && prev?.doneAt != null && prev.n === n) return;
      sayings.set(id, { text, typing: text.length === 0, at: text.length === 0 ? now : prev?.at ?? now, doneAt: null, n });
      return;
    }
    sayings.set(id, { text, typing: false, at: prev?.at ?? now, doneAt: now, n });
    status.talk = { id, status: String(m.status ?? 'ok'), text };
    if (convo?.id === id && convo.status === 'waiting') {
      convo = { ...convo, status: 'typing' };
      onChatReplied?.(id);
    }
    // 接你话的那一位说完了：再站一会儿就接着过他的日子
    if (heard?.id === id) heard = { id, until: now + HEARD_AFTER_MS };
  }

  /** C# 推来的那场会话预览（citizen-transcript）：进卷轴阅读器。只认这座城里此刻有的居民。 */
  function showCitizenTranscript(m) {
    const c = world?.citizens.get(m?.conversationId);
    if (!c) return;
    status.transcript = { id: c.id, entries: Array.isArray(m.entries) ? m.entries.length : 0, omitted: m.omitted ?? 0 };
    onRead?.({ title: fill(ui.transcriptTitle, { name: c.data.name ?? '', title: m.title || c.data.title || ui.citizenInfoUntitled }), markdown: transcriptMarkdown(m, ui) });
  }

  /** 一位居民的卡片（M-B）：他说的那几句、这座城的人口，"去看那份委托"。他站定、转身、挥手（animateCitizens）。 */
  function openCitizen(id) {
    if (!world?.citizens.has(id)) return;
    openPanel({ type: 'citizen', id });
    status.citizenOpened = id;
  }

  function openResident(kind) {
    openPanel({ type: 'resident', id: kind });
    const r = residents.find((x) => x.kind === kind);
    if (r && !r.met) {
      r.met = true;
      send('meet-resident', { residentId: kind });
      if (world) world.setResidents(residents, (k) => residents.find((x) => x.kind === k)?.name ?? k);
    }
  }

  function selectIsland(id) {
    chartOpen = false;
    status.chartOpen = false;
    send('select-island', { islandId: id });
    status.islandSelected = id;
  }

  function startPlacing(id) {
    const record = items.get(id);
    if (!record || record.item.state !== 'accepted') return;
    placing = id;
    card = null;
    status.placing = id;
    const venue = world?.venues.get(defaultVenue(record.item.kind));
    if (venue) lookAt(venue.plot.cx, venue.plot.cz + 2.5);
  }

  function placeItem(id, venue, slot) {
    const record = items.get(id);
    placing = null;
    status.placing = null;
    if (!record) return;
    // 本地先摆上（即时反馈），再告诉 C#——它是唯一的事实来源，随后推来的 items 会覆盖这里
    record.item = { ...record.item, placement: slot == null ? record.item.placement : { venue, slot } };
    send('place-item', slot == null ? { itemId: id, venue } : { itemId: id, venue, slot });
  }

  function onButton(id) {
    if (id.startsWith('herald-line:')) {
      const line = herald?.lines?.[Number(id.slice('herald-line:'.length))];
      if (line?.conversationId) send('select-conversation', { conversationId: line.conversationId });
      else if (line?.itemId && items.has(line.itemId)) card = line.itemId;
      return;
    }
    if (id.startsWith('suggestion:')) {
      const s = suggestions[Number(id.slice('suggestion:'.length))];
      if (s) send('take-suggestion', { suggestionId: s.id });
      panel = null;
      return;
    }
    if (id.startsWith('venue-place:')) {
      const itemId = id.slice('venue-place:'.length);
      if (panel?.type === 'venue') placeItem(itemId, panel.id, null);
      panel = null;
      return;
    }
    if (id.startsWith('island:')) { selectIsland(id.slice('island:'.length)); return; }
    switch (id) {
      case 'herald-dismiss': herald = null; break;
      case 'relocate': send('relocate'); break;
      case 'card-close': card = null; break;
      case 'card-read': {
        const record = items.get(card);
        if (record) send('read-scroll', { itemId: record.item.id });
        break;
      }
      case 'card-open': {
        const record = items.get(card);
        if (record?.item.path) send('open-file', { path: record.item.path });
        break;
      }
      case 'card-accept': decide('accepted'); break;
      case 'card-return': decide('returned'); break;
      case 'card-place': startPlacing(card); break;
      case 'card-unplace': {
        const record = items.get(card);
        if (record) { record.item = { ...record.item, placement: null }; send('unplace-item', { itemId: record.item.id }); }
        card = null;
        break;
      }
      case 'card-remove': {
        // 遗失的藏品：只有你点了"移除"才删记录（11.3）
        const record = items.get(card);
        if (record?.item.lost) send('remove-item', { itemId: record.item.id });
        card = null;
        break;
      }
      case 'placing-default': {
        const record = items.get(placing);
        if (record) placeItem(placing, defaultVenue(record.item.kind), null);
        break;
      }
      case 'placing-cancel': placing = null; status.placing = null; break;
      case 'panel-close': panel = null; break;
      case 'resident-chat': {
        // 和一位职业居民聊几句（2026-10-13：工具能力撤下了，他是城里有职业、有性子的人）：和居民同一条路——对话栏借给他，
        // 他站定面朝你，他的话在他头顶。who 是 resident:<名字>
        if (panel?.type !== 'resident' || !onChat) break;
        const r = residents.find((x) => x.kind === panel.id);
        if (!r || !world?.residents.has(panel.id)) break;
        convo = { id: `resident:${panel.id}`, status: 'typing', sentAt: 0 };
        status.chatting = convo.id;
        panel = null;
        syncUiBlocked();
        onChat({ conversationId: convo.id, name: r.name ?? r.kind, bust: r.kind });
        break;
      }
      case 'citizen-transcript':
        // 看看那场会话：C# 回 citizen-transcript，进卷轴阅读器（卡片留着）
        if (panel?.type === 'citizen') send('citizen-transcript', { conversationId: panel.id });
        break;
      case 'citizen-open':
        // 一位居民 = 一份委托：走会话树的同一条路径（9.2）
        if (panel?.type === 'citizen') send('select-conversation', { conversationId: panel.id });
        panel = null;
        break;
      case 'bema-propose':
        // 提一个议题：卡片收起（先让对话栏不再让位），栏借给辩论；回车发 debate-start
        if (panel?.type !== 'bema' || !onDebate) break;
        panel = null;
        syncUiBlocked();
        motionPending = true;
        onDebate({ stage: 'motion', id: 'motion' });
        break;
      case 'debate-close':
        endDebateFromPage();
        break;
      case 'citizen-chat': {
        // 和他聊几句：卡片收起，对话栏借给他（main.mjs 的 onChat → talk.openChat）；他站定面朝你，镜头留在你们两个身上，
        // 他的话出现在他头顶。Esc 告辞
        if (panel?.type !== 'citizen' || !onChat) break;
        const c = world?.citizens.get(panel.id);
        if (!c) break;
        convo = { id: c.id, status: 'typing', sentAt: 0 };
        status.chatting = c.id;
        panel = null;
        // 卡片收起要先让对话栏不再让位（不等下一帧），借给他的那一下输入框才拿得到焦点
        syncUiBlocked();
        onChat({ conversationId: c.id, name: c.data.name ?? '', bust: c.look.bust });
        break;
      }
      case 'oracle-request': send('request-oracle'); panel = null; break;
      case 'oracle-latest':
        if (oracle?.latestConversationId) send('select-conversation', { conversationId: oracle.latestConversationId });
        panel = null;
        break;
      case 'chart-close': chartOpen = false; status.chartOpen = false; break;
      case 'follow-athena': setFollow('athena'); break;
      default: break;
    }
  }

  // 收下 / 退回：本地先变（即时反馈），再告诉 C#——它是唯一的事实来源，随后推来的 items 会覆盖这里
  function decide(state) {
    const record = items.get(card);
    if (!record) return;
    record.item = { ...record.item, state };
    record.collectedAt = performance.now();
    send(state === 'accepted' ? 'accept-delivery' : 'return-delivery', { itemId: record.item.id });
    card = null;
  }

  /** 视角轮换：自由 → 跟着你 → 跟着雅典娜（V 键）。 */
  function cycleView() { setFollow(nextView(follow)); }

  /** 换一份资产清单（应用推来的覆盖）：先用程序化的把城画出来，外部模型取到了再整城重建一次。 */
  async function setAssetManifest(manifest) {
    assets = createAssetLibrary(M, { manifest });
    status.assets = assets.report;
    if (assets.report.requested.length === 0) return 0;
    const loaded = await assets.prepare();
    status.assets = assets.report;
    if (loaded > 0 && city) loadCity(city, { key: cityKey, sanctuary, now: clock });
    return loaded;
  }

  return {
    R, camera, controls, overlay,
    get world() { return world; },
    get layout() { return layout; },
    get city() { return city; },
    /** 雅典娜的时间线（athena.mjs）：测试与运动自检读它。 */
    get athenaSim() { return athenaSim; },
    /** 请她去一个地方（站点名）：走得到就走，远了化作一道光（运动自检用它造出"走"和"瞬移"两个时间窗）。 */
    athenaGoTo(site, t = clock) { return athenaSim?.goTo(site, t) ?? false; },
    /** 页面的时钟（frame 的 now，只进不退）。 */
    get clock() { return clock; },
    get used() { return used; },
    get follow() { return follow; },
    set follow(v) { setFollow(v); },
    get regions() { return regions; },
    get assets() { return assets; },
    placeCamera,
    loadCity,
    updateCity,
    frame,
    setFollow,
    cycleView,
    orbitBy,
    openTalk,
    /** 你托付了一件事：接下来两秒多你做"说"的手势（M3）。 */
    noteSaid() { anim.saidAt = performance.now(); },
    /** 对话栏拿走了键盘：按住的方向键全部松开，人停下。 */
    releaseKeys() { pressed.clear(); running = false; },
    /** Esc：收起摆放提示、卡片或面板（一次一层）。 */
    dismiss() {
      if (placing) { placing = null; status.placing = null; } else if (card) card = null; else if (panel) panel = null;
      else if (debate) endDebateFromPage();
    },
    /** 镜头此刻的方位角与仰角（度）：测试用。 */
    cameraAngles() {
      offsetTmp.copy(camera.position).sub(controls.target);
      spherical.setFromVector3(offsetTmp);
      return { azimuth: THREE.MathUtils.radToDeg(spherical.theta), elevation: 90 - THREE.MathUtils.radToDeg(spherical.phi) };
    },
    onButton,
    click,
    setAssetManifest,
    /** 面板此刻画的内容（测试用）。 */
    panelView,
    get ui() { return ui; },
    setTables(tables) {
      if (tables.ui) ui = { ...UI_ZH, ...tables.ui };
      if (tables.publicNames) publicNames = { ...PUBLIC_NAMES, ...tables.publicNames };
      if (tables.citizen) citizenWords = { ...CITIZEN_ZH, ...tables.citizen };
    },
    /**
     * 居民即会话（M-B）：C# 推来的 { street: [...最多 32 位], elders: [...], census }。人物一帧建两位（不卡），
     * 搬了家（家换了）的重新排日程；不在名单里的拆掉。
     */
    setCitizens(payload) {
      citizens = {
        street: Array.isArray(payload?.street) ? payload.street.filter((c) => c && c.id != null).slice(0, TOWN.streetCap) : [],
        elders: Array.isArray(payload?.elders) ? payload.elders : [],
        census: payload?.census ?? null,
      };
      if (chatTalk && !citizens.street.some((c) => c.id === chatTalk.a || c.id === chatTalk.b)) chatTalk = null;
      if (panel?.type === 'citizen' && !citizens.street.some((c) => c.id === panel.id)) panel = null;
      applyAccumulation();
    },
    /** 居民此刻的分档（测试用）。 */
    get citizenTiers() { return citizenTiers; },
    /**
     * 对话栏借出去之后的进展（talk.mjs 的 onLent）：问话——'sent' 问出去了开始等、'cancel' 不问了；
     * 辩论——'cancel' 是不辩了（提议题时按 Esc 什么也不发；辩到一半按 Esc 发 debate-end）。
     */
    lentEvent(event, info = {}) {
      if (info.kind === 'chat') {
        if (!convo) return;
        if (event === 'sent' && info.id === convo.id) {
          const now = performance.now();
          convo = { ...convo, status: 'waiting', sentAt: now };
          // 你的话在你头顶；他开始想（头顶一串点），之前那一句换掉
          mine = { text: String(info.text ?? ''), at: now };
          sayings.set(convo.id, { text: '', typing: true, at: now, doneAt: null });
        } else if (event === 'cancel') {
          // 告辞：他最近的那一句照常留一阵再淡出（还在想的就不想了）
          const s = sayings.get(convo.id);
          if (s?.typing) sayings.delete(convo.id);
          convo = null;
          status.chatting = null;
        }
      } else if (info.kind === 'debate') {
        if (event === 'cancel') {
          if (debate) endDebateFromPage();
          motionPending = false;
        }
      }
    },
    setDebate,
    /** 讲台此刻的现状与陪审团（测试用）。 */
    get debate() { return debate ? { ...debate } : null; },
    get audience() { return new Map(audienceOf); },
    get stage() { return stage(); },
    setTalk,
    endAmbient,
    speak,
    prefillWaiting,
    /** 接你话的那一位（测试用）。 */
    get heard() { return heard ? { ...heard } : null; },
    /** 让两位居民马上聊（冒烟测试用：走和挑人时一样的那条路——发 ambient-chat、先开口的那位头顶一串点）。 */
    ambientNow(a, b) {
      if (!world?.citizens.has(a) || !world?.citizens.has(b) || a === b) return false;
      if (chatTalk) endAmbient({ a: chatTalk.a, b: chatTalk.b, status: 'ok' });   // 城里自己挑的那一对先散
      const wall = performance.now();
      startAmbient({ a, b, key: a < b ? `${a}|${b}` : `${b}|${a}`, start: wall }, wall);
      return true;
    },
    /** 下一次挑一对的时刻（测试用：没配模型之后是 Infinity）。 */
    get nextChatPickAt() { return nextChatPickAt; },
    showCitizenTranscript,
    /** 此刻在和谁聊（测试用）。 */
    get chatting() { return convo ? { ...convo } : null; },
    /** 头顶此刻的话（测试用）：id → { text, typing, doneAt }。 */
    get sayings() { return new Map(sayings); },
    /** 你头顶此刻的那一句（测试用）。 */
    get mine() { return mine ? { ...mine } : null; },
    /** 让一位居民向你打招呼（运动自检用：保证走路那一段有居民头顶的话要逐帧核对）。 */
    greetCitizen(id) {
      const c = world?.citizens.get(id);
      if (!c?.figure) return false;
      const wall = performance.now();
      greeting = { id, start: wall, line: greetLine(c.data.seed, citizenWords.greet, wall) };
      greetedAt.set(id, wall);
      return true;
    },
    /**
     * C# 推来的成果清单（整份）。这座城装好之后才到的新的待收成果，就是刚做完的那一回合：直接出现在你脚下，你伸手接一下
     * （2026-10-13：她不再走过来递——直播拿掉了）。装城时就已经在的（重启、你离开期间交付的）在广场南边排成一排等你。
     */
    setItems(list) {
      const seen = new Set();
      const wall = performance.now();
      let handed = false;
      for (const item of list ?? []) {
        seen.add(item.id);
        const record = items.get(item.id);
        if (record) {
          const wasPending = record.item.state === 'pending';
          record.item = item;
          if (wasPending && item.state !== 'pending' && record.collectedAt == null) record.collectedAt = wall;
        } else {
          const fresh = itemsPrimed && world != null && item.state === 'pending' && !item.lost && !item.placement;
          const spot = fresh ? { x: player.pos.x + 0.4, z: player.pos.z + 0.25 } : null;
          items.set(item.id, { item, object: null, display: null, spot, revealAt: null, collectedAt: null });
          handed = handed || fresh;
        }
      }
      if (world) itemsPrimed = true;
      if (handed && !player.moving) anim.playerOneShot = { action: 'receive', start: wall };
      for (const [id, record] of items) {
        if (seen.has(id)) continue;
        if (record.object && world) world.root.remove(record.object);
        if (record.display) record.display.parent?.remove(record.display);
        items.delete(id);
      }
      status.items = [...items.values()].map((r) => ({ id: r.item.id, state: r.item.state, lost: !!r.item.lost, placement: r.item.placement ?? null }));
    },
    setNotices(list) { notices = (list ?? []).slice(-5); },
    setHerald(report) {
      const linesIn = (report?.lines ?? []).map((l) => (typeof l === 'string' ? { text: l } : l)).filter((l) => l && l.text);
      herald = linesIn.length ? { ...report, lines: linesIn } : null;
      status.herald = herald;
    },
    setGrove(trees) { groveTrees = Array.isArray(trees) ? trees : []; if (world && city) { const before = layout.plots.size; layout = createLayout({ ...city, groveTrees: groveTrees.length }); if (layout.plots.size !== before) { loadCity(city, { key: cityKey, sanctuary, now: clock }); return; } world.setLayout(layout); applyAccumulation(); } },
    setResidents(list) { residents = Array.isArray(list) ? list.map((r) => ({ ...r })) : []; applyAccumulation(); },
    setBoard(list) { suggestions = Array.isArray(list) ? list.filter((s) => s && s.id && s.line) : []; applyAccumulation(); status.board = suggestions.map((s) => s.id); },
    setOracle(info) { oracle = info ?? null; applyAccumulation(); status.oracle = !!oracle?.exists; },
    setIslands(list) { islands = Array.isArray(list) ? list : []; status.islands = islands.map((i) => i.id); },
    toggleChart(open = !chartOpen) { chartOpen = !!open; status.chartOpen = chartOpen; if (chartOpen) { panel = null; card = null; } },
    get chartOpen() { return chartOpen; },
    /** 近景此刻看着谁（'talk' | 'resident' | null）：测试用。 */
    get closeUp() { return closeUp?.kind ?? null; },
    openPanel(type, id = null) { openPanel({ type, id }); },
    /** 测试与运动自检：就当你站在这一样跟前（树的名牌、脚下的"E …"提示都出现，同样按当帧矩阵投影）；null 取消。 */
    setHover(type, id) { forcedFocus = type ? { type, id: id ?? null } : null; },
    /** 你此刻够得着的那一样（测试用）。 */
    get focus() { return focus; },
    /** 城里能互动的东西（测试用）。 */
    interactables: () => interactables(world && athenaSim ? athenaPose(athenaSim.state(), clock) : null),
    /** 按一下交互键（测试与外部调用）。 */
    interact() { if (card || panel) { card = null; panel = null; return true; } return activate(focus); },
    /** 测试：把你放到 (x, z)，面朝 yaw。 */
    placePlayer(x, z, yaw = player.yaw) { player.pos = { x, z }; player.yaw = yaw; player.y = null; },
    openResident,
    startPlacing,
    setFog(info) { fogInfo = info; status.fog = !!info; if (R.scene) R.scene.fog = info ? new THREE.Fog(nightK > 0.5 ? 0x1b1e23 : 0xd8d4cd, 220, 360) : null; },
    /** 夜里 / 白天（应用的深 / 浅色主题）。instant：直接到位（打开页面时）；否则一秒多过渡过去。 */
    setNight(on, { instant = false } = {}) {
      nightTarget = on ? 1 : 0;
      if (instant) nightK = nightTarget;
      applyNight();
    },
    get night() { return nightK; },
    startVoyage(caption) {
      arrival = null;
      voyage = { startedAt: performance.now(), readyAt: null, cityArrived: false, arriveAt: null, caption, departZoom: camera.zoom, restoreZoom: camera.zoom, settled: false };
      status.voyage = 'sailing';
    },
    /** 新城到了：完整的（partial = false）就可以靠岸；只有顶层的，等到上限再靠。 */
    cityArrived({ partial = false, at = performance.now() } = {}) {
      if (!voyage) return;
      voyage.cityArrived = true;
      if (!partial && voyage.readyAt == null) voyage.readyAt = at;
    },
    get voyaging() { return voyage != null; },
    get voyageStartedAt() { return voyage?.startedAt ?? null; },
    get lastInputAt() { return lastInputAt; },
    get playerMoving() { return player.moving || pressed.size > 0; },
    get player() { return { ...player.pos, yaw: player.yaw }; },
    policy(visible) {
      return framePolicy({ visible, step: athenaSim?.state().step.kind ?? null, playerMoving: player.moving || pressed.size > 0, transitioning: voyage != null || reveal != null || glide != null || arrival != null || nightK !== nightTarget, now: performance.now(), lastInputAt });
    },
    resize() { R.resize(); overlay.resize(); },
    collectAll() { for (const id of items.keys()) { card = id; decide('accepted'); } },
    openCard(id) { if (items.has(id)) card = id; },
  };
}
