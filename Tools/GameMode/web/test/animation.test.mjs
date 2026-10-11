// M3（决定 2，A 档）的动画：新动作（跑、说话、挥手、接过）、停着时的小动作、动作之间的过渡、头的朝向、弹簧骨上的衣料。
// 运行：node --test Tools/GameMode/web/test/
import { test } from 'node:test';
import assert from 'node:assert/strict';
import * as THREE from '../../../../Assets/Polis/vendor/three.bundle.mjs';
import {
  ACTIONS, JOINT_NAMES, poseAt, restPose, walkPeriod, LIMITS, ONE_SHOT_MS, oneShotDone, fidgetAt, FIDGET, FIDGETS,
} from '../../../../Assets/Polis/src/art/poses.mjs';
import { createChain, stepChain, settleChain, deflection, segmentDirection, windAt, SPRING } from '../../../../Assets/Polis/src/art/springs.mjs';
import { buildCharacter, CHARACTER_IDS } from '../../../../Assets/Polis/src/art/characters.mjs';
import { BLEND_MS, LOOK, setClothWind } from '../../../../Assets/Polis/src/art/rig.mjs';
import { MODEL_ACTIONS, clipFor } from '../../../../Assets/Polis/src/art/asset-manifest.mjs';

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
const same = (a, b) => JOINT_NAMES.every((j) => Object.keys(a[j]).every((k) => near(a[j][k], b[j][k], 1e-9)));

test('poses: the new actions are in the closed set and every one stays finite and bounded for a long time', () => {
  assert.deepEqual([...ACTIONS], ['walk', 'run', 'idle', 'work', 'deliver', 'talk', 'wave', 'receive']);
  for (const action of ['run', 'idle', 'talk', 'wave', 'receive']) {
    for (const speaker of [true, false]) {
      for (let t = 0; t <= 45_000; t += 61) {
        const p = poseAt(action, t, { speaker, seed: 5, stride: 2.2, speed: 10.5 });
        for (const a of angles(p)) {
          assert.ok(Number.isFinite(a), `${action} t=${t}: NaN`);
          assert.ok(Math.abs(a) <= LIMITS.angle, `${action} t=${t}: angle ${a}`);
        }
        for (const k of ['x', 'y', 'z']) assert.ok(Math.abs(p.hips[k]) <= LIMITS.hipsOffset, `${action}: hips.${k}`);
      }
    }
  }
});

test('poses: running is periodic, leans forward, bends the elbows, lifts the hips higher and swings the legs wider than walking', () => {
  const style = { stride: 2.2, speed: 10.5 };
  const P = walkPeriod(style);
  for (let t = 0; t < P; t += 40) {
    const a = poseAt('run', t, style), b = poseAt('run', t + P, style);
    assert.ok(near(a.legL.rx, b.legL.rx) && near(a.shoulderR.rx, b.shoulderR.rx), 'one period later the run repeats');
    assert.ok(a.elbowL.rx <= -1.3 && a.elbowR.rx <= -1.3, 'elbows stay bent');
  }
  const run = poseAt('run', 123, style), walk = poseAt('walk', 123, style);
  assert.ok(run.spine.rx > walk.spine.rx + 0.1, 'leans into the run');
  const amp = (action) => { let lo = Infinity, hi = -Infinity; for (let t = 0; t < P; t += 5) { const y = poseAt(action, t, style).hips.y; lo = Math.min(lo, y); hi = Math.max(hi, y); } return hi - lo; };
  assert.ok(amp('run') > amp('walk') * 1.5, 'the hips rise and fall more when running');
  const swing = (action) => Math.max(...Array.from({ length: 40 }, (_, i) => Math.abs(poseAt(action, (P * i) / 40, style).legL.rx)));
  assert.ok(swing('run') > swing('walk') * 1.4, 'the legs swing wider');
});

test('poses: talking — the speaker gestures with one hand, the listener holds still and nods; a carried tool keeps its hand', () => {
  const vary = (key, style) => { const vals = []; for (let t = 0; t < 6000; t += 50) vals.push(key(poseAt('talk', t, style))); return Math.max(...vals) - Math.min(...vals); };
  assert.ok(vary((p) => p.shoulderR.rx, { speaker: true }) > 0.3, 'the speaker\'s right arm moves');
  assert.ok(vary((p) => p.shoulderR.rx, { speaker: false }) < 1e-9, 'the listener\'s arms stay put');
  assert.ok(vary((p) => p.head.rx, { speaker: false }) > 0.05, 'the listener nods');
  const carry = { side: 'R', grip: 'staff', prop: 'rod' };
  assert.ok(vary((p) => p.shoulderR.rx, { speaker: true, carry }) < 1e-9, 'holding a staff in the right hand, that arm does not gesture');
  assert.ok(vary((p) => p.shoulderL.rx, { speaker: true, carry }) > 0.3, 'the left hand gestures instead');
  assert.equal(poseAt('talk', 300, { carry }).propR, 'rod', 'and the staff stays in hand');
  const a = poseAt('talk', 777, { seed: 1 }), b = poseAt('talk', 777, { seed: 2 });
  assert.ok(!same(a, b), 'two speakers with different seeds are not in lockstep');
});

test('poses: waving and receiving are one-shots: rest at both ends, the gesture in the middle, done at their duration', () => {
  const rest = restPose();
  for (const action of ['wave', 'receive']) {
    const ms = ONE_SHOT_MS[action];
    assert.ok(same(poseAt(action, 0), rest), `${action} starts at rest`);
    assert.ok(same(poseAt(action, ms), rest) && same(poseAt(action, ms + 500), rest), `${action} ends at rest and stays there`);
    assert.ok(!oneShotDone(action, ms - 1) && oneShotDone(action, ms), `${action} is done at ${ms} ms`);
    let worst = 0;
    for (let t = 0; t < ms; t += 16) {
      const a = angles(poseAt(action, t)), b = angles(poseAt(action, t + 16));
      worst = Math.max(worst, ...a.map((v, i) => Math.abs(v - b[i])));
    }
    assert.ok(worst < 0.12, `${action}: no joint jumps more than 0.12 rad per frame (worst ${worst.toFixed(3)})`);
  }
  assert.ok(poseAt('wave', ONE_SHOT_MS.wave * 0.5).shoulderR.rx < -2.0, 'the hand goes up over the shoulder');
  const reach = poseAt('receive', ONE_SHOT_MS.receive * 0.4);
  assert.ok(reach.shoulderL.rx < -0.9 && reach.shoulderR.rx < -0.9 && reach.elbowL.rx > -0.6, 'both arms reach out');
  assert.ok(!oneShotDone('walk', 1e9) && !oneShotDone('idle', 1e9), 'loops are never done');
});

test('poses: idle fidgets come after the first quiet slot, are deterministic, differ by seed, and carrying residents only look and shift', () => {
  for (let t = 0; t < FIDGET.everyMs; t += 100) assert.equal(fidgetAt(t, { seed: 0 }), null, `quiet during the first slot (t=${t})`);
  const first = FIDGET.everyMs + 400;
  const f = fidgetAt(first, { seed: 0 });
  assert.ok(f && FIDGETS.includes(f.kind) && f.e > 0 && f.e <= 1, 'a fidget during the second slot');
  assert.deepEqual(fidgetAt(first, { seed: 0 }), f, 'same inputs, same fidget');
  assert.equal(fidgetAt(first + FIDGET.lastsMs + 50, { seed: 0 }), null, 'and it is over after lastsMs');
  const kinds = new Set();
  for (let k = 1; k < 30; k++) kinds.add(fidgetAt(k * FIDGET.everyMs + 300, { seed: 0 })?.kind);
  assert.ok(kinds.size >= 4, `the fidgets vary over time (${[...kinds].join(',')})`);
  for (let k = 1; k < 30; k++) {
    const c = fidgetAt(k * (FIDGET.everyMs + 600) + 300, { seed: 1, carry: { side: 'L', grip: 'hug', prop: 'tablet' } });
    if (c) assert.ok(c.kind === 'look' || c.kind === 'shift', `carrying: ${c.kind} is not allowed`);
  }
  const t0 = [], t1 = [];
  for (let t = 0; t < 60_000; t += 100) { t0.push(!!fidgetAt(t, { seed: 0 })); t1.push(!!fidgetAt(t, { seed: 4 })); }
  assert.ok(t0.some((v, i) => v !== t1[i]), 'two seeds fidget at different moments');
  const p = poseAt('idle', first, { seed: 0 }), q = poseAt('idle', first, { seed: 99 });
  assert.ok(!same(p, q), 'the fidget shows in the pose');
});

test('springs: a chain keeps its lengths, trails a moving anchor within maxAngle, settles when it stops, obeys the wind and is deterministic', () => {
  const down = new THREE.Vector3(0, -1, 0);
  const run = (fn) => {
    const chain = createChain({ segments: 3, segmentLength: 0.35 });
    const anchor = new THREE.Vector3(0, 1, 0);
    const log = [];
    for (let i = 0; i < 240; i++) {
      fn(i, anchor);
      stepChain(chain, { dt: 1 / 60, anchor, restDir: down });
      log.push(deflection(chain, anchor, down));
      // 定长
      for (let k = 0; k < 3; k++) {
        const parent = k === 0 ? anchor : chain.nodes[k - 1].pos;
        assert.ok(near(chain.nodes[k].pos.distanceTo(parent), 0.35, 1e-6), `segment ${k} keeps its length`);
      }
      // 不超过最大偏角
      const dir = segmentDirection(chain, 0, anchor);
      assert.ok(Math.acos(Math.max(-1, Math.min(1, dir.dot(down)))) <= chain.maxAngle + 1e-6, 'within maxAngle');
    }
    return { chain, log };
  };
  // 锚点匀速走 1 秒（6 米 / 秒）再停 3 秒
  const { log } = run((i, anchor) => { if (i < 60) anchor.z += 0.1; });
  assert.ok(log[59] > 0.25, `trails behind while moving (${log[59].toFixed(3)} m)`);
  assert.ok(log[239] < 0.02, `settles after stopping (${log[239].toFixed(3)} m)`);
  const a = run((i, anchor) => { if (i < 60) anchor.z += 0.1; }).log, b = run((i, anchor) => { if (i < 60) anchor.z += 0.1; }).log;
  assert.deepEqual(a, b, 'deterministic');
  // 风：吹向 −z 的风把静止的链推向 −z
  const chain = createChain({ segments: 2, segmentLength: 0.4 });
  const anchor = new THREE.Vector3(0, 1, 0);
  for (let i = 0; i < 120; i++) stepChain(chain, { dt: 1 / 60, anchor, restDir: down, wind: new THREE.Vector3(0, 0, -1.2) });
  assert.ok(chain.nodes[1].pos.z < -0.1, `the wind pushes the tip to −z (${chain.nodes[1].pos.z.toFixed(3)})`);
  // 碰撞体把节点推出去
  const c2 = createChain({ segments: 1, segmentLength: 0.5 });
  const a2 = new THREE.Vector3(0, 1, 0);
  for (let i = 0; i < 60; i++) stepChain(c2, { dt: 1 / 60, anchor: a2, restDir: down, colliders: [{ center: new THREE.Vector3(0, 0.5, 0), radius: 0.3 }] });
  assert.ok(c2.nodes[0].pos.distanceTo(new THREE.Vector3(0, 0.5, 0)) >= 0.3 - 1e-6, 'the tip stays outside the sphere');
  // 30 帧与 60 帧的手感一样：同一秒的位移差得不多
  const d60 = run((i, anchor) => { if (i < 60) anchor.z += 0.1; }).log[59];
  const c30 = createChain({ segments: 3, segmentLength: 0.35 });
  const a30 = new THREE.Vector3(0, 1, 0);
  let d30 = 0;
  for (let i = 0; i < 30; i++) { a30.z += 0.2; stepChain(c30, { dt: 1 / 30, anchor: a30, restDir: down }); d30 = deflection(c30, a30, down); }
  assert.ok(Math.abs(d30 - d60) < 0.02, `30 fps ${d30.toFixed(3)} vs 60 fps ${d60.toFixed(3)}（30 帧拆两步、锚点插值，结果一样）`);
  assert.ok(windAt(0).length() >= 0 && windAt(12_345).length() < 2, 'the wind is a bounded, finite drift');
  assert.throws(() => createChain({ segments: 0, segmentLength: 1 }), RangeError);
  void settleChain; void SPRING;
});

test('rig: switching actions crossfades over BLEND_MS, a variant change too, and the head turns toward a lookAt point', () => {
  const M = stubMaterials();
  const f = buildCharacter(M, 'player');
  f.pose('idle', 0, { clock: 1000 });
  const idleSpine = f.joints.spine.rotation.x;
  f.pose('run', 0, { clock: 1016, stride: 2.2, speed: 10.5 });
  assert.ok(f.state.from, 'a transition is in progress');
  assert.ok(near(f.joints.spine.rotation.x, idleSpine, 1e-6), 'the frame of the switch still shows the old pose');
  f.pose('run', 84, { clock: 1100, stride: 2.2, speed: 10.5 });
  const mid = f.joints.spine.rotation.x;
  const runSpine = poseAt('run', 84, { stride: 2.2, speed: 10.5 }).spine.rx;
  assert.ok(mid > idleSpine + 0.02 && mid < runSpine - 0.05, `84 ms in, the lean is between idle (${idleSpine.toFixed(3)}) and run (${runSpine.toFixed(3)}): ${mid.toFixed(3)}`);
  f.pose('run', 400, { clock: 1016 + BLEND_MS + 50, stride: 2.2, speed: 10.5 });
  assert.equal(f.state.from, null, 'the transition is over after BLEND_MS');
  f.pose('work', 0, { clock: 2000, variant: 'read' });
  f.pose('work', 0, { clock: 2016, variant: 'hammer' });
  assert.ok(f.state.from, 'a new work variant starts a transition too');
  f.pose('work', 500, { clock: 2016 + BLEND_MS + 20, variant: 'hammer' });
  f.pose('work', 516, { clock: 2016 + BLEND_MS + 36, variant: 'hammer' });
  assert.equal(f.state.from, null, 'and the same variant does not restart it');

  // 头看向左前方的一个点：几帧之后转过去，夹在上限之内；目标在身后就回正
  const g = buildCharacter(M, 'scribe');
  let clock = 5000;
  for (let i = 0; i < 90; i++) { clock += 16; g.pose('idle', 0, { clock, lookAt: { x: 5, y: 1.4, z: 5 } }); }
  assert.ok(g.state.look.yaw > 0.7 && g.state.look.yaw <= LOOK.maxYaw, `looks left-forward (${g.state.look.yaw.toFixed(3)})`);
  const base = poseAt('idle', 0, { seed: 0 }).head.ry;
  assert.ok(g.joints.head.rotation.y > base + 0.6, 'the head joint actually turned');
  for (let i = 0; i < 90; i++) { clock += 16; g.pose('idle', 0, { clock, lookAt: { x: 0, y: 1.4, z: -5 } }); }
  assert.ok(Math.abs(g.state.look.yaw) < 0.05, 'a point behind the back is not looked at');
  for (let i = 0; i < 90; i++) { clock += 16; g.pose('work', 0, { clock, variant: 'write', lookAt: { x: 5, y: 1.4, z: 5 } }); }
  const writing = poseAt('work', 0, { variant: 'write' }).head.ry;
  assert.ok(near(g.joints.head.rotation.y, writing, 1e-6), 'while working the head has its own business');
});

test('rig: spring-bone strips hang where they belong (veil, cape, braids, apron) and never blow up', () => {
  const M = stubMaterials();
  setClothWind(new THREE.Vector3(0, 0, 0));
  const strips = (id) => buildCharacter(M, id).cloth.length;
  assert.equal(strips('player'), 0, 'a plain himation has no strips: its skirt is the simulated garment (cloth.test.mjs)');
  assert.ok(strips('priestess') >= 3, 'the veil adds chains');
  assert.ok(strips('herald') >= 3, 'the cape adds chains');
  assert.ok(strips('maiden') >= 2, 'the braids add chains');
  assert.ok(strips('smith') >= 1, 'the apron adds a chain');
  const skinned = [];
  buildCharacter(M, 'priestess').root.traverse((o) => { if (o.isSkinnedMesh) skinned.push(o); });
  assert.ok(skinned.length >= 1, 'the veil is a skinned mesh');
  for (const m of skinned) {
    const w = m.geometry.attributes.skinWeight;
    for (let i = 0; i < w.count; i += 37) {
      const sum = w.getX(i) + w.getY(i) + w.getZ(i) + w.getW(i);
      assert.ok(near(sum, 1, 1e-5), `skin weights sum to 1 (vertex ${i}: ${sum})`);
    }
  }
  // 斗篷：跑起来滞后、停下来回摆、瞬移重新安放、长短帧都不炸
  const f = buildCharacter(M, 'herald');
  const cape = f.cloth.find((st) => st.chain.segments === 3);
  const anchor = () => cape.root.getWorldPosition(new THREE.Vector3());
  const tip = () => cape.chain.nodes[cape.chain.nodes.length - 1].pos.clone();
  let clock = 0, dist = 0;
  for (let i = 0; i < 60; i++) { clock += 16.67; f.pose('idle', clock, { clock }); }
  for (let i = 0; i < 60; i++) { clock += 16.67; f.root.position.z += 0.1; dist += 0.1; f.pose('run', (dist / 6) * 1000, { clock, speed: 6, stride: 2.2 }); }
  assert.ok(anchor().z - tip().z > 0.3, `running at 6 m/s the cape streams behind (${(anchor().z - tip().z).toFixed(3)})`);
  for (let i = 0; i < 150; i++) { clock += 16.67; f.pose('idle', clock, { clock }); }
  assert.ok(Math.hypot(tip().x - anchor().x, tip().z - anchor().z) < 0.25, 'after a stop the cape hangs again');
  f.root.position.x += 10;
  clock += 16.67; f.pose('idle', clock, { clock });
  assert.ok(Math.hypot(tip().x - anchor().x, tip().z - anchor().z) < 0.5, 'after a teleport the cape is re-seated');
  f.pose('idle', clock, { clock });
  clock += 400; f.pose('idle', clock, { clock });
  for (const strip of f.cloth) for (const n of strip.chain.nodes) assert.ok(Number.isFinite(n.pos.x + n.pos.y + n.pos.z), 'finite');
});

test('asset manifest: the new actions are known; a model without a clip for them falls back sensibly', () => {
  assert.deepEqual([...MODEL_ACTIONS], ['walk', 'run', 'idle', 'work', 'deliver', 'talk', 'wave', 'receive']);
  const entry = { animations: { walk: 'Walk', idle: 'Idle' } };
  assert.equal(clipFor(entry, 'run'), 'Walk', 'run falls back to walk');
  assert.equal(clipFor(entry, 'talk'), 'Idle', 'talk falls back to idle');
  assert.equal(clipFor({ animations: { run: 'Run' } }, 'run'), 'Run');
  assert.equal(clipFor({ animations: {} }, 'run'), null, 'nothing at all means still');
});
