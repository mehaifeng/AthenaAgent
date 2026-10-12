// 人物的骨架（设计稿 M2 行"人物及其动作"）。人物只在游戏视距出现（第 8 节：不做 3D 近景），所以骨架是一组刚体：
// 髋、腿（大腿 / 小腿 / 脚）、脊柱、头、肩 / 肘 / 手，衣服按衣着挂在髋和脊柱上。姿势由 poses.mjs 的纯函数给出，
// 这里只把关节角摆上去——测试测的姿势就是画出来的姿势。衣褶几何（drape）与柱身的凹槽是同一种做法
// （古典少女像本来就是这样雕的），头部的雕法取自打样。
//
// 约定：人物正面朝 +z；左边是 +x（镜头从东南看过来，看到的是人物的左前方）。所有尺寸按 1.75 米高的身体给，
// 整体按 spec.height 缩放。锚点（胸口、头顶）在根节点的坐标里，不随呼吸起伏——旁白气泡不该跟着一颤一颤。
//
// M3（决定 2，A 档）：动作切换时两组姿势之间过渡 BLEND_MS（poses.blendPoses——M2 写了没接）；头会看向一个点（style.lookAt）；
// 围裙、面纱、辫子、斗篷挂在弹簧骨上（springs.mjs）；**裙摆是布料模拟**（cloth.mjs，用户：弹簧骨的裙摆风一吹、人一动腿就露在外面）——
// 一张粗代理布按位置约束积分、被大腿 / 小腿 / 骨盆 / 髋的胶囊顶在外面，带褶的细网格每帧贴在代理上重建。
// 姿势仍是纯函数给的；布料和弹簧骨只加二级运动，不改姿势。
import * as THREE from '../../vendor/three.bundle.mjs';
import { mergeGeometries } from '../../vendor/three.bundle.mjs';
import { lerp, smooth } from './noise.mjs';
import { restPose, poseAt, blendPoses } from './poses.mjs';
import { createChain, stepChain, settleChain, segmentDirection, SPRING } from './springs.mjs';
import { createCloth, stepCloth, bindFine, applyFine, clothPoint, CLOTH } from './cloth.mjs';
import { faceDecalGeometry, faceMaterialKey, lookFor } from './face.mjs';

/** 动作切换的过渡时长（毫秒）。 */
export const BLEND_MS = 220;
/** 头转向看的东西：最多转这么多（弧度）；目标在身后（超过 LOOK.giveUp）就不扭头，回正。 */
export const LOOK = Object.freeze({ maxYaw: 1.0, maxPitch: 0.45, giveUp: 1.5, tauMs: 140 });

/** 全城共用的风（世界坐标，米 / 秒量级的漂移）：game.mjs 每帧设一次，弹簧骨读它。 */
const WIND = new THREE.Vector3();
export function setClothWind(v) { WIND.copy(v); }
export function clothWind() { return WIND; }

const shadowed = (o) => { o.castShadow = true; o.receiveShadow = true; return o; };
const mesh = (g, m) => shadowed(new THREE.Mesh(g, m));

/** 佩普洛斯下摆的竖褶，和柱身的凹槽是同一种几何。 */
export function drape({ y0, y1, rings = 40, segs = 96, radius, ez = 0.8, folds = [], growth = () => 1, bumps = [], hem = null, thStart = 0, thEnd = Math.PI * 2 }) {
  const pos = [], uv = [], idx = [];
  for (let j = 0; j <= rings; j++) {
    const t = j / rings, yBase = lerp(y0, y1, t);
    for (let k = 0; k <= segs; k++) {
      const th = thStart + (k / segs) * (thEnd - thStart);
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

export function keyed(keys) {
  return (t) => {
    for (let i = 0; i < keys.length - 1; i++) {
      const [t0, r0] = keys[i], [t1, r1] = keys[i + 1];
      if (t <= t1) return lerp(r0, r1, smooth(t0, t1, t));
    }
    return keys[keys.length - 1][1];
  };
}

export function limbGeometry(a, b, r) {
  const A = new THREE.Vector3(...a), B = new THREE.Vector3(...b);
  const dir = new THREE.Vector3().subVectors(B, A);
  const len = dir.length();
  const g = new THREE.CapsuleGeometry(r, Math.max(0.001, len), 4, 10);
  g.applyQuaternion(new THREE.Quaternion().setFromUnitVectors(new THREE.Vector3(0, 1, 0), dir.normalize()));
  const mid = A.clone().addScaledVector(B.clone().sub(A), 0.5);
  g.translate(mid.x, mid.y, mid.z);
  return g;
}

export function blobGeometry(at, scale, rot = [0, 0, 0], detail = [20, 14]) {
  const g = new THREE.SphereGeometry(1, detail[0], detail[1]);
  g.applyMatrix4(new THREE.Matrix4().compose(new THREE.Vector3(...at), new THREE.Quaternion().setFromEuler(new THREE.Euler(...rot)), new THREE.Vector3(...scale)));
  return g;
}

export function merge(list) {
  return mergeGeometries(list.map((g) => {
    const n = g.index ? g.toNonIndexed() : g;
    for (const name of Object.keys(n.attributes)) if (!['position', 'normal', 'uv'].includes(name)) n.deleteAttribute(name);
    if (!n.attributes.normal) n.computeVertexNormals();
    if (!n.attributes.uv) n.setAttribute('uv', new THREE.Float32BufferAttribute(new Float32Array(n.attributes.position.count * 2), 2));
    return n;
  }));
}

// ———————————————————————— 弹簧骨上的衣料 ————————————————————————

const DOWN = new THREE.Vector3(0, -1, 0);

/**
 * 把一片垂着的衣料（drape 的几何：环从下到上，x = r·sinθ，z = r·cosθ·ez）挂到几条弹簧骨上：每个顶点按方位分给最近的两条链、
 * 按高度分给链上相邻的两节（最多四个影响，权重和为 1）。链的根骨头钉在衣料的顶环上（anchorRadius 处），不动；
 * 下面的几节由 springs.mjs 推着转。返回蒙皮网格与每条链。
 * @param {object} options chains 链数、bones 每链几节、top / bottom 衣料的上下沿（几何的 y）、anchorRadius 根骨头离中轴多远、
 *   ez 衣料的前后压扁比、thStart / thEnd 衣料覆盖的方位（整圈时链均匀分在 0、90°、180°、270°），scale 身体的缩放
 *   （弹簧骨在世界坐标里算，节长要跟着身体缩），spring 这片衣料的弹簧参数
 */
export function clothStrip(geometry, material, { chains = 4, bones = 3, top, bottom, anchorRadius = 0, ez = 1, thStart = 0, thEnd = Math.PI * 2, scale = 1, spring = {} }) {
  const pos = geometry.attributes.position;
  const count = pos.count;
  const skinIndex = new Uint16Array(count * 4), skinWeight = new Float32Array(count * 4);
  const span = top - bottom, segLen = span / bones;
  const closed = Math.abs((thEnd - thStart) - Math.PI * 2) < 1e-6;
  const arc = thEnd - thStart;
  const azimuths = Array.from({ length: chains }, (_, c) => (closed ? thStart + (c * arc) / chains : thStart + ((c + 0.5) * arc) / chains));
  const spacing = arc / chains;
  const wrap = (a) => Math.atan2(Math.sin(a), Math.cos(a));
  for (let i = 0; i < count; i++) {
    const th = Math.atan2(pos.getX(i), pos.getZ(i) / ez);
    const weights = azimuths.map((az, c) => {
      const d = Math.abs(wrap(th - az));
      return { c, w: chains === 1 ? 1 : Math.max(0, closed ? Math.cos(d) : 1 - d / spacing) };
    }).sort((a, b) => b.w - a.w).slice(0, 2);
    let sum = weights[0].w + (weights[1]?.w ?? 0);
    if (sum <= 0) { weights[0].w = 1; sum = 1; }
    const sAlong = Math.min(1, Math.max(0, (top - pos.getY(i)) / span));
    const k = Math.min(bones - 1, Math.floor(sAlong * bones));
    const k2 = Math.min(bones - 1, k + 1);
    const f = k2 === k ? 0 : Math.min(1, Math.max(0, sAlong * bones - k));
    const slots = [];
    for (const { c, w } of weights) {
      if (!(w > 0)) continue;
      slots.push([c * bones + k, (w / sum) * (1 - f)]);
      if (f > 0) slots.push([c * bones + k2, (w / sum) * f]);
    }
    for (let j = 0; j < 4; j++) {
      skinIndex[i * 4 + j] = slots[j]?.[0] ?? 0;
      skinWeight[i * 4 + j] = slots[j]?.[1] ?? 0;
    }
  }
  geometry.setAttribute('skinIndex', new THREE.Uint16BufferAttribute(skinIndex, 4));
  geometry.setAttribute('skinWeight', new THREE.Float32BufferAttribute(skinWeight, 4));
  const mesh = shadowed(new THREE.SkinnedMesh(geometry, material));
  const all = [], strips = [];
  azimuths.forEach((az) => {
    const root = new THREE.Bone();
    root.position.set(anchorRadius * Math.sin(az), top, anchorRadius * Math.cos(az) * ez);
    mesh.add(root);
    const list = [root];
    let parent = root;
    for (let k = 1; k < bones; k++) {
      const b = new THREE.Bone();
      b.position.set(0, -segLen, 0);
      parent.add(b);
      list.push(b);
      parent = b;
    }
    all.push(...list);
    strips.push({ chain: createChain({ segments: bones, segmentLength: segLen * scale, ...spring }), bones: list, root, restLocal: DOWN.clone(), last: null });
  });
  mesh.updateMatrixWorld(true);
  mesh.bind(new THREE.Skeleton(all));
  return { mesh, strips };
}

/**
 * 一缕硬的垂饰（辫子那样的一根）：一节弹簧骨，网格整个挂在骨头上跟着转。attach、end 都在 parent 的坐标里。
 */
export function rigidStrand(geometryFromOrigin, material, parent, attach, end, { scale = 1, spring = {} } = {}) {
  const bone = new THREE.Bone();
  bone.position.set(...attach);
  parent.add(bone);
  const m = shadowed(new THREE.Mesh(geometryFromOrigin, material));
  bone.add(m);
  const rest = new THREE.Vector3(end[0] - attach[0], end[1] - attach[1], end[2] - attach[2]);
  const length = rest.length();
  rest.normalize();
  return { chain: createChain({ segments: 1, segmentLength: length * scale, ...spring }), bones: [bone], root: bone, restLocal: rest, last: null };
}

// 头部：在球面上用高斯隆起和凹陷雕出眉弓、眼窝、鼻梁、鼻尖、鼻翼、颧骨、人中、上下唇、嘴缝、下巴、下颌（打样的做法，M3 加细）。
// 参数（characters.mjs 的 spec.face）：age 年纪（old 加法令纹、眼袋、瘦脸颊）、jaw 下颌的方正（0.2 柔 … 0.8 方）、
// nose 鼻子大小、female 女性（眉弓浅、下巴小、颧骨高）、beard 有胡子时下巴不另外隆起。同一组参数永远雕出同一个头。
const headCache = new Map();
export function sculptHead(face = {}) {
  const f = { age: 'adult', jaw: 0.5, nose: 1, female: false, beard: false, ...(typeof face === 'boolean' ? { beard: face } : face) };
  const key = JSON.stringify(f);
  if (headCache.has(key)) return headCache.get(key);
  const g = new THREE.SphereGeometry(1, 64, 48);
  const pos = g.attributes.position, v = new THREE.Vector3();
  const old = f.age === 'old', fem = !!f.female, brow = fem ? 0.6 : 1;
  const features = [
    [[0.3, 0.3, 0.92], 0.14, 0.045 * brow], [[-0.3, 0.3, 0.92], 0.14, 0.045 * brow],   // 眉弓
    [[0, 0.24, 0.98], 0.16, 0.02],                                                       // 眉心
    // 眼窝和眼球：五官是画上去的（face.mjs），眼睛要落在一个微微隆起的眼球上，眼球坐在浅一些的眼窝里——
    // 原来 −0.085 的深坑配上独立的眼球网格，换成贴图后整只眼睛沉在阴影里
    [[0.36, 0.14, 0.93], 0.12, -0.05], [[-0.36, 0.14, 0.93], 0.12, -0.05],               // 眼窝
    [[0.36, 0.14, 0.93], 0.06, 0.03], [[-0.36, 0.14, 0.93], 0.06, 0.03],                 // 眼球
    [[0, 0.1, 1.0], 0.06, 0.06 * f.nose], [[0, -0.02, 1.02], 0.07, 0.11 * f.nose],      // 鼻梁、鼻尖
    [[0, -0.11, 1.0], 0.055, 0.1 * f.nose],
    [[0.1, -0.15, 0.97], 0.045, 0.045 * f.nose], [[-0.1, -0.15, 0.97], 0.045, 0.045 * f.nose],   // 鼻翼
    [[0.52, -0.05, 0.78], 0.17, fem ? 0.04 : 0.028], [[-0.52, -0.05, 0.78], 0.17, fem ? 0.04 : 0.028],   // 颧骨
    [[0, -0.29, 1.0], 0.02, -0.012],                                                     // 人中
    [[0, -0.345, 0.98], 0.045, 0.028], [[0, -0.455, 0.97], 0.05, 0.03],                 // 上唇、下唇
    [[0, -0.4, 1.0], 0.022, -0.02],                                                      // 嘴缝
    [[0, -0.66, 0.78], 0.12, f.beard ? 0.0 : fem ? 0.035 : 0.055],                       // 下巴
  ];
  if (old) {
    features.push([[0.2, -0.22, 0.95], 0.05, -0.02], [[-0.2, -0.22, 0.95], 0.05, -0.02]);   // 法令纹
    features.push([[0.36, 0.0, 0.92], 0.05, 0.015], [[-0.36, 0.0, 0.92], 0.05, 0.015]);     // 眼袋
    features.push([[0.5, -0.32, 0.72], 0.14, -0.03], [[-0.5, -0.32, 0.72], 0.14, -0.03]);   // 瘦脸颊
  }
  const centers = features.map(([c, s, a]) => [new THREE.Vector3(...c).normalize(), s, a]);
  const taper = 0.3 - f.jaw * 0.16;   // 下颌：方正的脸收得少
  for (let i = 0; i < pos.count; i++) {
    v.fromBufferAttribute(pos, i);
    let d = 0;
    for (const [c, s, a] of centers) d += a * Math.exp(-v.distanceToSquared(c) / (2 * s * s));
    const jaw = v.y < 0 ? 1 - taper * Math.pow(-v.y, 1.5) : 1;
    v.multiplyScalar(1 + d);
    v.x *= jaw;
    if (v.z < 0) v.z *= 1.07;                       // 后脑
    if (v.y > 0.35 && v.z > 0.5) v.z *= 0.975;      // 额头略平
    if (Math.abs(v.x) > 0.8 && v.y > 0.1) v.x *= 0.985;   // 太阳穴
    pos.setXYZ(i, v.x, v.y, v.z);
  }
  g.computeVertexNormals();
  g.scale(0.082, 0.11, 0.095);
  headCache.set(key, g);
  return g;
}

/**
 * 五官与耳朵：眼、眉、鼻翼、唇是一张画在脸上的贴图（face.mjs——SVG 路径画到画布上，沿 z 投影到头网格正面那一片，
 * 抬起 0.3 毫米），起伏全是雕出来的头本身的；耳朵仍是网格。2026-10-12 之前眼睛是两个球加一枚扁球虹膜，
 * 眉毛、嘴唇各是一枚扁椭球——近看就是几根线条勾出来的脸（用户原话：显得很呆笨，没有活人的细腻感）。
 */
function faceDetails(M, spec, headGeometry, skin) {
  const out = [];
  const decal = new THREE.Mesh(faceDecalGeometry(headGeometry), M[faceMaterialKey(lookFor(spec))]);
  decal.name = 'face';
  decal.receiveShadow = true;
  decal.renderOrder = 1;
  out.push(decal);
  for (const sx of [-1, 1]) {
    const ear = mesh(blobGeometry([sx * 0.082, 0.002, -0.004], [0.011, 0.026, 0.017], [0, sx * 0.3, 0]), skin);
    ear.name = 'ear';
    out.push(ear);
  }
  return out;
}

/**
 * 手（M3 近景）：手掌、四根两节的手指、拇指。返回 { group, curl(c) }——curl 把手指握起来（0 伸直、1 攥紧），
 * 拿着东西的手握得紧，空手微曲。
 * 手的坐标系（手腕在原点）：y 沿前臂（手指朝 −y），z 是掌心的朝向（手指往 +z 弯），x 横过指根；拇指在 +sx 那一侧。
 * 攥紧的拳眼（GRIP.fist）是一条沿 x 的孔——锤柄、凿子、绳子从这里穿过去（M3 第三轮：第一版的锤柄顺着前臂、锤头贴着手腕）。
 */
function buildHand(sx, skin) {
  const group = new THREE.Group();
  group.add(mesh(blobGeometry([0, -0.03, 0.006], [0.023, 0.036, 0.011]), skin));
  const fingers = [];
  const finger = (x, y, z, len1, len2, r1, r2, spread = 0) => {
    const base = new THREE.Group();
    base.position.set(x, y, z);
    base.rotation.z = spread;
    base.add(mesh(limbGeometry([0, 0, 0], [0, -len1, 0], r1), skin));
    const mid = new THREE.Group();
    mid.position.set(0, -len1, 0);
    mid.add(mesh(limbGeometry([0, 0, 0], [0, -len2, 0], r2), skin));
    base.add(mid);
    group.add(base);
    return { base, mid };
  };
  // 从小指（−sx，离拇指最远）数到食指（+sx，挨着拇指）：中指最长、小指最短。第一版反了——最短的那根挨着拇指，是一只左右颠倒的手
  const LENGTHS = [0.84, 1.0, 1.06, 0.98];
  for (let i = 0; i < 4; i++) {
    const x = sx * (-0.016 + i * 0.0107);
    const long = LENGTHS[i];
    fingers.push(finger(x, -0.062, 0.006, 0.024 * long, 0.019 * long, 0.0052, 0.0044, sx * (i - 1.5) * 0.022));
  }
  const thumb = finger(sx * 0.024, -0.034, 0.012, 0.024, 0.02, 0.0062, 0.0054, sx * -0.95);
  thumb.base.rotation.x = -0.45;
  return {
    group,
    curl(c) {
      for (const f of fingers) { f.base.rotation.x = -c * 1.25; f.mid.rotation.x = -c * 1.35; }
      thumb.base.rotation.x = -0.45 - c * 0.5;
      thumb.mid.rotation.x = -c * 0.9;
    },
  };
}

/**
 * 腿的轮廓（2026-10-12，用户：雅典娜的小腿太粗）。之前大腿、小腿各是一根等粗的胶囊，小腿肚另贴一枚椭球——
 * 脚踝和膝下一样粗（半径 4.4 厘米），整条小腿是一根管子。现在每条腿段是一圈圈椭圆放样出来的：
 * 小腿肚在上三分之一、偏后，往下收到细的脚踝（女子 2.2 厘米、男子 2.7 厘米），踝骨处再略鼓一点；大腿从髋往膝收。
 * s 是腿段上的位置（0 在上端关节、1 在下端关节），返回这一圈的 x、z 半径和中心往后的偏移（米，build 之前）。
 */
export const LEG_PROFILES = Object.freeze({
  female: {
    thigh: { rx: [[0, 0.064], [0.3, 0.059], [0.75, 0.047], [1, 0.041]], rz: [[0, 0.064], [0.3, 0.06], [0.75, 0.046], [1, 0.04]], zc: [[0, 0], [0.4, 0.004], [1, 0]] },
    shin: { rx: [[0, 0.039], [0.1, 0.037], [0.3, 0.0375], [0.58, 0.029], [0.84, 0.0205], [0.96, 0.0215], [1.03, 0.022]], rz: [[0, 0.04], [0.1, 0.04], [0.3, 0.0465], [0.58, 0.032], [0.84, 0.0215], [0.96, 0.024], [1.03, 0.025]], zc: [[0, 0], [0.3, -0.011], [0.62, -0.005], [1, -0.006]] },
    knee: [0.041, 0.044, 0.042],
  },
  male: {
    thigh: { rx: [[0, 0.066], [0.4, 0.062], [0.8, 0.052], [1, 0.047]], rz: [[0, 0.066], [0.4, 0.064], [0.8, 0.051], [1, 0.046]], zc: [[0, 0], [0.4, 0.005], [1, 0]] },
    shin: { rx: [[0, 0.045], [0.1, 0.043], [0.3, 0.045], [0.6, 0.035], [0.84, 0.025], [0.96, 0.026], [1.03, 0.027]], rz: [[0, 0.045], [0.1, 0.045], [0.3, 0.052], [0.6, 0.037], [0.84, 0.026], [0.96, 0.029], [1.03, 0.03]], zc: [[0, 0], [0.3, -0.01], [0.62, -0.005], [1, -0.007]] },
    knee: [0.048, 0.05, 0.048],
  },
});

/**
 * 手臂和脖子的轮廓（2026-10-12，用户：雅典娜的手臂比腿还粗，所有角色的脖子都像电线杆一样）。
 * 之前上臂是一根 4.1 厘米、前臂一根 3.3 厘米的等粗胶囊——小腿收细以后，胳膊比腿还粗；脖子是一根 4.7 厘米的直立圆柱，
 * 上下一样粗、笔直地插在肩上。现在都按 legSection 的办法放样：
 * 上臂在三角肌处最粗、往肘收，前臂靠肘的那一截有肌肉、往手腕收细，手腕是扁的（x 宽、z 薄）；
 * 脖子在下颌下最细（女子宽 9.5 厘米、男子 10.4 厘米，头宽的 63%——第一版的圆柱是 50–57%，又上下一样粗），往下张开接到斜方肌（底下藏在衣领里），
 * 整根向前倾（NECK_LEAN）；头也放低了 3 厘米，露出来的一截短了。
 */
export const ARM_PROFILES = Object.freeze({
  female: {
    upper: { rx: [[0, 0.035], [0.18, 0.034], [0.5, 0.029], [0.85, 0.0245], [1.02, 0.024]], rz: [[0, 0.036], [0.18, 0.033], [0.5, 0.03], [0.85, 0.025], [1.02, 0.024]], zc: [[0, 0], [1, 0]] },
    fore: { rx: [[-0.03, 0.024], [0.18, 0.0265], [0.55, 0.022], [0.92, 0.0185], [1.02, 0.0185]], rz: [[-0.03, 0.024], [0.18, 0.025], [0.55, 0.0195], [0.92, 0.0135], [1.02, 0.013]], zc: [[0, 0], [1, 0]] },
  },
  male: {
    upper: { rx: [[0, 0.0455], [0.18, 0.0455], [0.5, 0.04], [0.85, 0.032], [1.02, 0.031]], rz: [[0, 0.0455], [0.18, 0.043], [0.5, 0.041], [0.85, 0.032], [1.02, 0.031]], zc: [[0, 0], [1, 0]] },
    fore: { rx: [[-0.03, 0.031], [0.18, 0.036], [0.55, 0.03], [0.92, 0.0235], [1.02, 0.0235]], rz: [[-0.03, 0.031], [0.18, 0.033], [0.55, 0.0265], [0.92, 0.017], [1.02, 0.0165]], zc: [[0, 0], [1, 0]] },
  },
});
/** 脖子：s 从下颌下（0）到衣领下的斜方肌（1）。 */
export const NECK_PROFILES = Object.freeze({
  female: { rx: [[0, 0.049], [0.35, 0.0475], [0.62, 0.049], [0.84, 0.06], [1, 0.078]], rz: [[0, 0.045], [0.35, 0.042], [0.62, 0.045], [0.84, 0.049], [1, 0.056]], zc: [[0, 0], [1, 0]] },
  male: { rx: [[0, 0.054], [0.35, 0.052], [0.62, 0.055], [0.84, 0.068], [1, 0.09]], rz: [[0, 0.05], [0.35, 0.048], [0.62, 0.05], [0.84, 0.056], [1, 0.065]], zc: [[0, 0], [1, 0]] },
});
/** 脖子往前倾的斜率（每往下一米往后退多少）：底在衣领里、顶在下颌后面，侧面看是斜的，不是一根立柱。 */
export const NECK_LEAN = 0.26;

/** 沿 a → b 放样一段手臂（taperedLimb 是沿 −y 的，这里转到 a → b 的方向上）。 */
export function limbAlong(section, a, b, opts = {}) {
  const A = new THREE.Vector3(...a), dir = new THREE.Vector3(...b).sub(A);
  const g = taperedLimb(section, dir.length(), opts);
  g.applyQuaternion(new THREE.Quaternion().setFromUnitVectors(new THREE.Vector3(0, -1, 0), dir.normalize()));
  g.translate(A.x, A.y, A.z);
  return g;
}

/** 脖子：顶在 (0, topY, topZ)，往下 length，按 NECK_LEAN 往后斜。 */
export function neckGeometry(section, topY, topZ, length) {
  const g = taperedLimb(section, length, { rings: 16, segs: 24 });
  const p = g.attributes.position;
  for (let i = 0; i < p.count; i++) p.setXYZ(i, p.getX(i), topY + p.getY(i), topZ + p.getZ(i) + p.getY(i) * NECK_LEAN);
  g.computeVertexNormals();
  return g;
}

/** 小腿的长度：膝关节到踝关节（脚的原点）。 */
export const SHIN_LENGTH = 0.44;

/** 腿段的轮廓函数：s → { rx, rz, zc }（乘过 build）。 */
export function legSection(profile, b = 1) {
  const rx = keyed(profile.rx), rz = keyed(profile.rz), zc = keyed(profile.zc);
  return (s) => ({ rx: rx(s) * b, rz: rz(s) * b, zc: zc(s) });
}

/** 放样一段腿：从 y = 0 往下到 y = −length，按 section(s) 的椭圆一圈圈连起来，两端封口。s 可以略超过 1（伸进脚里）。 */
export function taperedLimb(section, length, { s1 = 1, rings = 28, segs = 22 } = {}) {
  const pos = [], uv = [], idx = [];
  for (let i = 0; i <= rings; i++) {
    const s = (i / rings) * s1, c = section(s), y = -s * length;
    for (let j = 0; j < segs; j++) {
      const th = (j / segs) * Math.PI * 2;
      pos.push(Math.sin(th) * c.rx, y, c.zc + Math.cos(th) * c.rz);
      uv.push(j / segs, s);
    }
  }
  for (let i = 0; i < rings; i++) {
    for (let j = 0; j < segs; j++) {
      const a = i * segs + j, b2 = i * segs + ((j + 1) % segs), c = a + segs, d = b2 + segs;
      idx.push(a, c, b2, b2, c, d);
    }
  }
  for (const [ring, flip] of [[0, true], [rings, false]]) {
    const s = (ring / rings) * s1, c = section(s);
    const center = pos.length / 3;
    pos.push(0, -s * length, c.zc); uv.push(0.5, s);
    for (let j = 0; j < segs; j++) {
      const a = ring * segs + j, b2 = ring * segs + ((j + 1) % segs);
      if (flip) idx.push(center, a, b2); else idx.push(center, b2, a);
    }
  }
  const g = new THREE.BufferGeometry();
  g.setAttribute('position', new THREE.Float32BufferAttribute(pos, 3));
  g.setAttribute('uv', new THREE.Float32BufferAttribute(uv, 2));
  g.setIndex(idx);
  g.computeVertexNormals();
  return g;
}

/** 脚（M3 近景）：脚背、脚跟、五个脚趾、凉鞋的鞋底与两道皮带。原点在踝关节（膝关节下 0.44）。 */
function buildFoot(sx, skin, leather, ankleSection = null) {
  const g = new THREE.Group();
  g.add(mesh(blobGeometry([0, -0.012, 0.045], [0.034, 0.026, 0.08]), skin));
  g.add(mesh(blobGeometry([0, -0.01, -0.012], [0.028, 0.03, 0.028]), skin));
  for (let i = 0; i < 5; i++) {
    const x = sx * (-0.022 + i * 0.011), len = i === 0 ? 0.03 : 0.026 - i * 0.002, r = i === 0 ? 0.0075 : 0.0058;
    g.add(mesh(limbGeometry([x, -0.026, 0.118], [x, -0.028, 0.118 + len], r), skin));
  }
  const sole = mesh(new THREE.BoxGeometry(0.074, 0.011, 0.2), leather);
  sole.position.set(0, -0.0365, 0.06);
  g.add(sole);
  const strap = mesh(new THREE.BoxGeometry(0.076, 0.005, 0.014), leather);
  strap.position.set(0, -0.004, 0.072);
  strap.rotation.x = 0.45;
  g.add(strap);
  // 脚踝的皮带：贴着这一截小腿的粗细（小腿收细以后，原来 3.1 厘米的箍离女子的脚踝有一厘米空）
  const c = ankleSection ? ankleSection(1 - 0.012 / SHIN_LENGTH) : { rx: 0.0275, rz: 0.0275, zc: 0 };
  const ankle = mesh(new THREE.TorusGeometry(1, 0.0035 / Math.max(c.rx, c.rz), 6, 20), leather);
  ankle.rotation.x = Math.PI / 2;
  ankle.scale.set(c.rx + 0.003, c.rz + 0.003, c.rx + 0.003);
  ankle.position.set(0, 0.012, c.zc);
  g.add(ankle);
  return g;
}

/**
 * 图勒尼亚凉鞋（雅典娜）：木底、金边、金带交叉过脚背、绕过脚跟，缠到小腿的那一段在 calfLacing（挂在膝关节上，和脚一起动）。
 * 原点在踝关节，和 buildFoot 一样。
 *
 * 第二版（2026-10-12，用户：脚掌叠在厚底鞋上很僵硬，整体没有一点美丽飒爽的气质）。第一版是一枚平放的椭球脚
 * 搁在一块 3.2 厘米厚、上下一样平的木块上，脚和鞋各是各的，像一只脚踩着一块砖。现在：
 * - 鞋底是坡跟：脚跟 2.2 厘米、脚尖 0.9 厘米，脚背顺着它微微前倾——像一只低跟鞋，脚有了足弓和走势；
 *   鞋底的轮廓是脚形（脚跟窄、前掌宽、脚尖圆），不再是椭圆。
 * - 脚是放样出来的一整只（footGeometry）：脚跟、足弓、前掌、脚背的隆起连成一体，五个脚趾由长到短、趾尖略收。
 * - 金带用射线贴着脚面走（strapOver），不再是几根悬空的直管。
 * TYRRHENIAN_LIFT 是比普通凉鞋多出来的高度（坡跟脚跟处的厚度减去普通鞋底），身体整个抬高这么多。
 */
export const TYRRHENIAN_LIFT = 0.016;
const SANDAL_BOTTOM = -0.0425 - TYRRHENIAN_LIFT;
/** 坡跟的厚度（米）：z 是脚的长轴（脚跟 −0.045 → 脚尖 0.165）。 */
const wedge = keyed([[-0.05, 0.022], [0.02, 0.0205], [0.09, 0.0115], [0.17, 0.009]]);
/** 鞋底在 z 处的半宽：脚跟窄、足弓略收、前掌最宽。 */
const soleHalf = keyed([[-0.05, 0.024], [0.0, 0.0265], [0.04, 0.026], [0.1, 0.0355], [0.135, 0.034], [0.17, 0.028]]);
const SOLE_Z = [-0.05, 0.168];
export const soleTop = (z) => SANDAL_BOTTOM + wedge(z);

/**
 * 一只脚（踝关节为原点、脚尖朝 +z）：一圈圈放样——每一圈是一个上圆下平的截面，宽按 soleHalf 收窄一点，
 * 底落在 soleTop 上，顶是脚背（踝前最高，往脚趾顺下去）。
 */
function footGeometry(sx) {
  const top = keyed([[-0.05, -0.012], [-0.025, 0.004], [0.0, 0.012], [0.03, 0.002], [0.07, -0.016], [0.105, -0.028], [0.125, -0.033]]);
  const rings = 26, segs = 20, pos = [], idx = [];
  const z0 = -0.047, z1 = 0.127;
  for (let i = 0; i <= rings; i++) {
    const f = i / rings, z = lerp(z0, z1, f);
    // 两端收拢成圆头（脚跟、前掌的边）
    const end = Math.sqrt(Math.max(0, 1 - Math.pow(Math.abs(f - 0.5) * 2, 6)));
    const w = soleHalf(z) * 0.86 * end, yb = soleTop(z) + 0.0008, yt = lerp(yb, top(z), 0.15 + 0.85 * end);
    // 内侧（大脚趾那边，−sx）的足弓抬起来：脚底内侧不着地
    const arch = Math.max(0, 1 - Math.abs(z - 0.035) / 0.05) * 0.006;
    for (let j = 0; j < segs; j++) {
      const th = (j / segs) * Math.PI * 2, c = Math.cos(th), sn = Math.sin(th);
      const x = sn * w;
      const inner = Math.max(0, -sx * sn);
      const yMid = (yb + yt) / 2 + 0.002, h = (yt - yb) / 2;
      let y = c > 0 ? yMid + c * (h + 0.002) : yMid + c * h * (1 - 0.65 * sn * sn);   // 下半截压平成脚底
      if (c < 0) y += arch * inner;
      pos.push(x, Math.max(y, yb), z);
    }
  }
  for (let i = 0; i < rings; i++) {
    for (let j = 0; j < segs; j++) {
      const a = i * segs + j, b2 = i * segs + ((j + 1) % segs), c = a + segs, d = b2 + segs;
      idx.push(a, c, b2, b2, c, d);
    }
  }
  for (const [ring, flip] of [[0, true], [rings, false]]) {
    let cx = 0, cy = 0, cz = 0;
    for (let j = 0; j < segs; j++) { cx += pos[(ring * segs + j) * 3]; cy += pos[(ring * segs + j) * 3 + 1]; cz += pos[(ring * segs + j) * 3 + 2]; }
    const center = pos.length / 3;
    pos.push(cx / segs, cy / segs, cz / segs + (flip ? 0.004 : -0.004));
    for (let j = 0; j < segs; j++) {
      const a = ring * segs + j, b2 = ring * segs + ((j + 1) % segs);
      if (flip) idx.push(center, a, b2); else idx.push(center, b2, a);
    }
  }
  const g = new THREE.BufferGeometry();
  g.setAttribute('position', new THREE.Float32BufferAttribute(pos, 3));
  g.setIndex(idx);
  g.computeVertexNormals();
  return g;
}

/** 坡跟鞋底：脚形轮廓（soleHalf）的柱体，底平、顶按 wedge 斜下去，脚尖微翘。 */
function wedgeSole() {
  const g = new THREE.CylinderGeometry(1, 1, 1, 40, 1);
  const p = g.attributes.position, v = new THREE.Vector3();
  for (let i = 0; i < p.count; i++) {
    v.fromBufferAttribute(p, i);
    const z = lerp(SOLE_Z[0], SOLE_Z[1], (v.z + 1) / 2);
    const x = v.x * soleHalf(z) * 1.04;
    const up = v.y > 0;
    const toe = Math.max(0, (z - 0.12) / 0.05);
    const y = (up ? soleTop(z) : SANDAL_BOTTOM + 0.004 * toe * toe) + 0.004 * toe * toe;
    p.setXYZ(i, x, y, z);
  }
  g.computeVertexNormals();
  return g;
}

/** 鞋底上沿的一圈（金边沿它走）：θ 处的点。 */
function soleRim(th, out = 0.0006) {
  const z = lerp(SOLE_Z[0], SOLE_Z[1], (Math.cos(th) + 1) / 2);
  const toe = Math.max(0, (z - 0.12) / 0.05);
  return new THREE.Vector3(Math.sin(th) * (soleHalf(z) * 1.04 + out), soleTop(z) + 0.004 * toe * toe, z);
}

function buildTyrrhenianFoot(sx, skin, wood, gold) {
  const g = new THREE.Group();
  const footMesh = mesh(footGeometry(sx), skin);
  footMesh.name = 'foot';
  g.add(footMesh);
  // 脚趾：大脚趾在内侧（−sx），由长到短，往外一个个退后一点，趾尖略收、贴着鞋面
  for (let i = 0; i < 5; i++) {
    const x = sx * (-0.0195 + i * 0.0096), z0 = 0.112 - i * 0.0045 - (i > 2 ? 0.003 : 0);
    const len = i === 0 ? 0.03 : 0.025 - i * 0.0022, r = i === 0 ? 0.0068 : 0.0054 - i * 0.0004;
    const z1 = z0 + len;
    g.add(mesh(limbGeometry([x, soleTop(z0) + r + 0.0012, z0], [x + sx * i * 0.0012, soleTop(z1) + r * 0.85, z1], r), skin));
  }
  g.add(mesh(wedgeSole(), wood));
  const rimPts = Array.from({ length: 64 }, (_, k) => soleRim((k / 64) * Math.PI * 2));
  g.add(mesh(new THREE.TubeGeometry(new THREE.CatmullRomCurve3(rimPts, true), 128, 0.0019, 5, true), gold));
  // 金带：贴着脚面走（射线从脚内部往外打，取最外面那一层），两端扎进鞋底边
  const ray = new THREE.Raycaster(), probe = new THREE.Mesh(footMesh.geometry, new THREE.MeshBasicMaterial({ side: THREE.DoubleSide }));
  probe.updateMatrixWorld(true);
  const onFoot = (z, side, lift = 0.0028) => {
    // side：−1 … 1 横过脚背（0 是正中最高处）。从脚里一条中线往外打
    const yc = soleTop(z) + 0.008, th = side * Math.PI * 0.5;
    const dir = new THREE.Vector3(Math.sin(th), Math.cos(th), 0).normalize();
    ray.set(new THREE.Vector3(0, yc, z), dir);
    const hits = ray.intersectObject(probe);
    const far = hits.length ? hits[hits.length - 1].point : new THREE.Vector3(0, yc, z);
    return far.addScaledVector(dir, lift);
  };
  const edge = (z, side) => new THREE.Vector3(side * soleHalf(z) * 1.0, soleTop(z) + 0.0015, z);
  const strap = (pts, r = 0.0022) => mesh(new THREE.TubeGeometry(new THREE.CatmullRomCurve3(pts), 40, r, 6, false), gold);
  // 过脚趾根的一道
  g.add(strap([edge(0.1, -1), onFoot(0.102, -0.75), onFoot(0.104, -0.35), onFoot(0.105, 0), onFoot(0.104, 0.35), onFoot(0.102, 0.75), edge(0.1, 1)]));
  // 脚背上交叉的两道：从前掌两边斜着上到踝前
  for (const s2 of [1, -1]) {
    g.add(strap([edge(0.075, s2), onFoot(0.066, s2 * 0.7), onFoot(0.05, s2 * 0.25), onFoot(0.034, -s2 * 0.2), onFoot(0.02, -s2 * 0.6), onFoot(0.008, -s2 * 0.9)]));
  }
  // 绕过脚跟的一道
  const heel = [];
  for (let k = 0; k <= 8; k++) {
    const th = lerp(-Math.PI * 0.62, Math.PI * 0.62, k / 8);
    heel.push(new THREE.Vector3(Math.sin(th) * 0.0262, soleTop(-0.03) + 0.012 + Math.cos(th) * 0.002, -0.012 - Math.cos(th) * 0.03));
  }
  g.add(strap(heel));
  return g;
}

/**
 * 缠到小腿的金带：从脚踝往上两道反向的螺旋交叉缠到小腿中段，脚踝和顶上各一道箍。
 * 贴着小腿（taperedLimb 放样的那根）的表面再往外 2.5 毫米——用的是同一个 section，所以小腿收细了，带子也跟着收。
 */
function calfLacing(section, gold, { from = -0.425, to = -0.24, turns = 1.75 } = {}) {
  const surface = (y, th) => {
    const c = section(-y / SHIN_LENGTH), off = 0.0025;
    return new THREE.Vector3(Math.sin(th) * (c.rx + off), y, c.zc + Math.cos(th) * (c.rz + off));
  };
  const parts = [];
  for (const dir of [1, -1]) {
    const pts = [];
    for (let k = 0; k <= 64; k++) {
      const f = k / 64, y = lerp(from, to, f);
      pts.push(surface(y, dir * f * turns * Math.PI * 2 + (dir > 0 ? 0 : Math.PI)));
    }
    parts.push(new THREE.TubeGeometry(new THREE.CatmullRomCurve3(pts), 128, 0.0021, 5, false));
  }
  const ring = (y, r) => {
    const pts = Array.from({ length: 24 }, (_, k) => surface(y, (k / 24) * Math.PI * 2));
    return new THREE.TubeGeometry(new THREE.CatmullRomCurve3(pts, true), 48, r, 5, true);
  };
  parts.push(ring(to, 0.0034), ring(to - 0.011, 0.002), ring(-SHIN_LENGTH + 0.012, 0.003));
  return mesh(merge(parts), gold);
}

/** 胡子：椭球按发丝方向揉出一缕缕的起伏，下沿参差；短须贴脸、长须垂到胸前。 */
function sculptBeard(len) {
  const g = new THREE.SphereGeometry(1, 28, 20);
  const pos = g.attributes.position, v = new THREE.Vector3();
  for (let i = 0; i < pos.count; i++) {
    v.fromBufferAttribute(pos, i);
    const around = Math.atan2(v.x, v.z);
    const strand = 0.5 + 0.5 * Math.sin(around * 11 + v.y * 3);
    const wisp = 0.5 + 0.5 * Math.sin(around * 23 - v.y * 5);
    let k = 1 + 0.06 * strand + 0.03 * wisp;
    if (v.y < -0.3) k *= 1 + 0.08 * (0.5 + 0.5 * Math.sin(around * 9 + 1.7)) * (-v.y - 0.3);   // 下沿参差
    if (v.z < 0) k *= 0.55;   // 贴着脸的那一面压扁
    v.multiplyScalar(k);
    pos.setXYZ(i, v.x, v.y, v.z);
  }
  g.computeVertexNormals();
  g.scale(0.066, 0.04 + len * 0.62, 0.05);
  g.translate(0, -0.072 - len * 0.42, 0.052);
  return g;
}

function sculptHair(kind = 'cap') {
  const g = kind === 'bun'
    ? new THREE.SphereGeometry(1, 24, 18)
    : new THREE.SphereGeometry(1, 48, 28, 0, Math.PI * 2, 0, Math.PI * (kind === 'short' ? 0.55 : 0.66));
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

/**
 * 发型（2026-10-12，用户：头发是贴着头皮的一层，雅典娜的头发有时破皮、看得到下面的头皮；每个角色要有自己的发型）。
 * 第一版的头发是另套的一个球（0.087 × 0.113 × 0.1），头却是雕过的——后脑放大了 7%、额头和太阳穴压平了，两样形状对不上，
 * 后脑和起伏处头皮就从头发里钻出来。现在头发就是这个人雕好的头（sculptHead）：每个顶点沿法线往外推——发区里推出
 * thick（再加头顶的蓬松 volume、后脑的厚度 backVolume、发丝 strands、波浪 waves、卷 curls，分缝处压下去 part），
 * 发区外往里收 HAIR_INSET 藏进头里。同一份顶点，所以发区里的头发处处在头皮之上（至少 HAIR_MIN）；发际线是那一圈斜着钻进头里的三角形。
 * 发区按顶点在单位球上的方向 u 定：发际线的高度（u.y）在前额 front、额角 temple、耳上 side、后颈 back 各给一个，按方位角插值；
 * bald 挖掉头顶（秃顶的人）。附件：发髻 bun、辫子 braids、披下来的长发 long（一片挂在弹簧骨上的头发，见 buildHair）。
 * 每个人物一种，名字就是人物的 Id（characters.mjs 的 hair）。
 */
export const HAIR_INSET = 0.003;
export const HAIR_MIN = 0.004;
export const HAIR_STYLES = Object.freeze({
  // 雅典娜：中分，波浪从两鬓往后梳、盖住耳朵上半，在后颈挽一个低髻（发带压在上面）
  athena: { front: 0.58, temple: 0.32, side: 0.0, back: -0.45, thick: 0.007, volume: 0.003, backVolume: 0.004, part: 0.004, strands: 20, strandAmp: 0.0022, waves: 6, waveAmp: 0.0028, bun: { at: [0, -0.052, -0.112], size: [0.048, 0.04, 0.042] } },
  // 你：中分的长发，两边垂过耳朵到下颌，后面一片披到背上（弹簧骨，走起来飘）
  player: { front: 0.52, temple: 0.24, side: -0.42, back: -0.8, thick: 0.009, volume: 0.005, backVolume: 0.007, part: 0.004, strands: 26, strandAmp: 0.0018, waves: 3, waveAmp: 0.002, long: { top: 0.035, length: 0.42, topRadius: 0.098, bottomRadius: 0.14, arc: 0.3 } },
  // 测绘员：帽子底下一圈短波浪，盖到耳上
  surveyor: { front: 0.42, temple: 0.2, side: 0.0, back: -0.4, thick: 0.007, volume: 0.002, backVolume: 0.003, strands: 14, strandAmp: 0.002, waves: 4, waveAmp: 0.003 },
  // 书记员：短而齐整，侧分，贴着头型
  scribe: { front: 0.5, temple: 0.3, side: 0.12, back: -0.35, thick: 0.0055, volume: 0.002, backVolume: 0.002, part: 0.003, partX: 0.35, strands: 30, strandAmp: 0.0012 },
  // 账房：老人，头顶秃了，两侧和后脑一圈灰白短发
  accountant: { front: 0.4, temple: 0.2, side: 0.05, back: -0.35, thick: 0.005, volume: 0, backVolume: 0.002, bald: { y: 0.42, z: -0.35 }, strands: 24, strandAmp: 0.001 },
  // 铁匠：剃得很短的平头（毡帽底下露一圈）
  smith: { front: 0.55, temple: 0.35, side: 0.2, back: -0.25, thick: 0.0045, volume: 0, backVolume: 0.001, curls: { amp: 0.0012, freq: 9 } },
  // 水手：乱糟糟的一头，被风吹得翘起来
  sailor: { front: 0.44, temple: 0.18, side: -0.08, back: -0.5, thick: 0.008, volume: 0.005, backVolume: 0.004, strands: 9, strandAmp: 0.005, curls: { amp: 0.003, freq: 5 } },
  // 图书管理员：老人，秃顶，两侧和后面的灰发留长、垂到后颈
  librarian: { front: 0.4, temple: 0.15, side: -0.15, back: -0.75, thick: 0.007, volume: 0, backVolume: 0.006, bald: { y: 0.35, z: -0.5 }, strands: 18, strandAmp: 0.0025, waves: 3, waveAmp: 0.002 },
  // 金色侍女：古风的两条辫子，头顶一个髻
  maiden: { front: 0.5, temple: 0.3, side: 0.0, back: -0.45, thick: 0.007, volume: 0.003, backVolume: 0.004, part: 0.003, strands: 16, strandAmp: 0.003, bun: { at: [0, 0.02, -0.112], size: [0.05, 0.045, 0.045] }, braids: true },
  // 传令官：浓密的卷发垂到后颈（帽子挂在背后，整头露着）
  herald: { front: 0.46, temple: 0.22, side: -0.2, back: -0.68, thick: 0.007, volume: 0.004, backVolume: 0.005, curls: { amp: 0.003, freq: 7 } },
  // 画师：头发全挽进发网（sakkos），前面露一道发际线
  painter: { front: 0.5, temple: 0.28, side: 0.05, back: -0.4, thick: 0.006, volume: 0.002, backVolume: 0.003, part: 0.003, strands: 22, strandAmp: 0.0015 },
  // 工匠：紧密的短卷发
  artisan: { front: 0.44, temple: 0.25, side: 0.05, back: -0.4, thick: 0.007, volume: 0.003, backVolume: 0.003, curls: { amp: 0.003, freq: 11 } },
  // 女祭司：中分，两鬓的波浪往后拢进面纱（不挽髻：面纱从头顶披下来，髻会从面纱后面顶出一个包）
  priestess: { front: 0.48, temple: 0.22, side: -0.05, back: -0.45, thick: 0.007, volume: 0.003, backVolume: 0.004, part: 0.004, strands: 18, strandAmp: 0.002, waves: 4, waveAmp: 0.002 },
});

/** 方向 u（单位球）在发区里有多少（0 不在、1 在）。 */
export function hairMask(style, u) {
  const line = hairline(style, u);
  let m = smooth(line - 0.05, line + 0.05, u.y);
  if (style.bald) m *= 1 - smooth(style.bald.y - 0.05, style.bald.y + 0.05, u.y) * smooth(style.bald.z - 0.12, style.bald.z + 0.12, u.z);
  return m;
}

/** 发际线的高度（单位球的 u.y）在方向 u 的方位角上。 */
function hairline(style, u) {
  const az = Math.abs(Math.atan2(u.x, u.z));
  return keyed([[0, style.front], [0.85, style.temple], [Math.PI / 2, style.side], [Math.PI, style.back]])(az);
}

/** 方向 u 处头发的厚度（发区里，米）。发际线上薄、往里渐厚：第一版一出发际线就是满厚，额前一道陡坎，像戴了顶头盔。 */
function hairThickness(style, u) {
  const pole = HAIR_POLE;
  const along = Math.acos(Math.max(-1, Math.min(1, u.dot(pole))));
  const w = u.clone().addScaledVector(pole, -u.dot(pole));
  const around = Math.atan2(w.dot(HAIR_E2), w.dot(HAIR_E1));
  let t = style.thick + (style.volume ?? 0) * Math.pow(Math.max(0, u.y), 1.5) + (style.backVolume ?? 0) * Math.max(0, -u.z);
  if (style.strands) t += style.strandAmp * Math.abs(Math.sin(around * style.strands + 1.4 * Math.sin(along * 7)));
  if (style.waves) t += style.waveAmp * (0.5 + 0.5 * Math.sin(along * style.waves * 2 + around * 2));
  if (style.curls) {
    const f = style.curls.freq;
    t += style.curls.amp * (0.5 + 0.5 * Math.sin(f * u.x * 7.1 + 1.3) * Math.sin(f * u.y * 6.3 + 0.7) * Math.sin(f * u.z * 5.9 + 2.1));
  }
  if (style.part) {
    // 分缝：从额头沿头顶往后的一道浅沟（partX 是侧分，偏到一边）
    const x = u.x - (style.partX ?? 0) * Math.max(0, u.y);
    t -= style.part * Math.exp(-(x * x) / (2 * 0.035 * 0.035)) * smooth(0.35, 0.55, u.y) * smooth(-0.35, -0.05, u.z);
  }
  const line = hairline(style, u);
  return HAIR_MIN + Math.max(0, t - HAIR_MIN) * (0.25 + 0.75 * smooth(line, line + 0.35, u.y));
}
const HAIR_POLE = new THREE.Vector3(0, 0.75, 0.66).normalize();
const HAIR_E1 = new THREE.Vector3(1, 0, 0);
const HAIR_E2 = new THREE.Vector3().crossVectors(HAIR_POLE, HAIR_E1).normalize();

/** 雕出来的头的三个半径（sculptHead 最后那一下缩放）：顶点除以它、归一化，就是它在单位球上的方向。 */
export const HEAD_RADII = Object.freeze([0.082, 0.11, 0.095]);
/** 没有五官的头（金色侍女）：一个椭球。 */
const FACELESS_RADII = Object.freeze([0.078, 0.104, 0.09]);

/**
 * 一种发型的头发外壳：这个人实际的头（head：雕好的头或无脸的椭球）沿法线外推（发区里）或内收（发区外）。
 * 返回几何与 offsets（每个顶点推了多少，测试用；顶点和 head 一一对应）。
 */
export function sculptHairShell(head, radii, style) {
  const g = head.clone();
  const pos = g.attributes.position;
  const u = new THREE.Vector3(), n = new THREE.Vector3();
  const offsets = new Float32Array(pos.count);
  for (let i = 0; i < pos.count; i++) {
    u.set(pos.getX(i) / radii[0], pos.getY(i) / radii[1], pos.getZ(i) / radii[2]).normalize();
    const m = hairMask(style, u);
    const t = m * hairThickness(style, u) - (1 - m) * HAIR_INSET;
    offsets[i] = t;
    // 往外推的方向用椭球的法线（位置的函数），不用网格的顶点法线：球面接缝两侧的重复顶点法线不同，推出去裂开一道缝（雅典娜的后脑）
    n.set(pos.getX(i) / (radii[0] * radii[0]), pos.getY(i) / (radii[1] * radii[1]), pos.getZ(i) / (radii[2] * radii[2])).normalize();
    pos.setXYZ(i, pos.getX(i) + n.x * t, pos.getY(i) + n.y * t, pos.getZ(i) + n.z * t);
  }
  g.computeVertexNormals();
  return { geometry: g, offsets };
}

const doubleSided = new WeakMap();
/** 头发的双面材质（披下来的那一片是一张面，背面也要画）。 */
function twoSided(material) {
  if (!doubleSided.has(material)) { const m = material.clone(); m.side = THREE.DoubleSide; doubleSided.set(material, m); }
  return doubleSided.get(material);
}

/**
 * 头发挂到头关节上：外壳、发髻、辫子、披下来的长发（长发和辫子挂在弹簧骨上，推进 strips）。
 * @returns {number} 发带那个高度的头发厚度（米）——发带、头冠按它往外放一圈，压在头发上而不是埋进去
 */
function buildHair(spec, headGeometry, hairMat, head, strips, scale) {
  const style = HAIR_STYLES[spec.hair];
  if (!style) return headGeometry;
  const shell = mesh(sculptHairShell(headGeometry, spec.faceless ? FACELESS_RADII : HEAD_RADII, style).geometry, hairMat);
  shell.name = 'hair';
  head.add(shell);
  // 头发盖过耳朵（发际线在耳朵下面）就不放耳朵：耳朵比头发厚，会从头发里戳出一个肉色的尖
  if (style.side <= -0.2) for (const ear of head.children.filter((o) => o.name === 'ear')) head.remove(ear);
  if (style.bun) {
    const bun = mesh(sculptHair('bun'), hairMat);
    bun.name = 'hairBun';
    bun.scale.set(...style.bun.size); bun.position.set(...style.bun.at);
    head.add(bun);
  }
  if (style.braids) {
    // 两条辫子：各挂在一节弹簧骨上，走起来甩、停下来晃（M3）
    for (const sx of [-1, 1]) {
      const attach = [sx * 0.07, -0.06, -0.03], end = [sx * 0.1, -0.3, 0.02];
      const rel = [end[0] - attach[0], end[1] - attach[1], end[2] - attach[2]];
      strips.push(rigidStrand(limbGeometry([0, 0, 0], rel, 0.016), hairMat, head, attach, end, { scale, spring: { stiffness: 2.4, drag: 0.3, gravity: 1.0, maxAngle: 1.1, inertia: 0.55, wind: 0.5, radius: 0.016 } }));
    }
  }
  if (style.long) {
    // 披下来的长发：脑后半圈一片（像面纱那样），顶上埋进头发外壳里，下面披到背上；四条三节的弹簧骨——走、跑、转身、风里会飘
    // 上沿收在头发外壳里面（比后脑窄、比耳朵高），往下才放宽：第一版上沿比头宽，背后看是一道横杆挂着一片帘子，正面看两边从脸旁伸出来
    const L = style.long, thStart = Math.PI * (1 - L.arc), thEnd = Math.PI * (1 + L.arc);
    const sheet = clothStrip(drape({
      y0: L.top - L.length, y1: L.top, rings: 14, segs: 32, ez: 0.95, thStart, thEnd,
      radius: (t) => lerp(L.bottomRadius, L.topRadius, Math.pow(t, 0.45)),
      folds: [{ n: 26, amp: 0.004, ph: 0.6, wobble: 0.4, wf: 2.4 }, { n: 9, amp: 0.006, ph: 1.9, wobble: 0.3, wf: 1.6 }],
      growth: (t) => 1 - 0.6 * t,
      hem: { amp: 0.02, n: 11, ph: 0.3 },
    }), twoSided(hairMat), {
      chains: 4, bones: 3, top: L.top, bottom: L.top - L.length, anchorRadius: L.topRadius * 0.9, ez: 0.95, thStart, thEnd, scale,
      // 头发比面纱重：身体的位移只传四分之一给它、往回拉得更快、更吃重力——第一版照着面纱调（惯性 0.6），你走路（6 米/秒）时
      // 头发偏 43°、跑起来 60° 顶在最大偏角上，一直横着飞。现在走 17°、跑 28°（Node 里量的，tools.test.mjs 把住）
      spring: { stiffness: 3.4, drag: 0.4, gravity: 1.6, maxAngle: 0.8, inertia: 0.25, wind: 0.5, radius: 0.03 },
    });
    sheet.mesh.name = 'hairLong';
    head.add(sheet.mesh);
    strips.push(...sheet.strips);
  }
  return shell.geometry;
}

/**
 * 贴着头（或头发）的一圈带子：发带、头带、头冠、花环。带子在一个过 center、绕 x 轴倾斜 tilt 的平面上（tilt 正：前高后低），
 * 每个方向从 center 往外量到 surface 最远的那一点，带子的中心线放在那里往外 lift——压在头发上，不埋进去也不浮着
 * （第一版是固定半径的圆环：头发换成按头外推的一层之后，雅典娜的金发带整个埋进了头发里）。
 */
export function bandOnHead(surface, { center = [0, 0, 0], tilt = 0, tube = 0.004, lift = 0.5, scale = [1, 1, 1], segments = 48 }) {
  const c = new THREE.Vector3(...center);
  const n = new THREE.Vector3(0, Math.cos(tilt), -Math.sin(tilt));
  const e1 = new THREE.Vector3(1, 0, 0), e2 = new THREE.Vector3().crossVectors(n, e1).normalize();
  const pos = surface.attributes.position, v = new THREE.Vector3(), dir = new THREE.Vector3();
  const pts = [];
  for (let k = 0; k < segments; k++) {
    const phi = (k / segments) * Math.PI * 2;
    dir.copy(e1).multiplyScalar(Math.cos(phi)).addScaledVector(e2, Math.sin(phi));
    // 平面里这个方向上的表面：离平面不到 6 毫米、方向差不到 0.12 弧度的顶点里，沿这个方向伸得最远的
    let reach = 0;
    for (let i = 0; i < pos.count; i++) {
      v.fromBufferAttribute(pos, i).sub(c);
      if (Math.abs(v.dot(n)) > 0.006) continue;
      const along = v.dot(dir);
      if (along <= 0 || v.clone().addScaledVector(n, -v.dot(n)).angleTo(dir) > 0.12) continue;
      reach = Math.max(reach, along);
    }
    pts.push(c.clone().addScaledVector(dir, reach + tube * lift));
  }
  const g = new THREE.TubeGeometry(new THREE.CatmullRomCurve3(pts, true, 'centripetal'), segments * 2, tube, 6, true);
  g.scale(...scale);
  return g;
}

/** surface 上满足 keep 的顶点离 center 最远多少（面纱的顶盖按它定半径，盖住头发）。 */
function farthest(surface, center, keep) {
  const pos = surface.attributes.position, v = new THREE.Vector3(), c = new THREE.Vector3(...center);
  let far = 0;
  for (let i = 0; i < pos.count; i++) { v.fromBufferAttribute(pos, i); if (keep(v)) far = Math.max(far, v.distanceTo(c)); }
  return far;
}

// ———————————————————————— 衣着 ————————————————————————

/** 下身的衣料（挂在髋上，坐标已减去髋高）。长衣到脚踝，短衣到膝上。返回几何与上下沿（弹簧骨要知道从哪挂到哪）。 */
function lowerGarment(spec) {
  const hipY = 0.95;
  const long = !['exomis', 'chlamys'].includes(spec.garment);
  // spec.hem：下摆离地多高（身体单位）。雅典娜的裙子曾短到小腿中段（hem 0.27，露出缠到小腿的凉鞋），同一天按用户的要求恢复成长裙
  const y0 = spec.hem ?? (long ? 0.03 : 0.5);
  const flare = spec.garment === 'peplos' ? 0.29 : spec.garment === 'himation' ? 0.27 : long ? 0.25 : 0.24;
  const g = drape({
    y0, y1: 1.12, rings: long ? 30 : 16, segs: 64, ez: 0.76,
    radius: (t) => lerp(flare * (spec.build ?? 1), 0.155 * (spec.build ?? 1), Math.pow(t, 0.75)),
    folds: [
      { n: long ? 13 : 9, amp: 0.018, ph: 0.4, wobble: 0.35, wf: 2.1 },
      { n: 27, amp: 0.006, ph: 1.3, wobble: 0.5, wf: 3.3 },
    ],
    growth: (t) => 1.0 - 0.8 * t,
    hem: { amp: long ? 0.012 : 0.02, n: 7, ph: 0.5 },
  });
  g.translate(0, -hipY, 0);
  const b = spec.build ?? 1;
  return { geometry: g, top: 1.12 - hipY, bottom: y0 - hipY, ez: 0.76, anchorRadius: 0.155 * b, radius: (t) => lerp(flare * b, 0.155 * b, Math.pow(t, 0.75)) };
}

/**
 * 模拟的裙摆（M3）：代理布 rings × segs 按衣料的光滑母线（没有褶）安放，顶上 pinnedRings 圈钉在髋上；drape 生成的带褶细网格
 * 绑到代理上（bindFine），每帧按代理重建（applyFine）。网格直接挂在人物的根上，顶点按世界坐标算、换回根的局部坐标写入。
 */
function simulatedGarment(lower, material, { rings = 10, segs = 24, pinnedRings = 2, scale = 1 } = {}) {
  const cloth = createCloth({
    rings, segs, pinnedRings,
    rest: (j, k) => {
      const t = j / (rings - 1), th = (k / segs) * Math.PI * 2, r = lower.radius(t);
      return [r * Math.sin(th), lerp(lower.bottom, lower.top, t), r * Math.cos(th) * lower.ez];
    },
  });
  const geometry = lower.geometry;
  const pos = geometry.attributes.position;
  const fine = new Float32Array(pos.array);   // 安放位置（髋的局部坐标）
  const binding = bindFine(cloth, fine, (i) => ({
    t: (fine[i * 3 + 1] - lower.bottom) / (lower.top - lower.bottom),
    theta: Math.atan2(fine[i * 3], fine[i * 3 + 2] / lower.ez),
  }));
  pos.setUsage(THREE.DynamicDrawUsage);
  const mesh = shadowed(new THREE.Mesh(geometry, material));
  mesh.frustumCulled = false;
  return { cloth, binding, mesh, world: new Float32Array(fine.length), scale };
}

/** 斗篷（chlamys）：从两肩垂到大腿后面的一片半圆衣料，挂在脊柱上（坐标已减去脊柱的高度），三条弹簧骨。 */
function cape(spec) {
  const spineY = 0.98;
  const b = spec.build ?? 1;
  const g = drape({
    y0: 0.62, y1: 1.44, rings: 14, segs: 36, ez: 0.58, thStart: Math.PI * 0.5, thEnd: Math.PI * 1.5,
    radius: (t) => lerp(0.31 * b, 0.2 * b, Math.pow(t, 0.8)),
    folds: [{ n: 7, amp: 0.014, ph: 0.3, wobble: 0.3, wf: 2.0 }],
    growth: (t) => 1 - 0.7 * t,
    hem: { amp: 0.018, n: 5, ph: 0.9 },
  });
  g.translate(0, -spineY, 0);
  return { geometry: g, top: 1.44 - spineY, bottom: 0.62 - spineY, ez: 0.58, anchorRadius: 0.19 * b };
}

/**
 * 上身的衣料（挂在脊柱上，坐标已减去脊柱的高度）：翻折的上衣、斜披的外衣或只遮一边肩的短衣。
 * 腋下往上，两侧收回到躯干的宽度（UPPER_SIDE_CUT）——M3 第四轮，用户：衣服的袖子是固定的，没有和手臂一起运动。
 * 第一版这一筒布在肩下比躯干宽出 4 厘米，垂着手时正好罩住上臂的上半截、读成一对袖子，胳膊一抬它却留在原地，
 * 成了两块方垫肩、胳膊从底下伸出去。袖子另做，挂在肩关节上跟着上臂转（sleeveGeometry）。
 */
export const UPPER_SIDE_CUT = 0.05;
/** 领口到肩那一块的半宽（身体单位）：到胳膊内侧（肩关节 0.172 减上臂粗 0.041）为止，肩头归袖子。 */
export const SHOULDER_YOKE = 0.125;
function upperGarment(spec) {
  const spineY = 0.98;
  const b = spec.build ?? 1;
  const parts = [];
  // 女性：腰收进去（腰带那里 0.155）、翻折的下摆收到 0.205（第一版 0.24，像一只灯罩，整个人是方的）、胸前两处隆起（前面偏左右各 24°、在腋下那个高度），衣料顺着它垂下来——
  // 2026-10-12，用户：玩家要突出女性特点。第一版的上衣男女一样，是一只直筒
  const fem = !!spec.face?.female;
  const overR = fem
    ? keyed([[0, 0.205 * b], [0.38, 0.155 * b], [0.52, 0.166 * b], [0.74, 0.18 * b], [1, 0.155 * b]])
    : keyed([[0, 0.25 * b], [0.38, 0.17 * b], [0.52, 0.19 * b], [0.74, 0.19 * b], [1, 0.16 * b]]);
  const sideCut = keyed([[0.42, 0], [0.72, UPPER_SIDE_CUT * b]]);
  const wrap = (a) => Math.atan2(Math.sin(a), Math.cos(a));
  const bust = (t, th) => (fem ? 0.032 * b * Math.exp(-(((t - 0.7) / 0.085) ** 2)) * (Math.exp(-((wrap(th - 0.42) / 0.32) ** 2)) + Math.exp(-((wrap(th + 0.42) / 0.32) ** 2))) : 0);
  // 翻折的下摆到哪：女子的佩普洛斯短到胯上（0.93；第一版 0.86 垂到大腿中段，把人在胯下横切一刀，腿显得短，2026-10-12）。
  // 轮廓仍按原来 0.86 → 1.47 的位置取（u），只是从 y0 开始
  const y0 = fem && spec.garment === 'peplos' ? 0.93 : 0.86;
  const u = (t) => (lerp(y0, 1.47, t) - 0.86) / 0.61;
  parts.push(drape({
    y0, y1: 1.47, rings: 24, segs: 64, ez: 0.62,
    radius: (t, th) => overR(u(t)) - sideCut(u(t)) * Math.pow(Math.abs(Math.sin(th)), 3) + bust(u(t), th),
    folds: [{ n: 9, amp: 0.012, ph: 0.9, wobble: 0.3, wf: 2.0 }],
    growth: (t) => (1 - t) * 1.2 + 0.15,
    hem: { amp: spec.garment === 'peplos' ? 0.024 : 0.01, n: 5, ph: 1.2 },
  }));
  const belt = new THREE.TorusGeometry((fem ? 0.16 : 0.173) * b, 0.012, 6, 40);
  belt.rotateX(Math.PI / 2); belt.scale(1, 1, 0.63); belt.translate(0, 1.095, 0);
  parts.push(belt);
  // 领口到肩的那一块（盖住斜方肌）只到胳膊内侧：肩头归袖子（挂在肩关节上，袖顶的圆顶就是肩头）。
  // 第一版宽 0.165，盖过了肩关节（0.172）的大半——垂着手时叠在袖顶上，像套了两层袖子；胳膊一抬，它是留在原地的两块肩垫
  parts.push(blobGeometry([0, 1.455, 0], [SHOULDER_YOKE * b, 0.07, 0.1]));
  const g = merge(parts);
  g.translate(0, -spineY, 0);
  let sash = null;
  if (spec.garment === 'himation' || spec.garment === 'chlamys') {
    // 斜披的外衣：一卷布从左肩靠脖子的地方斜过胸口、绕到右腰，再从背后回到左肩——贴着身子走。
    // 第一版是一个比身子还宽的圆环（半径 0.21）斜着套上去，左肩那一段伸到 0.24、整个搭在左上臂外侧，
    // 垂着手时读成一只袖子，胳膊一抬它留在原地（2026-10-12，用户：真假袖子同时存在）
    const path = [[0.072, 1.478, 0], [0.05, 1.41, 0.125], [-0.04, 1.27, 0.14], [-0.13, 1.14, 0.118], [-0.188, 1.1, 0], [-0.13, 1.14, -0.112], [-0.04, 1.27, -0.13], [0.05, 1.41, -0.112]]
      .map(([x, y, z]) => new THREE.Vector3(x * b, y, z * b));
    sash = new THREE.TubeGeometry(new THREE.CatmullRomCurve3(path, true, 'centripetal'), 64, 0.042, 8, true);
    sash.translate(0, -spineY, 0);
  }
  return { torso: g, sash };
}

/**
 * 袖长（从肩关节沿上臂往下，米，按 1.75 米的身体；上臂 0.26）：佩普洛斯没有袖子，只有别针别住、搭在肩头的那一小截布；
 * 希顿的袖子到上臂中段；别的是短袖。
 */
export const SLEEVE = Object.freeze({ peplos: 0.06, chiton: 0.15, himation: 0.12, exomis: 0.11, chlamys: 0.12 });

/**
 * 一只袖子（肩关节的坐标系，挂在肩关节上、跟着上臂转）：顶上一个圆顶罩住肩头，下面一截贴着上臂的布筒（上臂 0.041，
 * 袖子比它粗一厘米多），几道浅褶，袖口卷一道边——有厚度，胳膊举过头时从袖口看进去不是一张纸。衣料是双面材质。
 * 第一版的筒比胳膊粗 3 厘米，胳膊一举，袖口像一圈飘着的纸带。
 */
export function sleeveGeometry(spec, sx) {
  const b = spec.build ?? 1;
  const len = SLEEVE[spec.garment] ?? 0.12;
  // 袖子贴着手臂（ARM_PROFILES）：上臂收细以后，原来 5.2 / 5.6 厘米的袖子在肩上鼓成两只气球
  const upper = legSection(ARM_PROFILES[spec.face?.female ? 'female' : 'male'].upper, b);
  const rTop = upper(0).rx + 0.012 * b, rHem = upper(len / 0.26).rx + 0.011 * b;
  const tube = drape({
    y0: -len, y1: 0, rings: 6, segs: 28, ez: 0.94,
    radius: (t) => lerp(rHem, rTop, t),
    folds: [{ n: 5, amp: 0.003, ph: 0.8 * sx, wobble: 0.3, wf: 3 }],
    growth: (t) => 1 - 0.7 * t,
  });
  const cap = new THREE.SphereGeometry(rTop, 24, 8, 0, Math.PI * 2, 0, Math.PI / 2);
  // 圆顶要盖过躯干的肩头：肩头的皮肤在肩关节上方 4.4 厘米，扁一些（0.8）的圆顶只到 3–4 厘米，肩垫收窄之后
  // 袖顶和领口之间露出一小块皮（2026-10-12）
  cap.scale(1, 0.95, 0.94);
  const hem = new THREE.TorusGeometry(rHem, 0.0055, 6, 28);
  hem.rotateX(Math.PI / 2);
  hem.scale(1, 1, 0.94);
  hem.translate(0, -len, 0);
  const g = merge([tube, cap, hem]);
  // 沿着上臂摆：肩 → 肘是 [sx·0.02, −0.26, 0.01]（arm() 里的那一根）
  g.applyQuaternion(new THREE.Quaternion().setFromUnitVectors(new THREE.Vector3(0, -1, 0), new THREE.Vector3(sx * 0.02, -0.26, 0.01).normalize()));
  return g;
}

// ———————————————————————— 头饰与手里的东西 ————————————————————————

/** surface：头发的外壳（没有头发就是头）——发带、头冠、面纱的顶盖贴着它放（bandOnHead）。 */
function headgear(spec, M, strips, scale, surface) {
  const out = [];
  switch (spec.headgear) {
    case 'fillet': {
      // 发带贴着头发：头在发带那个高度的截面是椭圆（前后比左右深），环也压成椭圆，不再浮在头外一圈
      out.push(mesh(bandOnHead(surface, { center: [0, 0.03, -0.012], tilt: 0.7, tube: 0.0045 }), M.gold));
      break;
    }
    case 'diadem': {
      out.push(mesh(bandOnHead(surface, { center: [0, 0.05, 0], tilt: 0.5, tube: 0.012, lift: 0.4 }), M.gold));
      break;
    }
    case 'headband': {
      // M2 美术审查：第一版的环比那个高度的头宽出一圈、管径 1.2 厘米，近看像一圈浮着的帽檐。改成细带、贴着头发
      out.push(mesh(bandOnHead(surface, { center: [0, 0.045, -0.004], tilt: 0.25, tube: 0.006 }), M.clothShades[1]));
      break;
    }
    case 'pilos':
    case 'pilosTilted': {
      // 圆锥形的毡帽：铁匠、水手。远看是头顶一个尖，剪影上最好认
      const c = mesh(new THREE.ConeGeometry(0.1, 0.2, 14), M.leather);
      c.position.set(0, 0.11, -0.01);
      if (spec.headgear === 'pilosTilted') c.rotation.z = 0.25;
      out.push(c);
      break;
    }
    case 'petasos':
    case 'petasosBack': {
      // 宽檐旅人帽；传令官的用带子挂在脑后
      const hat = new THREE.Group();
      hat.add(mesh(new THREE.CylinderGeometry(0.22, 0.24, 0.015, 24), M.leather));
      const crown = mesh(new THREE.SphereGeometry(0.1, 16, 10, 0, Math.PI * 2, 0, Math.PI / 2), M.leather);
      hat.add(crown);
      if (spec.headgear === 'petasosBack') { hat.position.set(0, -0.12, -0.16); hat.rotation.x = -1.2; }
      else { hat.position.set(0, 0.08, -0.005); }
      out.push(hat);
      break;
    }
    case 'veil': {
      // 女祭司的面纱：从头顶披到肩后，脸露着——只有后面大半圈（第一版是整圈的锥筒，近景里是一只罩住头的灯罩）。
      // M3：挂在三条弹簧骨上，走动和风里会飘（比衣摆更吃风、更软）
      const veil = clothStrip(drape({
        y0: -0.3, y1: 0.11, rings: 10, segs: 28, ez: 0.92, thStart: Math.PI * 0.62, thEnd: Math.PI * 1.38,
        radius: (t) => lerp(0.21, 0.108, Math.pow(t, 0.8)),
        folds: [{ n: 9, amp: 0.008, ph: 0.2, wobble: 0.3, wf: 2.0 }], growth: (t) => 1 - 0.7 * t,
      }), M.clothShades[0], {
        chains: 3, bones: 2, top: 0.11, bottom: -0.3, anchorRadius: 0.1, ez: 0.92, thStart: Math.PI * 0.62, thEnd: Math.PI * 1.38, scale,
        spring: { stiffness: 2.2, drag: 0.3, gravity: 0.8, maxAngle: 1.2, inertia: 0.6, wind: 1.6, radius: 0.03 },
      });
      veil.mesh.position.set(0, 0.0, -0.02);
      strips.push(...veil.strips);
      out.push(veil.mesh);
      // 头顶的一片：盖住发顶，面纱从它的后沿垂下
      // 半径取它盖住的那一片头发（含发髻）最远的一点再加 3 毫米：第一版固定 0.108，换成外推的头发之后发顶从顶盖里透出来
      const capAt = [0, 0.012, -0.012], capEdge = 0.012 + Math.cos(Math.PI * 0.42) * 0.12;
      const capR = farthest(surface, capAt, (p) => p.y > capEdge - 0.01) + 0.003;
      const cap = mesh(new THREE.SphereGeometry(capR, 24, 12, 0, Math.PI * 2, 0, Math.PI * 0.42), M.clothShades[0]);
      cap.position.set(...capAt);
      out.push(cap);
      out.push(mesh(bandOnHead(cap.geometry.clone().translate(...capAt), { center: [0, 0.06, 0.005], tilt: 0.3, tube: 0.018, lift: 0.5 }), M.laurel));
      break;
    }
    case 'sakkos': {
      const s = mesh(blobGeometry([0, 0.01, -0.07], [0.1, 0.1, 0.13]), M.clothShades[2]);
      out.push(s);
      break;
    }
    default:
      break;
  }
  return out;
}

/** 挂在左手上的小件（其余挂在右手）。 */
export const LEFT_HAND_PROPS = Object.freeze(['tablet', 'palette', 'abacus', 'chisel', 'tongs']);

/**
 * 两只手一起拿的东西（M3 第三轮）：不挂在哪一只手上，每帧按两只手此刻的握点摆——展开的卷轴两根轴各在一只拳里、
 * 交付的卷轴横在两只手上、等审批的石板两手捧在胸前、祭碗托在两只掌心上、缆绳穿过两只拳头。
 * 第一版都挂在右手上：另一只手悬在空中，祭碗整个藏在手里看不见，缆绳是两根从右手伸出去的直杆。
 */
export const TWO_HANDED_PROPS = Object.freeze(['scrollOpen', 'gift', 'sealed', 'phiale', 'rope']);

/**
 * 手里的几个点（手的坐标系，见 buildHand；按 1.75 米的身体）：
 *   fist —— 攥紧时手指围出来的拳眼，沿 x 的一条孔：柄、凿子、缆绳从这里穿过（量自真骨架：手指弯 0.85 时的那一圈）；
 *   palm —— 掌面：托着的东西（石板、算板、调色板、祭碗）底面落在这里，手指伸直；
 *   pinch —— 捏笔的指尖（拇指和食指之间，×sx）：笔杆从这里斜着出去，笔尖在前下方。
 */
export const GRIP = Object.freeze({ fist: Object.freeze([0, -0.058, 0.027]), palm: Object.freeze([0, -0.05, 0.019]), pinch: Object.freeze([0.012, -0.088, 0.04]) });

/** 每样东西握的方式（手指弯多少；两手一起拿的，两只手都按它）：拳握 0.9、捏笔 0.55、托着 0.12。 */
export const PROP_CURL = Object.freeze({
  hammer: 0.92, mallet: 0.92, chisel: 0.92, tongs: 0.92, scroll: 0.88, rope: 0.9, scrollOpen: 0.88, staff: 0.88, rod: 0.88,
  stylus: 0.55, brush: 0.55, tablet: 0.12, abacus: 0.12, palette: 0.25, phiale: 0.2, gift: 0.4, sealed: 0.55,
});

/** 两手一起拿的东西，每只手拿着它的哪个点。 */
const TWO_HANDED_GRIP = Object.freeze({ scrollOpen: 'fist', rope: 'fist', gift: 'palm', sealed: 'palm', phiale: 'palm' });

/** a → b 的一根圆杆（r0 在 a 端、r1 在 b 端）。 */
function rodBetween(a, b, r0, r1, radial = 8) {
  const A = new THREE.Vector3(...a), B = new THREE.Vector3(...b);
  const dir = new THREE.Vector3().subVectors(B, A);
  const g = new THREE.CylinderGeometry(r1, r0, dir.length(), radial);
  g.applyQuaternion(new THREE.Quaternion().setFromUnitVectors(new THREE.Vector3(0, 1, 0), dir.normalize()));
  const mid = A.clone().add(B).multiplyScalar(0.5);
  g.translate(mid.x, mid.y, mid.z);
  return g;
}

/**
 * 手里能拿的小件：按名字建好、挂在手上（手的坐标系；sx 是那只手的左右，拇指在 +sx 那一侧），姿势里点到名字的才显示。
 * 握法（M3 第三轮，用户：工匠拿锤子的方式完全错误、雅典娜用工具也有问题）：
 *   拳握——柄沿拳眼横穿过拳头，干活的那一头（锤头、槌头、凿尾、钳口）在拇指那一侧；姿势把这只手转到拇指朝前（poses 的 NEUTRAL），
 *     柄就和前臂垂直、落在挥动的那个平面里——第一版的柄顺着前臂、锤头贴着手腕，像握着一根擀面杖；
 *   托——石板、算板、调色板平放在掌面上（板面就是手的 x–y 面）：前臂朝前时掌心朝上，板是平的，手腕一弯就朝着脸——
 *     第一版的板和手掌垂直，前臂抬平时板竖着立在胸前、正面朝外，像举着一块招牌；
 *   捏——笔杆从拇指与食指之间斜着出去，笔尖在指尖前下方 4 厘米（写字、作画时笔尖落在板面上）。
 */
function buildProp(name, M, accentGift, sx = -1) {
  const g = new THREE.Group();
  const add = (geo, mat, p = [0, 0, 0], r = [0, 0, 0]) => { const m = mesh(geo, mat); m.position.set(...p); m.rotation.set(...r); g.add(m); return m; };
  const F = GRIP.fist;
  const along = (d, dy = 0, dz = 0) => [sx * d, F[1] + dy, F[2] + dz];   // 拳眼上离拳心 d 米的一点（正的往拇指那边）
  const P = GRIP.palm;
  const pen = (length, tipR, backR, mat) => {
    // 笔尖在指尖前下方，笔杆斜着从虎口出去（朝手腕、朝拇指那一侧）
    const tip = [sx * 0.004, -0.128, 0.052], back = [sx * 0.05, -0.128 + length * 0.86, 0.052 + length * 0.12];
    add(rodBetween(tip, back, tipR, backR, 6), mat);
    return { tip, back };
  };
  switch (name) {
    case 'scroll':
      // 卷着的卷轴：攥在拳里，轴沿拳眼
      add(rodBetween(along(-0.12), along(0.14), 0.03, 0.03, 12), M.papyrus);
      for (const d of [-0.125, 0.145]) add(rodBetween(along(d), along(d + 0.012), 0.012, 0.012, 8), M.darkWood);
      break;
    case 'tablet': {
      // 蜡板：木框里一层深色的蜡，平放在掌面上（宽边横过手掌，伸出指尖一截）
      add(new THREE.BoxGeometry(0.24, 0.17, 0.016), M.wood, [0, P[1] - 0.04, P[2] + 0.008]);
      add(new THREE.BoxGeometry(0.205, 0.135, 0.004), M.dark, [0, P[1] - 0.04, P[2] + 0.017]);
      break;
    }
    case 'stylus':
      pen(0.14, 0.0025, 0.0055, M.bronze);
      break;
    case 'hammer': {
      // 锻锤：柄穿过拳眼，从小指那一侧露出 4 厘米尾巴，锤头在拇指那一侧的柄端；锤头的长轴在挥动的平面里（手的 y）
      add(rodBetween(along(-0.045), along(0.27), 0.017, 0.015, 8), M.wood);
      add(new THREE.BoxGeometry(0.07, 0.17, 0.066), M.bronze, along(0.29, -0.012));
      add(new THREE.BoxGeometry(0.076, 0.03, 0.072), M.dark, along(0.29, -0.09));   // 锤面
      break;
    }
    case 'mallet':
      // 雕石头的木槌：圆的槌头和柄同轴（车出来的那种），在拇指那一侧
      add(rodBetween(along(-0.04), along(0.17), 0.013, 0.016, 8), M.wood);
      add(rodBetween(along(0.17), along(0.3), 0.046, 0.055, 14), M.darkWood);
      break;
    case 'chisel':
      // 凿子：攥在左拳里，凿尾从拇指那一侧露出来（木槌敲它），刃从小指那一侧伸出去抵着石头
      add(rodBetween(along(0.085), along(-0.13), 0.011, 0.009, 8), M.bronze);
      add(rodBetween(along(0.085), along(0.1), 0.014, 0.014, 8), M.bronze);        // 凿尾被敲宽的那一圈
      add(new THREE.BoxGeometry(0.03, 0.016, 0.005), M.bronze, along(-0.14));        // 扁刃
      break;
    case 'tongs': {
      // 火钳：两根细长的钳臂从拳眼朝拇指那一侧伸出去 45 厘米，钳口夹着一根铁条（铁不发光——光只属于雅典娜）
      for (const s of [-1, 1]) add(rodBetween(along(-0.03, 0, s * 0.006), along(0.44, 0, s * 0.012), 0.007, 0.006, 6), M.dark);
      add(rodBetween(along(0.43, 0.0, -0.06), along(0.43, 0.0, 0.08), 0.012, 0.012, 6), M.dark);
      break;
    }
    case 'staff': {
      // 传令官的权杖（kerykeion）：举着的短杖，杖头两条相对的蛇（两个小环）。握在杖身下三分之一处；
      // 姿势里手腕补回前臂的角度（poses.mjs 的 upright），杖才是竖的
      add(new THREE.CylinderGeometry(0.014, 0.016, 1.1, 8), M.wood, [0, 0.22, 0.02]);
      for (const sx of [-1, 1]) add(new THREE.TorusGeometry(0.042, 0.009, 6, 14), M.gold, [sx * 0.032, 0.8, 0.02], [0, 0, 0]);
      break;
    }
    case 'rod':
      // 测绘员的测杆：2.2 米长，一节一节的刻度。握在胸口的高度，杖脚落在地上（手腕补回前臂的角度，杆是竖的）
      add(new THREE.CylinderGeometry(0.015, 0.017, 2.2, 8), M.wood, [0, -0.1, 0.02]);
      for (let k = 0; k < 6; k++) add(new THREE.CylinderGeometry(0.021, 0.021, 0.05, 8), M.dark, [0, -0.95 + k * 0.34, 0.02]);
      break;
    case 'brush': {
      // 画笔：捏在指尖，笔尖一撮深色的毛
      const { tip, back } = pen(0.22, 0.004, 0.007, M.wood);
      const d = new THREE.Vector3(...back).sub(new THREE.Vector3(...tip)).normalize().multiplyScalar(-0.02);
      add(rodBetween(tip, [tip[0] + d.x, tip[1] + d.y, tip[2] + d.z], 0.006, 0.002, 6), M.dark);
      break;
    }
    case 'palette': {
      // 调色板：一块平放在掌面上的木板（拇指穿过板上的孔），上面几团颜料——只有一团是釉色，其余是浅色（点缀色不属于居民）
      const board = add(new THREE.CylinderGeometry(0.11, 0.11, 0.01, 20), M.wood, [0, P[1] - 0.05, P[2] + 0.005], [Math.PI / 2, 0, 0]);
      board.scale.set(1.25, 1, 1);
      for (let k = 0; k < 4; k++) add(new THREE.SphereGeometry(0.016, 8, 5), k === 1 ? M.glaze : k === 3 ? M.dark : M.papyrus, [-0.07 + k * 0.045, P[1] - 0.1 + (k % 2) * 0.03, P[2] + 0.014], [0, 0, 0]);
      break;
    }
    case 'abacus': {
      // 算板：平放在掌面和小臂上的一块深色木板，三道刻槽里摆着计数的石子
      add(new THREE.BoxGeometry(0.2, 0.3, 0.018), M.darkWood, [sx * -0.03, P[1] - 0.02, P[2] + 0.009]);
      for (let row = 0; row < 3; row++) {
        add(new THREE.BoxGeometry(0.004, 0.26, 0.004), M.dark, [sx * -0.03 + (row - 1) * 0.055, P[1] - 0.02, P[2] + 0.019]);
        for (let k = 0; k < 3 + row; k++) add(new THREE.SphereGeometry(0.011, 6, 4), M.papyrus, [sx * -0.03 + (row - 1) * 0.055, P[1] - 0.12 + k * 0.05 + row * 0.01, P[2] + 0.026]);
      }
      break;
    }
    default:
      throw new Error(`rig: unknown prop '${name}'`);
  }
  g.visible = false;
  return g;
}

const _tA = new THREE.Vector3(), _tB = new THREE.Vector3(), _tC = new THREE.Vector3(), _tX = new THREE.Vector3(), _tY = new THREE.Vector3(), _tZ = new THREE.Vector3();
const _tM = new THREE.Matrix4();

/** 把 group 摆到原点 o、三根轴 x / y / z 上（都在人物根的坐标系里）。 */
function placeBasis(group, o, x, y, z) {
  _tM.makeBasis(x, y, z).setPosition(o);
  group.matrixAutoUpdate = false;
  group.matrix.copy(_tM);
  group.matrixWorldNeedsUpdate = true;
}

/** 由两只手算出来的一组正交轴：x 从右手指向左手，z 尽量朝 forward，y = z × x。 */
function handBasis(L, R, forward) {
  _tX.subVectors(L, R);
  if (_tX.lengthSq() < 1e-8) _tX.set(1, 0, 0);
  _tX.normalize();
  _tZ.copy(forward).addScaledVector(_tX, -forward.dot(_tX));
  if (_tZ.lengthSq() < 1e-8) _tZ.set(0, 0, 1);
  _tZ.normalize();
  _tY.crossVectors(_tZ, _tX).normalize();
}

/**
 * 缆绳：一根管子按一条折线（先用 Catmull–Rom 拉顺）铺开，每帧原地改顶点，不重新分配。
 * 返回 { mesh, set(points) }。
 */
function ropeTube(material, { segments = 48, radial = 6, radius = 0.016 } = {}) {
  const count = (segments + 1) * (radial + 1);
  const g = new THREE.BufferGeometry();
  const pos = new Float32Array(count * 3), nrm = new Float32Array(count * 3), uv = new Float32Array(count * 2);
  const idx = [];
  for (let i = 0; i < segments; i++) for (let k = 0; k < radial; k++) {
    const a = i * (radial + 1) + k, b = a + radial + 1;
    // 朝外绕：(a+1 − a) × (b − a) = bn × t = n。第一版绕反了（法线朝里），双面的帆布材质按背面打光，整根缆是黑的
    idx.push(a, a + 1, b, b, a + 1, b + 1);
  }
  for (let i = 0; i <= segments; i++) for (let k = 0; k <= radial; k++) { uv[(i * (radial + 1) + k) * 2] = i / segments * 8; uv[(i * (radial + 1) + k) * 2 + 1] = k / radial; }
  g.setIndex(idx);
  g.setAttribute('position', new THREE.BufferAttribute(pos, 3).setUsage(THREE.DynamicDrawUsage));
  g.setAttribute('normal', new THREE.BufferAttribute(nrm, 3).setUsage(THREE.DynamicDrawUsage));
  g.setAttribute('uv', new THREE.BufferAttribute(uv, 2));
  const m = shadowed(new THREE.Mesh(g, material));
  m.frustumCulled = false;
  const curve = new THREE.CatmullRomCurve3([new THREE.Vector3(), new THREE.Vector3(0, 0, 1)], false, 'centripetal');
  const p = new THREE.Vector3(), t = new THREE.Vector3(), n = new THREE.Vector3(), bn = new THREE.Vector3(), ref = new THREE.Vector3();
  return {
    mesh: m,
    set(points) {
      curve.points = points;
      for (let i = 0; i <= segments; i++) {
        const u = i / segments;
        curve.getPointAt(u, p);
        curve.getTangentAt(u, t);
        ref.set(0, 1, 0);
        if (Math.abs(t.dot(ref)) > 0.9) ref.set(1, 0, 0);
        n.crossVectors(t, ref).normalize();
        bn.crossVectors(t, n).normalize();
        for (let k = 0; k <= radial; k++) {
          const a = (k / radial) * Math.PI * 2, c = Math.cos(a), s = Math.sin(a);
          const o = (i * (radial + 1) + k) * 3;
          const nx = n.x * c + bn.x * s, ny = n.y * c + bn.y * s, nz = n.z * c + bn.z * s;
          nrm[o] = nx; nrm[o + 1] = ny; nrm[o + 2] = nz;
          pos[o] = p.x + nx * radius; pos[o + 1] = p.y + ny * radius; pos[o + 2] = p.z + nz * radius;
        }
      }
      g.attributes.position.needsUpdate = true;
      g.attributes.normal.needsUpdate = true;
    },
  };
}

/**
 * 两只手一起拿的东西（TWO_HANDED_PROPS）：一个挂在人物根上的 group，update(L, R, ctx) 每帧按两只手的握点摆好。
 * L / R：左右手的握点（根的坐标系，TWO_HANDED_GRIP 说是拳眼还是掌面）；ctx：{ face 脸的位置, scale 身体缩放 }。
 */
function buildTwoHanded(name, M, accentGift) {
  const g = new THREE.Group();
  g.visible = false;
  const fwd = new THREE.Vector3(0, 0, 1);
  switch (name) {
    case 'scrollOpen': {
      // 展开的卷轴：两根轴竖着各攥在一只拳里，纸横在两手之间、朝着读的人的脸
      const rollers = [0, 1].map(() => { const m = mesh(new THREE.CylinderGeometry(0.021, 0.021, 0.22, 10), M.papyrus); g.add(m); return m; });
      const knobs = [0, 1, 2, 3].map(() => { const m = mesh(new THREE.SphereGeometry(0.015, 8, 6), M.darkWood); g.add(m); return m; });
      const sheet = mesh(new THREE.PlaneGeometry(1, 0.19), M.papyrus);
      sheet.material = M.papyrus;
      g.add(sheet);
      const lines = [0, 1, 2, 3, 4].map(() => { const m = new THREE.Mesh(new THREE.PlaneGeometry(1, 0.008), M.dark); g.add(m); return m; });
      g.userData.update = (L, R, ctx) => {
        _tC.addVectors(L, R).multiplyScalar(0.5);
        // 纸面朝着脸：法线指向脸
        _tA.subVectors(ctx.face, _tC);
        handBasis(L, R, _tA);
        placeBasis(g, _tC, _tX, _tY, _tZ);
        const half = Math.max(0.05, L.distanceTo(R) / 2);
        rollers[0].position.set(half, 0, 0); rollers[1].position.set(-half, 0, 0);
        knobs.forEach((k, i) => k.position.set(i < 2 ? half : -half, i % 2 ? 0.12 : -0.12, 0));
        sheet.scale.set(Math.max(0.02, half * 2 - 0.03), 1, 1);
        sheet.position.set(0, 0, 0.004);
        lines.forEach((l, i) => { l.scale.set(Math.max(0.02, half * 2 - 0.09), 1, 1); l.position.set(0, 0.06 - i * 0.03, 0.006); });
      };
      break;
    }
    case 'gift': {
      // 交付：一卷红色的卷轴（点缀色，"轮到你了"的那样东西），横着托在两只手上
      const rod = new THREE.Mesh(new THREE.CylinderGeometry(0.04, 0.04, 0.32, 14), accentGift ? M.accent : M.papyrus);
      rod.rotation.z = Math.PI / 2;
      rod.castShadow = true;
      if (accentGift) rod.userData.accent = true;
      g.add(rod);
      const ends = [-1, 1].map((s) => { const m = mesh(new THREE.CylinderGeometry(0.043, 0.043, 0.012, 14), M.darkWood); m.rotation.z = Math.PI / 2; m.position.x = s * 0.165; g.add(m); return m; });
      void ends;
      g.userData.update = (L, R) => {
        _tC.addVectors(L, R).multiplyScalar(0.5);
        handBasis(L, R, fwd);
        _tC.addScaledVector(_tY, 0.042);   // 搁在掌面上
        placeBasis(g, _tC, _tX, _tY, _tZ);
      };
      break;
    }
    case 'sealed': {
      // 等审批：两手捧在胸前的石板，捆着一道红绳、压着一枚红封印——"轮到你了"。板面朝外（红色看得见），
      // 红绳与封印两面都有：从背后看也认得出
      g.add(mesh(new THREE.BoxGeometry(0.26, 0.18, 0.02), M.wood));
      const cord = new THREE.Mesh(new THREE.BoxGeometry(0.036, 0.188, 0.03), M.accent);
      const seal = new THREE.Mesh(new THREE.CylinderGeometry(0.06, 0.06, 0.05, 18), M.accent);
      seal.rotation.x = Math.PI / 2;
      for (const m of [cord, seal]) { m.castShadow = true; m.userData.accent = true; g.add(m); }
      g.userData.update = (L, R) => {
        _tC.addVectors(L, R).multiplyScalar(0.5);
        handBasis(L, R, fwd);
        // 板竖着捧在两只掌心之间：板的下沿落在两手上，中心比手高半块板
        _tC.addScaledVector(_tY, 0.07).addScaledVector(_tZ, 0.02);
        placeBasis(g, _tC, _tX, _tY, _tZ);
      };
      break;
    }
    case 'phiale': {
      // 祭碗：一只浅金碗托在两只掌心上（第一版是倒扣在右手里的半个球壳，从哪个角度都看不见）
      const bowl = mesh(new THREE.LatheGeometry([0, 0.03, 0.06, 0.085, 0.095, 0.098].map((r, i) => new THREE.Vector2(r, i * 0.007 + (i > 3 ? (i - 3) * 0.006 : 0))), 20), M.gold);
      bowl.material = M.gold;
      g.add(bowl);
      const boss = mesh(new THREE.SphereGeometry(0.02, 10, 6, 0, Math.PI * 2, 0, Math.PI / 2), M.gold);
      boss.position.y = 0.004;
      g.add(boss);
      g.userData.update = (L, R) => {
        _tC.addVectors(L, R).multiplyScalar(0.5);
        handBasis(L, R, fwd);
        _tC.addScaledVector(_tY, 0.006).addScaledVector(_tZ, 0.015);
        // 碗口朝上：用身体的上方，不随两手的连线歪
        _tY.set(0, 1, 0); _tZ.copy(fwd).addScaledVector(_tY, -fwd.dot(_tY)).normalize(); _tX.crossVectors(_tY, _tZ).normalize();
        placeBasis(g, _tC, _tX, _tY, _tZ);
      };
      break;
    }
    case 'rope': {
      // 缆绳：从船上拉过来，穿过前面那只拳、后面那只拳，垂到身后脚边的一盘缆上。船上那一头是 ctx.ropeTo（根的坐标系，
      // 由 style.ropeTo 的世界坐标换过来：水手的缆拴在泊着的船的舷上，船起伏它跟着动）；没给就落在身前 2.6 米的低处
      const tube = ropeTube(M.awning);
      g.add(tube.mesh);
      const coil = new THREE.Group();
      for (let k = 0; k < 3; k++) {
        const ring = mesh(new THREE.TorusGeometry(0.15 - k * 0.025, 0.016, 6, 20), M.awning);
        ring.rotation.x = Math.PI / 2;
        ring.position.y = 0.016 + k * 0.028;
        coil.add(ring);
      }
      coil.position.set(-0.05, 0, -0.42);
      g.add(coil);
      const far = new THREE.Vector3(0.05, 0.12, 2.6);
      const pts = Array.from({ length: 7 }, () => new THREE.Vector3());
      g.userData.update = (L, R, ctx) => {
        if (ctx.ropeTo) far.copy(ctx.ropeTo);
        else far.set(0.05, 0.12 / ctx.scale, 2.6);
        // 前面那只手（离船近的）先接住缆
        const lead = L.distanceToSquared(far) <= R.distanceToSquared(far) ? L : R, rear = lead === L ? R : L;
        pts[0].copy(far);
        pts[1].lerpVectors(far, lead, 0.55).y -= 0.05 * Math.min(2.6, far.distanceTo(lead));   // 两头之间往下垂一点（越长垂得越多）
        pts[2].copy(lead);
        pts[3].copy(rear);
        pts[4].copy(rear).add(_tA.set(0, -0.25, -0.08));
        pts[5].set(coil.position.x + 0.1, 0.12, coil.position.z + 0.12);
        pts[6].set(coil.position.x + 0.13, 0.05, coil.position.z);
        tube.set(pts);
      };
      break;
    }
    default:
      throw new Error(`rig: unknown two-handed prop '${name}'`);
  }
  return g;
}

/**
 * 按人物参数建一副骨架。
 * @param {object} M 材质
 * @param {object} spec 人物参数（characters.mjs）：height、garment、cloth、hair、beard、headgear、build、props、glow、faceless、gold
 * @returns {{ root: THREE.Group, joints: object, props: Map<string, THREE.Group>, spec: object, apply(pose): void, pose(action, t, style): void }}
 */
export function buildFigure(M, spec) {
  const H = spec.height ?? 1.75;
  const s = H / 1.75;
  const root = new THREE.Group();
  root.name = `figure:${spec.id ?? 'anon'}`;
  const body = new THREE.Group();
  body.scale.setScalar(s);
  root.add(body);
  const joint = (parent, x, y, z, order = 'XYZ') => {
    const j = new THREE.Group();
    j.position.set(x, y, z);
    j.rotation.order = order;
    parent.add(j);
    return j;
  };
  const cloth = spec.cloth;
  const skin = spec.gold ? M.maiden : M.skin;
  const hairMat = spec.gold ? M.maiden : spec.hairMat ?? M.hair;
  const b = spec.build ?? 1;

  /** 挂在弹簧骨上的衣料（衣摆、围裙、面纱、辫子、斗篷）：每帧由 updateCloth 推进。 */
  const strips = [];
  const J = {};
  // 厚底鞋把整个人抬高（脚底仍然落在地面上）
  const lift = spec.sandals === 'tyrrhenian' ? TYRRHENIAN_LIFT : 0;
  J.hips = joint(body, 0, 0.95 + lift, 0);
  J.hips.userData.base = new THREE.Vector3(0, 0.95 + lift, 0);
  J.legL = joint(J.hips, 0.085 * b, -0.02, 0);
  J.legR = joint(J.hips, -0.085 * b, -0.02, 0);
  J.kneeL = joint(J.legL, 0, -0.44, 0);
  J.kneeR = joint(J.legR, 0, -0.44, 0);
  const legMat = spec.gold ? M.maiden : M.skin;
  const longGarment = !['exomis', 'chlamys'].includes(spec.garment);
  // 腿：M2 给长衣的人去掉了腿（走路时屈膝的小腿从衣摆前面戳出来一截肉色）；M3 衣摆是会动的布，走起来向后甩，
  // 没有腿就只剩两只鞋浮在衣摆前面——腿建回来，长衣的步子仍摆得小（legSwing 0.24），布料碰撞把裙摆挡在腿外（cloth.mjs）。
  // 雅典娜也走路了（M3，用户实测：滑行像女鬼在飘），所以人人有腿
  const legShape = LEG_PROFILES[spec.face?.female ? 'female' : 'male'];
  const shin = legSection(legShape.shin, b);
  for (const [sx, leg, knee] of [[1, J.legL, J.kneeL], [-1, J.legR, J.kneeR]]) {
    // 大腿、小腿：放样出来的腿（LEG_PROFILES）——大腿往膝收，小腿肚在上三分之一，往下收到细的脚踝；膝盖处一个小圆
    const thigh = mesh(taperedLimb(legSection(legShape.thigh, b), 0.44, { s1: 1.04 }), legMat);
    thigh.name = 'thigh';
    leg.add(thigh);
    const [kx, ky, kz] = legShape.knee;
    leg.add(mesh(blobGeometry([0, -0.44, 0.008], [kx * b, ky, kz * b]), legMat));
    const shinMesh = mesh(taperedLimb(shin, SHIN_LENGTH, { s1: 1.03 }), legMat);
    shinMesh.name = 'shin';
    knee.add(shinMesh);
    const foot = spec.sandals === 'tyrrhenian'
      ? buildTyrrhenianFoot(sx, M.skin, M.wood, M.gold)
      : buildFoot(sx, spec.gold ? M.maiden : M.skin, spec.gold ? M.maiden : M.leather, shin);
    foot.position.set(0, -SHIN_LENGTH, 0.0);
    knee.add(foot);
    if (spec.sandals === 'tyrrhenian') { const lacing = calfLacing(shin, M.gold); lacing.name = 'lacing'; knee.add(lacing); }
  }
  J.skirt = joint(J.hips, 0, 0, 0);
  // 裙摆：布料模拟（cloth.mjs）——走起来下摆滞后、停下来回摆、风里飘，而且被腿顶在外面（M3）
  const lower = lowerGarment(spec);
  const skirt = simulatedGarment(lower, cloth, { rings: !longGarment ? 7 : spec.hem > 0.15 ? 9 : 11, segs: 32, pinnedRings: 2, scale: s });
  root.add(skirt.mesh);
  const garments = [skirt];
  if (spec.apron) {
    // 铁匠、工匠的皮围裙：从腰带垂到膝上、顺着身体弯过来的一片浅色鞣皮，挂在下摆上，走路时跟着下摆一起晃。
    // 第一版是挂在胸前的一块平板、用深皮，去色后是一块纯黑的长方形。M3：一条弹簧骨（皮比布硬，摆幅小）
    const apron = new THREE.CylinderGeometry(0.2 * b, 0.27 * b, 0.6, 18, 4, true, -1.0, 2.0);
    apron.scale(1, 1, 0.74);
    apron.translate(0, 0.8 - 0.95, 0);
    const apronStrip = clothStrip(apron, M.hide, {
      chains: 1, bones: 2, top: 0.8 - 0.95 + 0.3, bottom: 0.8 - 0.95 - 0.3, anchorRadius: 0.2 * b, ez: 0.74, thStart: -1.0, thEnd: 1.0, scale: s,
      spring: { stiffness: 2.6, drag: 0.4, gravity: 1.2, maxAngle: 0.5, inertia: 0.3, wind: 0.3, radius: 0.03 },
    });
    J.skirt.add(apronStrip.mesh);
    strips.push(...apronStrip.strips);
  }

  J.spine = joint(J.hips, 0, 0.03, 0);
  const upper = upperGarment(spec);
  const upperMesh = mesh(upper.torso, cloth);
  upperMesh.name = 'upperGarment';
  J.spine.add(upperMesh);
  if (upper.sash) { const sashMesh = mesh(upper.sash, cloth); sashMesh.name = 'sash'; J.spine.add(sashMesh); }
  if (spec.garment === 'chlamys') {
    // 传令官的斗篷：挂在肩上、垂到大腿后面，三条弹簧骨——跑起来、风里会扬起来（M3）
    const c = cape(spec);
    const capeStrip = clothStrip(c.geometry, cloth, {
      chains: 3, bones: 3, top: c.top, bottom: c.bottom, anchorRadius: c.anchorRadius, ez: c.ez, thStart: Math.PI * 0.5, thEnd: Math.PI * 1.5, scale: s,
      spring: { stiffness: 1.4, drag: 0.3, gravity: 0.9, maxAngle: 1.25, inertia: 0.7, wind: 1.4, radius: 0.03 },
    });
    capeStrip.mesh.name = 'cape';
    J.spine.add(capeStrip.mesh);
    strips.push(...capeStrip.strips);
  }
  const fem = !!spec.face?.female;
  const armShape = ARM_PROFILES[fem ? 'female' : 'male'];
  const skinTorso = [
    // 肩头的皮肤：要藏在袖顶里面——袖子随手臂收细以后，原来 4.2 厘米的这一团从女子的袖顶戳出来
    ...[-1, 1].map((sx) => blobGeometry([sx * 0.168 * b, 1.432 - 0.98, 0], fem ? [0.034, 0.036, 0.036] : [0.04, 0.042, 0.042])),
    // 脖子：放样、前倾（NECK_PROFILES）。第一版 4.2 厘米的胶囊在宽大的上衣上面细得像一根杆，第二版 4.7 厘米的直立圆柱
    // 上下一样粗——像电线杆（2026-10-12，用户）
    neckGeometry(legSection(NECK_PROFILES[fem ? 'female' : 'male'], b), 0.608, 0.028, 0.15),
  ];
  const torsoSkin = mesh(merge(skinTorso), skin);
  torsoSkin.name = 'torsoSkin';
  J.spine.add(torsoSkin);

  // 头比第一版低 3 厘米：露在衣领外的脖子短一截（2026-10-12，用户：脖子像电线杆）
  J.head = joint(J.spine, 0, 0.675, 0.014);
  const face = { beard: !!spec.beard, ...(spec.face ?? {}) };
  // 头的网格（头发按它外推，所以留着它）
  const headGeometry = spec.faceless ? blobGeometry([0, 0.0, 0], [...FACELESS_RADII], [0, 0, 0], [48, 32]) : sculptHead(face);
  const headMesh = mesh(headGeometry, skin);
  headMesh.name = 'head';
  J.head.add(headMesh);
  if (!spec.faceless) {
    for (const piece of faceDetails(M, spec, headGeometry, skin)) J.head.add(piece);
  }
  // 仪态（spec.bearing）：头的基础俯角与上身的后仰，叠在每个姿势上。人人默认微微低头（0.06）；
  // 雅典娜抬着下巴、挺着胸（2026-10-12，用户：整体没有一点飒爽的气质）
  const headTilt = spec.bearing?.head ?? 0.06, spineTilt = spec.bearing?.spine ?? 0;
  J.head.rotation.x = headTilt;
  // 女子的头窄一些、小一些（2026-10-12：同一颗 16.4 厘米宽的圆头放在雅典娜身上显得头大脸圆）。缩放挂在头关节上，
  // 头发、发带、五官贴图跟着一起缩
  if (fem) J.head.scale.set(0.92, 0.97, 0.95);
  // 头发：按发型从雕好的头往外推一层（HAIR_STYLES），带发髻、辫子、披下来的长发
  const hairSurface = buildHair(spec, headGeometry, hairMat, J.head, strips, s);
  if (spec.beard) {
    // 胡子：一团按发丝方向揉出一缕缕起伏的体积（M3 近景：第一版是光滑的一枚蛋），贴着下颌，长胡子往下拖
    const len = spec.beard === 'long' ? 0.13 : spec.beard === 'full' ? 0.07 : 0.035;
    const beard = mesh(sculptBeard(len), spec.beardMat ?? hairMat);
    J.head.add(beard);
  }
  for (const piece of headgear(spec, M, strips, s, hairSurface)) J.head.add(piece);

  // 手臂：肩 → 肘 → 手。左在 +x
  const arm = (side) => {
    const sx = side === 'L' ? 1 : -1;
    const shoulder = joint(J.spine, sx * 0.172 * b, 1.43 - 0.98, 0, 'XYZ');
    // 上臂、前臂：放样（ARM_PROFILES），三角肌处最粗、肘细、前臂靠肘有肌肉、手腕扁而细
    const upperArm = mesh(limbAlong(legSection(armShape.upper, b), [0, 0, 0], [sx * 0.02, -0.26, 0.01], { s1: 1.02, rings: 20, segs: 18 }), skin);
    upperArm.name = 'upperArm';
    shoulder.add(upperArm);
    // 袖子挂在肩关节上：胳膊抬到哪，袖子跟到哪（M3 第四轮；第一版的"袖子"是上衣挂在脊柱上的两侧，不动）
    const sleeve = mesh(sleeveGeometry(spec, sx), cloth);
    sleeve.name = `sleeve${side}`;
    shoulder.add(sleeve);
    const elbow = joint(shoulder, sx * 0.02, -0.26, 0.01);
    const foreArm = mesh(limbAlong(legSection(armShape.fore, b), [0, 0.008, 0], [0, -0.245, 0.02], { s1: 1.02, rings: 20, segs: 18 }), skin);
    foreArm.name = 'foreArm';
    elbow.add(foreArm);
    const er = legSection(armShape.upper, b)(1);
    elbow.add(mesh(blobGeometry([0, -0.004, 0.002], [er.rx * 1.02, 0.03, er.rz * 1.02], [0, 0, 0], [14, 10]), skin));   // 肘：两段之间不留台阶
    const hand = joint(elbow, 0, -0.25, 0.02);
    const built = buildHand(sx, skin);
    built.curl(0.45);
    hand.add(built.group);
    return { shoulder, elbow, hand, fingers: built };
  };
  const L = arm('L'), R = arm('R');
  J.shoulderL = L.shoulder; J.elbowL = L.elbow; J.handL = L.hand;
  J.shoulderR = R.shoulder; J.elbowR = R.elbow; J.handR = R.hand;
  const hands = { L: L.fingers, R: R.fingers };

  // 手里的东西：每样建一份挂在手上，按姿势显隐
  const props = new Map();
  const twoHanded = [];
  for (const name of spec.props ?? []) {
    // 交付的卷轴只有雅典娜的那一卷是点缀色（她把成果交到你手上——"轮到你了"）；居民手里的卷轴永远是纸色。
    // M2 美术审查：第一版抄写员的交付动作里也是一卷红色的卷轴
    const accentGift = spec.accentGift ?? spec.id === 'athena';
    if (TWO_HANDED_PROPS.includes(name)) {
      // 两只手一起拿的：挂在根上，每帧按两只手摆（updateHeld）
      const p = buildTwoHanded(name, M, accentGift);
      root.add(p);
      props.set(name, p);
      twoHanded.push([name, p]);
      continue;
    }
    const left = LEFT_HAND_PROPS.includes(name);
    const p = buildProp(name, M, accentGift, left ? 1 : -1);
    (left ? J.handL : J.handR).add(p);
    props.set(name, p);
  }
  if (spec.carry) {
    // 走和停时拿着的家什必须真的挂在那只手上：否则姿势抬起了一只空手，另一只手里的东西又不显示
    const { prop, side } = spec.carry;
    if (!props.has(prop)) throw new Error(`rig: '${spec.id}' carries '${prop}' but has no such prop`);
    if (TWO_HANDED_PROPS.includes(prop)) throw new Error(`rig: '${spec.id}' carries '${prop}', which takes both hands`);
    if ((side === 'L') !== LEFT_HAND_PROPS.includes(prop)) throw new Error(`rig: '${spec.id}' carries '${prop}' in the wrong hand`);
  }

  if (spec.glow) root.traverse((o) => { if (o.isMesh) o.userData.athena = true; });
  root.userData.chest = new THREE.Vector3(0, 1.15 * s, 0);
  root.userData.headTop = new THREE.Vector3(0, H + 0.05, 0);
  root.userData.hand = new THREE.Vector3(-0.2 * s, 0.85 * s, 0.1 * s);
  root.userData.height = H;

  function apply(p) {
    const base = J.hips.userData.base;
    J.hips.position.set(base.x + p.hips.x, base.y + p.hips.y, base.z + p.hips.z);
    J.hips.rotation.set(p.hips.rx, p.hips.ry, p.hips.rz);
    J.spine.rotation.set(spineTilt + p.spine.rx, p.spine.ry, p.spine.rz);
    J.head.rotation.set(headTilt + p.head.rx, p.head.ry, p.head.rz);
    J.shoulderL.rotation.set(p.shoulderL.rx, p.shoulderL.ry, p.shoulderL.rz);
    J.shoulderR.rotation.set(p.shoulderR.rx, p.shoulderR.ry, p.shoulderR.rz);
    J.elbowL.rotation.set(p.elbowL.rx, 0, 0);
    J.elbowR.rotation.set(p.elbowR.rx, 0, 0);
    // 手腕：rx 在前臂的挥动面里弯、ry 是前臂的转（拇指朝前 / 掌心朝上）、rz 侧偏
    J.handL.rotation.set(p.handL.rx, p.handL.ry ?? 0, p.handL.rz);
    J.handR.rotation.set(p.handR.rx, p.handR.ry ?? 0, p.handR.rz);
    J.legL.rotation.set(p.legL.rx, 0, p.legL.rz);
    J.legR.rotation.set(p.legR.rx, 0, p.legR.rz);
    J.kneeL.rotation.set(p.kneeL.rx, 0, 0);
    J.kneeR.rotation.set(p.kneeR.rx, 0, 0);
    J.skirt.rotation.set(p.skirt.rx, 0, p.skirt.rz);
    J.skirt.scale.set(1, 1, p.skirt.sz);
    for (const [name, prop] of props) prop.visible = p.propR === name || p.propL === name;
    // 手指：按拿着的东西握（拳握攥紧、捏笔半握、托着伸直，PROP_CURL）；两手一起拿的两只手都握；空手微曲（M3）
    const both = TWO_HANDED_PROPS.includes(p.propR) ? p.propR : TWO_HANDED_PROPS.includes(p.propL) ? p.propL : null;
    hands.L.curl(PROP_CURL[both ?? p.propL] ?? (p.propL ? 0.85 : 0.45));
    hands.R.curl(PROP_CURL[both ?? p.propR] ?? (p.propR ? 0.85 : 0.45));
  }

  // 两只手一起拿的东西：按两只手此刻的握点摆（根的坐标系）。在姿势摆好、矩阵更新之后调
  const _gripL = new THREE.Vector3(), _gripR = new THREE.Vector3(), _face = new THREE.Vector3(), _rootInvHeld = new THREE.Matrix4();
  const _ropeTo = new THREE.Vector3();
  const heldCtx = { face: _face, scale: s, ropeTo: null };
  /** ropeTo：缆绳另一头的世界坐标（style.ropeTo），没有就是 null。 */
  function updateHeld(ropeTo = null) {
    let any = false;
    for (const [, prop] of twoHanded) if (prop.visible) any = true;
    if (!any) return;
    root.updateMatrixWorld(true);
    _rootInvHeld.copy(root.matrixWorld).invert();
    for (const [name, prop] of twoHanded) {
      if (!prop.visible) continue;
      const at = GRIP[TWO_HANDED_GRIP[name]];
      _gripL.set(at[0], at[1], at[2]).applyMatrix4(J.handL.matrixWorld).applyMatrix4(_rootInvHeld);
      _gripR.set(-at[0], at[1], at[2]).applyMatrix4(J.handR.matrixWorld).applyMatrix4(_rootInvHeld);
      _face.set(0, 0.02, 0.09).applyMatrix4(J.head.matrixWorld).applyMatrix4(_rootInvHeld);
      heldCtx.ropeTo = ropeTo ? _ropeTo.copy(ropeTo).applyMatrix4(_rootInvHeld) : null;
      prop.userData.update(_gripL, _gripR, heldCtx);
    }
  }
  apply(restPose());

  // —— 动作之间的过渡、头的朝向、弹簧骨：pose() 的状态 ——
  const state = { key: null, from: null, blendStart: 0, applied: null, clock: null, look: { yaw: 0, pitch: 0 } };
  const _anchor = new THREE.Vector3(), _rest = new THREE.Vector3(), _dir = new THREE.Vector3(), _target = new THREE.Vector3();
  const _qParent = new THREE.Quaternion(), _qInv = new THREE.Quaternion(), _qLocal = new THREE.Quaternion();
  // 弹簧骨的碰撞体都在衣料里面（髋 0.14 < 衣摆顶环 0.155）：只拦住甩进身体里的布，不把静止的衣摆顶开
  const colliders = [
    { joint: J.hips, offset: new THREE.Vector3(0, -0.2, 0), radius: 0.13 * b, center: new THREE.Vector3() },
    { joint: J.kneeL, offset: new THREE.Vector3(0, -0.1, 0), radius: 0.09, center: new THREE.Vector3() },
    { joint: J.kneeR, offset: new THREE.Vector3(0, -0.1, 0), radius: 0.09, center: new THREE.Vector3() },
    { joint: J.head, offset: new THREE.Vector3(0, 0, 0), radius: 0.1, center: new THREE.Vector3() },
    // 背：从肩胛到腰三个球（脊柱坐标，身体单位，已在关节的世界矩阵里缩放）。披到背上的长发、面纱、斗篷搭在背上，
    // 不穿进衣服里——2026-10-12 之前背上没有碰撞体，玩家站着时长发直直垂进上衣的背里（231 个顶点里 151 个在衣服里，最深 2.7 厘米）。
    // 球 + 链的半径（0.03）要盖过上衣背面（约 0.124），又不比衣服的两侧宽（0.18），所以只会把甩进背里的点推出来
    ...[[0.4, 0.095], [0.27, 0.1], [0.14, 0.095]].map(([y, r]) => ({ joint: J.spine, offset: new THREE.Vector3(0, y, -0.012), radius: r * b, center: new THREE.Vector3(), local: true })),
  ];
  // 布料的碰撞体（世界坐标，每帧从关节取）：骨盆一根横着的胶囊、髋一个球、大腿和小腿各一根胶囊、膝一个球——腿把裙摆顶在外面
  // 半径都要装得进静止的衣料里：衣料前后压扁到 0.76（lowerGarment 的 ez），腰部半径 0.155 在前后方向只有 0.118——
  // 第一版髋球 0.15、腿缝胶囊 0.13 比衣料的前后还厚，每一步都把前后两面顶出去，结构常年拉长三四成、前襟被顶到髋前 0.3 米
  const bodyColliders = {
    pelvis: { a: new THREE.Vector3(), b: new THREE.Vector3(), r: 0.09 * b },
    hips: { a: new THREE.Vector3(), r: 0.11 * b },
    // 两腿之间从骨盆到膝的一根竖胶囊：真裙子是一个闭合的筒，前襟不会从两腿之间滑到身后去；没有它，向后甩的前襟会从腿缝里穿过去
    crotch: { a: new THREE.Vector3(), b: new THREE.Vector3(), r: 0.1 * b },
    thighL: { a: new THREE.Vector3(), b: new THREE.Vector3(), r: 0.07 * b }, thighR: { a: new THREE.Vector3(), b: new THREE.Vector3(), r: 0.07 * b },
    shinL: { a: new THREE.Vector3(), b: new THREE.Vector3(), r: 0.058 }, shinR: { a: new THREE.Vector3(), b: new THREE.Vector3(), r: 0.058 },
    kneeL: { a: new THREE.Vector3(), r: 0.065 * b }, kneeR: { a: new THREE.Vector3(), r: 0.065 * b },
  };
  const bodyList = Object.values(bodyColliders).map((c) => ({ a: c.a, b: c.b ?? null, r: c.r * s, baseR: c.r }));
  const _ankle = new THREE.Vector3(0, -0.44, 0), _hipOff = new THREE.Vector3(0, -0.06, 0), _crotchTop = new THREE.Vector3(0, -0.02, 0), _crotchBottom = new THREE.Vector3(0, -0.44, 0);
  const _rootInv = new THREE.Matrix4(), _wv = new THREE.Vector3();
  function refreshBodyColliders() {
    // 偏移是髋的局部坐标：髋的世界矩阵里已经带着身体的缩放 s，不再另乘（第一版乘了两次，高个子的髋球、腿缝偏低几毫米）
    J.legL.getWorldPosition(bodyColliders.pelvis.a); J.legR.getWorldPosition(bodyColliders.pelvis.b);
    bodyColliders.hips.a.copy(_hipOff).applyMatrix4(J.hips.matrixWorld);
    bodyColliders.crotch.a.copy(_crotchTop).applyMatrix4(J.hips.matrixWorld);
    bodyColliders.crotch.b.copy(_crotchBottom).applyMatrix4(J.hips.matrixWorld);
    J.legL.getWorldPosition(bodyColliders.thighL.a); J.kneeL.getWorldPosition(bodyColliders.thighL.b);
    J.legR.getWorldPosition(bodyColliders.thighR.a); J.kneeR.getWorldPosition(bodyColliders.thighR.b);
    J.kneeL.getWorldPosition(bodyColliders.shinL.a); bodyColliders.shinL.b.copy(_ankle).applyMatrix4(J.kneeL.matrixWorld);
    J.kneeR.getWorldPosition(bodyColliders.shinR.a); bodyColliders.shinR.b.copy(_ankle).applyMatrix4(J.kneeR.matrixWorld);
    J.kneeL.getWorldPosition(bodyColliders.kneeL.a); J.kneeR.getWorldPosition(bodyColliders.kneeR.a);
  }
  const smooth01 = (x) => { const k = Math.min(1, Math.max(0, x)); return k * k * (3 - 2 * k); };
  const clone = (p) => JSON.parse(JSON.stringify(p));

  /**
   * 布料与弹簧骨推进一帧。布料：代理布在世界里按髋的坐标系钉住、被身体的胶囊顶开，细网格重建后换回根的局部坐标写进几何；
   * 弹簧骨：锚点与安放方向取自骨头在世界里的位置和它父节点的朝向；瞬移（锚点一帧挪了 1.5 米以上）就重新安放。
   */
  function updateCloth(dt) {
    root.updateMatrixWorld(true);
    refreshBodyColliders();
    _rootInv.copy(root.matrixWorld).invert();
    for (const g of garments) {
      // 身体按径向挡（cloth.mjs 文件头）：布只会被往外推，一穿模也到不了腿的里面——最近点推出会把扫过腿轴的布推到腿后面，
      // 前襟从两腿之间滑到身后、整条腿露在裙摆外（M3 第三轮，用户实测）
      stepCloth(g.cloth, { dt, frame: J.hips.matrixWorld, body: bodyList, wind: WIND, floor: Math.max(CLOTH.floorY, root.position.y + 0.003) });
      applyFine(g.cloth, g.binding, g.world, null, J.hips.matrixWorld);
      const attr = g.mesh.geometry.attributes.position;
      for (let i = 0; i < attr.count; i++) {
        _wv.set(g.world[i * 3], g.world[i * 3 + 1], g.world[i * 3 + 2]).applyMatrix4(_rootInv);
        attr.setXYZ(i, _wv.x, _wv.y, _wv.z);
      }
      attr.needsUpdate = true;
      g.mesh.geometry.computeVertexNormals();
    }
    for (const c of colliders) {
      c.center.copy(c.offset).multiplyScalar(c.local ? 1 : s).applyMatrix4(c.joint.matrixWorld);
      c.worldRadius = c.radius * s;
    }
    const spheres = colliders.map((c) => ({ center: c.center, radius: c.worldRadius }));
    for (const strip of strips) {
      const { chain, bones, root: head, restLocal } = strip;
      head.getWorldPosition(_anchor);
      head.parent.getWorldQuaternion(_qParent);
      _rest.copy(restLocal).applyQuaternion(_qParent).normalize();
      if (strip.last && _anchor.distanceTo(strip.last) > 1.5) settleChain(chain, _anchor, _rest);
      strip.last = strip.last ? strip.last.copy(_anchor) : _anchor.clone();
      stepChain(chain, { dt, anchor: _anchor, restDir: _rest, wind: WIND, colliders: spheres });
      for (let k = 0; k < bones.length; k++) {
        segmentDirection(chain, k, _anchor, _dir);
        _qInv.copy(_qParent).invert();
        _dir.applyQuaternion(_qInv).normalize();
        _qLocal.setFromUnitVectors(restLocal, _dir);
        bones[k].quaternion.copy(_qLocal);
        _qParent.multiply(_qLocal);
      }
    }
  }

  // 一建好就把布摆到站姿下的位置：裙摆的网格挂在根上、几何却是按髋的局部坐标生成的，没摆过的人物
  // （还没 pose 过的那一帧、审查图的第 0 帧）裙摆整个落在脚边、两条腿光着（M3 第三轮对照图里看到的）
  updateCloth(0);

  /** 头看向世界里的一个点：相对身体的偏航与俯仰，超出能转的范围就回正（不扭头看身后）。 */
  function lookAngles(target) {
    if (!target) return { yaw: 0, pitch: 0 };
    J.head.getWorldPosition(_target);
    const dx = target.x - _target.x, dz = target.z - _target.z, dy = (target.y ?? _target.y) - _target.y;
    const flat = Math.hypot(dx, dz);
    if (flat < 0.3) return { yaw: 0, pitch: 0 };
    let yaw = Math.atan2(dx, dz) - root.rotation.y;
    yaw = Math.atan2(Math.sin(yaw), Math.cos(yaw));
    if (Math.abs(yaw) > LOOK.giveUp) return { yaw: 0, pitch: 0 };
    return {
      yaw: Math.max(-LOOK.maxYaw, Math.min(LOOK.maxYaw, yaw)),
      pitch: Math.max(-LOOK.maxPitch, Math.min(LOOK.maxPitch, -Math.atan2(dy, flat))),
    };
  }

  return {
    root,
    joints: J,
    props,
    spec,
    apply,
    /** 弹簧骨上的衣料（围裙、面纱、辫子、斗篷；测试与审查看它）。 */
    cloth: strips,
    /** 布料模拟的衣料（裙摆）：{ cloth, binding, mesh, world }。 */
    garments,
    /** 布料此刻的身体碰撞体（世界坐标；测试用它核对裙摆没被腿穿过）。 */
    bodyColliders: () => bodyList,
    /** 裙摆代理上的一个点（世界坐标）：j 从下沿 0 数、k 从正前方 0 顺时针数。 */
    hemPoint: (j, k, target) => clothPoint(garments[0].cloth, j, k, target),
    get state() { return state; },
    /**
     * 换上外部的身体之前（bind.mjs）：拆掉程序化的身体、衣料、头发，只留手里的东西；布料与弹簧骨不再推进
     * （它们挂的是程序化的衣料，外部网格的衣裙蒙在关节上）。关节、姿势、过渡、看向、手里的东西照旧。
     */
    dropBody() {
      const keep = new Set();
      for (const prop of props.values()) prop.traverse((o) => keep.add(o));
      const doomed = [];
      root.traverse((o) => { if (o.isMesh && !keep.has(o)) doomed.push(o); });
      for (const o of doomed) o.parent.remove(o);
      garments.length = 0;
      strips.length = 0;
    },
    /**
     * 摆一个动作在 t 毫秒时的姿势（长衣的人腿摆得小，免得脚从衣摆里踢出来）。走和停的时候，居民手里拿着
     * 他那一行的家什（spec.carry）——测杆、权杖、石板远看认得出是谁，不只在做事的那几秒出现。
     * M3：换了动作（或做事的样子、说 / 听）就从上一帧的姿势过渡过去（BLEND_MS）；style.lookAt 是头要看的点（世界坐标）；
     * style.clock 是这一帧的时刻（毫秒，缺省 performance.now()——测试传它，过渡和弹簧骨才可复现）。
     * style.ropeTo 是缆绳另一头拴着的地方（世界坐标；收缆时用，见 buildTwoHanded 的 rope）。
     */
    pose(action, t, style = {}) {
      const now = style.clock ?? performance.now();
      const dt = state.clock == null ? 1 / 60 : Math.min(0.1, Math.max(0, (now - state.clock) / 1000));
      state.clock = now;
      const full = {
        legSwing: !longGarment ? 0.42 : spec.hem > 0.15 ? 0.32 : 0.24,   // 裙子到小腿中段的人步子放开一点
        carry: spec.carry ?? null,
        seed: spec.seed ?? 0,
        ...style,
        stride: style.stride ?? spec.stride,
      };
      let target = poseAt(action, t, full);
      const key = `${action}:${full.variant ?? ''}:${full.speaker === false ? 'listen' : 'speak'}`;
      if (state.key !== null && state.key !== key && state.applied) {
        state.from = clone(state.applied);
        state.blendStart = now;
      }
      state.key = key;
      if (state.from) {
        const k = (now - state.blendStart) / BLEND_MS;
        if (k >= 1) state.from = null;
        else target = blendPoses(state.from, target, smooth01(k));
      }
      // 头的朝向：平滑地转过去；做事和交付时头有自己的事，不看
      const want = lookAngles(style.lookAt ?? null);
      const kl = 1 - Math.exp(-(dt * 1000) / LOOK.tauMs);
      state.look.yaw += (want.yaw - state.look.yaw) * kl;
      state.look.pitch += (want.pitch - state.look.pitch) * kl;
      if (action !== 'work' && action !== 'deliver') {
        target.head.ry += state.look.yaw;
        target.head.rx += state.look.pitch;
      }
      apply(target);
      state.applied = target;
      updateCloth(dt);
      updateHeld(style.ropeTo ?? null);
    },
  };
}
