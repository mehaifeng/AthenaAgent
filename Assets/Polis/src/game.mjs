// 城邦这一局：场景、人物、镜头、旁白、成果、面板，以及每一帧的推进。M0 的 main.mjs 拆出来的——
// 同一套东西现在有两个驱动：回放（夹具，测试与演示）和实时（应用里的 NativeWebView，C# 推事件）。
//
// 屏幕空间效果（光晕、旁白气泡、建筑名牌）必须用"这一帧真正用来渲染的"相机矩阵投影（设计稿 12.2）。
// 每帧的顺序固定为：推进 → 摆好人物和镜头 → 对齐矩阵 → 算屏幕空间位置 → 渲染 → 叠加层。
import * as THREE from '../vendor/three.bundle.mjs';
import { OrbitControls } from '../vendor/three.bundle.mjs';
import { createLayout, pointAlong, PLAYER_SPEED } from './layout.mjs';
import { cityIndex, siteForTool } from './replay.mjs';
import { createOrchestrator, stepProgress } from './orchestrator.mjs';
import { lineForVisit, pick, LINES_ZH, PLACE_ZH } from './narration.mjs';
import { createRenderer } from './render.mjs';
import { createOverlay } from './overlay.mjs';
import { makeMaterials } from './art/materials.mjs';
import { buildDeliveredItem } from './art/figures.mjs';
import { buildWorld, PUBLIC_NAMES } from './world.mjs';
import { hashSeed } from './art/noise.mjs';
import { framePolicy, transitionArrival } from './live.mjs';

const CAMERA = { distance: 300, elevation: 33, azimuth: 45 };
const BUBBLE_MS = 3600;
const BOAT_MS = 7000;
const VOYAGE_FADE_MS = 400;
const REVEAL_MS = 3200;

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
  followAthena: '镜头：跟着雅典娜',
  followPlayer: '镜头：跟着你',
  followFree: '镜头：自由',
});

/**
 * @param {object} options
 * @param {URLSearchParams} options.params
 * @param {object} options.status window.__polis（测试读它）
 * @param {(type: string, fields?: object) => void} options.send 发一条意图给宿主（回放模式里只记下）
 * @param {(item: object) => void} [options.onReadItem] 读一件成果（回放模式在本地开阅读器）
 */
export function createGame({ params, status, send, onReadItem }) {
  const R = createRenderer({
    canvas: document.getElementById('scene'),
    ao: params.get('ao') !== '0', shadows: params.get('shadows') !== '0',
    grade: params.get('grade') !== '0', accent: params.get('accent') !== '0',
  });
  const M = makeMaterials();
  const overlay = createOverlay(document.getElementById('overlay'));

  let ui = { ...UI_ZH };
  let lines = { ...LINES_ZH };
  let places = { ...PLACE_ZH };
  let publicNames = { ...PUBLIC_NAMES };

  // —— 城 ——
  let city = null;
  let cityKey = null;
  let layout = null;
  let world = null;
  let index = cityIndex({});
  let kindOf = () => null;
  let buildingKeys = [];
  let sanctuary = false;
  let fogInfo = null;
  const flashes = new Map();   // 建筑 key → 外部改动的闪动开始时间

  // —— 玩家：点哪走哪（真实时间，不随回放暂停） ——
  const player = { pos: { x: 2.6, z: 4.4 }, route: null, start: 0, length: 0, yaw: Math.PI * 0.85, savedAt: 0 };
  const playerSite = () => `pt:${(player.pos.x + 0.9).toFixed(2)},${(player.pos.z + 0.4).toFixed(2)}`;

  // —— 编排器 + 旁白（换城、换会话时整个重建） ——
  let sim = null;
  function createSim(home = 'agora', startAt = 0) {
    let wander = 0;
    const orch = createOrchestrator({
      travelMs: (a, b) => layout.travelMs(a, b),
      wanderTarget: () => `agora:${(wander++) % 6}`,
      playerSpot: playerSite,
      home,
      startAt,
    });
    return { orch, time: startAt, cues: [], cueSteps: 0, turns: [], delivered: new Map(), stoppedAt: null };
  }

  // —— 成果：C# 推来的藏品（实时模式），或回放里交付时生成的本地记录 ——
  const items = new Map();      // id → { item, object, collectedAt, shownAt }
  let card = null;              // 当前打开的成果卡片的 id
  let notices = [];
  let herald = null;
  let voyage = null;            // { startedAt, arriveAt, caption, fadeOutAt }
  let reveal = null;            // 第一次进入的俯瞰揭幕
  let lastInputAt = -Infinity;

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
  let dragButton = -1;
  controls.addEventListener('start', () => { if (dragButton === 2) follow = 'free'; lastInputAt = performance.now(); });

  // —— 城的装载：整座重建，或者按座替换 ——
  function loadCity(doc, options = {}) {
    const keepPlayer = options.key != null && options.key === cityKey;
    if (world) world.dispose();
    city = doc;
    cityKey = options.key ?? null;
    sanctuary = !!options.sanctuary;
    layout = createLayout(doc);
    world = buildWorld(R.scene, doc, layout, M, { names: publicNames, sanctuary });
    index = cityIndex(doc);
    kindOf = (key) => doc.buildings.find((b) => b.key === key)?.kind ?? null;
    buildingKeys = [...world.buildings.keys()];
    for (const record of items.values()) record.object = null;   // 旧城的物件随旧城拆掉
    if (!keepPlayer) {
      const p = options.player;
      player.pos = p && Number.isFinite(p.x) && Number.isFinite(p.z) ? { x: p.x, z: p.z } : { x: 2.6, z: 4.4 };
      player.route = null;
      if (p && Number.isFinite(p.zoom)) { camera.zoom = p.zoom; camera.updateProjectionMatrix(); }
    }
    if (!keepPlayer || !sim) sim = createSim(options.home ?? 'agora', options.now ?? 0);
    if (options.founding) {
      // 第一次进入这座城（5.4）：俯瞰揭幕，旁白"这是你的城邦，每座建筑都是你的一个文件夹"；之后从存档恢复，不再播
      reveal = { startedAt: performance.now() };
      sim.cues.push({ t: sim.time, text: ui.founding, minMs: REVEAL_MS });
    }
    status.city = { buildings: doc.buildings.length, vacant: (doc.vacant ?? []).length, market: (doc.market ?? []).length, sanctuary, key: cityKey };
  }

  /**
   * 同一座城的新版本（核对之后、或外部改动之后）：能按座替换就按座替换——
   * 新建筑的地块在当前这一圈之内、海岸线没变；否则整城重建（人物与时间线保留）。
   * origins：建筑 key → 'athena' | 'external'；外部改动的建筑名牌闪一下（没有光、没有角色动作）。
   */
  function updateCity(doc, origins = {}) {
    if (!city || !layout || !world || (doc.seaStartsAtZ ?? 2) !== (city.seaStartsAtZ ?? 2)) {
      loadCity(doc, { key: cityKey, sanctuary, now: sim?.time ?? 0 });
      return 'rebuilt';
    }
    const before = new Map(city.buildings.map((b) => [b.key, b]));
    const after = new Map(doc.buildings.map((b) => [b.key, b]));
    const fits = doc.buildings.every((b) => layout.plots.has(`b:${b.key}`) || Math.max(Math.abs(b.plot.x), Math.abs(b.plot.z)) <= layout.ring);
    const moved = doc.buildings.some((b) => before.has(b.key) && (before.get(b.key).plot.x !== b.plot.x || before.get(b.key).plot.z !== b.plot.z));
    const marketChanged = JSON.stringify(doc.market ?? []) !== JSON.stringify(city.market ?? []);
    if (!fits || moved || marketChanged) {
      // 改名时 C# 已经把账本条目换了名字：同一块地换了一个 key，这也走整城重建（地块没动）
      const keep = { player: { ...player.pos }, time: sim?.time ?? 0 };
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
    index = cityIndex(doc);
    kindOf = (key) => doc.buildings.find((b) => b.key === key)?.kind ?? null;
    buildingKeys = [...world.buildings.keys()];
    markExternal(origins);
    status.city = { ...status.city, buildings: doc.buildings.length, vacant: (doc.vacant ?? []).length };
    return changed > 0 ? 'patched' : 'unchanged';
  }

  function sameBuilding(a, b) {
    return a.kind === b.kind && a.sizeClass === b.sizeClass && a.state === b.state && a.incomplete === b.incomplete;
  }

  function markExternal(origins) {
    const t = performance.now();
    for (const [key, origin] of Object.entries(origins ?? {})) if (origin === 'external') flashes.set(`b:${key}`, t);
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
      const site = f < 0.5 ? layout.sitePoint(safeSite(step.from)) : layout.sitePoint(safeSite(step.to));
      Object.assign(pose, { x: site.x, z: site.z, yaw: facingAt(safeSite(step.to)), visible: f < 0.15 || f > 0.85, glow: 0.55 });
      pose.teleport = new THREE.Vector3(p.x, 1.15, p.z);
      return pose;
    }
    const at = safeSite(step.at ?? state.position);
    const p = layout.sitePoint(at);
    Object.assign(pose, { x: p.x, z: p.z, yaw: facingAt(at) });
    if (step.kind === 'meditate') {
      const level = 0.6 + Math.min(1, (step.reasoning ?? 0) / 1200) * 0.8;
      pose.glow = 0.2 + 0.07 * level * (0.5 + 0.5 * Math.sin(((now - step.start) / 1700) * Math.PI * 2));
    } else if (step.kind === 'await') {
      // 停在门槛前，手中石板的封印是红色的；光收着，不干活
      pose.glow = 0.16;
      pose.prop = 'sealed';
    } else if (step.kind === 'act') {
      pose.acting = true;
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
    // 光属于雅典娜：她在哪干活，哪里就亮。做事时她的光挪到身前、照得更远，把所在建筑的正面照亮
    const light = a.userData.light;
    if (pose.acting) {
      light.position.set(0, 2.6, 2.2);
      light.distance = 14;
      light.intensity = pose.glow > 0.1 ? 28 : 2;
    } else {
      light.position.set(0, 0.45, 0.3);
      light.distance = 4.5;
      light.intensity = 0.6 + pose.glow * 3;
    }
  }

  // —— 场景里随动作变化的东西：脚手架、船、侍女、交付的成果 ——
  function applyEffects(state, now) {
    const step = state.step;
    for (const [key, scaffold] of world.scaffolds) {
      const writing = step && step.kind === 'act' && step.category === 'write' && step.at === key;
      // 被打断的委托：停工的脚手架（设计稿 10.3 第 5 条）
      scaffold.visible = !!(writing || (sim.stoppedAt === key));
    }

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

    const sub = lastStep((s) => s.kind === 'act' && s.category === 'subagents' && s.start <= now && (s.end == null || now < s.end));
    world.maidens.forEach((m) => { m.visible = false; });
    if (sub) {
      const tool = sim.orch.tools.get(sub.visit.toolIds[0]);
      const n = Math.min(world.maidens.length, tool?.meta?.agents ?? 3);
      const duration = Math.max(4000, (sub.end ?? now + 1) - sub.start);
      const f = Math.min(1, (now - sub.start) / duration);
      const home = layout.sitePoint(safeSite(sub.at));
      for (let k = 0; k < n; k++) {
        const m = world.maidens[k];
        const around = { x: home.x + Math.cos((k / n) * Math.PI * 2) * 1.3, z: home.z + Math.sin((k / n) * Math.PI * 2) * 1.3 };
        const target2 = buildingKeys.length ? layout.sitePoint(buildingKeys[(hashSeed(`${sub.start}:${k}`) % buildingKeys.length)]) : around;
        const out = f < 0.12 ? 0 : f < 0.45 ? ease((f - 0.12) / 0.33) : f < 0.7 ? 1 : f < 0.9 ? 1 - ease((f - 0.7) / 0.2) : 0;
        m.visible = f < 0.97;
        m.position.set(around.x + (target2.x - around.x) * out, 0.02, around.z + (target2.z - around.z) * out);
        m.scale.setScalar(0.9 * Math.min(1, f / 0.12, (1 - f) / 0.1 + 0.001));
        m.rotation.y = Math.atan2(target2.x - around.x, target2.z - around.z) + (f > 0.58 ? Math.PI : 0);
      }
    }

    // 回放里的交付：递到 55% 时在你脚下生成一件本地成果（实时模式里成果由 C# 推来，按 itemId 对上这一步）
    for (const s of sim.orch.steps) {
      if (s.kind !== 'deliver' || now < s.start + (s.end - s.start) * 0.55 || sim.delivered.has(s)) continue;
      sim.delivered.set(s, true);
      const id = s.itemId ?? `replay:${s.turn ?? sim.delivered.size}:${Math.round(s.start)}`;
      const existing = items.get(id);
      const p = layout.sitePoint(safeSite(s.at));
      if (existing) {
        existing.spot = { x: p.x - 0.5, z: p.z - 0.15 };
        existing.revealAt = performance.now();
      } else if (!s.itemId) {
        items.set(id, { item: { id, kind: 'scroll', title: pick(lines.deliver, s.turn ?? 0), state: 'pending', local: true }, object: null, spot: { x: p.x - 0.5, z: p.z - 0.15 }, revealAt: performance.now() });
      }
    }
    placeItems(now);
  }

  /** 成果在场景里的样子：待处理的带颜色和白环；收下的颜色褪掉、沉进城里；退回的变灰、沉下去；遗失的是空底座。 */
  function placeItems() {
    const wall = performance.now();
    let row = 0;
    for (const record of [...items.values()].sort((a, b) => (a.item.createdAt ?? '').localeCompare(b.item.createdAt ?? ''))) {
      const { item } = record;
      const visibleState = item.state === 'pending' || record.collectedAt != null;
      if (!visibleState && !item.lost) {
        if (record.object) record.object.visible = false;
        continue;
      }
      if (!record.object) {
        record.object = buildDeliveredItem(M, item.kind ?? 'scroll');
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
      o.children.forEach((child, i) => { if (i > 0 && child !== o.userData.ring) child.visible = !item.lost; });
      const ring = o.userData.ring;
      ring.visible = pending;
      ring.scale.setScalar(1 + 0.22 * Math.sin(wall / 260));
      if (record.collectedAt != null) {
        const k = Math.min(1, (wall - record.collectedAt) / 1200);
        o.position.y = 0.01 - k * 0.3;
        o.visible = k < 1;
        if (k >= 1) record.collectedAt = null;
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
        sim.cues.push({ t: s.start, text: lineForVisit(s.visit, tools, kindOf, i, lines, 'go', places) });
      } else if (s.kind === 'act' && !s.visit.announced) {
        s.visit.announced = true;
        sim.cues.push({ t: s.start, text: lineForVisit(s.visit, tools, kindOf, i, lines, 'at', places), step: i });
      } else if (s.kind === 'act' && !s.visit.arrived && ['terminal', 'web'].includes(s.category)) {
        s.visit.arrived = true;
        sim.cues.push({ t: s.start, text: lineForVisit(s.visit, tools, kindOf, i, lines, 'at', places) });
      } else if (s.kind === 'deliver') {
        sim.cues.push({ t: s.start, text: lineForVisit(s.visit, [], kindOf, i, lines, 'go', places), minMs: 2600 });
      } else if (s.kind === 'await') {
        // 刚开工就停下来等审批（工具调用先出现、审批随后弹出）：那句"我在做什么"不成立了，撤掉，只说要你点头
        const prev = steps[i - 1];
        if (prev && prev.kind === 'act' && prev.stoppedForApproval && prev.end - prev.start < 400) {
          sim.cues = sim.cues.filter((c) => c.step !== i - 1);
        }
        sim.cues.push({ t: s.start, text: pick(lines.approval, i), minMs: 2600, step: i });
      }
      if (s.kind === 'act' && s.failed) sim.cues.push({ t: s.start + 900, text: pick(lines.failure, i) });
    }
    sim.cueSteps = steps.length;
  }

  function currentLine(now) {
    const all = [
      ...sim.turns.map((t) => ({ t: t.t, text: pick(lines.turn, t.turn), minMs: 1600 })),
      ...sim.cues.filter((c) => c.text),
    ].sort((a, b) => a.t - b.t);
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

  // —— 玩家走路 ——
  function updatePlayer(wall) {
    if (player.route) {
      const f = Math.min(1, (wall - player.start) / (player.length / PLAYER_SPEED * 1000));
      const p = pointAlong(player.route, f);
      player.pos = { x: p.x, z: p.z };
      player.yaw = p.dir;
      if (f >= 1) {
        player.route = null;
        // 走到了就记下位置（存档里的"玩家进度"）；走的途中不发
        send('save-player', { x: +player.pos.x.toFixed(2), z: +player.pos.z.toFixed(2), zoom: +camera.zoom.toFixed(2) });
      }
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
  let regions = [];
  function frame(now, dt, wall, { snap = false, sync = true, script = null, feed = null, hud = null } = {}) {
    if (!world) {
      overlay.draw({ time: wall, labels: [], bubble: null, edges: [], voyage: voyageFrame(wall), ui });
      return null;
    }
    if (feed) feed(sim.orch, now);
    const state = sim.orch.update(now);
    sim.time = now;
    refreshCues();
    updatePlayer(wall);
    const pose = athenaPose(state, now);
    applyAthena(pose);
    applyEffects(state, now);
    followCamera(dt, pose, snap);
    if (reveal) applyReveal(wall);
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
    const flashNow = performance.now();
    const labels = world.labels.map((l) => {
      const started = l.key ? flashes.get(l.key) : null;
      const flash = started != null ? Math.max(0, 1 - (flashNow - started) / 2400) : 0;
      return { ...l, ...R.projectCss(l.anchor), world: l.anchor, flash };
    });
    const line = currentLine(now);
    const edges = [];
    for (const record of items.values()) {
      if (!record.object || !record.object.visible || record.item.state !== 'pending' || record.item.lost) continue;
      const p = R.projectCss(tmp.copy(record.object.position));
      if (p.x < 0 || p.y < 0 || p.x > window.innerWidth || p.y > window.innerHeight) edges.push(p);
    }
    // 等审批：她停在门槛前，红色的封印也算"轮到你了"——在画面外时同样在边缘指一下
    if (state.step?.kind === 'await') {
      const p = R.projectCss(tmp.set(pose.x, 1, pose.z));
      if (p.x < 0 || p.y < 0 || p.x > window.innerWidth || p.y > window.innerHeight) edges.push(p);
    }
    used = { glowWorld, glowUv: { x: glowUv.x, y: glowUv.y }, headWorld, bubbleCss, labels };

    R.render();
    regions = overlay.draw({
      time: wall,
      labels: labels.filter((l) => l.x > -60 && l.y > -20 && l.x < window.innerWidth + 60 && l.y < window.innerHeight + 20),
      bubble: line ? { text: line.text, x: bubbleCss.x, y: bubbleCss.y, alpha: line.alpha } : null,
      edges,
      hud,
      notices: notices.length ? notices : null,
      herald,
      fog: fogInfo,
      card: card ? cardView(card) : null,
      voyage: voyageFrame(wall),
      ui,
    });
    status.now = now;
    status.step = state.step?.kind ?? null;
    status.frames = (status.frames ?? 0) + 1;
    return state;
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
    if (arriveAt != null && wall >= arriveAt) {
      alpha = Math.max(0, 1 - (wall - arriveAt) / VOYAGE_FADE_MS);
      if (alpha === 0) { voyage = null; status.voyage = null; return null; }
    }
    const progress = Math.min(1, (wall - startedAt) / Math.max(1600, (arriveAt ?? wall + 2000) - startedAt));
    return { alpha, progress, caption };
  }

  function applyReveal(wall) {
    // 奠基揭幕：从高处俯瞰慢慢推近（镜头缩放从 0.45 推到 1），只播一次
    const f = Math.min(1, (wall - reveal.startedAt) / REVEAL_MS);
    camera.zoom = 0.45 + 0.55 * ease(f);
    camera.updateProjectionMatrix();
    if (f >= 1) reveal = null;
  }

  function cardView(id) {
    const record = items.get(id);
    if (!record) return null;
    const { item } = record;
    const kindLabel = { scroll: ui.kindScroll, ledger: ui.kindLedger, painting: ui.kindPainting, answer: ui.kindAnswer }[item.kind] ?? ui.kindScroll;
    const stateLabel = item.lost ? ui.stateLost
      : item.modified ? `${{ pending: ui.statePending, accepted: ui.stateAccepted, returned: ui.stateReturned }[item.state] ?? ''} · ${ui.stateModified}`
        : { pending: ui.statePending, accepted: ui.stateAccepted, returned: ui.stateReturned }[item.state] ?? '';
    return { title: item.title ?? '', kindLabel, stateLabel, state: item.state, path: item.path ?? null };
  }

  // —— 输入 ——
  const canvas = R.renderer.domElement;
  let downAt = null;
  canvas.addEventListener('pointerdown', (e) => { dragButton = e.button; downAt = { x: e.clientX, y: e.clientY, t: performance.now() }; lastInputAt = performance.now(); });
  canvas.addEventListener('pointerup', (e) => {
    const d = downAt;
    downAt = null;
    dragButton = -1;
    lastInputAt = performance.now();
    if (!d || e.button !== 0 || Math.hypot(e.clientX - d.x, e.clientY - d.y) > 6) return;
    // 先看叠加层上的按钮（面板画在 canvas 上，按坐标命中）
    const hitId = overlay.hit(e.clientX, e.clientY);
    if (hitId) { onButton(hitId); return; }
    if (!world || fogInfo || voyage) return;
    for (const record of items.values()) {
      if (!record.object || !record.object.visible) continue;
      const p = R.projectCss(record.object.position);
      if (Math.hypot(p.x - e.clientX, p.y - e.clientY) < 28) { card = record.item.id; return; }
    }
    card = null;
    const ndc = new THREE.Vector2((e.clientX / window.innerWidth) * 2 - 1, -(e.clientY / window.innerHeight) * 2 + 1);
    const ray = new THREE.Raycaster();
    R.syncMatrices();
    ray.setFromCamera(ndc, camera);
    const hit = ray.ray.intersectPlane(new THREE.Plane(new THREE.Vector3(0, 1, 0), 0), new THREE.Vector3());
    if (hit && hit.z < layout.quayZ + 2) walkPlayerTo(layout.snapClick({ x: hit.x, z: hit.z }), performance.now());
  });
  canvas.addEventListener('contextmenu', (e) => e.preventDefault());

  function onButton(id) {
    switch (id) {
      case 'herald-dismiss': herald = null; break;
      case 'relocate': send('relocate'); break;
      case 'card-close': card = null; break;
      case 'card-read': {
        const record = items.get(card);
        if (record) {
          if (record.item.local) onReadItem?.(record.item);
          else send('read-scroll', { itemId: record.item.id });
        }
        break;
      }
      case 'card-open': {
        const record = items.get(card);
        if (record?.item.path) send('open-file', { path: record.item.path });
        break;
      }
      case 'card-accept': decide('accepted'); break;
      case 'card-return': decide('returned'); break;
      default: break;
    }
  }

  // 收下 / 退回：本地先变（即时反馈），再告诉 C#——它是唯一的事实来源，随后推来的 items 会覆盖这里
  function decide(state) {
    const record = items.get(card);
    if (!record) return;
    record.item = { ...record.item, state };
    record.collectedAt = performance.now();
    if (!record.item.local) send(state === 'accepted' ? 'accept-delivery' : 'return-delivery', { itemId: record.item.id });
    card = null;
  }

  function toggleFollow() { follow = follow === 'athena' ? 'player' : 'athena'; }

  return {
    R, camera, controls, overlay,
    get world() { return world; },
    get layout() { return layout; },
    get city() { return city; },
    get sim() { return sim; },
    get used() { return used; },
    get follow() { return follow; },
    set follow(v) { follow = v; },
    get regions() { return regions; },
    placeCamera,
    loadCity,
    updateCity,
    frame,
    toggleFollow,
    onButton,
    get ui() { return ui; },
    setTables(tables) {
      if (tables.ui) ui = { ...UI_ZH, ...tables.ui };
      if (tables.lines) lines = { ...LINES_ZH, ...tables.lines };
      if (tables.places) places = { ...PLACE_ZH, ...tables.places };
      if (tables.publicNames) publicNames = { ...PUBLIC_NAMES, ...tables.publicNames };
    },
    /** 换一份委托（同一座城里切换会话）：雅典娜出现在那份委托最后一次动作的地点（9.2）。 */
    focus({ last = null, interrupted = false, now = 0 } = {}) {
      const site = last ? safeSite(siteForTool(last, index)) : 'agora';
      sim = createSim(site, now);
      routes = new WeakMap();
      sim.stoppedAt = interrupted && site.startsWith('b:') ? site : null;
      if (interrupted) sim.cues.push({ t: now, text: pick(lines.interrupted, 0), minMs: 3600 });
      status.focus = { site, interrupted };
    },
    /** 时间线从头来（回放的"重新开始"与跳转）：城不拆，编排器与本地成果重建。 */
    resetTimeline(home = 'agora', now = 0) {
      sim = createSim(home, now);
      routes = new WeakMap();
      for (const [id, record] of items) {
        if (!record.item.local) continue;
        if (record.object && world) world.root.remove(record.object);
        items.delete(id);
      }
      card = null;
    },
    /** 新回合开始时的开场白（实时模式里由 turn 事件驱动）。 */
    noteTurn(t, turn) {
      sim.turns.push({ t, turn });
      sim.stoppedAt = null;
    },
    setItems(list) {
      const seen = new Set();
      for (const item of list ?? []) {
        seen.add(item.id);
        const record = items.get(item.id);
        if (record) {
          const wasPending = record.item.state === 'pending';
          record.item = item;
          if (wasPending && item.state !== 'pending' && record.collectedAt == null) record.collectedAt = performance.now();
        } else {
          items.set(item.id, { item, object: null, spot: null, revealAt: null, collectedAt: null });
        }
      }
      for (const [id, record] of items) {
        if (record.item.local || seen.has(id)) continue;
        if (record.object && world) world.root.remove(record.object);
        items.delete(id);
      }
      status.items = [...items.values()].map((r) => ({ id: r.item.id, state: r.item.state, lost: !!r.item.lost }));
    },
    setNotices(list) { notices = (list ?? []).slice(-5); },
    setHerald(report) { herald = report && report.lines?.length ? report : null; status.herald = herald; },
    setFog(info) { fogInfo = info; status.fog = !!info; if (R.scene) R.scene.fog = info ? new THREE.Fog(0xd8d4cd, 220, 360) : null; },
    startVoyage(caption) { voyage = { startedAt: performance.now(), readyAt: null, cityArrived: false, arriveAt: null, caption }; status.voyage = 'sailing'; },
    /** 新城到了：完整的（partial = false）就可以靠岸；只有顶层的，等到上限再靠。 */
    cityArrived({ partial = false, at = performance.now() } = {}) {
      if (!voyage) return;
      voyage.cityArrived = true;
      if (!partial && voyage.readyAt == null) voyage.readyAt = at;
    },
    get voyaging() { return voyage != null; },
    get voyageStartedAt() { return voyage?.startedAt ?? null; },
    get lastInputAt() { return lastInputAt; },
    get playerMoving() { return player.route != null; },
    policy(visible) {
      return framePolicy({ visible, step: sim?.orch.state(sim.time).step?.kind ?? null, playerMoving: player.route != null, transitioning: voyage != null || reveal != null, now: performance.now(), lastInputAt });
    },
    resize() { R.resize(); overlay.resize(); },
    collectAll() { for (const id of items.keys()) { card = id; decide('accepted'); } },
    openCard(id) { if (items.has(id)) card = id; },
  };
}
