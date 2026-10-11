// 城邦这一局：场景、人物、镜头、旁白、成果、面板，以及每一帧的推进。M0 的 main.mjs 拆出来的——
// 同一套东西现在有两个驱动：回放（夹具，测试与演示）和实时（应用里的 NativeWebView，C# 推事件）。
//
// 屏幕空间效果（光晕、旁白气泡、建筑与居民的名牌、橄榄树的名牌）必须用"这一帧真正用来渲染的"相机矩阵投影（设计稿 12.2）。
// 每帧的顺序固定为：推进 → 摆好人物和镜头 → 对齐矩阵 → 算屏幕空间位置 → 渲染 → 叠加层。
//
// M2：人物是可动的骨架（art/rig.mjs），动作来自纯函数的姿势（art/poses.mjs）；城里的一切经资产库（art/assets.mjs）造出来。
// 积累：收下的藏品摆在港前三处场馆的底座上（摆在哪由你选）、橄榄林一份委托一棵树、居民随能力出现、委托板三条建议、
// 神庙的每日神谕、传令官的报告、群岛海图——意图都是封闭集合里的那几条，C# 逐条校验；网页从不替人发消息，也没有"批准"。
import * as THREE from '../vendor/three.bundle.mjs';
import { OrbitControls } from '../vendor/three.bundle.mjs';
import { createLayout, pointAlong, PLAYER_SPEED } from './layout.mjs';
import { cityIndex, siteForTool } from './replay.mjs';
import { createOrchestrator, stepProgress } from './orchestrator.mjs';
import { lineForVisit, pick, LINES_ZH, PLACE_ZH } from './narration.mjs';
import { createRenderer } from './render.mjs';
import { createOverlay } from './overlay.mjs';
import { makeMaterials } from './art/materials.mjs';
import { createAssetLibrary } from './art/assets.mjs';
import { workVariantFor } from './art/characters.mjs';
import { buildWorld, PUBLIC_NAMES } from './world.mjs';
import { hashSeed } from './art/noise.mjs';
import { framePolicy, transitionArrival } from './live.mjs';
import { islandLayout, chartTransform, hitIsland, SLOTS_PER_VENUE } from './civic-layout.mjs';

const CAMERA = { distance: 300, elevation: 33, azimuth: 45 };
const BUBBLE_MS = 3600;
const BOAT_MS = 7000;
const VOYAGE_FADE_MS = 400;
const REVEAL_MS = 3200;
const PICK_PX = 26;

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
  // M2
  place: '摆放',
  move: '换个地方',
  takeDown: '撤下',
  remove: '移除',
  placeHint: '点一个空底座，把《{title}》摆在那里',
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
  residentTry: '请雅典娜试试',
  chart: '海图',
  chartTitle: '群岛海图',
  chartHint: '一座岛是一座城邦。点一座岛，驶向它最近的那份委托。',
  chartRunning: '有委托在进行',
  chartWaiting: '等你点头',
  chartHere: '你在这里',
  chartMissing: '找不到文件夹',
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
  let assets = createAssetLibrary(M);
  status.assets = assets.report;

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

  // —— 积累（C# 推来的，全部可以从存档与会话重新推导） ——
  let groveTrees = [];         // [{ conversationId, title, index, stage, turns, interrupted, scheduled }]
  let residents = [];          // [{ kind, met, name, intro, tryLabel }]
  let suggestions = [];        // [{ id, line }]
  let oracle = null;           // { exists, title, body, latestConversationId }
  let islands = [];            // 海图
  let panel = null;            // { type: 'resident' | 'board' | 'oracle' | 'venue' | 'chart', … }
  let placing = null;          // 正在摆放的藏品 id
  let hover = null;            // { type: 'tree' | 'resident', id }
  let pendingBoardLine = null; // 奠基揭幕之后雅典娜说的第一条建议

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
  const items = new Map();      // id → { item, object, display, collectedAt, spot, revealAt }
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
  let glide = null;   // 镜头被"请"去某处（摆放时看场馆）：{ to, until }
  controls.addEventListener('start', () => { if (dragButton === 2) { follow = 'free'; glide = null; } lastInputAt = performance.now(); });

  // —— 城的装载：整座重建，或者按座替换 ——
  function loadCity(doc, options = {}) {
    const keepPlayer = options.key != null && options.key === cityKey;
    if (world) world.dispose();
    city = doc;
    cityKey = options.key ?? null;
    sanctuary = !!options.sanctuary;
    layout = createLayout({ ...doc, groveTrees: groveTrees.length });
    world = buildWorld(R.scene, doc, layout, M, { names: publicNames, sanctuary, assets });
    index = cityIndex(doc);
    kindOf = (key) => doc.buildings.find((b) => b.key === key)?.kind ?? null;
    buildingKeys = [...world.buildings.keys()];
    for (const record of items.values()) { record.object = null; record.display = null; }   // 旧城的物件随旧城拆掉
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
      pendingBoardLine = { t: sim.time + REVEAL_MS + 400 };
    }
    applyAccumulation();
    status.city = { buildings: doc.buildings.length, vacant: (doc.vacant ?? []).length, market: (doc.market ?? []).length, sanctuary, key: cityKey };
  }

  /** 积累系统的现状画进这座城：树、居民、委托板的纸条、神谕的三足鼎、藏品的位次。 */
  function applyAccumulation() {
    if (!world) return;
    world.setGrove(groveTrees);
    world.setResidents(residents, (kind) => residents.find((r) => r.kind === kind)?.name ?? kind);
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

  /** 雅典娜在时刻 now 的位置、朝向、光，以及她这一刻的动作（走 / 停 / 做事 / 交付）。 */
  function athenaPose(state, now) {
    const step = state.step;
    const pose = { x: 0, z: 0, yaw: 0, visible: true, glow: 0.22, teleport: null, action: 'idle', t: now, style: {} };
    if (!step) return pose;
    const t = now - step.start;
    if (step.kind === 'walk' || step.kind === 'wander') {
      const p = pointAlong(routeOf(step), ease(stepProgress(step, now)));
      Object.assign(pose, { x: p.x, z: p.z, yaw: p.dir, glow: 0.24, action: 'walk', t });
      return pose;
    }
    if (step.kind === 'teleport') {
      const f = stepProgress(step, now);
      const p = pointAlong(routeOf(step), f);
      const site = f < 0.5 ? layout.sitePoint(safeSite(step.from)) : layout.sitePoint(safeSite(step.to));
      Object.assign(pose, { x: site.x, z: site.z, yaw: facingAt(safeSite(step.to)), visible: f < 0.15 || f > 0.85, glow: 0.55, action: 'idle', t });
      pose.teleport = new THREE.Vector3(p.x, 1.15, p.z);
      return pose;
    }
    const at = safeSite(step.at ?? state.position);
    const p = layout.sitePoint(at);
    Object.assign(pose, { x: p.x, z: p.z, yaw: facingAt(at), t });
    if (step.kind === 'meditate') {
      const level = 0.6 + Math.min(1, (step.reasoning ?? 0) / 1200) * 0.8;
      pose.glow = 0.2 + 0.07 * level * (0.5 + 0.5 * Math.sin(((now - step.start) / 1700) * Math.PI * 2));
      pose.action = 'idle';
    } else if (step.kind === 'await') {
      // 停在门槛前，手中石板的封印是红色的；光收着，不干活
      pose.glow = 0.16;
      pose.action = 'work';
      pose.style = { variant: 'await' };
    } else if (step.kind === 'act') {
      pose.acting = true;
      pose.glow = step.failed ? 0.07 : 0.32;   // 失败：光暗下去
      pose.action = 'work';
      pose.style = { variant: workVariantFor('athena', step.category) };
    } else if (step.kind === 'deliver') {
      pose.glow = 0.34;
      pose.action = 'deliver';
      pose.style = { duration: Math.max(600, (step.end ?? step.start + 1800) - step.start) };
    }
    return pose;
  }

  function applyAthena(pose) {
    const a = world.athena;
    a.position.set(pose.x, 0.012, pose.z);
    a.rotation.y = pose.yaw;
    a.visible = pose.visible;
    world.athenaFigure.pose(pose.action, pose.t, pose.style);
    // 光属于雅典娜：她在哪干活，哪里就亮。做事时她的光挪到身前、照得更远，把所在建筑的正面照亮
    const light = a.userData.light;
    if (!light) return;
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

  // —— 场景里随动作变化的东西：脚手架、船、侍女、居民、交付的成果 ——
  function applyEffects(state, now, wall) {
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
      boat.position.x += Math.sin(out * Math.PI) * 2.5;
      boat.rotation.y = f < 0.5 ? 0 : Math.PI;
    }

    const sub = lastStep((s) => s.kind === 'act' && s.category === 'subagents' && s.start <= now && (s.end == null || now < s.end));
    world.maidens.forEach((m) => { m.root.visible = false; });
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
        const moving = (f > 0.12 && f < 0.45) || (f > 0.7 && f < 0.9);
        m.root.visible = f < 0.97;
        m.root.position.set(around.x + (target2.x - around.x) * out, 0.02, around.z + (target2.z - around.z) * out);
        m.root.scale.setScalar(0.9 * Math.min(1, f / 0.12, (1 - f) / 0.1 + 0.001));
        m.root.rotation.y = Math.atan2(target2.x - around.x, target2.z - around.z) + (f > 0.58 ? Math.PI : 0);
        m.pose(moving ? 'walk' : out >= 1 ? 'work' : 'idle', now - sub.start + k * 137, { variant: 'read', speed: 4, stride: 1.1 });
      }
    }

    animateResidents(wall);

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
        items.set(id, { item: { id, kind: 'scroll', title: pick(lines.deliver, s.turn ?? 0), state: 'pending', local: true }, object: null, display: null, spot: { x: p.x - 0.5, z: p.z - 0.15 }, revealAt: performance.now() });
      }
    }
    placeItems();
  }

  /**
   * 居民：多数时候待在自己的位置上——停一会儿、干一会儿自己的活；每隔一阵沿一小段路走出去再走回来。
   * 节拍按居民的名字错开，满城的人不会同时抬手。
   */
  function animateResidents(wall) {
    for (const r of world.residents.values()) {
      const cycle = 26000;
      const t = (wall + r.phase * 3) % cycle;
      const out = { x: r.home.x + Math.sin(r.home.yaw + 1.2) * 2.4, z: r.home.z + Math.cos(r.home.yaw + 1.2) * 2.4 };
      let action = 'idle', at = t, x = r.home.x, z = r.home.z, yaw = r.home.yaw;
      if (t < 9000) { action = 'idle'; at = t; }
      else if (t < 16000) { action = 'work'; at = t - 9000; }
      else if (t < 18600) {
        const f = (t - 16000) / 2600;
        x = r.home.x + (out.x - r.home.x) * ease(f); z = r.home.z + (out.z - r.home.z) * ease(f);
        yaw = Math.atan2(out.x - r.home.x, out.z - r.home.z); action = 'walk'; at = t - 16000;
      } else if (t < 21400) { action = 'idle'; x = out.x; z = out.z; yaw = Math.atan2(out.x - r.home.x, out.z - r.home.z) + Math.PI * 0.5; at = t; }
      else if (t < 24000) {
        const f = (t - 21400) / 2600;
        x = out.x + (r.home.x - out.x) * ease(f); z = out.z + (r.home.z - out.z) * ease(f);
        yaw = Math.atan2(r.home.x - out.x, r.home.z - out.z); action = 'walk'; at = t - 21400;
      } else { action = 'idle'; at = t; }
      r.figure.root.position.set(x, 0.012, z);
      r.figure.root.rotation.y = yaw;
      r.figure.pose(action, at, { variant: r.work, speed: 1.3, stride: 1.3 });
      r.label.anchor.set(x, (r.figure.root.userData.height ?? 1.75) + 0.55, z);
      r.action = action;
    }
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
    if (world) world.showSlotMarkers(placing != null, () => true);
    if (placing && world) {
      const pulse = 1 + 0.18 * Math.sin(wall / 230);
      for (const v of world.venues.values()) for (const s of v.slots) if (s.marker.visible) s.marker.scale.setScalar(pulse);
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
    // 奠基揭幕之后，雅典娜按委托板给出第一条建议（5.4）
    if (pendingBoardLine && suggestions.length > 0) {
      sim.cues.push({ t: Math.max(sim.time, pendingBoardLine.t), text: suggestions[0].line, minMs: 3400 });
      pendingBoardLine = null;
    }
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
    let action = 'idle', t = wall;
    if (player.route) {
      const f = Math.min(1, (wall - player.start) / (player.length / PLAYER_SPEED * 1000));
      const p = pointAlong(player.route, f);
      player.pos = { x: p.x, z: p.z };
      player.yaw = p.dir;
      action = 'walk';
      t = wall - player.start;
      if (f >= 1) {
        player.route = null;
        // 走到了就记下位置（存档里的"玩家进度"）；走的途中不发
        send('save-player', { x: +player.pos.x.toFixed(2), z: +player.pos.z.toFixed(2), zoom: +camera.zoom.toFixed(2) });
      }
    }
    world.player.position.set(player.pos.x, 0.012, player.pos.z);
    world.player.rotation.y = player.yaw;
    world.playerFigure.pose(action, t, { speed: PLAYER_SPEED, stride: 2.0 });
  }

  function walkPlayerTo(site, wall) {
    const from = `pt:${player.pos.x.toFixed(2)},${player.pos.z.toFixed(2)}`;
    const route = layout.route(from, site);
    player.route = route;
    player.start = wall;
    player.length = route.reduce((d, p, i) => (i ? d + Math.hypot(p.x - route[i - 1].x, p.z - route[i - 1].z) : 0), 0);
  }

  function followCamera(dt, pose, snap = false) {
    if (glide && performance.now() > glide.until) glide = null;
    if (follow === 'free' && !glide) return;
    const want = glide
      ? glide.to.clone()
      : follow === 'player'
        ? new THREE.Vector3(player.pos.x, 1, player.pos.z)
        : pose.teleport ? new THREE.Vector3(pose.teleport.x, 1, pose.teleport.z) : new THREE.Vector3(pose.x, 1, pose.z);
    const k = snap ? 1 : 1 - Math.exp(-dt / 450);
    const delta = want.sub(controls.target).multiplyScalar(k);
    controls.target.add(delta);
    camera.position.add(delta);
  }

  /** 请镜头去看一处（摆放时看场馆）：几秒后交还给原来的跟随方式。 */
  function lookAt(x, z, ms = 4000) {
    glide = { to: new THREE.Vector3(x, 1, z), until: performance.now() + ms };
    if (follow === 'athena') follow = 'free';
  }

  // —— 一帧：推进 → 摆好人物和镜头 → 对齐矩阵 → 算屏幕空间位置 → 渲染 → 叠加层 ——
  const tmp = new THREE.Vector3();
  let used = null;
  let regions = [];
  function frame(now, dt, wall, { snap = false, sync = true, script = null, feed = null, hud = null } = {}) {
    if (!world) {
      overlay.draw({ time: wall, labels: [], bubble: null, edges: [], voyage: voyageFrame(wall), ui, chart: chartView() });
      return null;
    }
    if (feed) feed(sim.orch, now);
    const state = sim.orch.update(now);
    sim.time = now;
    refreshCues();
    updatePlayer(wall);
    const pose = athenaPose(state, now);
    applyAthena(pose);
    applyEffects(state, now, wall);
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
    // 名牌：建筑、公共建筑、市集摊位一直画；居民的只在还没结识、或者指针停在他身上时画（满城的名字太挤）；
    // 橄榄树的只在指针停在那棵树上时画；遗失的藏品在它的底座上挂一块名牌
    const anchored = world.labels.filter((l) => l.kind !== 'resident' || l.unmet || (hover?.type === 'resident' && l.key === `resident:${hover.id}`));
    if (hover?.type === 'tree') {
      const tree = world.groveTrees.find((t) => t.conversationId === hover.id);
      if (tree) anchored.push({ text: tree.title, kind: 'tree', key: `tree:${tree.conversationId}`, anchor: tree.top });
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
    // 点击要用的屏幕位置也在这里算（同一组矩阵）：树、居民、空着的底座
    picks = {
      trees: world.groveTrees.map((t) => ({ id: t.conversationId, ...R.projectCss(t.base) })),
      residents: [...world.residents.values()].map((r) => ({ id: r.kind, ...R.projectCss(tmp.copy(r.figure.root.position).setY(1.0)) })),
      slots: [...world.venues.values()].flatMap((v) => v.slots.map((s) => ({ venue: v.venue, slot: s.index, item: s.item, ...R.projectCss(s.world) }))),
      board: R.projectCss(tmp.copy(world.agora.userData.board).add(world.agora.position)),
    };
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
      panel: panelView(),
      placing: placing ? placingView() : null,
      voyage: voyageFrame(wall),
      chart: chartView(),
      ui,
    });
    status.now = now;
    status.step = state.step?.kind ?? null;
    status.frames = (status.frames ?? 0) + 1;
    status.athenaAction = pose.action;
    return state;
  }
  let picks = { trees: [], residents: [], slots: [], board: null };

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
      placed: !!item.placement, lost: !!item.lost, local: !!item.local,
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
        return { type: 'resident', bust: panel.id, expression: 'speak', title: r.name ?? panel.id, lines: [r.intro ?? ''], tryLabel: r.tryLabel ?? ui.residentTry };
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
        const stored = [...items.values()].filter((r) => r.item.state === 'accepted' && !r.item.placement && !r.item.local).slice(0, 5).map((r) => ({ id: r.item.id, title: r.item.title ?? '' }));
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

  // —— 输入 ——
  const canvas = R.renderer.domElement;
  let downAt = null;
  canvas.addEventListener('pointerdown', (e) => { dragButton = e.button; downAt = { x: e.clientX, y: e.clientY, t: performance.now() }; lastInputAt = performance.now(); });
  canvas.addEventListener('pointermove', (e) => {
    if (downAt || !world) return;
    hover = null;
    const near = (list) => list.reduce((best, p) => {
      const d = Math.hypot(p.x - e.clientX, p.y - e.clientY);
      return d < PICK_PX && (!best || d < best.d) ? { ...p, d } : best;
    }, null);
    const r = near(picks.residents);
    if (r) { hover = { type: 'resident', id: r.id }; return; }
    const t = near(picks.trees);
    if (t) hover = { type: 'tree', id: t.id };
  });
  canvas.addEventListener('pointerup', (e) => {
    const d = downAt;
    downAt = null;
    dragButton = -1;
    lastInputAt = performance.now();
    if (!d || e.button !== 0 || Math.hypot(e.clientX - d.x, e.clientY - d.y) > 6) return;
    click(e.clientX, e.clientY);
  });
  canvas.addEventListener('contextmenu', (e) => e.preventDefault());

  function click(x, y) {
    // 先看叠加层上的按钮（面板画在 canvas 上，按坐标命中）
    const hitId = overlay.hit(x, y);
    if (hitId) { onButton(hitId); return; }
    if (chartOpen) {
      const view = chartView();
      const id = hitIsland(view.layout, view.transform, x, y);
      if (id) selectIsland(id);
      return;
    }
    if (!world || fogInfo || voyage) return;
    const near = (list, radius = PICK_PX) => list.reduce((best, p) => {
      const dist = Math.hypot(p.x - x, p.y - y);
      return dist < radius && (!best || dist < best.d) ? { ...p, d: dist } : best;
    }, null);
    // 摆放：点一个空底座
    if (placing) {
      const slot = near(picks.slots.filter((s) => !s.item), 34);
      if (slot) { placeItem(placing, slot.venue, slot.slot); return; }
    }
    for (const record of items.values()) {
      const object = record.display?.visible ? record.display : record.object;
      if (!object || !object.visible) continue;
      const p = R.projectCss(object.getWorldPosition(new THREE.Vector3()).setY(object === record.display ? 1.0 : 0.2));
      if (Math.hypot(p.x - x, p.y - y) < 28) { card = record.item.id; panel = null; return; }
    }
    // 遗失的藏品：点它的空底座也打开卡片
    const lostSlot = near(picks.slots.filter((s) => s.item && items.get(s.item)?.item.lost), 30);
    if (lostSlot) { card = lostSlot.item; panel = null; return; }
    const resident = near(picks.residents);
    if (resident) { openResident(resident.id); return; }
    const tree = near(picks.trees);
    if (tree) {
      // 点一棵树 = 选中那份委托（走会话树的同一条路径，9.2）
      send('select-conversation', { conversationId: tree.id });
      status.treeSelected = tree.id;
      return;
    }
    if (picks.board && Math.hypot(picks.board.x - x, picks.board.y - y) < 40) { openPanel({ type: 'board' }); return; }
    card = null;
    const ndc = new THREE.Vector2((x / window.innerWidth) * 2 - 1, -(y / window.innerHeight) * 2 + 1);
    const ray = new THREE.Raycaster();
    R.syncMatrices();
    ray.setFromCamera(ndc, camera);
    const hit = ray.ray.intersectPlane(new THREE.Plane(new THREE.Vector3(0, 1, 0), 0), new THREE.Vector3());
    if (!hit) return;
    // 神庙：每日神谕；场馆的北半边（建筑）：场馆面板
    const temple = layout.plots.get('temple');
    if (temple && Math.abs(hit.x - temple.cx) < 6 && Math.abs(hit.z - temple.cz) < 6) { openPanel({ type: 'oracle' }); return; }
    for (const v of world.venues.values()) {
      if (Math.abs(hit.x - v.plot.cx) < 6 && hit.z > v.plot.cz - 6.25 && hit.z < v.plot.cz + 0.8) { openPanel({ type: 'venue', id: v.venue }); return; }
    }
    if (hit.z < layout.quayZ + 2 || layout.onTerrace(hit)) {
      const site = layout.snapClick({ x: hit.x, z: hit.z });
      if (site) walkPlayerTo(site, performance.now());
    }
  }

  function openPanel(next) {
    panel = next;
    card = null;
    status.panel = next.type;
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
      case 'resident-try':
        if (panel?.type === 'resident') send('try-resident', { residentId: panel.id });
        panel = null;
        break;
      case 'oracle-request': send('request-oracle'); panel = null; break;
      case 'oracle-latest':
        if (oracle?.latestConversationId) send('select-conversation', { conversationId: oracle.latestConversationId });
        panel = null;
        break;
      case 'chart-close': chartOpen = false; status.chartOpen = false; break;
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

  function toggleFollow() { follow = follow === 'athena' ? 'player' : 'athena'; glide = null; }

  /** 换一份资产清单（应用推来的覆盖）：先用程序化的把城画出来，外部模型取到了再整城重建一次。 */
  async function setAssetManifest(manifest) {
    assets = createAssetLibrary(M, { manifest });
    status.assets = assets.report;
    if (assets.report.requested.length === 0) return 0;
    const loaded = await assets.prepare();
    status.assets = assets.report;
    if (loaded > 0 && city) loadCity(city, { key: cityKey, sanctuary, now: sim?.time ?? 0 });
    return loaded;
  }

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
    get assets() { return assets; },
    get picks() { return picks; },
    placeCamera,
    loadCity,
    updateCity,
    frame,
    toggleFollow,
    onButton,
    click,
    setAssetManifest,
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
          items.set(item.id, { item, object: null, display: null, spot: null, revealAt: null, collectedAt: null });
        }
      }
      for (const [id, record] of items) {
        if (record.item.local || seen.has(id)) continue;
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
    setGrove(trees) { groveTrees = Array.isArray(trees) ? trees : []; if (world && city) { const before = layout.plots.size; layout = createLayout({ ...city, groveTrees: groveTrees.length }); if (layout.plots.size !== before) { loadCity(city, { key: cityKey, sanctuary, now: sim?.time ?? 0 }); return; } world.layout = layout; applyAccumulation(); } },
    setResidents(list) { residents = Array.isArray(list) ? list.map((r) => ({ ...r })) : []; applyAccumulation(); },
    setBoard(list) { suggestions = Array.isArray(list) ? list.filter((s) => s && s.id && s.line) : []; applyAccumulation(); status.board = suggestions.map((s) => s.id); },
    setOracle(info) { oracle = info ?? null; applyAccumulation(); status.oracle = !!oracle?.exists; },
    setIslands(list) { islands = Array.isArray(list) ? list : []; status.islands = islands.map((i) => i.id); },
    toggleChart(open = !chartOpen) { chartOpen = !!open; status.chartOpen = chartOpen; if (chartOpen) { panel = null; card = null; } },
    get chartOpen() { return chartOpen; },
    openPanel(type, id = null) { openPanel({ type, id }); },
    /** 指针停在哪（测试与运动自检用：停在一棵树上，树的名牌就出现，它同样按当帧矩阵投影）。 */
    setHover(type, id) { hover = type ? { type, id } : null; },
    openResident,
    startPlacing,
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
      return framePolicy({ visible, step: sim?.orch.state(sim.time).step?.kind ?? null, playerMoving: player.route != null, transitioning: voyage != null || reveal != null || glide != null, now: performance.now(), lastInputAt, residents: world?.residents.size ?? 0 });
    },
    resize() { R.resize(); overlay.resize(); },
    collectAll() { for (const id of items.keys()) { card = id; decide('accepted'); } },
    openCard(id) { if (items.has(id)) card = id; },
  };
}
