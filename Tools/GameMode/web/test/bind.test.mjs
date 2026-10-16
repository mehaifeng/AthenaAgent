// 外部人物蒙在程序化骨架上（Assets/Polis/src/art/bind.mjs）：找关节、蒙皮权重、姿势跟着走、清单的 rig。
// 用一个合成的 A-pose 人台（mannequin.mjs：关节在哪是已知的）代替文生 3D 的模型——仓库里不放第三方模型。
//   node --test Tools/GameMode/web/test/bind.test.mjs
import { test } from 'node:test';
import assert from 'node:assert/strict';
import * as THREE from '../../../../Assets/Polis/vendor/three.bundle.mjs';
import { BONES, collectBody, sliceBody, fitLandmarks, skinPart, prepareExternalBody, attachExternalBody } from '../../../../Assets/Polis/src/art/bind.mjs';
import { planOverrides } from '../../../../Assets/Polis/src/art/asset-manifest.mjs';
import { buildCharacter } from '../../../../Assets/Polis/src/art/characters.mjs';
import { restPose } from '../../../../Assets/Polis/src/art/poses.mjs';
import { mannequin, ARM_ANGLE } from './mannequin.mjs';

/** 材质的替身（Node 里没有画布）。 */
function stubMaterials() {
  const cache = new Map();
  return new Proxy({}, {
    get: (_, key) => {
      if (typeof key !== 'string') return undefined;
      if (!cache.has(key)) {
        cache.set(key, key.endsWith('Shades')
          ? Array.from({ length: 12 }, (_, i) => new THREE.MeshStandardMaterial({ name: `${key}${i}` }))
          : new THREE.MeshStandardMaterial({ name: key }));
      }
      return cache.get(key);
    },
  });
}

const ENTRY = { model: 'mannequin.glb', scale: 1, rotateY: 0, offset: [0, 0, 0] };

test('bind: the landmarks of an A-pose mannequin land on its joints', () => {
  const { scene, truth } = mannequin();
  const parts = collectBody(scene, ENTRY);
  const L = fitLandmarks(sliceBody(parts));
  assert.ok(Math.abs(L.H - 1.805) < 0.01, `height ${L.H}`);
  for (const [side, sx] of [['L', 1], ['R', -1]]) {
    assert.ok(L[`shoulder${side}`].distanceTo(truth.shoulder[sx]) < 0.04, `${side} shoulder ${L[`shoulder${side}`].toArray()} vs ${truth.shoulder[sx].toArray()}`);
    assert.ok(L[`wrist${side}`].distanceTo(truth.wrist[sx]) < 0.05, `${side} wrist ${L[`wrist${side}`].toArray()} vs ${truth.wrist[sx].toArray()}`);
    assert.ok(Math.abs(L[`ankle${side}`].x - truth.ankle[sx].x) < 0.02, `${side} ankle x ${L[`ankle${side}`].x}`);
    // 肘在肩与腕之间，指尖在腕外下方
    const elbow = L[`elbow${side}`];
    assert.ok(elbow.y < L[`shoulder${side}`].y && elbow.y > L[`wrist${side}`].y, `${side} elbow between shoulder and wrist`);
    assert.ok(L[`finger${side}`].y < L[`wrist${side}`].y && (L[`finger${side}`].x - L[`wrist${side}`].x) * sx > 0, `${side} fingertips beyond the wrist`);
  }
  assert.ok(L.neckY > 1.5 && L.neckY < 1.64, `neck ${L.neckY}`);
  assert.ok(L.armpitY < L.shoulderTopY && L.armpitY > 1.3, `armpit ${L.armpitY}`);
  assert.ok(Math.abs(L.hemY - 0.24) < 0.02 && L.legsVisible, `hem ${L.hemY}`);
});

test('bind: an arm hanging against the body is not an A-pose — the binder says so instead of guessing', () => {
  const { scene } = mannequin({ armAngle: 0.02 });
  assert.throws(() => fitLandmarks(sliceBody(collectBody(scene, ENTRY))), /A-pose/);
});

test('bind: the weights put the hand on the hand, the head on the head, and keep the dress off the arms', () => {
  const { scene, truth } = mannequin();
  const parts = collectBody(scene, ENTRY);
  const profile = sliceBody(parts);
  const L = fitLandmarks(profile);
  const dominant = (point) => {
    // 离 point 最近的顶点的主骨头
    let best = null, bestD = Infinity;
    const v = new THREE.Vector3();
    for (const { geometry } of parts) {
      if (!geometry.attributes.skinIndex) skinPart(geometry, L, profile);
      const pos = geometry.attributes.position, si = geometry.attributes.skinIndex, sw = geometry.attributes.skinWeight;
      for (let i = 0; i < pos.count; i++) {
        v.fromBufferAttribute(pos, i);
        const d = v.distanceTo(point);
        if (d >= bestD) continue;
        bestD = d;
        let top = 0;
        for (let q = 1; q < 4; q++) if (sw.getComponent(i, q) > sw.getComponent(i, top)) top = q;
        best = BONES[si.getComponent(i, top)];
      }
    }
    return best;
  };
  const handTip = truth.wrist[1].clone().add(new THREE.Vector3(Math.sin(ARM_ANGLE), -Math.cos(ARM_ANGLE), 0).multiplyScalar(0.15));
  assert.equal(dominant(handTip), 'handL');
  const upperArmR = truth.shoulder[-1].clone().addScaledVector(new THREE.Vector3(-Math.sin(ARM_ANGLE), -Math.cos(ARM_ANGLE), 0), 0.2);
  assert.equal(dominant(upperArmR), 'shoulderR', 'the upper arm follows the shoulder');
  assert.equal(dominant(new THREE.Vector3(0, 1.8, 0)), 'head');
  assert.equal(dominant(new THREE.Vector3(0.09, 0.02, 0.1)), 'kneeL', 'the foot follows the shin');
  for (const p of [new THREE.Vector3(0.2, 0.3, 0), new THREE.Vector3(-0.2, 0.3, 0), new THREE.Vector3(0, 0.6, 0.18), new THREE.Vector3(0, 0.6, -0.18)]) {
    assert.ok(!/shoulder|elbow|hand|head/.test(dominant(p)), `the dress at ${p.toArray()} follows the hips and legs, not ${dominant(p)}`);
  }
  for (const { geometry } of parts) {
    const sw = geometry.attributes.skinWeight;
    for (let i = 0; i < sw.count; i++) {
      const total = sw.getX(i) + sw.getY(i) + sw.getZ(i) + sw.getW(i);
      assert.ok(Math.abs(total - 1) < 1e-4, `weights sum to 1 (vertex ${i}: ${total})`);
    }
  }
});

test('bind: on the procedural rig the mesh leaves its A-pose, follows every pose, and the props stay in the hands', () => {
  const { scene } = mannequin();
  const body = prepareExternalBody(scene, ENTRY);
  const figure = attachExternalBody(buildCharacter(stubMaterials(), 'athena'), body);
  // 程序化的身体拆掉了，手里的东西留着
  const visible = [];
  figure.root.traverse((o) => { if (o.isMesh && !o.isSkinnedMesh) visible.push(o); });
  const propMeshes = new Set();
  for (const p of figure.props.values()) p.traverse((o) => propMeshes.add(o));
  assert.ok(visible.every((o) => propMeshes.has(o)), 'only the props are left of the procedural figure');
  assert.ok(figure.props.has('gift') && figure.props.has('sealed'), 'the "your turn" props are still there');
  assert.equal(figure.skinned.length, body.parts.length);
  assert.equal(figure.skeleton.bones.length, BONES.length);
  assert.ok(figure.skinned.every((m) => m.userData.athena), 'Athena\'s body is in the glow mask');
  assert.equal(figure.cloth.length, 0, 'no spring bones left running');
  assert.equal(figure.garments.length, 0, 'no procedural cloth left running');

  const L = body.landmarks;
  const handMesh = figure.skinned.find((m) => {
    const p = m.geometry.attributes.position;
    return p.count > 0 && new THREE.Vector3().fromBufferAttribute(p, 0).distanceTo(L.wristL) < 0.3;
  }) ?? figure.skinned[0];
  // 左手上离手腕最近的一个顶点
  const pos = handMesh.geometry.attributes.position;
  let wristVertex = 0, best = Infinity;
  const v = new THREE.Vector3();
  for (let i = 0; i < pos.count; i++) { v.fromBufferAttribute(pos, i); const d = v.distanceTo(L.wristL.clone().add(new THREE.Vector3(0.02, -0.03, 0))); if (d < best) { best = d; wristVertex = i; } }
  const skinned = (mesh, i) => {
    figure.root.updateMatrixWorld(true);
    mesh.skeleton.update();
    return mesh.getVertexPosition(i, new THREE.Vector3()).applyMatrix4(mesh.matrixWorld);
  };
  const handJoint = () => figure.joints.handL.getWorldPosition(new THREE.Vector3());

  // 站姿：手臂放下来了（A-pose 的手腕离身体 0.43 米，站姿下不到 0.3），手上的顶点贴着程序化的手
  figure.apply(restPose());
  const rest = skinned(handMesh, wristVertex);
  assert.ok(rest.x < 0.3, `the arm hangs down at rest (wrist vertex x ${rest.x.toFixed(3)})`);
  assert.ok(rest.distanceTo(handJoint()) < 0.08, `the wrist vertex rides on the hand joint at rest (${rest.distanceTo(handJoint()).toFixed(3)} m)`);
  // 挥手、走路、交付：手上的顶点一直跟着程序化的手
  for (const [action, t, style] of [['wave', 640, {}], ['walk', 400, { speed: 3.2, stride: 1.6 }], ['deliver', 600, { duration: 1800 }], ['work', 900, { variant: 'write' }]]) {
    figure.pose(action, t, { ...style, clock: 10_000 + t });
    figure.pose(action, t, { ...style, clock: 20_000 + t });   // 过渡走完
    const at = skinned(handMesh, wristVertex);
    assert.ok(at.distanceTo(handJoint()) < 0.08, `${action}: the wrist vertex rides on the hand joint (${at.distanceTo(handJoint()).toFixed(3)} m)`);
  }
  // 名牌和光按外部身体的尺寸
  assert.ok(Math.abs(figure.root.userData.height - L.H) < 1e-9 && figure.root.userData.rig === 'procedural');
});

test('asset manifest: rig is for figures only, only "procedural", and never together with animations', () => {
  const known = ['figure.athena', 'figure.scribe', 'figure.herald', 'building.house'];
  const { overrides, ignored } = planOverrides(known, {
    'figure.athena': { model: 'athena.glb', scale: 1.6, rig: 'procedural' },
    'figure.scribe': { model: 's.glb', rig: 'mixamo' },
    'figure.herald': { model: 'h.glb', rig: 'procedural', animations: { walk: 'Walk' } },
    'building.house': { model: 'house.glb', rig: 'procedural' },
  });
  assert.deepEqual([...overrides.keys()], ['figure.athena']);
  assert.equal(overrides.get('figure.athena').rig, 'procedural');
  const why = Object.fromEntries(ignored.map((i) => [i.id, i.reason]));
  assert.match(why['figure.scribe'], /unknown rig/);
  assert.match(why['figure.herald'], /rig and animations/);
  assert.match(why['building.house'], /only figures/);
  assert.equal(planOverrides(known, { 'figure.athena': { model: 'a.glb' } }).overrides.get('figure.athena').rig, null, 'no rig written: none');
});
