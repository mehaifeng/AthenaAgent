// 人物：只在游戏视距出现（设计稿 8 节：人物不做 3D 近景）。雅典娜取自打样：真身，不披甲、不持兵，
// 一件佩普洛斯；让人认出她是神的是光，不是装备。玩家、黄金侍女、雕像是 M0 新加的，用同一套"衣褶"几何。
import * as THREE from '../../vendor/three.bundle.mjs';
import { mergeGeometries, mergeVertices } from '../../vendor/three.bundle.mjs';
import { lerp, smooth, valueNoise, fbm } from './noise.mjs';

const shadowed = (o) => { o.castShadow = true; o.receiveShadow = true; return o; };
const mesh = (g, m) => shadowed(new THREE.Mesh(g, m));

/** 佩普洛斯下摆的竖褶，和柱身的凹槽是同一种几何——古典少女像本来就是这样雕的。 */
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

function keyed(keys) {
  return (t) => {
    for (let i = 0; i < keys.length - 1; i++) {
      const [t0, r0] = keys[i], [t1, r1] = keys[i + 1];
      if (t <= t1) return lerp(r0, r1, smooth(t0, t1, t));
    }
    return keys[keys.length - 1][1];
  };
}

function limbGeometry(a, b, r) {
  const A = new THREE.Vector3(...a), B = new THREE.Vector3(...b);
  const dir = new THREE.Vector3().subVectors(B, A);
  const len = dir.length();
  const g = new THREE.CapsuleGeometry(r, Math.max(0.001, len), 4, 12);
  g.applyQuaternion(new THREE.Quaternion().setFromUnitVectors(new THREE.Vector3(0, 1, 0), dir.normalize()));
  const mid = A.clone().addScaledVector(B.clone().sub(A), 0.5);
  g.translate(mid.x, mid.y, mid.z);
  return g;
}

function blobGeometry(at, scale, rot = [0, 0, 0], detail = [24, 16]) {
  const g = new THREE.SphereGeometry(1, detail[0], detail[1]);
  g.applyMatrix4(new THREE.Matrix4().compose(new THREE.Vector3(...at), new THREE.Quaternion().setFromEuler(new THREE.Euler(...rot)), new THREE.Vector3(...scale)));
  return g;
}

function merge(list) {
  return mergeGeometries(list.map((g) => {
    const n = g.index ? g.toNonIndexed() : g;
    for (const name of Object.keys(n.attributes)) if (!['position', 'normal', 'uv'].includes(name)) n.deleteAttribute(name);
    if (!n.attributes.normal) n.computeVertexNormals();
    // 合并要求每件都有同样的属性：没有 UV 的（叶团）补一份零
    if (!n.attributes.uv) n.setAttribute('uv', new THREE.Float32BufferAttribute(new Float32Array(n.attributes.position.count * 2), 2));
    return n;
  }));
}

// 头部：在球面上用几处高斯隆起和凹陷雕出眉弓、眼窝、鼻子、颧骨、嘴唇和下巴（打样）
function sculptHead() {
  const g = new THREE.SphereGeometry(1, 48, 36);
  const pos = g.attributes.position, v = new THREE.Vector3();
  const features = [
    [[0, 0.22, 0.98], 0.18, 0.05], [[0.36, 0.08, 0.93], 0.12, -0.085], [[-0.36, 0.08, 0.93], 0.12, -0.085],
    [[0, 0.0, 1.0], 0.07, 0.15], [[0, -0.14, 1.0], 0.065, 0.14], [[0.5, -0.14, 0.85], 0.17, 0.02],
    [[-0.5, -0.14, 0.85], 0.17, 0.02], [[0, -0.38, 0.93], 0.07, 0.04], [[0, -0.64, 0.77], 0.12, 0.05],
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

function sculptHair(kind = 'cap') {
  const g = kind === 'bun'
    ? new THREE.SphereGeometry(1, 32, 24)
    : new THREE.SphereGeometry(1, 72, 40, 0, Math.PI * 2, 0, Math.PI * 0.66);
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

/** 雅典娜（打样）。右前臂抬起、掌心向上（像在说话或递出什么），左手垂下拈着衣襟。 */
export function buildAthena(M) {
  const A = new THREE.Group();
  const skirt = drape({
    y0: 0.015, y1: 1.14, rings: 48, segs: 112, ez: 0.74,
    radius: (t) => lerp(0.29, 0.155, Math.pow(t, 0.75)),
    folds: [
      { n: 13, amp: 0.021, ph: 0.4, wobble: 0.35, wf: 2.1 },
      { n: 27, amp: 0.007, ph: 1.3, wobble: 0.5, wf: 3.3 },
      { n: 6, amp: 0.012, ph: 2.2, wobble: 0.2, wf: 1.2 },
    ],
    growth: (t) => 1.0 - 0.8 * t,
    bumps: [{ th: 0.42, y: 0.5, amp: 0.06, w: 0.36, h: 0.17 }],
    hem: { amp: 0.012, n: 7, ph: 0.5 },
  });
  A.add(mesh(skirt, M.cloth));
  const overR = keyed([[0, 0.255], [0.38, 0.17], [0.52, 0.192], [0.74, 0.188], [1, 0.158]]);
  A.add(mesh(drape({
    y0: 0.86, y1: 1.47, rings: 36, segs: 112, ez: 0.62,
    radius: (t) => overR(t),
    folds: [{ n: 9, amp: 0.012, ph: 0.9, wobble: 0.3, wf: 2.0 }, { n: 19, amp: 0.0045, ph: 0.2, wobble: 0.4, wf: 3.0 }],
    growth: (t) => (1 - t) * 1.2 + 0.15,
    hem: { amp: 0.024, n: 5, ph: 1.2 },
  }), M.cloth));
  const belt = new THREE.TorusGeometry(0.173, 0.012, 8, 64);
  belt.rotateX(Math.PI / 2); belt.scale(1, 1, 0.63); belt.translate(0, 1.095, 0);
  A.add(mesh(merge([belt, blobGeometry([0, 1.455, 0], [0.165, 0.07, 0.1])]), M.cloth));
  A.add(mesh(merge([-1, 1].map((sx) => blobGeometry([sx * 0.11, 1.49, 0.05], [0.014, 0.014, 0.014], [0, 0, 0], [8, 6]))), M.gold));

  const skin = [
    ...[-1, 1].map((sx) => blobGeometry([sx * 0.168, 1.432, 0], [0.04, 0.042, 0.042])),
    limbGeometry([0.172, 1.43, 0.0], [0.235, 1.19, 0.07], 0.042),
    limbGeometry([0.235, 1.19, 0.07], [0.33, 1.25, 0.24], 0.034),
    blobGeometry([0.352, 1.262, 0.275], [0.032, 0.016, 0.05], [0.3, 0.55, 0.1]),
    limbGeometry([-0.172, 1.43, 0.0], [-0.215, 1.17, -0.03], 0.042),
    limbGeometry([-0.215, 1.17, -0.03], [-0.226, 0.95, 0.04], 0.034),
    blobGeometry([-0.229, 0.895, 0.05], [0.022, 0.05, 0.03]),
    limbGeometry([0, 1.47, 0.0], [0, 1.6, 0.015], 0.042),
    ...[-1, 1].map((sx) => blobGeometry([sx * 0.065, 0.018, 0.2], [0.03, 0.016, 0.055])),
  ];
  A.add(mesh(merge(skin), M.skin));

  const head = new THREE.Group(); head.position.set(0, 1.705, 0.014); head.rotation.x = 0.1; A.add(head);
  head.add(mesh(sculptHead(), M.skin));
  const hair = mesh(sculptHair(), M.hair);
  hair.scale.set(0.087, 0.113, 0.1); hair.position.set(0, 0.008, -0.01); hair.rotation.x = -0.95; head.add(hair);
  const bun = mesh(sculptHair('bun'), M.hair);
  bun.scale.set(0.055, 0.05, 0.05); bun.position.set(0, -0.03, -0.11); head.add(bun);
  const fillet = new THREE.Mesh(new THREE.TorusGeometry(0.093, 0.004, 6, 48), M.gold);
  fillet.rotation.x = Math.PI / 2 - 0.7; fillet.position.set(0, 0.03, -0.012); head.add(fillet);

  // 她的光照在脚下的一圈地面上；身边的光晕在调色里加，只加在身体轮廓之外
  const light = new THREE.PointLight(0xfff6e8, 1.1, 4.5, 2);
  light.position.set(0, 0.45, 0.3);
  A.add(light);
  A.traverse((o) => { if (o.isMesh) o.userData.athena = true; });
  A.userData.chest = new THREE.Vector3(0, 1.15, 0);
  A.userData.headTop = new THREE.Vector3(0, 1.9, 0);
  A.userData.hand = new THREE.Vector3(0.352, 1.29, 0.29);
  A.userData.light = light;
  return A;
}

/** 玩家：一件较深的希玛纯（斗篷）罩着长衣，没有光——光只属于雅典娜。 */
export function buildPlayer(M) {
  const P = new THREE.Group();
  const robe = drape({
    y0: 0.02, y1: 1.42, rings: 24, segs: 56, ez: 0.78,
    radius: keyed([[0, 0.27], [0.55, 0.2], [0.78, 0.19], [0.92, 0.2], [1, 0.12]]),
    folds: [{ n: 9, amp: 0.016, ph: 0.2, wobble: 0.4, wf: 2.4 }, { n: 17, amp: 0.006, ph: 1.0, wobble: 0.3, wf: 3.1 }],
    growth: (t) => 1 - 0.6 * t,
    hem: { amp: 0.015, n: 5, ph: 0.3 },
  });
  P.add(mesh(robe, M.playerCloth));
  // 斜挎的一道披肩
  const sash = new THREE.TorusGeometry(0.2, 0.05, 6, 24, Math.PI * 1.1);
  sash.rotateZ(-0.7); sash.rotateY(0.2); sash.scale(1, 1.6, 0.75); sash.translate(-0.02, 1.15, 0.0);
  P.add(mesh(sash, M.playerCloth));
  P.add(mesh(merge([
    limbGeometry([0, 1.42, 0], [0, 1.55, 0.01], 0.045),
    blobGeometry([0, 1.66, 0.01], [0.085, 0.11, 0.095]),
    limbGeometry([-0.2, 1.38, 0], [-0.24, 0.98, 0.05], 0.04),
    limbGeometry([0.2, 1.38, 0], [0.24, 0.98, 0.05], 0.04),
  ]), M.skin));
  P.add(mesh(blobGeometry([0, 1.71, -0.015], [0.092, 0.08, 0.098]), M.hair));
  P.userData.headTop = new THREE.Vector3(0, 1.85, 0);
  return P;
}

/** 黄金侍女（子代理）：《伊利亚特》里赫菲斯托斯的黄金侍女。比雅典娜小一号，通身金色、无脸的简化造型。 */
export function maidenGeometry() {
  return merge([
    drape({
      y0: 0.0, y1: 1.15, rings: 14, segs: 32, ez: 0.8,
      radius: keyed([[0, 0.22], [0.6, 0.15], [1, 0.12]]),
      folds: [{ n: 8, amp: 0.012, ph: 0.4, wobble: 0.3, wf: 2 }],
    }),
    blobGeometry([0, 1.3, 0], [0.075, 0.1, 0.085], [0, 0, 0], [16, 12]),
    limbGeometry([0.16, 1.1, 0], [0.24, 0.85, 0.12], 0.03),
    limbGeometry([-0.16, 1.1, 0], [-0.24, 0.85, 0.12], 0.03),
  ]);
}

/** 雕塑园里的立像：便宜的一体几何（衣褶 + 头 + 一条手臂），去色后是一尊尊白色大理石像。 */
export function statueGeometry() {
  const n = valueNoise(7);
  return merge([
    drape({
      y0: 0.0, y1: 1.55, rings: 16, segs: 36, ez: 0.75,
      radius: (t, th) => keyed([[0, 0.3], [0.55, 0.2], [1, 0.15]])(t) + 0.015 * fbm(n, th * 2, t * 4, 8, 2),
      folds: [{ n: 11, amp: 0.016, ph: 0.6, wobble: 0.3, wf: 2.2 }],
    }),
    blobGeometry([0, 1.72, 0.01], [0.1, 0.13, 0.11], [0, 0, 0], [16, 12]),
    limbGeometry([0.2, 1.5, 0], [0.32, 1.9, 0.12], 0.045),
  ]);
}

/** 雅典娜手里的小件：卷轴、石板、锤子、交付用的红卷轴（点缀色，只给"轮到你了"）。 */
export function buildProps(M) {
  const scroll = new THREE.Group();
  for (const x of [-0.11, 0.11]) {
    const rod = mesh(new THREE.CylinderGeometry(0.022, 0.022, 0.2, 10), M.papyrus);
    rod.position.set(x, 0, 0);
    scroll.add(rod);
  }
  const sheet = mesh(new THREE.PlaneGeometry(0.22, 0.18), M.papyrus);
  sheet.rotation.x = -1.1; sheet.position.set(0, 0.0, 0.02);
  scroll.add(sheet);

  const tablet = new THREE.Group();
  tablet.add(mesh(new THREE.BoxGeometry(0.26, 0.02, 0.18), M.wood));
  const stylus = mesh(new THREE.CylinderGeometry(0.005, 0.005, 0.16, 6), M.bronze);
  stylus.rotation.z = 0.9; stylus.position.set(0.05, 0.06, 0);
  tablet.add(stylus);

  const hammer = new THREE.Group();
  const handle = mesh(new THREE.CylinderGeometry(0.018, 0.02, 0.42, 8), M.wood);
  handle.position.y = -0.12;
  hammer.add(handle);
  const headM = mesh(new THREE.BoxGeometry(0.16, 0.07, 0.07), M.bronze);
  headM.position.y = 0.09;
  hammer.add(headM);

  const gift = new THREE.Group();
  const giftRod = new THREE.Mesh(new THREE.CylinderGeometry(0.04, 0.04, 0.32, 14), M.accent);
  giftRod.rotation.z = Math.PI / 2;
  giftRod.castShadow = true;
  gift.add(giftRod);
  const giftTie = new THREE.Mesh(new THREE.TorusGeometry(0.043, 0.008, 6, 16), M.papyrus);
  giftTie.rotation.y = Math.PI / 2;
  gift.add(giftTie);
  giftRod.userData.accent = true;   // 只有这一件在调色里留色

  for (const g of [scroll, tablet, hammer, gift]) { g.visible = false; g.traverse((o) => { if (o.isMesh) o.userData.athena = true; }); }
  return { scroll, tablet, hammer, gift };
}

/**
 * 摆在地上等你收下的成果：一卷红色的卷轴（点缀色）放在一块圆石座上，脚下一圈白环一胀一缩。
 * 红绿色弱的人看红色和深灰差别不大，所以"轮到你了"不能只靠颜色：白环提供亮度对比和脉动（设计稿 8 节）。
 */
export function buildDeliveredScroll(M) {
  const g = new THREE.Group();
  const base = mesh(new THREE.CylinderGeometry(0.46, 0.5, 0.1, 24), M.marble);
  base.position.y = 0.05;
  g.add(base);
  const rod = new THREE.Mesh(new THREE.CylinderGeometry(0.11, 0.11, 0.78, 18), M.accent);
  rod.rotation.z = Math.PI / 2;
  rod.position.y = 0.22;
  rod.castShadow = true;
  rod.userData.accent = true;
  g.add(rod);
  for (const x of [-0.42, 0.42]) {
    const knob = new THREE.Mesh(new THREE.CylinderGeometry(0.035, 0.035, 0.08, 10), M.papyrus);
    knob.rotation.z = Math.PI / 2;
    knob.position.set(x, 0.22, 0);
    g.add(knob);
  }
  const tie = new THREE.Mesh(new THREE.TorusGeometry(0.115, 0.016, 6, 18), M.papyrus);
  tie.rotation.y = Math.PI / 2;
  tie.position.y = 0.22;
  g.add(tie);
  const ring = new THREE.Mesh(new THREE.TorusGeometry(0.8, 0.045, 8, 48), new THREE.MeshBasicMaterial({ color: 0xffffff }));
  ring.rotation.x = Math.PI / 2;
  ring.position.y = 0.03;
  g.add(ring);
  g.userData.ring = ring;
  return g;
}

/** 柏树（打样）。 */
export function buildCypress(M, height, seed) {
  const T = new THREE.Group();
  const pts = [];
  for (let i = 0; i <= 30; i++) {
    const t = i / 30;
    const r = 0.62 * Math.pow(Math.sin(Math.PI * Math.min(1, Math.pow(t, 0.55))), 0.7) * (1 - 0.55 * t);
    pts.push(new THREE.Vector2(Math.max(0.001, r), 0.3 + t * (height - 0.3)));
  }
  T.add(mesh(lumpy(new THREE.LatheGeometry(pts, 24), seed, 0.22, 4.5), M.cypress));
  T.add(mesh(new THREE.CylinderGeometry(0.09, 0.13, 0.5, 8), M.bark));
  return T;
}

/** 橄榄树（打样）：扭曲的干 + 银灰的冠。叶团先合并顶点，法线才是平滑的。 */
export function buildOlive(M, seed, rnd) {
  const T = new THREE.Group();
  const trunk = new THREE.CatmullRomCurve3([
    new THREE.Vector3(0, 0, 0), new THREE.Vector3(0.25, 0.6, 0.05), new THREE.Vector3(-0.12, 1.2, 0.15), new THREE.Vector3(0.1, 1.8, -0.05),
  ]);
  const bark = [new THREE.TubeGeometry(trunk, 16, 0.15, 8, false)];
  for (const [a, b] of [[[0.05, 1.6, 0], [0.95, 2.35, 0.45]], [[0.0, 1.7, 0.05], [-0.8, 2.45, -0.35]], [[0.1, 1.75, 0], [0.2, 2.6, -0.85]]]) {
    bark.push(new THREE.TubeGeometry(new THREE.LineCurve3(new THREE.Vector3(...a), new THREE.Vector3(...b)), 2, 0.07, 6, false));
  }
  T.add(mesh(merge(bark), M.bark));
  const leaves = [];
  for (let i = 0; i < 16; i++) {
    const base = new THREE.IcosahedronGeometry(0.3 + rnd() * 0.3, 2);
    base.deleteAttribute('normal'); base.deleteAttribute('uv');
    const g = lumpy(mergeVertices(base), seed + i, 0.45, 7);
    const ang = rnd() * Math.PI * 2, rad = 0.2 + rnd() * 1.2;
    g.scale(1, 0.68, 1);
    g.translate(Math.cos(ang) * rad, 2.05 + rnd() * 0.9 + (1.2 - rad) * 0.35, Math.sin(ang) * rad);
    leaves.push(g);
  }
  T.add(mesh(merge(leaves), M.olive));
  return T;
}

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
