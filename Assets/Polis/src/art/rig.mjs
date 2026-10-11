// 人物的骨架（设计稿 M2 行"人物及其动作"）。人物只在游戏视距出现（第 8 节：不做 3D 近景），所以骨架是一组刚体：
// 髋、腿（大腿 / 小腿 / 脚）、脊柱、头、肩 / 肘 / 手，衣服按衣着挂在髋和脊柱上。姿势由 poses.mjs 的纯函数给出，
// 这里只把关节角摆上去——测试测的姿势就是画出来的姿势。衣褶几何（drape）与柱身的凹槽是同一种做法
// （古典少女像本来就是这样雕的），头部的雕法取自打样。
//
// 约定：人物正面朝 +z；左边是 +x（镜头从东南看过来，看到的是人物的左前方）。所有尺寸按 1.75 米高的身体给，
// 整体按 spec.height 缩放。锚点（胸口、头顶）在根节点的坐标里，不随呼吸起伏——旁白气泡不该跟着一颤一颤。
import * as THREE from '../../vendor/three.bundle.mjs';
import { mergeGeometries } from '../../vendor/three.bundle.mjs';
import { lerp, smooth } from './noise.mjs';
import { restPose, poseAt } from './poses.mjs';

const shadowed = (o) => { o.castShadow = true; o.receiveShadow = true; return o; };
const mesh = (g, m) => shadowed(new THREE.Mesh(g, m));

/** 佩普洛斯下摆的竖褶，和柱身的凹槽是同一种几何。 */
export function drape({ y0, y1, rings = 40, segs = 96, radius, ez = 0.8, folds = [], growth = () => 1, bumps = [], hem = null }) {
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

// 头部：在球面上用几处高斯隆起和凹陷雕出眉弓、眼窝、鼻子、颧骨、嘴唇和下巴（打样）
const headCache = new Map();
export function sculptHead(beard = false) {
  const key = beard ? 'b' : 'p';
  if (headCache.has(key)) return headCache.get(key);
  const g = new THREE.SphereGeometry(1, 40, 30);
  const pos = g.attributes.position, v = new THREE.Vector3();
  const features = [
    [[0, 0.22, 0.98], 0.18, 0.05], [[0.36, 0.08, 0.93], 0.12, -0.085], [[-0.36, 0.08, 0.93], 0.12, -0.085],
    [[0, 0.0, 1.0], 0.07, 0.15], [[0, -0.14, 1.0], 0.065, 0.14], [[0.5, -0.14, 0.85], 0.17, 0.02],
    [[-0.5, -0.14, 0.85], 0.17, 0.02], [[0, -0.38, 0.93], 0.07, 0.04], [[0, -0.64, 0.77], 0.12, beard ? 0.0 : 0.05],
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
  headCache.set(key, g);
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

// ———————————————————————— 衣着 ————————————————————————

/** 下身的衣料（挂在髋上，坐标已减去髋高）。长衣到脚踝，短衣到膝上。 */
function lowerGarment(spec) {
  const hipY = 0.95;
  const long = !['exomis', 'chlamys'].includes(spec.garment);
  const y0 = long ? 0.03 : 0.5;
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
  return g;
}

/** 上身的衣料（挂在脊柱上，坐标已减去脊柱的高度）：翻折的上衣、斜披的外衣或只遮一边肩的短衣。 */
function upperGarment(spec) {
  const spineY = 0.98;
  const b = spec.build ?? 1;
  const parts = [];
  const overR = keyed([[0, 0.25 * b], [0.38, 0.17 * b], [0.52, 0.19 * b], [0.74, 0.19 * b], [1, 0.16 * b]]);
  parts.push(drape({
    y0: 0.86, y1: 1.47, rings: 24, segs: 64, ez: 0.62,
    radius: (t) => overR(t),
    folds: [{ n: 9, amp: 0.012, ph: 0.9, wobble: 0.3, wf: 2.0 }],
    growth: (t) => (1 - t) * 1.2 + 0.15,
    hem: { amp: spec.garment === 'peplos' ? 0.024 : 0.01, n: 5, ph: 1.2 },
  }));
  const belt = new THREE.TorusGeometry(0.173 * b, 0.012, 6, 40);
  belt.rotateX(Math.PI / 2); belt.scale(1, 1, 0.63); belt.translate(0, 1.095, 0);
  parts.push(belt);
  parts.push(blobGeometry([0, 1.455, 0], [0.165 * b, 0.07, 0.1]));
  if (spec.garment === 'himation' || spec.garment === 'chlamys') {
    // 斜披的外衣：从左肩斜绕到右腰的一道厚布
    const sash = new THREE.TorusGeometry(0.21 * b, 0.055, 6, 20, Math.PI * 1.15);
    sash.rotateZ(-0.75); sash.rotateY(0.2); sash.scale(1, 1.55, 0.78); sash.translate(-0.01, 1.16, 0.0);
    parts.push(sash);
  }
  const g = merge(parts);
  g.translate(0, -spineY, 0);
  return g;
}

// ———————————————————————— 头饰与手里的东西 ————————————————————————

function headgear(spec, M) {
  const out = [];
  switch (spec.headgear) {
    case 'fillet': {
      // 发带贴着头发：头在发带那个高度的截面是椭圆（前后比左右深），环也压成椭圆，不再浮在头外一圈
      const t = new THREE.Mesh(new THREE.TorusGeometry(0.086, 0.004, 6, 40), M.gold);
      t.rotation.x = Math.PI / 2 - 0.7; t.scale.set(1, 1.12, 1); t.position.set(0, 0.03, -0.012);
      out.push(t);
      break;
    }
    case 'diadem': {
      const t = new THREE.Mesh(new THREE.TorusGeometry(0.094, 0.012, 6, 32), M.gold);
      t.rotation.x = Math.PI / 2 - 0.5; t.position.set(0, 0.05, 0);
      out.push(t);
      break;
    }
    case 'headband': {
      // M2 美术审查：第一版的环比那个高度的头宽出一圈、管径 1.2 厘米，近看像一圈浮着的帽檐。改成细带、贴着头发
      const t = new THREE.Mesh(new THREE.TorusGeometry(0.084, 0.006, 6, 32), M.clothShades[1]);
      t.rotation.x = Math.PI / 2 - 0.25; t.scale.set(1, 1.15, 1); t.position.set(0, 0.045, -0.004);
      out.push(t);
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
      // 女祭司的面纱：从头顶披到肩后
      const v = mesh(drape({ y0: -0.28, y1: 0.12, rings: 10, segs: 32, ez: 0.9, radius: (t) => lerp(0.2, 0.11, t) }), M.clothShades[0]);
      v.position.set(0, 0.0, -0.035);
      out.push(v);
      const wreath = mesh(new THREE.TorusGeometry(0.1, 0.018, 6, 24), M.laurel);
      wreath.rotation.x = Math.PI / 2 - 0.3; wreath.position.set(0, 0.06, 0.005);
      out.push(wreath);
      break;
    }
    case 'sakkos': {
      const s = mesh(blobGeometry([0, 0.01, -0.07], [0.1, 0.1, 0.13]), M.clothShades[2]);
      out.push(s);
      break;
    }
    case 'hood': {
      // 你（玩家）：兜帽罩住头
      const h = mesh(new THREE.SphereGeometry(0.125, 20, 14, 0, Math.PI * 2, 0, Math.PI * 0.62), spec.cloth);
      h.position.set(0, 0.012, -0.022); h.rotation.x = -0.32;
      out.push(h);
      break;
    }
    default:
      break;
  }
  return out;
}

/** 挂在左手上的小件（其余挂在右手）。 */
export const LEFT_HAND_PROPS = Object.freeze(['tablet', 'palette', 'abacus', 'chisel', 'sealed']);

/** 手里能拿的小件：按名字建好、挂在手上，姿势里点到名字的才显示。 */
function buildProp(name, M, accentGift) {
  const g = new THREE.Group();
  const add = (geo, mat, p = [0, 0, 0], r = [0, 0, 0]) => { const m = mesh(geo, mat); m.position.set(...p); m.rotation.set(...r); g.add(m); return m; };
  switch (name) {
    case 'scroll':
      add(new THREE.CylinderGeometry(0.03, 0.03, 0.26, 10), M.papyrus, [0, 0, 0.04], [Math.PI / 2, 0, 0]);
      break;
    case 'scrollOpen':
      for (const x of [-0.12, 0.12]) add(new THREE.CylinderGeometry(0.022, 0.022, 0.22, 10), M.papyrus, [x, 0, 0.06]);
      add(new THREE.PlaneGeometry(0.24, 0.2), M.papyrus, [0, 0.0, 0.07], [-1.15, 0, 0]);
      g.position.x = 0.1;   // 双手捧着：挂在右手上，往两手中间挪一点
      break;
    case 'tablet':
      add(new THREE.BoxGeometry(0.24, 0.018, 0.17), M.wood, [0, 0, 0.06]);
      break;
    case 'stylus':
      add(new THREE.CylinderGeometry(0.005, 0.005, 0.15, 6), M.bronze, [0, 0, 0.04], [0.6, 0, 0]);
      break;
    case 'hammer':
      add(new THREE.CylinderGeometry(0.016, 0.018, 0.36, 8), M.wood, [0, -0.05, 0.02]);
      add(new THREE.BoxGeometry(0.15, 0.065, 0.065), M.bronze, [0, 0.12, 0.02]);
      break;
    case 'mallet':
      add(new THREE.CylinderGeometry(0.014, 0.016, 0.26, 8), M.wood, [0, -0.02, 0.02]);
      add(new THREE.CylinderGeometry(0.05, 0.05, 0.12, 10), M.darkWood, [0, 0.11, 0.02], [0, 0, Math.PI / 2]);
      break;
    case 'chisel':
      add(new THREE.CylinderGeometry(0.008, 0.012, 0.2, 6), M.bronze, [0, 0.02, 0.06], [1.2, 0, 0]);
      break;
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
    case 'rope': {
      // 收缆：一段缆从手里往前斜下到船上，一段从手里垂到身后的地上（姿势里手腕补正，手的坐标和地面对齐）。
      // 第一版是一根从手里平伸出去的直杆，远看是一根长矛
      const ahead = [0, -0.5, 0.866], behind = [0, -0.8, -0.6];
      add(new THREE.CylinderGeometry(0.018, 0.018, 1.4, 6), M.awning, ahead.map((v) => v * 0.7), [Math.atan2(ahead[2], ahead[1]), 0, 0]);
      add(new THREE.CylinderGeometry(0.018, 0.018, 1.6, 6), M.awning, behind.map((v) => v * 0.8), [Math.atan2(behind[2], behind[1]), 0, 0]);
      add(new THREE.TorusGeometry(0.03, 0.016, 6, 12), M.awning, [0, 0, 0.01], [0, 0, 0]);
      break;
    }
    case 'brush':
      add(new THREE.CylinderGeometry(0.006, 0.008, 0.22, 6), M.wood, [0, 0.04, 0.04], [0.5, 0, 0]);
      break;
    case 'palette':
      add(new THREE.CylinderGeometry(0.11, 0.11, 0.012, 16), M.wood, [0, 0, 0.06]);
      for (let k = 0; k < 3; k++) add(new THREE.SphereGeometry(0.018, 6, 4), k === 1 ? M.glaze : M.papyrus, [-0.05 + k * 0.05, 0.012, 0.06]);
      break;
    case 'abacus':
      add(new THREE.BoxGeometry(0.3, 0.02, 0.18), M.darkWood, [0, 0, 0.07]);
      for (let k = 0; k < 6; k++) add(new THREE.SphereGeometry(0.014, 6, 4), M.papyrus, [-0.11 + k * 0.045, 0.016, 0.07 + (k % 2) * 0.04]);
      break;
    case 'phiale':
      add(new THREE.SphereGeometry(0.1, 16, 8, 0, Math.PI * 2, Math.PI * 0.65, Math.PI * 0.35), M.gold, [0, 0.02, 0.07], [Math.PI, 0, 0]);
      break;
    case 'gift': {
      // 交付：一卷红色的卷轴（点缀色，"轮到你了"的那样东西）
      const rod = new THREE.Mesh(new THREE.CylinderGeometry(0.04, 0.04, 0.32, 14), accentGift ? M.accent : M.papyrus);
      rod.rotation.z = Math.PI / 2;
      rod.position.set(0.1, 0, 0.1);
      rod.castShadow = true;
      if (accentGift) rod.userData.accent = true;
      g.add(rod);
      break;
    }
    case 'sealed': {
      // 等审批：手中石板捆着一道红绳、压着一枚红封印——"轮到你了"。石板捧在胸前时是竖着的，
      // 红绳与封印穿过板子、两面都露出来：第一版的封印只在一面，正好贴着她的胸口，游戏视距里看不见红
      add(new THREE.BoxGeometry(0.26, 0.02, 0.18), M.wood, [0, 0, 0.06]);
      const cord = new THREE.Mesh(new THREE.BoxGeometry(0.036, 0.03, 0.188), M.accent);
      cord.position.set(0, 0, 0.06);
      const seal = new THREE.Mesh(new THREE.CylinderGeometry(0.066, 0.066, 0.05, 18), M.accent);
      seal.position.set(0, 0, 0.06);
      for (const m of [cord, seal]) { m.castShadow = true; m.userData.accent = true; g.add(m); }
      break;
    }
    default:
      throw new Error(`rig: unknown prop '${name}'`);
  }
  g.visible = false;
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

  const J = {};
  J.hips = joint(body, 0, 0.95, 0);
  J.hips.userData.base = new THREE.Vector3(0, 0.95, 0);
  J.legL = joint(J.hips, 0.085 * b, -0.02, 0);
  J.legR = joint(J.hips, -0.085 * b, -0.02, 0);
  J.kneeL = joint(J.legL, 0, -0.44, 0);
  J.kneeR = joint(J.legR, 0, -0.44, 0);
  const legMat = spec.gold ? M.maiden : M.skin;
  // 长衣（到脚踝）里看不见腿：只建脚。M2 美术审查：走路时屈膝的小腿会从衣摆前面戳出来一截肉色
  const longGarment = !['exomis', 'chlamys'].includes(spec.garment);
  for (const [leg, knee] of [[J.legL, J.kneeL], [J.legR, J.kneeR]]) {
    if (!longGarment) {
      leg.add(mesh(limbGeometry([0, 0, 0], [0, -0.44, 0.0], 0.062 * b), legMat));
      knee.add(mesh(limbGeometry([0, 0, 0], [0, -0.4, -0.01], 0.048 * b), legMat));
    }
    // 脚：滑行的神不迈步，也就不露脚（衣摆向后拖时，两只深色的鞋会从衣摆前面露成两个黑点）
    if (!spec.glides) knee.add(mesh(blobGeometry([0, -0.455, 0.05], [0.042, 0.03, 0.1]), spec.gold ? M.maiden : M.leather));
  }
  J.skirt = joint(J.hips, 0, 0, 0);
  J.skirt.add(mesh(lowerGarment(spec), cloth));
  if (spec.apron) {
    // 铁匠、工匠的皮围裙：从腰带垂到膝上、顺着身体弯过来的一片浅色鞣皮，挂在下摆上，走路时跟着下摆一起晃。
    // 第一版是挂在胸前的一块平板、用深皮，去色后是一块纯黑的长方形
    const apron = new THREE.CylinderGeometry(0.2 * b, 0.27 * b, 0.6, 18, 4, true, -1.0, 2.0);
    apron.scale(1, 1, 0.74);
    apron.translate(0, 0.8 - 0.95, 0);
    J.skirt.add(mesh(apron, M.hide));
  }

  J.spine = joint(J.hips, 0, 0.03, 0);
  J.spine.add(mesh(upperGarment(spec), cloth));
  const skinTorso = [
    ...[-1, 1].map((sx) => blobGeometry([sx * 0.168 * b, 1.432 - 0.98, 0], [0.04, 0.042, 0.042])),
    // 脖子：第一版 4.2 厘米，在宽大的上衣上面细得像一根杆
    limbGeometry([0, 1.47 - 0.98, 0.0], [0, 1.6 - 0.98, 0.015], 0.047),
  ];
  J.spine.add(mesh(merge(skinTorso), skin));

  J.head = joint(J.spine, 0, 0.705, 0.014);
  const headGeo = sculptHead(!!spec.beard);
  if (spec.faceless) {
    J.head.add(mesh(blobGeometry([0, 0.0, 0], [0.078, 0.104, 0.09], [0, 0, 0], [16, 12]), skin));
  } else {
    J.head.add(mesh(headGeo, skin));
  }
  J.head.rotation.x = 0.06;
  // 头发
  if (spec.hair !== 'hidden' && spec.hair !== 'bald') {
    const kind = spec.hair === 'short' || spec.hair === 'receding' ? 'short' : 'cap';
    const hair = mesh(sculptHair(kind), hairMat);
    // 发际线：发罩往后转。M2 美术审查：长发（发髻、辫子）的发罩第一版压到眉毛上，近看是一顶帽子
    hair.scale.set(0.087, 0.113, 0.1); hair.position.set(0, 0.008, -0.01);
    hair.rotation.x = spec.hair === 'receding' ? -1.4 : kind === 'cap' ? -1.15 : -0.98;
    J.head.add(hair);
  }
  if (spec.hair === 'bun' || spec.hair === 'braids') {
    const bun = mesh(sculptHair('bun'), hairMat);
    bun.scale.set(0.055, 0.05, 0.05); bun.position.set(0, -0.03, -0.11);
    J.head.add(bun);
    if (spec.hair === 'braids') for (const sx of [-1, 1]) J.head.add(mesh(limbGeometry([sx * 0.07, -0.06, -0.03], [sx * 0.1, -0.3, 0.02], 0.016), hairMat));
  }
  if (spec.beard) {
    const len = spec.beard === 'long' ? 0.13 : spec.beard === 'full' ? 0.07 : 0.035;
    const beard = mesh(blobGeometry([0, -0.075 - len * 0.4, 0.055], [0.07, 0.04 + len * 0.6, 0.055]), spec.beardMat ?? hairMat);
    J.head.add(beard);
  }
  for (const piece of headgear(spec, M)) J.head.add(piece);

  // 手臂：肩 → 肘 → 手。左在 +x
  const arm = (side) => {
    const sx = side === 'L' ? 1 : -1;
    const shoulder = joint(J.spine, sx * 0.172 * b, 1.43 - 0.98, 0, 'XYZ');
    shoulder.add(mesh(limbGeometry([0, 0, 0], [sx * 0.02, -0.26, 0.01], 0.041 * b), skin));
    const elbow = joint(shoulder, sx * 0.02, -0.26, 0.01);
    elbow.add(mesh(limbGeometry([0, 0, 0], [0, -0.23, 0.02], 0.033 * b), skin));
    const hand = joint(elbow, 0, -0.25, 0.02);
    hand.add(mesh(blobGeometry([0, -0.02, 0.01], [0.024, 0.045, 0.032]), skin));
    return { shoulder, elbow, hand };
  };
  const L = arm('L'), R = arm('R');
  J.shoulderL = L.shoulder; J.elbowL = L.elbow; J.handL = L.hand;
  J.shoulderR = R.shoulder; J.elbowR = R.elbow; J.handR = R.hand;

  // 手里的东西：每样建一份挂在手上，按姿势显隐
  const props = new Map();
  for (const name of spec.props ?? []) {
    // 交付的卷轴只有雅典娜的那一卷是点缀色（她把成果交到你手上——"轮到你了"）；居民手里的卷轴永远是纸色。
    // M2 美术审查：第一版抄写员的交付动作里也是一卷红色的卷轴
    const p = buildProp(name, M, spec.accentGift ?? spec.id === 'athena');
    const hand = LEFT_HAND_PROPS.includes(name) ? J.handL : J.handR;
    hand.add(p);
    props.set(name, p);
  }
  if (spec.carry) {
    // 走和停时拿着的家什必须真的挂在那只手上：否则姿势抬起了一只空手，另一只手里的东西又不显示
    const { prop, side } = spec.carry;
    if (!props.has(prop)) throw new Error(`rig: '${spec.id}' carries '${prop}' but has no such prop`);
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
    J.spine.rotation.set(p.spine.rx, p.spine.ry, p.spine.rz);
    J.head.rotation.set(0.06 + p.head.rx, p.head.ry, p.head.rz);
    J.shoulderL.rotation.set(p.shoulderL.rx, p.shoulderL.ry, p.shoulderL.rz);
    J.shoulderR.rotation.set(p.shoulderR.rx, p.shoulderR.ry, p.shoulderR.rz);
    J.elbowL.rotation.set(p.elbowL.rx, 0, 0);
    J.elbowR.rotation.set(p.elbowR.rx, 0, 0);
    J.handL.rotation.set(p.handL.rx, 0, p.handL.rz);
    J.handR.rotation.set(p.handR.rx, 0, p.handR.rz);
    J.legL.rotation.set(p.legL.rx, 0, p.legL.rz);
    J.legR.rotation.set(p.legR.rx, 0, p.legR.rz);
    J.kneeL.rotation.set(p.kneeL.rx, 0, 0);
    J.kneeR.rotation.set(p.kneeR.rx, 0, 0);
    J.skirt.rotation.set(p.skirt.rx, 0, p.skirt.rz);
    J.skirt.scale.set(1, 1, p.skirt.sz);
    for (const [name, prop] of props) prop.visible = p.propR === name || p.propL === name;
  }
  apply(restPose());

  return {
    root,
    joints: J,
    props,
    spec,
    apply,
    /**
     * 摆一个动作在 t 毫秒时的姿势（长衣的人腿摆得小，免得脚从衣摆里踢出来）。走和停的时候，居民手里拿着
     * 他那一行的家什（spec.carry）——测杆、权杖、石板远看认得出是谁，不只在做事的那几秒出现。
     */
    pose(action, t, style = {}) {
      apply(poseAt(action, t, {
        legSwing: longGarment ? 0.24 : 0.42,
        carry: spec.carry ?? null,
        ...style,
        glide: style.glide ?? (action === 'walk' && spec.glides),
      }));
    },
  };
}
