// 雅典娜的城邦 —— 程序化美术打样
//
// 所有几何和贴图都由代码生成，不加载任何外部素材。从仓库根目录起静态服务后打开：
//   python3 -I -m http.server 8765 --bind 127.0.0.1 --directory <仓库根>
//   http://127.0.0.1:8765/Artwork/Polis/sample/index.html                 可交互（拖动旋转、滚轮缩放）
//   http://127.0.0.1:8765/Artwork/Polis/sample/index.html?shot=game       固定机位：game | mid | close
//   追加 &grade=0 关掉黑白调色，&accent=0 关掉点缀色
// 固定机位模式渲染几帧后停下并把 document.title 设为 ready，供无头截图使用（见 shoot.sh）。

import * as THREE from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';
import { EffectComposer } from 'three/addons/postprocessing/EffectComposer.js';
import { RenderPass } from 'three/addons/postprocessing/RenderPass.js';
import { GTAOPass } from 'three/addons/postprocessing/GTAOPass.js';
import { OutputPass } from 'three/addons/postprocessing/OutputPass.js';
import { ShaderPass } from 'three/addons/postprocessing/ShaderPass.js';
import { RoomEnvironment } from 'three/addons/environments/RoomEnvironment.js';
import { mergeGeometries, mergeVertices } from 'three/addons/utils/BufferGeometryUtils.js';

const params = new URLSearchParams(location.search);
const SHOT = params.get('shot');
const GRADE = params.get('grade') !== '0';
const ACCENT = params.get('accent') !== '0';
const AO = params.get('ao') !== '0';            // 调试用：关掉环境光遮蔽
const SHADOWS = params.get('shadows') !== '0';  // 调试用：关掉阴影

const lerp = THREE.MathUtils.lerp;
const deg = THREE.MathUtils.degToRad;
const smooth = (a, b, x) => {
  const t = Math.min(1, Math.max(0, (x - a) / (b - a)));
  return t * t * (3 - 2 * t);
};

// ------------------------------------------------------------------
// 噪声：确定性、可平铺（周期 p 内首尾相接），同一个种子每次生成同样的纹理
// ------------------------------------------------------------------
function rng(seed) {
  return () => {
    seed |= 0; seed = (seed + 0x6D2B79F5) | 0;
    let t = Math.imul(seed ^ (seed >>> 15), 1 | seed);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

function valueNoise(seed) {
  const r = rng(seed);
  const lattice = new Float32Array(256 * 256);
  for (let i = 0; i < lattice.length; i++) lattice[i] = r();
  const at = (x, y, p) => lattice[(((x % p) + p) % p) * 256 + (((y % p) + p) % p)];
  return (x, y, p = 256) => {
    // 周期超过格点表就会越界读出 NaN，贴图上整排变黑——第一版地面的黑色斜带就是这么来的
    if (p > 256) throw new Error(`valueNoise: period ${p} exceeds the 256 lattice`);
    const xi = Math.floor(x), yi = Math.floor(y);
    const xf = x - xi, yf = y - yi;
    const u = xf * xf * (3 - 2 * xf), v = yf * yf * (3 - 2 * yf);
    const a = at(xi, yi, p), b = at(xi + 1, yi, p), c = at(xi, yi + 1, p), d = at(xi + 1, yi + 1, p);
    return a + (b - a) * u + (c - a) * v + (a - b - c + d) * u * v;
  };
}

function fbm(noise, x, y, period, octaves = 5) {
  let sum = 0, amp = 0.5, freq = 1, norm = 0;
  for (let o = 0; o < octaves; o++) {
    sum += amp * noise(x * freq, y * freq, period * freq);
    norm += amp; amp *= 0.5; freq *= 2;
  }
  return sum / norm;
}

function field(size, fn) {
  const f = new Float32Array(size * size);
  for (let y = 0; y < size; y++) for (let x = 0; x < size; x++) f[y * size + x] = fn(x / size, y / size);
  return f;
}

function textureFrom(size, values, toRGB, { repeat = 1, srgb = true } = {}) {
  const canvas = document.createElement('canvas');
  canvas.width = canvas.height = size;
  const ctx = canvas.getContext('2d');
  const img = ctx.createImageData(size, size);
  for (let i = 0; i < size * size; i++) {
    const [r, g, b] = toRGB(values[i]);
    img.data[i * 4] = r; img.data[i * 4 + 1] = g; img.data[i * 4 + 2] = b; img.data[i * 4 + 3] = 255;
  }
  ctx.putImageData(img, 0, 0);
  const t = new THREE.CanvasTexture(canvas);
  t.wrapS = t.wrapT = THREE.RepeatWrapping;
  t.repeat.set(repeat, repeat);
  t.colorSpace = srgb ? THREE.SRGBColorSpace : THREE.NoColorSpace;
  t.anisotropy = 8;
  return t;
}

const tone = (v, tint = [1, 1, 1]) => [v * 255 * tint[0], v * 255 * tint[1], v * 255 * tint[2]];

// ------------------------------------------------------------------
// 材质。世界里的颜色最终会被调成黑白，这里仍按真实颜色给，
// 是为了让去色后的明暗关系像胶片拍到的那样自然（暖白的大理石、偏暗的土）。
// ------------------------------------------------------------------
function makeMaterials() {
  const n1 = valueNoise(11), n2 = valueNoise(23), n3 = valueNoise(37), n4 = valueNoise(41);

  const marbleField = field(512, (u, v) => {
    const cloud = fbm(n1, u * 8, v * 8, 8, 5);
    const warp = fbm(n2, u * 4, v * 4, 4, 4);
    const vein = Math.abs(Math.sin((u * 2 + v * 1 + warp * 2.6) * Math.PI * 2));
    return cloud - (1 - smooth(0.0, 0.04, vein)) * 0.3;
  });
  const marbleMap = textureFrom(512, marbleField, (f) => tone(0.80 + (f - 0.5) * 0.10, [1, 0.985, 0.955]));
  const marbleBump = textureFrom(512, marbleField, (f) => tone(f), { srgb: false });

  // 干土：大尺度的起伏 + 细砂 + 零星的草丛暗斑
  const n5 = valueNoise(53);
  const earthField = field(512, (u, v) => {
    const broad = fbm(n3, u * 8, v * 8, 8, 6);
    const grit = fbm(n4, u * 64, v * 64, 64, 2);
    const tuft = smooth(0.6, 0.7, fbm(n5, u * 32, v * 32, 32, 3));
    return broad * 0.6 + grit * 0.4 - tuft * 0.16;
  });
  const earthMap = textureFrom(512, earthField, (f) => tone(0.40 + (f - 0.45) * 0.24, [1, 0.96, 0.9]), { repeat: 10 });

  // 铺地石板：行高、块宽都不等的错缝条石，石面有风化斑驳，灰缝细而浅
  const paveField = (() => {
    const size = 512, f = new Float32Array(size * size), r = rng(5);
    const rows = [];
    for (let y = 0; y < size;) { const h = Math.min(size - y, 48 + Math.floor(r() * 40)); rows.push({ y0: y, y1: y + h }); y += h; }
    for (const row of rows) {
      row.breaks = [];
      let x = r() * 120;
      while (x < size + 200) { row.breaks.push(x); x += 60 + r() * 140; }
      row.tones = row.breaks.map(() => r());
    }
    for (const row of rows) {
      for (let y = row.y0; y < row.y1; y++) {
        const dy = Math.min(y - row.y0, row.y1 - y);
        for (let x = 0; x < size; x++) {
          let k = 0;
          while (k < row.breaks.length - 1 && row.breaks[k + 1] <= x) k++;
          const left = row.breaks[k] <= x ? row.breaks[k] : row.breaks[k] - size;
          const right = row.breaks[k + 1] ?? left + 120;
          const dx = Math.min(Math.abs(x - left), Math.abs(right - x));
          const wear = fbm(n4, (x / size) * 16, (y / size) * 16, 16, 4);
          const grout = Math.min(dx, dy) < 1.4 + wear * 1.2;
          const grain = fbm(n1, (x / size) * 32, (y / size) * 32, 32, 3);
          f[y * size + x] = grout ? -1 : row.tones[k] * 0.45 + grain * 0.3 + wear * 0.25;
        }
      }
    }
    return f;
  })();
  const paveMap = textureFrom(512, paveField, (f) => (f < 0 ? tone(0.5, [1, 0.97, 0.93]) : tone(0.6 + (f - 0.5) * 0.16, [1, 0.98, 0.94])), { repeat: 4 });
  const paveBump = textureFrom(512, paveField, (f) => tone(f < 0 ? 0.2 : 0.55 + f * 0.45), { srgb: false, repeat: 4 });

  const clothField = field(256, (u, v) => fbm(n2, u * 32, v * 64, 32, 3));
  const clothMap = textureFrom(256, clothField, (f) => tone(0.90 + (f - 0.5) * 0.06, [1, 0.99, 0.965]), { repeat: 3 });

  const marble = new THREE.MeshStandardMaterial({ map: marbleMap, bumpMap: marbleBump, bumpScale: 0.35, roughness: 0.6 });
  const wall = marble.clone(); wall.color = new THREE.Color(0.9, 0.9, 0.9);
  return {
    marble,
    wall,
    // 俯视时屋顶占画面最大，得比铺地明显暗一档，城里的建筑才分得开
    roof: new THREE.MeshStandardMaterial({ color: 0x75685e, roughness: 0.8 }),
    dark: new THREE.MeshStandardMaterial({ color: 0x1c1a18, roughness: 0.9 }),
    earth: new THREE.MeshStandardMaterial({ map: earthMap, roughness: 1 }),
    pave: new THREE.MeshStandardMaterial({ map: paveMap, bumpMap: paveBump, bumpScale: 0.6, roughness: 0.92 }),
    cloth: new THREE.MeshStandardMaterial({
      map: clothMap, roughness: 0.86, side: THREE.DoubleSide,
      emissive: new THREE.Color(1, 0.98, 0.94), emissiveIntensity: 0.025,
    }),
    skin: new THREE.MeshStandardMaterial({ color: 0xc8ae98, roughness: 0.6 }),
    hair: new THREE.MeshStandardMaterial({ color: 0x3a332e, roughness: 0.65 }),
    gold: new THREE.MeshStandardMaterial({ color: 0xd8c08a, roughness: 0.3, metalness: 0.9 }),
    cypress: new THREE.MeshStandardMaterial({ color: 0x48503f, roughness: 0.95 }),
    olive: new THREE.MeshStandardMaterial({ color: 0x7c8574, roughness: 0.95 }),
    bark: new THREE.MeshStandardMaterial({ color: 0x5c534a, roughness: 0.95 }),
  };
}

const shadowed = (o) => { o.castShadow = true; o.receiveShadow = true; return o; };
const mesh = (g, m) => shadowed(new THREE.Mesh(g, m));
function box(parent, mat, w, h, d, x, y, z) {
  const b = mesh(new THREE.BoxGeometry(w, h, d), mat);
  b.position.set(x, y, z);
  parent.add(b);
  return b;
}

// ------------------------------------------------------------------
// 多立克神庙（6×13 列，比例取自赫菲斯托斯神庙一类的古典作品；单位≈米，柱底径 1）
// ------------------------------------------------------------------

// 带凹槽和收分的柱身。每条凹槽单独成片，相邻凹槽在棱线处不共用顶点，棱线才是锐的。
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

// 柱头：几道环线、外张的钟形（echinus）、方形顶板（abacus）
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
  const echinus = new THREE.LatheGeometry(pts, 48);
  const abacus = new THREE.BoxGeometry(1.28, 0.17, 1.28);
  abacus.translate(0, 0.238 + 0.085, 0);
  return mergeGeometries([echinus, abacus]);
}

// 三陇板：后板 + 三根竖条，竖条之间的凹槽靠后板退进去形成
function triglyphGeometry(w, h) {
  const parts = [];
  const back = new THREE.BoxGeometry(w, h, 0.08); back.translate(0, 0, -0.03); parts.push(back);
  for (const x of [-0.16, 0, 0.16]) {
    const bar = new THREE.BoxGeometry(0.1, h - 0.06, 0.12); bar.translate(x, -0.03, 0.0); parts.push(bar);
  }
  const cap = new THREE.BoxGeometry(w + 0.02, 0.07, 0.13); cap.translate(0, h / 2 - 0.035, 0.0); parts.push(cap);
  return mergeGeometries(parts);
}

function palmette(height, width) {
  const s = new THREE.Shape();
  s.moveTo(0, 0);
  s.bezierCurveTo(width * 0.55, height * 0.12, width * 0.62, height * 0.62, 0, height);
  s.bezierCurveTo(-width * 0.62, height * 0.62, -width * 0.55, height * 0.12, 0, 0);
  const g = new THREE.ExtrudeGeometry(s, { depth: 0.08, bevelEnabled: true, bevelSize: 0.02, bevelThickness: 0.02, bevelSegments: 2 });
  g.translate(0, 0, -0.04);
  return g;
}

function buildTemple(M) {
  const T = new THREE.Group();
  const R0 = 0.5, R1 = 0.39;
  const H_SHAFT = 5.2, H_CAP = 0.408, H_COL = H_SHAFT + H_CAP;
  const AX = 2.55, NX = 6, NZ = 13;
  const spanX = (NX - 1) * AX, spanZ = (NZ - 1) * AX;
  const STEP_H = 0.33, STEP_T = 0.42, NSTEPS = 3;
  const styW = spanX + 1.6, styL = spanZ + 1.6;
  const yS = NSTEPS * STEP_H;

  // 三级台基
  for (let i = 0; i < NSTEPS; i++) {
    const grow = (NSTEPS - 1 - i) * STEP_T * 2;
    box(T, M.marble, styW + grow, STEP_H, styL + grow, 0, i * STEP_H + STEP_H / 2, 0);
  }

  // 柱廊：一圈 34 根，同一份几何用实例绘制
  const shaft = flutedShaft({ r0: R0, r1: R1, h: H_SHAFT });
  const cap = doricCapital(R1); cap.translate(0, H_SHAFT, 0);
  const colGeom = mergeGeometries([shaft, cap]);
  const spots = [];
  for (let i = 0; i < NX; i++) for (let j = 0; j < NZ; j++) {
    if (i !== 0 && i !== NX - 1 && j !== 0 && j !== NZ - 1) continue;
    spots.push([-spanX / 2 + i * AX, yS, -spanZ / 2 + j * AX]);
  }
  const cols = shadowed(new THREE.InstancedMesh(colGeom, M.marble, spots.length));
  const m4 = new THREE.Matrix4();
  spots.forEach((p, k) => { m4.makeRotationY(k * 0.37); m4.setPosition(p[0], p[1], p[2]); cols.setMatrixAt(k, m4); });
  T.add(cols);

  // 内殿与门洞（正面朝 +z）
  const cellaW = spanX - 2 * 1.05 * AX, cellaL = spanZ - 2 * 1.45 * AX;
  box(T, M.wall, cellaW, H_COL, cellaL, 0, yS + H_COL / 2, -0.2);
  box(T, M.dark, 2.1, 4.1, 0.12, 0, yS + 2.05, -0.2 + cellaL / 2 + 0.02);
  for (const sx of [-1, 1]) box(T, M.wall, 0.6, H_COL, 0.5, sx * (cellaW / 2 - 0.3), yS + H_COL / 2, -0.2 + cellaL / 2 + 0.2);

  // 檐部：额枋、窄带、檐壁（三陇板与陇间板）、檐口
  const yA = yS + H_COL;
  const entW = spanX + 1.28, entL = spanZ + 1.28;
  const ARCH_H = 0.74, TAENIA = 0.07, FRIEZE_H = 0.74, CROWN = 0.07, CORN_H = 0.34, OVER = 0.42;
  box(T, M.marble, entW - 0.06, ARCH_H, entL - 0.06, 0, yA + ARCH_H / 2, 0);
  box(T, M.marble, entW + 0.02, TAENIA, entL + 0.02, 0, yA + ARCH_H + TAENIA / 2, 0);
  const yF = yA + ARCH_H + TAENIA;
  box(T, M.marble, entW - 0.18, FRIEZE_H, entL - 0.18, 0, yF + FRIEZE_H / 2, 0);

  const tri = triglyphGeometry(0.5, FRIEZE_H);
  const triSpots = [];
  const along = (n, len) => {
    const a = [];
    for (let i = 0; i <= 2 * (n - 1); i++) a.push(-((n - 1) * AX) / 2 + (i * AX) / 2);
    a[0] = -len / 2 + 0.27; a[a.length - 1] = len / 2 - 0.27;   // 角部三陇板顶到转角（古典多立克的做法）
    return a;
  };
  for (const x of along(NX, entW)) { triSpots.push([x, entL / 2 - 0.07, 0]); triSpots.push([x, -(entL / 2 - 0.07), Math.PI]); }
  // 转角处两个面各有一块三陇板，在角上相接
  for (const z of along(NZ, entL)) { triSpots.push([entW / 2 - 0.07, z, Math.PI / 2]); triSpots.push([-(entW / 2 - 0.07), z, -Math.PI / 2]); }
  const tris = shadowed(new THREE.InstancedMesh(tri, M.marble, triSpots.length));
  triSpots.forEach(([x, z, rot], k) => {
    m4.makeRotationY(rot);
    m4.setPosition(x, yF + FRIEZE_H / 2, z);
    tris.setMatrixAt(k, m4);
  });
  T.add(tris);

  box(T, M.marble, entW + 0.06, CROWN, entL + 0.06, 0, yF + FRIEZE_H + CROWN / 2, 0);
  const yC = yF + FRIEZE_H + CROWN;
  box(T, M.marble, entW + 2 * OVER, CORN_H, entL + 2 * OVER, 0, yC + CORN_H / 2, 0);

  // 山墙与屋顶：13° 坡，前后山花三角退进檐口
  const yR = yC + CORN_H;
  const RW = entW + 2 * OVER, RL = entL + 2 * OVER;
  const pitch = deg(13);
  const ridge = (RW / 2) * Math.tan(pitch);
  const tym = new THREE.Shape();
  tym.moveTo(-RW / 2 + 0.35, 0); tym.lineTo(RW / 2 - 0.35, 0); tym.lineTo(0, ridge - 0.12); tym.closePath();
  const prism = new THREE.ExtrudeGeometry(tym, { depth: RL - 0.8, bevelEnabled: false });
  prism.translate(0, 0, -(RL - 0.8) / 2);
  const roofBody = mesh(prism, M.marble); roofBody.position.y = yR; T.add(roofBody);

  const slopeLen = Math.hypot(RW / 2, ridge);
  for (const sz of [1, -1]) for (const sx of [1, -1]) {
    const rake = mesh(new THREE.BoxGeometry(slopeLen + 0.25, 0.32, 0.72), M.marble);
    rake.position.set(sx * RW / 4, yR + ridge / 2 + 0.06, sz * (RL / 2 - 0.36));
    rake.rotation.z = -sx * pitch;
    T.add(rake);
  }
  for (const sx of [1, -1]) {
    const slab = mesh(new THREE.BoxGeometry(slopeLen + 0.35, 0.12, RL + 0.08), M.roof);
    slab.position.set(sx * RW / 4, yR + ridge / 2 + 0.25, 0);
    slab.rotation.z = -sx * pitch;
    T.add(slab);
  }
  // 盖瓦（顺坡的半圆脊）与檐口的瓦当
  const tile = new THREE.CylinderGeometry(0.075, 0.075, slopeLen + 0.3, 8, 1, false, 0, Math.PI);
  tile.rotateZ(Math.PI / 2);
  const tileSpots = [];
  for (let z = -RL / 2 + 0.3; z <= RL / 2 - 0.3; z += 0.62) tileSpots.push(z);
  const tiles = shadowed(new THREE.InstancedMesh(tile, M.roof, tileSpots.length * 2));
  const ante = new THREE.BoxGeometry(0.04, 0.26, 0.2);
  const antes = shadowed(new THREE.InstancedMesh(ante, M.marble, tileSpots.length * 2));
  const q = new THREE.Quaternion(), s1 = new THREE.Vector3(1, 1, 1), p = new THREE.Vector3();
  let ti = 0;
  for (const sx of [1, -1]) for (const z of tileSpots) {
    q.setFromEuler(new THREE.Euler(0, 0, -sx * pitch));
    p.set(sx * RW / 4, yR + ridge / 2 + 0.33, z);
    tiles.setMatrixAt(ti, m4.compose(p, q, s1));
    p.set(sx * (RW / 2 + 0.12), yR + 0.3, z);
    antes.setMatrixAt(ti, m4.compose(p, new THREE.Quaternion(), s1));
    ti++;
  }
  T.add(tiles, antes);
  const ridgeTile = mesh(new THREE.CylinderGeometry(0.13, 0.13, RL + 0.1, 12), M.roof);
  ridgeTile.rotation.x = Math.PI / 2; ridgeTile.position.y = yR + ridge + 0.26; T.add(ridgeTile);

  // 顶饰：山尖的大棕叶饰，四角的小棕叶饰
  for (const sz of [1, -1]) {
    const top = mesh(palmette(1.15, 0.85), M.marble);
    top.position.set(0, yR + ridge + 0.22, sz * (RL / 2 - 0.36)); T.add(top);
    for (const sx of [1, -1]) {
      const corner = mesh(palmette(0.6, 0.45), M.marble);
      corner.position.set(sx * (RW / 2 - 0.25), yR + 0.2, sz * (RL / 2 - 0.36)); T.add(corner);
    }
  }

  T.userData.front = styL / 2 + (NSTEPS - 1) * STEP_T;   // 最下一级台阶的前沿
  return T;
}

// ------------------------------------------------------------------
// 雅典娜：真身，不披甲、不持兵，一件佩普洛斯。
// 佩普洛斯下摆的竖褶，和柱身的凹槽是同一种几何——古典少女像本来就是这样雕的。
// ------------------------------------------------------------------
function drape({ y0, y1, rings = 40, segs = 96, radius, ez = 0.8, folds = [], growth = () => 1, bumps = [], hem = null }) {
  const pos = [], uv = [], idx = [];
  for (let j = 0; j <= rings; j++) {
    const t = j / rings, yBase = lerp(y0, y1, t);
    for (let k = 0; k <= segs; k++) {
      const th = (k / segs) * Math.PI * 2;
      let r = radius(t, th);
      let f = 0;
      for (const fold of folds) f += fold.amp * Math.sin(fold.n * th + fold.ph + fold.wobble * Math.sin(yBase * fold.wf + fold.n));
      r += f * growth(t);
      for (const b of bumps) {
        const d = Math.atan2(Math.sin(th - b.th), Math.cos(th - b.th));
        r += b.amp * Math.exp(-(d * d) / (2 * b.w * b.w)) * Math.exp(-((yBase - b.y) ** 2) / (2 * b.h * b.h));
      }
      let y = yBase;
      if (hem) y += hem.amp * Math.sin(hem.n * th + hem.ph) * Math.pow(1 - t, 6);
      pos.push(r * Math.sin(th), y, r * Math.cos(th) * ez);
      uv.push((k / segs) * 4, t * 2);
    }
  }
  const row = segs + 1;
  for (let j = 0; j < rings; j++) for (let k = 0; k < segs; k++) {
    const a = j * row + k, b = a + 1, c = a + row, d = c + 1;
    idx.push(a, b, c, b, d, c);
  }
  const g = new THREE.BufferGeometry();
  g.setAttribute('position', new THREE.Float32BufferAttribute(pos, 3));
  g.setAttribute('uv', new THREE.Float32BufferAttribute(uv, 2));
  g.setIndex(idx);
  g.computeVertexNormals();
  return g;
}

function keyed(keys) {
  return (t) => {
    for (let i = 0; i < keys.length - 1; i++) {
      const [t0, r0] = keys[i], [t1, r1] = keys[i + 1];
      if (t <= t1) return lerp(r0, r1, smooth(t0, t1, t));
    }
    return keys[keys.length - 1][1];
  };
}

function limb(parent, mat, a, b, r) {
  const A = new THREE.Vector3(...a), B = new THREE.Vector3(...b);
  const dir = new THREE.Vector3().subVectors(B, A);
  const len = dir.length();
  const g = new THREE.CapsuleGeometry(r, Math.max(0.001, len), 6, 16);
  const m = mesh(g, mat);
  m.position.copy(A).addScaledVector(dir, 0.5);
  m.quaternion.setFromUnitVectors(new THREE.Vector3(0, 1, 0), dir.normalize());
  parent.add(m);
  return m;
}

function blob(parent, mat, at, scale, rot = [0, 0, 0]) {
  const m = mesh(new THREE.SphereGeometry(1, 28, 20), mat);
  m.position.set(...at); m.scale.set(...scale); m.rotation.set(...rot);
  parent.add(m);
  return m;
}

function buildAthena(M) {
  const A = new THREE.Group();

  const skirt = drape({
    y0: 0.015, y1: 1.14, rings: 60, segs: 128, ez: 0.74,
    radius: (t) => lerp(0.29, 0.155, Math.pow(t, 0.75)),
    folds: [
      { n: 13, amp: 0.021, ph: 0.4, wobble: 0.35, wf: 2.1 },
      { n: 27, amp: 0.007, ph: 1.3, wobble: 0.5, wf: 3.3 },
      { n: 6, amp: 0.012, ph: 2.2, wobble: 0.2, wf: 1.2 },
    ],
    growth: (t) => 1.0 - 0.8 * t,
    bumps: [{ th: 0.42, y: 0.5, amp: 0.06, w: 0.36, h: 0.17 }],   // 放松那条腿的膝盖把衣料顶起来
    hem: { amp: 0.012, n: 7, ph: 0.5 },
  });
  A.add(mesh(skirt, M.cloth));

  const overR = keyed([[0, 0.255], [0.38, 0.17], [0.52, 0.192], [0.74, 0.188], [1, 0.158]]);
  const over = drape({
    y0: 0.86, y1: 1.47, rings: 44, segs: 128, ez: 0.62,
    radius: (t) => overR(t),
    folds: [
      { n: 9, amp: 0.012, ph: 0.9, wobble: 0.3, wf: 2.0 },
      { n: 19, amp: 0.0045, ph: 0.2, wobble: 0.4, wf: 3.0 },
    ],
    growth: (t) => (1 - t) * 1.2 + 0.15,
    hem: { amp: 0.024, n: 5, ph: 1.2 },
  });
  A.add(mesh(over, M.cloth));

  const belt = new THREE.TorusGeometry(0.173, 0.012, 8, 72);
  belt.rotateX(Math.PI / 2); belt.scale(1, 1, 0.63);
  const beltMesh = mesh(belt, M.cloth); beltMesh.position.y = 1.095; A.add(beltMesh);

  blob(A, M.cloth, [0, 1.455, 0], [0.165, 0.07, 0.1]);            // 肩线处把衣服的上口封住
  for (const sx of [-1, 1]) blob(A, M.gold, [sx * 0.11, 1.49, 0.05], [0.014, 0.014, 0.014]);   // 别针

  // 双臂裸露：右前臂抬起、掌心向上（像在说话或递出什么），左手垂下拈着衣襟
  for (const sx of [-1, 1]) blob(A, M.skin, [sx * 0.168, 1.432, 0], [0.04, 0.042, 0.042]);
  limb(A, M.skin, [0.172, 1.43, 0.0], [0.235, 1.19, 0.07], 0.042);
  limb(A, M.skin, [0.235, 1.19, 0.07], [0.33, 1.25, 0.24], 0.034);
  blob(A, M.skin, [0.352, 1.262, 0.275], [0.032, 0.016, 0.05], [0.3, 0.55, 0.1]);
  limb(A, M.skin, [-0.172, 1.43, 0.0], [-0.215, 1.17, -0.03], 0.042);
  limb(A, M.skin, [-0.215, 1.17, -0.03], [-0.226, 0.95, 0.04], 0.034);
  blob(A, M.skin, [-0.229, 0.895, 0.05], [0.022, 0.05, 0.03]);

  limb(A, M.skin, [0, 1.47, 0.0], [0, 1.6, 0.015], 0.042);       // 颈
  const head = new THREE.Group(); head.position.set(0, 1.705, 0.014); head.rotation.x = 0.1; A.add(head);
  head.add(mesh(sculptHead(), M.skin));
  const hair = mesh(sculptHair(), M.hair);
  hair.scale.set(0.087, 0.113, 0.1); hair.position.set(0, 0.008, -0.01); hair.rotation.x = -0.95; head.add(hair);
  const bun = mesh(sculptHair('bun'), M.hair);                  // 脑后的发髻，同样刻出发绺
  bun.scale.set(0.055, 0.05, 0.05); bun.position.set(0, -0.03, -0.11); head.add(bun);
  const fillet = new THREE.Mesh(new THREE.TorusGeometry(0.093, 0.004, 6, 64), M.gold);
  fillet.rotation.x = Math.PI / 2 - 0.7; fillet.position.set(0, 0.03, -0.012); head.add(fillet);

  for (const sx of [-1, 1]) blob(A, M.skin, [sx * 0.065, 0.018, 0.2], [0.03, 0.016, 0.055]);   // 衣摆下露出的脚

  // 她的光照在脚下的一圈地面上；身边的光晕在调色里加，只加在身体轮廓之外，免得把人冲成一团白
  const light = new THREE.PointLight(0xfff6e8, 1.1, 4.5, 2);
  light.position.set(0, 0.45, 0.3);
  A.add(light);
  A.traverse((o) => { if (o.isMesh) o.userData.athena = true; });
  A.userData.chest = new THREE.Vector3(0, 1.15, 0);
  return A;
}

// 头部：在球面上用几处高斯隆起和凹陷雕出眉弓、眼窝、鼻子、颧骨、嘴唇和下巴，下颌收窄——雕塑的做法
function sculptHead() {
  const g = new THREE.SphereGeometry(1, 72, 54);
  const pos = g.attributes.position, v = new THREE.Vector3();
  const features = [
    [[0, 0.22, 0.98], 0.18, 0.05],       // 眉弓
    [[0.36, 0.08, 0.93], 0.12, -0.085],  // 眼窝
    [[-0.36, 0.08, 0.93], 0.12, -0.085],
    [[0, 0.0, 1.0], 0.07, 0.15],         // 鼻梁
    [[0, -0.14, 1.0], 0.065, 0.14],      // 鼻尖
    [[0.5, -0.14, 0.85], 0.17, 0.02],    // 颧骨
    [[-0.5, -0.14, 0.85], 0.17, 0.02],
    [[0, -0.38, 0.93], 0.07, 0.04],      // 嘴唇
    [[0, -0.64, 0.77], 0.12, 0.05],      // 下巴
  ].map(([c, s, a]) => [new THREE.Vector3(...c).normalize(), s, a]);
  for (let i = 0; i < pos.count; i++) {
    v.fromBufferAttribute(pos, i);
    let d = 0;
    for (const [c, s, a] of features) d += a * Math.exp(-v.distanceToSquared(c) / (2 * s * s));
    const jaw = v.y < 0 ? 1 - 0.24 * Math.pow(-v.y, 1.5) : 1;
    v.multiplyScalar(1 + d);
    v.x *= jaw;
    if (v.z < 0) v.z *= 1.07;
    pos.setXYZ(i, v.x, v.y, v.z);
  }
  g.computeVertexNormals();
  g.scale(0.082, 0.11, 0.095);
  return g;
}

// 头发：中分后向两侧和脑后梳去。发绺是从额顶分发点放射出的"经线"，沿发绺再带一点波纹，
// 古典雕像的头发就是这样刻的。kind = 'bun' 时绕发髻自身的轴刻纹。
function sculptHair(kind = 'cap') {
  const g = kind === 'bun'
    ? new THREE.SphereGeometry(1, 48, 32)
    : new THREE.SphereGeometry(1, 128, 64, 0, Math.PI * 2, 0, Math.PI * 0.66);
  const pos = g.attributes.position, v = new THREE.Vector3(), w = new THREE.Vector3();
  const pole = kind === 'bun' ? new THREE.Vector3(0, 0, 1) : new THREE.Vector3(0, 0.75, 0.66).normalize();
  const e1 = new THREE.Vector3(1, 0, 0), e2 = new THREE.Vector3().crossVectors(pole, e1).normalize();
  for (let i = 0; i < pos.count; i++) {
    v.fromBufferAttribute(pos, i);
    w.copy(v).addScaledVector(pole, -v.dot(pole));
    const around = Math.atan2(w.dot(e2), w.dot(e1));
    const along = Math.acos(Math.max(-1, Math.min(1, v.dot(pole))));
    const strand = Math.abs(Math.sin(around * 13 + 1.4 * Math.sin(along * 7)));
    const wave = 0.5 + 0.5 * Math.sin(along * 16);
    v.multiplyScalar(1.025 + 0.03 * strand + 0.012 * wave);
    pos.setXYZ(i, v.x, v.y, v.z);
  }
  g.computeVertexNormals();
  return g;
}

// ------------------------------------------------------------------
// 双耳瓶：车削旋转体 + 两只把手；黑绘纹样画在画布贴图上
// ------------------------------------------------------------------
function amphoraTexture() {
  const W = 1024, H = 512, c = document.createElement('canvas');
  c.width = W; c.height = H;
  const g = c.getContext('2d');
  const clay = '#b9572f', black = '#1d1714';
  g.fillStyle = clay; g.fillRect(0, 0, W, H);
  const yOf = (v) => (1 - v) * H;                       // 贴图 v=0 在底部
  g.fillStyle = black;
  g.fillRect(0, yOf(0.3), W, H - yOf(0.3));               // 瓶足与下腹的黑釉
  g.fillRect(0, 0, W, yOf(0.8));                          // 瓶颈
  // 下腹的放射纹
  const rayBase = yOf(0.3), rayTop = yOf(0.4);
  for (let i = 0; i < 48; i++) {
    const x = (i / 48) * W;
    g.beginPath(); g.moveTo(x, rayBase); g.lineTo(x + W / 96, rayTop); g.lineTo(x + W / 48, rayBase); g.closePath(); g.fill();
  }
  // 肩部回纹带
  const top = yOf(0.7), bot = yOf(0.62), h = bot - top, cells = 24, w = W / cells;
  g.fillRect(0, top - 4, W, 3); g.fillRect(0, bot + 1, W, 3);
  g.lineWidth = h * 0.13; g.strokeStyle = black; g.lineCap = 'square';
  for (let i = 0; i < cells; i++) {
    const x = i * w;
    g.beginPath();
    g.moveTo(x + w * 0.1, bot - h * 0.12);
    g.lineTo(x + w * 0.1, top + h * 0.12);
    g.lineTo(x + w * 0.85, top + h * 0.12);
    g.lineTo(x + w * 0.85, bot - h * 0.38);
    g.lineTo(x + w * 0.38, bot - h * 0.38);
    g.lineTo(x + w * 0.38, bot - h * 0.12 - h * 0.48);
    g.moveTo(x + w * 0.1, bot - h * 0.12);
    g.lineTo(x + w * 1.1, bot - h * 0.12);
    g.stroke();
  }
  const t = new THREE.CanvasTexture(c);
  t.colorSpace = THREE.SRGBColorSpace; t.anisotropy = 8;
  return t;
}

function amphoraGeometry() {
  const P = [[0.0, 0.0], [0.085, 0.0], [0.09, 0.016], [0.066, 0.04], [0.058, 0.07], [0.1, 0.14], [0.17, 0.25], [0.205, 0.36],
    [0.21, 0.44], [0.195, 0.52], [0.16, 0.6], [0.11, 0.65], [0.082, 0.68], [0.075, 0.72], [0.078, 0.78], [0.098, 0.8], [0.1, 0.82], [0.085, 0.825], [0.07, 0.8]];
  const pts = P.map(([r, y]) => new THREE.Vector2(r, y));
  const body = new THREE.LatheGeometry(pts, 48);
  const parts = [body];
  for (const sx of [-1, 1]) {
    const curve = new THREE.CatmullRomCurve3([
      new THREE.Vector3(sx * 0.075, 0.76, 0), new THREE.Vector3(sx * 0.15, 0.79, 0),
      new THREE.Vector3(sx * 0.19, 0.72, 0), new THREE.Vector3(sx * 0.165, 0.6, 0),
    ]);
    parts.push(new THREE.TubeGeometry(curve, 24, 0.014, 8, false));
  }
  return mergeGeometries(parts.map((g) => (g.index ? g : g)), false);
}

function buildAmphora(tex, accent) {
  const mat = new THREE.MeshStandardMaterial({ map: tex, roughness: 0.38, side: THREE.DoubleSide });
  const m = mesh(amphoraGeometry(), mat);
  m.userData.accent = accent;
  return m;
}

// ------------------------------------------------------------------
// 植物：柏树（细高的火焰形）与橄榄树（扭曲的干 + 银灰的冠）
// ------------------------------------------------------------------
function lumpy(geom, seed, amount, freq) {
  const n = valueNoise(seed), pos = geom.attributes.position, v = new THREE.Vector3();
  for (let i = 0; i < pos.count; i++) {
    v.fromBufferAttribute(pos, i);
    const th = Math.atan2(v.x, v.z) / (Math.PI * 2) + 0.5;
    const k = 1 + amount * (fbm(n, th * 8, v.y * freq, 8, 3) - 0.5) * 2;
    v.x *= k; v.z *= k;
    pos.setXYZ(i, v.x, v.y, v.z);
  }
  geom.computeVertexNormals();
  return geom;
}

function buildCypress(M, height, seed) {
  const T = new THREE.Group();
  const pts = [];
  for (let i = 0; i <= 40; i++) {
    const t = i / 40;
    // 意大利柏：底部很快收拢成柱状，最宽处在三分之一高，顶端收成尖
    const r = 0.62 * Math.pow(Math.sin(Math.PI * Math.min(1, Math.pow(t, 0.55))), 0.7) * (1 - 0.55 * t);
    pts.push(new THREE.Vector2(Math.max(0.001, r), 0.3 + t * (height - 0.3)));
  }
  T.add(mesh(lumpy(new THREE.LatheGeometry(pts, 48), seed, 0.22, 4.5), M.cypress));
  T.add(mesh(new THREE.CylinderGeometry(0.09, 0.13, 0.5, 8), M.bark));
  return T;
}

function buildOlive(M, seed) {
  const T = new THREE.Group(), r = rng(seed);
  const trunk = new THREE.CatmullRomCurve3([
    new THREE.Vector3(0, 0, 0), new THREE.Vector3(0.25, 0.6, 0.05), new THREE.Vector3(-0.12, 1.2, 0.15), new THREE.Vector3(0.1, 1.8, -0.05),
  ]);
  T.add(mesh(new THREE.TubeGeometry(trunk, 32, 0.15, 12, false), M.bark));
  for (const [a, b] of [[[0.05, 1.6, 0], [0.95, 2.35, 0.45]], [[0.0, 1.7, 0.05], [-0.8, 2.45, -0.35]], [[0.1, 1.75, 0], [0.2, 2.6, -0.85]]]) {
    T.add(mesh(new THREE.TubeGeometry(new THREE.LineCurve3(new THREE.Vector3(...a), new THREE.Vector3(...b)), 4, 0.07, 8, false), M.bark));
  }
  // 树冠：十几团大小不一、压扁的叶团。二十面体默认每个面独立顶点，先合并顶点，法线才是平滑的，
  // 否则就是第一版那种低多边形的"云"。
  for (let i = 0; i < 26; i++) {
    const base = new THREE.IcosahedronGeometry(0.28 + r() * 0.3, 4);
    base.deleteAttribute('normal'); base.deleteAttribute('uv');
    const g = lumpy(mergeVertices(base), seed + i, 0.45, 7);
    const c = mesh(g, M.olive);
    const ang = r() * Math.PI * 2, rad = 0.2 + r() * 1.25;
    c.position.set(Math.cos(ang) * rad, 2.05 + r() * 0.9 + (1.2 - rad) * 0.35, Math.sin(ang) * rad);
    c.scale.y = 0.68;
    T.add(c);
  }
  return T;
}

// ------------------------------------------------------------------
// 场景、灯光、机位
// ------------------------------------------------------------------
const renderer = new THREE.WebGLRenderer({ antialias: true, preserveDrawingBuffer: true });
renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
renderer.setSize(window.innerWidth, window.innerHeight);
renderer.shadowMap.enabled = SHADOWS;
renderer.shadowMap.type = THREE.PCFShadowMap;
renderer.toneMapping = THREE.ACESFilmicToneMapping;
renderer.toneMappingExposure = 0.82;
document.body.appendChild(renderer.domElement);

const scene = new THREE.Scene();
scene.background = new THREE.Color(0xc9c4bb);
const pmrem = new THREE.PMREMGenerator(renderer);
scene.environment = pmrem.fromScene(new RoomEnvironment(), 0.04).texture;
scene.environmentIntensity = 0.18;

const M = makeMaterials();

const earth = new THREE.Mesh(new THREE.PlaneGeometry(400, 400), M.earth);
earth.rotation.x = -Math.PI / 2; earth.receiveShadow = true; scene.add(earth);

const temple = buildTemple(M);
scene.add(temple);
const front = temple.userData.front;

const pave = new THREE.Mesh(new THREE.PlaneGeometry(34, 58), M.pave);
pave.rotation.x = -Math.PI / 2; pave.position.set(0, 0.012, 5); pave.receiveShadow = true; scene.add(pave);

const athena = buildAthena(M);
athena.position.set(2.6, 0.012, front + 4.6);
athena.rotation.y = deg(38);
scene.add(athena);

const amphoraTex = amphoraTexture();
const jars = new THREE.Group();
const jarAccent = buildAmphora(amphoraTex, true); jarAccent.position.set(0, 0, 0); jarAccent.rotation.y = 0.6;
const jarB = buildAmphora(amphoraTex, false); jarB.position.set(-0.62, 0, -0.35); jarB.rotation.y = 2.1;
const jarC = buildAmphora(amphoraTex, false); jarC.rotation.set(0, 0.4, Math.PI / 2 - 0.12); jarC.position.set(-0.5, 0.2, 0.55);
jars.add(jarAccent, jarB, jarC);
jars.scale.setScalar(1.25);
jars.position.set(-2.4, 0.012, front + 3.6);
scene.add(jars);

for (const [x, z, h, s] of [[-12, -6, 8.5, 3], [-11, 4, 7.2, 4], [12.5, -12, 9, 5], [13.5, -3, 7.8, 6], [-13, 14, 6.8, 7]]) {
  const c = buildCypress(M, h, s); c.position.x = x; c.position.z = z; scene.add(c);
}
for (const [x, z, s] of [[-9, front + 6.5, 21], [9.5, front + 8, 31], [11, 9, 41]]) {
  const o = buildOlive(M, s); o.position.set(x, 0, z); o.rotation.y = s; scene.add(o);
}

const hemi = new THREE.HemisphereLight(0xe8eef3, 0x8a7f70, 0.55);
scene.add(hemi);
const sun = new THREE.DirectionalLight(0xfff2df, 3.6);
sun.castShadow = true;
sun.shadow.mapSize.set(4096, 4096);
const sc = sun.shadow.camera;
sc.left = -40; sc.right = 40; sc.top = 40; sc.bottom = -40; sc.near = 1; sc.far = 260;
sun.shadow.bias = -0.0003; sun.shadow.normalBias = 0.025; sun.shadow.radius = 3;
scene.add(sun, sun.target);

const SHOTS = {
  game: { frustum: 40, target: [0, 1.5, 6], azim: 45, elev: 33 },
  mid: { frustum: 13, target: [0.4, 1.8, front + 2.2], azim: 40, elev: 27 },
  close: { frustum: 2.6, target: [athena.position.x, 1.0, athena.position.z], azim: 38, elev: 16 },
};
const shot = SHOTS[SHOT] ?? SHOTS.game;

const aspect = window.innerWidth / window.innerHeight;
const camera = new THREE.OrthographicCamera(-shot.frustum * aspect / 2, shot.frustum * aspect / 2, shot.frustum / 2, -shot.frustum / 2, 0.1, 1200);
const target = new THREE.Vector3(...shot.target);
camera.position.copy(target).add(new THREE.Vector3().setFromSphericalCoords(300, deg(90 - shot.elev), deg(shot.azim)));
camera.lookAt(target);

// 太阳从镜头左前方照来：正面受光、右侧面落在阴影里，柱子的影子打在内殿墙上
sun.position.copy(target).add(new THREE.Vector3().setFromSphericalCoords(120, deg(90 - 54), deg(-38)));
sun.target.position.copy(target);

// ------------------------------------------------------------------
// 后期：环境光遮蔽 → 色调映射 → 黑白调色（只给标记物体留色）+ 雅典娜的光 + 暗角 + 颗粒
// ------------------------------------------------------------------
const size = new THREE.Vector2();
renderer.getDrawingBufferSize(size);
const composer = new EffectComposer(renderer);
composer.addPass(new RenderPass(scene, camera));
const gtao = new GTAOPass(scene, camera, size.x, size.y);
gtao.output = GTAOPass.OUTPUT.Default;
gtao.updateGtaoMaterial({ radius: shot.frustum > 20 ? 0.9 : 0.45, distanceExponent: 1.2, thickness: 1.2, scale: 1.0, samples: 16, distanceFallOff: 1.0, screenSpaceRadius: false });
gtao.updatePdMaterial({ lumaPhi: 10, depthPhi: 2, normalPhi: 3, radius: 6, samples: 16 });
gtao.blendIntensity = 1.0;
if (AO) composer.addPass(gtao);
composer.addPass(new OutputPass());

const maskTarget = new THREE.WebGLRenderTarget(size.x, size.y);
const MASK_ACCENT = new THREE.MeshBasicMaterial({ color: 0xff0000 });
const MASK_ATHENA = new THREE.MeshBasicMaterial({ color: 0x00ff00 });
const MASK_OFF = new THREE.MeshBasicMaterial({ color: 0x000000 });
function renderMask() {
  const saved = new Map();
  scene.traverse((o) => {
    if (o.isMesh) { saved.set(o, o.material); o.material = o.userData.accent ? MASK_ACCENT : o.userData.athena ? MASK_ATHENA : MASK_OFF; }
  });
  const bg = scene.background;
  scene.background = new THREE.Color(0x000000);
  renderer.shadowMap.autoUpdate = false;
  renderer.setRenderTarget(maskTarget);
  renderer.clear();
  renderer.render(scene, camera);
  renderer.setRenderTarget(null);
  renderer.shadowMap.autoUpdate = true;
  scene.background = bg;
  saved.forEach((m, o) => { o.material = m; });
}

const grade = new ShaderPass({
  uniforms: {
    tDiffuse: { value: null },
    tMask: { value: null },
    uGrade: { value: GRADE ? 1 : 0 },
    uAccent: { value: ACCENT ? 1 : 0 },
    uAspect: { value: aspect },
    uGlowPos: { value: new THREE.Vector2(0.5, 0.5) },
    uGlowRadius: { value: 0.05 },
    uGlowStrength: { value: 0.2 },
    uSeed: { value: 0.37 },
  },
  vertexShader: /* glsl */`
    varying vec2 vUv;
    void main() { vUv = uv; gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0); }`,
  fragmentShader: /* glsl */`
    uniform sampler2D tDiffuse, tMask;
    uniform float uGrade, uAccent, uAspect, uGlowRadius, uGlowStrength, uSeed;
    uniform vec2 uGlowPos;
    varying vec2 vUv;
    float hash12(vec2 p) { vec3 p3 = fract(vec3(p.xyx) * 0.1031); p3 += dot(p3, p3.yzx + 33.33); return fract((p3.x + p3.y) * p3.z); }
    void main() {
      vec3 c = texture2D(tDiffuse, vUv).rgb;
      vec4 mask = texture2D(tMask, vUv);
      float m = mask.r * uAccent;
      float self = mask.g;
      float l = dot(c, vec3(0.2126, 0.7152, 0.0722));
      float g = mix(l, smoothstep(0.0, 1.0, l), 0.45);             // 轻微的 S 曲线
      vec3 col = mix(c, vec3(g), uGrade * (1.0 - m));
      vec2 d = vUv - uGlowPos; d.x *= uAspect;
      float r2 = dot(d, d) / (uGlowRadius * uGlowRadius);
      col += uGlowStrength * exp(-r2 * 1.2) * (1.0 - 0.92 * self) * vec3(1.0);   // 雅典娜周身的光，只绕着她
      col += self * 0.025;
      vec2 q = vUv - 0.5; q.x *= uAspect;
      col *= mix(1.0, 0.84, smoothstep(0.5, 1.15, length(q)));     // 暗角
      col += (hash12(vUv * vec2(1777.0, 1009.0) + uSeed) - 0.5) * 0.028;   // 颗粒
      gl_FragColor = vec4(clamp(col, 0.0, 1.0), 1.0);
    }`,
});
// ShaderPass 构造时会克隆 uniforms，而渲染目标的贴图不能被克隆（会被置空），所以构造之后再挂上
grade.uniforms.tMask.value = maskTarget.texture;
composer.addPass(grade);

const tmpA = new THREE.Vector3(), tmpB = new THREE.Vector3();
function updateGlow() {
  // 光晕是屏幕空间的效果，必须用这一帧的相机矩阵投影。OrbitControls（以及任何 lookAt）
  // 在算新朝向之前会先用"新位置 + 旧朝向"刷新一次矩阵，直到 render 才改正——不在这里更新，
  // 光晕就是透过一台不存在的相机投出来的：镜头一动就滑开（相机离目标 300，放大成几百像素），停下才吸回去。
  camera.updateMatrixWorld();
  athena.updateMatrixWorld(true);
  tmpA.copy(athena.userData.chest).applyMatrix4(athena.matrixWorld).project(camera);
  tmpB.set(0, 1.9, 0).applyMatrix4(athena.matrixWorld).project(camera);
  const feet = new THREE.Vector3(0, 0, 0).applyMatrix4(athena.matrixWorld).project(camera);
  grade.uniforms.uGlowPos.value.set((tmpA.x + 1) / 2, (tmpA.y + 1) / 2);
  const heightUv = Math.abs(tmpB.y - feet.y) / 2;
  grade.uniforms.uGlowRadius.value = Math.max(0.008, heightUv * 0.75);
}

function frame() {
  updateGlow();
  renderMask();
  composer.render();
}

if (params.get('selftest') === 'glow') {
  // 运动自检：镜头每帧绕目标转 1.5°（和 OrbitControls 一样只改位置和朝向），比较这一帧光晕用的屏幕位置
  // 与她在这一帧画面里的真实投影。静止截图查不出这类问题——镜头不动时两者永远一致。
  const truth = new THREE.Vector3();
  let maxErr = 0;
  for (let i = 0; i < 60; i++) {
    camera.position.copy(target).add(new THREE.Vector3().setFromSphericalCoords(300, deg(90 - shot.elev), deg(shot.azim + i * 1.5)));
    camera.lookAt(target);
    frame();
    truth.copy(athena.userData.chest).applyMatrix4(athena.matrixWorld).project(camera);   // render 之后的矩阵才是这一帧真正用的
    const used = grade.uniforms.uGlowPos.value;
    if (i > 0) maxErr = Math.max(maxErr, Math.hypot(((truth.x + 1) / 2 - used.x) * size.x, ((truth.y + 1) / 2 - used.y) * size.y));
  }
  document.title = `glow-max-error-px:${maxErr.toFixed(2)}`;
} else if (SHOT) {
  // 固定机位不需要动画循环；同步画几帧（第一帧要先生成阴影贴图和环境光），无头截图时也不依赖 rAF 的节拍
  for (let i = 0; i < 3; i++) frame();
  document.title = 'ready';
} else {
  const controls = new OrbitControls(camera, renderer.domElement);
  controls.target.copy(target);
  controls.enableDamping = true;
  controls.maxPolarAngle = deg(84);
  controls.update();
  window.addEventListener('resize', () => {
    const w = window.innerWidth, h = window.innerHeight, a = w / h;
    const f = shot.frustum;
    camera.left = -f * a / 2; camera.right = f * a / 2; camera.top = f / 2; camera.bottom = -f / 2;
    camera.updateProjectionMatrix();
    renderer.setSize(w, h); composer.setSize(w, h);
    renderer.getDrawingBufferSize(size); maskTarget.setSize(size.x, size.y);
    grade.uniforms.uAspect.value = a;
  });
  renderer.setAnimationLoop(() => { controls.update(); frame(); });
}
