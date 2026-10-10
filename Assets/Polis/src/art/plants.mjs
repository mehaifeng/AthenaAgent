// 植物：橄榄（四个树龄，橄榄林里一份委托一棵）、柏树、月桂、盆栽、莨苕。全部代码生成，同一个种子长出同一棵树。
//
// 打样的橄榄树冠"有点像花椰菜"（设计稿 12.8）：十几个起伏很大的圆团挤在一起。橄榄冠的样子其实是稀疏、偏平、
// 有空隙的一簇簇银灰叶团，长在两三根扭曲分叉的老枝末端。所以这里：主干分叉成几根枝，叶团挂在枝端、压扁、
// 大小不一、彼此留缝，起伏收小——远看是一把撑开的灰伞，近看能看见枝。
import * as THREE from '../../vendor/three.bundle.mjs';
import { mergeGeometries, mergeVertices } from '../../vendor/three.bundle.mjs';
import { valueNoise, fbm, rng } from './noise.mjs';
import { shadowed, m4 } from './kit.mjs';

const mesh = (g, m) => shadowed(new THREE.Mesh(g, m));

function merge(list) {
  return mergeGeometries(list.map((g) => {
    const n = g.index ? g.toNonIndexed() : g;
    for (const name of Object.keys(n.attributes)) if (!['position', 'normal', 'uv'].includes(name)) n.deleteAttribute(name);
    if (!n.attributes.normal) n.computeVertexNormals();
    if (!n.attributes.uv) n.setAttribute('uv', new THREE.Float32BufferAttribute(new Float32Array(n.attributes.position.count * 2), 2));
    return n;
  }));
}

/** 按噪声把一团几何的轮廓揉得不规则（只动 x / z）。 */
export function lumpy(geom, seed, amount, freq) {
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

/** 橄榄的树龄：树苗、小树、成树、老树（与 C# 的 PolisTreeStage 次序一致）。 */
export const OLIVE_STAGES = Object.freeze(['sapling', 'young', 'grown', 'ancient']);

const OLIVE = {
  sapling: { height: 1.0, trunk: 0.04, limbs: 2, clumps: 5, clump: [0.16, 0.26], spread: 0.35, gnarl: 0.05 },
  young: { height: 1.9, trunk: 0.08, limbs: 3, clumps: 10, clump: [0.24, 0.4], spread: 0.75, gnarl: 0.12 },
  grown: { height: 2.7, trunk: 0.14, limbs: 3, clumps: 16, clump: [0.3, 0.52], spread: 1.25, gnarl: 0.22 },
  ancient: { height: 3.1, trunk: 0.24, limbs: 4, clumps: 22, clump: [0.32, 0.58], spread: 1.6, gnarl: 0.34 },
};

/**
 * 一棵橄榄的两份几何：枝干与叶团（橄榄林按树龄实例化，几百棵也只有几个绘制调用）。
 * @returns {{ bark: THREE.BufferGeometry, leaves: THREE.BufferGeometry, height: number }}
 */
export function oliveGeometries(stage = 'grown', seed = 1) {
  const p = OLIVE[stage];
  if (!p) throw new Error(`plants: unknown olive stage '${stage}'`);
  const r = rng(seed * 7919 + 13);
  const fork = p.height * 0.48;
  // 主干：几处扭弯；老树的干更粗、分成两股
  const trunkPts = [new THREE.Vector3(0, 0, 0)];
  for (let i = 1; i <= 4; i++) {
    const y = (fork * i) / 4;
    trunkPts.push(new THREE.Vector3((r() - 0.5) * p.gnarl * 2, y, (r() - 0.5) * p.gnarl * 2));
  }
  const bark = [new THREE.TubeGeometry(new THREE.CatmullRomCurve3(trunkPts), 14, p.trunk, 8, false)];
  bark.push(new THREE.CylinderGeometry(p.trunk * 1.25, p.trunk * 1.6, 0.18, 8).translate(0, 0.09, 0));
  if (stage === 'ancient') {
    const twin = trunkPts.map((v, i) => v.clone().add(new THREE.Vector3(0.18 * Math.min(1, i / 2), 0, -0.1 * Math.min(1, i / 2))));
    bark.push(new THREE.TubeGeometry(new THREE.CatmullRomCurve3(twin), 12, p.trunk * 0.7, 7, false));
  }
  const top = trunkPts[trunkPts.length - 1];
  const tips = [];
  for (let l = 0; l < p.limbs; l++) {
    const a = (l / p.limbs) * Math.PI * 2 + r() * 0.9;
    const reach = p.spread * (0.65 + r() * 0.45);
    const tip = new THREE.Vector3(top.x + Math.cos(a) * reach, p.height * (0.78 + r() * 0.12), top.z + Math.sin(a) * reach);
    const mid = top.clone().lerp(tip, 0.5).add(new THREE.Vector3((r() - 0.5) * 0.3, 0.12, (r() - 0.5) * 0.3));
    bark.push(new THREE.TubeGeometry(new THREE.QuadraticBezierCurve3(top.clone(), mid, tip), 6, p.trunk * 0.55, 6, false));
    tips.push(tip);
  }
  // 叶团：挂在枝端附近，压扁、大小不一；一部分在枝端之间，补出伞形的冠
  const leaves = [];
  for (let i = 0; i < p.clumps; i++) {
    const tip = tips[i % tips.length];
    const next = tips[(i + 1) % tips.length];
    const along = i < tips.length * 2 ? 0 : r() * 0.6;
    const c = tip.clone().lerp(next, along);
    const size = p.clump[0] + r() * (p.clump[1] - p.clump[0]);
    const base = new THREE.IcosahedronGeometry(size, 1);
    base.deleteAttribute('normal'); base.deleteAttribute('uv');
    const g = lumpy(mergeVertices(base), seed * 31 + i, 0.22, 5);
    g.scale(1.25, 0.55 + r() * 0.15, 1.1);
    g.rotateY(r() * Math.PI);
    g.translate(c.x + (r() - 0.5) * size * 1.6, c.y + (r() - 0.35) * size * 1.1, c.z + (r() - 0.5) * size * 1.6);
    leaves.push(g);
  }
  return { bark: merge(bark), leaves: merge(leaves), height: p.height + p.clump[1] };
}

/** 一棵橄榄（单独摆放的那种：空地上、广场边、美术审查）。 */
export function buildOlive(M, { stage = 'grown', seed = 1 } = {}) {
  const T = new THREE.Group();
  const g = oliveGeometries(stage, seed);
  T.add(mesh(g.bark, M.bark));
  T.add(mesh(g.leaves, M.olive));
  T.userData.height = g.height;
  return T;
}

/** 柏树（打样）：细长的火焰形。 */
export function buildCypress(M, { height = 6, seed = 3 } = {}) {
  const T = new THREE.Group();
  const pts = [];
  for (let i = 0; i <= 30; i++) {
    const t = i / 30;
    const rr = 0.62 * Math.pow(Math.sin(Math.PI * Math.min(1, Math.pow(t, 0.55))), 0.7) * (1 - 0.55 * t);
    pts.push(new THREE.Vector2(Math.max(0.001, rr), 0.3 + t * (height - 0.3)));
  }
  T.add(mesh(lumpy(new THREE.LatheGeometry(pts, 24), seed, 0.22, 4.5), M.cypress));
  T.add(mesh(new THREE.CylinderGeometry(0.09, 0.13, 0.5, 8), M.bark));
  T.userData.height = height;
  return T;
}

/** 月桂：圆圆的一丛深色灌木，比橄榄暗、比柏树矮——神庙与神谕的植物。 */
export function buildLaurel(M, { seed = 5, size = 1 } = {}) {
  const r = rng(seed);
  const leaves = [];
  for (let i = 0; i < 9; i++) {
    const base = new THREE.IcosahedronGeometry(0.32 + r() * 0.18, 1);
    base.deleteAttribute('normal'); base.deleteAttribute('uv');
    const g = lumpy(mergeVertices(base), seed * 13 + i, 0.18, 6);
    const a = r() * Math.PI * 2, d = r() * 0.45;
    g.translate(Math.cos(a) * d, 0.42 + r() * 0.55, Math.sin(a) * d);
    leaves.push(g);
  }
  const T = new THREE.Group();
  const crown = mesh(merge(leaves), M.laurel);
  crown.scale.setScalar(size);
  T.add(crown);
  T.userData.height = 1.3 * size;
  return T;
}

/** 盆栽：一只陶盆里一小丛橄榄苗（常用的建筑门口摆着它）。 */
export function buildPottedPlant(M, { seed = 9 } = {}) {
  const T = new THREE.Group();
  const pot = new THREE.LatheGeometry([[0.0, 0], [0.18, 0], [0.22, 0.04], [0.27, 0.36], [0.3, 0.4], [0.27, 0.42]].map(([x, y]) => new THREE.Vector2(x, y)), 16);
  T.add(mesh(pot, M.terracotta));
  const r = rng(seed);
  const leaves = [];
  for (let i = 0; i < 5; i++) {
    const base = new THREE.IcosahedronGeometry(0.16 + r() * 0.08, 1);
    base.deleteAttribute('normal'); base.deleteAttribute('uv');
    const g = lumpy(mergeVertices(base), seed * 3 + i, 0.2, 6);
    g.scale(1.2, 0.75, 1.2);
    g.translate((r() - 0.5) * 0.24, 0.62 + r() * 0.22, (r() - 0.5) * 0.24);
    leaves.push(g);
  }
  T.add(mesh(new THREE.CylinderGeometry(0.02, 0.025, 0.3, 6).translate(0, 0.55, 0), M.bark));
  T.add(mesh(merge(leaves), M.olive));
  T.userData.height = 0.95;
  return T;
}

/** 莨苕（acanthus）：一簇向外翻的尖叶，柱头上那种叶子的原型——铺在场馆台地的边上。 */
export function buildAcanthus(M, { seed = 11 } = {}) {
  const r = rng(seed);
  const leaves = [];
  for (let i = 0; i < 9; i++) {
    const s = new THREE.Shape();
    s.moveTo(0, 0);
    s.bezierCurveTo(0.09, 0.12, 0.08, 0.32, 0, 0.46);
    s.bezierCurveTo(-0.08, 0.32, -0.09, 0.12, 0, 0);
    const g = new THREE.ShapeGeometry(s, 4);
    g.rotateX(-0.5 - r() * 0.5);
    g.rotateY((i / 9) * Math.PI * 2 + r() * 0.3);
    leaves.push(g);
  }
  const T = new THREE.Group();
  T.add(mesh(merge(leaves), M.ivy));
  T.userData.height = 0.4;
  return T;
}

export { m4 };
