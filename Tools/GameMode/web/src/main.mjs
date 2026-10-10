// 雅典娜的城邦 · M0 原型：加载夹具（城邦快照 + 回放事件），按真实节奏回放。
//
//   index.html?fixture=fixtures/synthetic.json   默认的合成夹具
//   &speed=2  回放速度        &t=30000  从第 30 秒开始       &paused=1  先暂停
//   &shot=1   固定一帧、不跑动画循环（截图用），渲染完 document.title = 'ready'
//   &selftest=motion   运动自检：镜头逐帧移动，比较屏幕空间效果与当帧真实投影，title = 'motion-max-error-px:…'
//   &ao=0 &shadows=0 &grade=0 &accent=0   调试开关
import * as THREE from '../vendor/three.bundle.mjs';
import { OrbitControls } from '../vendor/three.bundle.mjs';
import { createLayout, pointAlong, PLAYER_SPEED } from './layout.mjs';
import { createReplay } from './replay.mjs';
import { createOrchestrator, stepProgress } from './orchestrator.mjs';
import { lineForVisit, pick, LINES_ZH } from './narration.mjs';
import { createRenderer } from './render.mjs';
import { createOverlay } from './overlay.mjs';
import { makeMaterials } from './art/materials.mjs';
import { buildDeliveredScroll } from './art/figures.mjs';
import { buildWorld } from './world.mjs';
import { hashSeed } from './art/noise.mjs';

const params = new URLSearchParams(location.search);
const status = { ready: false, errors: [], phase: 'boot' };
window.__polis = status;
const record = (msg) => status.errors.push(String(msg));
window.addEventListener('error', (e) => record(e.message));
window.addEventListener('unhandledrejection', (e) => record(e.reason?.stack ?? e.reason));

const CAMERA = { distance: 300, elevation: 33, azimuth: 45 };
const BUBBLE_MS = 3600;
const BOAT_MS = 7000;

main().catch((e) => {
  record(e?.stack ?? e);
  document.title = 'error';
  const box = document.getElementById('error');
  box.hidden = false;
  box.querySelector('.detail').textContent = String(e?.message ?? e);
});

async function main() {
  status.phase = 'webgl';
  const probe = document.createElement('canvas').getContext('webgl2');
  if (!probe) throw new Error('这个浏览器没有 WebGL 2，城邦画不出来（设计稿 12.5：失败要明说，不静默降级）');

  status.phase = 'fixture';
  const fixtureUrl = params.get('fixture') ?? 'fixtures/synthetic.json';
  const res = await fetch(fixtureUrl);
  if (!res.ok) throw new Error(`读不到夹具 ${fixtureUrl}：HTTP ${res.status}`);
  const fixture = await res.json();
  if (fixture.kind !== 'athena-polis-fixture' || fixture.schema !== 1) throw new Error('这不是城邦夹具（schema 1）');
  status.fixture = { synthetic: fixture.synthetic, buildings: fixture.buildings.length, toolCalls: fixture.replay?.toolCalls ?? 0, durationMs: fixture.replay?.durationMs ?? 0 };

  status.phase = 'build';
  const layout = createLayout(fixture);
  const R = createRenderer({
    canvas: document.getElementById('scene'),
    ao: params.get('ao') !== '0', shadows: params.get('shadows') !== '0',
    grade: params.get('grade') !== '0', accent: params.get('accent') !== '0',
  });
  const M = makeMaterials();
  const world = buildWorld(R.scene, fixture, layout, M);
  const overlay = createOverlay(document.getElementById('overlay'));
  const kindOf = (key) => fixture.buildings.find((b) => b.key === key)?.kind ?? null;
  const buildingKeys = [...world.buildings.keys()];

  // —— 玩家：点哪走哪（真实时间，不随回放暂停） ——
  const player = { pos: { x: 2.6, z: 4.4 }, route: null, start: 0, length: 0, yaw: Math.PI * 0.85 };
  const playerSite = () => `pt:${(player.pos.x + 0.9).toFixed(2)},${(player.pos.z + 0.4).toFixed(2)}`;

  // —— 回放 + 编排器（重新开始 / 跳转时整个重建） ——
  let sim;
  function createSim() {
    let wander = 0;
    const replay = createReplay(fixture);
    const orch = createOrchestrator({
      travelMs: (a, b) => layout.travelMs(a, b),
      wanderTarget: () => `agora:${(wander++) % 6}`,
      playerSpot: playerSite,
    });
    return { replay, orch, time: 0, cues: [], cueSteps: 0, delivered: new Map(), collected: new Set() };
  }
  sim = createSim();

  // —— 每一步的路线（缓存） ——
  const routes = new WeakMap();
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

  /** 雅典娜在时刻 now 的位置、朝向、光，以及手里拿什么。 */
  function athenaPose(state, now) {
    const step = state.step;
    const pose = { x: 0, z: 0, yaw: 0, visible: true, glow: 0.22, teleport: null, prop: null, swing: 0 };
    if (!step) return pose;
    if (step.kind === 'walk' || step.kind === 'wander') {
      const p = pointAlong(routeOf(step), ease(stepProgress(step, now)));
      Object.assign(pose, { x: p.x, z: p.z, yaw: p.dir, glow: 0.24 });
      return pose;
    }
    if (step.kind === 'teleport') {
      const f = stepProgress(step, now);
      const p = pointAlong(routeOf(step), f);
      const site = f < 0.5 ? layout.sitePoint(step.from) : layout.sitePoint(step.to);
      Object.assign(pose, { x: site.x, z: site.z, yaw: facingAt(step.to), visible: f < 0.15 || f > 0.85, glow: 0.55 });
      pose.teleport = new THREE.Vector3(p.x, 1.15, p.z);
      return pose;
    }
    const at = step.at ?? state.position;
    const p = layout.sitePoint(at);
    Object.assign(pose, { x: p.x, z: p.z, yaw: facingAt(at) });
    if (step.kind === 'meditate') {
      const level = 0.6 + Math.min(1, (step.reasoning ?? 0) / 1200) * 0.8;
      pose.glow = 0.2 + 0.07 * level * (0.5 + 0.5 * Math.sin(((now - step.start) / 1700) * Math.PI * 2));
    } else if (step.kind === 'act') {
      pose.glow = step.failed ? 0.07 : 0.32;   // 失败：光暗下去
      const c = step.category;
      pose.prop = c === 'read' || c === 'memory' ? 'scroll' : c === 'write' ? 'tablet' : c === 'terminal' || c === 'workshop' ? 'hammer' : null;
      pose.swing = Math.sin(now / 160);
    } else if (step.kind === 'deliver') {
      pose.glow = 0.34;
      pose.prop = stepProgress(step, now) < 0.55 ? 'gift' : null;
    }
    return pose;
  }

  function applyAthena(pose) {
    const a = world.athena;
    a.position.set(pose.x, 0.012, pose.z);
    a.rotation.y = pose.yaw;
    a.visible = pose.visible;
    for (const [name, prop] of Object.entries(world.props)) prop.visible = pose.visible && pose.prop === name;
    if (pose.prop === 'hammer') world.props.hammer.rotation.x = -0.6 + 0.6 * pose.swing;
    a.userData.light.intensity = 0.6 + pose.glow * 3;
  }

  // —— 场景里随动作变化的东西：脚手架、船、侍女、交付的卷轴 ——
  function applyEffects(state, now) {
    const step = state.step;
    for (const [key, scaffold] of world.scaffolds) scaffold.visible = !!(step && step.kind === 'act' && step.category === 'write' && step.at === key);

    // 船：港口每有一次"出海"，船开出去再回来（纯画面，不挡任何事）
    const web = lastStep((s) => s.kind === 'act' && s.at === 'harbor' && s.start <= now && now - s.start < BOAT_MS);
    const boat = world.boat;
    boat.position.copy(world.boatHome);
    boat.rotation.y = 0;
    if (web) {
      const f = (now - web.start) / BOAT_MS;
      const out = f < 0.42 ? ease(f / 0.42) : f < 0.58 ? 1 : 1 - ease((f - 0.58) / 0.42);
      boat.position.z += out * 32;
      boat.position.x += Math.sin(out * Math.PI) * 4;
      boat.rotation.y = f < 0.5 ? 0 : Math.PI;
    }

    // 黄金侍女：派发子代理时从雅典娜身边唤出，分头去各处，再回来、消散
    const sub = lastStep((s) => s.kind === 'act' && s.category === 'subagents' && s.start <= now && (s.end == null || now < s.end));
    world.maidens.forEach((m) => { m.visible = false; });
    if (sub) {
      const tool = sim.orch.tools.get(sub.visit.toolIds[0]);
      const n = Math.min(world.maidens.length, tool?.meta?.agents ?? 3);
      const duration = Math.max(4000, (sub.end ?? now + 1) - sub.start);
      const f = Math.min(1, (now - sub.start) / duration);
      const home = layout.sitePoint(sub.at);
      for (let k = 0; k < n; k++) {
        const m = world.maidens[k];
        const around = { x: home.x + Math.cos((k / n) * Math.PI * 2) * 1.3, z: home.z + Math.sin((k / n) * Math.PI * 2) * 1.3 };
        const target = buildingKeys.length ? layout.sitePoint(buildingKeys[(hashSeed(`${sub.start}:${k}`) % buildingKeys.length)]) : around;
        const out = f < 0.12 ? 0 : f < 0.45 ? ease((f - 0.12) / 0.33) : f < 0.7 ? 1 : f < 0.9 ? 1 - ease((f - 0.7) / 0.2) : 0;
        m.visible = f < 0.97;
        m.position.set(around.x + (target.x - around.x) * out, 0.02, around.z + (target.z - around.z) * out);
        m.scale.setScalar(0.9 * Math.min(1, f / 0.12, (1 - f) / 0.1 + 0.001));
        m.rotation.y = Math.atan2(target.x - around.x, target.z - around.z) + (f > 0.58 ? Math.PI : 0);
      }
    }

    // 交付：递到 55% 时卷轴落地，红色（点缀色），等你点它收下
    for (const s of sim.orch.steps) {
      if (s.kind !== 'deliver' || now < s.start + (s.end - s.start) * 0.55 || sim.delivered.has(s)) continue;
      const scroll = buildDeliveredScroll(M);
      const p = layout.sitePoint(s.at);
      scroll.position.set(p.x - 0.5, 0.01, p.z - 0.15);
      scroll.rotation.y = 0.5;
      world.athena.parent.add(scroll);
      sim.delivered.set(s, { object: scroll, collectedAt: null });
    }
    for (const [s, item] of sim.delivered) {
      if (now < s.start) { item.object.visible = false; continue; }
      item.object.visible = true;
      const ring = item.object.userData.ring;
      ring.visible = item.collectedAt == null;
      ring.scale.setScalar(1 + 0.22 * Math.sin(performance.now() / 260));
      if (item.collectedAt != null) {
        const k = Math.min(1, (performance.now() - item.collectedAt) / 1200);
        item.object.position.y = 0.01 - k * 0.3;
        item.object.visible = k < 1;
      }
    }
  }

  function lastStep(pred) {
    const steps = sim.orch.steps;
    for (let i = steps.length - 1; i >= 0; i--) if (pred(steps[i])) return steps[i];
    return null;
  }

  // —— 旁白：按步骤和回合事件生成"台词时间线"，取最近的一句 ——
  function refreshCues() {
    const steps = sim.orch.steps;
    for (let i = sim.cueSteps; i < steps.length; i++) {
      const s = steps[i];
      const tools = (s.visit?.toolIds ?? []).map((id) => sim.orch.tools.get(id)?.meta).filter(Boolean);
      if ((s.kind === 'walk' || s.kind === 'teleport') && s.visit?.kind === 'work') {
        s.visit.announced = true;
        sim.cues.push({ t: s.start, text: lineForVisit(s.visit, tools, kindOf, i) });
      } else if (s.kind === 'act' && !s.visit.announced) {
        s.visit.announced = true;
        sim.cues.push({ t: s.start, text: lineForVisit(s.visit, tools, kindOf, i) });
      } else if (s.kind === 'deliver') {
        sim.cues.push({ t: s.start, text: lineForVisit(s.visit, [], kindOf, i) });
      }
      if (s.kind === 'act' && s.failed) sim.cues.push({ t: s.start + 900, text: pick(LINES_ZH.failure, i) });
    }
    sim.cueSteps = steps.length;
  }

  function currentLine(now) {
    let best = null;
    for (const e of sim.replay.fed) {
      if (e.type === 'turn' && e.t <= now && now - e.t < BUBBLE_MS && (!best || e.t >= best.t)) best = { t: e.t, text: pick(LINES_ZH.turn, e.turn) };
    }
    for (const c of sim.cues) if (c.text && c.t <= now && now - c.t < BUBBLE_MS && (!best || c.t >= best.t)) best = c;
    if (!best) return null;
    const age = now - best.t;
    return { text: best.text, alpha: Math.min(1, age / 150, (BUBBLE_MS - age) / 400) };
  }

  // —— 玩家走路 ——
  function updatePlayer(wall) {
    if (player.route) {
      const f = Math.min(1, (wall - player.start) / (player.length / PLAYER_SPEED * 1000));
      const p = pointAlong(player.route, f);
      player.pos = { x: p.x, z: p.z };
      player.yaw = p.dir;
      if (f >= 1) player.route = null;
    }
    world.player.position.set(player.pos.x, 0.012, player.pos.z);
    world.player.rotation.y = player.yaw;
  }

  function walkPlayerTo(site, wall) {
    const from = `pt:${player.pos.x.toFixed(2)},${player.pos.z.toFixed(2)}`;
    const route = layout.route(from, site);
    player.route = route;
    player.start = wall;
    player.length = route.reduce((d, p, i) => (i ? d + Math.hypot(p.x - route[i - 1].x, p.z - route[i - 1].z) : 0), 0);
  }

  function collect(item) {
    item.collectedAt = performance.now();
    item.object.traverse((o) => { if (o.userData.accent) o.userData.accent = false; });   // 收下：颜色褪进城邦
  }

  // —— 镜头：默认跟着雅典娜，F 切到跟着你；右键拖动平移、滚轮缩放 ——
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
  controls.mouseButtons = { LEFT: null, MIDDLE: THREE.MOUSE.DOLLY, RIGHT: THREE.MOUSE.PAN };
  controls.minZoom = 0.4; controls.maxZoom = 2.6;
  controls.update();
  let follow = params.get('follow') === 'player' ? 'player' : 'athena';
  controls.addEventListener('start', () => { if (dragButton === 2) follow = 'free'; });
  let dragButton = -1;

  function followCamera(dt, pose, snap = false) {
    if (follow === 'free') return;
    const want = follow === 'player'
      ? new THREE.Vector3(player.pos.x, 1, player.pos.z)
      : pose.teleport ? new THREE.Vector3(pose.teleport.x, 1, pose.teleport.z) : new THREE.Vector3(pose.x, 1, pose.z);
    const k = snap ? 1 : 1 - Math.exp(-dt / 450);
    const delta = want.sub(controls.target).multiplyScalar(k);
    controls.target.add(delta);
    camera.position.add(delta);
  }

  // —— 一帧：推进 → 摆好人物和镜头 → 对齐矩阵 → 算屏幕空间位置 → 渲染 → 叠加层 ——
  const tmp = new THREE.Vector3();
  let used = null;
  function frame(now, dt, wall, { snap = false, sync = true, script = null } = {}) {
    sim.replay.feed(sim.orch, now);
    const state = sim.orch.update(now);
    refreshCues();
    updatePlayer(wall);
    const pose = athenaPose(state, now);
    applyAthena(pose);
    applyEffects(state, now);
    followCamera(dt, pose, snap);
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
    const labels = world.labels.map((l) => ({ ...l, ...R.projectCss(l.anchor), world: l.anchor }));
    const line = currentLine(now);
    const edges = [];
    for (const item of sim.delivered.values()) {
      if (!item.object.visible || item.collectedAt != null) continue;
      const p = R.projectCss(tmp.copy(item.object.position));
      if (p.x < 0 || p.y < 0 || p.x > window.innerWidth || p.y > window.innerHeight) edges.push(p);
    }
    used = { glowWorld, glowUv: { x: glowUv.x, y: glowUv.y }, headWorld, bubbleCss, labels };

    R.render();
    const { turn, delivered } = sim.replay.turnAt(now);
    overlay.draw({
      time: wall,
      labels: labels.filter((l) => l.x > -60 && l.y > -20 && l.x < window.innerWidth + 60 && l.y < window.innerHeight + 20),
      bubble: line ? { text: line.text, x: bubbleCss.x, y: bubbleCss.y, alpha: line.alpha } : null,
      edges,
      hud: hudVisible ? [
        `回放 ${clock(now)} / ${clock(sim.replay.duration)} · 第 ${turn} 回合 / 共 ${fixture.replay?.turns ?? 0}${delivered ? '（已交付）' : ''} · ×${speed}${playing ? '' : ' · 已暂停'}`,
        `镜头：${follow === 'athena' ? '跟着雅典娜' : follow === 'player' ? '跟着你' : '自由'} · 点地面让你走过去 · 红色的卷轴是交给你的成果，点它收下`,
      ] : null,
    });
    status.now = now;
    status.step = state.step?.kind ?? null;
    return state;
  }

  const clock = (ms) => {
    const s = Math.max(0, Math.floor(ms / 1000));
    return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`;
  };

  // —— 输入 ——
  let playing = params.get('paused') !== '1';
  let speed = Number(params.get('speed') ?? 1) || 1;
  let hudVisible = params.get('hud') !== '0';
  const canvas = R.renderer.domElement;
  let downAt = null;
  canvas.addEventListener('pointerdown', (e) => { dragButton = e.button; downAt = { x: e.clientX, y: e.clientY, t: performance.now() }; });
  canvas.addEventListener('pointerup', (e) => {
    const d = downAt;
    downAt = null;
    dragButton = -1;
    if (!d || e.button !== 0 || Math.hypot(e.clientX - d.x, e.clientY - d.y) > 6) return;
    // 先看是不是点在等你收下的卷轴上
    for (const item of sim.delivered.values()) {
      if (item.collectedAt != null || !item.object.visible) continue;
      const p = R.projectCss(item.object.position);
      if (Math.hypot(p.x - e.clientX, p.y - e.clientY) < 28) { collect(item); return; }
    }
    const ndc = new THREE.Vector2((e.clientX / window.innerWidth) * 2 - 1, -(e.clientY / window.innerHeight) * 2 + 1);
    const ray = new THREE.Raycaster();
    R.syncMatrices();
    ray.setFromCamera(ndc, camera);
    const hit = ray.ray.intersectPlane(new THREE.Plane(new THREE.Vector3(0, 1, 0), 0), new THREE.Vector3());
    if (hit && hit.z < layout.quayZ + 2) walkPlayerTo(layout.snapClick({ x: hit.x, z: hit.z }), performance.now());
  });
  canvas.addEventListener('contextmenu', (e) => e.preventDefault());
  const toggleFollow = () => { follow = follow === 'athena' ? 'player' : 'athena'; syncButtons(); };
  const restart = () => { sim = createSim(); syncButtons(); };
  window.addEventListener('keydown', (e) => {
    if (e.code === 'Space') { playing = !playing; e.preventDefault(); }
    else if (e.key === 'f' || e.key === 'F') toggleFollow();
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
    buttons.follow.textContent = follow === 'player' ? '镜头：跟着你' : follow === 'athena' ? '镜头：跟着雅典娜' : '镜头：自由';
  }
  buttons.play.addEventListener('click', () => { playing = !playing; syncButtons(); });
  buttons.speed.addEventListener('click', () => { speed = speed === 1 ? 2 : speed === 2 ? 4 : 1; syncButtons(); });
  buttons.follow.addEventListener('click', toggleFollow);
  buttons.restart.addEventListener('click', restart);
  syncButtons();
  window.addEventListener('resize', () => { R.resize(); overlay.resize(); });

  // —— 跳到某一刻：把回放和编排器从头推进过去（编排器是纯逻辑，推进很快） ——
  function seek(t) {
    sim = createSim();
    for (let x = 0; x < t; x += 50) {
      sim.replay.feed(sim.orch, x);
      sim.orch.update(x);
    }
    sim.time = t;
  }

  status.phase = 'render';
  const startAt = Number(params.get('t') ?? 0) || 0;
  if (startAt > 0) seek(startAt);

  status.seek = (t) => { seek(t); frame(sim.time, 16, performance.now(), { snap: true }); };
  status.pixelStats = () => pixelStats(R.renderer.domElement);
  status.steps = () => sim.orch.steps.map(({ kind, start, end, at, to, from }) => ({ kind, start, end, at, to, from }));
  status.collectAll = () => { for (const item of sim.delivered.values()) if (item.collectedAt == null) collect(item); };

  if (params.get('selftest') === 'motion') {
    const result = motionSelftest();
    status.selftest = result;
    status.ready = true;
    document.title = `motion-max-error-px:${result.maxErrorPx.toFixed(3)}`;
    return;
  }

  if (params.get('shot') === '1') {
    for (let i = 0; i < 3; i++) frame(sim.time, 16, performance.now(), { snap: true });
    status.ready = true;
    document.title = 'ready';
    return;
  }

  let last = performance.now();
  frame(sim.time, 16, last, { snap: true });
  status.ready = true;
  document.title = 'ready';
  const loop = (wall) => {
    const dt = Math.min(100, wall - last);
    last = wall;
    if (playing) sim.time += dt * speed;
    try {
      frame(sim.time, dt, wall);
    } catch (e) {
      record(e?.stack ?? e);
      throw e;
    }
    requestAnimationFrame(loop);
  };
  requestAnimationFrame(loop);

  // —— 运动自检（设计稿 12.7）：镜头按脚本逐帧移动，比较每个屏幕空间效果这一帧用的位置与当帧的真实投影 ——
  // 真值在渲染之后算：渲染器这时已经把相机和人物的矩阵更新成了这一帧真正用的那一组。
  function motionSelftest() {
    const sync = params.get('nosync') !== '1';   // nosync=1：故意不对齐矩阵，看修复前的偏差有多大
    // 先把整段回放过一遍，找出一段"走"和一次"瞬移"
    seek(sim.replay.duration + 2000);
    const steps = sim.orch.steps;
    const walk = steps.find((s) => s.kind === 'walk' && s.end - s.start > 800);
    const tele = steps.find((s) => s.kind === 'teleport');
    const windows = [walk, tele].filter(Boolean).map((s) => [s.start - 200, s.end + 200]);
    if (windows.length === 0) windows.push([0, 3000]);
    const errors = { glow: 0, bubble: 0, labels: 0 };
    let frames = 0;
    const size = R.size, dpr = overlay.dpr;
    let i = 0;
    for (const [from, to] of windows) {
      seek(Math.max(0, from));
      follow = 'athena';
      for (let t = Math.max(0, from); t <= to; t += 40, i++) {
        // 脚本化的镜头运动：跟着雅典娜的同时绕目标转、推拉、平移，每帧都动（和 OrbitControls 一样只改位置，朝向交给 lookAt）
        const script = () => {
          camera.zoom = 1 + 0.35 * Math.sin(i / 7);
          camera.updateProjectionMatrix();
          controls.target.add(new THREE.Vector3(Math.sin(i / 5) * 2.5, 0, Math.cos(i / 6) * 2.5));
          placeCamera(controls.target, 45 + i * 1.3, 33 + 6 * Math.sin(i / 9));
        };
        frame(t, 40, performance.now(), { snap: true, sync, script });
        // —— 真值：渲染之后的矩阵 ——
        const truthGlow = used.glowWorld.clone();
        if (!(sim.orch.state(t).step?.kind === 'teleport')) truthGlow.copy(world.athena.userData.chest).applyMatrix4(world.athena.matrixWorld);
        const tg = truthGlow.project(camera);
        if (frames > 0) {
          errors.glow = Math.max(errors.glow, Math.hypot(((tg.x + 1) / 2 - used.glowUv.x) * size.x, ((tg.y + 1) / 2 - used.glowUv.y) * size.y));
          const head = sim.orch.state(t).step?.kind === 'teleport' ? used.headWorld.clone() : world.athena.userData.headTop.clone().applyMatrix4(world.athena.matrixWorld);
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
