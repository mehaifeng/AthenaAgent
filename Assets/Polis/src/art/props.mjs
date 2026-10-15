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

/**
 * 火盆：三条外撇的铜腿托一只车出来的浅盆，盆里堆着炭（盆沿一圈浅灰的灰烬）。神庙前的那一只不点火（光只属于雅典娜，第 8 节）。
 * M2 美术审查：第一版的盆是一个半球壳、盆口盖着一张深色的圆片，铜又近乎黑色，远看是一张黑色的小圆桌。
 */
export function buildBrazier(M) {
  const kit = new Kit();
  for (let i = 0; i < 3; i++) {
    const a = (i / 3) * Math.PI * 2;
    kit.cylinder(M.bronze, 0.025, 0.035, 0.85, Math.sin(a) * 0.22, 0.42, Math.cos(a) * 0.22, 6, Math.cos(a) * 0.18, -Math.sin(a) * 0.18);
  }
  kit.add(M.bronze, lathe([[0.04, 0], [0.2, 0.03], [0.32, 0.1], [0.38, 0.2], [0.42, 0.22], [0.42, 0.25], [0.36, 0.24], [0.3, 0.14], [0.04, 0.08]], 22), m4(0, 0.84, 0));
  kit.add(M.dark, new THREE.SphereGeometry(0.3, 14, 6, 0, Math.PI * 2, 0, Math.PI / 2), m4(0, 0.97, 0, 0, 0, 0, 1, 0.28, 1));
  kit.add(M.stone, new THREE.TorusGeometry(0.31, 0.035, 6, 20), m4(0, 1.02, 0, 0, Math.PI / 2));
  return kit.build();
}

/**
 * 三足鼎（tripod）：德尔斐式的高足大鼎——三条长腿托一只圆底的鼎腹，两只大环耳立在鼎沿上（神谕的标志）。
 * M2 美术审查：第一版的鼎腹是一个开口的半球壳（从斜上方看是一道黑色的月牙），两只环耳悬在半空，
 * 铜又近乎黑色——远看是一张带两只眼睛的笑脸。现在是车出来的鼎腹、浅铜色，环耳的下沿压在鼎沿上。
 */
export function buildTripod(M) {
  const kit = new Kit();
  const rimY = 1.86, rimR = 0.42;
  for (let i = 0; i < 3; i++) {
    const a = (i / 3) * Math.PI * 2 + Math.PI / 6;
    kit.cylinder(M.bronze, 0.03, 0.045, 1.62, Math.sin(a) * 0.32, 0.81, Math.cos(a) * 0.32, 6, Math.cos(a) * 0.12, -Math.sin(a) * 0.12);
  }
  kit.add(M.bronze, lathe([[0.02, 0], [0.18, 0.03], [0.32, 0.1], [0.41, 0.22], [0.43, 0.32], [0.41, 0.4], [0.44, 0.42], [0.44, 0.45], [0.38, 0.44], [0.36, 0.3], [0.02, 0.2]], 24), m4(0, rimY - 0.45, 0));
  // 环耳：立在鼎沿的左右两边，环面朝前后——斜上方看下去是两道竖着的椭圆，下沿接着鼎沿
  for (const sx of [-1, 1]) kit.add(M.bronze, new THREE.TorusGeometry(0.15, 0.022, 6, 20), m4(sx * (rimR - 0.06), rimY + 0.13, 0));
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

/**
 * 画架：三条腿的木架，上面靠着一块彩绘板（画面朝前）。M3 第三轮：板心抬到 1.21 米——画师站着画，笔尖落在板上
 * （第一版板心 0.94，画板矮到腰下，站着的人够不着）。userData.panel：画面中心与朝向（画架的局部坐标），画师的工位按它算。
 */
export const EASEL_PANEL = Object.freeze({ centre: Object.freeze([0, 1.21, 0.13]), tilt: -0.1 });
export function buildEasel(M) {
  const kit = new Kit();
  for (const sx of [-1, 1]) kit.cylinder(M.wood, 0.02, 0.025, 1.62, sx * 0.3, 0.8, 0.06, 6, -0.1, sx * 0.12);
  kit.cylinder(M.wood, 0.02, 0.025, 1.62, 0, 0.78, -0.3, 6, 0.32);
  kit.box(M.wood, 0.8, 0.04, 0.08, 0, 0.9, 0.12);
  kit.box(M.darkWood, 0.72, 0.56, 0.04, 0, 1.21, 0.1, 0, -0.1);
  kit.box(M.papyrus, 0.62, 0.46, 0.01, 0, 1.21, 0.125, 0, -0.1);
  // 板上几笔：一个人形的剪影和一道地平线（灰度里读得出是一幅画）
  kit.box(M.dark, 0.5, 0.012, 0.004, 0, 1.08, 0.135, 0, -0.1);
  kit.box(M.dark, 0.06, 0.2, 0.004, -0.08, 1.2, 0.134, 0, -0.1);
  kit.add(M.dark, new THREE.SphereGeometry(0.035, 8, 6), m4(-0.08, 1.33, 0.134, 0, 0, 0, 1, 1, 0.12));
  const group = kit.build();
  group.userData.panel = EASEL_PANEL;
  return group;
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

/**
 * 木箱（货堆里的）：第四轮（需求 9，2026-10-15，用户：给杂物多加一点细节）——竖着的木板（板缝是一道道暗线）、上下两道铁箍、
 * 箍上一排钉头、四角的包角，盖板比箱身宽一圈，两侧一对绳提手。
 */
export function buildCrate(M) {
  const kit = new Kit();
  kit.box(M.wood, 0.7, 0.58, 0.7, 0, 0.29, 0);
  kit.box(M.wood, 0.76, 0.05, 0.76, 0, 0.605, 0);                          // 盖板
  for (let k = -2; k <= 2; k++) {
    for (const [w, d, x, z] of [[0.006, 0.71, k * 0.14, 0], [0.71, 0.006, 0, k * 0.14]]) kit.box(M.darkWood, w, 0.56, d, x, 0.29, z);   // 板缝
  }
  for (const y of [0.1, 0.48]) {
    kit.box(M.dark, 0.72, 0.045, 0.72, 0, y, 0);                            // 铁箍
    for (let k = -2; k <= 2; k++) for (const [x, z] of [[k * 0.14, 0.362], [k * 0.14, -0.362], [0.362, k * 0.14], [-0.362, k * 0.14]]) {
      kit.box(M.bronze, 0.018, 0.018, 0.018, x, y, z);                      // 钉头
    }
  }
  for (const sx of [-1, 1]) for (const sz of [-1, 1]) kit.box(M.dark, 0.05, 0.6, 0.05, sx * 0.34, 0.3, sz * 0.34);   // 包角
  for (const sx of [-1, 1]) kit.add(M.hide, new THREE.TorusGeometry(0.07, 0.012, 6, 12, Math.PI), m4(sx * 0.365, 0.36, 0, Math.PI / 2, 0, 0));   // 绳提手
  return kit.build();
}

/**
 * 篮子：第四轮——一圈圈编出来的筐（几道横向的藤圈加一排竖着的筋，口沿一道粗一点的箍），一只提梁，里面堆着羊毛团和几颗果子。
 */
export function buildBasket(M) {
  const kit = new Kit();
  kit.add(M.wood, new THREE.CylinderGeometry(0.26, 0.2, 0.3, 18, 1, true), m4(0, 0.15, 0));
  kit.cylinder(M.wood, 0.2, 0.2, 0.02, 0, 0.01, 0, 14);
  for (let k = 0; k < 6; k++) {
    const y = 0.03 + k * 0.05, r = 0.2 + (0.06 * y) / 0.3 + 0.006;
    kit.add(M.darkWood, new THREE.TorusGeometry(r, 0.008, 4, 22), m4(0, y, 0, 0, Math.PI / 2, 0));   // 横向的藤圈
  }
  for (let k = 0; k < 16; k++) {
    const a = (k / 16) * Math.PI * 2;
    kit.add(M.darkWood, new THREE.CylinderGeometry(0.006, 0.006, 0.31, 4), m4(Math.cos(a) * 0.232, 0.155, Math.sin(a) * 0.232, 0, 0, Math.cos(a) * 0.19));
  }
  kit.add(M.wood, new THREE.TorusGeometry(0.262, 0.016, 6, 24), m4(0, 0.3, 0, 0, Math.PI / 2, 0));   // 口沿
  kit.add(M.wood, new THREE.TorusGeometry(0.24, 0.014, 6, 18, Math.PI), m4(0, 0.3, 0));               // 提梁
  for (let i = 0; i < 4; i++) kit.add(M.awning, new THREE.SphereGeometry(0.1, 10, 8), m4(Math.cos(i * 1.7) * 0.1, 0.3, Math.sin(i * 1.7) * 0.1));
  for (let i = 0; i < 3; i++) kit.add(M.terracotta, new THREE.SphereGeometry(0.045, 8, 6), m4(0.12 * Math.cos(i * 2.2 + 0.5), 0.36, 0.12 * Math.sin(i * 2.2 + 0.5)));
  return kit.build();
}

/** 麻袋（第四轮）：鼓鼓囊囊的一袋谷子，口子用绳扎着，袋身几道褶。 */
export function buildSack(M) {
  const kit = new Kit();
  const body = new THREE.SphereGeometry(0.24, 14, 10);
  const pos = body.attributes.position, v = new THREE.Vector3();
  for (let i = 0; i < pos.count; i++) {
    v.fromBufferAttribute(pos, i);
    const sag = v.y < 0 ? 1.15 : 1 - 0.35 * (v.y / 0.24) ** 2;        // 底下坠、上面收口
    pos.setXYZ(i, v.x * sag * (1 + 0.04 * Math.sin(v.y * 40 + Math.atan2(v.z, v.x) * 5)), v.y * 1.25, v.z * sag);
  }
  body.computeVertexNormals();
  kit.add(M.awning, body, m4(0, 0.3, 0));
  kit.add(M.awning, new THREE.CylinderGeometry(0.05, 0.07, 0.12, 8), m4(0, 0.63, 0));    // 扎起来的袋口
  kit.add(M.hide, new THREE.TorusGeometry(0.06, 0.012, 6, 12), m4(0, 0.6, 0, 0, Math.PI / 2, 0));
  return kit.build();
}

/** 晾网架（第四轮，港口）：两根桩子一根横杆，挂着一张渔网（网格是一道道细绳），网脚坠着几颗石坠。 */
export function buildNetRack(M) {
  const kit = new Kit();
  for (const sx of [-1, 1]) kit.box(M.darkWood, 0.08, 1.9, 0.08, sx * 1.1, 0.95, 0);
  kit.cylinder(M.wood, 0.03, 0.03, 2.4, 0, 1.85, 0, 8, 0, Math.PI / 2);
  for (let k = 0; k <= 12; k++) {
    const x = -1 + k / 6;
    const sag = 0.08 * Math.sin((k / 12) * Math.PI);
    kit.box(M.hide, 0.008, 1.35, 0.008, x, 1.18 - sag / 2, 0.03 + 0.02 * Math.sin(k));
    kit.add(M.stone, new THREE.SphereGeometry(0.03, 6, 4), m4(x, 0.48 - sag, 0.03));
  }
  for (let j = 0; j <= 8; j++) kit.box(M.hide, 2.0, 0.008, 0.008, 0, 0.5 + j * 0.165, 0.04);
  return kit.build();
}

/** 柴垛（第四轮，作坊与民居旁）：劈好的柴一层层码着，两头各一根木桩挡着。 */
export function buildWoodpile(M) {
  const kit = new Kit();
  woodpileInto(kit, M);
  return kit.build();
}

/** 把一垛柴码进别的东西的 kit 里（作坊的窑边）：原点在柴垛底面中心，ry 转它的朝向。 */
export function woodpileInto(kit, M, { x = 0, z = 0, ry = 0 } = {}) {
  const c = Math.cos(ry), sn = Math.sin(ry);
  const at = (lx, lz) => [x + lx * c + lz * sn, z - lx * sn + lz * c];
  for (let row = 0; row < 4; row++) {
    for (let k = 0; k < 7 - (row % 2); k++) {
      const [px, pz] = at(-0.54 + k * 0.18 + (row % 2) * 0.09, 0);
      kit.cylinder(row % 2 ? M.wood : M.darkWood, 0.075, 0.08, 0.7, px, 0.08 + row * 0.15, pz, 7, Math.PI / 2, 0, ry + (k * 0.7 + row) % 0.3);
    }
  }
  for (const sx of [-1, 1]) { const [px, pz] = at(sx * 0.68, 0); kit.box(M.darkWood, 0.08, 0.75, 0.08, px, 0.375, pz, ry); }
}

/**
 * 雕像的台座（第四轮，需求 9）：一层座脚、一截方柱身、一道挑出的檐，正面一条略深的刻字带（几行浅刻的字痕）。
 * 原点在台座底面中心，正面朝 +z；返回台面的高度。
 */
export function statuePlinth(kit, M, { x = 0, z = 0, width = 0.9, height = 0.9, ry = 0 } = {}) {
  const at = (lx, ly, lz) => {
    const c = Math.cos(ry), sn = Math.sin(ry);
    return [x + lx * c + lz * sn, ly, z - lx * sn + lz * c];
  };
  const box = (mat, w, h, d, lx, ly, lz) => { const [px, py, pz] = at(lx, ly, lz); kit.box(mat, w, h, d, px, py, pz, ry); };
  box(M.marble, width + 0.14, 0.1, width + 0.14, 0, 0.05, 0);
  box(M.marble, width + 0.06, 0.05, width + 0.06, 0, 0.125, 0);
  box(M.marble, width - 0.04, height - 0.27, width - 0.04, 0, 0.15 + (height - 0.27) / 2, 0);
  box(M.marble, width + 0.06, 0.05, width + 0.06, 0, height - 0.095, 0);
  box(M.marble, width + 0.12, 0.07, width + 0.12, 0, height - 0.035, 0);
  // 刻字带：正面一块略深的石面，上面几行浅刻的字痕
  box(M.stone, width - 0.24, 0.26, 0.01, 0, height * 0.55, (width - 0.04) / 2 + 0.004);
  for (let line = 0; line < 3; line++) {
    for (let k = 0; k < 7; k++) box(M.shadowGap, 0.045 + (k % 3) * 0.012, 0.022, 0.006, -0.26 + k * 0.087, height * 0.55 + 0.08 - line * 0.075, (width - 0.04) / 2 + 0.01);
  }
  return height;
}

/**
 * 雕塑园、港口、广场的立像（第四轮，需求 9：给雕像多加一点细节）。一尊持矛的着衣立像：对立式平衡（重心落在右腿，左膝微曲、
 * 衣褶顺着左腿往下垂），下身长袍细密的竖褶加上斜挂的外袍（himation）从左肩斜到右胯，上身胸肩、颈、头，头发与鬓角、
 * 鼻梁，一只手举着长矛、一只手垂着挽住外袍的一角，袍下露出两只脚尖。一体几何（几尊共用一份），约三千个三角形。
 */
export function statueGeometry(drape, blob, limb, merge, keyed) {
  const n = valueNoise(7);
  const parts = [
    // 下身长袍：右腿承重——左边（+x）的衣褶往前鼓一点，裙摆略微斜
    drape({
      y0: 0.0, y1: 0.98, rings: 18, segs: 40, ez: 0.72,
      radius: (t, th) => keyed([[0, 0.27], [0.35, 0.22], [0.75, 0.19], [1, 0.17]])(t) + 0.012 * fbm(n, th * 2, t * 4, 8, 2) + 0.025 * Math.max(0, Math.sin(th)) * (1 - t),
      folds: [{ n: 17, amp: 0.012, ph: 0.6, wobble: 0.25, wf: 2.4 }, { n: 5, amp: 0.018, ph: 1.4, wobble: 0.6, wf: 1.1 }],
      hem: { amp: 0.03, n: 3, ph: 0.4 },
    }),
    // 上身：胸、腰，衣褶斜着走
    drape({
      y0: 0.96, y1: 1.42, rings: 10, segs: 32, ez: 0.68,
      radius: (t) => keyed([[0, 0.17], [0.45, 0.19], [0.85, 0.2], [1, 0.12]])(t),
      folds: [{ n: 9, amp: 0.008, ph: 0.2, wobble: 0.9, wf: 6 }],
    }),
    blob([0, 1.37, 0], [0.22, 0.07, 0.12], [0, 0, 0], [16, 8]),       // 两肩
    limb([0, 1.4, 0], [0, 1.56, 0.012], 0.047),                         // 颈
    blob([0, 1.67, 0.015], [0.088, 0.112, 0.1], [0, 0, 0], [16, 12]),  // 头
    blob([0, 1.705, -0.012], [0.096, 0.098, 0.103], [0.2, 0, 0], [14, 10]),   // 头发（后脑饱满、额前一道发际）
    blob([0, 1.63, 0.03], [0.07, 0.04, 0.07], [0, 0, 0], [10, 6]),     // 胡须的轮廓（下颌）
    blob([0, 1.665, 0.11], [0.014, 0.03, 0.02], [0.2, 0, 0], [8, 6]),  // 鼻梁
    // 斜挂的外袍：从左肩斜到右胯的一道厚褶（一根顺着胸前弯过去的管子，压扁一点）
    new THREE.TubeGeometry(new THREE.CatmullRomCurve3([
      new THREE.Vector3(0.17, 1.39, 0.0), new THREE.Vector3(0.1, 1.3, 0.12), new THREE.Vector3(-0.02, 1.12, 0.17), new THREE.Vector3(-0.15, 0.95, 0.14), new THREE.Vector3(-0.2, 0.86, 0.06),
    ]), 20, 0.05, 8, false).scale(1, 1, 0.8),
    // 举矛的右手（−x）：上臂、前臂、拳；矛杆直立
    limb([-0.2, 1.36, 0], [-0.29, 1.6, 0.07], 0.042),
    limb([-0.29, 1.6, 0.07], [-0.27, 1.84, 0.12], 0.036),
    blob([-0.27, 1.87, 0.13], [0.04, 0.045, 0.04], [0, 0, 0], [8, 6]),
    limb([-0.27, 0.05, 0.15], [-0.27, 2.12, 0.12], 0.011),
    blob([-0.27, 2.17, 0.12], [0.022, 0.06, 0.012], [0, 0, 0], [6, 6]),     // 矛头
    // 垂着的左手（+x）挽着外袍的一角：上臂、前臂、垂下来的一角布
    limb([0.2, 1.36, 0], [0.27, 1.1, 0.05], 0.042),
    limb([0.27, 1.1, 0.05], [0.2, 0.93, 0.17], 0.036),
    drape({
      y0: 0.42, y1: 0.93, rings: 6, segs: 12, ez: 0.5,
      radius: (t) => 0.035 + 0.03 * (1 - t),
      folds: [{ n: 3, amp: 0.01, ph: 0, wobble: 0, wf: 1 }],
    }).translate(0.21, 0, 0.17),
    // 袍下露出两只脚尖（右脚站稳、左脚往前一点）
    blob([-0.07, 0.03, 0.21], [0.045, 0.03, 0.08], [0, 0, 0], [10, 6]),
    blob([0.09, 0.03, 0.18], [0.045, 0.03, 0.08], [0, 0.25, 0], [10, 6]),
  ];
  return merge(parts);
}

/** 立像（第四轮）：立在带线脚与刻字带的台座上（statuePlinth）。原点在台座底面中心。 */
export function buildStatue(M, geometry) {
  const kit = new Kit();
  const top = statuePlinth(kit, M, { width: 0.8, height: 0.85 });
  kit.add(M.marble, geometry, m4(0, top, 0));
  return kit.build();
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
