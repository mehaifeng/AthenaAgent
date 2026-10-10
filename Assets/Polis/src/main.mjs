// 雅典娜的城邦：页面入口。两种驱动——
//
//   实时（mode=live）：应用里的 NativeWebView 打开它（OfficePreviewHost 的 /polis/ 路由）。页面先自检，
//       通过就发 ready，之后由 C# 推城邦、事件、成果（bridge.mjs），页面只发封闭集合里的意图。
//   回放（默认）：普通浏览器里读夹具（城邦快照 + 回放事件），按真实节奏回放——演示、冒烟测试和运动自检用。
//       index.html?fixture=../fixtures/synthetic.json   &speed=2  &t=30000  &paused=1
//       &shot=1   固定一帧、不跑动画循环（截图用），渲染完 document.title = 'ready'
//       &selftest=motion   运动自检：镜头逐帧移动，比较屏幕空间效果与当帧真实投影，title = 'motion-max-error-px:…'
//       &ao=0 &shadows=0 &grade=0 &accent=0   调试开关
//
// 失败要明说（设计稿 12.5）：没有 WebGL 2、脚本出错，都在实时模式里回报 failed（C# 在游戏区显示原因和"回到对话"），
// 在回放模式里显示页面自己的错误卡片。
import * as THREE from '../vendor/three.bundle.mjs';
import { createGame } from './game.mjs';
import { createReplay } from './replay.mjs';
import { createLiveFeed } from './live.mjs';
import { connectHost } from './bridge.mjs';
import { openReader } from './reader.mjs';
import { cityIndex } from './replay.mjs';

const params = new URLSearchParams(location.search);
const live = params.get('mode') === 'live';
const status = { ready: false, errors: [], phase: 'boot', mode: live ? 'live' : 'replay' };
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

  status.phase = 'build';
  const game = createGame({
    params,
    status,
    send: (type, fields) => (host ? host.send(type, fields) : sentLocally(type, fields)),
    onReadItem: (item) => openReader(document, document.getElementById('reader'), { title: item.title, markdown: item.markdown ?? item.title }),
  });
  window.__polisGame = game;
  window.addEventListener('resize', () => game.resize());

  if (live) return startLive(game);
  return startReplay(game);
}

function sentLocally(type, fields) {
  (window.__polisSent ??= []).push({ v: 1, type, ...fields });
  return false;
}

// ———————————————————————— 实时模式 ————————————————————————

let liveGame = null;
let liveFeed = createLiveFeed();
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
    init: whenReady((m) => { liveGame.setTables(m.tables ?? {}); syncFollowButton(); }),
    city: whenReady((m) => {
      const isNew = !liveGame.city || m.key !== status.city?.key;
      if (isNew) {
        liveGame.loadCity(m.city, { key: m.key, sanctuary: !!m.sanctuary, player: m.player, founding: !!m.founding, now: clock() });
        liveFeed = createLiveFeed({ city: cityIndex(m.city) });
      } else {
        status.lastUpdate = liveGame.updateCity(m.city, m.origins ?? {});
        liveFeed.setCity(cityIndex(m.city));
      }
      liveGame.cityArrived({ partial: !!m.partial });
      wake();
    }),
    depart: whenReady((m) => { liveGame.startVoyage(m.caption ?? ''); wake(); }),
    arrive: whenReady(() => { liveGame.cityArrived({ partial: false }); wake(); }),
    focus: whenReady((m) => {
      liveFeed = createLiveFeed({ city: cityIndex(liveGame.city ?? {}) });
      liveGame.focus({ last: m.last ?? null, interrupted: !!m.interrupted, now: clock() });
      wake();
    }),
    events: whenReady((m) => {
      const now = clock();
      for (const e of m.events ?? []) if (e.type === 'turn') liveGame.noteTurn(Math.max(0, now - (e.ageMs ?? 0)), e.turn ?? 0);
      status.liveEvents = (status.liveEvents ?? 0) + liveFeed.push(m.events, now);
      wake();
    }),
    items: whenReady((m) => { liveGame.setItems(m.items); wake(); }),
    notices: whenReady((m) => { liveGame.setNotices(m.notices); wake(); }),
    report: whenReady((m) => { liveGame.setHerald(m); wake(); }),
    fog: whenReady((m) => { liveGame.setFog(m.missing ? { path: m.path ?? null } : null); wake(); }),
    visibility: (m) => { visible = !!m.visible; status.visible = visible; wake(); },
    scroll: whenReady((m) => {
      openReader(document, document.getElementById('reader'), { title: m.title ?? '', markdown: m.markdown ?? '', closeLabel: m.closeLabel ?? '收起' });
    }),
    onUnknown: (m) => record(`unknown host message: ${m?.type}`),
    onError: (e) => record(e?.stack ?? e),
  };
}

function syncFollowButton() {
  if (!liveGame) return;
  const ui = liveGame.ui;
  document.getElementById('btn-follow').textContent =
    liveGame.follow === 'player' ? ui.followPlayer : liveGame.follow === 'athena' ? ui.followAthena : ui.followFree;
}

function startLive(game) {
  liveGame = game;
  document.body.classList.add('live');
  const follow = document.getElementById('btn-follow');
  follow.addEventListener('click', () => { game.toggleFollow(); syncFollowButton(); wake(); });
  document.addEventListener('visibilitychange', wake);
  for (const run of backlog.splice(0)) run();
  game.frame(clock(), 16, performance.now(), { snap: true });
  status.phase = 'ready';
  status.ready = true;
  document.title = 'ready';
  host.send('ready');
  wake();
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
      liveGame.frame(clock(), dt, wall, { feed: (orch) => liveFeed.drain(orch) });
    } catch (e) {
      loopRunning = false;
      fail('runtime', e);
      return;
    }
  }
  requestAnimationFrame(liveLoop);
}

// ———————————————————————— 回放模式 ————————————————————————

async function startReplay(game) {
  status.phase = 'fixture';
  const fixtureUrl = params.get('fixture') ?? '../fixtures/synthetic.json';
  const res = await fetch(fixtureUrl);
  if (!res.ok) throw new Error(`读不到夹具 ${fixtureUrl}：HTTP ${res.status}`);
  const fixture = await res.json();
  if (fixture.kind !== 'athena-polis-fixture' || fixture.schema !== 1) throw new Error('这不是城邦夹具（schema 1）');
  status.fixture = { synthetic: fixture.synthetic, buildings: fixture.buildings.length, toolCalls: fixture.replay?.toolCalls ?? 0, durationMs: fixture.replay?.durationMs ?? 0 };

  status.phase = 'build';
  game.loadCity(fixture, { key: 'fixture' });
  let replay = createReplay(fixture);
  // 回放的时钟喂事件；turn 事件顺带记下开场白
  const feedReplay = (orch, now) => replay.feed({ ingest: (e) => { if (e.type === 'turn') game.noteTurn(e.t, e.turn); orch.ingest(e); } }, now);
  const restart = () => { game.resetTimeline(); replay = createReplay(fixture); };

  // —— 跳到某一刻：把回放和编排器从头推进过去（编排器是纯逻辑，推进很快） ——
  function seek(t) {
    restart();
    for (let x = 0; x < t; x += 50) {
      feedReplay(game.sim.orch, x);
      game.sim.orch.update(x);
    }
    game.sim.time = t;
  }

  let playing = params.get('paused') !== '1';
  let speed = Number(params.get('speed') ?? 1) || 1;
  let hudVisible = params.get('hud') !== '0';
  const clock2 = (ms) => {
    const s = Math.max(0, Math.floor(ms / 1000));
    return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`;
  };
  const hud = () => {
    if (!hudVisible) return null;
    const { turn, delivered } = replay.turnAt(game.sim.time);
    const follow = game.follow;
    return [
      `回放 ${clock2(game.sim.time)} / ${clock2(replay.duration)} · 第 ${turn} 回合 / 共 ${fixture.replay?.turns ?? 0}${delivered ? '（已交付）' : ''} · ×${speed}${playing ? '' : ' · 已暂停'}`,
      `镜头：${follow === 'athena' ? '跟着雅典娜' : follow === 'player' ? '跟着你' : '自由'} · 点地面让你走过去 · 红色的卷轴是交给你的成果，点它看看`,
    ];
  };

  window.addEventListener('keydown', (e) => {
    if (e.code === 'Space') { playing = !playing; e.preventDefault(); }
    else if (e.key === 'f' || e.key === 'F') game.toggleFollow();
    else if (e.key === '1' || e.key === '2' || e.key === '4') speed = Number(e.key);
    else if (e.key === 'r' || e.key === 'R') restart();
    else if (e.key === 'h' || e.key === 'H') hudVisible = !hudVisible;
    syncButtons();
  });
  const buttons = {
    play: document.getElementById('btn-play'),
    speed: document.getElementById('btn-speed'),
    follow: document.getElementById('btn-follow'),
    restart: document.getElementById('btn-restart'),
  };
  function syncButtons() {
    buttons.play.textContent = playing ? '暂停' : '继续';
    buttons.speed.textContent = `速度 ×${speed}`;
    buttons.follow.textContent = game.follow === 'player' ? '镜头：跟着你' : game.follow === 'athena' ? '镜头：跟着雅典娜' : '镜头：自由';
  }
  buttons.play.addEventListener('click', () => { playing = !playing; syncButtons(); });
  buttons.speed.addEventListener('click', () => { speed = speed === 1 ? 2 : speed === 2 ? 4 : 1; syncButtons(); });
  buttons.follow.addEventListener('click', () => { game.toggleFollow(); syncButtons(); });
  buttons.restart.addEventListener('click', () => { restart(); syncButtons(); });
  syncButtons();

  status.phase = 'render';
  const startAt = Number(params.get('t') ?? 0) || 0;
  if (startAt > 0) seek(startAt);

  status.seek = (t) => { seek(t); game.frame(game.sim.time, 16, performance.now(), { snap: true, hud: hud() }); };
  status.pixelStats = () => pixelStats(game.R.renderer.domElement);
  status.steps = () => game.sim.orch.steps.map(({ kind, start, end, at, to, from }) => ({ kind, start, end, at, to, from }));
  status.collectAll = () => game.collectAll();

  if (params.get('selftest') === 'motion') {
    const result = motionSelftest(game, seek, replay.duration);
    status.selftest = result;
    status.ready = true;
    document.title = `motion-max-error-px:${result.maxErrorPx.toFixed(3)}`;
    return;
  }

  if (params.get('shot') === '1') {
    for (let i = 0; i < 3; i++) game.frame(game.sim.time, 16, performance.now(), { snap: true, feed: feedReplay, hud: hud() });
    status.ready = true;
    document.title = 'ready';
    return;
  }

  let last = performance.now();
  game.frame(game.sim.time, 16, last, { snap: true, feed: feedReplay, hud: hud() });
  status.ready = true;
  document.title = 'ready';
  const loop = (wall) => {
    const dt = Math.min(100, wall - last);
    last = wall;
    const now = playing ? game.sim.time + dt * speed : game.sim.time;
    try {
      game.frame(now, dt, wall, { feed: feedReplay, hud: hud() });
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
function motionSelftest(game, seek, duration) {
  const sync = params.get('nosync') !== '1';   // nosync=1：故意不对齐矩阵，看修复前的偏差有多大
  seek(duration + 2000);
  const steps = game.sim.orch.steps;
  const walk = steps.find((s) => s.kind === 'walk' && s.end - s.start > 800);
  const tele = steps.find((s) => s.kind === 'teleport');
  const windows = [walk, tele].filter(Boolean).map((s) => [s.start - 200, s.end + 200]);
  if (windows.length === 0) windows.push([0, 3000]);
  const errors = { glow: 0, bubble: 0, labels: 0 };
  let frames = 0;
  const camera = game.camera;
  const controls = game.controls;
  const size = game.R.size, dpr = game.overlay.dpr;
  let i = 0;
  for (const [from, to] of windows) {
    seek(Math.max(0, from));
    game.follow = 'athena';
    for (let t = Math.max(0, from); t <= to; t += 40, i++) {
      const script = () => {
        camera.zoom = 1 + 0.35 * Math.sin(i / 7);
        camera.updateProjectionMatrix();
        controls.target.add(new THREE.Vector3(Math.sin(i / 5) * 2.5, 0, Math.cos(i / 6) * 2.5));
        game.placeCamera(controls.target, 45 + i * 1.3, 33 + 6 * Math.sin(i / 9));
      };
      game.frame(t, 40, performance.now(), { snap: true, sync, script });
      const used = game.used;
      const world = game.world;
      const teleporting = game.sim.orch.state(t).step?.kind === 'teleport';
      const truthGlow = used.glowWorld.clone();
      if (!teleporting) truthGlow.copy(world.athena.userData.chest).applyMatrix4(world.athena.matrixWorld);
      const tg = truthGlow.project(camera);
      if (frames > 0) {
        errors.glow = Math.max(errors.glow, Math.hypot(((tg.x + 1) / 2 - used.glowUv.x) * size.x, ((tg.y + 1) / 2 - used.glowUv.y) * size.y));
        const head = teleporting ? used.headWorld.clone() : world.athena.userData.headTop.clone().applyMatrix4(world.athena.matrixWorld);
        const hb = head.project(camera);
        errors.bubble = Math.max(errors.bubble, Math.hypot(((hb.x + 1) / 2) * window.innerWidth - used.bubbleCss.x, (1 - (hb.y + 1) / 2) * window.innerHeight - used.bubbleCss.y) * dpr);
        for (const l of used.labels) {
          const lp = l.world.clone().project(camera);
          errors.labels = Math.max(errors.labels, Math.hypot(((lp.x + 1) / 2) * window.innerWidth - l.x, (1 - (lp.y + 1) / 2) * window.innerHeight - l.y) * dpr);
        }
      }
      frames++;
    }
  }
  const maxErrorPx = Math.max(errors.glow, errors.bubble, errors.labels);
  return { maxErrorPx, errors, frames, windows, sync };
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
