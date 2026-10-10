// 器物：全部由代码生成（车削体、盒子、管子）。陶器按打样的轮廓车出来，再按瓶身比例套几道黑釉带——
// 整个世界会被调成黑白，红陶与黑釉去色后是中灰与近黑，纹饰读得出来。点缀色从不用在器物上（它只给"轮到你了"）。
// 每个函数返回一个 Group，原点在器物底面中心，正面朝 +z。
import * as THREE from '../../vendor/three.bundle.mjs';
import { mergeGeometries } from '../../vendor/three.bundle.mjs';
import { Kit, m4, shadowed } from './kit.mjs';
import { valueNoise, fbm } from './noise.mjs';

const lathe = (pts, segments = 20) => new THREE.LatheGeometry(pts.map(([r, y]) => new THREE.Vector2(r, y)), segments);
const mesh = (g, m) => shadowed(new THREE.Mesh(g, m));

/** 釉带：在车削体高度 y0..y1 之间套一圈略大一点的薄壳，半径按轮廓取。 */
function bands(kit, mat, profile, ranges, puff = 1.012) {
  for (const [y0, y1] of ranges) {
    const pts = profile.filter(([, y]) => y >= y0 - 1e-6 && y <= y1 + 1e-6).map(([r, y]) => [r * puff + 0.002, y]);
    if (pts.length >= 2) kit.add(mat, lathe(pts, 24));
  }
}

function handle(kit, mat, from, to, bulge, thickness = 0.016) {
  const a = new THREE.Vector3(...from), b = new THREE.Vector3(...to);
  const mid = a.clone().lerp(b, 0.5).add(new THREE.Vector3(...bulge));
  kit.add(mat, new THREE.TubeGeometry(new THREE.QuadraticBezierCurve3(a, mid, b), 10, thickness, 6, false));
}

// —— 瓶身轮廓（打样的双耳瓶，高约 0.82） ——
const AMPHORA = [[0.0, 0.0], [0.085, 0.0], [0.09, 0.016], [0.066, 0.04], [0.058, 0.07], [0.1, 0.14], [0.17, 0.25], [0.205, 0.36],
  [0.21, 0.44], [0.195, 0.52], [0.16, 0.6], [0.11, 0.65], [0.082, 0.68], [0.075, 0.72], [0.078, 0.78], [0.098, 0.8], [0.1, 0.82], [0.085, 0.825], [0.07, 0.8]];

const amphoraCache = { g: null };
/** 双耳瓶的瓶身（去掉把手）：建筑门口、货堆里成排摆的那种，共用一份几何。 */
export function amphoraBody() {
  if (!amphoraCache.g) amphoraCache.g = lathe(AMPHORA, 14);
  return amphoraCache.g;
}

/** 双耳瓶（完整）：瓶身、两只把手、肩上和腹部的黑釉带。 */
export function buildAmphora(M) {
  const kit = new Kit();
  kit.add(M.terracotta, lathe(AMPHORA, 24));
  bands(kit, M.glaze, AMPHORA, [[0.58, 0.68], [0.36, 0.44], [0.0, 0.07]]);
  for (const sx of [-1, 1]) handle(kit, M.terracotta, [sx * 0.08, 0.76, 0], [sx * 0.16, 0.6, 0], [sx * 0.07, 0.06, 0], 0.018);
  return kit.build();
}

/** 水罐（hydria）：腹宽、肩平，两侧两只横把、背后一只竖把。 */
export function buildHydria(M) {
  const P = [[0, 0], [0.11, 0], [0.12, 0.02], [0.09, 0.05], [0.12, 0.12], [0.2, 0.24], [0.235, 0.34], [0.22, 0.42], [0.17, 0.48], [0.1, 0.52], [0.075, 0.56], [0.08, 0.62], [0.11, 0.64], [0.1, 0.66], [0.07, 0.63]];
  const kit = new Kit();
  kit.add(M.terracotta, lathe(P, 24));
  bands(kit, M.glaze, P, [[0.24, 0.42]]);
  for (const sx of [-1, 1]) handle(kit, M.terracotta, [sx * 0.2, 0.32, 0.02], [sx * 0.2, 0.32, -0.02], [sx * 0.08, 0, 0], 0.014);
  handle(kit, M.terracotta, [0, 0.6, -0.08], [0, 0.42, -0.2], [0, 0.04, -0.1], 0.016);
  return kit.build();
}

/** 调酒缸（krater）：口宽的钟形大缸立在圈足上，两只把手在下腹。 */
export function buildKrater(M) {
  const P = [[0, 0], [0.13, 0], [0.13, 0.03], [0.07, 0.06], [0.07, 0.11], [0.14, 0.16], [0.22, 0.27], [0.27, 0.4], [0.3, 0.5], [0.31, 0.52], [0.29, 0.52], [0.26, 0.42]];
  const kit = new Kit();
  kit.add(M.terracotta, lathe(P, 28));
  bands(kit, M.glaze, P, [[0.16, 0.3], [0.48, 0.52]]);
  for (const sx of [-1, 1]) handle(kit, M.terracotta, [sx * 0.2, 0.25, 0.04], [sx * 0.2, 0.25, -0.04], [sx * 0.1, 0.03, 0], 0.016);
  return kit.build();
}

/** 酒杯（kylix）：浅浅的宽碗，高脚，两只水平把手。 */
export function buildKylix(M) {
  const P = [[0, 0], [0.07, 0], [0.07, 0.012], [0.02, 0.03], [0.018, 0.08], [0.06, 0.1], [0.16, 0.13], [0.2, 0.15], [0.19, 0.152], [0.06, 0.12]];
  const kit = new Kit();
  kit.add(M.glaze, lathe(P, 24));
  for (const sx of [-1, 1]) handle(kit, M.glaze, [sx * 0.17, 0.135, 0.03], [sx * 0.17, 0.135, -0.03], [sx * 0.06, 0.01, 0], 0.01);
  return kit.build();
}

/** 酒壶（oinochoe）：一只把手、三叶形的口（用压扁的壶口近似）。 */
export function buildOinochoe(M) {
  const P = [[0, 0], [0.07, 0], [0.075, 0.015], [0.06, 0.03], [0.1, 0.08], [0.13, 0.15], [0.12, 0.22], [0.08, 0.27], [0.05, 0.3], [0.05, 0.34], [0.07, 0.36], [0.065, 0.37], [0.04, 0.35]];
  const kit = new Kit();
  const body = lathe(P, 24);
  kit.add(M.glaze, body);
  bands(kit, M.terracotta, P, [[0.1, 0.17]]);
  handle(kit, M.glaze, [0, 0.35, -0.05], [0, 0.2, -0.12], [0, 0.06, -0.08], 0.012);
  return kit.build();
}

/** 油壶（lekythos）：细长的白底油瓶——白底陶瓶这个画风就是从它来的，瓶身留白。 */
export function buildLekythos(M) {
  const P = [[0, 0], [0.05, 0], [0.05, 0.012], [0.035, 0.025], [0.055, 0.06], [0.06, 0.24], [0.055, 0.28], [0.03, 0.3], [0.018, 0.33], [0.018, 0.38], [0.03, 0.4], [0.028, 0.41], [0.012, 0.4]];
  const kit = new Kit();
  kit.add(M.marble, lathe(P, 20));
  bands(kit, M.glaze, P, [[0.24, 0.3], [0.0, 0.03]]);
  handle(kit, M.glaze, [0, 0.36, -0.018], [0, 0.29, -0.04], [0, 0.03, -0.035], 0.007);
  return kit.build();
}

/** 油灯：小小的一盏，带一个嘴和一只环形把手。灯不点着——光只属于雅典娜。 */
export function buildLamp(M) {
  const kit = new Kit();
  kit.add(M.terracotta, new THREE.SphereGeometry(0.07, 16, 10, 0, Math.PI * 2, 0, Math.PI / 2), m4(0, 0.0, 0, 0, Math.PI, 0, 1, 0.55, 1));
  kit.add(M.terracotta, new THREE.CylinderGeometry(0.07, 0.065, 0.03, 16), m4(0, 0.045, 0));
  kit.add(M.terracotta, new THREE.CylinderGeometry(0.02, 0.026, 0.09, 8), m4(0, 0.04, 0.08, 0, Math.PI / 2));
  kit.add(M.glaze, new THREE.TorusGeometry(0.028, 0.008, 6, 12), m4(0, 0.06, -0.08, 0, 0, Math.PI / 2));
  return kit.build();
}

/** 火盆：三足铜盆。神庙前的那一只不点火（光只属于雅典娜，第 8 节）。 */
export function buildBrazier(M) {
  const kit = new Kit();
  for (let i = 0; i < 3; i++) {
    const a = (i / 3) * Math.PI * 2;
    kit.cylinder(M.bronze, 0.025, 0.035, 0.85, Math.sin(a) * 0.22, 0.42, Math.cos(a) * 0.22, 6, Math.cos(a) * 0.18, -Math.sin(a) * 0.18);
  }
  kit.add(M.bronze, new THREE.SphereGeometry(0.36, 20, 10, 0, Math.PI * 2, Math.PI / 2, Math.PI / 2), m4(0, 1.02, 0));
  kit.cylinder(M.bronze, 0.37, 0.37, 0.05, 0, 1.02, 0, 20);
  kit.cylinder(M.dark, 0.3, 0.3, 0.02, 0, 1.0, 0, 16);   // 盆里的炭灰
  return kit.build();
}

/** 三足鼎（tripod）：德尔斐式的高足大鼎，两只环耳——神谕的标志。 */
export function buildTripod(M) {
  const kit = new Kit();
  for (let i = 0; i < 3; i++) {
    const a = (i / 3) * Math.PI * 2 + Math.PI / 6;
    kit.cylinder(M.bronze, 0.03, 0.045, 1.5, Math.sin(a) * 0.3, 0.75, Math.cos(a) * 0.3, 6, Math.cos(a) * 0.12, -Math.sin(a) * 0.12);
  }
  kit.add(M.bronze, new THREE.SphereGeometry(0.4, 20, 10, 0, Math.PI * 2, Math.PI / 2, Math.PI / 2), m4(0, 1.62, 0));
  for (const sx of [-1, 1]) kit.add(M.bronze, new THREE.TorusGeometry(0.16, 0.025, 6, 18), m4(sx * 0.28, 1.82, 0, Math.PI / 2));
  return kit.build();
}

/** 凳子（diphros）：四条车出来的腿、一块凳面。 */
export function buildStool(M) {
  const kit = new Kit();
  kit.box(M.wood, 0.5, 0.05, 0.4, 0, 0.44, 0);
  for (const sx of [-1, 1]) for (const sz of [-1, 1]) {
    kit.add(M.darkWood, lathe([[0.025, 0], [0.03, 0.05], [0.02, 0.2], [0.032, 0.32], [0.022, 0.42]], 8), m4(sx * 0.2, 0, sz * 0.15));
  }
  return kit.build();
}

/** 箱子（kibotos）：带脚、带一圈铜包角的木箱——金库里收账册的那种。 */
export function buildChest(M) {
  const kit = new Kit();
  kit.box(M.darkWood, 0.9, 0.5, 0.55, 0, 0.35, 0);
  kit.box(M.wood, 0.94, 0.08, 0.59, 0, 0.62, 0);
  for (const sx of [-1, 1]) for (const sz of [-1, 1]) {
    kit.box(M.darkWood, 0.08, 0.12, 0.08, sx * 0.4, 0.06, sz * 0.23);
    kit.box(M.bronze, 0.05, 0.52, 0.05, sx * 0.45, 0.35, sz * 0.27);
  }
  kit.box(M.bronze, 0.12, 0.1, 0.03, 0, 0.5, 0.29);
  return kit.build();
}

/** 卷轴筒（capsa）：一只圆筒，里面立着一捆卷轴，露出卷轴的端头。 */
export function buildScrollCase(M) {
  const kit = new Kit();
  kit.add(M.darkWood, new THREE.CylinderGeometry(0.2, 0.2, 0.42, 16, 1, true), m4(0, 0.21, 0));
  kit.cylinder(M.darkWood, 0.2, 0.2, 0.03, 0, 0.015, 0, 16);
  for (let i = 0; i < 7; i++) {
    const a = (i / 7) * Math.PI * 2;
    const r = i === 0 ? 0 : 0.11;
    kit.cylinder(M.papyrus, 0.045, 0.045, 0.5 + (i % 3) * 0.04, Math.cos(a) * r, 0.26 + (i % 3) * 0.02, Math.sin(a) * r, 8);
  }
  return kit.build();
}

/** 画架：三条腿的木架，上面靠着一块彩绘板（画面朝前）。 */
export function buildEasel(M) {
  const kit = new Kit();
  for (const sx of [-1, 1]) kit.cylinder(M.wood, 0.02, 0.025, 1.5, sx * 0.3, 0.74, 0.06, 6, -0.1, sx * 0.12);
  kit.cylinder(M.wood, 0.02, 0.025, 1.5, 0, 0.72, -0.28, 6, 0.32);
  kit.box(M.wood, 0.8, 0.04, 0.08, 0, 0.62, 0.12);
  kit.box(M.darkWood, 0.72, 0.56, 0.04, 0, 0.94, 0.1, 0, -0.1);
  kit.box(M.papyrus, 0.62, 0.46, 0.01, 0, 0.94, 0.125, 0, -0.1);
  return kit.build();
}

/** 方柱头像（herm）：一根方柱，顶上一颗有胡子的头。 */
export function buildHerm(M) {
  const kit = new Kit();
  kit.box(M.marble, 0.42, 1.5, 0.42, 0, 0.75, 0);
  kit.box(M.marble, 0.5, 0.08, 0.5, 0, 0.04, 0);
  kit.add(M.marble, new THREE.SphereGeometry(0.24, 16, 12), m4(0, 1.75, 0, 0, 0, 0, 0.9, 1.15, 0.95));
  kit.add(M.marble, new THREE.SphereGeometry(0.16, 12, 10), m4(0, 1.6, 0.1, 0, 0, 0, 1, 1.1, 0.7));   // 胡须
  for (const sx of [-1, 1]) kit.box(M.marble, 0.1, 0.07, 0.14, sx * 0.26, 1.28, 0);   // 两侧的短桩（herm 的"肩"）
  return kit.build();
}

/** 石碑（stele）：带小山花的一块立石——广场的委托板就是一块。 */
export function buildStele(M, { width = 1.3, height = 2.6 } = {}) {
  const kit = new Kit();
  kit.box(M.marble, width + 0.9, 0.25, 1.0, 0, 0.125, 0);
  kit.box(M.marble, width + 0.5, 0.25, 0.8, 0, 0.375, 0);
  kit.box(M.marble, width, height, 0.28, 0, 0.5 + height / 2, 0);
  const g = new THREE.Shape();
  g.moveTo(-(width + 0.2) / 2, 0); g.lineTo((width + 0.2) / 2, 0); g.lineTo(0, 0.45); g.closePath();
  const ped = new THREE.ExtrudeGeometry(g, { depth: 0.32, bevelEnabled: false });
  ped.translate(0, 0, -0.16);
  kit.add(M.marble, ped, m4(0, 0.5 + height, 0));
  return kit.build();
}

/** 木箱（货堆里的）：一只钉了横档的方箱。 */
export function buildCrate(M) {
  const kit = new Kit();
  kit.box(M.wood, 0.7, 0.6, 0.7, 0, 0.3, 0);
  for (const y of [0.1, 0.5]) kit.box(M.darkWood, 0.72, 0.06, 0.72, 0, y, 0);
  return kit.build();
}

/** 篮子：编织的圆篮，里面堆着羊毛团（作坊、市集）。 */
export function buildBasket(M) {
  const kit = new Kit();
  kit.add(M.wood, new THREE.CylinderGeometry(0.26, 0.2, 0.3, 14, 1, true), m4(0, 0.15, 0));
  kit.cylinder(M.wood, 0.2, 0.2, 0.02, 0, 0.01, 0, 14);
  for (let i = 0; i < 4; i++) kit.add(M.awning, new THREE.SphereGeometry(0.1, 10, 8), m4(Math.cos(i * 1.7) * 0.1, 0.3, Math.sin(i * 1.7) * 0.1));
  return kit.build();
}

/** 雕塑园里的立像：便宜的一体几何（衣褶 + 头 + 一条手臂），去色后是一尊尊白色大理石像。 */
export function statueGeometry(drape, blob, limb, merge, keyed) {
  const n = valueNoise(7);
  return merge([
    drape({
      y0: 0.0, y1: 1.55, rings: 16, segs: 36, ez: 0.75,
      radius: (t, th) => keyed([[0, 0.3], [0.55, 0.2], [1, 0.15]])(t) + 0.015 * fbm(n, th * 2, t * 4, 8, 2),
      folds: [{ n: 11, amp: 0.016, ph: 0.6, wobble: 0.3, wf: 2.2 }],
    }),
    blob([0, 1.72, 0.01], [0.1, 0.13, 0.11], [0, 0, 0], [16, 12]),
    limb([0.2, 1.5, 0], [0.32, 1.9, 0.12], 0.045),
  ]);
}

export function buildStatue(M, geometry) {
  const g = new THREE.Group();
  g.add(mesh(geometry, M.marble));
  return g;
}

/** 底座：藏品摆在上面的那种（场馆里两排各 6 个）。遗失的藏品只剩它（11.3"空底座"）。 */
export function buildPedestal(M) {
  const kit = new Kit();
  kit.box(M.marble, 0.86, 0.12, 0.86, 0, 0.06, 0);
  kit.box(M.marble, 0.68, 0.58, 0.68, 0, 0.41, 0);
  kit.box(M.marble, 0.82, 0.1, 0.82, 0, 0.75, 0);
  return kit.build();
}

/** 器物的合并工具：供 statueGeometry 之外的调用方把几块几何合成一份。 */
export function mergeAll(list) {
  return mergeGeometries(list.map((g) => (g.index ? g.toNonIndexed() : g)));
}
