// 弹簧骨（M3 决定 2，A 档）：衣摆、面纱、辫子、围裙、斗篷各挂几节骨头，跟着身体的运动滞后、晃、回摆，再被重力和风拉着——
// 风格化游戏做衣物飘动的那一套（VRM SpringBone 一类），确定性、便宜（全城十几个人几百个节点），Node 单测覆盖。
// 不是布料模拟：没有逐顶点的约束，也不会和自己相交；每条链是一串定长的节，末端按 verlet 积分，受四样东西：
//   惯性（上一帧的位移 × (1 − drag)）、刚度（拉回安放时的方向）、重力、风；再被几个球形碰撞体推开（腿、髋）。
// 最大偏角 maxAngle 把快速移动时的飘拦住：没有它斗篷会平平地飘在身后。
import * as THREE from '../../vendor/three.bundle.mjs';

/** 一节骨头的默认参数。 */
export const SPRING = Object.freeze({
  stiffness: 1.6,   // 每秒拉回安放方向多少节长（VRM 的 stiffnessForce：速度量级，不是加速度）
  drag: 0.32,       // 60 帧时每帧吃掉多少速度（别的帧率按秒折算，30 帧和 60 帧手感一样）
  gravity: 0.9,     // 向下的漂移速度（米 / 秒）：布被拖着往下，不是铁块在自由落体
  maxAngle: 0.95,   // 离安放方向最多偏多少（弧度）
  inertia: 0.45,    // 身体的位移有多少传给布（0 整个跟着身体走、1 布留在原地）：玩家走 6 米 / 秒，
                    //   全传的话斗篷永远顶在最大偏角上平飘；传一半，走是走、跑是跑
  maxDt: 1 / 60,    // 一步最多积分多久：更长的帧拆成几步（30 帧拆两步，锚点按步插值），30 帧和 60 帧算出来一样
});

const _dir = new THREE.Vector3(), _next = new THREE.Vector3(), _tmp = new THREE.Vector3(), _rest = new THREE.Vector3();
const _delta = new THREE.Vector3(), _from = new THREE.Vector3(), _anchorAt = new THREE.Vector3();

/**
 * 一条链：从锚点垂下 segments 节，每节 segmentLength 米。
 * @param {{ segments: number, segmentLength: number, stiffness?: number, drag?: number, gravity?: number, maxAngle?: number, radius?: number, wind?: number }} options
 *   radius：节点自己的半径（碰撞用）；wind：吃风的程度（面纱 > 衣摆 > 辫子）
 */
export function createChain({ segments, segmentLength, stiffness = SPRING.stiffness, drag = SPRING.drag, gravity = SPRING.gravity, maxAngle = SPRING.maxAngle, inertia = SPRING.inertia, radius = 0.02, wind = 1 }) {
  if (!(segments >= 1) || !(segmentLength > 0)) throw new RangeError('springs: a chain needs at least one segment of positive length');
  return {
    segments, segmentLength, stiffness, drag, gravity, maxAngle, inertia, radius, wind,
    nodes: Array.from({ length: segments }, () => ({ pos: new THREE.Vector3(), prev: new THREE.Vector3() })),
    anchor: new THREE.Vector3(),
    settled: false,
  };
}

/** 把链摆到安放的位置（第一帧、瞬移之后）：从锚点沿 restDir 一节一节排开，速度清零。 */
export function settleChain(chain, anchor, restDir) {
  for (let i = 0; i < chain.nodes.length; i++) {
    const n = chain.nodes[i];
    n.pos.copy(anchor).addScaledVector(restDir, chain.segmentLength * (i + 1));
    n.prev.copy(n.pos);
  }
  chain.anchor.copy(anchor);
  chain.settled = true;
}

/**
 * 推进一帧。
 * @param {object} chain createChain 的结果
 * @param {{ dt: number, anchor: THREE.Vector3, restDir: THREE.Vector3, wind?: THREE.Vector3|null, colliders?: Array<{ center: THREE.Vector3, radius: number }> }} frame
 *   dt 秒；anchor、restDir（单位向量）、wind（米 / 秒²）、colliders 都在同一个坐标系里（世界）
 */
export function stepChain(chain, { dt, anchor, restDir, wind = null, colliders = [] }) {
  if (!chain.settled) settleChain(chain, anchor, restDir);
  if (!(dt > 0)) return;
  const steps = Math.min(8, Math.max(1, Math.ceil(dt / SPRING.maxDt)));
  const h = dt / steps;
  // 身体这一帧挪了 delta：按步插值锚点；每一步只把 (1 − inertia) 那一份位移直接带给布（布跟着人走），其余留给惯性去追
  _delta.subVectors(anchor, chain.anchor);
  const from = _from.copy(chain.anchor);
  for (let s = 0; s < steps; s++) {
    _tmp.copy(_delta).multiplyScalar((1 - chain.inertia) / steps);
    for (const n of chain.nodes) { n.pos.add(_tmp); n.prev.add(_tmp); }
    _anchorAt.copy(from).addScaledVector(_delta, (s + 1) / steps);
    substep(chain, h, _anchorAt, restDir, wind, colliders);
  }
  chain.anchor.copy(anchor);
}

function substep(chain, h, anchor, restDir, wind, colliders) {
  const { nodes, segmentLength: L } = chain;
  for (let i = 0; i < nodes.length; i++) {
    const n = nodes[i];
    const parent = i === 0 ? anchor : nodes[i - 1].pos;
    // 惯性 + 刚度 + 重力 + 风（后三样都是速度量级 × h，和 VRM 一样；drag 按 60 帧定义、按秒折算）
    const keep = Math.pow(1 - chain.drag, h * 60);
    _next.copy(n.pos).add(_tmp.subVectors(n.pos, n.prev).multiplyScalar(keep));
    _next.addScaledVector(restDir, chain.stiffness * L * h);
    _next.y -= chain.gravity * h;
    if (wind) _next.addScaledVector(wind, chain.wind * h);
    // 定长
    _dir.subVectors(_next, parent);
    if (_dir.lengthSq() < 1e-12) _dir.copy(restDir);
    _dir.normalize();
    // 最大偏角：和安放方向夹角超了就往回转
    const cos = THREE.MathUtils.clamp(_dir.dot(restDir), -1, 1);
    const angle = Math.acos(cos);
    if (angle > chain.maxAngle) {
      _rest.copy(restDir);
      const axis = _tmp.crossVectors(_rest, _dir);
      if (axis.lengthSq() > 1e-12) {
        axis.normalize();
        _dir.copy(_rest).applyAxisAngle(axis, chain.maxAngle);
      } else {
        _dir.copy(_rest);
      }
    }
    _next.copy(parent).addScaledVector(_dir, L);
    // 碰撞体：推出去，再定长（节点正好落在球心上时沿着和这一节垂直的方向推）
    for (const c of colliders) {
      _tmp.subVectors(_next, c.center);
      const r = c.radius + chain.radius;
      const d = _tmp.length();
      if (d >= r) continue;
      if (d > 1e-6) _tmp.multiplyScalar(1 / d);
      else { _tmp.set(0, 1, 0).cross(_dir); if (_tmp.lengthSq() < 1e-6) _tmp.set(1, 0, 0).cross(_dir); _tmp.normalize(); }
      _next.copy(c.center).addScaledVector(_tmp, r);
      _dir.subVectors(_next, parent).normalize();
      _next.copy(parent).addScaledVector(_dir, L);
    }
    n.prev.copy(n.pos);
    n.pos.copy(_next);
  }
}

/** 第 i 节此刻的方向（单位向量，世界坐标）：从它的父节点指向它。 */
export function segmentDirection(chain, i, anchor, target = new THREE.Vector3()) {
  const parent = i === 0 ? anchor : chain.nodes[i - 1].pos;
  return target.subVectors(chain.nodes[i].pos, parent).normalize();
}

/** 链末端离安放位置多远（米）：测试用它看"动起来了"和"停下来了"。 */
export function deflection(chain, anchor, restDir) {
  const tip = chain.nodes[chain.nodes.length - 1].pos;
  const rest = _tmp.copy(anchor).addScaledVector(restDir, chain.segmentLength * chain.nodes.length);
  return tip.distanceTo(rest);
}

/**
 * 风：从海上吹来的一阵一阵的风（设计稿第 8 节的"通透的地中海空气"）。方向固定从南（+z 是海）吹向北，
 * 强度按时间慢慢起伏，偶尔一阵大的；返回米 / 秒量级的漂移向量，供弹簧骨直接加上（和重力同一量纲）。
 */
export function windAt(ms, target = new THREE.Vector3()) {
  const t = ms / 1000;
  const slow = 0.5 + 0.5 * Math.sin(t * 0.21) * Math.sin(t * 0.073 + 1.3);
  const gust = Math.max(0, Math.sin(t * 0.9) * Math.sin(t * 0.37 + 0.6)) ** 3;
  const strength = 0.35 * slow + 1.1 * gust;
  return target.set(0.18 * Math.sin(t * 0.5), 0, -1).normalize().multiplyScalar(strength);
}
