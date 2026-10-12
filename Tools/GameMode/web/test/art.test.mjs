// M2 的纯逻辑：人物姿势、资产清单的缺省回退、半身像、港前场馆与橄榄林的布局、群岛海图、新意图。
// 运行：node --test Tools/GameMode/web/test/
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { ACTIONS, WORK_VARIANTS, JOINT_NAMES, poseAt, blendPoses, restPose, walkPeriod, LIMITS, NEUTRAL, ARMS } from '../../../../Assets/Polis/src/art/poses.mjs';
import { planOverrides, resolveSource, clipFor, modelUrl, isAssetId, MODEL_ROUTE, accentPropsShown, ACCENT_PROPS } from '../../../../Assets/Polis/src/art/asset-manifest.mjs';
import { bust, toSvg, CHARACTERS, EXPRESSIONS, BUST_VIEWBOX, CURLS, CURL_BOX, crownPolygon } from '../../../../Assets/Polis/src/art/busts.mjs';
import {
  venueSlotLocal, venueStandLocal, groveSpot, groveTerraces, islandLayout, chartTransform, hitIsland,
  SLOTS_PER_VENUE, TREES_PER_TERRACE, PLOT_SIZE, TERRACE,
} from '../../../../Assets/Polis/src/civic-layout.mjs';
import { intent, INTENT_TYPES } from '../../../../Assets/Polis/src/live.mjs';
import * as THREE from '../../../../Assets/Polis/vendor/three.bundle.mjs';
import { makeWaterMaterial, swellHeight, SWELLS, MAX_RECTS } from '../../../../Assets/Polis/src/art/water.mjs';
import { buildCharacter, CHARACTER_IDS, CHARACTER_SPECS } from '../../../../Assets/Polis/src/art/characters.mjs';
import { FACE_LOOKS, FACE_RECT, DECAL_LIFT, lookFor, faceMaterialKey, eyePaths, browPath, lipPaths } from '../../../../Assets/Polis/src/art/face.mjs';
import { LEG_PROFILES, ARM_PROFILES, NECK_PROFILES, NECK_LEAN, HEAD_RADII, legSection, sculptHead } from '../../../../Assets/Polis/src/art/rig.mjs';

/** 材质的替身：人物只要按名字拿到一份材质（Node 里没有画布，真材质的贴图建不出来）。 */
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

const angles = (pose) => JOINT_NAMES.flatMap((j) => Object.entries(pose[j]).filter(([k]) => k !== 'sz' && !['x', 'y', 'z'].includes(k)).map(([, v]) => v));
const near = (a, b, eps = 1e-9) => Math.abs(a - b) <= eps;

test('poses: every action and work variant gives finite, bounded joint angles', () => {
  for (const action of ACTIONS) {
    const variants = action === 'work' ? WORK_VARIANTS : [undefined];
    for (const variant of variants) {
      for (let t = 0; t <= 6000; t += 37) {
        const p = poseAt(action, t, { variant, duration: 1800 });
        for (const a of angles(p)) {
          assert.ok(Number.isFinite(a), `${action}/${variant} t=${t}: NaN`);
          assert.ok(Math.abs(a) <= LIMITS.angle, `${action}/${variant} t=${t}: angle ${a} beyond the limit`);
        }
        for (const k of ['x', 'y', 'z']) assert.ok(Math.abs(p.hips[k]) <= LIMITS.hipsOffset, `${action}/${variant}: hips.${k} ${p.hips[k]}`);
      }
    }
  }
  assert.throws(() => poseAt('dance', 0), /unknown action/);
  assert.throws(() => poseAt('work', 0, { variant: 'juggle' }), /unknown work variant/);
  assert.throws(() => poseAt('idle', NaN), /finite/);
});

test('poses: walking is periodic, left and right are half a cycle apart, and the hips bob twice per cycle', () => {
  const style = { stride: 1.3, speed: 1.3 };
  const P = walkPeriod(style);
  assert.ok(near(P, 2000), `two steps of 1.3 m at 1.3 m/s take 2 s (got ${P})`);
  for (let t = 0; t < P; t += 50) {
    const a = poseAt('walk', t, style), b = poseAt('walk', t + P, style), h = poseAt('walk', t + P / 2, style);
    assert.ok(near(a.legL.rx, b.legL.rx, 1e-9) && near(a.shoulderR.rx, b.shoulderR.rx, 1e-9), 'one period later the pose repeats');
    assert.ok(near(a.legL.rx, h.legR.rx, 1e-9), 'the left leg does what the right leg did half a cycle earlier');
    assert.ok(Math.sign(a.legL.rx) !== Math.sign(a.shoulderL.rx) || near(a.legL.rx, 0, 1e-6), 'an arm swings against its own leg');
  }
  const bobs = [];
  for (let t = 0; t < P; t += P / 400) bobs.push(poseAt('walk', t, style).hips.y);
  const peaks = bobs.filter((y, i) => i > 0 && i < bobs.length - 1 && y > bobs[i - 1] && y >= bobs[i + 1]).length;
  assert.equal(peaks, 1, 'inside one open period one interior peak, plus the one at t=0: the hips rise twice per cycle');
  assert.ok(bobs[0] > bobs[100], 'highest when the legs pass each other');
});

test('poses: Athena walks — stepping legs at her own stride, no gliding left anywhere', () => {
  // M0–M2 她滑行（12 米 / 秒）；M3 用户实测说像女鬼在飘，改成 3.2 米 / 秒的疾步。姿势里再没有"滑行"这一说
  const style = { speed: 3.2, stride: 1.6 };
  const P = walkPeriod(style);
  assert.ok(near(P, 1000), `a stride of 1.6 m at 3.2 m/s: one cycle per second (got ${P})`);
  let swing = 0;
  for (let t = 0; t < P; t += 25) swing = Math.max(swing, Math.abs(poseAt('walk', t, style).legL.rx));
  assert.ok(swing > 0.2, 'her legs step');
  assert.ok(!('glide' in poseAt('walk', 0, { glide: true })), 'style.glide is not a thing any more');
  assert.ok(Math.abs(poseAt('walk', 250, { ...style, glide: true }).legL.rx) > 0.1, 'and asking for it changes nothing');
});

test('poses: delivering starts and ends at rest, holds the gift until it is set down, and bends to the ground', () => {
  const rest = restPose();
  const start = poseAt('deliver', 0, { duration: 1800 });
  const end = poseAt('deliver', 1800, { duration: 1800 });
  for (const j of JOINT_NAMES) for (const k of Object.keys(rest[j])) {
    assert.ok(near(start[j][k], rest[j][k], 1e-9), `deliver starts at rest (${j}.${k})`);
    assert.ok(near(end[j][k], rest[j][k], 1e-9), `deliver ends at rest (${j}.${k})`);
  }
  assert.equal(poseAt('deliver', 900, { duration: 1800 }).propR, 'gift', 'the gift is in her hands at half time');
  assert.equal(poseAt('deliver', 1200, { duration: 1800 }).propR, null, 'and on the ground after 60 %');
  const low = poseAt('deliver', 1080, { duration: 1800 });
  assert.ok(low.hips.y < -0.1 && low.spine.rx > 0.4, 'at 60 % she is bent down to the ground');
  // 连续：相邻两帧的关节角差得不多（没有跳帧）
  let worst = 0;
  for (let t = 0; t < 1800; t += 16) {
    const a = angles(poseAt('deliver', t, { duration: 1800 })), b = angles(poseAt('deliver', t + 16, { duration: 1800 }));
    worst = Math.max(worst, ...a.map((v, i) => Math.abs(v - b[i])));
  }
  assert.ok(worst < 0.12, `no joint jumps more than 0.12 rad in one 16 ms frame (worst ${worst.toFixed(3)})`);
});

test('poses: work variants put the right tools in the hands', () => {
  const props = Object.fromEntries(WORK_VARIANTS.map((v) => { const p = poseAt('work', 500, { variant: v }); return [v, [p.propL, p.propR]]; }));
  assert.deepEqual(props.write, ['tablet', 'stylus']);
  assert.deepEqual(props.hammer, [null, 'hammer']);
  assert.deepEqual(props.read, [null, 'scrollOpen']);
  assert.deepEqual(props.announce, [null, 'staff']);
  assert.deepEqual(props.forge, ['tongs', 'hammer'], 'the smith holds the hot iron with tongs while he hammers');
  assert.deepEqual(props.carve, ['chisel', 'mallet']);
  // 举到肩后用 420 毫秒，落到砧面上只用 180 毫秒（M3 第三轮的锤：落点由 tools.test.mjs 用真骨架核对）
  const strike = [0, 200, 420, 600, 680].map((t) => poseAt('work', t, { variant: 'forge' }).shoulderR.rx);
  assert.ok(strike[2] < -2.4, `the hammer goes up behind the shoulder (${strike[2].toFixed(2)})`);
  assert.ok(strike[3] > -0.75 && strike[4] > -0.75, `then comes down onto the anvil, fast (${strike[3].toFixed(2)} 180 ms later)`);
  // 拳握的家什：抡锤的前臂转到拇指朝前（柄才落在挥动的那个平面里）
  for (const [v, side] of [['forge', 'R'], ['forge', 'L'], ['hammer', 'R']]) {
    const p = poseAt('work', 650, { variant: v });
    assert.ok(Math.abs(p[`hand${side}`].ry - NEUTRAL[side]) < 1e-9, `${v}: the ${side} forearm turns thumb-forward for a power grip`);
  }
  // 凿石头不在身前的竖直面里挥：凿尾转向右手、槌柄横过凿子，两只前臂各自比拇指朝前多转一点（M3 第四轮按真骨架解的，
  // 钉死拇指朝前再解，落槌时右拳缩到离肚子 16 厘米）——仍在前臂能转的范围里
  for (const side of ['L', 'R']) {
    const p = poseAt('work', 650, { variant: 'carve' });
    assert.ok(Math.abs(p[`hand${side}`].ry - NEUTRAL[side]) < 0.9, `carve: the ${side} forearm stays within its turn (${(p[`hand${side}`].ry - NEUTRAL[side]).toFixed(2)} from thumb-forward)`);
  }
});

test('poses: blending is exact at the ends and switches the held tool halfway', () => {
  const a = poseAt('idle', 1234), b = poseAt('work', 400, { variant: 'write' });
  const at0 = blendPoses(a, b, 0), at1 = blendPoses(a, b, 1), mid = blendPoses(a, b, 0.5);
  for (const j of JOINT_NAMES) for (const k of Object.keys(a[j])) {
    assert.ok(near(at0[j][k], a[j][k]) && near(at1[j][k], b[j][k]), `${j}.${k}`);
  }
  assert.equal(at0.propL, null); assert.equal(mid.propL, 'tablet');
});

test('poses: residents carry their tools while walking and standing; long staves stay upright', () => {
  // 手的坐标轴在 x 上转过的总角度（脊柱 + 肩 + 肘 + 腕）：0 就是竖直，握着的杖是竖的
  const tilt = (p, side) => p.spine.rx + p[`shoulder${side}`].rx + p[`elbow${side}`].rx + p[`hand${side}`].rx;
  const staff = { side: 'R', grip: 'staff', prop: 'rod' };
  for (let t = 0; t < 4000; t += 50) {
    for (const action of ['idle', 'walk']) {
      const p = poseAt(action, t, { carry: staff, stride: 1.3, speed: 1.3 });
      assert.equal(p.propR, 'rod', `${action} t=${t}: the rod is in hand`);
      assert.ok(Math.abs(tilt(p, 'R')) < 1e-9, `${action} t=${t}: the rod stands upright`);
      for (const a of angles(p)) assert.ok(Math.abs(a) <= LIMITS.angle, `${action} t=${t}: angle ${a}`);
    }
  }
  const w0 = poseAt('walk', 500, { carry: staff }), w1 = poseAt('walk', 1500, { carry: staff });
  assert.equal(w0.shoulderR.rx, w1.shoulderR.rx, 'the staff arm does not swing with the steps');
  assert.notEqual(w0.shoulderL.rx, w1.shoulderL.rx, 'the free arm still swings');
  const hug = poseAt('walk', 700, { carry: { side: 'L', grip: 'hug', prop: 'tablet' } });
  assert.equal(hug.propL, 'tablet');
  // 板平放在左掌上、托在腰前（第一版前臂收在胸前，板竖着贴在胸口、正面朝外）——掌心朝上由 tools.test.mjs 用真骨架核对
  assert.deepEqual([hug.shoulderL.rx, hug.shoulderL.ry, hug.shoulderL.rz, hug.elbowL.rx, hug.handL.rx, hug.handL.ry, hug.handL.rz], ARMS.hugL, 'the board rests on the left palm');
  // 做事时立着的权杖、测杆：手腕同样补正（第一版的杖随前臂倒向一边）。收缆不在这里了：M3 第三轮的缆绳每帧按两只拳头铺，不是挂在手上的直杆
  for (const v of ['announce', 'survey']) {
    for (let t = 0; t < 3000; t += 70) assert.ok(Math.abs(tilt(poseAt('work', t, { variant: v }), 'R')) < 1e-9, `${v} t=${t}: the hand frame stays level`);
  }
  assert.equal(poseAt('idle', 0, { carry: null }).propR, null, 'no carry, empty hands');
  assert.equal(poseAt('work', 300, { variant: 'write', carry: staff }).propR, 'stylus', 'work poses have their own tools');
  assert.equal(poseAt('deliver', 300, { duration: 1800, carry: staff }).propR, 'gift', 'delivering puts the tool away');
  assert.throws(() => poseAt('idle', 0, { carry: { side: 'R', grip: 'juggle', prop: 'rod' } }), /unknown carry grip/);
});

// 2026-10-12，用户：所有人物的五官是几根线条勾勒的，显得呆笨——五官改成画在脸上的贴图（face.mjs），贴在雕好的头上。
// 旧的做法：眼白、虹膜各一个球，眉毛和两片嘴唇各一枚扁椭球（eyeWhite / iris / lips 材质）。
test('faces: every sculpted head wears its painted face on the head itself; no bead eyes, no blob brows or lips', () => {
  const M = stubMaterials();
  for (const id of CHARACTER_IDS) {
    const spec = { id, ...CHARACTER_SPECS[id] };
    const f = buildCharacter(M, id);
    const head = f.joints.head;
    const decals = head.children.filter((o) => o.name === 'face');
    const old = [];
    f.root.traverse((o) => { if (o.isMesh && ['eyeWhite', 'iris', 'lips'].includes(o.material?.name)) old.push(o.material.name); });
    assert.deepEqual(old, [], `${id}: no sphere eyes or blob lips`);
    if (spec.faceless) { assert.equal(decals.length, 0, `${id}: the gold maiden has no face`); continue; }
    assert.equal(decals.length, 1, `${id}: one painted face`);
    const decal = decals[0];
    assert.equal(decal.material.name, faceMaterialKey(lookFor(spec)), `${id}: wears the ${lookFor(spec)} look`);
    // 贴图那一片是头本身的面：每个顶点离头网格上对应的顶点正好 DECAL_LIFT，UV 都在贴图里，都在脸的正面
    const headMesh = head.children.find((o) => o.name === 'head');
    const hp = headMesh.geometry.attributes.position, dp = decal.geometry.attributes.position, uv = decal.geometry.attributes.uv;
    const used = new Set(decal.geometry.index.array);
    assert.ok(used.size > 300, `${id}: the face mask covers the face (${used.size} vertices)`);
    for (const i of used) {
      const d = Math.hypot(dp.getX(i) - hp.getX(i), dp.getY(i) - hp.getY(i), dp.getZ(i) - hp.getZ(i));
      assert.ok(Math.abs(d - DECAL_LIFT) < 1e-6, `${id}: the mask sits ${DECAL_LIFT * 1000} mm off the skin`);
      assert.ok(uv.getX(i) >= 0 && uv.getX(i) <= 1 && uv.getY(i) >= 0 && uv.getY(i) <= 1 && hp.getZ(i) > 0.02);
    }
  }
  assert.equal(lookFor({ id: 'athena', ...CHARACTER_SPECS.athena }), 'athena');
  assert.equal(new Set(CHARACTER_IDS.filter((id) => !CHARACTER_SPECS[id].faceless).map((id) => lookFor({ id, ...CHARACTER_SPECS[id] }))).size, Object.keys(FACE_LOOKS).length, 'every look is worn by someone');
  // 和半身像一样只用 M / L / C / Q / Z
  for (const look of Object.values(FACE_LOOKS)) {
    const e = eyePaths(look.eye);
    for (const d of [e.white, e.crease, e.lashLine, e.lowerLine, browPath(look.brow), ...Object.values(lipPaths(look.lips))]) {
      assert.match(d, /^[MLCQZ0-9 .\-]+$/, 'SVG path data uses M / L / C / Q / Z only');
    }
  }
  // 画上去的眼睛落在雕出来的眼球上（嘴唇的形状全靠画：头网格竖向一格约 7 毫米，雕不出唇缝）
  const g = sculptHead({ female: true, age: 'young', jaw: 0.3, nose: 0.95 });
  const ray = new THREE.Raycaster(), probe = new THREE.Mesh(g, new THREE.MeshBasicMaterial({ side: THREE.DoubleSide }));
  const zAt = (xmm, ymm) => { ray.set(new THREE.Vector3(xmm / 1000, ymm / 1000, 1), new THREE.Vector3(0, 0, -1)); return ray.intersectObject(probe)[0].point.z; };
  assert.ok(zAt(29.4, 15.3) > zAt(29.4, 6) - 0.0005, 'the painted eye sits on the eyeball, not at the bottom of a pit (the old socket was 2.3 mm below its lower rim)');
  assert.ok(FACE_RECT.x0 + FACE_RECT.size > 29.4 + FACE_LOOKS.athena.eye.half + 6);
});

// 2026-10-12，用户：雅典娜的小腿太粗了。之前小腿是一根等粗的胶囊（半径 4.4 厘米，脚踝和膝下一样粗）加一枚小腿肚椭球
test('legs: the shin tapers from the calf to a slim ankle, and the wedge sandal stands on the ground', () => {
  for (const [kind, ankleMax] of [['female', 0.023], ['male', 0.028]]) {
    const shin = legSection(LEG_PROFILES[kind].shin);
    const calf = Math.max(...Array.from({ length: 40 }, (_, i) => shin(i / 40).rz));
    const ankle = shin(0.86);
    assert.ok(Math.max(ankle.rx, ankle.rz) <= ankleMax, `${kind}: the ankle is slim (${ankle.rx.toFixed(4)})`);
    assert.ok(Math.max(ankle.rx, ankle.rz) < calf * 0.55, `${kind}: the ankle is about half the calf`);
    let peak = 0, peakS = 0;
    for (let i = 0; i <= 40; i++) if (shin(i / 40).rz > peak) { peak = shin(i / 40).rz; peakS = i / 40; }
    assert.ok(peakS > 0.15 && peakS < 0.45, `${kind}: the calf is in the upper third (s = ${peakS})`);
  }
  assert.ok(LEG_PROFILES.female.shin.rx.every(([s, r], i) => r <= LEG_PROFILES.male.shin.rx[Math.min(i, LEG_PROFILES.male.shin.rx.length - 1)][1]), 'women\'s legs are not thicker than men\'s');
  // 坡跟抬高了身体（TYRRHENIAN_LIFT），鞋底仍和穿普通凉鞋的人落在同一个高度
  const M = stubMaterials();
  const lowest = (id) => {
    const f = buildCharacter(M, id);
    f.pose('idle', 0, { clock: 0 });
    f.root.updateMatrixWorld(true);
    const box = new THREE.Box3();
    f.joints.kneeL.traverse((o) => { if (o.isMesh) box.expandByObject(o); });
    return box.min.y;
  };
  const athena = lowest('athena'), priestess = lowest('priestess');
  assert.ok(Math.abs(athena - priestess) < 0.002, `Athena's wedge stands where an ordinary sandal does (${athena.toFixed(4)} vs ${priestess.toFixed(4)} m)`);
});

// 2026-10-12，用户：雅典娜的手臂比腿还粗，所有角色的脖子都像电线杆一样。之前上臂、前臂是 4.1 / 3.3 厘米的等粗胶囊，
// 脖子是一根 4.7 厘米（头宽的 57%）、上下一样粗的直立圆柱
test('proportions: arms are slimmer than legs, and the neck is a neck, not a pole', () => {
  const peak = (sec, a = 0, b = 1) => Math.max(...Array.from({ length: 41 }, (_, i) => { const c = sec(a + (b - a) * i / 40); return Math.max(c.rx, c.rz); }));
  for (const kind of ['female', 'male']) {
    const thigh = legSection(LEG_PROFILES[kind].thigh), shin = legSection(LEG_PROFILES[kind].shin);
    const upper = legSection(ARM_PROFILES[kind].upper), fore = legSection(ARM_PROFILES[kind].fore);
    assert.ok(peak(upper) < peak(shin), `${kind}: the upper arm (${peak(upper)}) is slimmer than the calf (${peak(shin)})`);
    assert.ok(peak(fore) < peak(shin) * 0.75, `${kind}: the forearm is well under the calf`);
    assert.ok(peak(upper) < peak(thigh) * 0.7, `${kind}: the upper arm is well under the thigh`);
    const wrist = fore(0.95);
    assert.ok(wrist.rz < wrist.rx * 0.8, `${kind}: the wrist is flat, not round`);
    assert.ok(Math.max(wrist.rx, wrist.rz) < peak(fore) * 0.75, `${kind}: the forearm narrows to the wrist`);
    assert.ok(upper(0.85).rx < upper(0.18).rx * 0.8, `${kind}: the upper arm narrows from the shoulder to the elbow`);
    // 脖子：最细处至少是头宽的 60%（旧的圆柱 50–57%），往下张开接到肩上（底宽是最细处的 1.5 倍以上）
    const neck = legSection(NECK_PROFILES[kind]);
    const slim = Math.min(...Array.from({ length: 21 }, (_, i) => neck(i / 40).rx));
    const headHalfWidth = HEAD_RADII[0] * (kind === 'female' ? 0.92 : 1);
    assert.ok(slim / headHalfWidth > 0.6, `${kind}: the neck is ${(slim / headHalfWidth * 100).toFixed(0)}% of the head's width`);
    assert.ok(neck(1).rx > slim * 1.5, `${kind}: the neck flares into the shoulders`);
  }
  assert.ok(NECK_LEAN > 0.15, 'the neck leans forward, it does not stand straight up');
  // 雅典娜：抬着下巴、挺着胸；别人保持原来微微低头的仪态
  const M = stubMaterials();
  const a = buildCharacter(M, 'athena'), p = buildCharacter(M, 'priestess');
  a.pose('idle', 0, { clock: 0 }); p.pose('idle', 0, { clock: 0 });
  assert.ok(a.joints.head.rotation.x < p.joints.head.rotation.x - 0.05, 'Athena carries her chin higher');
  assert.ok(a.joints.spine.rotation.x < p.joints.spine.rotation.x, 'and her chest out');
});

test('figures: only Athena carries the accent color; carried tools hang on the hand that carries them; everyone has legs', () => {
  const M = stubMaterials();
  for (const id of CHARACTER_IDS) {
    const f = buildCharacter(M, id);
    const red = [], flagged = [];
    f.root.traverse((o) => {
      if (!o.isMesh) return;
      if (o.material?.name === 'accent') red.push(o);
      if (o.userData.accent) flagged.push(o);
    });
    assert.deepEqual(red, flagged, `${id}: every red mesh is flagged for the grade's accent mask, and nothing else is`);
    if (id === 'athena') {
      // 交付的红卷轴、等审批时石板上的红绳与封印
      assert.ok(red.length >= 3, 'Athena: the gift and the sealed tablet are red');
      assert.ok([...f.props.get('gift').children, ...f.props.get('sealed').children].some((m) => m.userData.accent));
    } else {
      // M2 美术审查：第一版抄写员交付时手里也是一卷红色的卷轴
      assert.equal(red.length, 0, `${id}: no accent color — the terracotta red means "your turn", and only Athena hands you things`);
    }
    // M3：人人都有大腿、膝、小腿、小腿肚和一只带脚趾和凉鞋的脚（衣摆是会动的布，没有腿就只剩两只鞋浮在前面）——雅典娜也走路了
    let legMeshes = 0;
    f.joints.legL.traverse((o) => { if (o.isMesh) legMeshes++; });
    assert.ok(legMeshes >= 12, `${id}: thigh, shin, foot, toes and sandal are built (${legMeshes} meshes)`);
    const carry = CHARACTER_SPECS[id].carry;
    if (carry) assert.equal(f.props.get(carry.prop).parent, f.joints[`hand${carry.side}`], `${id}: the ${carry.prop} hangs on the ${carry.side} hand`);
  }
  assert.throws(() => buildCharacter(M, 'surveyor', { carry: { side: 'L', grip: 'staff', prop: 'rod' } }), /wrong hand/);
  assert.throws(() => buildCharacter(M, 'smith', { carry: { side: 'R', grip: 'staff', prop: 'rod' } }), /no such prop/);
});

test('asset manifest: an external figure still shows the "your turn" props, whichever hand holds them', () => {
  assert.deepEqual([...ACCENT_PROPS], ['gift', 'sealed']);
  // 等审批的石板在左手：第一版只看右手，换成外部模型的雅典娜等审批时，胸前的红封印从来不出现
  assert.deepEqual(accentPropsShown(poseAt('work', 800, { variant: 'await' })), ['sealed'], 'waiting for approval: the red seal');
  assert.deepEqual(accentPropsShown(poseAt('deliver', 300, { duration: 1800 })), ['gift'], 'delivering: the red scroll');
  assert.deepEqual(accentPropsShown(poseAt('deliver', 1500, { duration: 1800 })), [], 'set down: nothing floats any more');
  assert.deepEqual(accentPropsShown(poseAt('work', 800, { variant: 'write' })), [], 'ordinary tools are not accents');
  assert.deepEqual(accentPropsShown(poseAt('idle', 0)), []);
  assert.deepEqual(accentPropsShown(null), []);
});

test('asset manifest: unknown ids, bad entries and failed loads all fall back to the procedural asset', () => {
  const known = ['building.house', 'figure.athena', 'prop.amphora'];
  const { overrides, ignored } = planOverrides(known, {
    'building.house': { model: 'house.glb', scale: 2 },
    'figure.athena': { model: 'athena.gltf', animations: { walk: 'Walk', idle: 'Idle' } },
    'prop.amphora': { model: '../amphora.glb' },
    'prop.lyre': { model: 'lyre.glb' },
    'building.temple!': { model: 'x.glb' },
    'figure.player': { model: 'p.glb' },
  });
  assert.deepEqual([...overrides.keys()], ['building.house', 'figure.athena']);
  assert.deepEqual(ignored.map((i) => i.id).sort(), ['building.temple!', 'figure.player', 'prop.amphora', 'prop.lyre'].sort());
  assert.equal(overrides.get('building.house').scale, 2);
  assert.deepEqual(overrides.get('building.house').offset, [0, 0, 0], 'defaults filled in');
  assert.equal(modelUrl(overrides.get('building.house')), `${MODEL_ROUTE}house.glb`);

  const loads = new Map([['building.house', { ok: false, reason: 'HTTP 404' }]]);
  assert.deepEqual(resolveSource('prop.amphora', overrides, loads), { source: 'procedural', reason: null }, 'no override: procedural');
  assert.deepEqual(resolveSource('figure.athena', overrides, loads), { source: 'procedural', reason: 'loading' }, 'still loading: procedural for now');
  assert.deepEqual(resolveSource('building.house', overrides, loads), { source: 'procedural', reason: 'HTTP 404' }, 'load failed: falls back');
  loads.set('figure.athena', { ok: true });
  assert.equal(resolveSource('figure.athena', overrides, loads).source, 'external', 'loaded: the external model is used');

  assert.equal(clipFor(overrides.get('figure.athena'), 'walk'), 'Walk');
  assert.equal(clipFor(overrides.get('figure.athena'), 'deliver'), 'Idle', 'an unmapped action falls back to idle');
  assert.equal(clipFor(overrides.get('building.house'), 'walk'), null, 'no clips at all: static');
  assert.equal(planOverrides(known, null).overrides.size, 0, 'no manifest: everything procedural');
  assert.equal(planOverrides(known, ['x']).ignored[0].reason, 'manifest is not an object');
  assert.ok(isAssetId('civic.paintedStoa') && !isAssetId('Civic.x') && !isAssetId('prop'), 'id shape');
});

test('busts: every character has the three expressions, deterministic, inside the frame, no accent color', () => {
  assert.equal(Object.keys(CHARACTERS).length, 13, 'Athena, the player and eleven residents');
  for (const c of Object.keys(CHARACTERS)) {
    const shapes = new Set();
    for (const e of EXPRESSIONS) {
      const a = bust(c, e), b = bust(c, e);
      assert.deepEqual(a, b, `${c}/${e} is deterministic`);
      assert.ok(a.paths.length > 12, `${c}/${e} has a real drawing`);
      for (const p of a.paths) {
        assert.match(p.d, /^[MmLlCcQqAaZz0-9 .,-]+$/, `${c}/${e}: path data only`);
        assert.ok(!/NaN|undefined/.test(p.d), `${c}/${e}: no NaN`);
        const nums = p.d.match(/-?\d+(\.\d+)?/g).map(Number);
        assert.ok(nums.every((n) => n > -40 && n < 160), `${c}/${e}: stays near the frame`);
        assert.ok(![p.fill, p.stroke].includes('#b9572f'), 'the accent color is reserved for "your turn"');
      }
      shapes.add(a.paths.map((p) => p.d).join('|'));
    }
    assert.equal(shapes.size, 3, `${c}: the three expressions differ`);
  }
  const svg = toSvg(bust('athena', 'speak'));
  assert.ok(svg.startsWith('<svg xmlns="http://www.w3.org/2000/svg"') && svg.includes(`viewBox="0 0 ${BUST_VIEWBOX.width} ${BUST_VIEWBOX.height}"`));
  assert.ok(!/<script|on\w+=/i.test(svg), 'the exported SVG is drawing only');
  assert.throws(() => bust('zeus'), /unknown character/);
});

/** 把半身像的一条路径采样成点（M/L/C/Q/Z 与相对的 m/l/c/a；弧线只取外接的两角，偏保守）。 */
function pathPoints(d) {
  const tok = d.trim().split(/\s+/);
  const pts = [];
  let i = 0, cx = 0, cy = 0, sx = 0, sy = 0, cmd = null;
  const num = () => Number(tok[i++]);
  const cubic = (x0, y0, x1, y1, x2, y2, x3, y3) => {
    for (let k = 1; k <= 16; k++) {
      const t = k / 16, u = 1 - t;
      pts.push([u * u * u * x0 + 3 * u * u * t * x1 + 3 * u * t * t * x2 + t * t * t * x3, u * u * u * y0 + 3 * u * u * t * y1 + 3 * u * t * t * y2 + t * t * t * y3]);
    }
  };
  while (i < tok.length) {
    if (/^[A-Za-z]$/.test(tok[i])) cmd = tok[i++];
    switch (cmd) {
      case 'M': cx = num(); cy = num(); sx = cx; sy = cy; pts.push([cx, cy]); cmd = 'L'; break;
      case 'm': cx += num(); cy += num(); sx = cx; sy = cy; pts.push([cx, cy]); cmd = 'l'; break;
      case 'L': cx = num(); cy = num(); pts.push([cx, cy]); break;
      case 'l': cx += num(); cy += num(); pts.push([cx, cy]); break;
      case 'C': case 'c': {
        const v = [num(), num(), num(), num(), num(), num()];
        if (cmd === 'c') for (let k = 0; k < 6; k += 2) { v[k] += cx; v[k + 1] += cy; }
        cubic(cx, cy, ...v); cx = v[4]; cy = v[5];
        break;
      }
      case 'Q': {
        const [x1, y1, x2, y2] = [num(), num(), num(), num()];
        cubic(cx, cy, cx + (2 / 3) * (x1 - cx), cy + (2 / 3) * (y1 - cy), x2 + (2 / 3) * (x1 - x2), y2 + (2 / 3) * (y1 - y2), x2, y2);
        cx = x2; cy = y2;
        break;
      }
      case 'a': {
        const rx = num(), ry = num(); num(); num(); num();
        const dx = num(), dy = num();
        pts.push([Math.min(cx, cx + dx) - rx, Math.min(cy, cy + dy) - ry], [Math.max(cx, cx + dx) + rx, Math.max(cy, cy + dy) + ry]);
        cx += dx; cy += dy;
        break;
      }
      case 'Z': case 'z': cx = sx; cy = sy; break;
      default: throw new Error(`unexpected path command '${cmd}' in ${d}`);
    }
  }
  return pts;
}

test('busts: curls sit inside the hair, and nothing but the meander reaches into the frieze band', () => {
  const poly = crownPolygon();
  const inside = (x, y) => {
    let c = false;
    for (let a = 0, b = poly.length - 1; a < poly.length; b = a++) {
      const [xa, ya] = poly[a], [xb, yb] = poly[b];
      if ((ya > y) !== (yb > y) && x < xa + ((y - ya) * (xb - xa)) / (yb - ya)) c = !c;
    }
    return c;
  };
  // M2 美术审查：第一版按方格排卷，靠前的一列落在额头和太阳穴上
  for (const [x, y] of CURLS) {
    for (const [dx, dy] of [[CURL_BOX.dx0, CURL_BOX.dy0], [CURL_BOX.dx1, CURL_BOX.dy0], [CURL_BOX.dx0, CURL_BOX.dy1], [CURL_BOX.dx1, CURL_BOX.dy1]]) {
      assert.ok(inside(x + dx, y + dy), `the curl at (${x}, ${y}) stays in the hair`);
    }
  }
  // 画框顶上的回纹带在 y 11–19、下面一道线在 22；画框之内的一切（头饰、手里的长家什）都在 23 以下、两边的框之内。
  // 第一版水手的尖帽顶到了 12，传令官背后的帽檐伸到 x=4
  for (const c of Object.keys(CHARACTERS)) {
    for (const e of EXPRESSIONS) {
      for (const p of bust(c, e).paths.slice(4)) {
        for (const [x, y] of pathPoints(p.d)) {
          assert.ok(y >= 23, `${c}/${e}: ${p.d.slice(0, 40)}… reaches y=${y.toFixed(1)}, into the frieze`);
          assert.ok(x >= 4 && x <= 116, `${c}/${e}: ${p.d.slice(0, 40)}… reaches x=${x.toFixed(1)}, onto the frame`);
        }
      }
    }
  }
});

test('venues: twelve pedestals in two rows on the terrace, the stand in the aisle between them', () => {
  const slots = Array.from({ length: SLOTS_PER_VENUE }, (_, i) => venueSlotLocal(i));
  assert.equal(new Set(slots.map((s) => `${s.x},${s.z}`)).size, SLOTS_PER_VENUE, 'every slot has its own place');
  for (const s of slots) {
    assert.ok(Math.abs(s.x) < PLOT_SIZE / 2 - 0.8 && s.z > TERRACE.buildingFront && s.z < PLOT_SIZE / 2 - 0.5, 'pedestals stand on the terrace, in front of the building');
  }
  const stand = venueStandLocal();
  assert.ok(stand.z > TERRACE.rowBack && stand.z < TERRACE.rowFront, 'the stand is in the aisle');
  for (let i = 0; i < 6; i++) assert.ok(slots[i].z < stand.z && slots[i + 6].z > stand.z, 'back row behind the aisle, front row in front of it');
  assert.throws(() => venueSlotLocal(12), RangeError);
});

test('grove: the n-th tree always stands in the same place; terraces grow west along the shore', () => {
  const origin = { x: -2, z: 2 };
  const first = groveSpot(0, origin), again = groveSpot(0, origin);
  assert.deepEqual(first, again, 'deterministic');
  assert.deepEqual(groveSpot(TREES_PER_TERRACE, origin).plot, { x: -3, z: 2 }, 'the 17th tree opens the next terrace to the west');
  const spots = Array.from({ length: 64 }, (_, i) => groveSpot(i, origin));
  for (let i = 0; i < spots.length; i++) {
    const a = spots[i];
    assert.ok(Math.abs(a.local.x) < PLOT_SIZE / 2 && Math.abs(a.local.z) < PLOT_SIZE / 2, 'inside its terrace');
    for (let j = i + 1; j < spots.length; j++) {
      const b = spots[j];
      if (a.terrace === b.terrace) assert.ok(Math.hypot(a.local.x - b.local.x, a.local.z - b.local.z) > 1.6, `trees ${i} and ${j} do not overlap`);
    }
  }
  assert.equal(groveTerraces(0), 1); assert.equal(groveTerraces(16), 1); assert.equal(groveTerraces(17), 2);
});

test('sea chart: the sanctuary in the middle, islands on a spiral by creation order, stable and hittable', () => {
  const islands = [
    { id: 'sanctuary', order: 0, sanctuary: true, commissions: 4 },
    { id: 'w-a', order: 1, commissions: 12 },
    { id: 'w-b', order: 2, commissions: 0 },
    { id: 'w-c', order: 3, commissions: 3 },
  ];
  const layout = islandLayout(islands);
  assert.deepEqual([layout[0].x, layout[0].y], [500, 500], 'the sanctuary sits in the middle');
  const dist = layout.map((l) => Math.hypot(l.x - 500, l.y - 500));
  assert.ok(dist[1] < dist[2] && dist[2] < dist[3], 'older workspaces are closer to the middle');
  for (let i = 0; i < layout.length; i++) for (let j = i + 1; j < layout.length; j++) {
    assert.ok(Math.hypot(layout[i].x - layout[j].x, layout[i].y - layout[j].y) > layout[i].size + layout[j].size * 0.6, `islands ${i} and ${j} do not overlap`);
  }
  assert.deepEqual(islandLayout(islands.slice(0, 3)).map((l) => [l.x, l.y]), layout.slice(0, 3).map((l) => [l.x, l.y]), 'a new island does not move the old ones');
  const t = chartTransform(layout, 1600, 1000);
  for (const l of layout) {
    const c = t.toScreen(l.x, l.y);
    assert.ok(c.x > 0 && c.x < 1600 && c.y > 0 && c.y < 1000, `${l.id} is on screen`);
    assert.equal(hitIsland(layout, t, c.x, c.y), l.id, `clicking ${l.id} hits it`);
  }
  assert.equal(hitIsland(layout, t, 2, 2), null, 'open sea hits nothing');
});

test('intents (M2): the new page intents are in the closed set; still no approval', () => {
  for (const name of ['place-item', 'unplace-item', 'remove-item', 'take-suggestion', 'request-oracle', 'select-island', 'meet-resident', 'try-resident']) {
    assert.ok(INTENT_TYPES.includes(name), name);
    assert.equal(intent(name, { x: 1 }).type, name);
  }
  assert.ok(!INTENT_TYPES.some((t) => /approv|allow|grant|consent/i.test(t)));
});

test('water: the sea moves, and every shader patch actually lands in three.js\'s standard shader', () => {
  // 2026-10-12，用户：模拟水体效果。海的材质是 MeshStandardMaterial + onBeforeCompile 的字符串替换——
  // 一个 #include 改了名，String.replace 什么也不做、不报错，海就悄悄变回一块静止的板（规则 1 的形状）。这里用 three.js 自己的着色器源码跑一遍替换
  const m = makeWaterMaterial(new THREE.Texture());
  const shader = { uniforms: {}, vertexShader: THREE.ShaderLib.standard.vertexShader, fragmentShader: THREE.ShaderLib.standard.fragmentShader };
  m.onBeforeCompile(shader);
  for (const needle of ['swell(wpos0.xz, uTime, sh, sg)', 'transformed.y += sh', 'vWaterWorld = vec3(']) {
    assert.ok(shader.vertexShader.includes(needle), `vertex patch landed: ${needle}`);
  }
  for (const needle of ['waterFoam = smoothstep', 'normal = normalize((viewMatrix * vec4(nW, 0.0)).xyz)']) {
    assert.ok(shader.fragmentShader.includes(needle), `fragment patch landed: ${needle}`);
  }
  for (const u of ['uTime', 'uRipple', 'uRects', 'uRectCount', 'uBoat', 'uNight']) assert.ok(shader.uniforms[u], `uniform ${u} is wired`);
  // update() 推进的是同一份 uniform（编译前后都算数）
  m.userData.water.update({ time: 12.5, night: 1, boat: { x: 3, z: 40, speed: 2 }, shores: [[-300, 28, 300, 29.6], [-1.6, 27, 1.6, 41]] });
  assert.equal(shader.uniforms.uTime.value, 12.5);
  assert.equal(shader.uniforms.uRectCount.value, 2);
  assert.deepEqual(shader.uniforms.uRects.value[1].toArray(), [-1.6, 27, 1.6, 41]);
  assert.throws(() => m.userData.water.update({ shores: Array.from({ length: MAX_RECTS + 1 }, () => [0, 0, 1, 1]) }), /at most/, 'more shores than the shader holds is an error, not a silent cut');
  assert.ok(shader.uniforms.uDeep.value.b < 0.3, 'the deep water at night is dark');
  // 水面不反光（2026-10-12，用户：水体不需要加光照反射——第一版粗糙度 0.1 加菲涅耳映天，近看满海白花花的高光碎片）
  assert.equal(m.roughness, 1, 'the sea is matte: no sun glints');
  assert.equal(m.metalness, 0);
  assert.equal(m.envMapIntensity, 0, 'the sea does not reflect the environment');
  assert.ok(!shader.fragmentShader.includes('uSky'), 'no sky reflection is mixed in');
  // 涌浪不大：海面在 -0.7，码头石岸顶在 0.05、栈桥桥面在 0.1——浪峰离它们远得很；海面确实随时间变
  const bound = SWELLS.reduce((a, w) => a + w.amp, 0);
  assert.ok(bound < 0.1, `swell amplitude bound ${bound.toFixed(3)} m`);
  let moved = 0;
  for (let k = 0; k < 200; k++) {
    const x = (k * 7.3) % 60 - 30, z = 28 + ((k * 3.1) % 40);
    assert.ok(Math.abs(swellHeight(x, z, k * 0.37)) <= bound + 1e-9);
    moved = Math.max(moved, Math.abs(swellHeight(x, z, 0) - swellHeight(x, z, 1.5)));
  }
  assert.ok(moved > 0.02, `the sea surface moves over time (${moved.toFixed(3)} m)`);
});

test('night windows: houses and workshops have lit window panes, ruins none, and the glow follows the night', async () => {
  // 2026-10-12，用户：夜里建筑窗户内要透出灯火。窗洞用一份共用的 windowLit，夜色一到整城一起亮
  const { buildHouse, buildWorkshop } = await import('../../../../Assets/Polis/src/art/buildings.mjs');
  const { setNightWindows, WINDOW_GLOW } = await import('../../../../Assets/Polis/src/art/materials.mjs');
  const { rng } = await import('../../../../Assets/Polis/src/art/noise.mjs');
  const M = stubMaterials();
  const litPanes = (group) => { let n = 0; group.traverse((o) => { if (o.isMesh && o.material === M.windowLit) n++; }); return n; };
  for (const sizeClass of [1, 2, 3, 4]) {
    for (const subtype of ['courtyard', 'pergola', 'twin', 'balcony']) {
      if (subtype === 'balcony' && sizeClass < 3) continue;
      const house = buildHouse(M, { key: `h${sizeClass}${subtype}`, sizeClass, subtype, state: 'active' }, rng(sizeClass * 31 + subtype.length));
      assert.ok(litPanes(house) > 0, `民居 ${subtype} / ${sizeClass}：有点灯的窗`);
    }
    const ruin = buildHouse(M, { key: `r${sizeClass}`, sizeClass, subtype: 'courtyard', state: 'ruin' }, rng(sizeClass));
    assert.equal(litPanes(ruin), 0, '废墟没有窗，也就没有灯');
  }
  let workshops = 0;
  for (let seed = 1; seed <= 24; seed++) if (litPanes(buildWorkshop(M, { key: `w${seed}`, sizeClass: 1 + (seed % 4), state: 'active' }, rng(seed))) > 0) workshops++;
  assert.ok(workshops >= 18, `作坊多数有点灯的窗（${workshops} / 24）——平房的作坊第一版一扇窗都没有`);
  // 门总是亮的：金库、货栈、粮仓只有一扇门，它不亮整座房子夜里就是黑的（罐棚是敞着的棚子，没有门）
  const { buildTreasury, buildWarehouse } = await import('../../../../Assets/Polis/src/art/buildings.mjs');
  for (const [name, build, subtype] of [['金库', buildTreasury, undefined], ['货栈', buildWarehouse, 'store'], ['粮仓', buildWarehouse, 'granary']]) {
    for (let seed = 1; seed <= 6; seed++) assert.ok(litPanes(build(M, { key: `${name}${seed}`, sizeClass: 1 + (seed % 4), subtype, state: 'active' }, rng(seed), {})) > 0, `${name}：门里透出灯火`);
  }
  const pane = { windowLit: new THREE.MeshStandardMaterial({ emissive: 0xffa94a, emissiveIntensity: 0 }) };
  setNightWindows(pane, 0);
  assert.equal(pane.windowLit.emissiveIntensity, 0, '白天窗洞是黑的');
  setNightWindows(pane, 0.5);
  assert.ok(near(pane.windowLit.emissiveIntensity, WINDOW_GLOW / 2), '过渡时跟着夜色');
  setNightWindows(pane, 3);
  assert.equal(pane.windowLit.emissiveIntensity, WINDOW_GLOW, '夜里亮到 WINDOW_GLOW 为止');
});
