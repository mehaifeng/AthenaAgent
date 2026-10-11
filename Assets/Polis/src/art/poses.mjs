// 人物的姿势（纯逻辑，不碰 three.js，Node 单测覆盖）。动作只有四种（设计稿 M2 行）：走、停、做事、交付。
// 每个函数按"动作开始后过了多久"给出一组关节角——骨架（rig.mjs）只负责把角度摆上去。这样动作可以在 Node 里断言
// （周期、左右对称、角度有界、交付首尾回到站姿），截图审查看到的就是测试测的那一套。
//
// 约定：人物正面朝 +z，y 向上。关节绕 x 轴转正角 = 上端朝前倾（脊柱前倾、点头）；手臂与腿垂向 −y，
// 所以"向前抬"是负角（肩 / 胯 rx < 0），膝盖向后弯是正角。rz 是外展（左正右负，左右镜像）。单位：弧度、米、毫秒。

export const ACTIONS = Object.freeze(['walk', 'idle', 'work', 'deliver']);

/** 做事的几种样子：按工具类别（雅典娜）或居民的行当选。 */
export const WORK_VARIANTS = Object.freeze([
  'read', 'write', 'hammer', 'summon', 'haul', 'paint', 'announce', 'shelve', 'count', 'survey', 'carve', 'tend', 'await',
]);

const JOINTS = ['hips', 'spine', 'head', 'shoulderL', 'shoulderR', 'elbowL', 'elbowR', 'handL', 'handR', 'legL', 'legR', 'kneeL', 'kneeR', 'skirt'];
export const JOINT_NAMES = Object.freeze(JOINTS);

/** 站姿：所有关节归零，手臂自然垂下、略离身体。 */
export function restPose() {
  return {
    hips: { x: 0, y: 0, z: 0, rx: 0, ry: 0, rz: 0 },
    spine: { rx: 0, ry: 0, rz: 0 },
    head: { rx: 0, ry: 0, rz: 0 },
    shoulderL: { rx: 0, ry: 0, rz: 0.06 },
    shoulderR: { rx: 0, ry: 0, rz: -0.06 },
    elbowL: { rx: -0.1 },
    elbowR: { rx: -0.1 },
    handL: { rx: 0, rz: 0 },
    handR: { rx: 0, rz: 0 },
    legL: { rx: 0, rz: 0 },
    legR: { rx: 0, rz: 0 },
    kneeL: { rx: 0 },
    kneeR: { rx: 0 },
    skirt: { rx: 0, rz: 0, sz: 1 },
    propR: null,
    propL: null,
  };
}

const TAU = Math.PI * 2;
const clamp01 = (x) => Math.min(1, Math.max(0, x));
const smooth = (x) => { const t = clamp01(x); return t * t * (3 - 2 * t); };
const lerp = (a, b, k) => a + (b - a) * k;
/** 分段关键帧：keys = [[f0, v0], [f1, v1], …]，段内平滑插值。 */
function keyed(keys, f) {
  if (f <= keys[0][0]) return keys[0][1];
  for (let i = 1; i < keys.length; i++) {
    const [f1, v1] = keys[i];
    const [f0, v0] = keys[i - 1];
    if (f <= f1) return lerp(v0, v1, smooth((f - f0) / Math.max(1e-6, f1 - f0)));
  }
  return keys[keys.length - 1][1];
}

/**
 * 手腕补回整条手臂（连脊柱）在 x 轴上转过的角度：手的坐标轴回到和身体一样竖直，握着的长杖才是竖的。
 * 前臂水平时，肩的外展（rz）只是让杖绕竖轴转一下，不会让它歪，所以只补 rx。
 */
export function upright(p, side) {
  return -(p.spine.rx + p[`shoulder${side}`].rx + p[`elbow${side}`].rx);
}

/**
 * 走和停的时候手里拿着家什（居民的 spec.carry）：
 *   staff — 长杖（测杆、权杖）：前臂平端、手腕补正，杖竖着；这只手不随步子摆；
 *   hug   — 石板、算板、卷轴、调色板：前臂收在胸前抱着。
 */
export const CARRY_GRIPS = Object.freeze(['staff', 'hug']);
function applyCarry(p, carry) {
  if (!carry) return p;
  const side = carry.side === 'L' ? 'L' : 'R';
  const sx = side === 'L' ? 1 : -1;
  if (carry.grip === 'staff') {
    p[`shoulder${side}`].rx = -0.3; p[`shoulder${side}`].rz = 0.12 * sx;
    p[`elbow${side}`].rx = -1.2;
    p[`hand${side}`].rx = upright(p, side);
  } else if (carry.grip === 'hug') {
    p[`shoulder${side}`].rx = -0.38; p[`shoulder${side}`].rz = -0.06 * sx;
    p[`elbow${side}`].rx = -1.55;
  } else {
    throw new Error(`poses: unknown carry grip '${carry.grip}'`);
  }
  p[`prop${side}`] = carry.prop;
  return p;
}

/** 走路一个完整周期（左右各迈一步）的时长：步幅 / 速度。 */
export function walkPeriod({ stride = 1.3, speed = 1.3 } = {}) {
  return (1000 * 2 * stride) / Math.max(0.2, speed);
}

/**
 * 走：腿前后摆、回摆时屈膝；髋一个周期起伏两次（两腿并拢时最高）；手臂与同侧腿反向摆；身体略前倾、肩髋反向扭。
 * 长袍下看不见腿，读得出"在走"靠的是起伏、摆臂和下摆的晃动（裙摆随步幅前后一伸一缩）。
 */
function walk(t, style) {
  const p = restPose();
  const P = style.period ?? walkPeriod(style);
  const ph = ((t % P) / P) * TAU;
  const s = Math.sin(ph);
  const swing = style.legSwing ?? 0.42;
  p.legL.rx = -swing * s;
  p.legR.rx = swing * s;
  // 摆动腿往前收时屈膝：左腿在 sin 从正转负（向前摆）的半周屈膝
  p.kneeL.rx = 0.62 * Math.max(0, Math.cos(ph)) * Math.max(0, s + 0.3);
  p.kneeR.rx = 0.62 * Math.max(0, -Math.cos(ph)) * Math.max(0, -s + 0.3);
  p.hips.y = 0.018 * Math.cos(2 * ph) - 0.006;
  p.hips.ry = 0.07 * s;
  p.spine.ry = -0.09 * s;
  p.spine.rx = 0.06;
  p.head.ry = 0.04 * s;
  const arm = style.armSwing ?? 0.36;
  p.shoulderL.rx = arm * s;
  p.shoulderR.rx = -arm * s;
  p.elbowL.rx = -0.22 - 0.16 * Math.max(0, -s);
  p.elbowR.rx = -0.22 - 0.16 * Math.max(0, s);
  p.skirt.rx = 0.035 * Math.sin(ph + 0.6);
  p.skirt.sz = 1 + 0.07 * Math.abs(s);
  return p;
}

/**
 * 雅典娜的"走"：神不迈步，沿街滑行（M0 定的 12 米 / 秒）——身体前倾、衣摆向后拖、两臂微张向后，轻轻浮动。
 * M2 美术审查：第一版前倾 0.08、衣摆 0.1，游戏视距里和站着分不出来；现在前倾与拖摆都加倍，头抬起来看前方
 */
function glide(t) {
  const p = restPose();
  p.hips.y = 0.014 * Math.sin((t / 700) * TAU) + 0.02;
  p.hips.z = -0.02;
  p.spine.rx = 0.17;
  p.head.rx = -0.12;
  p.shoulderL.rx = 0.4; p.shoulderL.rz = 0.28;
  p.shoulderR.rx = 0.4; p.shoulderR.rz = -0.28;
  p.elbowL.rx = -0.22; p.elbowR.rx = -0.22;
  p.skirt.rx = 0.24 + 0.03 * Math.sin((t / 260) * TAU);
  p.skirt.sz = 1.1;
  return p;
}

/** 停：呼吸、重心左右换、头偶尔转一转；手臂自然垂下。 */
function idle(t) {
  const p = restPose();
  p.spine.rx = 0.012 * Math.sin((t / 4200) * TAU);
  p.hips.x = 0.012 * Math.sin((t / 7000) * TAU);
  p.hips.rz = 0.02 * Math.sin((t / 7000) * TAU);
  p.head.ry = 0.28 * Math.sin((t / 9000) * TAU) * Math.abs(Math.sin((t / 9000) * TAU));
  p.shoulderL.rz = 0.07 + 0.01 * Math.sin((t / 4200) * TAU);
  p.shoulderR.rz = -0.07 - 0.01 * Math.sin((t / 4200) * TAU);
  p.elbowL.rx = -0.14; p.elbowR.rx = -0.14;
  return p;
}

/** 做事：按样子各有一个循环。返回的 propL / propR 是手里该出现的小件（骨架按名字显隐）。 */
function work(t, style) {
  const p = restPose();
  const v = style.variant ?? 'read';
  const w = (period) => ((t % period) / period) * TAU;
  switch (v) {
    case 'read':   // 双手捧着展开的卷轴，低头读
      p.shoulderL.rx = -0.92; p.shoulderL.rz = -0.22;
      p.shoulderR.rx = -0.92; p.shoulderR.rz = 0.22;
      p.elbowL.rx = -1.15; p.elbowR.rx = -1.15;
      p.head.rx = 0.36 + 0.04 * Math.sin(w(3200));
      p.spine.rx = 0.08;
      p.propR = 'scrollOpen';
      break;
    case 'write':  // 左手托石板，右手执笔，一笔一笔
      p.shoulderL.rx = -0.75; p.shoulderL.rz = -0.12; p.elbowL.rx = -1.45;
      p.shoulderR.rx = -0.68 + 0.05 * Math.sin(w(420)); p.shoulderR.rz = 0.18; p.elbowR.rx = -1.25 + 0.08 * Math.sin(w(210));
      p.head.rx = 0.42;
      p.spine.rx = 0.1;
      p.propL = 'tablet'; p.propR = 'stylus';
      break;
    case 'hammer': {  // 举锤、落锤、停一拍
      const f = (t % 900) / 900;
      p.shoulderR.rx = keyed([[0, -0.45], [0.55, -2.55], [0.68, -0.4], [1, -0.45]], f);
      p.elbowR.rx = keyed([[0, -1.0], [0.55, -0.5], [0.68, -0.25], [1, -1.0]], f);
      p.shoulderL.rx = -0.62; p.shoulderL.rz = -0.1; p.elbowL.rx = -0.95;
      p.spine.rx = keyed([[0, 0.12], [0.55, 0.02], [0.68, 0.24], [1, 0.12]], f);
      p.head.rx = 0.3;
      p.propR = 'hammer';
      break;
    }
    case 'summon': {  // 唤出侍女：两臂向两侧抬起，掌心向上，抬头
      const k = smooth(t / 900);
      p.shoulderL.rz = lerp(0.06, 1.25, k) + 0.05 * Math.sin(w(2400)); p.shoulderL.rx = -0.35 * k;
      p.shoulderR.rz = lerp(-0.06, -1.25, k) - 0.05 * Math.sin(w(2400)); p.shoulderR.rx = -0.35 * k;
      p.elbowL.rx = -0.25; p.elbowR.rx = -0.25;
      p.head.rx = -0.2 * k;
      p.spine.rx = -0.05 * k;
      break;
    }
    case 'haul': {  // 水手收缆：两手交替往回拉，身子后仰；缆从右手往前斜下（手腕补正，缆的方向和地面对齐）
      const s = Math.sin(w(1100));
      p.shoulderL.rx = -1.15 + 0.45 * s; p.elbowL.rx = -0.6 - 0.5 * Math.max(0, s);
      p.shoulderR.rx = -1.15 - 0.45 * s; p.elbowR.rx = -0.6 - 0.5 * Math.max(0, -s);
      p.shoulderL.rz = -0.1; p.shoulderR.rz = 0.1;
      p.spine.rx = -0.12 + 0.05 * s;
      p.hips.z = -0.03;
      p.handR.rx = upright(p, 'R');
      p.propR = 'rope';
      break;
    }
    case 'paint':  // 右臂伸向画板小幅运笔，左手托调色板
      p.shoulderR.rx = -1.12; p.shoulderR.rz = 0.1 + 0.07 * Math.sin(w(520)); p.elbowR.rx = -0.45 + 0.1 * Math.sin(w(1040));
      p.shoulderL.rx = -0.55; p.shoulderL.rz = 0.25; p.elbowL.rx = -1.35;
      p.head.rx = 0.12; p.head.ry = -0.1;
      p.propR = 'brush'; p.propL = 'palette';
      break;
    case 'announce':  // 传令官：右手立着权杖（手腕补正，杖是竖的），左手扬起比划，头左右看
      p.shoulderR.rx = -0.32; p.shoulderR.rz = -0.1; p.elbowR.rx = -1.25;
      p.shoulderL.rx = -1.35 + 0.28 * Math.sin(w(1400)); p.shoulderL.rz = 0.35; p.elbowL.rx = -0.55;
      p.head.ry = 0.3 * Math.sin(w(4000)); p.head.rx = -0.06;
      p.handR.rx = upright(p, 'R');
      p.propR = 'staff';
      break;
    case 'shelve':  // 图书管理员：伸手把卷轴放上高处的格子
      p.shoulderR.rx = -2.35 + 0.12 * Math.sin(w(1800)); p.shoulderR.rz = -0.05; p.elbowR.rx = -0.35;
      p.shoulderL.rx = -0.5; p.elbowL.rx = -1.2;
      p.spine.rx = -0.1; p.head.rx = -0.32;
      p.propR = 'scroll';
      break;
    case 'count':  // 账房：左手托算板，右手指一下一下拨
      p.shoulderL.rx = -0.8; p.shoulderL.rz = -0.15; p.elbowL.rx = -1.35;
      p.shoulderR.rx = -0.82; p.shoulderR.rz = 0.15; p.elbowR.rx = -1.3 + 0.14 * Math.abs(Math.sin(w(600)));
      p.head.rx = 0.4;
      p.propL = 'abacus';
      break;
    case 'survey':  // 测绘员：右手立着测杆（手腕补正，杆是竖的、杖脚落地），左手搭在额前远望，头慢慢扫视
      p.shoulderR.rx = -0.3; p.shoulderR.rz = -0.12; p.elbowR.rx = -1.2;
      p.shoulderL.rx = -2.1; p.shoulderL.rz = 0.35; p.elbowL.rx = -2.0;
      p.head.ry = 0.45 * Math.sin(w(5200)); p.head.rx = -0.05;
      p.handR.rx = upright(p, 'R');
      p.propR = 'rod';
      break;
    case 'carve': {  // 工匠：左手扶凿，右手持槌一下一下敲
      const f = (t % 600) / 600;
      p.shoulderL.rx = -0.95; p.shoulderL.rz = -0.2; p.elbowL.rx = -0.95;
      p.shoulderR.rx = keyed([[0, -1.0], [0.6, -1.75], [0.75, -0.95], [1, -1.0]], f); p.elbowR.rx = -1.0;
      p.head.rx = 0.45; p.spine.rx = 0.16;
      p.propL = 'chisel'; p.propR = 'mallet';
      break;
    }
    case 'tend':  // 女祭司：双手捧着祭碗伸向火盆，缓缓躬身
      p.shoulderL.rx = -0.72; p.shoulderL.rz = -0.18; p.elbowL.rx = -0.62;
      p.shoulderR.rx = -0.72; p.shoulderR.rz = 0.18; p.elbowR.rx = -0.62;
      p.spine.rx = 0.1 + 0.08 * Math.sin(w(3600)); p.head.rx = 0.25;
      p.propR = 'phiale';
      break;
    case 'await':  // 等审批：停在门槛前，双手把封着红印的石板捧在胸前，静静等（设计稿 6.1）
      p.shoulderL.rx = -0.62; p.shoulderL.rz = -0.12; p.elbowL.rx = -1.3;
      p.shoulderR.rx = -0.5; p.shoulderR.rz = 0.1; p.elbowR.rx = -1.0;
      p.head.rx = 0.12 + 0.03 * Math.sin(w(5200));
      p.spine.rx = 0.012 * Math.sin(w(4200));
      p.propL = 'sealed';
      break;
    default:
      throw new Error(`poses: unknown work variant '${v}'`);
  }
  return p;
}

/**
 * 交付：俯身、双手把成果递到前面，屈膝放到地上，松手，起身（设计稿 6.1"走到你面前，递上成果"）。
 * duration 是这一步的时长；0–0.3 递出、0.3–0.6 放下、0.6–0.7 松手、0.7–1 起身。成果在 0.6 之前在手里。
 */
function deliver(t, style) {
  const p = restPose();
  const f = clamp01(t / Math.max(300, style.duration ?? 1800));
  p.spine.rx = keyed([[0, 0], [0.3, 0.28], [0.6, 0.55], [0.72, 0.5], [1, 0]], f);
  p.hips.y = keyed([[0, 0], [0.3, -0.02], [0.6, -0.17], [0.72, -0.15], [1, 0]], f);
  p.hips.z = keyed([[0, 0], [0.6, -0.06], [1, 0]], f);
  p.legL.rx = keyed([[0, 0], [0.3, -0.12], [0.6, -0.55], [0.72, -0.5], [1, 0]], f);
  p.legR.rx = keyed([[0, 0], [0.3, -0.02], [0.6, -0.2], [0.72, -0.18], [1, 0]], f);
  p.kneeL.rx = keyed([[0, 0], [0.3, 0.2], [0.6, 1.05], [0.72, 0.98], [1, 0]], f);
  p.kneeR.rx = keyed([[0, 0], [0.3, 0.1], [0.6, 0.6], [0.72, 0.55], [1, 0]], f);
  const arm = keyed([[0, 0], [0.3, -1.25], [0.6, -0.8], [0.7, -0.62], [1, 0]], f);
  p.shoulderL.rx = arm; p.shoulderR.rx = arm;
  p.shoulderL.rz = keyed([[0, 0.06], [0.3, -0.16], [0.6, -0.12], [1, 0.06]], f);
  p.shoulderR.rz = -p.shoulderL.rz;
  p.elbowL.rx = keyed([[0, -0.1], [0.3, -0.32], [0.6, -0.12], [1, -0.1]], f);
  p.elbowR.rx = p.elbowL.rx;
  p.head.rx = keyed([[0, 0], [0.3, 0.1], [0.6, 0.18], [1, 0]], f);
  p.skirt.rx = keyed([[0, 0], [0.6, -0.12], [1, 0]], f);
  p.propR = f < 0.6 ? 'gift' : null;
  return p;
}

/**
 * 某个动作在 t 毫秒时的姿势。
 * @param {'walk'|'idle'|'work'|'deliver'} action
 * @param {number} t 动作开始后过了多久（毫秒）
 * @param {{ variant?: string, duration?: number, glide?: boolean, stride?: number, speed?: number, period?: number, legSwing?: number, armSwing?: number, carry?: { side: 'L'|'R', grip: 'staff'|'hug', prop: string } | null }} [style]
 */
export function poseAt(action, t, style = {}) {
  if (!Number.isFinite(t)) throw new TypeError('poses: t must be a finite number');
  switch (action) {
    case 'walk': return style.glide ? glide(t) : applyCarry(walk(t, style), style.carry);
    case 'idle': return applyCarry(idle(t), style.carry);
    case 'work': return work(t, style);
    case 'deliver': return deliver(t, style);
    default: throw new Error(`poses: unknown action '${action}'`);
  }
}

/** 两个姿势之间按 k（0..1）插值——动作切换时用一小段过渡，不跳帧。手里的小件取 k ≥ 0.5 的那一边。 */
export function blendPoses(a, b, k) {
  const out = restPose();
  const kk = clamp01(k);
  for (const joint of JOINTS) {
    for (const key of Object.keys(out[joint])) out[joint][key] = lerp(a[joint][key], b[joint][key], kk);
  }
  out.propR = kk < 0.5 ? a.propR : b.propR;
  out.propL = kk < 0.5 ? a.propL : b.propL;
  return out;
}

/** 关节角的上限（弧度）：测试用它确认没有哪个动作把胳膊拧到身体里去。 */
export const LIMITS = Object.freeze({ angle: 2.7, hipsOffset: 0.25 });
