// 人物的姿势（纯逻辑，不碰 three.js，Node 单测覆盖）。M2 的四种动作：走、停、做事、交付；
// M3（决定 2，A 档）加上：跑、说话（说 / 听）、挥手、接过，以及停着时隔一阵来一个的小动作（看一眼、换重心、抱臂、挠头、整衣）。
// 每个函数按"动作开始后过了多久"给出一组关节角——骨架（rig.mjs）只负责把角度摆上去。这样动作可以在 Node 里断言
// （周期、左右对称、角度有界、交付首尾回到站姿），截图审查看到的就是测试测的那一套。
//
// 约定：人物正面朝 +z，y 向上。关节绕 x 轴转正角 = 上端朝前倾（脊柱前倾、点头）；手臂与腿垂向 −y，
// 所以"向前抬"是负角（肩 / 胯 rx < 0），膝盖向后弯是正角。rz 是外展（左正右负，左右镜像）。单位：弧度、米、毫秒。

export const ACTIONS = Object.freeze(['walk', 'run', 'idle', 'work', 'deliver', 'talk', 'wave', 'receive']);

/** 一次性的动作（挥手、接过）：从站姿出发、回到站姿，时长固定（毫秒）；调用方到点就换回停。 */
export const ONE_SHOT_MS = Object.freeze({ wave: 1600, receive: 1600 });

/** 停着时的小动作：拿着家什的人只看一眼、换换重心（抱臂、挠头、整衣要腾出手）。 */
export const FIDGETS = Object.freeze(['look', 'shift', 'arms', 'scratch', 'garment']);
export const FIDGET = Object.freeze({ everyMs: 8000, spreadMs: 600, lastsMs: 2200 });

/** 做事的几种样子：按工具类别（雅典娜）或居民的行当选。 */
export const WORK_VARIANTS = Object.freeze([
  'read', 'write', 'hammer', 'forge', 'summon', 'haul', 'paint', 'announce', 'shelve', 'count', 'survey', 'carve', 'tend', 'await',
  // 第三轮：敲门（你，需求 3）；居民的手艺（需求 4，art/crafts.mjs 的十种活）
  'knock', 'wheel', 'spin', 'fish', 'knead', 'hoe', 'grind', 'stitch', 'dress', 'saw', 'lyre',
  // 第四轮（需求 5）：织工在立式织机前织布、牧人拄着牧杖看羊、商贩在市集吆喝
  'weave', 'herd', 'hawk',
  // 市集（2026-10-15）：买主看货、摊主摊开两手招呼、一次手递手（买主递钱接货 / 摊主收钱递货）、摆摆手走开
  'browse', 'offer', 'buy', 'sell', 'pass',
]);

/** 市集的手递手、摆手走开多长（毫秒；与 market.MARKET.tradeMs / passMs 一致——market.test 核对）。game 按它们给 t。 */
export const TRADE_MS = 3600;
export const PASS_MS = 1600;

/** 敲门一次多长（第三轮需求 3）：抬手、叩两下、放下。game 按它把"做事 / knock"当一次性的动作放。 */
export const KNOCK_MS = 1300;

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
    handL: { rx: 0, ry: 0, rz: 0 },
    handR: { rx: 0, ry: 0, rz: 0 },
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
 * 前臂转到拇指朝前（M3 第三轮）：锤、凿、火钳、缆绳从拳眼里横穿过去，前臂一转，柄就和前臂垂直、落在挥动的那个平面里——
 * 第一版没有这一转，柄只能顺着前臂，锤头贴在手腕上。
 */
export const NEUTRAL = Object.freeze({ L: -Math.PI / 2, R: Math.PI / 2 });

/**
 * 干活时的胳膊：[肩 rx, 肩 ry, 肩 rz, 肘 rx, 腕 rx, 腕 ry, 腕 rz]。不是手摆的，是拿真骨架（rig.mjs）按接触点解出来的——
 * 锤面落在身前 0.6 米、比脚高 0.79 的砧面上；火钳的钳口在砧面上；凿刃抵在身前 0.5 米的石面上、凿子斜 44° 朝着人，
 * 木槌的槌头落在凿尾上；笔尖落在左掌托着的蜡板中间；食指尖落在算板上；画笔的笔尖落在身前 0.53 米的画板上；
 * 两只拳各攥一根卷轴的轴；两只掌心托着祭碗、捧着石板；收缆时前面那只拳在身前 0.4 米、后面那只在胸前。
 * 解的时候身体的其余部分按各自的 base 摆（脊柱前倾、低头），下面 work() 里用的是同一组值。tools.test.mjs 用同一副骨架核对这些接触。
 *
 * 凿石头（M3 第四轮，用户：右臂直接向左穿过胸膛去敲凿子）：第一版的凿尾偏向左肩，右手只能从左上方反手去敲，
 * 解出来的右肩内收 0.8 弧度、右拳落在身体左半边、离胸口 10 厘米——没有约束"手臂不进身体"，解只管槌头碰到凿尾。
 * 现在凿尾转向右边、对着拿槌的手（凿轴向右偏 22°），解的时候加了三条：右肘留在身体右侧、肘不高过肩下 12 厘米，
 * 拳不越过身体中线；上臂、前臂、拳都不进躯干（按身体、不按衣服量的椭圆柱）；槌柄与凿子垂直（圆槌头的侧面敲在凿尾上）。
 * 解出来是这样一下：右肘贴在身侧不动，前臂和手腕往上一抬、再往下劈——槌头沿着凿轴落下去。
 */
export const ARMS = Object.freeze({
  hammerReady: [-0.199, 0.27, -0.047, -0.516, 0.498, Math.PI / 2, -0.418],
  hammerRaised: [-2.65, 0.023, -0.779, -2.094, 1.1, Math.PI / 2, 0.6],
  hammerStrike: [-0.704, 0.221, 0.091, -0.05, 0.857, Math.PI / 2, -0.045],
  tongs: [-0.136, 0, -0.294, -0.665, 1.001, -Math.PI / 2, 0],
  chisel: [-0.605, -0.903, 0.074, -0.835, -0.288, -1.907, 0.379],
  malletRaised: [0.345, -0.084, -0.27, -2.089, 0.054, 1.62, 0.055],
  malletStrike: [0.253, -0.04, -0.26, -1.753, 0.024, 2.356, 0.042],
  writeL: [0.072, -0.828, 0.481, -0.941, -0.977, 0, -0.123],
  writeR: [0.088, 0.333, 0.184, -1.559, 0.422, 0.9, 0.326],
  countL: [-0.347, -0.638, 0.029, -0.422, -1.1, 0, -0.053],
  countR: [-0.072, 0.255, 0.329, -1.09, -0.011, 0.6, 0.005],
  paintL: [0.225, 0.145, -0.134, -0.993, -0.926, 0, 0.177],
  paintR: [-0.82, 0.085, 0.167, -0.875, -0.189, 0.5, -0.057],
  paintR2: [-0.832, 0.135, 0.27, -0.605, -0.236, 0.5, -0.14],
  readL: [0.264, -0.004, -0.069, -1.646, 0.18, -Math.PI / 2, -0.176],
  readR: [0.264, 0.004, 0.069, -1.646, 0.18, Math.PI / 2, 0.177],
  tendL: [-0.477, -0.267, -0.148, -0.843, -0.36, 0, -0.038],
  tendR: [-0.477, 0.267, 0.148, -0.843, -0.359, 0, 0.036],
  awaitL: [0.024, -0.254, -0.057, -0.736, -0.598, -0.5, -0.122],
  awaitR: [0.024, 0.255, 0.057, -0.736, -0.598, 0.5, 0.122],
  haulFrontR: [-0.84, -0.129, 0.211, -0.05, 0.087, Math.PI / 2, 0.09],
  haulBackR: [0.442, 0.194, 0.123, -1.515, 0.331, Math.PI / 2, 0.331],
  haulFrontL: [-0.84, 0.129, -0.211, -0.05, 0.09, -Math.PI / 2, -0.087],
  haulBackL: [0.442, -0.194, -0.123, -1.515, 0.331, -Math.PI / 2, -0.33],
  hugL: [0.078, -0.33, -0.038, -0.841, -0.938, 0, -0.277],
});

/**
 * 居民的手艺（第三轮需求 4）：和 ARMS 一样不是手摆的，是拿真骨架（身高 1.75、体格 1 的参照人，缩放 1）按接触点解出来的，
 * 身体按 CRAFT_BODY 摆好再解（node 的解算脚本在进度文档里记着做法）。每样两帧（单手托着东西的那一侧一帧），干活时在帧之间插值：
 *   揉面——两只掌心朝下按在面团上（面团顶 0.9 米），从人前 0.35 米推到 0.49 米；
 *   拉坯——两只掌心朝里贴着陶坯两侧（坯在人前 0.42 米），从坯腰（0.88 米高、离中线 0.112）收到坯颈（0.97 米、0.085）；
 *   琢石——两拳握在斧柄尾，落斧时斧头最低的一点落在人前 0.55 米、0.58–0.6 米高的石面上；举起来时两拳在右肩上方；
 *   锯木——锯条竖着、锯齿朝下，锯齿在木板的中线上（人前 0.42 米、0.83 米高、x −0.22）切进 1 厘米，右拳在 0.08–0.28 米之间来回；
 *       左掌按在木板上；
 *   锄地——锄刃最低的一点落在人前 0.85 米的地上；
 *   研磨——左掌朝上托着研钵在腰前，杵头落在钵底、提起来离钵底 0.14 米；
 *   缝鞋——左掌托着鞋底朝着脸，锥尖扎在鞋底面上、拉线时离开 7 厘米；
 *   弹琴——左掌朝上托着琴身、弦面朝前，右手指尖在弦中段前 2.5 厘米、左右各 3 厘米；
 *   纺线——左拳攥着卷线杆竖着（手的 y 朝上）、齐胸，右手指尖在腰前，纺锤竖着垂下；
 *   钓鱼——左拳在前齐胸、右拳在右胯旁，竿朝前上 28°。
 * 门前的工位按这个人的身高缩放（world 摆的时候乘 身高 / 1.75，离人的距离也乘），所以高矮不同的人接触点一样落在工位上。
 * crafts.test.mjs 用参照人和真工位核对这些接触。
 */
export const CRAFT_ARMS = Object.freeze({
  kneadBack: { L: [-0.39, -1, 0.345, -0.469, -1.084, -2.68, 0.6], R: [-0.531, 0.968, -0.214, -0.337, -1.1, 2.678, -0.6] },
  kneadPush: { L: [-1.075, -1, -0.064, -0.038, -1.1, -2.662, -0.146], R: [-0.953, 1, -0.012, -0.19, -1.1, 2.68, 0.512] },
  // 拉坯的三帧一路接着解（下一帧从上一帧的解出发）：同一个解的分支，插值时掌心沿着坯面走（离坯面 3–7 毫米），不穿进坯里
  wheelLow: { L: [-0.653, -0.516, 0.036, -0.505, 0.412, -1.052, 0.141], R: [-0.64, 0.486, -0.025, -0.503, 0.42, 1.083, -0.325] },
  wheelMid: { L: [-0.475, -0.546, 0.144, -0.922, 0.681, -1.009, 0.084], R: [-0.461, 0.502, -0.115, -0.927, 0.707, 1.058, -0.312] },
  wheelHigh: { L: [-0.415, -0.559, 0.162, -1.186, 0.927, -0.991, 0.038], R: [-0.39, 0.532, -0.141, -1.2, 0.956, 1.023, -0.297] },
  dressStrike: { L: [-0.373, -1, -0.275, -0.447, -0.274, -1.576, 0.597], R: [-0.058, 0.4, 0.013, -0.908, 0.407, 1.688, -0.215] },
  dressRaised: { L: [-1.771, -0.799, -0.456, -1.806, -0.034, -1.558, 0.052], R: [-2.007, 0.102, 0.168, -1.943, -0.215, 1.494, -0.195] },
  // 锯木的三帧也是一路接着解的：锯条竖着、锯齿朝下，来回的每一处锯齿都在锯口里（0.826–0.830 米）
  sawBack: { L: [-0.716, -0.606, 0.112, -0.02, -1.1, -2.68, -0.6], R: [0.599, -0.044, -0.145, -1.305, -0.573, 1.417, -0.593] },
  sawMid: { L: [-0.701, -0.665, 0.127, -0.023, -1.1, -2.68, -0.6], R: [0.328, -0.025, -0.157, -1.282, -0.444, 1.413, -0.507] },
  sawPush: { L: [-0.718, -0.595, 0.11, -0.022, -1.1, -2.68, -0.6], R: [0.052, -0.03, -0.145, -1.237, -0.497, 1.419, -0.184] },
  hoeStrike: { L: [-0.533, -0.838, -0.351, -1.253, -0.394, -1.728, 0.55], R: [-0.932, -0.056, 0.106, -0.039, -0.589, 2.185, 0.432] },
  hoeRaised: { L: [0.439, -0.846, 0.321, -1.758, 0.2, -1.37, -0.349], R: [-0.422, 0.435, 0.146, -2.097, -0.163, 1.137, -0.201] },
  grind: { L: [-0.49, -0.267, -0.228, -0.082, -1.1, -0.132, -0.213] },
  grindDown: { R: [-0.122, 0.501, 0.115, -1.707, 0.075, 1.693, 0.05] },
  grindUp: { R: [-0.568, 0.428, 0.265, -1.829, 0.538, 1.643, 0.108] },
  stitch: { L: [0.004, -0.398, -0.048, -1.187, -0.933, -0.252, -0.307] },
  stitchPrick: { R: [-0.234, 0.922, -0.325, -0.818, -1.1, 1.745, -0.6] },
  stitchPull: { R: [-0.14, 0.801, -0.304, -1.145, -1.1, 1.754, -0.6] },
  lyre: { L: [-0.066, -0.119, -0.115, -0.587, -0.935, -0.108, 0.122] },
  lyreA: { R: [-0.529, 0.69, 0.392, -1.268, 0.218, 0.079, 0.406] },
  lyreB: { R: [-0.299, 0.61, 0.258, -1.447, 0.159, 0.065, 0.41] },
  spin: { L: [0.215, -0.479, 0.74, -1.651, 1.1, 0, -0.6], R: [-0.172, 0.539, -0.007, -1.015, 1.089, 0, 0.075] },
  fish: { L: [-0.464, -0.049, -0.25, -0.809, 0.016, -1.591, -0.001], R: [0.344, 0.1, 0.045, -0.874, 0.078, 1.615, 0.064] },
  // 第四轮（需求 5）。织布——经线面在人前 0.42 米，两只掌心朝前贴着经线（织口在 1.22–1.34 米）：右手从右边把梭子递到中间（A → B），
  // 再两手往上把纬线打紧（Beat）；三帧从 A 出发接着解，插值时掌心不离经线面
  weaveA: { L: [-0.421, 0.373, -0.299, -1.189, -1.1, -2.68, 0.541], R: [-0.419, -0.708, 0.312, -1.009, -1.1, 2.646, -0.453] },
  weaveB: { L: [-0.328, 0.49, -0.5, -1.222, -1.1, -2.68, 0.326], R: [-0.494, -0.084, 0.484, -0.811, -1.1, 2.68, -0.6] },
  weaveBeat: { L: [-0.498, 0.274, -0.429, -1.277, -1.1, -2.68, 0.6], R: [-0.467, -0.265, 0.5, -1.284, -1.1, 2.676, -0.594] },
  // 看羊——右拳 1.02 米高攥着牧杖、手的 y 轴朝上（杖竖着、杖脚在右前方的地上），左拳叠在右拳上面 0.11 米
  herd: { L: [-0.698, -0.951, -0.5, -0.963, -0.053, -1.536, 0.009], R: [0.122, 0.175, -0.21, -1.264, 0.509, 1.294, 0.503] },
});

/** 解 CRAFT_ARMS 时身体的样子（脊柱前倾、低头、膝、髋往后、两腿前后）；举起来那一帧的脊柱另记。 */
export const CRAFT_BODY = Object.freeze({
  knead: Object.freeze({ spine: 0.48, pushSpine: 0.58, head: 0.3, knees: [0.1, 0.1], hipsZ: -0.04 }),
  wheel: Object.freeze({ spine: 0.52, head: 0.38, knees: [0.14, 0.14], hipsZ: -0.03 }),
  dress: Object.freeze({ spine: 0.5, raisedSpine: 0.08, head: 0.3, legs: [-0.14, 0.12], knees: [0.2, 0.1], hipsZ: -0.04 }),
  saw: Object.freeze({ spine: 0.5, head: 0.38, legs: [-0.12, 0.12], knees: [0.1, 0], hipsZ: -0.04 }),
  hoe: Object.freeze({ spine: 0.48, raisedSpine: 0.16, head: 0.25, legs: [-0.18, 0.12], knees: [0.15, 0] }),
  grind: Object.freeze({ spine: 0.14, head: 0.45 }),
  stitch: Object.freeze({ spine: 0.12, head: 0.48 }),
  lyre: Object.freeze({ spine: 0.02, head: 0.12, headTurn: 0.18 }),
  spin: Object.freeze({ spine: 0.04, head: 0.22, headTurn: -0.1 }),
  fish: Object.freeze({ spine: 0.05, head: 0.12 }),
  weave: Object.freeze({ spine: 0.04, head: 0.02 }),
  herd: Object.freeze({ spine: 0.06, head: 0.05 }),
  hawk: Object.freeze({ spine: -0.02, head: -0.04 }),
});

function craftBody(p, b) {
  p.spine.rx = b.spine; p.head.rx = b.head; p.head.ry = b.headTurn ?? 0;
  if (b.legs) { p.legL.rx = b.legs[0]; p.legR.rx = b.legs[1]; }
  if (b.knees) { p.kneeL.rx = b.knees[0]; p.kneeR.rx = b.knees[1]; }
  if (b.hipsZ) p.hips.z = b.hipsZ;
}

/** 一侧胳膊此刻的七个角（ARMS 的格式）。 */
const armOf = (p, side) => [p[`shoulder${side}`].rx, p[`shoulder${side}`].ry, p[`shoulder${side}`].rz, p[`elbow${side}`].rx, p[`hand${side}`].rx, p[`hand${side}`].ry, p[`hand${side}`].rz];

/**
 * 市集上手递手的胳膊（2026-10-15）：隔着柜台、两人相距 1.37 米，伸直了才递得到——
 *   reach —— 两只掌心朝上、伸到身前齐胸（托着那包货：腕、肘、肩的 rx 加起来和祭碗那一组一样，掌心朝向不变，只是伸得远）；
 *   coinR —— 买主右手捏着钱，前臂端平伸过柜台；palmL —— 摊主左手掌心朝上伸过去接（面对面时，买主的右手正对着摊主的左手）。
 * market.test 在两副真骨架上量递到没有。
 */
export const MARKET_ARMS = Object.freeze({
  reachL: Object.freeze([-1.3, -0.18, -0.08, -0.22, -0.16, 0, -0.04]),
  reachR: Object.freeze([-1.3, 0.18, 0.08, -0.22, -0.16, 0, 0.04]),
  coinR: Object.freeze([-1.42, 0.1, -0.02, -0.12, -0.2, 0.6, 0]),
  palmL: Object.freeze([-1.38, -0.2, 0.06, -0.18, -0.25, -0.25, 0]),
});

/** 把一组胳膊角（ARMS 的格式）摆到一侧；mix 给了就在两组之间按 k 插值。 */
function setArm(p, side, a, b = null, k = 0) {
  const v = (i) => (b ? a[i] + (b[i] - a[i]) * k : a[i]);
  p[`shoulder${side}`].rx = v(0); p[`shoulder${side}`].ry = v(1); p[`shoulder${side}`].rz = v(2);
  p[`elbow${side}`].rx = v(3);
  p[`hand${side}`].rx = v(4); p[`hand${side}`].ry = v(5); p[`hand${side}`].rz = v(6);
  return p;
}

/**
 * 走和停的时候手里拿着家什（居民的 spec.carry）：
 *   staff — 长杖（测杆、权杖）：前臂平端、手腕补正，杖竖着；这只手不随步子摆；
 *   hug   — 石板、算板、调色板平托在左掌上；卷轴垂手斜握在右胯旁；
 *   both  — 市集上买的那包货（2026-10-15）：两只掌心捧在腰前（和等审批捧石板一样的胳膊），两只手都不随步子摆。
 */
export const CARRY_GRIPS = Object.freeze(['staff', 'hug', 'both']);
function applyCarry(p, carry) {
  if (!carry) return p;
  const side = carry.side === 'L' ? 'L' : 'R';
  const sx = side === 'L' ? 1 : -1;
  if (carry.grip === 'staff') {
    p[`shoulder${side}`].rx = -0.3; p[`shoulder${side}`].rz = 0.12 * sx;
    p[`elbow${side}`].rx = -1.2;
    p[`hand${side}`].rx = upright(p, side);
  } else if (carry.grip === 'hug') {
    if (side === 'L') {
      // 板（蜡板、算板、调色板）平放在左掌上、托在腰前（掌心朝上）——第一版前臂收在胸前，板竖着贴在胸口、正面朝外
      setArm(p, 'L', ARMS.hugL);
    } else {
      // 卷着的卷轴攥在右拳里，胳膊垂在身侧、肘微弯，拇指朝前、手腕往下压：卷轴斜着握在胯旁，前端朝前下
      // （2026-10-16 之前前臂端在胸前、卷轴竖着立在拳上，顶到肩高、离身子 0.44 米——像举着火把）
      p[`shoulder${side}`].rx = -0.1; p[`shoulder${side}`].rz = 0.1 * sx;
      p[`elbow${side}`].rx = -0.3;
      p[`hand${side}`].rx = 0.95; p[`hand${side}`].ry = NEUTRAL[side];
    }
  } else if (carry.grip === 'both') {
    setArm(p, 'L', ARMS.awaitL); setArm(p, 'R', ARMS.awaitR);
    p.propR = carry.prop;
    return p;
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
 * 停：呼吸、重心左右换、头偶尔转一转；手臂自然垂下。每隔 FIDGET.everyMs（按 seed 错开几百毫秒）来一个 2.2 秒的小动作，
 * 哪一个由 (seed, 第几次) 决定——同一个人同一刻永远是同一个动作（截图与断言可复现）；满城的人不会同时抬手。
 */
function idle(t, style = {}) {
  const p = restPose();
  p.spine.rx = 0.012 * Math.sin((t / 4200) * TAU);
  p.hips.x = 0.012 * Math.sin((t / 7000) * TAU);
  p.hips.rz = 0.02 * Math.sin((t / 7000) * TAU);
  p.head.ry = 0.28 * Math.sin((t / 9000) * TAU) * Math.abs(Math.sin((t / 9000) * TAU));
  p.shoulderL.rz = 0.07 + 0.01 * Math.sin((t / 4200) * TAU);
  p.shoulderR.rz = -0.07 - 0.01 * Math.sin((t / 4200) * TAU);
  p.elbowL.rx = -0.14; p.elbowR.rx = -0.14;
  const fidget = fidgetAt(t, style);
  if (fidget) {
    const { kind, e } = fidget;   // e：0 → 1 → 0 的包络，动作抬起又放下
    switch (kind) {
      case 'look': p.head.ry += 0.55 * e * fidget.side; p.head.rx -= 0.04 * e; break;
      case 'shift': p.hips.x += 0.035 * e * fidget.side; p.hips.rz += 0.06 * e * fidget.side; p.spine.rz -= 0.05 * e * fidget.side; p.head.rz += 0.03 * e * fidget.side; break;
      case 'arms':   // 抱臂
        p.shoulderL.rx = -0.5 * e; p.shoulderL.rz = -0.32 * e; p.elbowL.rx = -1.95 * e;
        p.shoulderR.rx = -0.55 * e; p.shoulderR.rz = 0.3 * e; p.elbowR.rx = -1.9 * e;
        break;
      case 'scratch':   // 右手挠挠后脑
        p.shoulderR.rx = -2.25 * e; p.shoulderR.rz = -0.55 * e; p.elbowR.rx = -2.2 * e; p.head.rz = 0.1 * e; p.head.rx += 0.08 * e;
        break;
      case 'garment':   // 左手整一下肩头的衣服
        p.shoulderL.rx = -1.05 * e; p.shoulderL.rz = -0.5 * e; p.elbowL.rx = -2.15 * e; p.head.ry -= 0.2 * e;
        break;
      default: break;
    }
  }
  return p;
}

/** 此刻停着的人在做哪个小动作（没有就是 null）：{ kind, e, side }。 */
export function fidgetAt(t, style = {}) {
  const seed = Math.abs(Math.floor(style.seed ?? 0));
  const slot = FIDGET.everyMs + (seed % 7) * FIDGET.spreadMs;
  const shifted = t + (seed % 13) * 457;
  const k = Math.floor(shifted / slot);
  const local = shifted - k * slot;
  if (k === 0 || local >= FIDGET.lastsMs) return null;   // 第一轮先安静站一会儿
  const pool = style.carry ? FIDGETS.slice(0, 2) : FIDGETS;
  const kind = pool[(k * 7 + seed * 3) % pool.length];
  const f = local / FIDGET.lastsMs;
  return { kind, e: Math.sin(Math.PI * f), side: (k + seed) % 2 === 0 ? 1 : -1 };
}

/**
 * 跑（按住 Shift）：步幅大、前倾、肘弯成直角前后摆、两腿交叉时腾空最高——和走的区别在剪影上一眼能看出来。
 * 周期仍由步幅 / 速度算（phase 按走过的距离推进，脚不打滑）。
 */
function run(t, style) {
  const p = restPose();
  const P = style.period ?? walkPeriod({ stride: style.stride ?? 2.2, speed: style.speed ?? 6 });
  const ph = ((t % P) / P) * TAU;
  const s = Math.sin(ph);
  const swing = style.legSwing ?? 0.72;
  p.legL.rx = -swing * s;
  p.legR.rx = swing * s;
  p.kneeL.rx = 1.1 * Math.max(0, Math.cos(ph)) * Math.max(0, s + 0.4);
  p.kneeR.rx = 1.1 * Math.max(0, -Math.cos(ph)) * Math.max(0, -s + 0.4);
  p.hips.y = 0.04 * Math.cos(2 * ph) + 0.012;
  p.hips.ry = 0.12 * s;
  p.spine.ry = -0.14 * s;
  p.spine.rx = 0.2;
  p.head.rx = -0.12;
  p.head.ry = 0.03 * s;
  const arm = style.armSwing ?? 0.8;
  p.shoulderL.rx = arm * s - 0.1;
  p.shoulderR.rx = -arm * s - 0.1;
  p.elbowL.rx = -1.45; p.elbowR.rx = -1.45;
  p.skirt.rx = 0.12 + 0.05 * Math.sin(ph + 0.6);
  p.skirt.sz = 1 + 0.12 * Math.abs(s);
  return p;
}

/**
 * 说话：speaker 为真是在说——一只手在胸前比划（抬起、翻掌、落下，节奏不齐），另一只手偶尔抬一下，头随着话点、略偏；
 * 为假是在听——双手交握在身前，头微微偏着，隔一阵点一下头。gestureSide 是比划的那只手（拿着家什的人用另一只）。
 */
function talk(t, style) {
  const p = restPose();
  const seed = Math.abs(Math.floor(style.seed ?? 0));
  const w = (period, ph = 0) => ((t + seed * 97) / period) * TAU + ph;
  p.spine.rx = 0.04 + 0.01 * Math.sin(w(3600));
  if (style.speaker !== false) {
    const g = style.gestureSide === 'L' ? 'L' : 'R', o = g === 'L' ? 'R' : 'L';
    const gs = g === 'L' ? 1 : -1, os = -gs;
    const beat = 0.5 + 0.5 * Math.sin(w(1300)) * Math.sin(w(2100, 0.7));
    p[`shoulder${g}`].rx = -0.7 - 0.45 * beat; p[`shoulder${g}`].rz = gs * (0.2 + 0.15 * beat);
    p[`elbow${g}`].rx = -1.5 + 0.35 * Math.sin(w(1300, 1.2));
    p[`hand${g}`].rz = gs * 0.3 * Math.sin(w(1300, 0.4));
    const other = Math.max(0, Math.sin(w(5200)));
    p[`shoulder${o}`].rx = -0.25 - 0.5 * other; p[`shoulder${o}`].rz = os * (0.12 + 0.1 * other);
    p[`elbow${o}`].rx = -0.9 - 0.4 * other;
    p.head.rx = 0.05 + 0.05 * Math.sin(w(900));
    p.head.ry = 0.08 * Math.sin(w(2700));
    p.head.rz = 0.04 * Math.sin(w(3300));
  } else {
    p.shoulderL.rx = -0.38; p.shoulderL.rz = -0.08; p.elbowL.rx = -1.15;
    p.shoulderR.rx = -0.38; p.shoulderR.rz = 0.08; p.elbowR.rx = -1.15;
    p.handL.rz = 0.25; p.handR.rz = -0.25;
    const nod = Math.max(0, Math.sin(w(4100)));
    p.head.rx = 0.06 + 0.1 * nod * nod;
    p.head.rz = 0.06;
    p.head.ry = 0.03 * Math.sin(w(6100));
  }
  return p;
}

/** 挥手（一次性，ONE_SHOT_MS.wave）：右臂举过肩，手来回摆两下半，放下；头转过去一点。 */
function wave(t) {
  const p = restPose();
  const f = clamp01(t / ONE_SHOT_MS.wave);
  const up = keyed([[0, 0], [0.3, 1], [0.7, 1], [1, 0]], f);   // 抬和放各 480 毫秒：一帧不超过 0.12 弧度
  // 从站姿出发（肩 rz ±0.06、肘 −0.1 是站姿的值），回到站姿
  p.shoulderR.rx = -2.3 * up; p.shoulderR.rz = -0.06 - 0.49 * up;
  p.elbowR.rx = -0.1 - 0.8 * up;
  p.handR.rz = up * 0.5 * Math.sin(f * TAU * 2.5);
  p.head.ry = -0.15 * up; p.head.rx = -0.05 * up;
  p.spine.rz = 0.03 * up;
  return p;
}

/** 接过（一次性，ONE_SHOT_MS.receive）：双手伸出去接，收回胸前抱一下，放下。 */
function receive(t) {
  const p = restPose();
  const f = clamp01(t / ONE_SHOT_MS.receive);
  const reach = keyed([[0, 0], [0.3, 1], [0.55, 1], [0.8, 0.35], [1, 0]], f);
  const hug = keyed([[0, 0], [0.5, 0], [0.75, 1], [1, 0]], f);
  p.shoulderL.rx = -1.05 * reach - 0.45 * hug; p.shoulderL.rz = 0.06 - 0.21 * reach - 0.11 * hug;
  p.shoulderR.rx = -1.05 * reach - 0.45 * hug; p.shoulderR.rz = -0.06 + 0.21 * reach + 0.11 * hug;
  p.elbowL.rx = -0.1 - 0.35 * reach - 1.4 * hug; p.elbowR.rx = -0.1 - 0.35 * reach - 1.4 * hug;
  p.spine.rx = 0.14 * reach;
  p.head.rx = 0.2 * reach + 0.1 * hug;
  return p;
}

/**
 * 挥锤一下（1 秒一个循环）：预备（锤面在砧面上方）→ 举到肩后（锤头垂在背后）→ 落下砸在砧面上 → 停一拍 → 回到预备。
 * 返回右臂这一刻在两组解出来的胳膊之间的插值（关节空间里插，锤头走的是一条从肩后到身前的弧）。
 */
function hammerArm(p, f) {
  if (f < 0.42) setArm(p, 'R', ARMS.hammerReady, ARMS.hammerRaised, smooth(f / 0.42));
  else if (f < 0.6) setArm(p, 'R', ARMS.hammerRaised, ARMS.hammerStrike, Math.pow(clamp01((f - 0.42) / 0.18), 1.6));   // 落得快
  else if (f < 0.72) setArm(p, 'R', ARMS.hammerStrike);
  else setArm(p, 'R', ARMS.hammerStrike, ARMS.hammerReady, smooth((f - 0.72) / 0.28));
  return p;
}

/** 做事：按样子各有一个循环。返回的 propL / propR 是手里该出现的小件（骨架按名字显隐）。 */
function work(t, style) {
  const p = restPose();
  const v = style.variant ?? 'read';
  const w = (period) => ((t % period) / period) * TAU;
  switch (v) {
    case 'read':   // 两只拳各攥一根卷轴的轴，纸横在胸前朝着脸，低头读；隔一阵翻一下眼（头动一点）
      p.spine.rx = 0.06;
      setArm(p, 'L', ARMS.readL); setArm(p, 'R', ARMS.readR);
      p.head.rx = 0.38 + 0.04 * Math.sin(w(3200));
      p.head.ry = 0.06 * Math.sin(w(5300));
      p.propR = 'scrollOpen';
      break;
    case 'write': {  // 左掌托着蜡板、朝着脸斜着；右手捏着笔，笔尖落在板上一笔一笔地划，写完一行抬一下笔
      p.spine.rx = 0.1; p.head.rx = 0.42;
      setArm(p, 'L', ARMS.writeL);
      setArm(p, 'R', ARMS.writeR);
      const lift = Math.max(0, Math.sin(w(1900)) - 0.85) * 2.2;   // 一行写完，笔抬起来一点
      p.handR.rz += 0.07 * Math.sin(w(420));
      p.handR.rx -= 0.04 * Math.sin(w(210));
      p.elbowR.rx += 0.05 * lift;
      p.propL = 'tablet'; p.propR = 'stylus';
      break;
    }
    case 'hammer': {  // 雅典娜：右手抡锤；左手在身前稳着（她身边没有铁砧）
      const f = (t % 1000) / 1000;
      p.spine.rx = 0.16; p.head.rx = 0.35;
      hammerArm(p, f);
      p.shoulderL.rx = -0.55; p.shoulderL.rz = -0.12; p.elbowL.rx = -1.05; p.handL.rx = 0.2;
      p.propR = 'hammer';
      break;
    }
    case 'forge': {  // 铁匠：右手抡锤砸在砧面上，左手的火钳把铁条按在砧面上（落锤那一下钳子跟着震一下）
      const f = (t % 1000) / 1000;
      p.spine.rx = 0.16; p.head.rx = 0.35;   // 和解落锤、火钳时的脊柱一样（ARMS）
      hammerArm(p, f);
      setArm(p, 'L', ARMS.tongs);
      const jolt = f > 0.6 && f < 0.72 ? Math.sin(((f - 0.6) / 0.12) * Math.PI) : 0;
      p.handL.rx += 0.03 * jolt;
      p.propR = 'hammer'; p.propL = 'tongs';
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
    case 'haul': {  // 收缆：缆从前方的船上拉过来，穿过两只拳头；两手轮换——前面那只往回拉、后面那只往前去接，身子后仰
      const f = (t % 1400) / 1400;
      p.spine.rx = -0.1 + 0.04 * Math.sin(f * TAU); p.hips.z = -0.03; p.head.rx = 0.08;
      // 前半拍：右手在前往回拉、左手从后往前伸；后半拍反过来
      const k = smooth(f < 0.5 ? f / 0.5 : (f - 0.5) / 0.5);
      if (f < 0.5) { setArm(p, 'R', ARMS.haulFrontR, ARMS.haulBackR, k); setArm(p, 'L', ARMS.haulBackL, ARMS.haulFrontL, k); }
      else { setArm(p, 'R', ARMS.haulBackR, ARMS.haulFrontR, k); setArm(p, 'L', ARMS.haulFrontL, ARMS.haulBackL, k); }
      p.propR = 'rope';
      break;
    }
    case 'paint': {  // 左掌托着调色板；右手的笔在身前的画板上一笔一笔地抹（两组解出来的落笔点之间来回，换一笔时笔离开画板）
      const f = (t % 1100) / 1100;
      p.spine.rx = 0.04; p.head.rx = 0.1; p.head.ry = -0.06;
      setArm(p, 'L', ARMS.paintL);
      setArm(p, 'R', ARMS.paintR, ARMS.paintR2, f < 0.7 ? smooth(f / 0.7) : 1 - smooth((f - 0.7) / 0.3));
      if (f >= 0.7) p.elbowR.rx -= 0.12 * Math.sin(((f - 0.7) / 0.3) * Math.PI);   // 回笔时离开画板
      p.propR = 'brush'; p.propL = 'palette';
      break;
    }
    case 'announce':  // 传令官：右手立着权杖（手腕补正，杖是竖的），左手扬起比划，头左右看
      p.shoulderR.rx = -0.32; p.shoulderR.rz = -0.1; p.elbowR.rx = -1.25;
      p.shoulderL.rx = -1.35 + 0.28 * Math.sin(w(1400)); p.shoulderL.rz = 0.35; p.elbowL.rx = -0.55;
      p.head.ry = 0.3 * Math.sin(w(4000)); p.head.rx = -0.06;
      p.handR.rx = upright(p, 'R');
      p.propR = 'staff';
      break;
    case 'shelve':  // 图书管理员：右拳攥着卷轴的中段，举起来放上高处的格子；左手扶着格子的边
      p.shoulderR.rx = -2.35 + 0.12 * Math.sin(w(1800)); p.shoulderR.rz = -0.05; p.elbowR.rx = -0.35;
      p.shoulderL.rx = -1.6; p.shoulderL.rz = 0.15; p.elbowL.rx = -0.7; p.handL.rx = 0.35;
      p.spine.rx = -0.1; p.head.rx = -0.32;
      p.propR = 'scroll';
      break;
    case 'count': {  // 账房：左掌托着算板（平放在腰前）；右手的食指一下一下拨板上的石子
      const f = (t % 600) / 600;
      p.spine.rx = 0.12; p.head.rx = 0.45;
      setArm(p, 'L', ARMS.countL);
      setArm(p, 'R', ARMS.countR);
      p.elbowR.rx += 0.09 * Math.max(0, Math.sin(f * TAU));   // 抬指、落指
      p.handR.rz += 0.05 * Math.sin(w(1800));                // 换一颗石子
      p.propL = 'abacus';
      break;
    }
    case 'survey':  // 测绘员：右手立着测杆（手腕补正，杆是竖的、杖脚落地），左手搭在额前远望，头慢慢扫视
      p.shoulderR.rx = -0.3; p.shoulderR.rz = -0.12; p.elbowR.rx = -1.2;
      p.shoulderL.rx = -2.1; p.shoulderL.rz = 0.35; p.elbowL.rx = -2.0;
      p.head.ry = 0.45 * Math.sin(w(5200)); p.head.rx = -0.05;
      p.handR.rx = upright(p, 'R');
      p.propR = 'rod';
      break;
    case 'carve': {  // 工匠：左拳攥着凿子，凿刃抵在石面上；右手的木槌抡起来、落在凿尾上（敲的那一下凿子吃进石头一点）
      const f = (t % 700) / 700;
      p.spine.rx = 0.12; p.head.rx = 0.38;
      setArm(p, 'L', ARMS.chisel);
      if (f < 0.55) setArm(p, 'R', ARMS.malletStrike, ARMS.malletRaised, smooth(f / 0.55));
      else if (f < 0.82) setArm(p, 'R', ARMS.malletRaised, ARMS.malletStrike, Math.pow(clamp01((f - 0.55) / 0.27), 1.7));
      else setArm(p, 'R', ARMS.malletStrike);
      const hit = f >= 0.82 ? Math.sin(((f - 0.82) / 0.18) * Math.PI) : 0;
      p.elbowL.rx -= 0.02 * hit;
      p.propL = 'chisel'; p.propR = 'mallet';
      break;
    }
    case 'tend': {  // 女祭司：两只掌心托着祭碗，往火盆那边缓缓递出去、收回来，躬身
      const k = 0.5 + 0.5 * Math.sin(w(3600));
      p.spine.rx = 0.14 + 0.06 * k; p.head.rx = 0.25;
      setArm(p, 'L', ARMS.tendL); setArm(p, 'R', ARMS.tendR);
      p.shoulderL.rx -= 0.12 * k; p.shoulderR.rx -= 0.12 * k;
      p.propR = 'phiale';
      break;
    }
    case 'knock': {  // 敲门（第三轮需求 3）：右臂抬到身前、前臂竖着，拳头朝门板叩两下（0.39、0.68 秒），再放下；左手垂着
      const f = (t % KNOCK_MS) / KNOCK_MS;
      const up = keyed([[0, 0], [0.22, 1], [0.8, 1], [1, 0]], f);
      const tap = (u) => Math.max(0, Math.sin(Math.PI * clamp01((f - u) / 0.11)));
      const knock = tap(0.3) + tap(0.52);
      p.shoulderR.rx = -1.15 * up; p.shoulderR.rz = -0.06 + 0.1 * up;
      p.elbowR.rx = -0.1 - 1.5 * up + 0.38 * knock * up;
      p.handR.rx = 0.15 * up; p.handR.rz = -0.1 * up;
      p.spine.rx = 0.03 * up; p.head.rx = 0.06 * up;
      break;
    }
    // —— 第三轮（需求 4）：居民的手艺。胳膊都是 CRAFT_ARMS 里按接触点解出来的几帧，在帧之间按节拍插值；身体按 CRAFT_BODY ——
    case 'wheel': {  // 拉坯：俯身在陶轮前，两只掌心贴着转着的陶坯两侧，从坯腰慢慢往上收到坯颈、再回到坯腰
      const k = 0.5 - 0.5 * Math.cos(w(2600));
      const [a, b, u] = k < 0.5 ? [CRAFT_ARMS.wheelLow, CRAFT_ARMS.wheelMid, k / 0.5] : [CRAFT_ARMS.wheelMid, CRAFT_ARMS.wheelHigh, (k - 0.5) / 0.5];
      craftBody(p, CRAFT_BODY.wheel);
      setArm(p, 'L', a.L, b.L, u);
      setArm(p, 'R', a.R, b.R, u);
      break;
    }
    case 'knead': {  // 揉面：俯在揉面台上，两只掌心按着面团往前推（身子跟着压下去）、收回来
      const f = (t % 1300) / 1300;
      const push = f < 0.55 ? smooth(f / 0.55) : 1 - smooth((f - 0.55) / 0.45);
      craftBody(p, CRAFT_BODY.knead);
      p.spine.rx = lerp(CRAFT_BODY.knead.spine, CRAFT_BODY.knead.pushSpine, push);
      setArm(p, 'L', CRAFT_ARMS.kneadBack.L, CRAFT_ARMS.kneadPush.L, push);
      setArm(p, 'R', CRAFT_ARMS.kneadBack.R, CRAFT_ARMS.kneadPush.R, push);
      break;
    }
    case 'dress': {  // 琢石：两手握着斧柄尾，举过右肩、劈在身前的石面上，停一拍再举
      const f = (t % 1400) / 1400;
      const up = f < 0.55 ? smooth(f / 0.55) : f < 0.7 ? 1 - Math.pow(clamp01((f - 0.55) / 0.15), 1.6) : 0;
      craftBody(p, CRAFT_BODY.dress);
      p.spine.rx = lerp(CRAFT_BODY.dress.spine, CRAFT_BODY.dress.raisedSpine, up);
      setArm(p, 'L', CRAFT_ARMS.dressStrike.L, CRAFT_ARMS.dressRaised.L, up);
      setArm(p, 'R', CRAFT_ARMS.dressStrike.R, CRAFT_ARMS.dressRaised.R, up);
      p.propL = 'stoneAxe';
      break;
    }
    case 'saw': {  // 锯木：俯身在锯木架前，左掌按着木板，右手的锯沿着锯口来回拉
      const k = 0.5 - 0.5 * Math.cos(w(1000));
      const [a, b, u] = k < 0.5 ? [CRAFT_ARMS.sawBack, CRAFT_ARMS.sawMid, k / 0.5] : [CRAFT_ARMS.sawMid, CRAFT_ARMS.sawPush, (k - 0.5) / 0.5];
      craftBody(p, CRAFT_BODY.saw);
      setArm(p, 'L', a.L, b.L, u);
      setArm(p, 'R', a.R, b.R, u);
      p.propR = 'saw';
      break;
    }
    case 'hoe': {  // 锄地：两手握锄，举到齐肩、刨进身前的土里，停一拍再举
      const f = (t % 1600) / 1600;
      const up = f < 0.55 ? smooth(f / 0.55) : f < 0.7 ? 1 - Math.pow(clamp01((f - 0.55) / 0.15), 1.5) : 0;
      craftBody(p, CRAFT_BODY.hoe);
      p.spine.rx = lerp(CRAFT_BODY.hoe.spine, CRAFT_BODY.hoe.raisedSpine, up);
      setArm(p, 'L', CRAFT_ARMS.hoeStrike.L, CRAFT_ARMS.hoeRaised.L, up);
      setArm(p, 'R', CRAFT_ARMS.hoeStrike.R, CRAFT_ARMS.hoeRaised.R, up);
      p.propL = 'hoe';
      break;
    }
    case 'grind': {  // 研磨：左掌托着研钵在腰前，右拳握杵竖着一下一下捣进钵底
      const f = (t % 760) / 760;
      const lift = f < 0.6 ? smooth(f / 0.6) : 1 - Math.pow(clamp01((f - 0.6) / 0.4), 1.4);
      craftBody(p, CRAFT_BODY.grind);
      setArm(p, 'L', CRAFT_ARMS.grind.L);
      setArm(p, 'R', CRAFT_ARMS.grindDown.R, CRAFT_ARMS.grindUp.R, lift);
      p.propL = 'mortar'; p.propR = 'pestle';
      break;
    }
    case 'stitch': {  // 缝鞋：左掌托着鞋底朝着脸，右手的锥子扎进去、拉线、再扎
      const f = (t % 1300) / 1300;
      const pull = f < 0.4 ? smooth(f / 0.4) : f < 0.7 ? 1 : 1 - smooth((f - 0.7) / 0.3);
      craftBody(p, CRAFT_BODY.stitch);
      setArm(p, 'L', CRAFT_ARMS.stitch.L);
      setArm(p, 'R', CRAFT_ARMS.stitchPrick.R, CRAFT_ARMS.stitchPull.R, pull);
      p.propL = 'sandal'; p.propR = 'awl';
      break;
    }
    case 'lyre': {  // 弹琴：左掌托着里拉琴竖在胸前，右手的指尖在弦上来回拨；头随着拍子轻轻点
      craftBody(p, CRAFT_BODY.lyre);
      setArm(p, 'L', CRAFT_ARMS.lyre.L);
      setArm(p, 'R', CRAFT_ARMS.lyreA.R, CRAFT_ARMS.lyreB.R, 0.5 - 0.5 * Math.cos(w(960)));
      p.head.rx += 0.05 * Math.sin(w(960)); p.head.rz = 0.04 * Math.sin(w(1920));
      p.propL = 'lyre';
      break;
    }
    case 'spin': {  // 纺线：左拳攥着卷线杆竖着、齐胸，右手在腰前捻线（手腕一转一转），纺锤从指尖垂下；头偏着看线
      craftBody(p, CRAFT_BODY.spin);
      setArm(p, 'L', CRAFT_ARMS.spin.L);
      setArm(p, 'R', CRAFT_ARMS.spin.R);
      const draft = 0.5 - 0.5 * Math.cos(w(3200));
      p.elbowR.rx += 0.12 * draft;
      p.handR.ry += 0.35 * Math.sin(w(700));
      p.propL = 'distaff'; p.propR = 'spindle';
      break;
    }
    case 'fish': {  // 钓鱼：左拳在前齐胸、右拳在右胯旁，竿朝前上；竿随手微微晃，隔一阵提一下
      const lift = Math.max(0, Math.sin(w(7000)) - 0.9) * 6;
      craftBody(p, CRAFT_BODY.fish);
      setArm(p, 'L', CRAFT_ARMS.fish.L);
      setArm(p, 'R', CRAFT_ARMS.fish.R);
      p.shoulderL.rx -= 0.18 * lift + 0.03 * Math.sin(w(2600));
      p.shoulderR.rx -= 0.08 * lift;
      p.propL = 'fishingRod';
      break;
    }
    case 'weave': {  // 织布（第四轮）：A → B 递梭子，B → Beat 打纬，Beat → A 回到右边；头跟着手里的梭子走
      const f = (t % 2600) / 2600;
      let a, b, u;
      if (f < 0.4) [a, b, u] = [CRAFT_ARMS.weaveA, CRAFT_ARMS.weaveB, smooth(f / 0.4)];
      else if (f < 0.6) [a, b, u] = [CRAFT_ARMS.weaveB, CRAFT_ARMS.weaveBeat, smooth((f - 0.4) / 0.2)];
      else [a, b, u] = [CRAFT_ARMS.weaveBeat, CRAFT_ARMS.weaveA, smooth((f - 0.6) / 0.4)];
      craftBody(p, CRAFT_BODY.weave);
      setArm(p, 'L', a.L, b.L, u);
      setArm(p, 'R', a.R, b.R, u);
      p.head.ry = f < 0.4 ? lerp(-0.12, 0.08, smooth(f / 0.4)) : f < 0.6 ? 0.08 : lerp(0.08, -0.12, smooth((f - 0.6) / 0.4));
      break;
    }
    case 'herd': {  // 看羊（第四轮）：两拳叠着拄在牧杖上，身子随着呼吸微微晃，头慢慢转着看羊群
      craftBody(p, CRAFT_BODY.herd);
      setArm(p, 'L', CRAFT_ARMS.herd.L);
      setArm(p, 'R', CRAFT_ARMS.herd.R);
      p.head.ry = 0.42 * Math.sin(w(9000)) + 0.12 * Math.sin(w(2300));
      p.spine.rz = 0.025 * Math.sin(w(5200));
      p.propR = 'crook';
      break;
    }
    case 'hawk': {  // 吆喝（第四轮，商贩在市集）：右手掌心朝上朝过路的人一招一招，左手叉腰，头左右张望
      craftBody(p, CRAFT_BODY.hawk);
      const beckon = 0.5 - 0.5 * Math.cos(w(900));
      const call = Math.max(0, Math.sin(w(5400)));   // 隔一阵扬一扬手
      p.shoulderR.rx = -1.0 - 0.35 * call; p.shoulderR.ry = 0.25; p.shoulderR.rz = -0.32;
      p.elbowR.rx = -0.75 - 0.6 * beckon;
      p.handR.rx = -0.35 + 0.5 * beckon; p.handR.ry = 0.2; p.handR.rz = 0;
      p.shoulderL.rx = 0.25; p.shoulderL.ry = -0.2; p.shoulderL.rz = 0.55;
      p.elbowL.rx = -1.65;
      p.handL.rx = 0.2; p.handL.ry = -1.2; p.handL.rz = 0.3;
      p.head.ry = 0.45 * Math.sin(w(6200));
      break;
    }
    case 'browse': {  // 看货（市集）：俯身看柜台上的货，右手伸过去翻一翻、拿起来又放下；左手隔一阵托着下巴想想，其余时候叉着腰
      p.spine.rx = 0.16 + 0.03 * Math.sin(w(5200));
      p.head.rx = 0.38; p.head.ry = 0.3 * Math.sin(w(9300));
      const touch = 0.5 - 0.5 * Math.cos(w(3100));
      p.shoulderR.rx = -0.55 - 0.3 * touch; p.shoulderR.rz = -0.12;
      p.elbowR.rx = -0.75 + 0.35 * touch;
      p.handR.rx = -0.45; p.handR.ry = 0.3;
      const think = smooth(clamp01((Math.sin(w(11000)) - 0.2) * 2.5));   // 一个循环里有一段托着下巴
      p.shoulderL.rx = lerp(0.25, -0.62, think); p.shoulderL.ry = lerp(-0.2, 0, think); p.shoulderL.rz = lerp(0.55, -0.22, think);
      p.elbowL.rx = lerp(-1.65, -2.3, think);
      p.handL.rx = lerp(0.2, -0.2, think); p.handL.ry = lerp(-1.2, 0, think); p.handL.rz = lerp(0.3, 0, think);
      p.head.rz = 0.06 * think;
      break;
    }
    case 'offer': {  // 招呼（市集，摊主）：两手摊开、掌心朝上朝柜台上的货一引，身子微微前倾，头朝着买主点一点
      const sweep = 0.5 - 0.5 * Math.cos(w(2600));
      p.spine.rx = 0.08 + 0.05 * sweep;
      for (const [side, sx] of [['L', 1], ['R', -1]]) {
        p[`shoulder${side}`].rx = -0.75 - 0.2 * sweep; p[`shoulder${side}`].ry = -sx * 0.25; p[`shoulder${side}`].rz = sx * (0.18 + 0.22 * sweep);
        p[`elbow${side}`].rx = -0.85 + 0.25 * sweep;
        p[`hand${side}`].rx = -0.3; p[`hand${side}`].ry = -sx * 0.25; p[`hand${side}`].rz = 0;
      }
      p.head.rx = 0.1 + 0.08 * Math.max(0, Math.sin(w(1800)));
      break;
    }
    case 'buy': {  // 买（市集，一次 TRADE_MS）：右手往腰间的钱袋里摸一枚钱、伸直了胳膊越过柜台递出去、收回；再两手伸出去接货、捧回腰前
      const f = clamp01(t / TRADE_MS);
      const purse = keyed([[0, 0], [0.08, 1], [0.14, 1], [0.2, 0], [1, 0]], f);
      const reach = keyed([[0, 0], [0.14, 0], [0.26, 1], [0.36, 1], [0.46, 0], [1, 0]], f);
      const take = keyed([[0, 0], [0.5, 0], [0.6, 1], [0.7, 1], [0.84, 0], [1, 0]], f);
      const hold = keyed([[0, 0], [0.7, 0], [0.84, 1], [1, 1]], f);
      p.spine.rx = 0.05 + 0.22 * reach + 0.24 * take;
      p.head.rx = 0.12 + 0.12 * purse - 0.08 * reach;
      // 右臂：钱袋（手垂到右胯前）→ 递钱（前臂端平，越过柜台）
      setArm(p, 'R', [0.12 * purse + MARKET_ARMS.coinR[0] * reach, MARKET_ARMS.coinR[1] * reach, -0.06 - 0.2 * purse + (MARKET_ARMS.coinR[2] + 0.06) * reach,
        -0.1 - 0.9 * purse + (MARKET_ARMS.coinR[3] + 0.1) * reach, -0.3 * purse + MARKET_ARMS.coinR[4] * reach, MARKET_ARMS.coinR[5] * reach, MARKET_ARMS.coinR[6] * reach]);
      // 接货：两只手伸出去托住（MARKET_ARMS.reach），再捧回腰前（捧石板的那一组）
      for (const side of ['L', 'R']) {
        const base = armOf(p, side);
        const target = MARKET_ARMS[`reach${side}`].map((v, i) => lerp(v, ARMS[`await${side}`][i], hold));
        setArm(p, side, base.map((v, i) => lerp(v, target[i], Math.max(take, hold))));
      }
      p.propR = f > 0.1 && f < 0.34 ? 'coin' : f >= 0.66 ? 'parcel' : null;
      break;
    }
    case 'sell': {  // 卖（市集，摊主，一次 TRADE_MS）：左手掌心朝上伸过去接钱（买主递钱的右手正对着他的左手）、收进腰间；
      // 两手从柜台上捧起那包货、伸直了递过去，松手
      const f = clamp01(t / TRADE_MS);
      const palm = keyed([[0, 0], [0.12, 1], [0.36, 1], [0.44, 0], [1, 0]], f);
      const purse = keyed([[0, 0], [0.4, 0], [0.45, 1], [0.49, 1], [0.54, 0], [1, 0]], f);
      const lift = keyed([[0, 0], [0.47, 0], [0.54, 1], [0.6, 0], [1, 0]], f);
      const give = keyed([[0, 0], [0.54, 0], [0.63, 1], [0.7, 1], [0.84, 0], [1, 0]], f);
      p.spine.rx = 0.06 + 0.22 * palm + 0.32 * lift + 0.24 * give;
      p.head.rx = 0.1 + 0.15 * lift - 0.06 * palm;
      const P = MARKET_ARMS.palmL;
      setArm(p, 'L', [P[0] * palm + 0.1 * purse, P[1] * palm, 0.06 + (P[2] - 0.06) * palm + 0.2 * purse,
        -0.1 + (P[3] + 0.1) * palm - 0.9 * purse, P[4] * palm, P[5] * palm, P[6] * palm]);
      for (const side of ['L', 'R']) {
        const a = MARKET_ARMS[`reach${side}`], b = ARMS[`tend${side}`];
        const low = [b[0] + 0.25, b[1], b[2], b[3] + 0.3, b[4], b[5], b[6]];   // 弯腰从柜台上捧起来
        const base = armOf(p, side);
        const target = low.map((v, i) => lerp(v, a[i], give));
        setArm(p, side, base.map((v, i) => lerp(v, target[i], Math.max(lift, give))));
      }
      p.propR = f >= 0.5 && f < 0.66 ? 'parcel' : null;
      break;
    }
    case 'pass': {  // 不买了（市集，一次 PASS_MS）：右手抬到胸前、掌心朝外左右摆两下，头跟着摇一摇，放下
      const f = clamp01(t / PASS_MS);
      const up = keyed([[0, 0], [0.22, 1], [0.75, 1], [1, 0]], f);
      p.shoulderR.rx = -0.75 * up; p.shoulderR.rz = -0.06 - 0.12 * up;
      p.elbowR.rx = -0.1 - 1.25 * up;
      p.handR.ry = 0.9 * up; p.handR.rz = up * 0.35 * Math.sin(f * TAU * 2);
      p.head.ry = up * 0.28 * Math.sin(f * TAU * 2 + 0.6);
      p.head.rx = 0.05 * up;
      break;
    }
    case 'await':  // 等审批：停在门槛前，两只掌心把封着红印的石板捧在胸前，静静等（设计稿 6.1）
      setArm(p, 'L', ARMS.awaitL); setArm(p, 'R', ARMS.awaitR);
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
 * @param {'walk'|'run'|'idle'|'work'|'deliver'|'talk'|'wave'|'receive'} action
 * @param {number} t 动作开始后过了多久（毫秒）
 * @param {{ variant?: string, duration?: number, stride?: number, speed?: number, period?: number, legSwing?: number, armSwing?: number, seed?: number, speaker?: boolean, carry?: { side: 'L'|'R', grip: 'staff'|'hug', prop: string } | null }} [style]
 */
export function poseAt(action, t, style = {}) {
  if (!Number.isFinite(t)) throw new TypeError('poses: t must be a finite number');
  switch (action) {
    case 'walk': return applyCarry(walk(t, style), style.carry);
    case 'run': return applyCarry(run(t, style), style.carry);
    case 'idle': return applyCarry(idle(t, style), style.carry);
    case 'work': return work(t, style);
    case 'deliver': return deliver(t, style);
    case 'talk': return applyCarry(talk(t, { ...style, gestureSide: style.carry?.side === 'R' ? 'L' : 'R' }), style.carry);
    case 'wave': return wave(t, style);
    case 'receive': return receive(t, style);
    default: throw new Error(`poses: unknown action '${action}'`);
  }
}

/** 一次性的动作到时间了没有（挥手、接过）；循环的动作永远没完。 */
export function oneShotDone(action, t) {
  return action in ONE_SHOT_MS && t >= ONE_SHOT_MS[action];
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
