// 城北的农田与牧场（第四轮需求 5，2026-10-15，用户：先定好职业，职业也指导新资产——农田、捕鱼、纺织、畜牧……）。
// 农田与牧场占地图最北一排地块（layout 的 country：西头一块牧场、往东三块农田），农夫在垄间锄地、牧人在圈里看羊。
// 这里只有几何与羊的日程（纯函数 sheepAt，Node 单测）；摆在哪、谁去干活在 layout / world / citizens。
//
// 规则（每一条都有断言）：
//   1. 农田不挡路（资产登记为不参与碰撞）：庄稼最高 0.8 米，落在人身高度带里，按足迹算整块田都是墙，农夫就走不进垄里；
//      田埂是齐膝的干砌石（0.3 米，人身高度带之下）。垄沿 x 走，农夫面朝垄的方向锄（layout.workSpot）。
//   2. 牧场的栅栏挡路（羊圈要像羊圈），南边留 PEN.gate 宽的圈门；圈里的羊（5–7 只）按墙钟在圈里几个点之间慢慢走、停下低头吃草
//      （sheepAt：同一刻永远是同一个位置，和居民的日程一样可复现），走的时候四条腿对角交替摆，吃草时头低下去。羊不挡路、不分档。
//   3. 农夫、牧人在哪儿干（countrySpot）：农夫在垄沟里、沿着垄锄；牧人从圈门进去、站在门里看羊。
import * as THREE from '../../vendor/three.bundle.mjs';
import { mergeGeometries } from '../../vendor/three.bundle.mjs';
import { Kit, m4, shadowed } from './kit.mjs';
import { rng } from './noise.mjs';

/** 一块农田的大小（米）与垄：沿 x 的 rows 条垄，垄距 rowGap，从 −d/2 + 0.9 起。 */
export const FIELD = Object.freeze({ w: 10.4, d: 9.2, rows: 7, rowGap: 1.2, edge: 0.3 });
/** 羊圈：半宽、半深，南边圈门宽，栏杆高。 */
export const PEN = Object.freeze({ hw: 4.6, hd: 3.6, gate: 1.8, rail: 0.95 });
export const FIELD_CROPS = Object.freeze(['wheat', 'greens', 'vines']);

/** 第 k 条垄的 z（田的本地坐标）。 */
export const furrowZ = (k) => -FIELD.d / 2 + 0.9 + k * FIELD.rowGap;

let wheatGeo = null, cabbageGeo = null, vineLeafGeo = null;
/** 一簇麦子（五根秆、各一只穗），高 0.62–0.78 米；几何共用。 */
function wheatTuft() {
  if (wheatGeo) return wheatGeo;
  const parts = [];
  const r = rng(7);
  for (let k = 0; k < 5; k++) {
    const h = 0.62 + r() * 0.16, a = (k / 5) * Math.PI * 2, lean = 0.06 + r() * 0.08;
    const stalk = new THREE.CylinderGeometry(0.004, 0.006, h, 4).translate(0, h / 2, 0);
    const ear = new THREE.CylinderGeometry(0.012, 0.008, 0.1, 5).translate(0, h + 0.05, 0);
    const tilt = new THREE.Matrix4().makeRotationFromEuler(new THREE.Euler(Math.cos(a) * lean, 0, Math.sin(a) * lean));
    const off = new THREE.Matrix4().makeTranslation(Math.cos(a) * 0.035, 0, Math.sin(a) * 0.035);
    for (const g of [stalk, ear]) { g.applyMatrix4(tilt); g.applyMatrix4(off); parts.push(g.toNonIndexed()); }
  }
  for (const g of parts) for (const name of Object.keys(g.attributes)) if (!['position', 'normal', 'uv'].includes(name)) g.deleteAttribute(name);
  wheatGeo = mergeGeometries(parts, false);
  return wheatGeo;
}
function cabbage() {
  if (!cabbageGeo) cabbageGeo = new THREE.SphereGeometry(0.17, 9, 6).scale(1, 0.62, 1).translate(0, 0.1, 0);
  return cabbageGeo;
}
function vineLeaf() {
  if (!vineLeafGeo) vineLeafGeo = new THREE.IcosahedronGeometry(0.16, 0).scale(1.2, 0.8, 0.9);
  return vineLeafGeo;
}

/**
 * 一块农田（原点在田的正中，地面 y = 0）：翻过的土、一圈齐膝的干砌石埂（南边留 1.4 米的口子）、沿 x 的几条垄，
 * 垄上按 crop 种麦子、菜或者搭葡萄架；scarecrow 为真时立一个稻草人。
 */
export function buildField(M, { crop = 'wheat', seed = 1, scarecrow = false } = {}) {
  const kit = new Kit();
  const r = rng(seed);
  const { w, d } = FIELD;
  kit.box(M.soil, w, 0.05, d, 0, 0.025, 0);
  // 干砌石埂：一块块大小不一的石头（南边正中留口子，农夫从这里下田）
  const stones = (x0, z0, x1, z1) => {
    const len = Math.hypot(x1 - x0, z1 - z0), n = Math.max(1, Math.round(len / 0.42));
    for (let k = 0; k < n; k++) {
      const u = (k + 0.5) / n, x = x0 + (x1 - x0) * u, z = z0 + (z1 - z0) * u;
      if (z1 === z0 && z0 > 0 && Math.abs(x) < 0.7) continue;
      const sw = 0.32 + r() * 0.14, sh = 0.18 + r() * 0.12;
      kit.box(M.stone, sw, sh, 0.3 + r() * 0.08, x, sh / 2, z, Math.atan2(z1 - z0, x1 - x0) + (r() - 0.5) * 0.3);
    }
  };
  const hx = w / 2 + 0.12, hz = d / 2 + 0.12;
  stones(-hx, -hz, hx, -hz); stones(-hx, hz, hx, hz); stones(-hx, -hz, -hx, hz); stones(hx, -hz, hx, hz);
  for (let k = 0; k < FIELD.rows; k++) {
    const z = furrowZ(k);
    // 垄：一条压实的土埂（高 0.1 米），庄稼种在垄上；两垄之间的沟是农夫走、锄的地方
    kit.box(M.soil, w - 0.8, 0.1, 0.42, 0, 0.07, z - FIELD.rowGap / 2 + 0.05);
    const zr = z - FIELD.rowGap / 2 + 0.05;
    if (crop === 'wheat') {
      for (let x = -w / 2 + 0.6; x <= w / 2 - 0.6; x += 0.23) kit.instance('wheat', wheatTuft, M.wheat, m4(x + (r() - 0.5) * 0.08, 0.1, zr + (r() - 0.5) * 0.12, r() * 6, 0, 0, 0.85 + r() * 0.3));
    } else if (crop === 'greens') {
      for (let x = -w / 2 + 0.7; x <= w / 2 - 0.7; x += 0.45) kit.instance('cabbage', cabbage, M.greens, m4(x, 0.1, zr, r() * 6, 0, 0, 0.8 + r() * 0.35));
    } else {
      // 葡萄架：每 1.6 米一根木桩、桩顶一根横杆，叶子一团团挂在横杆上
      for (let x = -w / 2 + 0.6; x <= w / 2 - 0.5; x += 1.6) kit.box(M.darkWood, 0.06, 1.2, 0.06, x, 0.65, zr);
      kit.box(M.wood, w - 1.0, 0.03, 0.03, 0, 1.22, zr);
      for (let x = -w / 2 + 0.7; x <= w / 2 - 0.7; x += 0.36) kit.instance('vineLeaf', vineLeaf, M.greens, m4(x, 1.05 + r() * 0.2, zr + (r() - 0.5) * 0.15, r() * 6, r(), 0));
    }
  }
  if (scarecrow) {
    // 稻草人：木桩、横担、草扎的头，披一件旧衣
    const sx = w / 2 - 1.4, sz = -d / 2 + 1.2;
    kit.box(M.darkWood, 0.07, 1.75, 0.07, sx, 0.875, sz);
    kit.box(M.darkWood, 0.95, 0.06, 0.06, sx, 1.42, sz);
    kit.add(M.wheat, new THREE.SphereGeometry(0.13, 8, 6), m4(sx, 1.68, sz));
    kit.add(M.reed ?? M.wheat, new THREE.ConeGeometry(0.2, 0.14, 8), m4(sx, 1.8, sz));
    kit.box(M.clothShades?.[2] ?? M.cloth, 0.48, 0.55, 0.16, sx, 1.2, sz);
  }
  const g = kit.build();
  g.userData.footprint = null;   // 规则 1：不挡路
  g.userData.country = 'field';
  return g;
}

/**
 * 牧场（原点在羊圈正中）：木栅栏（每 1.2 米一根桩、两道横栏，南边正中留圈门），圈里西北角一座草顶的棚子、一只石水槽、一堆干草。
 */
export function buildPasture(M) {
  const kit = new Kit();
  const { hw, hd, gate, rail } = PEN;
  kit.box(M.earth, hw * 2 + 0.4, 0.03, hd * 2 + 0.4, 0, 0.015, 0);
  const posts = (x0, z0, x1, z1) => {
    const len = Math.hypot(x1 - x0, z1 - z0), n = Math.max(1, Math.round(len / 1.2));
    for (let k = 0; k <= n; k++) {
      const u = k / n, x = x0 + (x1 - x0) * u, z = z0 + (z1 - z0) * u;
      kit.box(M.darkWood, 0.09, rail + 0.15, 0.09, x, (rail + 0.15) / 2, z);
    }
    const yaw = Math.atan2(-(z1 - z0), x1 - x0);
    for (const y of [0.45, rail - 0.05]) kit.box(M.wood, len, 0.06, 0.04, (x0 + x1) / 2, y, (z0 + z1) / 2, yaw);
  };
  posts(-hw, -hd, hw, -hd);
  posts(-hw, -hd, -hw, hd);
  posts(hw, -hd, hw, hd);
  posts(-hw, hd, -gate / 2, hd);
  posts(gate / 2, hd, hw, hd);
  // 草棚：西北角两根前柱、背靠栅栏，苇草的坡顶
  const sx = -hw + 1.3, sz = -hd + 0.9;
  for (const dx of [-0.9, 0.9]) kit.box(M.darkWood, 0.1, 1.7, 0.1, sx + dx, 0.85, sz + 0.75);
  kit.box(M.reed, 2.3, 0.08, 1.9, sx, 1.62, sz + 0.05, 0, -0.32);
  // 石水槽与一堆干草
  kit.box(M.stone, 1.1, 0.35, 0.4, hw - 1.2, 0.175, -hd + 0.7);
  kit.box(M.plinthSea ?? M.dark, 0.95, 0.02, 0.28, hw - 1.2, 0.33, -hd + 0.7);
  kit.add(M.wheat, new THREE.ConeGeometry(0.75, 0.9, 10), m4(hw - 1.1, 0.45, hd - 1.1));
  const g = kit.build();
  g.userData.country = 'pasture';
  return g;
}

/**
 * 一只羊（原点在四脚之间的地面上，头朝 +z）：一团团的羊毛、暗色的脸和耳朵、四条细腿。返回 { root, pose(walkPhase, walking, graze) }：
 * 走的时候对角的两条腿一起往前摆；graze 0–1 是头低下去吃草的程度。
 */
export function buildSheep(M, seed = 1) {
  const r = rng(seed);
  const root = new THREE.Group();
  const body = new THREE.Group();
  root.add(body);
  const s = 0.9 + r() * 0.2;
  const wool = [];
  for (let k = 0; k < 7; k++) {
    const g = new THREE.SphereGeometry(0.16 + r() * 0.06, 9, 7);
    g.translate((r() - 0.5) * 0.16, 0.56 + (r() - 0.5) * 0.08, -0.26 + (k / 6) * 0.5);
    wool.push(g.toNonIndexed());
  }
  for (const g of wool) for (const name of Object.keys(g.attributes)) if (!['position', 'normal', 'uv'].includes(name)) g.deleteAttribute(name);
  body.add(shadowed(new THREE.Mesh(mergeGeometries(wool, false), M.wool)));
  const neck = new THREE.Group();
  neck.position.set(0, 0.64, 0.3);
  body.add(neck);
  const head = shadowed(new THREE.Mesh(new THREE.SphereGeometry(0.1, 9, 7).scale(0.9, 1, 1.7), M.muzzle));
  head.position.set(0, 0.04, 0.14);
  neck.add(head);
  const poll = shadowed(new THREE.Mesh(new THREE.SphereGeometry(0.085, 8, 6), M.wool));
  poll.position.set(0, 0.1, 0.06);
  neck.add(poll);
  for (const sx of [-1, 1]) {
    const ear = shadowed(new THREE.Mesh(new THREE.BoxGeometry(0.1, 0.03, 0.05), M.muzzle));
    ear.position.set(sx * 0.1, 0.07, 0.08);
    ear.rotation.z = sx * -0.4;
    neck.add(ear);
  }
  const legs = [];
  for (const [lx, lz] of [[0.11, 0.2], [-0.11, 0.2], [0.11, -0.2], [-0.11, -0.2]]) {
    const hip = new THREE.Group();
    hip.position.set(lx, 0.42, lz);
    const leg = shadowed(new THREE.Mesh(new THREE.CylinderGeometry(0.022, 0.018, 0.42, 6).translate(0, -0.21, 0), M.muzzle));
    hip.add(leg);
    body.add(hip);
    legs.push(hip);
  }
  const tail = shadowed(new THREE.Mesh(new THREE.SphereGeometry(0.06, 6, 5), M.wool));
  tail.position.set(0, 0.6, -0.36);
  body.add(tail);
  root.scale.setScalar(s);
  return {
    root,
    pose(walkPhase, walking, graze) {
      const swing = walking ? 0.45 * Math.sin(walkPhase) : 0;
      legs[0].rotation.x = swing; legs[3].rotation.x = swing;
      legs[1].rotation.x = -swing; legs[2].rotation.x = -swing;
      body.position.y = walking ? 0.012 * Math.abs(Math.cos(walkPhase)) : 0;
      neck.rotation.x = 0.95 * graze + (walking ? 0.05 * Math.sin(walkPhase * 2) : 0.04 * Math.sin(walkPhase * 0.3));
    },
  };
}

/**
 * 规则 3：干活的站位（citizens 规则 7 的 workSpot）。fields——按种子挑一块农田、一条垄沟、沟里的一段，面朝垄的方向（沿 x）锄，
 * 锄刃落在身前 0.85 米的沟里；先走到田南沿那一点、再笔直走进那条沟（田不挡路）。pasture——从南边的圈门进羊圈，站在门里一侧，
 * 面朝圈里（北）看羊。那一点站不了就没有站位（他不在那儿干）。layout 要有 country（layout.mjs 的城郊）和 walkable。
 */
export function countrySpot(layout, site, seed) {
  const s = seed >>> 0;
  if (site === 'fields') {
    const fields = (layout.country ?? []).filter((c) => c.country === 'field');
    if (fields.length === 0) return null;
    const f = fields[(s >>> 3) % fields.length];
    const row = 1 + ((s >>> 7) % (FIELD.rows - 1));
    const dir = (s >>> 11) % 2 ? 1 : -1;
    const x = f.cx + (((s >>> 13) % 9) - 4) * 0.85;
    const z = f.cz + furrowZ(row);
    const p = { x, z };
    return layout.walkable(p) ? { ...p, yaw: (dir * Math.PI) / 2, via: [{ x: f.cx, z: f.stand.z }, { x: f.cx, z }] } : null;
  }
  if (site === 'pasture') {
    const pen = (layout.country ?? []).find((c) => c.country === 'pasture');
    if (!pen) return null;
    const gateZ = pen.cz + PEN.hd;
    const p = { x: pen.cx + (((s >>> 5) % 3) - 1) * 1.2, z: gateZ - 1.4 };
    return layout.walkable(p) ? { ...p, yaw: Math.PI, via: [{ x: pen.cx, z: gateZ + 0.9 }, { x: pen.cx, z: gateZ - 0.9 }] } : null;
  }
  return null;
}

/** 羊的日程（规则 2）：每只按种子在圈里有 WAYPOINTS 个点，到一个点停下吃草，再慢慢走到下一个。 */
export const SHEEP = Object.freeze({ waypoints: 6, speed: 0.35, grazeMs: [6000, 14000], margin: 0.7 });

/** 圈里第 k 个点（圈的本地坐标，离栅栏至少 margin 米，躲开西北角的草棚）。 */
function waypoint(seed, k) {
  const r = rng((seed * 7919 + k * 104729) >>> 0);
  for (let t = 0; t < 8; t++) {
    const x = (r() * 2 - 1) * (PEN.hw - SHEEP.margin), z = (r() * 2 - 1) * (PEN.hd - SHEEP.margin);
    if (x < -PEN.hw + 2.6 && z < -PEN.hd + 2.2) continue;   // 草棚底下
    return { x, z };
  }
  return { x: 0, z: 0 };
}

/**
 * 一只羊在墙钟 wall 这一刻在哪（圈的本地坐标）、朝哪、在走还是在吃草。纯函数：同一个种子、同一刻永远同一个样子。
 * 返回 { x, z, yaw, walking, phase（腿的相位，按走过的距离）, graze（0–1，头低下去多少） }。
 */
export function sheepAt(seed, wall) {
  const legs = [];
  let total = 0;
  for (let k = 0; k < SHEEP.waypoints; k++) {
    const a = waypoint(seed, k), b = waypoint(seed, (k + 1) % SHEEP.waypoints);
    const r = rng((seed * 31 + k * 977) >>> 0);
    const graze = SHEEP.grazeMs[0] + r() * (SHEEP.grazeMs[1] - SHEEP.grazeMs[0]);
    const dist = Math.hypot(b.x - a.x, b.z - a.z);
    const walk = (dist / SHEEP.speed) * 1000;
    legs.push({ a, b, graze, walk, dist });
    total += graze + walk;
  }
  let t = (((wall + seed * 1337) % total) + total) % total;
  for (const l of legs) {
    if (t < l.graze) {
      const yaw = Math.atan2(l.b.x - l.a.x, l.b.z - l.a.z);
      const g = Math.min(1, t / 600, (l.graze - t) / 600);
      return { x: l.a.x, z: l.a.z, yaw, walking: false, phase: t / 400, graze: Math.max(0, g) };
    }
    t -= l.graze;
    if (t < l.walk) {
      const f = t / l.walk;
      return {
        x: l.a.x + (l.b.x - l.a.x) * f, z: l.a.z + (l.b.z - l.a.z) * f, yaw: Math.atan2(l.b.x - l.a.x, l.b.z - l.a.z),
        walking: true, phase: ((l.dist * f) / 0.42) * Math.PI, graze: 0,
      };
    }
    t -= l.walk;
  }
  const last = legs[legs.length - 1];
  return { x: last.b.x, z: last.b.z, yaw: 0, walking: false, phase: 0, graze: 1 };
}
