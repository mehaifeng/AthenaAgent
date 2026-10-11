// 人物怎么拿家什、家什落在哪（M3 第三轮，用户：工匠区的 NPC 拿锤子的方式完全错误，雅典娜使用工具时也有问题）。
// 用真骨架（rig.mjs）和真城（world.mjs，合成夹具）核对：拳握的柄横穿过拳头、和前臂垂直，干活的那一头在拇指那一侧；
// 锤面落在砧面上、火钳的钳口按着铁条、木槌落在凿尾上、凿刃抵着石面、画笔落在画面上、笔尖在蜡板上；两只手一起拿的东西两只手都在。
// 第一版：锤柄顺着前臂、锤头贴在手腕上；铁匠背对铁砧朝空气抡锤；蜡板、算板、调色板竖着立在胸前；祭碗藏在手里；缆绳是两根直杆。
// 运行：node --test Tools/GameMode/web/test/
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import * as THREE from '../../../../Assets/Polis/vendor/three.bundle.mjs';
import { createLayout } from '../../../../Assets/Polis/src/layout.mjs';
import { createAssetLibrary } from '../../../../Assets/Polis/src/art/assets.mjs';
import { buildWorld } from '../../../../Assets/Polis/src/world.mjs';
import { demoAccumulation } from '../../../../Assets/Polis/src/demo.mjs';
import { buildCharacter, CHARACTER_IDS } from '../../../../Assets/Polis/src/art/characters.mjs';
import { GRIP } from '../../../../Assets/Polis/src/art/rig.mjs';

const fixture = JSON.parse(readFileSync(new URL('../fixtures/synthetic.json', import.meta.url), 'utf8'));

function stubMaterials() {
  const cache = new Map();
  return new Proxy({}, {
    get: (_, key) => {
      if (typeof key !== 'string') return undefined;
      if (!cache.has(key)) {
        cache.set(key, key.endsWith('Shades')
          ? [0, 1, 2, 3, 4].map((i) => new THREE.MeshStandardMaterial({ name: `${key}${i}` }))
          : new THREE.MeshStandardMaterial({ name: key }));
      }
      return cache.get(key);
    },
  });
}

const M = stubMaterials();
const V = (a) => new THREE.Vector3(...a);
/** 手（或别的关节）坐标系里的一点在世界里的位置。 */
const world = (joint, local) => V(local).applyMatrix4(joint.matrixWorld);
const dirOf = (joint, local) => V(local).transformDirection(joint.matrixWorld);

/** 摆一个干活的动作到 t 毫秒（先站稳，再做到 t：过渡 220 毫秒走完，布料与弹簧骨也推进过）。 */
function poseAt(figure, variant, t, extra = {}) {
  let clock = 0;
  for (let k = 0; k < 20; k++) { clock += 16; figure.pose('idle', clock, { clock }); }
  for (let k = 0; k <= 30; k++) { clock += 16; figure.pose('work', Math.max(0, t - (30 - k) * 16), { variant, clock, ...extra }); }
  figure.root.updateMatrixWorld(true);
}

test('tools: a power grip runs the handle through the fist across the forearm, with the working end out past the thumb', () => {
  // 锤、木槌、凿子、火钳：柄的方向（手的 x）和前臂（肘 → 腕）大致垂直；离拳心最远的那一头（锤头、槌头、凿尾、钳口）在拇指那一侧，
  // 离拳心 0.2 米以上。第一版的锤柄沿着手的 y——和前臂平行，锤头离手腕 0.12 米、朝着肘
  // 凿子反过来：刃从小指那一侧伸出去抵着石头（离拳心最远的就是刃），凿尾在拇指那一侧给木槌敲
  const cases = [['smith', 'forge', 'R', 'hammer', 0.27, 1], ['smith', 'forge', 'L', 'tongs', 0.4, 1], ['artisan', 'carve', 'R', 'mallet', 0.26, 1], ['artisan', 'carve', 'L', 'chisel', 0.14, -1]];
  for (const [id, variant, side, prop, reach, towardThumb] of cases) {
    const f = buildCharacter(M, id);
    poseAt(f, variant, 650);
    const hand = f.joints[`hand${side}`];
    const fist = world(hand, GRIP.fist);
    const thumb = dirOf(hand, [side === 'L' ? 1 : -1, 0, 0]);
    let far = null, farD = 0;
    f.props.get(prop).traverse((o) => {
      if (!o.isMesh) return;
      const pos = o.geometry.attributes.position, p = new THREE.Vector3();
      for (let i = 0; i < pos.count; i++) {
        p.fromBufferAttribute(pos, i).applyMatrix4(o.matrixWorld);
        const d = p.distanceTo(fist);
        if (d > farD) { farD = d; far = p.clone(); }
      }
    });
    assert.ok(farD > reach, `${id} ${prop}: the working end is ${farD.toFixed(2)} m out from the fist`);
    assert.ok(far.clone().sub(fist).dot(thumb) * towardThumb > 0, `${id} ${prop}: the working end is on the ${towardThumb > 0 ? 'thumb' : 'little-finger'} side`);
    // 柄横过手掌：从拳心到干活那一头的方向和手自己的长轴（手腕伸直时就是前臂的延长线）大致垂直——
    // 量在手的坐标系里，和这一刻手腕弯了多少无关（落锤那一下手腕甩出去 49°）
    const out = far.clone().sub(fist).normalize();
    const along = THREE.MathUtils.radToDeg(Math.acos(Math.abs(out.dot(dirOf(hand, [0, 1, 0])))));
    assert.ok(along > 60, `${id} ${prop}: the tool crosses the hand instead of running along the forearm (${along.toFixed(0)}° from the hand's long axis)`);
  }
});

test('tools: at their stations the smith hits the anvil with the tongs on the iron, the artisan drives the chisel into the stone, the painter touches the canvas', () => {
  const assets = createAssetLibrary(M);
  const acc = demoAccumulation(fixture);
  const layout = createLayout({ ...fixture, groveTrees: acc.trees.length });
  const w = buildWorld(new THREE.Scene(), fixture, layout, M, { assets });
  w.setResidents(acc.residents, (k) => k);
  const at = (kind) => {
    const r = w.residents.get(kind);
    assert.ok(r?.station, `${kind} works at a station`);
    r.figure.root.position.set(r.home.x, r.home.y, r.home.z);
    r.figure.root.rotation.y = r.home.yaw;
    return r;
  };
  // 铁匠：站在锻炉 0.2 米高的地坪上（第一版陷进去 19 厘米），落锤的那一刻锤面落在砧面上、朝下；火钳的钳口在砧面上
  const smith = at('smith');
  assert.ok(Math.abs(smith.home.y - (0.2 + 0.012)) < 1e-6, `the smith stands on the forge floor (${smith.home.y.toFixed(3)})`);
  const anvil = smith.station.at;
  poseAt(smith.figure, 'forge', 650);
  const hand = smith.figure.joints.handR;
  const face = world(hand, [-0.29, GRIP.fist[1] - 0.09, GRIP.fist[2]]);
  assert.ok(Math.hypot(face.x - anvil.x, face.z - anvil.z) < 0.05, `the hammer face lands on the anvil (${Math.hypot(face.x - anvil.x, face.z - anvil.z).toFixed(3)} m off the mark)`);
  // 砧面上躺着一根 3 厘米高的铁条：锤面（1.5 厘米厚的那一块的下沿）落在铁条上
  const onBar = face.y - 0.015 - (anvil.y + 0.03);
  assert.ok(Math.abs(onBar) < 0.02, `on the iron bar (${onBar.toFixed(3)} m from its top)`);
  assert.ok(dirOf(hand, [0, -1, 0]).y < -0.8, 'face down');
  const jaw = world(smith.figure.joints.handL, [0.43, GRIP.fist[1], GRIP.fist[2]]);
  assert.ok(Math.hypot(jaw.x - anvil.x, jaw.z - anvil.z) < 0.12 && Math.abs(jaw.y - anvil.y) < 0.06, `the tongs hold the iron on the anvil (${jaw.distanceTo(anvil).toFixed(3)} m)`);
  // 举锤时锤头在肩后、比头高
  poseAt(smith.figure, 'forge', 420);
  const head = world(smith.figure.joints.handR, [-0.29, GRIP.fist[1], GRIP.fist[2]]);
  const crown = smith.figure.joints.head.getWorldPosition(new THREE.Vector3());
  const back = new THREE.Vector3(-Math.sin(smith.home.yaw), 0, -Math.cos(smith.home.yaw));
  assert.ok(head.y > crown.y && head.clone().sub(crown).dot(back) > 0.1, 'raised, the hammer head is up behind the shoulder');

  // 工匠：凿刃抵在石面上，落槌的那一刻槌头贴着凿尾
  const artisan = at('artisan');
  poseAt(artisan.figure, 'carve', 650);
  const L = artisan.figure.joints.handL, R = artisan.figure.joints.handR;
  const blade = world(L, [-0.145, GRIP.fist[1], GRIP.fist[2]]), butt = world(L, [0.1, GRIP.fist[1], GRIP.fist[2]]);
  assert.ok(blade.distanceTo(artisan.station.at) < 0.03, `the chisel's edge is on the stone (${blade.distanceTo(artisan.station.at).toFixed(3)} m)`);
  const axis = butt.clone().sub(blade).normalize();
  assert.ok(axis.y > 0.4, 'the chisel leans back toward him, butt up');
  // 凿尾朝着拿槌的右手（人物的右边是根坐标的 −x）：第一版凿尾偏向左肩，右手只能横过胸口反手去敲
  const right = new THREE.Vector3(-1, 0, 0).transformDirection(artisan.figure.root.matrixWorld);
  assert.ok(axis.dot(right) > 0.2, `the chisel's butt turns toward the mallet hand (${axis.dot(right).toFixed(2)})`);
  const strike = butt.clone().addScaledVector(axis, 0.055);
  const mallet = world(R, [-0.235, GRIP.fist[1], GRIP.fist[2]]);
  assert.ok(mallet.distanceTo(strike) < 0.045, `the mallet comes down on the chisel's butt (${mallet.distanceTo(strike).toFixed(3)} m)`);
  // 圆槌头的侧面敲在凿尾上：槌柄与凿子垂直
  const handle = mallet.clone().sub(world(R, GRIP.fist)).normalize();
  assert.ok(Math.abs(handle.dot(axis)) < 0.15, `the mallet's handle crosses the chisel (${handle.dot(axis).toFixed(2)})`);

  // 画师：笔尖落在画面上（两组落笔点之间来回，落笔时离画面 2 厘米以内）
  const painter = at('painter');
  const canvas = painter.station.at;
  const normal = new THREE.Vector3(Math.sin(painter.home.yaw), 0, Math.cos(painter.home.yaw)).negate();   // 画面朝着她
  let closest = Infinity;
  for (const t of [0, 200, 400, 600, 770]) {
    poseAt(painter.figure, 'paint', t);
    const tip = world(painter.figure.joints.handR, [-0.004, -0.128, 0.052]);
    closest = Math.min(closest, Math.abs(tip.clone().sub(canvas).dot(normal)));
    assert.ok(Math.abs(tip.y - canvas.y) < 0.3, `t=${t}: the brush stays on the panel's height (${(tip.y - canvas.y).toFixed(2)})`);
  }
  assert.ok(closest < 0.02, `the brush touches the canvas (${closest.toFixed(3)} m)`);
});

test('tools: the stylus writes on the tablet, and boards lie flat on the palm instead of standing up like a sign', () => {
  for (const id of ['scribe', 'athena']) {
    const f = buildCharacter(M, id);
    let worst = 0;
    for (let t = 0; t <= 2000; t += 100) {
      poseAt(f, 'write', t);
      const L = f.joints.handL;
      const wax = world(L, [0, GRIP.palm[1] - 0.04, GRIP.palm[2] + 0.019]);
      const n = dirOf(L, [0, 0, 1]);
      const tip = world(f.joints.handR, [-0.004, -0.128, 0.052]);
      const off = tip.clone().sub(wax);
      const gap = off.dot(n);
      const inPlane = off.addScaledVector(n, -gap);
      worst = Math.max(worst, Math.abs(gap));
      assert.ok(inPlane.length() < 0.11 * (f.spec.height / 1.75), `${id} t=${t}: the stylus point is over the wax (${inPlane.length().toFixed(3)} m from its middle)`);
      assert.ok(n.y > 0.7, `${id} t=${t}: the tablet faces up toward the eyes (normal y ${n.y.toFixed(2)})`);
    }
    // 写完一行抬一下笔：最远离开板面 2.5 厘米；其余时候贴着
    assert.ok(worst < 0.025, `${id}: the stylus stays on the tablet (${worst.toFixed(3)} m at most)`);
  }
  // 抱着走：板平放在掌上（掌心朝上），不是竖着贴在胸口
  for (const [id, prop] of [['scribe', 'tablet'], ['accountant', 'abacus'], ['painter', 'palette']]) {
    const f = buildCharacter(M, id);
    let clock = 0;
    for (let k = 0; k < 30; k++) { clock += 16; f.pose('walk', k * 16, { clock, speed: 1.3, stride: 1.3 }); }
    f.root.updateMatrixWorld(true);
    assert.ok(f.props.get(prop).visible, `${id} carries the ${prop}`);
    assert.ok(dirOf(f.joints.handL, [0, 0, 1]).y > 0.7, `${id}: the ${prop} lies flat on the palm while walking`);
  }
  // 算账、作画时板也是平的
  for (const [id, variant] of [['accountant', 'count'], ['painter', 'paint']]) {
    const f = buildCharacter(M, id);
    poseAt(f, variant, 300);
    assert.ok(dirOf(f.joints.handL, [0, 0, 1]).y > 0.7, `${id}: the board lies flat while ${variant}ing`);
  }
});

test('tools: what takes two hands is held by both hands', () => {
  // 两只手一起拿的东西挂在根上、每帧按两只手的握点摆；这里核对两只手真的都在它上面（第一版只挂在右手，另一只手悬在空中）
  const cases = [
    ['athena', 'work', { variant: 'read' }, 'scrollOpen', 0.24, 0.44],
    ['athena', 'work', { variant: 'await' }, 'sealed', 0.1, 0.3],
    ['priestess', 'work', { variant: 'tend' }, 'phiale', 0.05, 0.24],
    ['athena', 'deliver', { duration: 1800 }, 'gift', 0.08, 0.36],
    ['sailor', 'work', { variant: 'haul' }, 'rope', 0.15, 0.6],
  ];
  for (const [id, action, style, prop, min, max] of cases) {
    const f = buildCharacter(M, id);
    let clock = 0;
    for (let k = 0; k < 40; k++) { clock += 16; f.pose(action, action === 'deliver' ? 300 : 500 + k * 16, { ...style, clock }); }
    f.root.updateMatrixWorld(true);
    const p = f.props.get(prop);
    assert.ok(p.visible, `${id} ${prop} is shown`);
    assert.equal(p.parent, f.root, `${id} ${prop} hangs on the figure, placed between the hands every frame`);
    const grip = prop === 'scrollOpen' || prop === 'rope' ? GRIP.fist : GRIP.palm;
    const l = world(f.joints.handL, grip), r = world(f.joints.handR, grip);
    const apart = l.distanceTo(r);
    assert.ok(apart > min && apart < max, `${id} ${prop}: the hands are ${apart.toFixed(2)} m apart`);
    // 东西就在两只手之间（离两手连线的中点不远）
    const centre = new THREE.Vector3().setFromMatrixPosition(p.matrixWorld);
    if (prop !== 'rope') assert.ok(centre.distanceTo(l.clone().add(r).multiplyScalar(0.5)) < 0.12, `${id} ${prop} sits between the hands`);
    if (prop === 'phiale') assert.ok(centre.y > Math.max(l.y, r.y), 'the bowl rests on the palms, not inside the hands');
  }
});

/**
 * 一点在躯干里陷了多深（米，0 = 没进去）。躯干按身体量、不按衣服量：脊柱坐标系里一根椭圆柱，半轴 0.135 × 0.105（乘体格），
 * 从髋往上到肩下——两肩的关节在 ±0.172，上臂垂着时贴在它外面。radius 是这一段手臂的粗细。
 */
function torsoDepth(figure, p, radius) {
  const b = figure.spec.build ?? 1, s = (figure.spec.height ?? 1.75) / 1.75;
  const q = p.clone().applyMatrix4(new THREE.Matrix4().copy(figure.joints.spine.matrixWorld).invert());
  if (q.y < -0.25 || q.y > 0.47) return 0;
  const ax = 0.135 * b, az = 0.105 * b;
  const d = (Math.hypot(q.x / ax, q.z / az) - 1) * Math.min(ax, az) - radius / s;
  return Math.max(0, -d) * s;
}

test('tools: carving, the striking arm stays on its own side and neither arm passes through the body', () => {
  // M3 第四轮，用户：工匠左手拿凿子顶着石头没错，但右臂直接向左穿过胸膛去锤凿子。量的是用户看到的那件事——
  // 一整个敲击循环里右肘、右拳在身体的哪一边，两条胳膊有没有进躯干。第一版落槌时右肘在 x = −0.016（身体正中）、
  // 右拳在 x = +0.162（身体左半边），离胸口 10 厘米
  const f = buildCharacter(M, 'artisan');
  const b = f.spec.build ?? 1, s = f.spec.height / 1.75;
  let clock = 0;
  for (let k = 0; k < 20; k++) { clock += 16; f.pose('idle', clock, { clock }); }
  for (let t = 0; t < 300; t += 10) { clock += 10; f.pose('work', t, { variant: 'carve', clock }); }   // 过渡走完
  const inv = new THREE.Matrix4();
  const local = (joint, p = [0, 0, 0]) => world(joint, p).applyMatrix4(inv);
  let worstElbow = -Infinity, worstFist = -Infinity, deepest = 0, where = '';
  for (let t = 300; t <= 1000; t += 10) {
    clock += 10;
    f.pose('work', t, { variant: 'carve', clock });
    f.root.updateMatrixWorld(true);
    inv.copy(f.root.matrixWorld).invert();
    worstElbow = Math.max(worstElbow, local(f.joints.elbowR).x);
    worstFist = Math.max(worstFist, local(f.joints.handR, GRIP.fist).x);
    for (const side of ['L', 'R']) {
      const sh = world(f.joints[`shoulder${side}`], [0, 0, 0]), el = world(f.joints[`elbow${side}`], [0, 0, 0]);
      const wr = world(f.joints[`hand${side}`], [0, 0, 0]), fist = world(f.joints[`hand${side}`], GRIP.fist);
      const samples = [];
      for (let k = 3; k <= 10; k++) samples.push([sh.clone().lerp(el, k / 10), 0.041 * b * s, 'upper arm']);
      for (let k = 0; k <= 10; k++) samples.push([el.clone().lerp(wr, k / 10), 0.033 * b * s, 'forearm']);
      samples.push([fist, 0.035, 'fist']);
      for (const [p, r, part] of samples) {
        const d = torsoDepth(f, p, r);
        if (d > deepest) { deepest = d; where = `${side} ${part} at t=${t}`; }
      }
    }
  }
  assert.ok(worstElbow < -0.15, `the right elbow stays at his right side (x reaches ${worstElbow.toFixed(3)})`);
  assert.ok(worstFist < -0.1, `the right fist never crosses to his left half (x reaches ${worstFist.toFixed(3)})`);
  assert.ok(deepest < 0.005, `no arm passes through the body (${(deepest * 100).toFixed(1)} cm deep, ${where})`);
});

test('tools: sleeves go with the arm, and the tunic keeps no fixed sleeve where the arm hangs', () => {
  // M3 第四轮，用户：衣服的袖子是固定的，没有和手臂一起运动。第一版的"袖子"是上衣挂在脊柱上的两侧——那一筒布在肩下
  // 比躯干宽出 4 厘米（0.19 对 0.15），垂着手时罩住上臂的上半截，胳膊一抬它留在原地，成了两块方垫肩、胳膊从底下伸出去
  for (const id of CHARACTER_IDS) {
    const f = buildCharacter(M, id);
    const b = f.spec.build ?? 1;
    // 腋下到肩下（脊柱坐标系 0.30–0.44，身体单位；肩关节在 0.45，再往上是肩头）上衣两侧不比胸宽——垂着手时上臂贴在这里。
    // 斜披的外衣是另一块，搭在肩上
    const pos = f.joints.spine.getObjectByName('upperGarment').geometry.attributes.position;
    let wide = 0;
    for (let i = 0; i < pos.count; i++) if (pos.getY(i) > 0.3 && pos.getY(i) < 0.44) wide = Math.max(wide, Math.abs(pos.getX(i)));
    assert.ok(wide < 0.15 * b, `${id}: above the armpit the tunic is no wider than the chest (${(wide / b).toFixed(3)}·build)`);
    for (const side of ['L', 'R']) assert.ok(f.joints[`shoulder${side}`].getObjectByName(`sleeve${side}`), `${id}: the ${side} sleeve hangs on the shoulder joint`);
  }
  // 把手臂举起来（挥手、平举、抡锤举到肩后、手搭在额前）：袖子的每一点都还裹在上臂周围
  const raised = [['scribe', 'wave', {}, 'R', 800], ['athena', 'work', { variant: 'summon' }, 'L', 1200], ['smith', 'work', { variant: 'forge' }, 'R', 420], ['surveyor', 'work', { variant: 'survey' }, 'L', 1200]];
  for (const [id, action, style, side, t] of raised) {
    const f = buildCharacter(M, id);
    const b = f.spec.build ?? 1, s = f.spec.height / 1.75;
    let clock = 0;
    for (let k = 0; k <= 30; k++) { clock += 16; f.pose(action, Math.max(0, t - (30 - k) * 16), { ...style, clock }); }
    f.root.updateMatrixWorld(true);
    const sh = world(f.joints[`shoulder${side}`], [0, 0, 0]), el = world(f.joints[`elbow${side}`], [0, 0, 0]);
    assert.ok(el.y > sh.y - 0.12, `${id} ${action}: the ${side} arm is up (elbow ${(el.y - sh.y).toFixed(2)} m from the shoulder's height)`);
    const sleeve = f.joints[`shoulder${side}`].getObjectByName(`sleeve${side}`);
    const p = sleeve.geometry.attributes.position, v = new THREE.Vector3(), axis = el.clone().sub(sh);
    let far = 0;
    for (let i = 0; i < p.count; i++) {
      v.fromBufferAttribute(p, i).applyMatrix4(sleeve.matrixWorld);
      const u = Math.max(0, Math.min(1, v.clone().sub(sh).dot(axis) / axis.lengthSq()));
      far = Math.max(far, v.distanceTo(sh.clone().addScaledVector(axis, u)));
    }
    assert.ok(far < 0.075 * b * s, `${id} ${action}: the ${side} sleeve stays wrapped round the raised upper arm (${far.toFixed(3)} m from it)`);
  }
});
