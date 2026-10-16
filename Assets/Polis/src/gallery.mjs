// 美术审查（M2 完成标准 c）：每件资产单独画一张对照图——游戏视距（镜头缩放 1，人物约 50 像素高）和中景
// （缩放 2.6，人物约 130 像素）各一张；人物的每个动作另截一张连续帧（上一行游戏视距、下一行中景）。
// 和城里同一套渲染（正交相机、环境光遮蔽、黑白调色、点缀色遮罩、颗粒），同一个太阳——光照是不是统一，在这里一眼看得出。
//   index.html?gallery=building.workshop&view=game&variant=weaver&state=bustling&size=2
//   index.html?gallery=figure.athena&action=walk&frames=8
//   index.html?gallery=bust:athena
//   index.html?gallery=citizen:12345&age=old&action=walk&frames=8   （一位居民，长相由种子定：art/citizens.mjs）
//   index.html?gallery=citizen:12345&trade=potter&action=work&frames=8 （他那门手艺：家什按职业、要工位的摆在他面前，art/crafts.mjs）
//   index.html?gallery=citizens&n=10&from=0&age=mixed&view=mid      （一排居民，种子与 demo.mjs 的编出来的居民相同；turn=-1.2 看侧影）
//   index.html?gallery=citizen-busts&n=12&from=0&age=mixed           （居民的半身像，对话面板里的实际大小）
//   index.html?gallery=figure.athena&action=walk&frames=8&manifest={"figure.athena":{"model":"athena.glb","scale":1.62,"rig":"procedural"}}
// 建筑、器物、植物旁边站一个灰色的你（玩家），用来看比例。渲染完 document.title = 'gallery-ready'。
import * as THREE from '../vendor/three.bundle.mjs';
import { createRenderer } from './render.mjs';
import { makeMaterials } from './art/materials.mjs';
import { createAssetLibrary } from './art/assets.mjs';
import { groundPlane, Kit, m4 } from './art/kit.mjs';
import { rng, hashSeed } from './art/noise.mjs';
import { workVariantFor, buildCitizen } from './art/characters.mjs';
import { walkPeriod, ONE_SHOT_MS } from './art/poses.mjs';
import { drawBust, EXPRESSIONS, CHARACTERS } from './art/busts.mjs';
import { citizenLook } from './art/citizens.mjs';
import { craftFor, STATION_GAPS, CRAFT_REFERENCE_HEIGHT } from './art/crafts.mjs';

const VIEWS = { game: 1, mid: 2.6 };

export async function runGallery({ params, status }) {
  const id = params.get('gallery');
  if (id === 'list') return listAssets(status);
  if (id.startsWith('bust:')) return bustSheet(id.slice(5), status);
  if (id === 'citizen-busts') return citizenBustSheet(params, status);
  const R = createRenderer({ canvas: document.getElementById('scene') });
  const M = makeMaterials();
  // &manifest=<JSON>：按一份资产清单（Id → 条目）换上外部模型再画（审查外部模型、蒙在程序化骨架上的人物用；模型从 /polis-assets/ 取）
  const manifest = params.get('manifest') ? JSON.parse(params.get('manifest')) : null;
  const assets = createAssetLibrary(M, { manifest });
  if (manifest) {
    await assets.prepare();
    status.assets = assets.report;
  }
  // 居民（M-B）不在资产表里：citizen:<种子> 按种子现建一位（与城里同一个 buildCitizen），citizens 是一排
  const citizenSeed = id.startsWith('citizen:') ? Number(id.slice('citizen:'.length)) >>> 0 : null;
  const entry = citizenSeed != null ? { id, kind: 'figure', label: `居民 ${citizenSeed}` }
    : id === 'citizens' ? { id, kind: 'lineup', label: '居民一排' }
    : assets.registry.get(id);
  if (!entry) throw new Error(`gallery: unknown asset '${id}'`);
  R.scene.background = new THREE.Color(0xd2cdc5);
  // &night=1：夜里的灯光与调色（审查夜景用）
  if (params.get('night') === '1') R.setNight(1);

  // 一块中性的铺地：圆形石板放在土地上（城里的地面就是这两样）
  const ground = new THREE.Group();
  const land = new THREE.Mesh(groundPlane(160, 160, 15), M.earth);
  land.receiveShadow = true;
  ground.add(land);
  const disc = new THREE.Mesh(new THREE.CircleGeometry(entry.kind === 'building' || entry.kind === 'civic' ? 15 : 6, 48).rotateX(-Math.PI / 2), M.pave);
  disc.position.y = 0.01;
  disc.receiveShadow = true;
  ground.add(disc);
  R.scene.add(ground);

  const camera = R.camera;
  const view = params.get('view') ?? 'game';
  const center = new THREE.Vector3(0, entry.kind === 'figure' ? 0.9 : 1.5, 0);
  // &az= &el=：审查用的机位（方位角、仰角，度；缺省是城里的 45° / 33°）——看裙摆有没有把腿露出来要从正面、侧面各看一遍
  const azimuth = Number(params.get('az') ?? 45), elevation = Number(params.get('el') ?? 33);
  const place = (zoom, c = center) => {
    camera.zoom = zoom;
    camera.updateProjectionMatrix();
    camera.position.copy(c).add(new THREE.Vector3().setFromSphericalCoords(300, THREE.MathUtils.degToRad(90 - elevation), THREE.MathUtils.degToRad(azimuth)));
    camera.lookAt(c);
    R.placeSun(c);
    R.syncMatrices();
  };

  if (entry.kind === 'lineup') return citizenLineup({ R, M, params, status, place, center, view, azimuth });

  if (entry.kind === 'figure') {
    const citizenSpec = citizenSeed != null ? citizenLook(citizenSeed, { age: params.get('age') ?? 'adult', trade: params.get('trade') }).spec : null;
    const figure = citizenSpec ? buildCitizen(M, citizenSpec) : assets.figure(id);
    R.scene.add(figure.root);
    // 居民干活（第三轮需求 4）：他那门手艺；要工位的，工位摆在他面前（和城里一样，正面朝着他）
    const craft = citizenSeed != null ? craftFor(params.get('trade'), citizenSeed) : null;
    const working = (params.get('action') ?? params.get('pose')) === 'work';
    if (craft && working && craft.station) {
      const station = assets.object(`prop.station-${craft.station}`);
      const k = citizenSpec.height / CRAFT_REFERENCE_HEIGHT;
      station.position.set(0, 0, STATION_GAPS[craft.station] * k);
      station.scale.setScalar(k);
      R.scene.add(station);
    }
    R.gradePass.uniforms.uGlowStrength.value = 0;
    // &weights=1：蒙在程序化骨架上的外部人物按蒙皮权重上色（每根骨头一种颜色，按权重混合；不去色）——查权重分错了地方用
    if (params.get('weights') === '1' && figure.skinned) showWeights(R, figure);
    window.__polisGallery = { figure, R };
    const action = params.get('action');
    const frames = Number(params.get('frames') ?? 0);
    const who = id.slice('figure.'.length);
    const variant = params.get('variant') ?? (craft ? craft.variant : workVariantFor(who, params.get('category') ?? 'write'));
    if (action && frames > 0) return actionSheet({ R, figure, who, action, frames, variant, status, place, entry, closeZoom: Number(params.get('zoom')) || 0 });
    const still = params.get('pose') ?? 'idle';
    figure.pose(still, Number(params.get('t') ?? 1200), { variant, duration: 1800 });
    // &zoom=：审查用的近看（比中景更近，只为找问题；对照图本身只有游戏视距与中景两种）
    const zoomOverride = Number(params.get('zoom'));
    if (zoomOverride > 0) center.set(0, Number(params.get('cy') ?? 1.6), 0);
    place(zoomOverride > 0 ? zoomOverride : VIEWS[view] ?? 1);
    // 和城里一样：雅典娜只在干活和交付时有光（game.mjs 的 athenaPose）
    if (who === 'athena' && (still === 'work' || still === 'deliver')) glowAt(R, figure);
    R.render();
    caption(`${entry.label} · ${id} · ${view === 'mid' ? '中景' : '游戏视距'}`);
    status.gallery = { id, view, kind: 'figure', height: figure.root.userData.height, clip: clipOf(R, visibleBox(figure.root), view === 'mid' ? 30 : 24) };
    status.ready = true;
    document.title = 'gallery-ready';
    return;
  }

  // 建筑：可以指定子型、状态、体量
  const params2 = {};
  if (entry.kind === 'building') {
    const kind = id.slice('building.'.length);
    params2.building = {
      key: params.get('key') ?? `审查-${kind}`,
      kind,
      sizeClass: Number(params.get('size') ?? 2),
      state: params.get('state') ?? 'lived',
      incomplete: params.get('incomplete') === '1',
      subtype: params.get('variant') ?? undefined,
    };
    params2.rand = rng(hashSeed(params2.building.key));
  } else if (id === 'plant.olive') {
    params2.stage = params.get('variant') ?? 'grown';
    params2.seed = Number(params.get('seed') ?? 1);
  } else if (id === 'civic.market') {
    params2.stalls = 6;
  } else if (id === 'civic.harbor') {
    Object.assign(params2, { quayLocalZ: 6, pierLength: 14, plotCenterZ: 0 });
  }
  // 光晕属于雅典娜：别的资产的对照图里不能有一团默认位置的光
  R.gradePass.uniforms.uGlowStrength.value = 0;
  const object = assets.object(id, params2);
  if (id.startsWith('prop.display-')) {
    const pedestal = assets.object('prop.pedestal');
    R.scene.add(pedestal);
  }
  R.scene.add(object);
  // 船在城里浮在水面上，船身在地面以下；对照图里没有水，把它抬到船底着地（像搁在船台上），船身才看得见
  if (id === 'civic.boat') object.position.y = -new THREE.Box3().setFromObject(object).min.y;
  // 比例：旁边站一个灰色的你
  const box = new THREE.Box3().setFromObject(object);
  const scaleFigure = assets.figure('figure.player');
  scaleFigure.pose('idle', 800, {});
  scaleFigure.root.position.set(Math.max(1.2, box.max.x + 1.0), 0, Math.max(0.5, box.max.z * 0.4));
  scaleFigure.root.rotation.y = -0.5;
  R.scene.add(scaleFigure.root);
  const height = Number.isFinite(box.max.y) ? box.max.y : 2;
  center.set((box.min.x + box.max.x) / 2 + 0.6, Math.min(4, height / 2), (box.min.z + box.max.z) / 2);
  const zoom = (VIEWS[view] ?? 1) * (entry.kind === 'prop' && view === 'mid' ? 1.6 : 1);
  place(zoom);
  R.render();
  caption(`${entry.label} · ${id}${params.get('variant') ? ` · ${params.get('variant')}` : ''}${params.get('state') ? ` · ${params.get('state')}` : ''} · ${view === 'mid' ? '中景' : '游戏视距'}`);
  const bounds = visibleBox(object).union(visibleBox(scaleFigure.root));
  status.gallery = { id, view, kind: entry.kind, height, subtype: object.userData.subtype ?? null, clip: clipOf(R, bounds, 26) };
  status.ready = true;
  document.title = 'gallery-ready';
}

/** 蒙皮权重的颜色：每根骨头一种（左右同色系、左亮右暗），顶点按四个权重混合。 */
const BONE_COLORS = {
  hips: 0x8a8a8a, spine: 0xd8d8d8, head: 0xf2e6a0, skirt: 0x7a3fb0,
  shoulderL: 0xff6b35, elbowL: 0xffb000, handL: 0xfff200, shoulderR: 0xa83a1a, elbowR: 0xa87400, handR: 0xa8a000,
  legL: 0x2f8cff, kneeL: 0x2fffd0, legR: 0x1a4a8a, kneeR: 0x1a8a72,
};
function showWeights(R, figure) {
  R.gradePass.uniforms.uGrade.value = 0;
  const bones = figure.skeleton.bones.map((b) => Object.keys(figure.joints).find((k) => figure.joints[k] === b));
  for (const mesh of figure.skinned) {
    const g = mesh.geometry.clone();
    const si = g.attributes.skinIndex, sw = g.attributes.skinWeight;
    const colors = new Float32Array(si.count * 3);
    const c = new THREE.Color();
    for (let i = 0; i < si.count; i++) {
      let r = 0, gr = 0, b = 0;
      for (let q = 0; q < 4; q++) {
        c.setHex(BONE_COLORS[bones[si.getComponent(i, q)]] ?? 0xff00ff);
        const w = sw.getComponent(i, q);
        r += c.r * w; gr += c.g * w; b += c.b * w;
      }
      colors.set([r, gr, b], i * 3);
    }
    g.setAttribute('color', new THREE.Float32BufferAttribute(colors, 3));
    mesh.geometry = g;
    mesh.material = new THREE.MeshLambertMaterial({ vertexColors: true });
  }
}

/** 雅典娜的光晕：和城里一样，按当帧矩阵把胸口投到屏幕上，半径随她在屏幕上的身高。 */
function glowAt(R, figure) {
  figure.root.updateMatrixWorld(true);
  const chest = figure.root.userData.chest.clone().applyMatrix4(figure.root.matrixWorld);
  const uv = R.projectUv(chest);
  const feet = R.projectUv(chest.clone().setY(0)), top = R.projectUv(chest.clone().setY(1.9));
  const g = R.gradePass.uniforms;
  g.uGlowPos.value.set(uv.x, uv.y);
  g.uGlowRadius.value = Math.max(0.008, Math.abs(top.y - feet.y) * 0.75);
  g.uGlowStrength.value = 0.22;
}

/** 只算看得见的网格的包围盒（人物手里没拿出来的小件是隐藏的，不该把截图撑大）。 */
function visibleBox(object) {
  const box = new THREE.Box3();
  object.updateMatrixWorld(true);
  object.traverseVisible((o) => { if (o.isMesh || o.isInstancedMesh) box.expandByObject(o, false); });
  return box;
}

/** 资产在屏幕上的外接矩形（包围盒的八个角投影到 CSS 像素）+ 边距：截图只截这一块，审查时按真实像素看。 */
function clipOf(R, box, margin) {
  const xs = [], ys = [];
  for (const x of [box.min.x, box.max.x]) for (const y of [box.min.y, box.max.y]) for (const z of [box.min.z, box.max.z]) {
    const p = R.projectCss(new THREE.Vector3(x, y, z));
    xs.push(p.x); ys.push(p.y);
  }
  const x0 = Math.max(0, Math.floor(Math.min(...xs) - margin)), y0 = Math.max(0, Math.floor(Math.min(...ys) - margin));
  const x1 = Math.min(window.innerWidth, Math.ceil(Math.max(...xs) + margin)), y1 = Math.min(window.innerHeight, Math.ceil(Math.max(...ys) + margin));
  return { x: x0, y: y0, width: Math.max(40, x1 - x0), height: Math.max(40, y1 - y0) };
}

/** 资产清单：每件资产的 Id、种类、中文名，以及要逐个渲染的变体（建筑的子型、橄榄的树龄、人物的动作）。 */
function listAssets(status) {
  const M = makeMaterials();
  const assets = createAssetLibrary(M);
  status.list = [...assets.registry.values()].map((e) => ({ id: e.id, kind: e.kind, label: e.label }));
  status.ready = true;
  document.title = 'gallery-ready';
}

function caption(text) {
  const overlay = document.getElementById('overlay');
  const ctx = overlay.getContext('2d');
  const dpr = Math.min(window.devicePixelRatio || 1, 2);
  overlay.width = window.innerWidth * dpr;
  overlay.height = window.innerHeight * dpr;
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  ctx.font = '600 15px "PingFang SC", "Noto Sans CJK SC", sans-serif';
  ctx.fillStyle = 'rgba(30,28,26,0.7)';
  const w = ctx.measureText(text).width + 24;
  ctx.fillRect(12, 12, w, 30);
  ctx.fillStyle = '#f4f1ec';
  ctx.textBaseline = 'middle';
  ctx.fillText(text, 24, 27);
}

/**
 * 一个动作的连续帧：上一行游戏视距（人物的真实像素大小），下一行中景。
 * closeZoom（&zoom=）：下一行换成这个缩放的近景，格子按比例放大——查衣摆、手里的家什这种细处用。
 */
function actionSheet({ R, figure, who, action, frames, variant, status, place, entry, closeZoom = 0 }) {
  const canvas = R.renderer.domElement;
  const ratio = R.renderer.getPixelRatio();
  const sheet = document.createElement('canvas');
  const midZoom = closeZoom > 0 ? closeZoom : 2.6;
  const grow = midZoom / 2.6;
  const tile = { game: [120, 120], mid: [Math.round(210 * grow), Math.round(280 * grow)] };
  sheet.width = frames * tile.mid[0];
  sheet.height = 46 + tile.game[1] + 12 + tile.mid[1] + 10;
  const ctx = sheet.getContext('2d');
  ctx.fillStyle = '#d2cdc5';
  ctx.fillRect(0, 0, sheet.width, sheet.height);
  const running = action === 'run';
  const style = { variant, duration: 1800, speed: who === 'player' ? (running ? 10.5 : 6) : who === 'athena' ? 3.2 : (running ? 4 : 1.3), stride: who === 'player' ? (running ? 2.2 : 2.0) : who === 'athena' ? 1.6 : (running ? 2.0 : 1.3), speaker: true };
  const cycle = action === 'walk' || action === 'run' ? walkPeriod(style)
    : action === 'deliver' ? 1800 : action === 'idle' ? 9000 : action in ONE_SHOT_MS ? ONE_SHOT_MS[action] : action === 'talk' ? 5200 : 1800;
  const oneShot = action === 'deliver' || action in ONE_SHOT_MS;
  const times = Array.from({ length: frames }, (_, k) => (cycle * k) / (oneShot ? frames - 1 : frames));
  // M3：过渡与弹簧骨要有时间——每一帧之前按 16 毫秒一步把人物推进到那一刻（衣摆才会跟着动作晃）；
  // 走和跑先让人走出一段，衣摆进入稳定的滞后再截
  let clock = 0;
  const advanceTo = (target) => {
    while (clock < target) {
      clock = Math.min(target, clock + 16);
      if (action === 'walk' || action === 'run') figure.root.position.z += (style.speed * 16) / 1000;
      figure.pose(action, clock, { ...style, clock: 1_000_000 + clock });
    }
  };
  if (action === 'walk' || action === 'run') { const lead = cycle * 3; for (let i = 0; i < frames; i++) times[i] += lead; }
  const target = new THREE.Vector3(0, 0.9, 0);
  // 每一帧先把人物推进到那一刻（一次），再按游戏视距与中景各截一张；镜头跟着人物（走和跑的几帧里人物在往前走，衣摆才有滞后）
  for (let k = 0; k < frames; k++) {
    advanceTo(times[k]);
    target.set(figure.root.position.x, 0.9, figure.root.position.z);
    for (const [row, zoom] of [['game', 1], ['mid', midZoom]]) {
      const [tw, th] = tile[row];
      place(zoom, target);
      if (who === 'athena' && (action === 'work' || action === 'deliver')) glowAt(R, figure);
      R.render();
      const c = target.clone().project(R.camera);
      const cx = ((c.x + 1) / 2) * window.innerWidth, cy = ((1 - c.y) / 2) * window.innerHeight;
      const sw = tw, sh = th;
      const sx = cx - sw / 2, sy = cy - sh * 0.55;
      const dx = k * tile.mid[0] + (tile.mid[0] - tw) / 2;
      const dy = row === 'game' ? 46 : 46 + tile.game[1] + 12;
      ctx.drawImage(canvas, sx * ratio, sy * ratio, sw * ratio, sh * ratio, dx, dy, tw, th);
      ctx.fillStyle = 'rgba(30,28,26,0.55)';
      ctx.font = '11px "PingFang SC", sans-serif';
      ctx.fillText(`${Math.round(times[k])} ms`, dx + 4, dy + 12);
    }
  }
  ctx.fillStyle = '#26221f';
  ctx.font = '600 16px "PingFang SC", "Noto Sans CJK SC", sans-serif';
  ctx.fillText(`${entry.label} · ${action}${action === 'work' ? `（${variant}）` : ''} · 上：游戏视距 · 下：中景 · 每帧间隔 ${Math.round(times[1] - times[0])} ms`, 12, 28);
  canvas.style.display = 'none';
  document.getElementById('overlay').style.display = 'none';
  sheet.style.position = 'absolute';
  sheet.style.left = '0'; sheet.style.top = '0';
  document.body.appendChild(sheet);
  status.gallery = { id: entry.id, action, frames, variant, size: [sheet.width, sheet.height] };
  status.ready = true;
  document.title = 'gallery-ready';
}

/** 与 demo.mjs 的 demoCitizens 同一个种子：第 i 位永远是同一个人，最低位交替男女。 */
function lineupSeed(i) { return ((Math.imul(i + 1, 2654435761) & ~1) | (i & 1)) >>> 0; }
const LINEUP_AGES = ['young', 'adult', 'old'];
function lineupAge(params, i) { const a = params.get('age') ?? 'mixed'; return a === 'mixed' ? LINEUP_AGES[i % 3] : a; }

/** 一排居民（M-B 的美术审查）：同一个镜头下比身高、衣服、发式、胡子；每人脚下写种子与年纪。 */
function citizenLineup({ R, M, params, status, place, center, view, azimuth }) {
  const n = Math.max(1, Math.min(16, Number(params.get('n') ?? 10)));
  const from = Math.max(0, Number(params.get('from') ?? 0));
  const gap = 0.95;
  const theta = THREE.MathUtils.degToRad(azimuth);
  const box = new THREE.Box3();
  const people = [];
  R.gradePass.uniforms.uGlowStrength.value = 0;
  for (let k = 0; k < n; k++) {
    const i = from + k;
    const seed = lineupSeed(i);
    const age = lineupAge(params, i);
    const look = citizenLook(seed, { age });
    const figure = buildCitizen(M, look.spec);
    // 排成一横排（垂直于视线），人人朝着镜头偏一点（四分之三侧）；facing=side 看侧面
    const offset = (k - (n - 1) / 2) * gap;
    figure.root.position.set(Math.cos(theta) * offset, 0, -Math.sin(theta) * offset);
    figure.root.rotation.y = theta + (params.get('facing') === 'side' ? Math.PI / 2 : Number(params.get('turn') ?? 0.35));
    figure.pose(params.get('pose') ?? 'idle', 1200 + k * 777, { duration: 1800, clock: 1_000_000 + k * 777 });
    R.scene.add(figure.root);
    box.union(visibleBox(figure.root));
    people.push({ i, seed, age, garment: look.spec.garment, hair: look.spec.hair, headgear: look.spec.headgear ?? null, beard: look.spec.beard ?? null, height: look.spec.height, scroll: look.spec.props.length > 0 });
  }
  const zoom = Number(params.get('zoom')) > 0 ? Number(params.get('zoom')) : view === 'mid' ? 1.6 : 1;
  center.set(0, 0.9, 0);
  place(zoom, center);
  R.render();
  caption(`居民一排 · 第 ${from}–${from + n - 1} 位 · ${params.get('age') ?? 'mixed'} · ${view === 'mid' ? '中景' : '游戏视距'}`);
  const overlay = document.getElementById('overlay');
  const ctx = overlay.getContext('2d');
  const dpr = overlay.width / window.innerWidth;
  ctx.save();
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  ctx.font = '10px "PingFang SC", sans-serif';
  ctx.fillStyle = 'rgba(30,28,26,0.75)';
  ctx.textAlign = 'center';
  for (const [k, p] of people.entries()) {
    const offset = (k - (n - 1) / 2) * gap;
    const at = R.projectCss(new THREE.Vector3(Math.cos(theta) * offset, -0.12, -Math.sin(theta) * offset));
    ctx.fillText(`#${p.i} ${p.age}`, at.x, at.y + 4);
  }
  ctx.restore();
  status.gallery = { id: 'citizens', view, kind: 'lineup', people, clip: clipOf(R, box, 40) };
  status.ready = true;
  document.title = 'gallery-ready';
}

/** 居民的半身像（M-B）：对话面板里的实际大小（132 像素）一排排列开，大的一行看线条。 */
function citizenBustSheet(params, status) {
  const n = Math.max(1, Math.min(24, Number(params.get('n') ?? 12)));
  const from = Math.max(0, Number(params.get('from') ?? 0));
  const perRow = 8;
  const sheet = document.createElement('canvas');
  sheet.width = 40 + perRow * 150;
  sheet.height = 70 + 360 + Math.ceil(n / perRow) * 190;
  const ctx = sheet.getContext('2d');
  ctx.fillStyle = '#d2cdc5';
  ctx.fillRect(0, 0, sheet.width, sheet.height);
  const looks = [];
  for (let k = 0; k < n; k++) {
    const i = from + k;
    const look = citizenLook(lineupSeed(i), { age: lineupAge(params, i) });
    looks.push({ i, look });
  }
  // 大的：前四位，三种神情里的 speak（卡片上用的那一种）
  looks.slice(0, 4).forEach(({ i, look }, k) => {
    drawBust(ctx, look.bust, 'speak', 40 + k * 300, 60, 340);
    ctx.fillStyle = '#26221f';
    ctx.font = '12px "PingFang SC", sans-serif';
    ctx.fillText(`#${i} ${look.bust.hair}/${look.bust.headgear ?? '-'}/${look.bust.garment}/${look.bust.beard ?? '-'}/${look.bust.age}`, 40 + k * 300, 415);
  });
  looks.forEach(({ i, look }, k) => {
    const x = 40 + (k % perRow) * 150, y = 440 + Math.floor(k / perRow) * 190;
    drawBust(ctx, look.bust, 'speak', x, y, 132);
    ctx.fillStyle = '#26221f';
    ctx.font = '11px "PingFang SC", sans-serif';
    ctx.fillText(`#${i} ${look.bust.age}`, x, y + 150);
  });
  ctx.font = '600 16px "PingFang SC", "Noto Sans CJK SC", sans-serif';
  ctx.fillText(`居民的半身像 · 第 ${from}–${from + n - 1} 位 · 上：放大看线条 · 下：对话面板里的实际大小（132 像素）`, 40, 30);
  document.getElementById('scene').style.display = 'none';
  document.getElementById('overlay').style.display = 'none';
  sheet.style.position = 'absolute';
  sheet.style.left = '0'; sheet.style.top = '0';
  document.body.appendChild(sheet);
  status.gallery = { id: 'citizen-busts', n, from };
  status.ready = true;
  document.title = 'gallery-ready';
}

/** 一个人物的半身像：三种神情，大（审查线条）与小（对话面板里实际的大小）各一排。 */
function bustSheet(character, status) {
  if (!CHARACTERS[character]) throw new Error(`gallery: unknown bust '${character}'`);
  const sheet = document.createElement('canvas');
  sheet.width = 1200; sheet.height = 640;
  const ctx = sheet.getContext('2d');
  ctx.fillStyle = '#d2cdc5';
  ctx.fillRect(0, 0, sheet.width, sheet.height);
  EXPRESSIONS.forEach((e, i) => {
    drawBust(ctx, character, e, 40 + i * 300, 50, 360);
    drawBust(ctx, character, e, 40 + i * 300 + 60, 440, 132);
    ctx.fillStyle = '#26221f';
    ctx.font = '13px "PingFang SC", sans-serif';
    ctx.fillText(e, 40 + i * 300, 430);
  });
  ctx.font = '600 16px "PingFang SC", "Noto Sans CJK SC", sans-serif';
  ctx.fillText(`半身像 · ${character} · 上：放大看线条 · 下：对话面板里的实际大小（132 像素）`, 40, 30);
  document.getElementById('scene').style.display = 'none';
  document.getElementById('overlay').style.display = 'none';
  sheet.style.position = 'absolute';
  sheet.style.left = '0'; sheet.style.top = '0';
  document.body.appendChild(sheet);
  status.gallery = { id: `bust:${character}` };
  status.ready = true;
  document.title = 'gallery-ready';
}

export { Kit, m4 };
