// 建筑套件（设计稿 M2 行"建筑套件"）：所有建筑都由这里的构件组装——台基与台阶、墙与门窗、柱廊、各种屋顶、
// 檐部与山花、栏墙、烟囱、瓦砾、常春藤、脚手架。构件只管几何，材质由调用方给；同一座建筑里同一种材质的零件
// 合成一个网格，柱子这类重复件用实例化（Kit）。柱子、柱头、三陇板、棕叶饰取自打样（Artwork/Polis/sample/）。
// 坐标：地块中心为原点、地面 y = 0、正面朝 +z（世界里再按朝向旋转、平移）。
import * as THREE from '../../vendor/three.bundle.mjs';
import { mergeGeometries } from '../../vendor/three.bundle.mjs';
import { lerp, deg } from './noise.mjs';

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
    const volute = new THREE.TorusGeometry(0.17, 0.07, 8, 20);
    volute.rotateY(Math.PI / 2);
    volute.translate(sx * 0.42, 0.06, 0);
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

/** 门：门洞（深色）+ 门楣；返回门的高度。 */
export function door(kit, M, w, h, x, y, z, { lintel = M.wood, frame = null } = {}) {
  kit.box(M.dark, w, h, 0.08, x, y + h / 2, z + 0.02);
  kit.box(lintel, w + 0.3, 0.14, 0.14, x, y + h + 0.07, z + 0.05);
  if (frame) for (const sx of [-1, 1]) kit.box(frame, 0.12, h, 0.1, x + sx * (w / 2 + 0.06), y + h / 2, z + 0.04);
  return h;
}

/** 小窗：深色窗洞，带一道窗台。 */
export function window_(kit, M, w, h, x, y, z) {
  kit.box(M.dark, w, h, 0.06, x, y, z + 0.02);
  kit.box(M.stone, w + 0.16, 0.07, 0.16, x, y - h / 2 - 0.035, z + 0.06);
}

/** 双坡屋顶：屋脊沿 x，宽 w（沿 x）、进深 d、坡高 h；两片坡面 + 两端山墙 + 屋脊，坐在 y 上。 */
export function gabledRoof(kit, roofMat, gableMat, w, d, h, y, z = 0, overhang = 0.35) {
  const half = d / 2 + overhang;
  const slope = Math.hypot(half, h);
  const pitch = Math.atan2(h, half);
  for (const sz of [1, -1]) kit.box(roofMat, w + overhang * 2, 0.16, slope, 0, y + h / 2 + 0.06, z + sz * half / 2, 0, sz * pitch);
  for (const sx of [1, -1]) kit.add(gableMat, gable(d, h, 0.2), m4(sx * (w / 2 - 0.1), y, z, Math.PI / 2));
  kit.cylinder(roofMat, 0.09, 0.09, w + overhang * 2, 0, y + h + 0.1, z, 8, 0, Math.PI / 2);
}

/** 屋脊朝前的双坡屋顶（山花朝正面，神殿式）：宽 w、进深 d、坡高 h。 */
export function frontGabledRoof(kit, roofMat, gableMat, w, d, h, y, z = 0, overhang = 0.3) {
  const half = w / 2 + overhang;
  const slope = Math.hypot(half, h);
  const pitch = Math.atan2(h, half);
  for (const sx of [1, -1]) kit.box(roofMat, slope, 0.15, d + overhang * 2, sx * half / 2, y + h / 2 + 0.06, z, 0, 0, -sx * pitch);
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
  for (let i = 0; i < n; i++) {
    if (skip?.(i)) continue;
    const x = n === 1 ? (x0 + x1) / 2 : x0 + (i * (x1 - x0)) / (n - 1);
    kit.instance(`${key}-${order}-${height.toFixed(2)}`, () => column(height, { detail, order }), mat, m4(x, y, z, i * twist));
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
