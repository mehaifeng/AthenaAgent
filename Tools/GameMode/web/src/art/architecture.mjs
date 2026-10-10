// 建筑：全部由代码生成。多立克神庙、柱头、三陇板、棕叶饰取自 Artwork/Polis/sample/polis-sample.js；
// 柱廊书库、民居、作坊、雕塑园、金库、仓库、圆形图书馆、锻炉、市集、港口、城门、广场是 M0 新加的。
// 每个构造函数在本地坐标里造：地块中心为原点、地面 y = 0、正面朝 +z；世界里再按朝向旋转、平移。
import * as THREE from '../../vendor/three.bundle.mjs';
import { mergeGeometries } from '../../vendor/three.bundle.mjs';
import { lerp, deg, rng } from './noise.mjs';

// —— 合并工具：同一座建筑里同一种材质的零件合成一个网格，柱子这类重复件用实例化 ——
const KEEP = ['position', 'normal', 'uv'];

function normalizeGeometry(geometry) {
  const g = geometry.index ? geometry.toNonIndexed() : geometry.clone();
  for (const name of Object.keys(g.attributes)) if (!KEEP.includes(name)) g.deleteAttribute(name);
  if (!g.attributes.normal) g.computeVertexNormals();
  if (!g.attributes.uv) g.setAttribute('uv', new THREE.Float32BufferAttribute(new Float32Array(g.attributes.position.count * 2), 2));
  return g;
}

const tmpM = new THREE.Matrix4(), tmpQ = new THREE.Quaternion(), tmpE = new THREE.Euler(), tmpS = new THREE.Vector3(), tmpP = new THREE.Vector3();
export function m4(x = 0, y = 0, z = 0, ry = 0, rx = 0, rz = 0, sx = 1, sy = sx, sz = sx) {
  tmpE.set(rx, ry, rz, 'YXZ');
  return new THREE.Matrix4().compose(tmpP.set(x, y, z), tmpQ.setFromEuler(tmpE), tmpS.set(sx, sy, sz));
}

export class Kit {
  constructor() {
    this.byMaterial = new Map();
    this.instances = new Map();
    this.objects = [];
  }

  add(material, geometry, matrix = null) {
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

export const shadowed = (o) => { o.castShadow = true; o.receiveShadow = true; return o; };

/** 盒子的 UV 按尺寸缩放（每 2 米一个贴图周期），大小不同的墙面纹理密度一致。 */
function uvScaled(g, w, h, d, period = 2) {
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

const columnCache = new Map();
/** 一根完整的多立克柱（柱身 + 柱头），按柱高缩放；同样参数的只生成一次。 */
export function doricColumn(height, { detail = 'full' } = {}) {
  const key = `${height.toFixed(3)}:${detail}`;
  if (columnCache.has(key)) return columnCache.get(key);
  // 按打样的比例（柱底径 1、柱身 5.2、柱头 0.408）整体缩放到目标柱高
  const s = height / 5.608;
  const shaft = detail === 'full'
    ? flutedShaft({ r0: 0.5, r1: 0.39, h: 5.2 })
    : flutedShaft({ r0: 0.5, r1: 0.39, h: 5.2, flutes: 16, seg: 3, hSeg: 8 });
  const cap = doricCapital(0.39); cap.translate(0, 5.2, 0);
  const g = mergeGeometries([shaft.toNonIndexed(), cap]);
  g.scale(s, s, s);
  columnCache.set(key, g);
  return g;
}

function triglyphGeometry(w, h) {
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
function gable(w, h, d) {
  const s = new THREE.Shape();
  s.moveTo(-w / 2, 0); s.lineTo(w / 2, 0); s.lineTo(0, h); s.closePath();
  const g = new THREE.ExtrudeGeometry(s, { depth: d, bevelEnabled: false });
  g.translate(0, 0, -d / 2);
  return g;
}

/** 双坡屋顶：屋脊沿 x，宽 w（沿 x）、进深 d、坡高 h；两片坡面 + 两端山墙，坐在 y = 0 上。 */
function gabledRoof(kit, roofMat, gableMat, w, d, h, y, z = 0, overhang = 0.35) {
  const half = d / 2 + overhang;
  const slope = Math.hypot(half, h);
  const pitch = Math.atan2(h, half);
  for (const sz of [1, -1]) {
    kit.box(roofMat, w + overhang * 2, 0.16, slope, 0, y + h / 2 + 0.06, z + sz * half / 2, 0, sz * pitch);
  }
  for (const sx of [1, -1]) kit.add(gableMat, gable(d, h, 0.2), m4(sx * (w / 2 - 0.1), y, z, Math.PI / 2));
  kit.cylinder(roofMat, 0.09, 0.09, w + overhang * 2, 0, y + h + 0.1, z, 8, 0, Math.PI / 2);
}

// ------------------------------------------------------------------
// 多立克神庙（打样，参数化）：nx × nz 列，台基三级。scale 之前约为赫菲斯托斯神庙一类的比例。
// ------------------------------------------------------------------
export function buildTemple(M, { nx = 6, nz = 7 } = {}) {
  const kit = new Kit();
  const R1 = 0.39, H_SHAFT = 5.2, H_CAP = 0.408, H_COL = H_SHAFT + H_CAP;
  const AX = 2.55;
  const spanX = (nx - 1) * AX, spanZ = (nz - 1) * AX;
  const STEP_H = 0.33, STEP_T = 0.42, NSTEPS = 3;
  const styW = spanX + 1.6, styL = spanZ + 1.6;
  const yS = NSTEPS * STEP_H;

  for (let i = 0; i < NSTEPS; i++) {
    const grow = (NSTEPS - 1 - i) * STEP_T * 2;
    kit.box(M.marble, styW + grow, STEP_H, styL + grow, 0, i * STEP_H + STEP_H / 2, 0);
  }

  const colGeom = () => doricColumn(H_COL);
  let k = 0;
  for (let i = 0; i < nx; i++) for (let j = 0; j < nz; j++) {
    if (i !== 0 && i !== nx - 1 && j !== 0 && j !== nz - 1) continue;
    kit.instance('temple-column', colGeom, M.marble, m4(-spanX / 2 + i * AX, yS, -spanZ / 2 + j * AX, (k++) * 0.37));
  }

  const cellaW = spanX - 2 * 1.05 * AX, cellaL = spanZ - 2 * 1.45 * AX;
  kit.box(M.wall, cellaW, H_COL, cellaL, 0, yS + H_COL / 2, -0.2);
  kit.box(M.dark, 2.1, 4.1, 0.12, 0, yS + 2.05, -0.2 + cellaL / 2 + 0.02);
  for (const sx of [-1, 1]) kit.box(M.wall, 0.6, H_COL, 0.5, sx * (cellaW / 2 - 0.3), yS + H_COL / 2, -0.2 + cellaL / 2 + 0.2);

  const yA = yS + H_COL;
  const entW = spanX + 1.28, entL = spanZ + 1.28;
  const ARCH_H = 0.74, TAENIA = 0.07, FRIEZE_H = 0.74, CROWN = 0.07, CORN_H = 0.34, OVER = 0.42;
  kit.box(M.marble, entW - 0.06, ARCH_H, entL - 0.06, 0, yA + ARCH_H / 2, 0);
  kit.box(M.marble, entW + 0.02, TAENIA, entL + 0.02, 0, yA + ARCH_H + TAENIA / 2, 0);
  const yF = yA + ARCH_H + TAENIA;
  kit.box(M.marble, entW - 0.18, FRIEZE_H, entL - 0.18, 0, yF + FRIEZE_H / 2, 0);

  const along = (n, len) => {
    const a = [];
    for (let i = 0; i <= 2 * (n - 1); i++) a.push(-((n - 1) * AX) / 2 + (i * AX) / 2);
    a[0] = -len / 2 + 0.27; a[a.length - 1] = len / 2 - 0.27;   // 角部三陇板顶到转角（古典多立克的做法）
    return a;
  };
  const tri = () => triglyphGeometry(0.5, FRIEZE_H);
  for (const x of along(nx, entW)) {
    kit.instance('temple-triglyph', tri, M.marble, m4(x, yF + FRIEZE_H / 2, entL / 2 - 0.07, 0));
    kit.instance('temple-triglyph', tri, M.marble, m4(x, yF + FRIEZE_H / 2, -(entL / 2 - 0.07), Math.PI));
  }
  for (const z of along(nz, entL)) {
    kit.instance('temple-triglyph', tri, M.marble, m4(entW / 2 - 0.07, yF + FRIEZE_H / 2, z, Math.PI / 2));
    kit.instance('temple-triglyph', tri, M.marble, m4(-(entW / 2 - 0.07), yF + FRIEZE_H / 2, z, -Math.PI / 2));
  }

  kit.box(M.marble, entW + 0.06, CROWN, entL + 0.06, 0, yF + FRIEZE_H + CROWN / 2, 0);
  const yC = yF + FRIEZE_H + CROWN;
  kit.box(M.marble, entW + 2 * OVER, CORN_H, entL + 2 * OVER, 0, yC + CORN_H / 2, 0);

  // 山墙与屋顶：13° 坡，前后山花三角退进檐口（屋脊沿 z，正面朝 +z）
  const yR = yC + CORN_H;
  const RW = entW + 2 * OVER, RL = entL + 2 * OVER;
  const pitch = deg(13);
  const ridge = (RW / 2) * Math.tan(pitch);
  const tym = new THREE.Shape();
  tym.moveTo(-RW / 2 + 0.35, 0); tym.lineTo(RW / 2 - 0.35, 0); tym.lineTo(0, ridge - 0.12); tym.closePath();
  const prism = new THREE.ExtrudeGeometry(tym, { depth: RL - 0.8, bevelEnabled: false });
  prism.translate(0, 0, -(RL - 0.8) / 2);
  kit.add(M.marble, prism, m4(0, yR, 0));
  const slopeLen = Math.hypot(RW / 2, ridge);
  for (const sz of [1, -1]) for (const sx of [1, -1]) {
    kit.box(M.marble, slopeLen + 0.25, 0.32, 0.72, sx * RW / 4, yR + ridge / 2 + 0.06, sz * (RL / 2 - 0.36), 0, 0, -sx * pitch);
  }
  for (const sx of [1, -1]) kit.box(M.roof, slopeLen + 0.35, 0.12, RL + 0.08, sx * RW / 4, yR + ridge / 2 + 0.25, 0, 0, 0, -sx * pitch);
  const tile = () => {
    const g = new THREE.CylinderGeometry(0.075, 0.075, slopeLen + 0.3, 6, 1, false, 0, Math.PI);
    g.rotateZ(Math.PI / 2);
    return g;
  };
  for (const sx of [1, -1]) for (let z = -RL / 2 + 0.3; z <= RL / 2 - 0.3; z += 0.62) {
    kit.instance('temple-tile', tile, M.roof, m4(sx * RW / 4, yR + ridge / 2 + 0.33, z, 0, 0, -sx * pitch));
  }
  kit.cylinder(M.roof, 0.13, 0.13, RL + 0.1, 0, yR + ridge + 0.26, 0, 10, Math.PI / 2);
  for (const sz of [1, -1]) {
    kit.add(M.marble, palmette(1.15, 0.85), m4(0, yR + ridge + 0.22, sz * (RL / 2 - 0.36)));
    for (const sx of [1, -1]) kit.add(M.marble, palmette(0.6, 0.45), m4(sx * (RW / 2 - 0.25), yR + 0.2, sz * (RL / 2 - 0.36)));
  }
  const group = kit.build();
  group.userData.height = yR + ridge + 1.3;
  group.userData.front = styL / 2 + (NSTEPS - 1) * STEP_T;
  return group;
}

// ------------------------------------------------------------------
// 文件夹建筑。b 是夹具里的建筑（kind / sizeClass / state / key），r 是按名字定的随机数。
// 状态：常用的门口有陶罐和盆栽；很久没动的爬常春藤；废墟掀了屋顶、倒了柱子。
// ------------------------------------------------------------------
function decorate(kit, M, b, r, { w, d, h, y0 = 0 }) {
  const state = b.state;
  if (state === 'bustling' || state === 'lived') {
    // 门口的双耳瓶和盆栽：有人住
    const side = r() > 0.5 ? 1 : -1;
    kit.add(M.tile, amphoraBody(), m4(side * (w / 2 - 0.7), y0, d / 2 + 0.55, r() * 6, 0, 0, 0.9));
    if (state === 'bustling') {
      kit.cylinder(M.tile, 0.26, 0.2, 0.42, -side * (w / 2 - 0.6), y0 + 0.21, d / 2 + 0.5, 12);
      kit.add(M.olive, new THREE.IcosahedronGeometry(0.42, 2), m4(-side * (w / 2 - 0.6), y0 + 0.72, d / 2 + 0.5, 0, 0, 0, 1, 0.8, 1));
    }
  }
  if (state === 'ivy' || state === 'ruin') {
    // 常春藤：从墙脚往上爬的一串串小叶团（一整块椭圆读起来像污渍，不像植物）
    const strands = state === 'ruin' ? 6 : 4;
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
  if (b.incomplete) {
    // 未完全测绘：门边插一根测量杆，挂一块布
    kit.cylinder(M.wood, 0.04, 0.05, 2.6, w / 2 + 0.4, 1.3, d / 2 + 0.9, 6);
    kit.box(M.awning, 0.6, 0.4, 0.02, w / 2 + 0.72, 2.3, d / 2 + 0.9);
  }
}

function rubble(kit, M, r, n, w, d) {
  for (let i = 0; i < n; i++) {
    const s = 0.25 + r() * 0.45;
    kit.box(M.stone, s * 1.4, s, s, (r() - 0.5) * w, s / 2, d / 2 + 0.4 + r() * 1.2, r() * 3, r() * 0.3, r() * 0.3);
  }
}

/**
 * 柱廊书库：又宽又矮的长廊（stoa），一排密柱、单坡屋顶，后墙上是放卷轴的格架。
 * 第一版比例接近一座小神殿，和金库（门廊双柱 + 山花）分不开；柱廊的辨识度在于"长、矮、柱子多、没有山花"。
 */
export function buildStoaLibrary(M, b, r) {
  const kit = new Kit();
  const sc = b.sizeClass;
  const W = [9.4, 10.4, 11.2, 12][sc - 1], D = 5.0, H = 3.1;
  const n = [6, 7, 8, 9][sc - 1];
  const ruin = b.state === 'ruin';
  kit.box(M.marble, W + 0.9, 0.22, D + 0.9, 0, 0.11, 0);
  kit.box(M.marble, W + 0.45, 0.22, D + 0.45, 0, 0.33, 0);
  const y0 = 0.44;
  const wallH = ruin ? H * 0.62 : H;
  kit.box(M.wall, W, wallH, 0.45, 0, y0 + wallH / 2, -D / 2 + 0.25);
  for (const sx of [-1, 1]) kit.box(M.wall, 0.45, wallH, D - 0.6, sx * (W / 2 - 0.22), y0 + wallH / 2, -0.25);
  // 后墙内侧的卷轴格架：深色的格子里露出一截截卷轴的端头
  const rows = 3, cols = Math.max(3, n - 2);
  for (let i = 0; i < cols; i++) for (let j = 0; j < rows; j++) {
    const x = -W / 2 + 1.0 + (i + 0.5) * ((W - 2.0) / cols);
    const y = y0 + 0.45 + j * 0.78;
    if (ruin && y > y0 + wallH - 0.4) continue;
    kit.box(M.darkWood, (W - 2.0) / cols - 0.12, 0.62, 0.3, x, y + 0.31, -D / 2 + 0.6);
    for (let s = 0; s < 3; s++) kit.cylinder(M.papyrus, 0.07, 0.07, 0.26, x - 0.2 + s * 0.2, y + 0.18 + (s % 2) * 0.17, -D / 2 + 0.78, 8, Math.PI / 2);
  }
  const colH = H;
  const col = () => doricColumn(colH, { detail: 'low' });
  for (let i = 0; i < n; i++) {
    const x = -W / 2 + 0.55 + i * ((W - 1.1) / (n - 1));
    if (ruin && i % 2 === 1) {
      // 断柱：只剩一截，柱身横倒在台阶前
      kit.instance(`stoa-col-stump`, () => doricColumn(colH * 0.4, { detail: 'low' }), M.marble, m4(x, y0, D / 2 - 0.5, r() * 6));
      kit.cylinder(M.marble, 0.24, 0.27, colH * 0.45, x + 0.3, 0.27, D / 2 + 1.3, 12, 0, Math.PI / 2, r());
      continue;
    }
    kit.instance(`stoa-col-${colH}`, col, M.marble, m4(x, y0, D / 2 - 0.5, i * 0.7));
  }
  if (!ruin) {
    kit.box(M.marble, W + 0.2, 0.42, 0.75, 0, y0 + colH + 0.21, D / 2 - 0.5);
    // 单坡屋顶：前低后高，从正面看是一整片斜坡，没有山花三角
    const rise = 1.0, depth = D + 0.9;
    const pitch = Math.atan2(rise, depth);
    kit.box(M.tile, W + 0.7, 0.16, Math.hypot(rise, depth), 0, y0 + colH + 0.5 + rise / 2, -0.15, 0, pitch);
    for (const sx of [-1, 1]) {
      const tri = new THREE.Shape();
      tri.moveTo(-D / 2, 0); tri.lineTo(D / 2 - 0.4, 0); tri.lineTo(-D / 2, rise); tri.closePath();
      kit.add(M.wall, new THREE.ExtrudeGeometry(tri, { depth: 0.45, bevelEnabled: false }), m4(sx * (W / 2 - 0.22) - 0.225, y0 + colH + 0.42, 0, -Math.PI / 2));
    }
  } else {
    rubble(kit, M, r, 6, W, D);
  }
  decorate(kit, M, b, r, { w: W, d: D, h: wallH, y0 });
  const group = kit.build();
  group.userData.height = y0 + colH + 1.7;
  return group;
}

/** 民居：白灰墙、瓦顶、一扇门几扇小窗；体量大的两层，侧边一圈矮院墙。 */
export function buildHouse(M, b, r) {
  const kit = new Kit();
  const sc = b.sizeClass;
  const W = 6.6 + sc * 0.7, D = 6 + sc * 0.4;
  const storeys = sc >= 3 ? 2 : 1;
  const ruin = b.state === 'ruin';
  const H = storeys * 3 * (ruin ? 0.75 : 1);
  kit.box(M.stone, W + 0.3, 0.25, D + 0.3, 0, 0.125, 0);
  kit.box(M.plaster, W, H, D, 0, 0.25 + H / 2, 0);
  const doorX = (r() - 0.5) * W * 0.4;
  kit.box(M.dark, 1.05, 2.05, 0.08, doorX, 0.25 + 1.03, D / 2 + 0.02);
  kit.box(M.wood, 1.35, 0.14, 0.14, doorX, 0.25 + 2.13, D / 2 + 0.05);
  for (let s = 0; s < storeys; s++) {
    for (const wx of [-W * 0.34, W * 0.34]) {
      if (Math.abs(wx - doorX) < 1.2 && s === 0) continue;
      kit.box(M.dark, 0.6, 0.55, 0.06, wx, 0.25 + s * 3 + 1.9, D / 2 + 0.02);
    }
  }
  if (!ruin) {
    gabledRoof(kit, M.tile, M.plaster, W, D, 1.4, 0.25 + H, 0, 0.4);
  } else {
    // 塌了一半的屋顶：只剩几根檩条
    for (let i = 0; i < 4; i++) kit.box(M.darkWood, W * (0.5 + r() * 0.5), 0.14, 0.14, (r() - 0.5) * 1.5, 0.25 + H + 0.1, -D / 2 + 0.8 + i * (D / 4), r() * 0.2, 0, (r() - 0.5) * 0.4);
    rubble(kit, M, r, 5, W, D);
  }
  // 侧边院墙
  const side = r() > 0.5 ? 1 : -1;
  const yardW = Math.min(2.2, (12.5 - W) / 2 - 0.2);
  if (yardW > 0.8) {
    kit.box(M.plaster, 0.3, 1.5, D * 0.85, side * (W / 2 + yardW), 0.75, -D * 0.07);
    kit.box(M.plaster, yardW, 1.5, 0.3, side * (W / 2 + yardW / 2), 0.75, -D / 2 + 0.2);
  }
  decorate(kit, M, b, r, { w: W, d: D, h: H });
  const group = kit.build();
  group.userData.height = 0.25 + H + (ruin ? 0.5 : 1.6);
  return group;
}

/** 作坊：屋前搭着木棚，棚下是工作台和陶罐，屋侧一座窑和烟囱。 */
export function buildWorkshop(M, b, r) {
  const kit = new Kit();
  const sc = b.sizeClass;
  const W = 6.4 + sc * 0.6, D = 5.2, H = 3.2;
  const ruin = b.state === 'ruin';
  kit.box(M.stone, W + 0.3, 0.22, D + 0.3, 0, 0.11, -0.8);
  kit.box(M.plaster, W, H, D, 0, 0.22 + H / 2, -0.8);
  kit.box(M.dark, 2.2, 2.3, 0.08, -W * 0.18, 0.22 + 1.15, D / 2 - 0.78);
  if (!ruin) gabledRoof(kit, M.tile, M.plaster, W, D, 1.2, 0.22 + H, -0.8, 0.35);
  // 木棚：四根柱子撑一片斜顶
  const shedD = 2.6;
  for (const sx of [-1, 1]) for (const sz of [0, 1]) kit.cylinder(M.wood, 0.09, 0.1, 2.6, sx * (W / 2 - 0.4), 1.3, D / 2 - 0.8 + 0.3 + sz * shedD, 8);
  if (!ruin) kit.box(M.darkWood, W - 0.4, 0.12, shedD + 0.6, 0, 2.75, D / 2 - 0.8 + shedD / 2 + 0.3, 0, -0.12);
  kit.box(M.wood, 2.2, 0.12, 0.9, W * 0.18, 0.95, D / 2 + 0.9);   // 工作台
  for (const lx of [-0.95, 0.95]) for (const lz of [-0.35, 0.35]) kit.box(M.wood, 0.08, 0.9, 0.08, W * 0.18 + lx, 0.45, D / 2 + 0.9 + lz);
  // 窑与烟囱
  const kx = (r() > 0.5 ? 1 : -1) * (W / 2 + 1.1);
  kit.add(M.stone, new THREE.SphereGeometry(1.15, 16, 10, 0, Math.PI * 2, 0, Math.PI / 2), m4(kx, 0, -0.6));
  kit.cylinder(M.stone, 0.32, 0.4, 4.6, kx, 2.3, -1.3, 10);
  kit.box(M.dark, 0.6, 0.5, 0.1, kx, 0.35, 0.53);
  for (let i = 0; i < 4; i++) kit.add(M.tile, amphoraBody(), m4(W / 2 - 0.6 - i * 0.45, 0, D / 2 + 2.2, r() * 6, 0, 0, 0.8));
  if (ruin) rubble(kit, M, r, 5, W, D);
  decorate(kit, M, b, r, { w: W, d: D, h: H });
  const group = kit.build();
  group.userData.height = 0.22 + H + 1.6;
  return group;
}

/** 雕塑园：一圈矮墙围着几尊立像，后角种柏树。 */
export function buildSculptureGarden(M, b, r, statueGeometry) {
  const kit = new Kit();
  const W = 11, D = 10;
  const ruin = b.state === 'ruin';
  kit.add(M.pave, groundPlane(W, D, 3), m4(0, 0.03, 0));
  for (const sx of [-1, 1]) kit.box(M.marble, 0.4, 1.1, D, sx * W / 2, 0.55, 0);
  kit.box(M.marble, W, 1.1, 0.4, 0, 0.55, -D / 2);
  for (const sx of [-1, 1]) kit.box(M.marble, W / 2 - 1.4, 1.1, 0.4, sx * (W / 4 + 0.7), 0.55, D / 2);
  const n = 2 + b.sizeClass;
  for (let i = 0; i < n; i++) {
    const a = (i / n) * Math.PI * 1.2 - Math.PI * 0.1;
    const x = Math.cos(a + Math.PI) * 3.3, z = -1 + Math.sin(a + Math.PI) * 2.8;
    kit.box(M.marble, 0.9, 0.9, 0.9, x, 0.45, z);
    if (ruin && i % 2 === 0) {
      kit.add(M.marble, statueGeometry, m4(x + 0.6, 0.3, z + 1.1, r() * 6, Math.PI / 2, 0, 0.9));
    } else {
      kit.add(M.marble, statueGeometry, m4(x, 0.9, z, Math.atan2(-x, 4 - z) + (r() - 0.5) * 0.4, 0, 0, 1.05));
    }
  }
  decorate(kit, M, b, r, { w: W, d: D, h: 1.1 });
  const group = kit.build();
  group.userData.height = 3.4;
  group.userData.trees = [[-W / 2 + 1.2, -D / 2 + 1.2], [W / 2 - 1.2, -D / 2 + 1.2]];
  return group;
}

/** 金库：德尔斐那种"门廊双柱"的小神殿，三级台基、山花、三陇板。 */
export function buildTreasury(M, b, r) {
  const kit = new Kit();
  const W = 6.2, L = 8.6, colH = 4.2;
  const ruin = b.state === 'ruin';
  for (let i = 0; i < 3; i++) kit.box(M.marble, W + (2 - i) * 0.6, 0.25, L + (2 - i) * 0.6, 0, 0.125 + i * 0.25, 0);
  const y0 = 0.75;
  const wallH = ruin ? colH * 0.6 : colH;
  kit.box(M.wall, W - 0.4, wallH, L - 2.4, 0, y0 + wallH / 2, -1.0);
  for (const sx of [-1, 1]) kit.box(M.wall, 0.55, wallH, 1.6, sx * (W / 2 - 0.47), y0 + wallH / 2, L / 2 - 1.2);
  kit.box(M.dark, 1.4, 2.8, 0.08, 0, y0 + 1.4, L / 2 - 2.2);
  for (const sx of [-1, 1]) {
    if (ruin && sx > 0) continue;
    kit.instance(`treasury-col-${colH}`, () => doricColumn(colH, { detail: 'low' }), M.marble, m4(sx * 1.05, y0, L / 2 - 0.55, sx));
  }
  if (!ruin) {
    kit.box(M.marble, W + 0.1, 0.55, L + 0.1, 0, y0 + colH + 0.28, 0);
    const tri = () => triglyphGeometry(0.4, 0.55);
    for (let i = 0; i < 5; i++) kit.instance('treasury-triglyph', tri, M.marble, m4(-W / 2 + 0.45 + i * ((W - 0.9) / 4), y0 + colH + 0.28, L / 2 + 0.06));
    kit.box(M.marble, W + 0.6, 0.22, L + 0.6, 0, y0 + colH + 0.67, 0);
    const ridge = 1.3;
    kit.add(M.marble, gable(W + 0.2, ridge, 0.3), m4(0, y0 + colH + 0.78, L / 2 + 0.1));
    kit.add(M.marble, gable(W + 0.2, ridge, 0.3), m4(0, y0 + colH + 0.78, -L / 2 - 0.1));
    const half = (W + 0.8) / 2, pitch = Math.atan2(ridge, half), slope = Math.hypot(half, ridge);
    for (const sx of [1, -1]) kit.box(M.tile, slope, 0.14, L + 0.8, sx * half / 2, y0 + colH + 0.78 + ridge / 2 + 0.05, 0, 0, 0, -sx * pitch);
    kit.add(M.marble, palmette(0.8, 0.6), m4(0, y0 + colH + 0.78 + ridge, L / 2 + 0.2));
  } else {
    rubble(kit, M, r, 6, W, L);
  }
  decorate(kit, M, b, r, { w: W, d: L, h: wallH, y0 });
  const group = kit.build();
  group.userData.height = y0 + colH + 2.6;
  return group;
}

/** 仓库：长而低的石屋，大门，门外堆着木箱和陶罐。 */
export function buildWarehouse(M, b, r) {
  const kit = new Kit();
  const sc = b.sizeClass;
  const W = 8.5 + sc * 0.6, D = 6, H = 3.3;
  const ruin = b.state === 'ruin';
  kit.box(M.stone, W, ruin ? H * 0.6 : H, D, 0, (ruin ? H * 0.6 : H) / 2, -0.4);
  kit.box(M.dark, 2.6, 2.6, 0.08, 0, 1.3, D / 2 - 0.36);
  kit.box(M.darkWood, 2.9, 0.18, 0.2, 0, 2.7, D / 2 - 0.3);
  if (!ruin) gabledRoof(kit, M.tile, M.stone, W, D, 1.1, H, -0.4, 0.3);
  for (let i = 0; i < 5; i++) {
    const s = 0.6 + r() * 0.3;
    kit.box(M.wood, s, s, s, -W / 2 + 0.7 + (i % 3) * 0.8, s / 2 + (i >= 3 ? 0.75 : 0), D / 2 + 0.7, r() * 0.4);
  }
  for (let i = 0; i < 3; i++) kit.add(M.tile, amphoraBody(), m4(W / 2 - 0.6 - i * 0.5, 0, D / 2 + 0.6, r() * 6, 0, 0, 0.85));
  if (ruin) rubble(kit, M, r, 6, W, D);
  decorate(kit, M, b, r, { w: W, d: D, h: H });
  const group = kit.build();
  group.userData.height = H + 1.6;
  return group;
}

// ------------------------------------------------------------------
// 公共建筑
// ------------------------------------------------------------------

/** 图书馆（记忆）：圆形的 tholos，一圈柱子、圆筒内殿、锥形瓦顶，一眼就和柱廊书库分得开。 */
export function buildTholos(M) {
  const kit = new Kit();
  const R = 5.1, colH = 4.0, n = 12;
  for (let i = 0; i < 3; i++) kit.cylinder(M.marble, R + 0.9 - i * 0.35, R + 0.9 - i * 0.35, 0.25, 0, 0.125 + i * 0.25, 0, 48);
  const y0 = 0.75;
  for (let i = 0; i < n; i++) {
    const a = (i / n) * Math.PI * 2;
    kit.instance('tholos-col', () => doricColumn(colH, { detail: 'low' }), M.marble, m4(Math.sin(a) * (R - 0.35), y0, Math.cos(a) * (R - 0.35), a));
  }
  kit.cylinder(M.wall, R - 1.6, R - 1.6, colH, 0, y0 + colH / 2, 0, 40);
  kit.box(M.dark, 1.3, 2.6, 0.2, 0, y0 + 1.3, R - 1.56);
  kit.cylinder(M.marble, R + 0.15, R + 0.15, 0.55, 0, y0 + colH + 0.27, 0, 48);
  kit.add(M.tile, new THREE.ConeGeometry(R + 0.35, 2.9, 48, 1, true), m4(0, y0 + colH + 0.55 + 1.45, 0));
  kit.add(M.marble, palmette(0.9, 0.6), m4(0, y0 + colH + 3.3, 0));
  const group = kit.build();
  group.userData.height = y0 + colH + 4.4;
  return group;
}

/** 锻炉：敞开的炉棚，铁砧、炉膛、风箱。炉火不发光——光只属于雅典娜，她来干活这里才亮。 */
export function buildForge(M) {
  const kit = new Kit();
  const W = 8.4, D = 6.4, H = 3.4;
  kit.box(M.stone, W + 0.4, 0.2, D + 0.4, 0, 0.1, -0.6);
  kit.box(M.stone, W, H, 0.5, 0, 0.2 + H / 2, -D / 2 - 0.35);
  for (const sx of [-1, 1]) kit.box(M.stone, 0.5, H, D - 1.4, sx * (W / 2 - 0.25), 0.2 + H / 2, -1.25);
  gabledRoof(kit, M.tile, M.stone, W, D, 1.3, 0.2 + H, -0.6, 0.4);
  for (const sx of [-1, 1]) kit.cylinder(M.wood, 0.14, 0.16, H, sx * (W / 2 - 0.3), 0.2 + H / 2, D / 2 - 0.9, 10);
  // 炉膛（后墙下）、烟囱
  kit.box(M.stone, 2.4, 1.1, 1.4, -1.2, 0.75, -D / 2 + 0.5);
  kit.box(M.dark, 1.6, 0.25, 0.9, -1.2, 1.35, -D / 2 + 0.55);
  kit.cylinder(M.stone, 0.45, 0.6, 3.5, -1.2, 0.2 + H + 1.2, -D / 2 - 0.2, 12);
  // 铁砧和木墩
  kit.cylinder(M.darkWood, 0.42, 0.48, 0.6, 1.0, 0.5, 0.6, 14);
  kit.box(M.bronze, 0.9, 0.32, 0.36, 1.0, 0.96, 0.6);
  kit.box(M.bronze, 0.35, 0.18, 0.28, 1.6, 1.0, 0.6);
  kit.box(M.wood, 1.6, 0.9, 0.7, 2.6, 0.45, -1.8);   // 风箱架
  for (let i = 0; i < 3; i++) kit.add(M.tile, amphoraBody(), m4(-W / 2 + 0.9 + i * 0.5, 0.2, D / 2 - 0.3, i, 0, 0, 0.8));
  const group = kit.build();
  group.userData.height = 0.2 + H + 3;
  group.userData.anvil = new THREE.Vector3(1.0, 1.2, 0.6);
  return group;
}

/** 市集：两排布篷摊位。顶层文件夹太多时，多出来的就在这里各占一个摊位。 */
export function buildMarket(M, stallCount) {
  const kit = new Kit();
  kit.add(M.pave, groundPlane(12, 12, 4), m4(0, 0.02, 0));
  const n = Math.max(6, stallCount);
  const spots = [];
  for (let i = 0; i < n; i++) {
    const row = i % 2, col = Math.floor(i / 2);
    const x = -4.2 + col * 2.9, z = row === 0 ? -3.2 : 2.2;
    if (x > 5) break;
    spots.push([x, z]);
    for (const sx of [-1, 1]) for (const sz of [-1, 1]) kit.cylinder(M.wood, 0.05, 0.06, 2.3, x + sx * 1.1, 1.15, z + sz * 0.8, 6);
    kit.box(M.awning, 2.5, 0.06, 2.0, x, 2.35, z, 0, row === 0 ? 0.12 : -0.12);
    kit.box(M.wood, 2.0, 0.1, 0.8, x, 0.9, z + (row === 0 ? 0.4 : -0.4));
    kit.add(M.tile, amphoraBody(), m4(x - 0.5, 0.95, z + (row === 0 ? 0.4 : -0.4), i, 0, 0, 0.45));
    kit.add(M.wood, new THREE.CylinderGeometry(0.3, 0.22, 0.28, 10), m4(x + 0.4, 1.09, z + (row === 0 ? 0.4 : -0.4)));
  }
  const group = kit.build();
  group.userData.height = 3.2;
  group.userData.stallSpots = spots;
  return group;
}

/** 城门：一座小门楼（propylon）横跨街口：两根方柱、过梁、三角楣。 */
export function buildGate(M) {
  const kit = new Kit();
  for (const sx of [-1, 1]) {
    kit.box(M.marble, 1.2, 4.6, 1.4, sx * 2.6, 2.3, 0);
    kit.box(M.stone, 7, 2.2, 0.8, sx * 6.6, 1.1, 0);   // 两侧的一段城墙
  }
  kit.box(M.marble, 6.6, 0.7, 1.6, 0, 4.95, 0);
  kit.add(M.marble, gable(6.8, 1.2, 1.6), m4(0, 5.3, 0));
  const group = kit.build();
  group.userData.height = 7.2;
  return group;
}

/** 广场：委托板（一块立着的大理石碑，带小山花）、两条石凳、一尊方柱头像。 */
export function buildAgora(M) {
  const kit = new Kit();
  kit.box(M.marble, 2.2, 0.25, 1.0, 0, 0.125, -4.2);
  kit.box(M.marble, 1.8, 0.25, 0.8, 0, 0.375, -4.2);
  kit.box(M.marble, 1.3, 2.6, 0.28, 0, 1.8, -4.2);
  kit.add(M.marble, gable(1.5, 0.45, 0.32), m4(0, 3.1, -4.2));
  for (const sx of [-1, 1]) {
    kit.box(M.marble, 2.4, 0.18, 0.6, sx * 4.2, 0.5, 1.5);
    for (const lx of [-0.9, 0.9]) kit.box(M.marble, 0.25, 0.42, 0.5, sx * 4.2 + lx, 0.21, 1.5);
  }
  kit.box(M.marble, 0.42, 1.5, 0.42, 4.6, 0.75, -3.6);
  kit.add(M.marble, new THREE.SphereGeometry(0.26, 16, 12), m4(4.6, 1.75, -3.6, 0, 0, 0, 0.9, 1.15, 0.95));
  const group = kit.build();
  group.userData.height = 3.6;
  return group;
}

/** 港口：港前广场（铺地、货堆、一尊海神像）、伸进海里的栈桥、系船柱。船是单独的动画件，见 buildBoat。 */
export function buildHarbor(M, quayLocalZ, pierLength, plotCenterZ, statueGeometry) {
  const kit = new Kit();
  kit.add(M.pave, groundPlane(12.4, 12.4, 4), m4(0, 0.016, plotCenterZ));
  kit.box(M.marble, 1.2, 1.1, 1.2, 0, 0.55, plotCenterZ - 2.5);
  kit.add(M.marble, statueGeometry, m4(0, 1.1, plotCenterZ - 2.5, 0, 0, 0, 1.25));
  for (let i = 0; i < 6; i++) kit.box(M.wood, 0.75, 0.75, 0.75, -4 + (i % 3) * 0.85, 0.375 + (i >= 3 ? 0.75 : 0), plotCenterZ + 2.5, i * 0.2);
  for (let i = 0; i < 6; i++) kit.add(M.tile, amphoraBody(), m4(3 + (i % 3) * 0.5, 0, plotCenterZ + 2 + Math.floor(i / 3) * 0.55, i, 0, 0, 0.85));
  kit.box(M.stone, 3.2, 0.9, pierLength, 0, -0.35, quayLocalZ + pierLength / 2);
  for (let z = quayLocalZ + 2; z < quayLocalZ + pierLength; z += 3.2) {
    for (const sx of [-1, 1]) kit.cylinder(M.stone, 0.16, 0.2, 0.55, sx * 1.35, 0.35, z, 8);
  }
  for (let i = 0; i < 4; i++) kit.box(M.wood, 0.7, 0.7, 0.7, -3 + (i % 2) * 0.8, 0.35 + (i >= 2 ? 0.7 : 0), quayLocalZ - 2.5, i * 0.3);
  for (let i = 0; i < 5; i++) kit.add(M.tile, amphoraBody(), m4(2.2 + (i % 3) * 0.5, 0, quayLocalZ - 2.2 - Math.floor(i / 3) * 0.5, i, 0, 0, 0.85));
  const group = kit.build();
  group.userData.height = 2;
  return group;
}

/** 一条小商船：船身是车削体压扁，一根桅杆、收着的帆。 */
export function buildBoat(M) {
  const kit = new Kit();
  const pts = [];
  for (let i = 0; i <= 12; i++) {
    const t = i / 12;
    pts.push(new THREE.Vector2(Math.max(0.02, Math.sin(Math.PI * t) * 1.15), (t - 0.5) * 9));
  }
  // 半个车削体：phi ∈ [-90°, 90°] 绕 x 转 90° 之后落在水线以下，开口朝上
  const hull = new THREE.LatheGeometry(pts, 20, -Math.PI / 2, Math.PI);
  hull.rotateX(Math.PI / 2);
  hull.scale(1, 0.85, 1);
  kit.add(M.darkWood, hull, m4(0, 0.15, 0));
  kit.box(M.wood, 2.1, 0.08, 7.8, 0, 0.12, 0);
  kit.cylinder(M.darkWood, 0.09, 0.12, 1.6, 0, 0.9, -4.7, 8, -0.75);   // 船尾翘起的尾饰
  kit.cylinder(M.wood, 0.08, 0.1, 6, 0, 3.1, 0.4, 8);
  kit.cylinder(M.wood, 0.05, 0.05, 3.6, 0, 5.2, 0.4, 6, 0, Math.PI / 2);
  kit.box(M.awning, 3.2, 0.5, 0.08, 0, 4.9, 0.4);
  kit.add(M.wood, new THREE.CylinderGeometry(0.1, 0.25, 1.2, 8), m4(0, 0.9, -4.6, 0, -0.6));
  const group = kit.build();
  group.userData.height = 6;
  return group;
}

// ------------------------------------------------------------------
// 小件
// ------------------------------------------------------------------
const amphoraCache = { g: null };
/** 双耳瓶的瓶身（打样的轮廓，去掉了把手），高约 0.82。 */
export function amphoraBody() {
  if (amphoraCache.g) return amphoraCache.g;
  const P = [[0.0, 0.0], [0.085, 0.0], [0.09, 0.016], [0.066, 0.04], [0.058, 0.07], [0.1, 0.14], [0.17, 0.25], [0.205, 0.36],
    [0.21, 0.44], [0.195, 0.52], [0.16, 0.6], [0.11, 0.65], [0.082, 0.68], [0.075, 0.72], [0.078, 0.78], [0.098, 0.8], [0.1, 0.82], [0.085, 0.825], [0.07, 0.8]];
  amphoraCache.g = new THREE.LatheGeometry(P.map(([r, y]) => new THREE.Vector2(r, y)), 14);
  return amphoraCache.g;
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

export { rng };
