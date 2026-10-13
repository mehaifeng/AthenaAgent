// 建筑套件（设计稿 M2 行"建筑套件"）：所有建筑都由这里的构件组装——台基与台阶、墙与门窗、柱廊、各种屋顶、
// 檐部与山花、栏墙、烟囱、瓦砾、常春藤、脚手架。构件只管几何，材质由调用方给；同一座建筑里同一种材质的零件
// 合成一个网格，柱子这类重复件用实例化（Kit）。柱子、柱头、三陇板、棕叶饰取自打样（Artwork/Polis/sample/）。
// 坐标：地块中心为原点、地面 y = 0、正面朝 +z（世界里再按朝向旋转、平移）。
import * as THREE from '../../vendor/three.bundle.mjs';
import { mergeGeometries } from '../../vendor/three.bundle.mjs';
import { lerp, deg } from './noise.mjs';
import { glassUv, LAMP_KINDS } from './lamps.mjs';

const KEEP = ['position', 'normal', 'uv'];

function normalizeGeometry(geometry) {
  const g = geometry.index ? geometry.toNonIndexed() : geometry.clone();
  for (const name of Object.keys(g.attributes)) if (!KEEP.includes(name)) g.deleteAttribute(name);
  if (!g.attributes.normal) g.computeVertexNormals();
  if (!g.attributes.uv) g.setAttribute('uv', new THREE.Float32BufferAttribute(new Float32Array(g.attributes.position.count * 2), 2));
  return g;
}

const tmpQ = new THREE.Quaternion(), tmpE = new THREE.Euler(), tmpS = new THREE.Vector3(), tmpP = new THREE.Vector3();
export function m4(x = 0, y = 0, z = 0, ry = 0, rx = 0, rz = 0, sx = 1, sy = sx, sz = sx) {
  tmpE.set(rx, ry, rz, 'YXZ');
  return new THREE.Matrix4().compose(tmpP.set(x, y, z), tmpQ.setFromEuler(tmpE), tmpS.set(sx, sy, sz));
}

export const shadowed = (o) => { o.castShadow = true; o.receiveShadow = true; return o; };

export class Kit {
  constructor() {
    this.byMaterial = new Map();
    this.instances = new Map();
    this.objects = [];
    this.lights = [];
    this.doors = [];
  }

  /**
   * 登记一处夜里的灯火（建筑本地坐标；n 是它朝外的方向，灯笼为 0）：建城时换算成世界坐标烘进光照图（lamps.mjs），
   * 街面和墙脚靠它被照亮。kind 取 LAMP_KINDS 的默认强度与照多远。
   */
  light(kind, x, y, z, { nx = 0, nz = 0, power, radius } = {}) {
    const base = LAMP_KINDS[kind];
    if (!base) throw new Error(`Kit: unknown light kind ${kind}`);
    this.lights.push({ kind, x, y, z, nx, nz, power: power ?? base.power, radius: radius ?? base.radius });
    return this;
  }

  add(material, geometry, matrix = null) {
    if (!material) throw new Error('Kit: a part needs a material');
    const g = normalizeGeometry(geometry);
    if (matrix) g.applyMatrix4(matrix);
    if (!this.byMaterial.has(material)) this.byMaterial.set(material, []);
    this.byMaterial.get(material).push(g);
    return this;
  }

  box(material, w, h, d, x, y, z, ry = 0, rx = 0, rz = 0) {
    return this.add(material, uvScaled(new THREE.BoxGeometry(w, h, d), w, h, d), m4(x, y, z, ry, rx, rz));
  }

  cylinder(material, rTop, rBottom, h, x, y, z, segments = 16, rx = 0, rz = 0, ry = 0) {
    return this.add(material, new THREE.CylinderGeometry(rTop, rBottom, h, segments), m4(x, y, z, ry, rx, rz));
  }

  /** 重复件：同一个 key 的几何只生成一次，按矩阵实例化。 */
  instance(key, makeGeometry, material, matrix) {
    let pool = this.instances.get(key);
    if (!pool) {
      pool = { geometry: makeGeometry(), material, matrices: [] };
      this.instances.set(key, pool);
    }
    pool.matrices.push(matrix);
    return this;
  }

  object(obj) {
    this.objects.push(obj);
    return obj;
  }

  build(group = new THREE.Group()) {
    for (const [material, list] of this.byMaterial) {
      const merged = mergeGeometries(list, false);
      if (!merged) throw new Error('Kit: geometries could not be merged (attribute mismatch)');
      group.add(shadowed(new THREE.Mesh(merged, material)));
    }
    for (const pool of this.instances.values()) {
      const inst = shadowed(new THREE.InstancedMesh(pool.geometry, pool.material, pool.matrices.length));
      pool.matrices.forEach((m, k) => inst.setMatrixAt(k, m));
      inst.instanceMatrix.needsUpdate = true;
      inst.computeBoundingSphere();
      group.add(inst);
    }
    for (const obj of this.objects) group.add(obj);
    if (this.lights.length) group.userData.lights = [...(group.userData.lights ?? []), ...this.lights];
    if (this.doors.length) group.userData.doors = [...(group.userData.doors ?? []), ...this.doors];
    return group;
  }
}

/** 盒子的 UV 按尺寸缩放（每 2 米一个贴图周期），大小不同的墙面纹理密度一致。 */
export function uvScaled(g, w, h, d, period = 2) {
  const uv = g.attributes.uv, n = g.attributes.normal;
  for (let i = 0; i < uv.count; i++) {
    const ax = Math.abs(n.getX(i)), ay = Math.abs(n.getY(i));
    const su = ax > 0.5 ? d : w;
    const sv = ay > 0.5 ? d : h;
    uv.setXY(i, uv.getX(i) * su / period, uv.getY(i) * sv / period);
  }
  return g;
}

/** 平铺地面：UV 每 tile 米一个周期。 */
export function groundPlane(w, d, tile = 4) {
  const g = new THREE.PlaneGeometry(w, d);
  g.rotateX(-Math.PI / 2);
  const uv = g.attributes.uv;
  for (let i = 0; i < uv.count; i++) uv.setXY(i, uv.getX(i) * w / tile, uv.getY(i) * d / tile);
  return g;
}

// ------------------------------------------------------------------
// 柱子（打样）：带凹槽和收分的柱身 + 柱头。相邻凹槽在棱线处不共用顶点，棱线才是锐的。
// ------------------------------------------------------------------
function flutedShaft({ r0, r1, h, flutes = 20, seg = 6, hSeg = 30, depth = 0.07, entasis = 0.012 }) {
  const pos = [], uv = [], idx = [];
  const radiusAt = (y) => {
    const t = y / h;
    return lerp(r0, r1, t) + entasis * Math.sin(Math.PI * Math.min(1, t / 0.96));
  };
  let base = 0;
  const row = seg + 1;
  for (let f = 0; f < flutes; f++) {
    for (let j = 0; j <= hSeg; j++) {
      const y = (j / hSeg) * h, R = radiusAt(y);
      for (let k = 0; k <= seg; k++) {
        const t = k / seg, th = ((f + t) / flutes) * Math.PI * 2;
        const rr = R * (1 - depth * Math.sin(Math.PI * t));
        pos.push(rr * Math.sin(th), y, rr * Math.cos(th));
        uv.push(((f + t) / flutes) * 2, (y / h) * 3);
      }
    }
    for (let j = 0; j < hSeg; j++) for (let k = 0; k < seg; k++) {
      const a = base + j * row + k, b = a + 1, c = a + row, d = c + 1;
      idx.push(a, b, c, b, d, c);
    }
    base += (hSeg + 1) * row;
  }
  const g = new THREE.BufferGeometry();
  g.setAttribute('position', new THREE.Float32BufferAttribute(pos, 3));
  g.setAttribute('uv', new THREE.Float32BufferAttribute(uv, 2));
  g.setIndex(idx);
  g.computeVertexNormals();
  return g;
}

function doricCapital(r1) {
  const pts = [
    new THREE.Vector2(r1 - 0.02, 0.0),
    new THREE.Vector2(r1 + 0.01, 0.012), new THREE.Vector2(r1 - 0.004, 0.024),
    new THREE.Vector2(r1 + 0.014, 0.036), new THREE.Vector2(r1 + 0.004, 0.048),
  ];
  for (let i = 1; i <= 12; i++) {
    const t = i / 12;
    pts.push(new THREE.Vector2(r1 + (0.64 - r1) * Math.pow(t, 0.6), 0.048 + 0.19 * Math.pow(t, 1.35)));
  }
  pts.push(new THREE.Vector2(0.0, 0.238));
  const echinus = new THREE.LatheGeometry(pts, 32);
  const abacus = new THREE.BoxGeometry(1.28, 0.17, 1.28);
  abacus.translate(0, 0.238 + 0.085, 0);
  return mergeGeometries([echinus.toNonIndexed(), abacus.toNonIndexed()]);
}

/** 爱奥尼亚柱头：两侧卷涡（压扁的圆环）+ 薄柱冠。远看要和多立克分得开，所以卷涡做得比例偏大。 */
function ionicCapital(r1) {
  const parts = [];
  const echinus = new THREE.CylinderGeometry(r1 + 0.08, r1, 0.12, 24);
  echinus.translate(0, 0.06, 0);
  parts.push(echinus.toNonIndexed());
  for (const sx of [-1, 1]) {
    // 卷涡朝前（圆环在 xy 平面里）：M2 美术审查时第一版转了 90°，从正面看只是两条竖着的细边，和多立克分不开
    const volute = new THREE.TorusGeometry(0.21, 0.08, 8, 22);
    volute.translate(sx * 0.44, 0.02, 0);
    parts.push(volute.toNonIndexed());
  }
  const pad = new THREE.BoxGeometry(0.98, 0.1, 0.5);
  pad.translate(0, 0.14, 0);
  parts.push(pad.toNonIndexed());
  const abacus = new THREE.BoxGeometry(0.96, 0.08, 0.96);
  abacus.translate(0, 0.23, 0);
  parts.push(abacus.toNonIndexed());
  return mergeGeometries(parts);
}

const columnCache = new Map();
/** 一根完整的柱子（柱身 + 柱头），按柱高缩放；同样参数的只生成一次。order：'doric' | 'ionic'。 */
export function column(height, { detail = 'full', order = 'doric' } = {}) {
  const key = `${height.toFixed(3)}:${detail}:${order}`;
  if (columnCache.has(key)) return columnCache.get(key);
  // 按打样的比例（柱底径 1、柱身 5.2、柱头 0.408）整体缩放到目标柱高；爱奥尼亚柱更细（柱身 5.9）
  const ionic = order === 'ionic';
  const shaftH = ionic ? 5.9 : 5.2;
  const s = height / (shaftH + (ionic ? 0.27 : 0.408));
  const shaft = detail === 'full'
    ? flutedShaft({ r0: ionic ? 0.42 : 0.5, r1: ionic ? 0.36 : 0.39, h: shaftH, flutes: ionic ? 24 : 20 })
    : flutedShaft({ r0: ionic ? 0.42 : 0.5, r1: ionic ? 0.36 : 0.39, h: shaftH, flutes: 16, seg: 3, hSeg: 8 });
  const cap = ionic ? ionicCapital(0.36) : doricCapital(0.39);
  cap.translate(0, shaftH, 0);
  const parts = [shaft.toNonIndexed(), cap];
  if (ionic) {
    const base = new THREE.CylinderGeometry(0.5, 0.56, 0.22, 24);
    base.translate(0, 0.11, 0);
    parts.push(base.toNonIndexed());
  }
  const g = mergeGeometries(parts);
  g.scale(s, s, s);
  columnCache.set(key, g);
  return g;
}

/** 兼容旧名：多立克柱。 */
export function doricColumn(height, options = {}) {
  return column(height, { ...options, order: 'doric' });
}

export function triglyphGeometry(w, h) {
  const parts = [];
  const back = new THREE.BoxGeometry(w, h, 0.08); back.translate(0, 0, -0.03); parts.push(back);
  for (const x of [-0.16, 0, 0.16]) {
    const bar = new THREE.BoxGeometry(0.1, h - 0.06, 0.12); bar.translate(x, -0.03, 0.0); parts.push(bar);
  }
  const cap = new THREE.BoxGeometry(w + 0.02, 0.07, 0.13); cap.translate(0, h / 2 - 0.035, 0.0); parts.push(cap);
  return mergeGeometries(parts.map((p) => p.toNonIndexed()));
}

export function palmette(height, width) {
  const s = new THREE.Shape();
  s.moveTo(0, 0);
  s.bezierCurveTo(width * 0.55, height * 0.12, width * 0.62, height * 0.62, 0, height);
  s.bezierCurveTo(-width * 0.62, height * 0.62, -width * 0.55, height * 0.12, 0, 0);
  const g = new THREE.ExtrudeGeometry(s, { depth: 0.08, bevelEnabled: true, bevelSize: 0.02, bevelThickness: 0.02, bevelSegments: 2 });
  g.translate(0, 0, -0.04);
  return g;
}

/** 三角山花：底宽 w、高 h、厚 d，底边在 y = 0，居中。 */
export function gable(w, h, d) {
  const s = new THREE.Shape();
  s.moveTo(-w / 2, 0); s.lineTo(w / 2, 0); s.lineTo(0, h); s.closePath();
  const g = new THREE.ExtrudeGeometry(s, { depth: d, bevelEnabled: false });
  g.translate(0, 0, -d / 2);
  return g;
}

// ------------------------------------------------------------------
// 构件
// ------------------------------------------------------------------

/** 台基：steps 级台阶，每级高 stepH、外扩 stepT；返回顶面高度。 */
export function podium(kit, mat, w, d, { steps = 3, stepH = 0.25, stepT = 0.3, z = 0 } = {}) {
  for (let i = 0; i < steps; i++) {
    const grow = (steps - 1 - i) * stepT * 2;
    kit.box(mat, w + grow, stepH, d + grow, 0, i * stepH + stepH / 2, z);
  }
  return steps * stepH;
}

/**
 * 门：门洞（深色；夜里透出屋里的灯火）+ 门楣；返回门的高度。门总是亮的——不少建筑（金库、仓房）只有这一个开口，
 * 它不亮整座房子夜里就是黑的；明暗的变化交给窗。
 */
export function door(kit, M, w, h, x, y, z, { lintel = M.wood, frame = null, lantern = 0 } = {}) {
  litOpening(kit, M, w, h, 0.08, x, y + h / 2, z + 0.02, true);
  // 第三轮（需求 2）：门登记在套件上——门洞中心、门槛高、宽高，墙面在 z（建筑本地坐标，正面朝 +z）。
  // 会开的门扇不进合并的几何（它要转），由 world 按这一条另建；地面上的门才有人进出（doors.usableSill）
  kit.doors?.push({ x, y, z, w, h });
  kit.box(lintel, w + 0.3, 0.14, 0.14, x, y + h + 0.07, z + 0.05);
  if (frame) for (const sx of [-1, 1]) kit.box(frame, 0.12, h, 0.1, x + sx * (w / 2 + 0.06), y + h / 2, z + 0.04);
  // 门边挂壁灯（lantern = -1 左 / 1 右 / 'both' 两边）：夜里门前一片街面靠它亮起来
  if (lantern && M.lanternPane) for (const sx of lantern === 'both' ? [-1, 1] : [lantern]) wallLantern(kit, M, x + sx * (w / 2 + 0.42), y + h + 0.32, z);
  return h;
}

/**
 * 透出灯火的开口（门洞、窗洞、神庙的门）：UV 编好位置和火苗相位（lamps.mjs 的 glassUv），登记一处朝外（+z）的灯火。
 * 没有 windowLit 的材质表（极简的测试桩）就是一块深色。
 */
export function litOpening(kit, M, w, h, d, x, y, z, isDoor, power) {
  if (!M.windowLit) return kit.box(M.dark, w, h, d, x, y, z);
  kit.add(M.windowLit, glassUv(new THREE.BoxGeometry(w, h, d), { x, y, z, door: isDoor }), m4(x, y, z));
  // 光从开口中间偏下出来（屋里的灯放在桌上、地上）
  kit.light(isDoor ? 'door' : 'window', x, y - h * 0.15, z + d / 2, { nz: 1, ...(power ? { power: LAMP_KINDS[isDoor ? 'door' : 'window'].power * power } : {}) });
  return kit;
}

/**
 * 小窗：深色窗洞，带一道窗台。夜里窗洞透出灯火（M.windowLit）——按位置定，大约四扇里有一扇不点灯，满城的窗不会一齐亮着。
 */
export function window_(kit, M, w, h, x, y, z) {
  const lit = M.windowLit && windowLitAt(x, y, z);
  if (lit) litOpening(kit, M, w, h, 0.06, x, y, z + 0.02, false, y > 3 ? 0.7 : 1);
  else kit.box(M.dark, w, h, 0.06, x, y, z + 0.02);
  kit.box(M.stone, w + 0.16, 0.07, 0.16, x, y - h / 2 - 0.035, z + 0.06);
}

/** 这扇窗夜里点不点灯：只由它在建筑里的位置决定（同一座房子每次重建都一样）。 */
export function windowLitAt(x, y, z) {
  const h = Math.sin(x * 12.9898 + y * 78.233 + z * 37.719) * 43758.5453;
  return h - Math.floor(h) > 0.25;
}

/**
 * 吊灯笼（2026-10-13，用户：柱廊的灯笼只是两个发光几何体，完全没有设计）。照庞贝出土的青铜提灯：
 * 顶上一枚挂钩和一段椭圆环的链子；尖顶的铜盖，盖尖一颗圆钮、盖沿一道箍；一圈六片刮薄的牛角做灯罩（lanternPane，
 * 夜里从里面透出火光，片与片之间是铜骨）、上下两道铜箍；底下一只浅铜盘，盘底垂一颗滴珠。
 * (x, yHook, z) 是挂钩的位置，drop 是链长，scale 整体缩放（壁灯用小一号）。返回灯罩中心的高度。
 */
export function lantern(kit, M, x, yHook, z, { drop = 0.4, scale = 1 } = {}) {
  const s = scale, b = M.bronze;
  // 挂钩：一枚小圆盘钉在梁下，下面一个环
  kit.cylinder(b, 0.05 * s, 0.06 * s, 0.03 * s, x, yHook - 0.015 * s, z, 10);
  kit.add(b, new THREE.TorusGeometry(0.03 * s, 0.007 * s, 5, 10), m4(x, yHook - 0.06 * s, z));
  // 链子：一节节椭圆环，相邻两节转 90°
  const linkL = 0.062 * s, top = yHook - 0.085 * s, yTop = yHook - drop;
  const links = Math.max(1, Math.round((top - yTop - 0.04 * s) / linkL));
  for (let i = 0; i < links; i++) {
    kit.instance('lantern-link', () => new THREE.TorusGeometry(0.02, 0.0055, 4, 10), b,
      m4(x, top - (i + 0.5) * ((top - yTop - 0.04 * s) / links), z, (i % 2) * Math.PI / 2, 0, 0, s, s * 1.55, s));
  }
  // 提环与盖：提环、盖尖的圆钮、尖顶（两段，下段外撇）、盖沿一道箍
  kit.add(b, new THREE.TorusGeometry(0.045 * s, 0.008 * s, 5, 12), m4(x, yTop - 0.01 * s, z));
  kit.add(b, new THREE.SphereGeometry(0.028 * s, 8, 6), m4(x, yTop - 0.065 * s, z));
  kit.cylinder(b, 0.018 * s, 0.07 * s, 0.09 * s, x, yTop - 0.12 * s, z, 12);
  kit.cylinder(b, 0.07 * s, 0.2 * s, 0.09 * s, x, yTop - 0.205 * s, z, 12);
  kit.add(b, new THREE.TorusGeometry(0.198 * s, 0.014 * s, 5, 24), m4(x, yTop - 0.25 * s, z, 0, Math.PI / 2));
  // 灯罩：六片牛角（开口圆筒，UV 一圈 0..1——着色器按六片画铜骨），六根铜骨，上下两道箍
  const bodyTop = yTop - 0.26 * s, bodyH = 0.34 * s, bodyY = bodyTop - bodyH / 2, r = 0.168 * s;
  kit.add(M.lanternPane ?? M.windowLit ?? M.dark, glassUv(new THREE.CylinderGeometry(r, r * 0.94, bodyH, 18, 1, true), { x, y: bodyY, z }), m4(x, bodyY, z));
  for (let k = 0; k < 6; k++) {
    const a = (k / 6) * Math.PI * 2;
    kit.box(b, 0.022 * s, bodyH + 0.02 * s, 0.022 * s, x + Math.cos(a) * (r + 0.004 * s), bodyY, z + Math.sin(a) * (r + 0.004 * s), -a);
  }
  for (const yy of [bodyTop, bodyTop - bodyH]) kit.add(b, new THREE.TorusGeometry(r + 0.012 * s, 0.013 * s, 5, 24), m4(x, yy, z, 0, Math.PI / 2));
  // 底盘与滴珠
  const yBase = bodyTop - bodyH;
  kit.cylinder(b, 0.19 * s, 0.12 * s, 0.05 * s, x, yBase - 0.025 * s, z, 16);
  kit.add(b, new THREE.SphereGeometry(0.03 * s, 8, 6), m4(x, yBase - 0.075 * s, z));
  kit.cylinder(b, 0.022 * s, 0.002 * s, 0.09 * s, x, yBase - 0.135 * s, z, 8);
  kit.light('lantern', x, bodyY, z, { power: LAMP_KINDS.lantern.power * Math.min(1, s * 1.2) });
  return bodyY;
}

/**
 * 街灯（2026-10-13，用户：外面的街道没有被打亮）：街口的一根青铜灯柱。方石座上三只兽足撑起一根收分的细柱，
 * 柱上两颗圆节，柱顶一只承盘，伸出一根带卷草托的铜臂，臂端吊一盏灯笼（lantern）。本地原点在柱脚，臂朝 +z
 * （放的时候转向街口）。柱子只有 9 厘米粗，挡路的只是脚下一小圈；灯笼最低处在 2 米以上。
 */
export function lampPost(kit, M) {
  const b = M.bronze, H = 2.85;
  kit.box(M.stone, 0.46, 0.22, 0.46, 0, 0.11, 0);
  kit.box(M.stone, 0.36, 0.06, 0.36, 0, 0.25, 0);
  for (let k = 0; k < 3; k++) {
    const a = (k / 3) * Math.PI * 2 + Math.PI / 6;
    kit.box(b, 0.05, 0.22, 0.05, Math.sin(a) * 0.1, 0.38, Math.cos(a) * 0.1, a, 0.5);
    kit.add(b, new THREE.SphereGeometry(0.038, 8, 6), m4(Math.sin(a) * 0.15, 0.3, Math.cos(a) * 0.15, 0, 0, 0, 1, 0.7, 1));
  }
  kit.cylinder(b, 0.07, 0.09, 0.12, 0, 0.5, 0, 12);
  kit.cylinder(b, 0.032, 0.045, H - 0.56, 0, 0.56 + (H - 0.56) / 2, 0, 10);
  for (const y of [1.15, 2.05]) kit.add(b, new THREE.SphereGeometry(0.06, 10, 8), m4(0, y, 0, 0, 0, 0, 1, 0.75, 1));
  kit.cylinder(b, 0.13, 0.05, 0.08, 0, H + 0.04, 0, 14);
  kit.add(b, new THREE.SphereGeometry(0.04, 8, 6), m4(0, H + 0.12, 0));
  // 臂与卷草托（和壁灯同一种做法），臂端吊灯笼
  const reach = 0.55, y = H - 0.1;
  kit.box(b, 0.03, 0.03, reach, 0, y, reach / 2);
  const strut = Math.hypot(0.24, reach * 0.7);
  kit.box(b, 0.022, 0.022, strut, 0, y - 0.12, reach * 0.35, 0, -Math.atan2(0.24, reach * 0.7));
  kit.add(b, new THREE.TorusGeometry(0.07, 0.01, 5, 14, Math.PI * 1.5), m4(0, y - 0.075, reach * 0.42, Math.PI / 2, 0, Math.PI * 0.25));
  kit.add(b, new THREE.SphereGeometry(0.024, 8, 6), m4(0, y, reach));
  lantern(kit, M, 0, y - 0.02, reach, { drop: 0.12, scale: 0.85 });
  return kit;
}

/**
 * 壁灯：墙上一块铜板，伸出一根带卷草托的铜臂，臂端吊一盏小一号的灯笼（lantern）。(x, y, z) 是铜板贴墙的位置（墙面 z），
 * 臂向 +z 伸出 0.42 米。挂在门边 2 米多高，最低处也在身体那一段（0.35–1.6 米）之上，不挡路。
 */
export function wallLantern(kit, M, x, y, z) {
  const b = M.bronze, reach = 0.42;
  kit.box(b, 0.12, 0.26, 0.03, x, y - 0.06, z + 0.02);
  kit.add(b, new THREE.SphereGeometry(0.018, 6, 4), m4(x, y + 0.04, z + 0.04));
  kit.add(b, new THREE.SphereGeometry(0.018, 6, 4), m4(x, y - 0.16, z + 0.04));
  // 臂：一根平伸的方铜条，下面一根斜撑，斜撑和臂之间一个四分之三圈的卷草
  kit.box(b, 0.026, 0.026, reach, x, y, z + reach / 2 + 0.03);
  const strut = Math.hypot(0.2, reach * 0.7);
  kit.box(b, 0.02, 0.02, strut, x, y - 0.1, z + reach * 0.35 + 0.03, 0, -Math.atan2(0.2, reach * 0.7));
  kit.add(b, new THREE.TorusGeometry(0.06, 0.009, 5, 14, Math.PI * 1.5), m4(x, y - 0.065, z + reach * 0.42, Math.PI / 2, 0, Math.PI * 0.25));
  // 臂端一颗圆钮，灯笼吊在它下面
  kit.add(b, new THREE.SphereGeometry(0.022, 8, 6), m4(x, y, z + reach + 0.03));
  return lantern(kit, M, x, y - 0.02, z + reach, { drop: 0.1, scale: 0.72 });
}

/**
 * 双坡屋顶：屋脊沿 x，宽 w（沿 x）、进深 d、坡高 h；两片坡面 + 两端山墙 + 屋脊，坐在 y 上，中心在 (x, z)。
 * x 是 M2 美术审查补的：并排两户的民居第一版两片屋顶都落在地块中线上，各自外侧一半没有屋顶。
 */
export function gabledRoof(kit, roofMat, gableMat, w, d, h, y, z = 0, overhang = 0.35, x = 0) {
  const half = d / 2 + overhang;
  const slope = Math.hypot(half, h);
  const pitch = Math.atan2(h, half);
  for (const sz of [1, -1]) kit.box(roofMat, w + overhang * 2, 0.16, slope, x, y + h / 2 + 0.06, z + sz * half / 2, 0, sz * pitch);
  for (const sx of [1, -1]) kit.add(gableMat, gable(d, h, 0.2), m4(x + sx * (w / 2 - 0.1), y, z, Math.PI / 2));
  kit.cylinder(roofMat, 0.09, 0.09, w + overhang * 2, x, y + h + 0.1, z, 8, 0, Math.PI / 2);
}

/**
 * 顺着屋面的侧墙（单坡屋顶的两端）：墙脚在 yBottom，墙顶按 topAt(z) 贴着屋面走，从 zA 到 zB，厚 t、中心在 x。
 * 第一版是一块固定坡度的三角，坡度和屋面不一样、还偏出墙体 0.45 米：一端在屋面下留出黑缝，另一端从屋面上戳出白角。
 */
export function sideGable(kit, mat, { x, zA, zB, yBottom, topAt, t = 0.45 }) {
  const s = new THREE.Shape();
  s.moveTo(zA, yBottom); s.lineTo(zB, yBottom); s.lineTo(zB, topAt(zB)); s.lineTo(zA, topAt(zA)); s.closePath();
  // 形状的 x 是世界的 z、y 是世界的 y；绕 y 转 −90° 后挤出方向朝 −x，所以中心再挪半个厚度
  kit.add(mat, new THREE.ExtrudeGeometry(s, { depth: t, bevelEnabled: false }), m4(x + t / 2, 0, 0, -Math.PI / 2));
}

/**
 * 废墟的墙壳：屋顶没了，四面墙各断成几截、高低不一，里面露出泥地（废墟要看得进去——第一版是一个实心方块，
 * 顶上一块平平的白盖子，读起来是一座平顶房上搁了几根棍子）。gaps 是正面墙上的豁口（门洞）：[{ x, w }]。
 */
export function ruinShell(kit, mat, r, { w, h, d, x = 0, y = 0, z = 0, t = 0.3, floor = null, gaps = [] }) {
  const run = (len, alongX, cx, cz, front) => {
    const n = Math.max(2, Math.round(len / 1.5));
    const seg = len / n;
    for (let i = 0; i < n; i++) {
      const off = -len / 2 + seg * (i + 0.5);
      if (front && gaps.some((g) => Math.abs(cx + off - g.x) < g.w / 2 + seg / 2 - 0.2)) continue;
      const hh = h * (0.42 + r() * 0.58);
      if (alongX) kit.box(mat, seg + 0.02, hh, t, cx + off, y + hh / 2, cz);
      else kit.box(mat, t, hh, seg + 0.02, cx, y + hh / 2, cz + off);
    }
  };
  run(w, true, x, z + d / 2 - t / 2, true);
  run(w, true, x, z - d / 2 + t / 2, false);
  run(d - 2 * t, false, x - w / 2 + t / 2, z, false);
  run(d - 2 * t, false, x + w / 2 - t / 2, z, false);
  if (floor) kit.box(floor, w - 2 * t, 0.04, d - 2 * t, x, y + 0.02, z);
}

/** 屋脊朝前的双坡屋顶（山花朝正面，神殿式）：宽 w、进深 d、坡高 h。 */
export function frontGabledRoof(kit, roofMat, gableMat, w, d, h, y, z = 0, overhang = 0.3) {
  const half = w / 2 + overhang;
  const slope = Math.hypot(half, h);
  const pitch = Math.atan2(h, half);
  // 坡面的盒子长边顺着坡（局部 z），再转 90°：瓦垄的贴图（v 顺坡）才是朝下流的，不是横着的
  for (const sx of [1, -1]) kit.box(roofMat, d + overhang * 2, 0.15, slope, sx * half / 2, y + h / 2 + 0.06, z, Math.PI / 2, sx * pitch);
  kit.add(gableMat, gable(w + 0.1, h, 0.24), m4(0, y, z + d / 2 + overhang - 0.1));
  kit.add(gableMat, gable(w + 0.1, h, 0.24), m4(0, y, z - d / 2 - overhang + 0.1));
  kit.cylinder(roofMat, 0.09, 0.09, d + overhang * 2, 0, y + h + 0.08, z, 8, Math.PI / 2);
}

/** 单坡屋顶：前低后高，从正面看是一整片斜坡，没有山花三角（柱廊、棚子）。 */
export function shedRoof(kit, roofMat, w, d, rise, y, z = 0, overhang = 0.35) {
  const depth = d + overhang * 2;
  const pitch = Math.atan2(rise, depth);
  kit.box(roofMat, w + overhang * 2, 0.15, Math.hypot(rise, depth), 0, y + rise / 2, z, 0, pitch);
}

/** 平顶加一圈女儿墙（金库、粮仓）。 */
export function flatRoof(kit, roofMat, parapetMat, w, d, y, z = 0) {
  kit.box(roofMat, w + 0.3, 0.2, d + 0.3, 0, y + 0.1, z);
  for (const sz of [1, -1]) kit.box(parapetMat, w + 0.3, 0.45, 0.22, 0, y + 0.42, z + sz * (d / 2 + 0.04));
  for (const sx of [1, -1]) kit.box(parapetMat, 0.22, 0.45, d + 0.3, sx * (w / 2 + 0.04), y + 0.42, z);
}

/**
 * 一排柱子（沿 x）：n 根、从 x0 到 x1、立在 y、在 z；instanceKey 让同一座建筑里的柱子共用一份几何。
 * skip(i) 为 true 的位置不立柱（废墟里倒掉的那几根由调用方另画）。
 */
export function colonnade(kit, mat, { n, x0, x1, y, z, height, order = 'doric', detail = 'low', key = 'col', skip = null, twist = 0.7 }) {
  // 多立克柱各转一个角度，凹槽才不会排成一模一样的竖条；爱奥尼亚的卷涡必须朝前，不转
  const turn = order === 'ionic' ? 0 : twist;
  for (let i = 0; i < n; i++) {
    if (skip?.(i)) continue;
    const x = n === 1 ? (x0 + x1) / 2 : x0 + (i * (x1 - x0)) / (n - 1);
    kit.instance(`${key}-${order}-${height.toFixed(2)}`, () => column(height, { detail, order }), mat, m4(x, y, z, i * turn));
  }
}

/** 檐部：一条额枋 + 一道檐口（可选三陇板）。返回顶面高度。 */
export function entablature(kit, M, w, d, y, z, { triglyphs = 0, mat = M.marble } = {}) {
  kit.box(mat, w, 0.42, d, 0, y + 0.21, z);
  if (triglyphs > 0) {
    const tri = () => triglyphGeometry(0.38, 0.42);
    for (let i = 0; i < triglyphs; i++) kit.instance(`tri-${w.toFixed(1)}`, tri, mat, m4(-w / 2 + 0.3 + (i * (w - 0.6)) / Math.max(1, triglyphs - 1), y + 0.21, z + d / 2 + 0.05));
  }
  kit.box(mat, w + 0.3, 0.18, d + 0.3, 0, y + 0.51, z);
  return y + 0.6;
}

/** 栏墙：一圈矮墙（院墙、雕塑园的围墙），可在正面留门。 */
export function enclosure(kit, mat, w, d, h, { gateWidth = 0, thickness = 0.35 } = {}) {
  for (const sx of [-1, 1]) kit.box(mat, thickness, h, d, sx * w / 2, h / 2, 0);
  kit.box(mat, w, h, thickness, 0, h / 2, -d / 2);
  if (gateWidth > 0) {
    const seg = (w - gateWidth) / 2;
    for (const sx of [-1, 1]) kit.box(mat, seg, h, thickness, sx * (gateWidth / 2 + seg / 2), h / 2, d / 2);
  } else {
    kit.box(mat, w, h, thickness, 0, h / 2, d / 2);
  }
}

/** 烟囱：一根略收分的圆柱，顶上一圈。 */
export function chimney(kit, mat, x, z, h, r = 0.34) {
  kit.cylinder(mat, r * 0.85, r, h, x, h / 2, z, 10);
  kit.cylinder(mat, r * 1.05, r * 1.05, 0.18, x, h + 0.09, z, 10);
}

/** 瓦砾：散落在正面的几块石头（废墟）。 */
export function rubble(kit, M, r, n, w, d) {
  for (let i = 0; i < n; i++) {
    const s = 0.25 + r() * 0.45;
    kit.box(M.stone, s * 1.4, s, s, (r() - 0.5) * w, s / 2, d / 2 + 0.4 + r() * 1.2, r() * 3, r() * 0.3, r() * 0.3);
  }
}

/** 常春藤：从墙脚往上爬的一串串小叶团（一整块椭圆读起来像污渍，不像植物）。 */
export function ivy(kit, M, r, { w, d, h, y0 = 0, strands = 4 }) {
  for (let i = 0; i < strands; i++) {
    const face = Math.floor(r() * 3);
    const px = face === 0 ? (r() - 0.5) * w * 0.8 : (face === 1 ? 1 : -1) * (w / 2 + 0.06);
    const pz = face === 0 ? d / 2 + 0.06 : (r() - 0.5) * d * 0.8;
    const top = Math.min(h - 0.2, 1 + r() * h * 0.8);
    for (let y = 0.15; y < top; y += 0.22 + r() * 0.12) {
      const spread = 0.25 + (y / top) * 0.35;
      const ox = face === 0 ? (r() - 0.5) * spread * 2 : 0;
      const oz = face === 0 ? 0 : (r() - 0.5) * spread * 2;
      const s = 0.13 + r() * 0.12;
      kit.add(M.ivy, new THREE.IcosahedronGeometry(1, 0), m4(px + ox, y0 + y, pz + oz, r() * 6, r() * 6, 0, s, s, s * 0.6));
    }
  }
}

/** 脚手架：写入时搭在建筑正面的木架子（单独的件，按动作显隐）。 */
export function buildScaffold(M, width, height) {
  const kit = new Kit();
  const n = Math.max(2, Math.round(width / 2.2));
  for (let i = 0; i <= n; i++) {
    const x = -width / 2 + (i * width) / n;
    for (const z of [0, 0.9]) kit.cylinder(M.wood, 0.05, 0.05, height, x, height / 2, z, 6);
  }
  for (let y = 1.4; y < height; y += 1.4) {
    kit.box(M.wood, width + 0.2, 0.06, 1.1, 0, y, 0.45);
    kit.box(M.wood, width + 0.2, 0.05, 0.05, 0, y + 0.6, 0.9);
  }
  kit.box(M.wood, Math.hypot(width, height) * 0.95, 0.05, 0.05, 0, height / 2, 0.92, 0, 0, Math.atan2(height, width));
  return kit.build();
}

export { deg };
