// 外部人物绑到程序化骨架（资产清单里的 "rig": "procedural"，2026-10-12）。一个站成 A-pose 的外部网格（文生 3D、雕刻、扫描……）
// 自动找出关节位置、算出蒙皮权重，蒙在这个人物自己的程序化骨架上——走 / 跑 / 停 / 做事 / 交付 / 说话 / 挥手 / 接过、
// 动作之间的过渡、头看向对方、手里的家什、步频跟着速度走，全部沿用程序化人物的那一套（poses.mjs、rig.mjs）。
// 外部模型只换掉"长什么样"。另一条路——模型自带动画片段（清单的 animations）——要别人先在别处绑骨做动作，
// 片段还只能按 1 倍速播（脚打滑）、手里拿不了东西、不会转头。
//
// 模型的要求（Docs/GameMode_AssetManifest_CN.md）：清单的 scale / rotateY / offset 摆好之后单位是米、脚底 y = 0、面朝 +z、
// 左手在 +x；A-pose——手臂离开身体，腋下以下与躯干之间有空当（找手臂靠这个空当）；两脚分开。
//
// 找关节靠水平切片：每 1 厘米一层，三角形和水平面的交线投影到 x 上的区间。腋下以下的切片分成"右臂、躯干、左臂"三段，
// 衣摆以下分成两条腿；脖子是头和肩之间最窄的一层。切三角形而不是挑顶点：减过面的网格在平坦处三角形很大，
// 按顶点挑一层会在衣裙中间凭空断开（第一版分析脚本就这样把一条裙子切成了三段）。
//
// 蒙皮按部位分（skinPart）：
//   手臂（腋下以下、躯干那一段以外）沿臂轴分给上臂、前臂、手；
//   肩头（腋下以上）按离臂轴的远近匀一部分给上臂——手臂从 A-pose 放下来，肩头跟着动，不在腋下撕开；
//   头按高度从脖子渐变；
//   腰以下的衣裙一部分跟胯、一部分按左右跟两条大腿、一部分跟裙摆关节（poses 的 skirt：走起来前后摆、跑起来往后飘），
//   两腿之间按左右渐变——裙子跟着腿摆但不会被两条腿撕开；衣摆附近绕着腿的那一圈再分一点给小腿，
//   小腿往后收的时候衣摆被带过去，不让腿从裙子后面戳出来；衣摆以下露出来的小腿和脚跟小腿。
// 最后在网格上（按位置焊接的邻接）平滑几轮，再每个顶点留最大的四个。
import * as THREE from '../../vendor/three.bundle.mjs';
import { restPose } from './poses.mjs';

/** 蒙皮用到的关节，顺序就是 skinIndex 里的编号。 */
export const BONES = Object.freeze(['hips', 'spine', 'head', 'shoulderL', 'elbowL', 'handL', 'shoulderR', 'elbowR', 'handR', 'legL', 'kneeL', 'legR', 'kneeR', 'skirt']);
const B = Object.freeze(Object.fromEntries(BONES.map((name, i) => [name, i])));
const NB = BONES.length;

/** 切片间距（米）、同一层里两段相距不到多少算一段。 */
export const SLICE = 0.01;
const MERGE_GAP = 0.004;
/** 程序化人物的标准身高：比例按它换算（k = 身高 / 1.75）。 */
const BASE_HEIGHT = 1.75;
/** 程序化骨架里髋关节、肩关节的高度比例（rig.mjs：髋关节 0.93、全身 1.75）。 */
const HIP_RATIO = 0.93 / BASE_HEIGHT;

const s1 = (a, b, x) => { const k = Math.min(1, Math.max(0, (x - a) / (b - a))); return k * k * (3 - 2 * k); };
const width = (iv) => iv.x1 - iv.x0;
const midX = (iv) => (iv.x0 + iv.x1) / 2;
const midZ = (iv) => (iv.z0 + iv.z1) / 2;

/**
 * 把 glTF 场景里的每个网格搬进人物的坐标系（清单的 scale / rotateY / offset 之后）：一份几何、一个材质。
 * 模型里原有的蒙皮与变形目标丢掉——骨架换成程序化的那一副。
 */
export function collectBody(scene, entry) {
  scene.updateMatrixWorld(true);
  const rootInv = new THREE.Matrix4().copy(scene.matrixWorld).invert();
  const place = new THREE.Matrix4().compose(
    new THREE.Vector3(...(entry.offset ?? [0, 0, 0])),
    new THREE.Quaternion().setFromAxisAngle(new THREE.Vector3(0, 1, 0), THREE.MathUtils.degToRad(entry.rotateY ?? 0)),
    new THREE.Vector3().setScalar(entry.scale ?? 1),
  );
  const parts = [];
  scene.traverse((o) => {
    if (!o.isMesh || !o.geometry?.attributes?.position) return;
    const geometry = o.geometry.clone();
    for (const name of ['skinIndex', 'skinWeight']) if (geometry.attributes[name]) geometry.deleteAttribute(name);
    geometry.morphAttributes = {};
    geometry.applyMatrix4(new THREE.Matrix4().multiplyMatrices(place, new THREE.Matrix4().multiplyMatrices(rootInv, o.matrixWorld)));
    parts.push({ geometry, material: o.material, name: o.name ?? '' });
  });
  return parts;
}

/**
 * 水平切片：每 step 米一层，每层是若干段 { x0, x1, z0, z1 }（三角形与水平面的交线投影到 x 上、相连的并成一段）。
 * @returns {{ minY: number, maxY: number, step: number, slices: Array<{ y: number, intervals: Array<{x0:number,x1:number,z0:number,z1:number}> }> }}
 */
export function sliceBody(parts, step = SLICE) {
  let minY = Infinity, maxY = -Infinity;
  for (const { geometry } of parts) {
    const p = geometry.attributes.position;
    for (let i = 0; i < p.count; i++) { const y = p.getY(i); if (y < minY) minY = y; if (y > maxY) maxY = y; }
  }
  if (!Number.isFinite(minY)) throw new Error('the model has no vertices');
  const count = Math.max(1, Math.floor((maxY - minY) / step) + 1);
  const raw = Array.from({ length: count }, () => []);
  const P = [new THREE.Vector3(), new THREE.Vector3(), new THREE.Vector3()];
  for (const { geometry } of parts) {
    const pos = geometry.attributes.position, index = geometry.index;
    const n = index ? index.count : pos.count;
    for (let t = 0; t + 2 < n; t += 3) {
      for (let j = 0; j < 3; j++) P[j].fromBufferAttribute(pos, index ? index.getX(t + j) : t + j);
      const y0 = Math.min(P[0].y, P[1].y, P[2].y), y1 = Math.max(P[0].y, P[1].y, P[2].y);
      const k0 = Math.max(0, Math.ceil((y0 - minY) / step)), k1 = Math.min(count - 1, Math.floor((y1 - minY) / step));
      for (let k = k0; k <= k1; k++) {
        const y = minY + k * step;
        let x0 = Infinity, x1 = -Infinity, z0 = Infinity, z1 = -Infinity, hits = 0;
        for (let j = 0; j < 3; j++) {
          const a = P[j], b = P[(j + 1) % 3];
          if (a.y === b.y || (a.y - y) * (b.y - y) > 0) continue;
          const f = (y - a.y) / (b.y - a.y);
          const x = a.x + (b.x - a.x) * f, z = a.z + (b.z - a.z) * f;
          if (x < x0) x0 = x;
          if (x > x1) x1 = x;
          if (z < z0) z0 = z;
          if (z > z1) z1 = z;
          hits++;
        }
        if (hits >= 2) raw[k].push(x0, x1, z0, z1);
      }
    }
  }
  return { minY, maxY, step, slices: raw.map((list, k) => ({ y: minY + k * step, intervals: mergeIntervals(list) })) };
}

function mergeIntervals(list) {
  const items = [];
  for (let i = 0; i < list.length; i += 4) items.push([list[i], list[i + 1], list[i + 2], list[i + 3]]);
  items.sort((a, b) => a[0] - b[0]);
  const out = [];
  for (const [x0, x1, z0, z1] of items) {
    const last = out[out.length - 1];
    if (last && x0 <= last.x1 + MERGE_GAP) {
      last.x1 = Math.max(last.x1, x1); last.z0 = Math.min(last.z0, z0); last.z1 = Math.max(last.z1, z1);
    } else out.push({ x0, x1, z0, z1 });
  }
  return out;
}

/** 离 x 最近（或包含 x）的那一段。 */
function central(slice, x) {
  let best = null, bestD = Infinity;
  for (const iv of slice.intervals) {
    const d = x < iv.x0 ? iv.x0 - x : x > iv.x1 ? x - iv.x1 : 0;
    if (d < bestD) { bestD = d; best = iv; }
  }
  return best;
}

function sliceAt(profile, y) {
  const k = Math.round((y - profile.minY) / profile.step);
  return profile.slices[Math.min(profile.slices.length - 1, Math.max(0, k))];
}

/**
 * 关节位置（人物坐标系，米）。找不到手臂（没有站成 A-pose、手贴着身体）就抛错——页面退回程序化的那一位，记一笔原因。
 */
export function fitLandmarks(profile) {
  const { slices, minY, maxY, step } = profile;
  const H = maxY - minY;
  const k = H / BASE_HEIGHT;
  if (H < 0.3) throw new Error(`the model is ${H.toFixed(2)} m tall; set "scale" so the figure is life-size`);
  const at = (y) => sliceAt(profile, y);

  // 身体的中线：头顶往下 8 厘米那一层的中点
  const crown = at(maxY - 0.08 * k);
  const cx = crown.intervals.length ? midX(central(crown, 0)) : 0;

  // 脖子：0.78H–0.93H 之间最窄的一层
  let neck = null;
  for (const s of slices) {
    if (s.y < minY + 0.78 * H || s.y > minY + 0.93 * H) continue;
    const c = central(s, cx);
    if (c && (!neck || width(c) < neck.w)) neck = { y: s.y, w: width(c) };
  }
  if (!neck) throw new Error('cannot find the neck');

  // 肩：从脖子往下，第一层宽过脖子 2.3 倍的
  let shoulderTopY = null;
  for (let y = neck.y; y > minY + 0.6 * H; y -= step) {
    const c = central(at(y), cx);
    if (c && width(c) > 2.3 * neck.w) { shoulderTopY = y; break; }
  }
  if (shoulderTopY == null) throw new Error('cannot find the shoulders');

  // 手臂：从肩往下，每层躯干那一段两侧各自的几段（手指张开时一侧有好几段，并成一段）
  const merged = (list) => list.length ? {
    x0: Math.min(...list.map((iv) => iv.x0)), x1: Math.max(...list.map((iv) => iv.x1)),
    z0: Math.min(...list.map((iv) => iv.z0)), z1: Math.max(...list.map((iv) => iv.z1)),
  } : null;
  const rows = [];
  for (let y = shoulderTopY; y > minY + 0.3 * H; y -= step) {
    const s = at(y), torso = central(s, cx);
    if (!torso) { rows.push({ y, torso: null, L: null, R: null }); continue; }
    rows.push({ y, torso, L: merged(s.intervals.filter((iv) => iv.x0 > torso.x1)), R: merged(s.intervals.filter((iv) => iv.x1 < torso.x0)) });
  }
  // 腋下：两侧同时离开躯干、并且往下连着三层都离开（一层衣褶的缝不算）
  const start = rows.findIndex((r, i) => [0, 1, 2].every((d) => rows[i + d]?.L && rows[i + d]?.R));
  if (start < 0) throw new Error('the arms do not stand apart from the body; the model must be in an A-pose');
  const armpitY = rows[start].y;
  const arms = {};
  for (const side of ['L', 'R']) {
    const samples = [];
    for (let i = start, misses = 0; i < rows.length && misses < 3; i++) {
      const iv = rows[i][side];
      if (!iv) { misses++; continue; }
      misses = 0;
      samples.push({ y: rows[i].y, x: midX(iv), z: midZ(iv), w: width(iv) });
    }
    if (samples.length < 10) throw new Error(`the ${side === 'L' ? 'left' : 'right'} arm is too short to fit (${samples.length} slices)`);
    arms[side] = samples;
  }

  // 衣摆与腿：从脚往上，两条腿各一段，衣摆处并成跨过中线的一段
  let hemY = minY;
  for (let y = minY + 0.02 * k; y < minY + 0.6 * H; y += step) {
    const across = at(y).intervals.find((iv) => iv.x0 < cx && iv.x1 > cx);
    if (across && width(across) > 0.2 * k) { hemY = y; break; }
  }
  const legsVisible = hemY > minY + 0.07 * k;
  const ankleY = minY + 0.04 * H;
  const hipY = minY + HIP_RATIO * H;
  const ankles = {};
  const legSlice = at(Math.min(hemY - 0.02 * k, minY + 0.1 * k));
  for (const [side, sx] of [['L', 1], ['R', -1]]) {
    const iv = legsVisible
      ? legSlice.intervals.filter((c) => (midX(c) - cx) * sx > 0).sort((a, b) => Math.abs(midX(a) - cx) - Math.abs(midX(b) - cx))[0]
      : null;
    ankles[side] = iv ? new THREE.Vector3(midX(iv), ankleY, midZ(iv)) : new THREE.Vector3(cx + sx * 0.085 * k, ankleY, 0);
  }

  // 腰：髋与腋下之间躯干最窄的一层
  let waist = null;
  for (const s of slices) {
    if (s.y < hipY + 0.05 * k || s.y > armpitY - 0.05 * k) continue;
    const c = central(s, cx);
    if (c && (!waist || width(c) < waist.w)) waist = { y: s.y, w: width(c) };
  }
  const waistY = waist?.y ?? (hipY + armpitY) / 2;

  const zAt = (y) => { const c = central(at(y), cx); return c ? midZ(c) : 0; };
  const hipZ = Math.max(-0.03 * k, Math.min(0.03 * k, zAt(hipY)));
  const L = {
    H, k, cx, minY, maxY, neckY: neck.y, shoulderTopY, armpitY, waistY, hipY, hemY, legsVisible,
    pelvis: new THREE.Vector3(cx, hipY + 0.02 * k, hipZ),
    head: new THREE.Vector3(cx, (neck.y + maxY) / 2, zAt((neck.y + maxY) / 2)),
  };
  L.spine = L.pelvis.clone().add(new THREE.Vector3(0, 0.03 * k, 0));

  for (const [side, sx] of [['L', 1], ['R', -1]]) {
    const samples = arms[side];
    const sampleAt = (y) => samples.reduce((m, p) => (Math.abs(p.y - y) < Math.abs(m.y - y) ? p : m), samples[0]);
    const tip = samples[samples.length - 1];
    const finger = new THREE.Vector3(tip.x, tip.y, tip.z);
    // 手腕：先按身高推（指尖顺着手臂往回一个手长 0.108H），再在推出来的高度上下 6 厘米里取最窄的一层（前臂往手腕收细）。
    // 不能在整条手臂的下半段找最窄：斜着的手被水平面斜切，指尖那几层也很窄——合成人台上一找就落进了手掌，
    // 并指的手也一样；只在张开五指的手上碰巧对
    const top = samples[0];
    const along = new THREE.Vector3(tip.x - top.x, tip.y - top.y, tip.z - top.z).normalize();
    const guess = finger.clone().addScaledVector(along, -0.108 * H);
    const nearGuess = samples.filter((p) => Math.abs(p.y - guess.y) <= 0.06 * k);
    const wristSample = nearGuess.length ? nearGuess.reduce((m, p) => (p.w < m.w ? p : m), nearGuess[0]) : null;
    const wrist = wristSample ? new THREE.Vector3(wristSample.x, wristSample.y, wristSample.z) : guess;
    // 肩关节：肩头往下 4.5 厘米，躯干外缘往里 5 厘米（三角肌的厚度），前后在躯干那一层的中间（都按身高缩放）。
    // 不用臂轴往上外推：A-pose 里肘常是微弯的，前臂比上臂外展得多，拿前臂定的轴推到肩高会落进胸口（这个模型推出来离中线 0.11 米）
    const ys = shoulderTopY - 0.045 * k;
    const torso = central(at(ys), cx);
    const half = torso ? (sx > 0 ? torso.x1 - cx : cx - torso.x0) : 0.2 * k;
    const s = new THREE.Vector3(cx + sx * Math.max(0.6 * half, half - 0.05 * k), ys, torso ? midZ(torso) : 0);
    L[`shoulder${side}`] = s;
    // 肘：肩到腕的 0.51 那个高度上（程序化骨架的上臂 0.26、前臂 0.25），手臂那一层的中心——肘弯着也找得准
    const ye = s.y + 0.51 * (wrist.y - s.y);
    const elbowSample = ye < armpitY ? sampleAt(ye) : null;
    L[`elbow${side}`] = elbowSample ? new THREE.Vector3(elbowSample.x, elbowSample.y, elbowSample.z) : s.clone().lerp(wrist, 0.51);
    L[`wrist${side}`] = wrist;
    L[`finger${side}`] = finger;
    L[`fingerY${side}`] = tip.y;
    const ankle = ankles[side];
    const hip = new THREE.Vector3(ankle.x, hipY, hipZ);
    L[`hip${side}`] = hip;
    L[`knee${side}`] = hip.clone().lerp(ankle, 0.5);
    L[`ankle${side}`] = ankle;
  }
  return L;
}

/** 腿的轴线在高度 y 处的点（髋 → 膝 → 踝）。 */
function legPointAt(L, side, y, target) {
  const hip = L[`hip${side}`], knee = L[`knee${side}`], ankle = L[`ankle${side}`];
  if (y >= knee.y) return target.copy(knee).lerp(hip, Math.min(1, (y - knee.y) / Math.max(1e-6, hip.y - knee.y)));
  return target.copy(ankle).lerp(knee, Math.max(0, (y - ankle.y) / Math.max(1e-6, knee.y - ankle.y)));
}

/**
 * 一个顶点的权重（未平滑）。out：长度为骨头数的数组。
 */
function weightsAt(v, L, profile, torsoRows, out) {
  out.fill(0);
  const k = L.k;
  // —— 手臂：腋下以下、指尖以上，切片里躯干那一段以外 ——
  const row = torsoRows[Math.min(torsoRows.length - 1, Math.max(0, Math.round((v.y - profile.minY) / profile.step)))];
  for (const [side, sx] of [['L', 1], ['R', -1]]) {
    if (v.y > L.armpitY + profile.step * 0.5 || v.y < L[`fingerY${side}`] - 0.02 * k || !row) continue;
    const outside = sx > 0 ? v.x > row.x1 + 0.002 : v.x < row.x0 - 0.002;
    if (!outside) continue;
    const S = L[`shoulder${side}`], E = L[`elbow${side}`], W = L[`wrist${side}`], F = L[`finger${side}`];
    const dx = F.x - S.x, dy = F.y - S.y, dz = F.z - S.z, len = Math.hypot(dx, dy, dz);
    const along = (p) => ((p.x - S.x) * dx + (p.y - S.y) * dy + (p.z - S.z) * dz) / len;
    const t = along(v), tE = along(E), tW = along(W);
    const hand = s1(tW - 0.012 * k, tW + 0.018 * k, t);
    const fore = s1(tE - 0.035 * k, tE + 0.035 * k, t) * (1 - hand);
    out[B[`hand${side}`]] = hand;
    out[B[`elbow${side}`]] = fore;
    out[B[`shoulder${side}`]] = 1 - hand - fore;
    return out;
  }

  // —— 躯干（腰以上）：头按高度从脖子渐变；肩头按离臂轴的远近匀一部分给上臂 ——
  const upper = s1(L.waistY - 0.04 * k, L.waistY + 0.04 * k, v.y);
  if (upper > 0) {
    const head = s1(L.neckY - 0.015 * k, L.neckY + 0.035 * k, v.y);
    let spine = 1 - head;
    if (v.y > L.armpitY - 0.03 * k && spine > 0) {
      for (const side of ['L', 'R']) {
        const S = L[`shoulder${side}`], F = L[`finger${side}`];
        const dx = F.x - S.x, dy = F.y - S.y, dz = F.z - S.z, len = Math.hypot(dx, dy, dz);
        const t = ((v.x - S.x) * dx + (v.y - S.y) * dy + (v.z - S.z) * dz) / len;
        const px = v.x - S.x - (dx / len) * t, py = v.y - S.y - (dy / len) * t, pz = v.z - S.z - (dz / len) * t;
        const cap = s1(-0.03 * k, 0.06 * k, t) * (1 - s1(0.06 * k, 0.11 * k, Math.hypot(px, py, pz)));
        if (cap > 0) { const share = spine * cap; out[B[`shoulder${side}`]] += share * upper; spine -= share; }
      }
    }
    out[B.head] += head * upper;
    out[B.spine] += spine * upper;
  }
  if (upper >= 1) return out;

  // —— 腰以下：衣裙、露出来的小腿和脚 ——
  const lowerShare = 1 - upper;
  const legPoint = new THREE.Vector3();
  const distTo = (side) => { legPointAt(L, side, v.y, legPoint); return Math.hypot(v.x - legPoint.x, v.z - legPoint.z); };
  const dL = distTo('L'), dR = distTo('R');
  const near = dL < dR ? 'L' : 'R';
  // 腿：脚踝以下全是；衣摆以下离腿轴 12 厘米以内（短衣露出来的大腿粗）；衣摆边上 8 厘米以内。衣摆垂得更低的褶子离腿远，仍是衣裙
  const dLeg = Math.min(dL, dR);
  const isLeg = L.legsVisible && (v.y < L.ankleL.y + 0.03 * k || (v.y < L.hemY && dLeg < 0.12 * k) || (v.y < L.hemY + 0.015 * k && dLeg < 0.08 * k));
  if (isLeg) {
    // 露出来的腿按高度分：膝以下跟小腿、膝以上跟大腿（短衣的人衣摆在膝上，露出来的有一截是大腿）
    const shin = s1(L[`knee${near}`].y + 0.03 * k, L[`knee${near}`].y - 0.03 * k, v.y);
    out[B[`knee${near}`]] += shin * lowerShare;
    out[B[`leg${near}`]] += (1 - shin) * lowerShare;
    return out;
  }
  const f = Math.min(1, Math.max(0, (L.hipY - v.y) / Math.max(1e-6, L.hipY - L.hemY)));
  let thighs = 0.5 * s1(0, 0.5, f);
  let skirt = 0.35 * s1(0.15, 1, f);
  let hips = 1 - thighs - skirt;
  // 衣摆附近绕着腿的那一圈：分一点给小腿（小腿往后收时把衣摆带过去）
  const kneeL = 0.45 * s1(0.55, 1, f) * (1 - s1(0.07 * k, 0.15 * k, dL));
  const kneeR = 0.45 * s1(0.55, 1, f) * (1 - s1(0.07 * k, 0.15 * k, dR));
  const rest = Math.max(0, 1 - kneeL - kneeR);
  thighs *= rest; skirt *= rest; hips *= rest;
  // 左右：从一侧到另一侧整幅渐变。第一版只在中线两边各 7 厘米里过渡，两条大腿反向一摆，前襟正中折出一道硬棱，
  // 翻起来的那半幅正对太阳、象牙白去色后整块发白（2026-10-12，迈步的侧面看得最清楚）
  const left = s1(-0.18 * k, 0.18 * k, v.x - L.cx);
  out[B.legL] += thighs * left * lowerShare;
  out[B.legR] += thighs * (1 - left) * lowerShare;
  out[B.skirt] += skirt * lowerShare;
  out[B.hips] += hips * lowerShare;
  out[B.kneeL] += kneeL * lowerShare;
  out[B.kneeR] += kneeR * lowerShare;
  return out;
}

/**
 * 给一份几何算蒙皮权重，写成 skinIndex / skinWeight（每个顶点四个）。smooth：在按位置焊接的网格上平滑几轮。
 * @returns {Float32Array} 平滑后、截成四个之前的完整权重（顶点数 × 骨头数；测试用）
 */
export function skinPart(geometry, L, profile, { smooth = 12 } = {}) {
  const pos = geometry.attributes.position;
  const n = pos.count;
  // 每层切片里躯干那一段（手臂分在它两侧）
  const torsoRows = profile.slices.map((s) => central(s, L.cx));
  // 按位置焊接：同一位置的顶点（贴图接缝两侧）是同一个点
  const keyOf = new Map();
  const id = new Int32Array(n);
  const v = new THREE.Vector3();
  const reps = [];
  for (let i = 0; i < n; i++) {
    v.fromBufferAttribute(pos, i);
    const key = `${Math.round(v.x * 1e5)},${Math.round(v.y * 1e5)},${Math.round(v.z * 1e5)}`;
    let j = keyOf.get(key);
    if (j === undefined) { j = reps.length; keyOf.set(key, j); reps.push(i); }
    id[i] = j;
  }
  const m = reps.length;
  let w = new Float32Array(m * NB);
  const tmp = new Float32Array(NB);
  for (let j = 0; j < m; j++) {
    v.fromBufferAttribute(pos, reps[j]);
    weightsAt(v, L, profile, torsoRows, tmp);
    w.set(tmp, j * NB);
  }
  if (smooth > 0) {
    // 邻接（CSR）：三角形的三条边
    const index = geometry.index;
    const tris = index ? index.count : n;
    const degree = new Int32Array(m);
    const edges = [];
    for (let t = 0; t + 2 < tris; t += 3) {
      const a = id[index ? index.getX(t) : t], b = id[index ? index.getX(t + 1) : t + 1], c = id[index ? index.getX(t + 2) : t + 2];
      for (const [p, q] of [[a, b], [b, c], [c, a]]) { if (p === q) continue; edges.push(p, q); degree[p]++; degree[q]++; }
    }
    const offset = new Int32Array(m + 1);
    for (let j = 0; j < m; j++) offset[j + 1] = offset[j] + degree[j];
    const nbr = new Int32Array(offset[m]);
    const fill = offset.slice(0, m);
    for (let e = 0; e < edges.length; e += 2) { const p = edges[e], q = edges[e + 1]; nbr[fill[p]++] = q; nbr[fill[q]++] = p; }
    let next = new Float32Array(m * NB);
    for (let it = 0; it < smooth; it++) {
      for (let j = 0; j < m; j++) {
        const d = offset[j + 1] - offset[j];
        for (let b = 0; b < NB; b++) {
          let sum = 0;
          for (let e = offset[j]; e < offset[j + 1]; e++) sum += w[nbr[e] * NB + b];
          next[j * NB + b] = d ? 0.5 * w[j * NB + b] + 0.5 * (sum / d) : w[j * NB + b];
        }
      }
      [w, next] = [next, w];
    }
  }
  // 每个顶点留最大的四个，归一
  const skinIndex = new Uint16Array(n * 4), skinWeight = new Float32Array(n * 4);
  const order = Array.from({ length: NB }, (_, b) => b);
  for (let i = 0; i < n; i++) {
    const base = id[i] * NB;
    order.sort((a, b) => w[base + b] - w[base + a]);
    let total = 0;
    for (let q = 0; q < 4; q++) total += Math.max(0, w[base + order[q]]);
    for (let q = 0; q < 4; q++) {
      skinIndex[i * 4 + q] = order[q];
      skinWeight[i * 4 + q] = total > 0 ? Math.max(0, w[base + order[q]]) / total : q === 0 ? 1 : 0;
    }
  }
  geometry.setAttribute('skinIndex', new THREE.Uint16BufferAttribute(skinIndex, 4));
  geometry.setAttribute('skinWeight', new THREE.Float32BufferAttribute(skinWeight, 4));
  return w;
}

/**
 * 一件外部模型准备一次（搬进人物坐标、切片、找关节、算权重）；同一模型的每个人物实例共用这一份几何。
 * @returns {{ parts: Array<{ geometry: THREE.BufferGeometry, material: THREE.Material }>, landmarks: object, profile: object }}
 */
export function prepareExternalBody(scene, entry) {
  const parts = collectBody(scene, entry);
  if (!parts.length) throw new Error('the model has no meshes');
  const profile = sliceBody(parts);
  const landmarks = fitLandmarks(profile);
  for (const part of parts) skinPart(part.geometry, landmarks, profile);
  return { parts, landmarks, profile };
}

/**
 * 把外部身体装到一个程序化人物上（rig.buildFigure 的结果）：拆掉程序化的身体（手里的东西留着），关节挪到外部网格的
 * 关节上、转成它的 A-pose 做绑定姿势，蒙皮网格挂在根上，再摆回站姿。之后人物照常 pose()——动作、过渡、看向、
 * 手里的家什全是程序化的那一套，只是看见的是外部网格。
 */
export function attachExternalBody(figure, body) {
  const J = figure.joints, root = figure.root;
  const s = J.hips.parent.scale.x;
  const L = body.landmarks;
  // 程序化骨架里骨头的方向（零旋转时）：上臂、前臂、大腿、小腿都朝下，按它们自己的偏移量取，不抄常数
  const restDir = (joint) => joint.position.clone().normalize();
  const dirs = { upperL: restDir(J.elbowL), upperR: restDir(J.elbowR), foreL: restDir(J.handL), foreR: restDir(J.handR), thigh: restDir(J.kneeL), shin: new THREE.Vector3(0, -1, 0) };
  figure.dropBody();

  const local = (v) => v.clone().divideScalar(s);
  J.hips.position.copy(local(L.pelvis));
  J.hips.userData.base.copy(J.hips.position);
  J.spine.position.copy(local(L.spine.clone().sub(L.pelvis)));
  J.head.position.copy(local(L.head.clone().sub(L.spine)));
  J.skirt.position.set(0, 0, 0);
  for (const name of BONES) { J[name].rotation.set(0, 0, 0); J[name].scale.set(1, 1, 1); }
  J.head.rotation.set(0.06, 0, 0);   // apply() 给头的那一点前倾：绑定时也带着，站姿下头照原样
  const fromTo = (a, b) => new THREE.Quaternion().setFromUnitVectors(a, b.clone().normalize());
  for (const side of ['L', 'R']) {
    const S = L[`shoulder${side}`], E = L[`elbow${side}`], W = L[`wrist${side}`], F = L[`finger${side}`];
    J[`shoulder${side}`].position.copy(local(S.clone().sub(L.spine)));
    J[`elbow${side}`].position.copy(dirs[`upper${side}`].clone().multiplyScalar(S.distanceTo(E) / s));
    J[`hand${side}`].position.copy(dirs[`fore${side}`].clone().multiplyScalar(E.distanceTo(W) / s));
    // 绑定姿势：上臂、前臂、手转到外部网格的方向（A-pose）
    const qs = fromTo(dirs[`upper${side}`], E.clone().sub(S));
    const qe = fromTo(dirs[`fore${side}`], W.clone().sub(E).applyQuaternion(qs.clone().invert()));
    const qh = fromTo(new THREE.Vector3(0, -1, 0), F.clone().sub(W).applyQuaternion(qs.clone().multiply(qe).invert()));
    J[`shoulder${side}`].quaternion.copy(qs);
    J[`elbow${side}`].quaternion.copy(qe);
    J[`hand${side}`].quaternion.copy(qh);
    const hip = L[`hip${side}`], knee = L[`knee${side}`], ankle = L[`ankle${side}`];
    J[`leg${side}`].position.copy(local(hip.clone().sub(L.pelvis)));
    J[`knee${side}`].position.copy(dirs.thigh.clone().multiplyScalar(hip.distanceTo(knee) / s));
    const ql = fromTo(dirs.thigh, knee.clone().sub(hip));
    J[`leg${side}`].quaternion.copy(ql);
    J[`knee${side}`].quaternion.copy(fromTo(dirs.shin, ankle.clone().sub(knee).applyQuaternion(ql.clone().invert())));
  }
  root.updateMatrixWorld(true);
  const skeleton = new THREE.Skeleton(BONES.map((name) => J[name]));   // 骨头的逆矩阵按此刻（绑定姿势）算
  const meshes = [];
  for (const part of body.parts) {
    const mesh = new THREE.SkinnedMesh(part.geometry, part.material);
    mesh.name = `external:${part.name}`;
    mesh.castShadow = true;
    mesh.receiveShadow = true;
    // 姿势一变包围球就不准；她总在镜头中间附近，剔除省不下什么，宁可不剔
    mesh.frustumCulled = false;
    if (figure.spec?.glow) mesh.userData.athena = true;
    root.add(mesh);
    mesh.updateMatrixWorld(true);
    mesh.bind(skeleton, mesh.matrixWorld);
    meshes.push(mesh);
  }
  figure.apply(restPose());
  root.updateMatrixWorld(true);
  // 名牌、光、交东西的位置按外部身体的尺寸
  const scale = L.H / (figure.root.userData.height || L.H);
  root.userData.chest = new THREE.Vector3(L.cx, (L.shoulderL.y + L.waistY) / 2, 0);
  root.userData.headTop = new THREE.Vector3(L.cx, L.maxY + 0.05, 0);
  if (root.userData.hand) root.userData.hand.multiplyScalar(scale);
  root.userData.height = L.H;
  root.userData.external = true;
  root.userData.rig = 'procedural';
  figure.external = true;
  figure.skinned = meshes;
  figure.skeleton = skeleton;
  return figure;
}
