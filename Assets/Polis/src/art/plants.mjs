// 植物：橄榄（四个树龄，橄榄林里一份委托一棵）、柏树、月桂、盆栽、莨苕。全部代码生成，同一个种子长出同一棵树。
//
// 打样的橄榄树冠"有点像花椰菜"（设计稿 12.8）：十几个起伏很大的圆团挤在一起。橄榄冠的样子其实是稀疏、偏平、
// 有空隙的一簇簇银灰叶团，长在两三根扭曲分叉的老枝末端。所以这里：主干分叉成几根枝，叶团挂在枝端、压扁、
// 大小不一、彼此留缝——远看是一把撑开的灰伞，近看能看见枝。
// M2 美术审查：第一版的叶团仍是十几个平滑的大圆团（光滑法线让每一团都像一朵云），中景里还是花椰菜。
// 现在叶团小一半、多两三倍，是棱面的二十面体（逐面法线，一个个小平面反光，读得出是一簇簇叶子），在枝端撒开、彼此留缝。
import * as THREE from '../../vendor/three.bundle.mjs';
import { mergeGeometries } from '../../vendor/three.bundle.mjs';
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
  sapling: { height: 1.0, trunk: 0.04, limbs: 2, clumps: 10, clump: [0.09, 0.16], spread: 0.35, gnarl: 0.05 },
  young: { height: 1.9, trunk: 0.08, limbs: 3, clumps: 24, clump: [0.13, 0.24], spread: 0.75, gnarl: 0.12 },
  grown: { height: 2.7, trunk: 0.14, limbs: 3, clumps: 40, clump: [0.16, 0.3], spread: 1.25, gnarl: 0.22 },
  ancient: { height: 3.1, trunk: 0.24, limbs: 4, clumps: 54, clump: [0.18, 0.34], spread: 1.6, gnarl: 0.34 },
};

/**
 * 一小团棱面的叶子：二十面体（不细分）按种子把每个顶点推远拉近一点，删掉法线——合并时逐面重算，每个小平面各自反光。
 * 压扁（叶团是平的一层，不是一个球）。
 */
export function leafClump(size, r, flatten = 0.55) {
  const g = new THREE.IcosahedronGeometry(size, 0);
  const pos = g.attributes.position, v = new THREE.Vector3();
  const jitter = new Map();
  for (let i = 0; i < pos.count; i++) {
    v.fromBufferAttribute(pos, i);
    // 同一个顶点在不同的面里出现多次：按位置取同一个扰动，面与面之间不开缝
    const key = `${v.x.toFixed(4)},${v.y.toFixed(4)},${v.z.toFixed(4)}`;
    if (!jitter.has(key)) jitter.set(key, 0.78 + r() * 0.44);
    v.multiplyScalar(jitter.get(key));
    pos.setXYZ(i, v.x, v.y * flatten, v.z);
  }
  g.deleteAttribute('normal');
  g.deleteAttribute('uv');
  return g;
}

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
  // 叶团：一簇簇撒在枝端周围（偏平的一层），少数落在两根枝之间补出伞形；每簇之间留缝
  const leaves = [];
  const cloud = p.spread * 0.42 + 0.12;
  for (let i = 0; i < p.clumps; i++) {
    const tip = tips[i % tips.length];
    const next = tips[(i + 1) % tips.length];
    const c = tip.clone().lerp(next, r() < 0.25 ? r() * 0.5 : 0);
    const size = p.clump[0] + r() * (p.clump[1] - p.clump[0]);
    const g = leafClump(size, r);
    g.rotateY(r() * Math.PI);
    const a = r() * Math.PI * 2, d = Math.sqrt(r()) * cloud;
    g.translate(c.x + Math.cos(a) * d, c.y + (r() - 0.4) * cloud * 0.55, c.z + Math.sin(a) * d);
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

/**
 * 月桂：圆圆的一丛深色灌木，比橄榄暗、比柏树矮——神庙与神谕的植物。一簇簇棱面的小叶团堆成一个蛋形
 * （M2 美术审查：第一版是两三个平滑的大球，远看是一块黑色的石头）。
 */
export function buildLaurel(M, { seed = 5, size = 1 } = {}) {
  const r = rng(seed);
  const leaves = [];
  for (let i = 0; i < 26; i++) {
    const g = leafClump(0.15 + r() * 0.1, r, 0.8);
    const y = 0.25 + r() * 0.95;
    const reach = 0.5 * Math.sin(Math.PI * Math.min(1, y / 1.25)) + 0.08;
    const a = r() * Math.PI * 2, d = Math.sqrt(r()) * reach;
    g.translate(Math.cos(a) * d, y, Math.sin(a) * d);
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
  for (let i = 0; i < 9; i++) {
    const g = leafClump(0.09 + r() * 0.06, r, 0.7);
    g.translate((r() - 0.5) * 0.3, 0.64 + r() * 0.24, (r() - 0.5) * 0.3);
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
