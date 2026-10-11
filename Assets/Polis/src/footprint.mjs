// 碰撞足迹（M3 决定 1）：每件资产按自己的网格推出"人能不能站在这里"的地面足迹，不再给整块地块套一个方盒子。
// 纯逻辑：读 three.js 的几何，不碰 DOM，Node 单测覆盖（footprint.test.mjs）。
//
// 规则（每一条都有断言）：
//   - 只看"人身高度带"里的三角形（BODY_BAND：膝盖到头顶，0.35–1.6 米）：台阶、路缘、铺地、底座的地脚迈得过去，
//     屋檐、葡萄架、城门的过梁、市集的凉棚从头顶过去，树只挡树干、不挡树冠；
//   - 被墙围死的地方（屋里）填实：从足迹外沿泛洪，泛不到的空格就是屋里——门是贴在墙上的深色板，不是洞，所以屋里永远到不了；
//   - 屋檐底下能走进 COVER_REACH（2.5 米）：柱廊的檐下、门廊、锻炉那样敞开的工棚是街的一部分，再往里就是屋里了
//     （人会消失在屋顶底下）。2.5 米是按镜头量的：缺省仰角 33° 下 3.6 米高的檐口遮住它身后 5.5 米，
//     最陡的 72° 遮住 1.2 米——取中间，铁匠在铁砧前的位置（檐口之内 1.4 米）你也站得到；
//   - 足迹按人的半径（PLAYER_RADIUS）膨胀，之后站立点仍是一个点的查询（layout.walkable 不变）；
//   - 矮于 BLOCKING_HEIGHT（0.4 米）的器物与植物不挡路（酒杯、油灯、地上的卷轴）——撞上看不见的东西读起来像 bug。
// 单位：米；栅格 CELL（0.1 米）。足迹在资产自己的坐标系里算一次、按摆放矩阵盖到全城的占用栅格上（createOccupancy）。
import * as THREE from '../vendor/three.bundle.mjs';

export const CELL = 0.1;
export const PLAYER_RADIUS = 0.25;
export const BLOCKING_HEIGHT = 0.4;
export const BODY_BAND = Object.freeze({ min: 0.35, max: 1.6 });
export const COVER_REACH = 2.5;

/** 一种资产怎样参与碰撞：按高度带推足迹（缺省）、或者不参与（成果、人物、走在上面的地面件）。 */
export const COLLISION_MODES = Object.freeze(['band', 'none']);

const _a = new THREE.Vector3(), _b = new THREE.Vector3(), _c = new THREE.Vector3();
const _m = new THREE.Matrix4(), _inv = new THREE.Matrix4();

/**
 * 一件资产（Group / Mesh）的足迹。返回 null 表示它不挡任何地方。
 * @param {THREE.Object3D} object 资产的根（世界矩阵会被刷新；足迹按根的局部坐标）
 * @param {{ band?: {min:number,max:number}, cell?: number, radius?: number, coverReach?: number|null, minHeight?: number, stopAt?: (o: THREE.Object3D) => boolean }} [options]
 *   minHeight：整件东西矮于它就不挡路（器物与植物传 BLOCKING_HEIGHT；建筑传 0）；
 *   coverReach：屋檐下能走进多深，null 表示不按"屋顶"算（树冠不是屋顶：器物与植物传 null）；
 *   stopAt：不往下走的子树（嵌在建筑里的树、台地上的器物各自算自己的足迹）
 */
export function footprintOf(object, { band = BODY_BAND, cell = CELL, radius = PLAYER_RADIUS, coverReach = COVER_REACH, minHeight = 0, stopAt = null } = {}) {
  object.updateMatrixWorld(true);
  _inv.copy(object.matrixWorld).invert();
  const solid = [], cover = [];
  let top = -Infinity;
  const take = (geometry, matrix) => {
    const pos = geometry.attributes.position;
    if (!pos) return;
    const index = geometry.index;
    const n = index ? index.count : pos.count;
    for (let i = 0; i < n; i += 3) {
      const ia = index ? index.getX(i) : i, ib = index ? index.getX(i + 1) : i + 1, ic = index ? index.getX(i + 2) : i + 2;
      _a.fromBufferAttribute(pos, ia).applyMatrix4(matrix);
      _b.fromBufferAttribute(pos, ib).applyMatrix4(matrix);
      _c.fromBufferAttribute(pos, ic).applyMatrix4(matrix);
      const lo = Math.min(_a.y, _b.y, _c.y), hi = Math.max(_a.y, _b.y, _c.y);
      top = Math.max(top, hi);
      if (hi < band.min) continue;
      if (lo > band.max) { cover.push([_a.x, _a.z, _b.x, _b.z, _c.x, _c.z]); continue; }
      for (const t of clipToBand([_a.clone(), _b.clone(), _c.clone()], band)) solid.push(t);
    }
  };
  walkVisible(object, (o) => o !== object && stopAt?.(o), (mesh) => {
    if (!mesh.isMesh || !mesh.geometry) return;
    const rel = _m.multiplyMatrices(_inv, mesh.matrixWorld).clone();
    if (mesh.isInstancedMesh) {
      const im = new THREE.Matrix4();
      for (let k = 0; k < mesh.count; k++) {
        mesh.getMatrixAt(k, im);
        take(mesh.geometry, new THREE.Matrix4().multiplyMatrices(rel, im));
      }
    } else {
      take(mesh.geometry, rel);
    }
  });
  if (solid.length === 0 || top < minHeight) return null;

  let minX = Infinity, maxX = -Infinity, minZ = Infinity, maxZ = -Infinity;
  for (const list of [solid, cover]) for (const t of list) for (let k = 0; k < 6; k += 2) {
    minX = Math.min(minX, t[k]); maxX = Math.max(maxX, t[k]);
    minZ = Math.min(minZ, t[k + 1]); maxZ = Math.max(maxZ, t[k + 1]);
  }
  const pad = radius + cell * 2;
  const x0 = Math.floor((minX - pad) / cell) * cell, z0 = Math.floor((minZ - pad) / cell) * cell;
  const w = Math.ceil((maxX + pad - x0) / cell) + 1, h = Math.ceil((maxZ + pad - z0) / cell) + 1;
  const wall = new Uint8Array(w * h);
  const roof = new Uint8Array(w * h);
  for (const t of solid) rasterize(t, wall, x0, z0, w, h, cell);
  for (const t of cover) rasterize(t, roof, x0, z0, w, h, cell);
  fillPockets(wall, w, h);
  if (coverReach != null) fillUnderCover(wall, roof, w, h, Math.round(coverReach / cell));
  const bits = dilate(wall, w, h, Math.ceil(radius / cell), radius / cell);
  let area = 0;
  for (let i = 0; i < bits.length; i++) area += bits[i];
  return { cell, x0, z0, w, h, bits, area: area * cell * cell, top };
}

/** 一棵实例化的树那样的几何（没有 Object3D）：按几何本身推足迹。 */
export function footprintOfGeometry(geometry, options = {}) {
  const mesh = new THREE.Mesh(geometry);
  return footprintOf(mesh, options);
}

/** 足迹里 (x, z)（资产局部坐标）是不是被占。 */
export function footprintBlocked(fp, x, z) {
  const i = Math.floor((x - fp.x0) / fp.cell), j = Math.floor((z - fp.z0) / fp.cell);
  if (i < 0 || j < 0 || i >= fp.w || j >= fp.h) return false;
  return fp.bits[j * fp.w + i] === 1;
}

/**
 * 全城的占用栅格：足迹按摆放矩阵盖上来，查询是一个点。
 * @param {{ minX: number, maxX: number, minZ: number, maxZ: number }} bounds 世界坐标（米）
 */
export function createOccupancy(bounds, cell = CELL) {
  const x0 = Math.floor(bounds.minX / cell) * cell, z0 = Math.floor(bounds.minZ / cell) * cell;
  const w = Math.ceil((bounds.maxX - x0) / cell) + 1, h = Math.ceil((bounds.maxZ - z0) / cell) + 1;
  const bits = new Uint8Array(w * h);
  let stamped = 0;
  const inv = new THREE.Matrix4(), p = new THREE.Vector3();
  const corners = [new THREE.Vector3(), new THREE.Vector3(), new THREE.Vector3(), new THREE.Vector3()];

  /** 把一个足迹按 matrixWorld（资产 → 世界）盖上来。 */
  function stamp(fp, matrixWorld) {
    if (!fp) return;
    const lx1 = fp.x0 + fp.w * fp.cell, lz1 = fp.z0 + fp.h * fp.cell;
    corners[0].set(fp.x0, 0, fp.z0); corners[1].set(lx1, 0, fp.z0); corners[2].set(fp.x0, 0, lz1); corners[3].set(lx1, 0, lz1);
    let minX = Infinity, maxX = -Infinity, minZ = Infinity, maxZ = -Infinity;
    for (const c of corners) {
      c.applyMatrix4(matrixWorld);
      minX = Math.min(minX, c.x); maxX = Math.max(maxX, c.x); minZ = Math.min(minZ, c.z); maxZ = Math.max(maxZ, c.z);
    }
    inv.copy(matrixWorld).invert();
    const i0 = Math.max(0, Math.floor((minX - x0) / cell)), i1 = Math.min(w - 1, Math.ceil((maxX - x0) / cell));
    const j0 = Math.max(0, Math.floor((minZ - z0) / cell)), j1 = Math.min(h - 1, Math.ceil((maxZ - z0) / cell));
    for (let j = j0; j <= j1; j++) {
      for (let i = i0; i <= i1; i++) {
        if (bits[j * w + i]) continue;
        p.set(x0 + (i + 0.5) * cell, 0, z0 + (j + 0.5) * cell).applyMatrix4(inv);
        if (footprintBlocked(fp, p.x, p.z)) bits[j * w + i] = 1;
      }
    }
    stamped++;
  }

  function blocked(x, z) {
    const i = Math.floor((x - x0) / cell), j = Math.floor((z - z0) / cell);
    if (i < 0 || j < 0 || i >= w || j >= h) return false;
    return bits[j * w + i] === 1;
  }

  return {
    cell, x0, z0, w, h, bits,
    stamp,
    blocked,
    clear() { bits.fill(0); stamped = 0; },
    get stamped() { return stamped; },
    /** 被占的面积（平方米）——测试与日志用。 */
    area() { let n = 0; for (let i = 0; i < bits.length; i++) n += bits[i]; return n * cell * cell; },
  };
}

// ———————————————————————— 内部 ————————————————————————

/** 只走可见的子树（隐藏的脚手架、空位的白环、消散了的侍女不算）；stop(o) 为真的子树不进。 */
function walkVisible(object, stop, fn) {
  if (object.visible === false || stop(object)) return;
  fn(object);
  for (const child of object.children) walkVisible(child, stop, fn);
}

/** 三角形裁到高度带里（Sutherland–Hodgman，先裁 y ≥ min 再裁 y ≤ max），返回若干个三角形的 XZ 投影。 */
function clipToBand(tri, band) {
  let poly = tri;
  poly = clipPoly(poly, (v) => v.y - band.min);
  if (poly.length < 3) return [];
  poly = clipPoly(poly, (v) => band.max - v.y);
  if (poly.length < 3) return [];
  const out = [];
  for (let k = 1; k + 1 < poly.length; k++) out.push([poly[0].x, poly[0].z, poly[k].x, poly[k].z, poly[k + 1].x, poly[k + 1].z]);
  return out;
}

function clipPoly(poly, inside) {
  const out = [];
  for (let i = 0; i < poly.length; i++) {
    const cur = poly[i], prev = poly[(i + poly.length - 1) % poly.length];
    const dc = inside(cur), dp = inside(prev);
    if (dc >= 0) {
      if (dp < 0) out.push(prev.clone().lerp(cur, dp / (dp - dc)));
      out.push(cur);
    } else if (dp >= 0) {
      out.push(prev.clone().lerp(cur, dp / (dp - dc)));
    }
  }
  return out;
}

/**
 * 保守光栅化：三角形碰到的格子都算（格心在三角形里，或离某条边不到半格多一点）。
 * 竖直的面投下来是一条线：一面墙正好落在格线上时两边的格子都要算上，线才连续——墙围出的屋里全靠这条线封口（fillPockets），
 * 第一版按"恰好半格"比较，浮点误差让格线上的墙漏出一个个缺口，门柱、箱子的中间都没填实。
 */
function rasterize(t, bits, x0, z0, w, h, cell) {
  const [ax, az, bx, bz, cx, cz] = t;
  const i0 = Math.max(0, Math.floor((Math.min(ax, bx, cx) - x0) / cell) - 1), i1 = Math.min(w - 1, Math.ceil((Math.max(ax, bx, cx) - x0) / cell) + 1);
  const j0 = Math.max(0, Math.floor((Math.min(az, bz, cz) - z0) / cell) - 1), j1 = Math.min(h - 1, Math.ceil((Math.max(az, bz, cz) - z0) / cell) + 1);
  const half = cell * 0.55;
  for (let j = j0; j <= j1; j++) {
    const pz = z0 + (j + 0.5) * cell;
    for (let i = i0; i <= i1; i++) {
      if (bits[j * w + i]) continue;
      const px = x0 + (i + 0.5) * cell;
      if (pointInTriangle(px, pz, ax, az, bx, bz, cx, cz)
        || segmentDistance(px, pz, ax, az, bx, bz) <= half
        || segmentDistance(px, pz, bx, bz, cx, cz) <= half
        || segmentDistance(px, pz, cx, cz, ax, az) <= half) bits[j * w + i] = 1;
    }
  }
}

function pointInTriangle(px, pz, ax, az, bx, bz, cx, cz) {
  const d1 = (px - bx) * (az - bz) - (ax - bx) * (pz - bz);
  const d2 = (px - cx) * (bz - cz) - (bx - cx) * (pz - cz);
  const d3 = (px - ax) * (cz - az) - (cx - ax) * (pz - az);
  const neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
  return !(neg && pos);
}

function segmentDistance(px, pz, ax, az, bx, bz) {
  const dx = bx - ax, dz = bz - az;
  const len2 = dx * dx + dz * dz;
  const t = len2 > 0 ? Math.max(0, Math.min(1, ((px - ax) * dx + (pz - az) * dz) / len2)) : 0;
  return Math.hypot(px - (ax + dx * t), pz - (az + dz * t));
}

/** 从外沿泛洪：泛不到的空格是被围死的屋里，填实。 */
function fillPockets(bits, w, h) {
  const seen = new Uint8Array(w * h);
  const stack = [];
  const push = (i, j) => {
    if (i < 0 || j < 0 || i >= w || j >= h) return;
    const k = j * w + i;
    if (seen[k] || bits[k]) return;
    seen[k] = 1;
    stack.push(k);
  };
  for (let i = 0; i < w; i++) { push(i, 0); push(i, h - 1); }
  for (let j = 0; j < h; j++) { push(0, j); push(w - 1, j); }
  while (stack.length) {
    const k = stack.pop();
    const i = k % w, j = (k - i) / w;
    push(i + 1, j); push(i - 1, j); push(i, j + 1); push(i, j - 1);
  }
  for (let k = 0; k < bits.length; k++) if (!bits[k] && !seen[k]) bits[k] = 1;
}

/** 屋檐下只能走进 reach 格：从"没有屋顶的空格"出发在空格里走 reach 步，走不到的有顶空格填实。 */
function fillUnderCover(bits, roof, w, h, reach) {
  let any = false;
  for (let k = 0; k < roof.length && !any; k++) any = roof[k] === 1 && bits[k] === 0;
  if (!any) return;
  const dist = new Int16Array(w * h).fill(-1);
  let frontier = [];
  for (let k = 0; k < bits.length; k++) if (!bits[k] && !roof[k]) { dist[k] = 0; frontier.push(k); }
  for (let step = 1; step <= reach && frontier.length; step++) {
    const next = [];
    for (const k of frontier) {
      const i = k % w, j = (k - i) / w;
      for (const [di, dj] of [[1, 0], [-1, 0], [0, 1], [0, -1]]) {
        const ii = i + di, jj = j + dj;
        if (ii < 0 || jj < 0 || ii >= w || jj >= h) continue;
        const kk = jj * w + ii;
        if (bits[kk] || dist[kk] >= 0) continue;
        dist[kk] = step;
        next.push(kk);
      }
    }
    frontier = next;
  }
  for (let k = 0; k < bits.length; k++) if (!bits[k] && roof[k] && dist[k] < 0) bits[k] = 1;
}

/** 按圆盘膨胀 r 格（人的半径）：之后一个点的查询就等于一个圆的查询。 */
function dilate(bits, w, h, r, exactR) {
  const out = new Uint8Array(w * h);
  const disc = [];
  // 圆盘半径按格数取整：被墙占的那格本身已经多出半格多（rasterize 的容差），这里不再加半格，否则走道两边各被吃掉 0.1 米
  for (let dj = -r; dj <= r; dj++) for (let di = -r; di <= r; di++) if (Math.hypot(di, dj) <= exactR + 0.01) disc.push([di, dj]);
  for (let j = 0; j < h; j++) {
    for (let i = 0; i < w; i++) {
      if (!bits[j * w + i]) continue;
      for (const [di, dj] of disc) {
        const ii = i + di, jj = j + dj;
        if (ii >= 0 && jj >= 0 && ii < w && jj < h) out[jj * w + ii] = 1;
      }
    }
  }
  return out;
}
