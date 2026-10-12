// 布料模拟（M3 追加，用户：弹簧骨的裙摆风一吹、人一动，双腿就露在裙摆外了——要带碰撞的布）。
// 纯逻辑，不碰 DOM、不碰渲染器，Node 单测覆盖（cloth.test.mjs）。
//
// 做法是游戏里常见的"代理网格 + 细网格蒙皮"：
//   - 代理：一张粗的环 × 段的格子（比如 10 × 24），按位置约束的 verlet 积分——重力、阻尼、风，结构 / 剪切 / 弯曲三种距离约束，
//     顶上几圈钉在髋的坐标系上（跟着身体走），每个点离它那一列钉住的点不超过一个系绳长度（快速移动时不被拉长），
//     每一步被几个胶囊 / 球（大腿、小腿、骨盆、髋）推出来——这就是"带碰撞"：腿把布顶在外面，布进不了腿；
//   - 细网格：drape 生成的那张带褶的衣料，每个顶点记下它相对代理格子上那一小片的偏移（切线坐标系里的三个数），
//     每帧按代理的新位置重建——褶是"贴"在会动的布上的浮雕，静止时和原来的几何完全一样（断言精确到 1e-6）。
// 不做的：自碰撞（布不和自己相交）、撕裂、真实的布料参数。单位：米、秒。所有坐标在世界里算，调用方自己换到网格的局部坐标。
//
// 身体按"径向"挡（M3 第三轮，用户：布料加了碰撞之后一穿模，双腿就整条露在裙摆外）。最近点推出（pushOut）只认离哪一面近：
// 一步里布点被腿扫过了腿的中轴，就被推到腿的另一面——前襟从两腿之间滑到身后，整条腿露在布的外面（实测：10.5 米 / 秒的冲刺里
// 腿外侧 73 % 的采样点、大腿 65 % 露在裙摆外）。裙摆是套在腿外面的一只筒，所以身体（body）不走 pushOut：在钉住的坐标系（髋）里，
// 每个布点沿它此刻的方位离身体轴的距离，不小于这个高度上每一块身体（胶囊、球）朝这个方位伸出去多远（截面的支撑函数）。
// 这是单向的——布只会被往外推，永远到不了腿的里面；两腿一前一后时，前襟搭在前腿外面、后摆搭在后腿外面，正是裙子的样子。
import * as THREE from '../../vendor/three.bundle.mjs';

export const CLOTH = Object.freeze({
  gravity: 9.8,
  damping: 0.985,     // 60 帧时每步保留多少速度（别的帧率按秒折算）
  follow: 0.955,      // 相对身体的速度每步保留多少：没有空气的世界里匀速奔跑的裙摆会一直往后积速度、拧成一团；这一项让它跟上身体，只在加减速、转身和腿踢到它时甩
  iterations: 6,      // 每步约束迭代几次（4 次时前襟会从两腿之间向后滑过去）
  thickness: 0.012,   // 布离碰撞体表面至少多远
  tetherSlack: 1.06,  // 系绳：离钉住的那个点最多是安放距离的几倍
  maxTilt: 0.95,      // 布离钉住它的那一列最多斜多少（弧度，54°）：厚羊毛不会像旗子一样平飘——10 米 / 秒的冲刺也只是甩到这个角度
  windDrag: 5.0,      // 风：按法线迎风的程度给加速度（米 / 秒² 每单位风）
  maxTurn: 0.12,      // 一步最多转多少（弧度，约 7°）：猛转身的那一帧按转角拆步，见 stepCloth
  maxSteps: 32,       // 一帧最多拆几步（180° 的转身 27 步）
  maxDt: 1 / 120,     // 一步最多积分多久；更长的帧拆成几步（60 帧两步、30 帧四步）：一步 1/60 时 6 米 / 秒的步速下结构拉长四成，1/120 时一成以内
  bend: 0.25,         // 弯曲约束的刚度（0 没有、1 和结构约束一样硬）
  floorY: 0.015,      // 地面：下摆不钻进地里
});

const TAN_TILT = Math.tan(0.95);
const _a = new THREE.Vector3(), _b = new THREE.Vector3(), _c = new THREE.Vector3(), _d = new THREE.Vector3(), _n = new THREE.Vector3();
const _p0 = new THREE.Vector3(), _p1 = new THREE.Vector3(), _pm = new THREE.Vector3(), _s0 = new THREE.Vector3(), _s1 = new THREE.Vector3(), _sm = new THREE.Vector3();
const _q0 = new THREE.Quaternion(), _q1 = new THREE.Quaternion(), _qm = new THREE.Quaternion(), _fm = new THREE.Matrix4();
const _bodyStep = new THREE.Vector3();

/**
 * 一张代理布。rest(j, k) 给第 j 圈（0 在下沿、rings − 1 在上沿）第 k 段的安放位置（钉住它的那个坐标系里，通常是髋）。
 * @param {{ rings: number, segs: number, rest: (j: number, k: number) => [number, number, number], pinnedRings?: number, bend?: number, thickness?: number, mass?: (j: number) => number }} options
 */
export function createCloth({ rings, segs, rest, pinnedRings = 1, bend = CLOTH.bend, thickness = CLOTH.thickness, windDrag = CLOTH.windDrag }) {
  if (!(rings >= 3) || !(segs >= 3)) throw new RangeError('cloth: at least 3 rings and 3 segments');
  const n = rings * segs;
  const restLocal = new Float32Array(n * 3);
  const pinned = new Uint8Array(n);
  for (let j = 0; j < rings; j++) {
    for (let k = 0; k < segs; k++) {
      const i = j * segs + k;
      const [x, y, z] = rest(j, k);
      restLocal[i * 3] = x; restLocal[i * 3 + 1] = y; restLocal[i * 3 + 2] = z;
      if (j >= rings - pinnedRings) pinned[i] = 1;
    }
  }
  const idx = (j, k) => j * segs + ((k + segs) % segs);
  const dist = (p, q) => Math.hypot(restLocal[p * 3] - restLocal[q * 3], restLocal[p * 3 + 1] - restLocal[q * 3 + 1], restLocal[p * 3 + 2] - restLocal[q * 3 + 2]);
  // 约束：结构（同圈相邻、上下相邻）、剪切（对角）、弯曲（隔一个）
  const constraints = [];
  const add = (p, q, k) => { if (p !== q) constraints.push({ a: p, b: q, rest: dist(p, q), k }); };
  for (let j = 0; j < rings; j++) {
    for (let k = 0; k < segs; k++) {
      add(idx(j, k), idx(j, k + 1), 1);
      if (j + 1 < rings) {
        add(idx(j, k), idx(j + 1, k), 1);
        add(idx(j, k), idx(j + 1, k + 1), 0.9);
        add(idx(j, k + 1), idx(j + 1, k), 0.9);
      }
      if (bend > 0) {
        add(idx(j, k), idx(j, k + 2), bend);
        if (j + 2 < rings) add(idx(j, k), idx(j + 2, k), bend);
      }
    }
  }
  // 系绳：每个点离它那一列最上面（钉住的）那个点的安放距离
  const tether = new Float32Array(n);
  for (let j = 0; j < rings; j++) for (let k = 0; k < segs; k++) tether[idx(j, k)] = dist(idx(j, k), idx(rings - 1, k));
  // 约束的热循环用定长数组（一帧每个人物要过两千条约束 × 6 次迭代 × 2 小步）；constraints 那一份对象留给测试读
  const cn = constraints.length;
  const cA = new Uint32Array(cn), cB = new Uint32Array(cn), cRest = new Float32Array(cn), cWa = new Float32Array(cn), cWb = new Float32Array(cn);
  constraints.forEach((c, i) => {
    const wa = pinned[c.a] ? 0 : 1, wb = pinned[c.b] ? 0 : 1, w = wa + wb;
    cA[i] = c.a; cB[i] = c.b; cRest[i] = c.rest;
    cWa[i] = w === 0 ? 0 : (wa / w) * c.k; cWb[i] = w === 0 ? 0 : (wb / w) * c.k;
  });
  // 每一列朝外的方向（钉住的坐标系里，水平）：布点正好落在身体轴上时，径向挡用它当方位
  const restDir = new Float32Array(n * 2);
  for (let i = 0; i < n; i++) {
    const x = restLocal[i * 3], z = restLocal[i * 3 + 2], len = Math.hypot(x, z);
    restDir[i * 2] = len > 1e-9 ? x / len : 0; restDir[i * 2 + 1] = len > 1e-9 ? z / len : 1;
  }
  return {
    rings, segs, n, restLocal, restDir, pinned, constraints, tether, thickness, windDrag,
    packed: { count: cn, a: cA, b: cB, rest: cRest, wa: cWa, wb: cWb },
    pos: new Float32Array(n * 3), prev: new Float32Array(n * 3), normals: new Float32Array(n * 3),
    // 每个布点上一小步结束时在身体的哪一边（钉住的坐标系里的水平方位）：径向挡按它推，不按此刻的方位——
    // 身体一下子起步、布点一步就从两小腿之间穿到了身后，此刻的方位已经朝后，按它推就把它推到了腿后面
    side: Float32Array.from(restDir),
    anchor: new THREE.Vector3(), lastFrame: new THREE.Matrix4(), settled: false,
    body: { list: [], scale: 1, count: 0 },
  };
}

/** 把布摆到安放的位置（第一帧、瞬移之后）：pos = frame × rest，速度清零。 */
export function resetCloth(cloth, frame) {
  const { n, restLocal, pos, prev } = cloth;
  cloth.side.set(cloth.restDir);
  for (let i = 0; i < n; i++) {
    _a.set(restLocal[i * 3], restLocal[i * 3 + 1], restLocal[i * 3 + 2]).applyMatrix4(frame);
    pos[i * 3] = prev[i * 3] = _a.x; pos[i * 3 + 1] = prev[i * 3 + 1] = _a.y; pos[i * 3 + 2] = prev[i * 3 + 2] = _a.z;
  }
  cloth.anchor.setFromMatrixPosition(frame);
  cloth.lastFrame.copy(frame);
  computeNormals(cloth);
  cloth.settled = true;
}

/**
 * 推进一帧。
 * @param {object} cloth
 * @param {{ dt: number, frame: THREE.Matrix4, colliders?: Array<{ a: THREE.Vector3, b?: THREE.Vector3, r: number }>, body?: Array<{ a: THREE.Vector3, b?: THREE.Vector3|null, r: number }>|null, wind?: THREE.Vector3|null, gravity?: number }} step
 *   frame：钉住的坐标系此刻的世界矩阵；colliders：球（只有 a）或胶囊（a → b），最近点推出；
 *   body：身体（腿、骨盆、髋），世界坐标、和 frame 同一刻——按径向挡，布永远在它们外面（见文件头）；wind：世界里的风（米 / 秒量级的漂移向量）；
 *   floor：脚下地面的高度（站在锻炉 0.2 米高的地坪上，下摆落在地坪上，不穿到街面）
 */
export function stepCloth(cloth, { dt, frame, colliders = [], body = null, wind = null, gravity = CLOTH.gravity, floor = CLOTH.floorY }) {
  if (!cloth.settled) resetCloth(cloth, frame);
  _a.setFromMatrixPosition(frame);
  if (_a.distanceTo(cloth.anchor) > 1.5) resetCloth(cloth, frame);   // 瞬移：重新安放，不从远处甩过来
  cloth.anchor.copy(_a);
  setBody(cloth, frame, body);
  if (!(dt > 0)) return;
  // 钉住的坐标系按步插值（位置、朝向、缩放）：30 帧拆两步时每一步钉子只挪一半，和 60 帧算出来一样
  _p0.setFromMatrixPosition(cloth.lastFrame); _q0.setFromRotationMatrix(cloth.lastFrame); _s0.setFromMatrixScale(cloth.lastFrame);
  _p1.setFromMatrixPosition(frame); _q1.setFromRotationMatrix(frame); _s1.setFromMatrixScale(frame);
  // 步数也按这一帧转了多少来定：一步最多转 CLOTH.maxTurn（2026-10-12，用户：雅典娜移动后偶发裙子剧烈抖动）。
  // 一帧里猛地转身（停步时朝向从路的方向跳到目的地的朝向，最多 180°）只拆成两步时，每一步腿的胶囊扫过 90°，
  // 布点记的"在身体哪一边"（side）还是上一步的，被径向推到错的一边，之后一秒多都在抖
  const turn = 2 * Math.acos(Math.min(1, Math.abs(_q0.dot(_q1))));
  const steps = Math.min(CLOTH.maxSteps, Math.max(1, Math.ceil(dt / CLOTH.maxDt), Math.ceil(turn / CLOTH.maxTurn)));
  const h = dt / steps;
  for (let s = 0; s < steps; s++) {
    const u = (s + 1) / steps;
    _pm.lerpVectors(_p0, _p1, u); _qm.slerpQuaternions(_q0, _q1, u); _sm.lerpVectors(_s0, _s1, u);
    _fm.compose(_pm, _qm, _sm);
    _bodyStep.subVectors(_p1, _p0).multiplyScalar(1 / steps);   // 这一小步身体挪了多少
    substep(cloth, h, _fm, colliders, wind, gravity, _bodyStep, floor);
  }
  cloth.lastFrame.copy(frame);
  computeNormals(cloth);
}

// —— 身体：换到钉住的坐标系里（腿相对髋的位置和身体在世界里走到哪无关，所以按这一帧的 frame 换一次，每个小步共用） ——
const _bodyInv = new THREE.Matrix4();
function setBody(cloth, frame, body) {
  const store = cloth.body;
  store.count = 0;
  if (!body || body.length === 0) return;
  _bodyInv.copy(frame).invert();
  _s1.setFromMatrixScale(frame);
  const scale = (_s1.x + _s1.y + _s1.z) / 3 || 1;
  store.scale = scale;
  for (const c of body) {
    let e = store.list[store.count];
    if (!e) { e = { a: new THREE.Vector3(), b: new THREE.Vector3(), capsule: false, r: 0 }; store.list[store.count] = e; }
    e.a.copy(c.a).applyMatrix4(_bodyInv);
    e.capsule = !!c.b;
    if (c.b) e.b.copy(c.b).applyMatrix4(_bodyInv);
    e.r = c.r / scale;
    store.count++;
  }
}

/**
 * 一块身体在高度 y 及以上的部分朝方位 (ux, uz) 伸出去多远（钉住的坐标系里的水平支撑函数；那里没有身体返回 -Infinity）。
 * 为什么是"及以上"而不是"这个高度的截面"：跑起来两脚离地的那一刻，下摆那个高度上一条腿也没有，只按截面挡，下摆就从两腿底下
 * 甩到身后（实测：起步就冲刺时腿外侧还有 26 % 露在外面）。布从上往下垂，抬起的膝盖、踢到身后的脚跟下面，布是像帘子一样直着
 * 垂下去的，不会钻到腿底下——所以一个布点要在它上方所有身体的外面。
 * 胶囊是一串沿轴排开的球：轴上高度 h 的那个球在 y 以上的部分朝外伸出 r（h ≥ y）或 sqrt(r² − (y − h)²)（y − r < h < y）。
 * 沿轴走，投影 L(t) 是线性的、半径那一项在球冠段是凹的，所以最大值只可能在 t = 0、t = 1、轴穿过高度 y 的地方、
 * 和球冠段的驻点（s = y − h = −k·r / sqrt(1 + k²)，k 是 L 对 h 的变化率）这几处——逐个算，取最大。
 */
export function bodySupport(e, y, ux, uz) {
  const { a, r } = e;
  if (!e.capsule) return a.y + r <= y ? -Infinity : a.x * ux + a.z * uz + reach(a.y, y, r);
  const b = e.b;
  if (a.y + r <= y && b.y + r <= y) return -Infinity;   // 整块都在这个点下面（腿对腰上的布点）
  const la = a.x * ux + a.z * uz, lb = b.x * ux + b.z * uz;
  let best = Math.max(la + reach(a.y, y, r), lb + reach(b.y, y, r));
  const dh = b.y - a.y;
  if (dh > 1e-6 || dh < -1e-6) {
    let t = (y - a.y) / dh;                   // 轴穿过高度 y
    if (t > 0 && t < 1) { const v = la + (lb - la) * t + r; if (v > best) best = v; }
    const k = (lb - la) / dh;                 // dL / dh
    const s = (-k * r) / Math.sqrt(1 + k * k);   // 球冠段的驻点（s = y − h，只在 0 < s < r 时成立）
    if (s > 0 && s < r) {
      t = (y - s - a.y) / dh;
      if (t > 0 && t < 1) { const v = la + (lb - la) * t + Math.sqrt(r * r - s * s); if (v > best) best = v; }
    }
  }
  return best;
}

/** 轴上高度 h 的那个球在 y 以上的部分朝外伸出多远（球心在 y 以下 r 之外就没有）。 */
function reach(h, y, r) {
  if (h >= y) return r;
  const d = y - h;
  return d < r ? Math.sqrt(r * r - d * d) : -Infinity;
}

const _rl = new THREE.Vector3();
/**
 * 把一个点（世界坐标，写回 arr[o..o+2]）按径向推到身体外面：换到钉住的坐标系里，沿方位 (ux, uz)——它所在的那一边——
 * 离轴的距离补到所有身体截面在这个方位上的支撑 + 厚度（所有截面支撑的最大值就是它们凸包的支撑：两腿之间的空当也算在里面，
 * 前襟不会从两小腿之间穿过去）。toLocal / toWorld 是这一刻钉住的坐标系的逆矩阵与矩阵。
 */
function pushOutsideBody(arr, o, store, toLocal, toWorld, thickness, ux, uz) {
  _rl.set(arr[o], arr[o + 1], arr[o + 2]).applyMatrix4(toLocal);
  const pad = thickness / store.scale;
  let need = -Infinity;
  for (let c = 0; c < store.count; c++) {
    const s = bodySupport(store.list[c], _rl.y, ux, uz);
    if (s > need) need = s;
  }
  if (need === -Infinity) return false;
  need += pad;
  const along = _rl.x * ux + _rl.z * uz;
  if (along >= need) return false;
  _rl.x += ux * (need - along); _rl.z += uz * (need - along);
  _rl.applyMatrix4(toWorld);
  arr[o] = _rl.x; arr[o + 1] = _rl.y; arr[o + 2] = _rl.z;
  return true;
}

const _subInv = new THREE.Matrix4();
function substep(cloth, h, frame, colliders, wind, gravity, bodyStep = null, floor = CLOTH.floorY) {
  const { n, pos, prev, restLocal, pinned, tether, normals, segs, rings, body, side } = cloth;
  if (body.count > 0) _subInv.copy(frame).invert();
  const keep = Math.pow(CLOTH.damping, h * 60);
  const follow = Math.pow(CLOTH.follow, h * 60);
  const bx = bodyStep ? bodyStep.x : 0, by = bodyStep ? bodyStep.y : 0, bz = bodyStep ? bodyStep.z : 0;
  // 钉住的点跟着坐标系走；其余按 verlet 积分
  for (let i = 0; i < n; i++) {
    const o = i * 3;
    if (pinned[i]) {
      _a.set(restLocal[o], restLocal[o + 1], restLocal[o + 2]).applyMatrix4(frame);
      prev[o] = pos[o]; prev[o + 1] = pos[o + 1]; prev[o + 2] = pos[o + 2];
      pos[o] = _a.x; pos[o + 1] = _a.y; pos[o + 2] = _a.z;
      continue;
    }
    let ax = 0, ay = -gravity, az = 0;
    if (wind) {
      // 迎风：法线和风越对着，吃的风越多（布是一面帆）；背风面也略受
      const facing = Math.abs(normals[o] * wind.x + normals[o + 1] * wind.y + normals[o + 2] * wind.z) / Math.max(1e-6, wind.length());
      const k = cloth.windDrag * (0.25 + 0.75 * facing);
      ax += wind.x * k; ay += wind.y * k; az += wind.z * k;
    }
    // 世界里的阻尼，再把相对身体的那部分速度收一收（跟着身体走）
    let vx = (pos[o] - prev[o]) * keep, vy = (pos[o + 1] - prev[o + 1]) * keep, vz = (pos[o + 2] - prev[o + 2]) * keep;
    vx = bx + (vx - bx) * follow; vy = by + (vy - by) * follow; vz = bz + (vz - bz) * follow;
    prev[o] = pos[o]; prev[o + 1] = pos[o + 1]; prev[o + 2] = pos[o + 2];
    pos[o] += vx + ax * h * h; pos[o + 1] += vy + ay * h * h; pos[o + 2] += vz + az * h * h;
  }
  const { count: cn, a: cA, b: cB, rest: cRest, wa: cWa, wb: cWb } = cloth.packed;
  for (let it = 0; it < CLOTH.iterations; it++) {
    for (let c = 0; c < cn; c++) {
      const oa = cA[c] * 3, ob = cB[c] * 3;
      const dx = pos[ob] - pos[oa], dy = pos[ob + 1] - pos[oa + 1], dz = pos[ob + 2] - pos[oa + 2];
      const d = Math.sqrt(dx * dx + dy * dy + dz * dz);
      if (d < 1e-9) continue;
      const diff = (d - cRest[c]) / d;
      const fa = cWa[c] * diff, fb = cWb[c] * diff;
      pos[oa] += dx * fa; pos[oa + 1] += dy * fa; pos[oa + 2] += dz * fa;
      pos[ob] -= dx * fb; pos[ob + 1] -= dy * fb; pos[ob + 2] -= dz * fb;
    }
    // 系绳 + 碰撞 + 地面
    for (let j = 0; j < rings; j++) {
      for (let k = 0; k < segs; k++) {
        const i = j * segs + k;
        if (pinned[i]) continue;
        const o = i * 3, top = ((rings - 1) * segs + k) * 3;
        const maxD = tether[i] * CLOTH.tetherSlack;
        const dx = pos[o] - pos[top], dy = pos[o + 1] - pos[top + 1], dz = pos[o + 2] - pos[top + 2];
        const d = Math.hypot(dx, dy, dz);
        if (d > maxD) { const f = maxD / d; pos[o] = pos[top] + dx * f; pos[o + 1] = pos[top + 1] + dy * f; pos[o + 2] = pos[top + 2] + dz * f; }
        // 最大倾角：离钉住的那一列的竖直落差不少于水平偏移 / tan(maxTilt)——超了就往下按（重力的方向），不往身体里拉：
        // 第一版把水平偏移往里收，飞起来的下摆被收到身体轴线上、再被腿和骨盆的胶囊顶上去，整条裙子卷到腰上成一团
        const drop = pos[top + 1] - pos[o + 1];
        const hLen = Math.hypot(pos[o] - pos[top], pos[o + 2] - pos[top + 2]);
        const minDrop = hLen / TAN_TILT;
        if (drop < minDrop) pos[o + 1] = pos[top + 1] - minDrop;
        for (const col of colliders) pushOut(pos, o, col, cloth.thickness);
        if (body.count > 0) pushOutsideBody(pos, o, body, _subInv, frame, cloth.thickness, side[i * 2], side[i * 2 + 1]);
        if (pos[o + 1] < floor) pos[o + 1] = floor;
      }
    }
  }
  // 这一小步之后每个布点在哪一边：绕着身体转（转身、风把衣摆吹到侧面）一步只挪一点，跟着更新；离轴太近读不准就不动它
  if (body.count > 0) {
    for (let i = 0; i < n; i++) {
      if (pinned[i]) continue;
      _rl.set(pos[i * 3], pos[i * 3 + 1], pos[i * 3 + 2]).applyMatrix4(_subInv);
      const rho = Math.hypot(_rl.x, _rl.z);
      if (rho > 0.02) { side[i * 2] = _rl.x / rho; side[i * 2 + 1] = _rl.z / rho; }
    }
  }
}

/** 把点推出球 / 胶囊（半径加上布的厚度）。 */
function pushOut(pos, o, col, thickness) {
  const r = col.r + thickness;
  _a.set(pos[o], pos[o + 1], pos[o + 2]);
  if (!col.b) {
    _b.copy(col.a);
  } else {
    // 胶囊：轴上离点最近的位置
    _c.subVectors(col.b, col.a);
    const len2 = _c.lengthSq();
    const t = len2 > 0 ? THREE.MathUtils.clamp(_d.subVectors(_a, col.a).dot(_c) / len2, 0, 1) : 0;
    _b.copy(col.a).addScaledVector(_c, t);
  }
  _d.subVectors(_a, _b);
  const dist = _d.length();
  if (dist >= r) return;
  if (dist < 1e-6) _d.set(0, 0, 1); else _d.multiplyScalar(1 / dist);
  _a.copy(_b).addScaledVector(_d, r);
  pos[o] = _a.x; pos[o + 1] = _a.y; pos[o + 2] = _a.z;
}

/** 每个代理点的法线（相邻四个点的叉积，面积加权），细网格的浮雕和风都用它。 */
export function computeNormals(cloth) {
  const { rings, segs, pos, normals } = cloth;
  const at = (j, k, v) => { const o = (j * segs + ((k + segs) % segs)) * 3; return v.set(pos[o], pos[o + 1], pos[o + 2]); };
  for (let j = 0; j < rings; j++) {
    for (let k = 0; k < segs; k++) {
      at(j, k + 1, _a).sub(at(j, k - 1, _b));                       // 沿圈
      at(Math.min(rings - 1, j + 1), k, _c).sub(at(Math.max(0, j - 1), k, _d));   // 沿列
      _n.crossVectors(_a, _c);
      if (_n.lengthSq() < 1e-14) _n.set(0, 0, 1); else _n.normalize();
      const o = (j * segs + k) * 3;
      normals[o] = _n.x; normals[o + 1] = _n.y; normals[o + 2] = _n.z;
    }
  }
}

/** 代理点 (j, k) 此刻的世界位置。 */
export function clothPoint(cloth, j, k, target = new THREE.Vector3()) {
  const o = (j * cloth.segs + ((k + cloth.segs) % cloth.segs)) * 3;
  return target.set(cloth.pos[o], cloth.pos[o + 1], cloth.pos[o + 2]);
}

/**
 * 细网格到代理的映射：每个细顶点落在代理的哪一小片上（由它在衣料上的参数 t ∈ [0, 1]、θ 决定），以及它相对那一小片切线坐标系的偏移。
 * @param {Float32Array|ArrayLike<number>} finePositions 细网格的安放位置（和代理的 rest 同一个坐标系）
 * @param {(i: number) => { t: number, theta: number }} param 第 i 个细顶点在衣料上的参数：t 从下沿 0 到上沿 1，theta 弧度（和代理的段一致：第 k 段在 2πk / segs）
 */
export function bindFine(cloth, finePositions, param) {
  const count = finePositions.length / 3;
  const cell = new Uint32Array(count * 2);     // j0, k0
  const frac = new Float32Array(count * 2);    // fu（沿列）、fv（沿圈）
  const offset = new Float32Array(count * 3);  // 切线坐标系里的偏移：沿圈切线、沿列切线、法线
  const dir = new Float32Array(count * 2);     // 安放时朝外的方位（径向挡在轴上时用）
  for (let i = 0; i < count; i++) {
    const x = finePositions[i * 3], z = finePositions[i * 3 + 2], len = Math.hypot(x, z);
    dir[i * 2] = len > 1e-9 ? x / len : 0; dir[i * 2 + 1] = len > 1e-9 ? z / len : 1;
  }
  const frame = frameAt;
  const u = new THREE.Vector3(), v = new THREE.Vector3(), nn = new THREE.Vector3(), p = new THREE.Vector3(), q = new THREE.Vector3();
  const restCloth = { rings: cloth.rings, segs: cloth.segs, pos: cloth.restLocal };
  for (let i = 0; i < count; i++) {
    const { t, theta } = param(i);
    const uu = THREE.MathUtils.clamp(t, 0, 1) * (cloth.rings - 1);
    const j0 = Math.min(cloth.rings - 2, Math.floor(uu)), fu = uu - j0;
    const vv = ((theta % (Math.PI * 2)) + Math.PI * 2) % (Math.PI * 2) / (Math.PI * 2) * cloth.segs;
    const k0 = Math.floor(vv) % cloth.segs, fv = vv - Math.floor(vv);
    cell[i * 2] = j0; cell[i * 2 + 1] = k0; frac[i * 2] = fu; frac[i * 2 + 1] = fv;
    frame(restCloth, j0, k0, fu, fv, p, u, v, nn);
    q.set(finePositions[i * 3], finePositions[i * 3 + 1], finePositions[i * 3 + 2]).sub(p);
    // 切线坐标系未必正交：解一个 3×3（u, v, n 为基）
    const m = new THREE.Matrix3().set(u.x, v.x, nn.x, u.y, v.y, nn.y, u.z, v.z, nn.z).invert();
    q.applyMatrix3(m);
    offset[i * 3] = q.x; offset[i * 3 + 1] = q.y; offset[i * 3 + 2] = q.z;
  }
  return { count, cell, frac, offset, dir };
}

/** 代理上 (j0 + fu, k0 + fv) 处的点与切线坐标系（双线性）。 */
function frameAt(cloth, j0, k0, fu, fv, p, u, v, n) {
  const { segs } = cloth;
  const P = (j, k, out) => { const o = (j * segs + ((k + segs) % segs)) * 3; return out.set(cloth.pos[o], cloth.pos[o + 1], cloth.pos[o + 2]); };
  const p00 = P(j0, k0, _a), p01 = P(j0, k0 + 1, _b), p10 = P(j0 + 1, k0, _c), p11 = P(j0 + 1, k0 + 1, _d);
  // 沿圈的切线（v 方向）与沿列的切线（u 方向）
  u.copy(p10).sub(p00).multiplyScalar(1 - fv).add(_n.copy(p11).sub(p01).multiplyScalar(fv));
  v.copy(p01).sub(p00).multiplyScalar(1 - fu).add(_n.copy(p11).sub(p10).multiplyScalar(fu));
  p.copy(p00).multiplyScalar((1 - fu) * (1 - fv)).add(_n.copy(p01).multiplyScalar((1 - fu) * fv)).add(_n.copy(p10).multiplyScalar(fu * (1 - fv))).add(_n.copy(p11).multiplyScalar(fu * fv));
  n.crossVectors(v, u);
  if (n.lengthSq() < 1e-14) n.set(0, 0, 1); else n.normalize();
  // 用单位切线：偏移是米
  if (u.lengthSq() > 1e-14) u.normalize(); else u.set(0, 1, 0);
  if (v.lengthSq() > 1e-14) v.normalize(); else v.set(1, 0, 0);
}

/**
 * 按代理此刻的位置重建细网格（写进 out，世界坐标）。colliders：细网格的顶点也逐个推出碰撞体——代理的格子比腿粗
 * （24 段时下沿两格之间 6.5 厘米，小腿 12 厘米宽），两个都在腿外的代理点之间拉直的布会从腿中间穿过去；
 * 细顶点自己再推一次，裙摆才真的裹在腿外面（cloth.test.mjs 对每个细顶点断言）。
 * frame：上一次 stepCloth 的钉住坐标系；给了它，细顶点也按径向挡在身体（stepCloth 的 body）外面——褶的谷比代理面深两三厘米，
 * 贴着腿的地方谷底会陷进腿里。
 */
const _fineInv = new THREE.Matrix4();
export function applyFine(cloth, binding, out, colliders = null, frame = null) {
  const { count, cell, frac, offset, dir } = binding;
  const u = new THREE.Vector3(), v = new THREE.Vector3(), nn = new THREE.Vector3(), p = new THREE.Vector3();
  const radial = frame && cloth.body.count > 0;
  if (radial) _fineInv.copy(frame).invert();
  for (let i = 0; i < count; i++) {
    frameAt(cloth, cell[i * 2], cell[i * 2 + 1], frac[i * 2], frac[i * 2 + 1], p, u, v, nn);
    const ox = offset[i * 3], oy = offset[i * 3 + 1], oz = offset[i * 3 + 2];
    out[i * 3] = p.x + u.x * ox + v.x * oy + nn.x * oz;
    out[i * 3 + 1] = p.y + u.y * ox + v.y * oy + nn.y * oz;
    out[i * 3 + 2] = p.z + u.z * ox + v.z * oy + nn.z * oz;
    if (colliders) for (const col of colliders) pushOut(out, i * 3, col, cloth.thickness * 0.5);
    if (radial) {
      // 细顶点在身体的哪一边：取它那一小片代理四个角的那一边（双线性），读不出来就用安放时的方位
      const j0 = cell[i * 2], k0 = cell[i * 2 + 1], fu = frac[i * 2], fv = frac[i * 2 + 1], S = cloth.segs, side = cloth.side;
      const c00 = (j0 * S + k0) * 2, c01 = (j0 * S + ((k0 + 1) % S)) * 2, c10 = ((j0 + 1) * S + k0) * 2, c11 = ((j0 + 1) * S + ((k0 + 1) % S)) * 2;
      let ux = (1 - fu) * ((1 - fv) * side[c00] + fv * side[c01]) + fu * ((1 - fv) * side[c10] + fv * side[c11]);
      let uz = (1 - fu) * ((1 - fv) * side[c00 + 1] + fv * side[c01 + 1]) + fu * ((1 - fv) * side[c10 + 1] + fv * side[c11 + 1]);
      const len = Math.hypot(ux, uz);
      if (len > 0.2) { ux /= len; uz /= len; } else { ux = dir[i * 2]; uz = dir[i * 2 + 1]; }
      pushOutsideBody(out, i * 3, cloth.body, _fineInv, frame, cloth.thickness * 0.5, ux, uz);
    }
  }
  return out;
}

/** 一个点离某个碰撞体表面多远（负数在里面；测试用）。 */
export function signedDistance(point, col) {
  if (!col.b) return point.distanceTo(col.a) - col.r;
  _c.subVectors(col.b, col.a);
  const len2 = _c.lengthSq();
  const t = len2 > 0 ? THREE.MathUtils.clamp(_d.subVectors(point, col.a).dot(_c) / len2, 0, 1) : 0;
  _b.copy(col.a).addScaledVector(_c, t);
  return point.distanceTo(_b) - col.r;
}
