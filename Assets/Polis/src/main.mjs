// 雅典娜的城邦：页面入口。两种驱动——
//
//   实时（mode=live）：应用里的 NativeWebView 打开它（OfficePreviewHost 的 /polis/ 路由）。页面先自检，
//       通过就发 ready，之后由 C# 推城邦、成果、对话栏的状态（bridge.mjs），页面只发封闭集合里的意图。
//   夹具（默认）：普通浏览器里读夹具（一座城邦的快照）——演示、冒烟测试和运动自检用。
//       index.html?fixture=../fixtures/synthetic.json
//       &shot=1   固定一帧、不跑动画循环（截图用），渲染完 document.title = 'ready'
//       &selftest=motion   运动自检：镜头逐帧移动，比较屏幕空间效果与当帧真实投影，title = 'motion-max-error-px:…'
//       &ao=0 &shadows=0 &grade=0 &accent=0   调试开关
//       &demo=1   加上一组合成的积累数据（居民、橄榄林、委托板、神谕、海图、摆好的藏品）——演示、截图与冒烟用
//       &gallery=<资产 Id>&view=game|mid   美术审查：只画一件资产（gallery.mjs），渲染完 title = 'gallery-ready'
//
// 雅典娜不镜像任何回合（2026-10-13）：原来的"回放"（把一段归档会话按真实节奏演一遍）连同实时的工具事件一起拿掉了，
// 夹具里只剩城邦快照。她在城里过她自己的日子（athena.mjs）。
//
// 失败要明说（设计稿 12.5）：没有 WebGL 2、脚本出错，都在实时模式里回报 failed（C# 在游戏区显示原因和"回到对话"），
// 在夹具模式里显示页面自己的错误卡片。
//
// 操作：WASD / 方向键走路，走近人或物件按 E 互动，左上角三种视角（缺省自由），V 换视角，M 海图，Esc 收起；
// 实时模式底部是和雅典娜说话的对话栏（talk.mjs）——走到她跟前按 E、或者在城里按 Enter。
import * as THREE from '../vendor/three.bundle.mjs';
import { createGame, ATHENA_HOVER_ANCHOR } from './game.mjs';
import { createTalk } from './talk.mjs';
import { isTypingTarget } from './controls.mjs';
import { connectHost } from './bridge.mjs';
import { openReader } from './reader.mjs';
import { demoAccumulation, demoCitizens } from './demo.mjs';
import { createRelationsView } from './relations-view.mjs';
import { relationsView } from './relations.mjs';

const params = new URLSearchParams(location.search);
const live = params.get('mode') === 'live';
// 夜里（M3 第三轮：跟着应用的深色主题）：地址里带着打开时的主题，页面一加载就是那一套颜色，不先白一下
const nightAtStart = params.get('theme') === 'dark';
document.body.classList.toggle('night', nightAtStart);
const status = { ready: false, errors: [], phase: 'boot', mode: live ? 'live' : 'fixture' };
window.__polis = status;
const record = (msg) => status.errors.push(String(msg));
window.addEventListener('error', (e) => record(e.message));
window.addEventListener('unhandledrejection', (e) => record(e.reason?.stack ?? e.reason));

let host = null;
let failedReported = false;
function fail(reason, error) {
  const detail = String(error?.stack ?? error?.message ?? error ?? '').slice(0, 4000);
  record(detail || reason);
  document.title = 'error';
  if (live && host && !failedReported) {
    failedReported = true;
    host.send('failed', { reason, detail });
  }
  const box = document.getElementById('error');
  box.hidden = false;
  box.querySelector('.detail').textContent = `${reason}: ${error?.message ?? error ?? ''}`;
}

async function main() {
  status.phase = 'webgl';
  const probe = document.createElement('canvas').getContext('webgl2');
  if (!probe) throw new Error('这个环境没有 WebGL 2，城邦画不出来（设计稿 12.5：失败要明说，不静默降级）');
  probe.getExtension('WEBGL_lose_context')?.loseContext();

  if (params.get('gallery')) {
    status.phase = 'gallery';
    const { runGallery } = await import('./gallery.mjs');
    return runGallery({ params, status });
  }

  status.phase = 'build';
  const send = (type, fields) => {
    if (type === 'say') liveGame?.noteSaid();   // 托付了：你在城里比划两秒（M3）
    return host ? host.send(type, fields) : sentLocally(type, fields);
  };
  // 对话栏只在实时模式里有：夹具里没有人在听
  if (live) {
    // 问话（M-C）：对话栏借给一位居民时，问出去 / 不问了告诉城里（他站定、镜头留在你们两个身上、等太久了替他说"没想起来"）
    talk = createTalk(document.getElementById('talk'), {
      // 和城里的一个人说一句（chat）：带上他此刻在哪儿、在干什么（第四轮需求 4、5，进他的提示词）
      send: (type, fields = {}) => send(type, type === 'chat' && fields.who ? { ...fields, ...(liveGame?.stateOf(fields.who) ?? {}) } : fields),
      onOpen: () => liveGame?.releaseKeys(),
      onLent: (event, info) => liveGame?.lentEvent(event, info),
      // 你自己说了一句（2026-10-13：对话栏平时是你自己说话，附近的人会接）；有一段写好的话在雅典娜那里等你
      onSpeak: (parsed) => { liveGame?.speak(parsed); wake(); },
      onPrefill: () => { liveGame?.prefillWaiting(); wake(); },
    });
    window.__polisTalk = talk;
  }
  const game = createGame({
    params,
    status,
    send,
    // 走到雅典娜身边按 E：和她说话（托付）。平时的对话栏（Enter）是你自己说话
    onTalk: talk ? () => { talk.openAthena(); } : null,
    onUiBlocked: (blocked) => talk?.setSuppressed(blocked),
    onViewChange: () => syncView(game),
    modalOpen: () => readerOpen() || relationsOpen(),
    topInset: live ? 52 : 0,
    defaultView: 'player',   // 第四轮需求 7（2026-10-15）：缺省跟着你
    talkOpen: () => !!talk?.isOpen,
    talkMode: () => talk?.mode ?? 'speak',
    onChat: talk ? (target) => { talk.openChat(target); } : null,
    onChatReplied: talk ? (id) => { if (talk.lentTo?.kind === 'chat' && talk.lentTo.id === id) talk.resumeLent(); } : null,
    // 讲台前的辩论（M-E）：栏借给辩论（提议题、一轮轮的论点）；办完了还回来——只还给借它的那一种（问话不受辩论影响）
    onDebate: talk ? (options) => { talk.openDebate(options); } : null,
    onLendEnd: (kind) => { if (talk?.lentTo?.kind === kind) talk.endLent(); },
    onRead: ({ title, markdown }) => openReader(document, document.getElementById('reader'), { title, markdown, closeLabel: game.ui.close ?? '收起' }),
  });
  window.__polisGame = game;
  // 人际总览（第三轮，人物关系）：左上角（夹具模式在右下）的"人际"打开；开着时方向键不走路，Esc 收起
  relationsPanel = createRelationsView(document.getElementById('relations'), { onClose: () => { if (live) wake(); } });
  window.__polisRelations = relationsPanel;
  document.getElementById('btn-relations').addEventListener('click', () => { toggleRelations(game); if (live) wake(); });
  game.setNight(nightAtStart, { instant: true });
  window.addEventListener('resize', () => game.resize());
  wireView(game);

  if (live) return startLive(game);
  return startFixture(game);
}

function sentLocally(type, fields) {
  (window.__polisSent ??= []).push({ v: 1, type, ...fields });
  return false;
}

const readerOpen = () => !document.getElementById('reader').hidden;
let relationsPanel = null;
const relationsOpen = () => !!relationsPanel?.isOpen;

/** 人际总览的内容：这座城的每个人（街上的、在屋里的、职业居民）怎么看你、和谁合得来（relations.relationsView）。 */
function relationsContent(game) {
  return relationsView(game.relations, game.relationsPeople(), game.ui);
}

function toggleRelations(game) {
  if (relationsOpen()) relationsPanel.close();
  else relationsPanel.open(relationsContent(game), game.ui);
}

/** 关系或居民变了：总览开着就重画（选中的人留着）。 */
function refreshRelations(game) {
  if (relationsOpen()) relationsPanel.update(relationsContent(game), game.ui);
}

function closeReader() {
  const reader = document.getElementById('reader');
  reader.hidden = true;
  reader.replaceChildren();
}

// —— 视角：左上角（夹具模式在右下）一组三选一的按钮，缺省"跟着你"（第四轮需求 7）；拖动平移会把镜头交还给你（自由），按钮跟着变 ——
function wireView(game) {
  for (const button of document.querySelectorAll('#view button[data-view]')) {
    button.addEventListener('click', () => { game.setFollow(button.dataset.view); syncView(game); wake(); });
  }
  syncView(game);
}

function syncView(game) {
  const ui = game.ui;
  const labels = { free: ui.viewFree, player: ui.viewPlayer, athena: ui.viewAthena };
  const view = document.getElementById('view');
  view.setAttribute('aria-label', ui.viewLabel);
  view.querySelector('.view-label').textContent = ui.viewLabel;
  for (const button of view.querySelectorAll('button[data-view]')) {
    button.textContent = labels[button.dataset.view] ?? button.dataset.view;
    button.setAttribute('aria-checked', String(button.dataset.view === game.follow));
    if (button.dataset.view === 'free') button.title = ui.viewFreeTip;
  }
}

/** Esc：先收起最上面的那一层——阅读器、海图、摆放、卡片或面板。 */
function dismissTopmost(game) {
  if (relationsOpen()) relationsPanel.close();
  else if (readerOpen()) closeReader();
  else if (game.chartOpen) game.toggleChart(false);
  else game.dismiss();
}

// ———————————————————————— 实时模式 ————————————————————————

let liveGame = null;
let talk = null;
const epoch = performance.now();
const clock = () => performance.now() - epoch;
let visible = true;
let loopRunning = false;
let lastRender = -Infinity;
let lastWall = performance.now();
const backlog = [];   // ready 之前到的消息（C# 不会这么做，但不能丢）

function liveHandlers() {
  const whenReady = (fn) => (m) => (liveGame ? fn(m) : backlog.push(() => fn(m)));
  return {
    init: whenReady((m) => {
      liveGame.setTables(m.tables ?? {});
      talk?.setTables(liveGame.ui);
      syncView(liveGame);
      syncChartButton();
      // 资产清单：C# 校验过的覆盖；外部模型取到之前先用程序化的，取不到就一直用程序化的（缺省回退）
      if (m.assets && Object.keys(m.assets).length > 0) liveGame.setAssetManifest(m.assets).then(wake, (e) => record(e?.stack ?? e));
    }),
    city: whenReady((m) => {
      const isNew = !liveGame.city || m.key !== status.city?.key;
      if (isNew) liveGame.loadCity(m.city, { key: m.key, sanctuary: !!m.sanctuary, player: m.player, founding: !!m.founding, now: clock() });
      else status.lastUpdate = liveGame.updateCity(m.city, m.origins ?? {});
      liveGame.cityArrived({ partial: !!m.partial });
      wake();
    }),
    depart: whenReady((m) => { liveGame.startVoyage(m.caption ?? ''); wake(); }),
    arrive: whenReady(() => { liveGame.cityArrived({ partial: false }); wake(); }),
    items: whenReady((m) => { liveGame.setItems(m.items); wake(); }),
    grove: whenReady((m) => { liveGame.setGrove(m.trees); wake(); }),
    residents: whenReady((m) => { liveGame.setResidents(m.residents); wake(); }),
    // 城里的人（第四轮起从建筑来）：{ street（≤ 32 位）, indoors, census }
    citizens: whenReady((m) => { liveGame.setCitizens(m.citizens ?? null); refreshRelations(liveGame); wake(); }),
    // 人物关系（第三轮需求 1）：{ you, pairs, ties }——谁怎么看你、两人之间、朋友与不和的人
    relations: whenReady((m) => { liveGame.setRelations(m.relations ?? null); refreshRelations(liveGame); wake(); }),
    // 问话的回答（M-C）：{ conversationId, status, text }——放进他的卡片，对话栏还给雅典娜
    // 先还对话栏、再放回答：栏还开着时，城里会以为你又在和雅典娜说话，不把卡片打开
    'talk': whenReady((m) => { liveGame.setTalk(m); wake(); }),
    'ambient-end': whenReady((m) => { liveGame.endAmbient(m); wake(); }),
    // 你自己说话之后附近几个人依次接话，说完了（第三轮需求 6）：{ who, spoke, status }
    'chorus-end': whenReady((m) => { liveGame.endChorus(m); wake(); }),
    // 敲门（第三轮需求 3）：C# 的回音——谁敲得太勤、谁打发过你不应门；请他出来，他出不出来
    'knock-reply': whenReady((m) => { liveGame.setKnockReply(m); wake(); }),
    'call-out-reply': whenReady((m) => { liveGame.setCallOutReply(m); wake(); }),
    // 讲台前的辩论（M-E）：{ state, id, motion, round, rounds, jury, transcript, verdict, record, notice }
    debate: whenReady((m) => { liveGame.setDebate(m.debate ?? null); wake(); }),
    // 围观的人起哄（2026-10-13）：{ id, round, reactions: [{ kind, text }] }——放到围观者的头顶
    'debate-crowd': whenReady((m) => { liveGame.setDebateCrowd(m); wake(); }),
    board: whenReady((m) => { liveGame.setBoard(m.suggestions); wake(); }),
    oracle: whenReady((m) => { liveGame.setOracle(m.oracle ?? null); wake(); }),
    islands: whenReady((m) => { liveGame.setIslands(m.islands); wake(); }),
    notices: whenReady((m) => { liveGame.setNotices(m.notices); wake(); }),
    report: whenReady((m) => { liveGame.setHerald(m); wake(); }),
    fog: whenReady((m) => { liveGame.setFog(m.missing ? { path: m.path ?? null } : null); wake(); }),
    // 对话栏的状态（这份委托在不在办事、等不等审批、能不能托付、草稿、附件）；带 open 时展开并聚焦（预填之后）
    composer: whenReady((m) => { talk?.apply(m); wake(); }),
    visibility: (m) => { visible = !!m.visible; status.visible = visible; wake(); },
    // 应用的主题换了（深 / 浅色）：页面上的条、阅读器立刻换（CSS 自己过渡），城里一秒多从白天走到夜里
    theme: (m) => {
      const night = !!m.night;
      document.body.classList.toggle('night', night);
      status.theme = night ? 'dark' : 'light';
      if (liveGame) { liveGame.setNight(night, { instant: !!m.instant }); wake(); }
      else backlog.push(() => liveGame.setNight(night, { instant: true }));
    },
    scroll: whenReady((m) => {
      openReader(document, document.getElementById('reader'), { title: m.title ?? '', markdown: m.markdown ?? '', closeLabel: m.closeLabel ?? '收起' });
    }),
    onUnknown: (m) => record(`unknown host message: ${m?.type}`),
    onError: (e) => record(e?.stack ?? e),
  };
}

function syncChartButton(game = liveGame) {
  if (!game) return;
  document.getElementById('btn-chart').textContent = game.ui.chart;
  document.getElementById('btn-relations').textContent = game.ui.relationsButton;
}

function startLive(game) {
  liveGame = game;
  document.body.classList.add('live');
  talk?.setTables(game.ui);
  document.getElementById('btn-chart').addEventListener('click', () => { game.toggleChart(); wake(); });
  // 按键：落在对话栏里的是字（talk.mjs 自己处理 Enter / Esc）；在城里，Enter 开口、Esc 收起、M 海图、V 换视角。
  // WASD / 方向键走路在 game.mjs 里。按物理键位认（e.code），开着中文输入法也一样。
  window.addEventListener('keydown', (e) => {
    if (isTypingTarget(e.target) || e.isComposing) return;
    if (e.key === 'Enter' && !e.shiftKey && !e.metaKey && !e.ctrlKey && !e.altKey) {
      if (talk?.available && !readerOpen() && !relationsOpen() && !game.chartOpen) { e.preventDefault(); talk.open({ focus: true }); wake(); }
      return;
    }
    if (e.key === 'Escape') { dismissTopmost(game); wake(); return; }
    if (e.metaKey || e.ctrlKey || e.altKey) return;
    if (e.code === 'KeyM') { game.toggleChart(); wake(); }
    else if (e.code === 'KeyV') { game.cycleView(); wake(); }
  });
  window.addEventListener('focus', () => updateHint());
  window.addEventListener('blur', () => updateHint());
  document.addEventListener('visibilitychange', wake);
  for (const run of backlog.splice(0)) run();
  game.frame(clock(), 16, performance.now(), { snap: true });
  status.phase = 'ready';
  status.ready = true;
  document.title = 'ready';
  host.send('ready');
  wake();
}

/**
 * 操作提示：对话栏正上方一行小字，走过第一步就不再出现。页面没有键盘焦点时（刚进游戏、点过左边的会话树）
 * 先提示"点一下画面"——原生 WebView 拿到焦点之前，按键到不了这里。
 */
let walkedOnce = false;
function updateHint() {
  const el = document.getElementById('hint');
  if (!liveGame) return;
  if (liveGame.playerMoving) walkedOnce = true;
  const ui = liveGame.ui;
  const show = !walkedOnce && !!liveGame.world && !!talk?.available && !talk.isOpen && !readerOpen() && !relationsOpen();
  const text = show ? (document.hasFocus() ? ui.controlsHint : ui.focusHint) : '';
  if (el.textContent !== text) el.textContent = text;
  if (el.hidden !== !show) el.hidden = !show;
}

/** 按帧率策略推进：不可见就停（不再请求帧），空闲 30 帧，有东西在动 60 帧（设计稿 12.6）。 */
function wake() {
  if (!liveGame || loopRunning) return;
  loopRunning = true;
  requestAnimationFrame(liveLoop);
}

function liveLoop(wall) {
  const fps = liveGame.policy(visible && !document.hidden);
  status.fps = fps;
  if (fps === 0) {
    loopRunning = false;   // 停下：可见性变了、或有新消息时 wake() 再起
    status.paused = true;
    return;
  }
  status.paused = false;
  if (wall - lastRender >= 1000 / fps - 2) {
    const dt = Math.min(100, wall - lastWall);
    lastWall = wall;
    lastRender = wall;
    try {
      liveGame.frame(clock(), dt, wall);
      updateHint();
    } catch (e) {
      loopRunning = false;
      fail('runtime', e);
      return;
    }
  }
  requestAnimationFrame(liveLoop);
}

// ———————————————————————— 夹具模式 ————————————————————————

async function startFixture(game) {
  status.phase = 'fixture';
  const fixtureUrl = params.get('fixture') ?? '../fixtures/synthetic.json';
  const res = await fetch(fixtureUrl);
  if (!res.ok) throw new Error(`读不到夹具 ${fixtureUrl}：HTTP ${res.status}`);
  const fixture = await res.json();
  if (fixture.kind !== 'athena-polis-fixture' || fixture.schema !== 1) throw new Error('这不是城邦夹具（schema 1）');
  status.fixture = { synthetic: fixture.synthetic, buildings: fixture.buildings.length };

  status.phase = 'build';
  if (params.get('demo') === '1') {
    // 合成的积累数据：居民、橄榄林、委托板、神谕、海图与摆好的藏品（演示、截图、冒烟——不是任何人的真实数据）
    const demo = demoAccumulation(fixture);
    game.setGrove(demo.trees);
    game.setResidents(demo.residents);
    game.setBoard(demo.suggestions);
    game.setOracle(demo.oracle);
    game.setIslands(demo.islands);
    game.setItems(demo.items);
  }
  game.loadCity(fixture, { key: 'fixture', now: clock() });
  // 居民即会话（M-B）：&demo=1 时一城 24 位编出来的居民；&citizens=N 换成 N 位（帧率门槛按 32 位量）
  const wantCitizens = params.get('citizens') != null ? Number(params.get('citizens')) : params.get('demo') === '1' ? 24 : 0;
  if (wantCitizens > 0) game.setCitizens(demoCitizens(fixture, wantCitizens));
  syncChartButton(game);
  document.getElementById('btn-chart').addEventListener('click', () => game.toggleChart());

  let hudVisible = params.get('hud') !== '0';
  const hud = () => (hudVisible
    ? [`演示城邦（${fixture.workspaceName ?? ''}）· 视角：${{ athena: '跟着雅典娜', player: '跟着你', free: '自由' }[game.follow]}（V 换）`,
      'WASD / 方向键走动（Shift 跑）· 拖动转视角 · 走近人或物件按 E · M 海图 · H 隐藏这一行']
    : null);
  window.addEventListener('keydown', (e) => {
    if (isTypingTarget(e.target) || e.isComposing || e.metaKey || e.ctrlKey || e.altKey) return;
    if (e.code === 'KeyV') game.cycleView();
    else if (e.code === 'KeyH') hudVisible = !hudVisible;
    else if (e.code === 'KeyM') game.toggleChart();
    else if (e.key === 'Escape') dismissTopmost(game);
    syncView(game);
  });

  status.phase = 'render';
  status.pixelStats = () => pixelStats(game.R.renderer.domElement);
  status.collectAll = () => game.collectAll();

  if (params.get('selftest') === 'motion') {
    const result = motionSelftest(game);
    status.selftest = result;
    status.ready = true;
    document.title = `motion-max-error-px:${result.maxErrorPx.toFixed(3)}`;
    return;
  }

  if (params.get('shot') === '1') {
    for (let i = 0; i < 3; i++) game.frame(clock(), 16, performance.now(), { snap: true, hud: hud() });
    status.ready = true;
    document.title = 'ready';
    return;
  }

  let last = performance.now();
  game.frame(clock(), 16, last, { snap: true, hud: hud() });
  status.ready = true;
  document.title = 'ready';
  const loop = (wall) => {
    const dt = Math.min(100, wall - last);
    last = wall;
    try {
      game.frame(clock(), dt, wall, { hud: hud() });
    } catch (e) {
      record(e?.stack ?? e);
      throw e;
    }
    requestAnimationFrame(loop);
  };
  requestAnimationFrame(loop);
}

// —— 运动自检（设计稿 12.7）：镜头按脚本逐帧移动，比较每个屏幕空间效果这一帧用的位置与当帧的真实投影 ——
// 真值在渲染之后算：渲染器这时已经把相机和人物的矩阵更新成了这一帧真正用的那一组。
// 两个时间窗是请雅典娜自己造出来的（2026-10-13：原来取自回放的"走"和"瞬移"）：走——从广场一圈的一个点走到对面那个点；
// 瞬移——请她去港口（远过 10 秒的路，化作一道光过去）。之后在自由视角里按住 W + D 走一段。
function motionSelftest(game) {
  const sync = params.get('nosync') !== '1';   // nosync=1：故意不对齐矩阵，看修复前的偏差有多大
  const errors = { glow: 0, bubble: 0, labels: 0 };
  const kinds = {};   // 名牌按种类计数：建筑、公共建筑、居民（M2）、遗失藏品的名牌（M2）……
  let frames = 0;
  const camera = game.camera;
  const controls = game.controls;
  const size = game.R.size, dpr = game.overlay.dpr;
  let i = 0;
  // M2：站在一棵橄榄树跟前（有积累数据时），树的名牌和脚下的"E 去这份委托"也进自检；隔一帧换成站在一位居民跟前——
  // 他在走，脚下那块"E 和他说话"跟着他走，按他这一帧的位置投影（M3 第三轮：互动改成走近按 E）
  const tree = game.world?.groveTrees?.[0];
  const walker = [...(game.world?.residents?.keys() ?? [])][0] ?? null;
  // 2026-10-13 起她没有瞬移（远了就跑）：去港口那一段现在是跑，同样逐帧核对
  const teleporting = () => false;

  function measure(t) {
    if (tree && i % 2 === 0) game.setHover('tree', tree.conversationId);
    else if (walker) game.setHover('resident', walker);
    else game.setHover(null);
    const script = () => {
      camera.zoom = 1 + 0.35 * Math.sin(i / 7);
      camera.updateProjectionMatrix();
      controls.target.add(new THREE.Vector3(Math.sin(i / 5) * 2.5, 0, Math.cos(i / 6) * 2.5));
      game.placeCamera(controls.target, 45 + i * 1.3, 33 + 6 * Math.sin(i / 9));
    };
    game.frame(t, 40, performance.now(), { snap: true, sync, script });
    const used = game.used;
    const world = game.world;
    const tele = teleporting();
    const truthGlow = used.glowWorld.clone();
    if (!tele) truthGlow.copy(world.athena.userData.chest).applyMatrix4(world.athena.matrixWorld);
    const tg = truthGlow.project(camera);
    if (frames > 0) {
      errors.glow = Math.max(errors.glow, Math.hypot(((tg.x + 1) / 2 - used.glowUv.x) * size.x, ((tg.y + 1) / 2 - used.glowUv.y) * size.y));
      const head = tele ? used.headWorld.clone() : world.athena.userData.headTop.clone().applyMatrix4(world.athena.matrixWorld);
      const hb = head.project(camera);
      errors.bubble = Math.max(errors.bubble, Math.hypot(((hb.x + 1) / 2) * window.innerWidth - used.bubbleCss.x, (1 - (hb.y + 1) / 2) * window.innerHeight - used.bubbleCss.y) * dpr);
      for (const l of used.labels) {
        kinds[l.kind] = (kinds[l.kind] ?? 0) + 1;
        const truth = l.key?.startsWith('prompt:athena') && !tele
          ? ATHENA_HOVER_ANCHOR.clone().applyMatrix4(world.athena.matrixWorld)
          : l.key?.startsWith('prompt:resident:')
            ? (() => { const r = world.residents.get(l.key.split(':')[2]); return new THREE.Vector3(r.figure.root.position.x, 0.02, r.figure.root.position.z); })()
            : l.world.clone();
        const lp = truth.project(camera);
        errors.labels = Math.max(errors.labels, Math.hypot(((lp.x + 1) / 2) * window.innerWidth - l.x, (1 - (lp.y + 1) / 2) * window.innerHeight - l.y) * dpr);
      }
      // 居民头顶的话（M-B）：按他这一帧的矩阵投影；挂在门上的话（第三轮需求 3：咚、咚，门里的人应声）按门上那一点
      for (const b of used.citizenBubbles ?? []) {
        if (b.anchor && b.alpha > 0) {
          kinds.doorBubble = (kinds.doorBubble ?? 0) + 1;
          const dp = b.anchor.clone().project(camera);
          errors.bubble = Math.max(errors.bubble, Math.hypot(((dp.x + 1) / 2) * window.innerWidth - b.x, (1 - (dp.y + 1) / 2) * window.innerHeight - b.y) * dpr);
          continue;
        }
        const c = world.citizens.get(b.id);
        if (!c?.figure || !(b.alpha > 0)) continue;
        kinds.citizenBubble = (kinds.citizenBubble ?? 0) + 1;
        const hp = c.figure.root.userData.headTop.clone().applyMatrix4(c.figure.root.matrixWorld).project(camera);
        errors.bubble = Math.max(errors.bubble, Math.hypot(((hp.x + 1) / 2) * window.innerWidth - b.x, (1 - (hp.y + 1) / 2) * window.innerHeight - b.y) * dpr);
      }
    }
    frames++;
    i++;
  }

  /** 请她去 site（时刻 t），返回这一步的时间窗：她开始动之前 200 毫秒到她停下之后 200 毫秒。 */
  function windowFor(site, t) {
    game.athenaGoTo(site, t);
    game.frame(t, 16, performance.now(), { snap: true });
    const step = game.athenaSim.state().step;
    return { kind: step.kind, span: [step.start - 200, (step.end ?? step.start) + 200] };
  }

  game.follow = 'athena';
  game.frame(0, 16, performance.now(), { snap: true });
  const walk = windowFor('agora:3', 1000);
  for (let t = walk.span[0]; t <= walk.span[1]; t += 40) measure(t);
  const tele = windowFor('harbor', walk.span[1] + 400);
  for (let t = tele.span[0]; t <= tele.span[1]; t += 40) measure(t);
  const windows = [walk.span, tele.span];
  const kindsOfWindows = [walk.kind, tele.kind];

  // 自由视角里走路（2026-10-11）：按住 W + D，镜头不跟任何人，只在你快走出画面时挪一下（keepPlayerInView）——
  // 它和脚本的绕转、推拉、平移一起挪镜头，屏幕空间效果照样要和当帧投影对齐；而且你一直在画面里
  const walkFrom = tele.span[1] + 400;
  game.follow = 'free';
  const start = game.player;
  // 居民（M-B）：离你最近、看得见的那位向你打招呼，他头顶的那句话在走路这一段逐帧核对
  const greeter = [...(game.world?.citizens?.values() ?? [])]
    .filter((c) => c.figure?.root.visible)
    .sort((a, b) => Math.hypot(a.figure.root.position.x - start.x, a.figure.root.position.z - start.z) - Math.hypot(b.figure.root.position.x - start.x, b.figure.root.position.z - start.z))[0];
  if (greeter) game.greetCitizen(greeter.id);
  // 门（第三轮需求 3）：离你最近的那扇门敲一下，门上的"咚、咚"与门里的应声在走路这一段逐帧核对
  const nearestDoor = [...(game.world?.doors?.values() ?? [])]
    .sort((a, b) => Math.hypot(a.x - start.x, a.z - start.z) - Math.hypot(b.x - start.x, b.z - start.z))[0];
  if (nearestDoor) game.knock(nearestDoor.id);
  const key = (type, code) => window.dispatchEvent(new KeyboardEvent(type, { code, key: code.slice(3).toLowerCase() }));
  key('keydown', 'KeyW');
  key('keydown', 'KeyD');
  let playerNdc = 0;
  const walkFrames = 90;
  for (let k = 0; k < walkFrames; k++) {
    measure(walkFrom + k * 40);
    const p = game.player;
    const ndc = new THREE.Vector3(p.x, 0.9, p.z).project(camera);
    playerNdc = Math.max(playerNdc, Math.abs(ndc.x), Math.abs(ndc.y));
  }
  key('keyup', 'KeyW');
  key('keyup', 'KeyD');
  const end = game.player;
  const walking = { frames: walkFrames, metres: +Math.hypot(end.x - start.x, end.z - start.z).toFixed(2), maxPlayerNdc: +playerNdc.toFixed(3) };

  const maxErrorPx = Math.max(errors.glow, errors.bubble, errors.labels);
  return { maxErrorPx, errors, frames, windows, windowKinds: kindsOfWindows, sync, kinds, walk: walking };
}

function pixelStats(canvas) {
  const w = 96, h = 60;
  const c = document.createElement('canvas');
  c.width = w; c.height = h;
  const ctx = c.getContext('2d', { willReadFrequently: true });
  ctx.drawImage(canvas, 0, 0, w, h);
  const data = ctx.getImageData(0, 0, w, h).data;
  let sum = 0, sum2 = 0;
  const distinct = new Set();
  for (let i = 0; i < data.length; i += 4) {
    const l = 0.2126 * data[i] + 0.7152 * data[i + 1] + 0.0722 * data[i + 2];
    sum += l; sum2 += l * l;
    distinct.add(`${data[i] >> 3},${data[i + 1] >> 3},${data[i + 2] >> 3}`);
  }
  const n = data.length / 4;
  const mean = sum / n;
  return { mean, std: Math.sqrt(Math.max(0, sum2 / n - mean * mean)), distinct: distinct.size };
}

// 实时模式也要能被测试读到画面统计
status.pixelStats = () => pixelStats(document.getElementById('scene'));

// 启动放在模块末尾：实时模式的 main() 一路同步走到 startLive，用到的模块级状态必须已经初始化。
if (live) host = connectHost(liveHandlers());
main().catch((e) => fail(status.phase === 'webgl' ? 'webgl' : 'script', e));
