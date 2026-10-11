// 布料模拟（M3 追加）：代理布的约束、钉住、系绳、碰撞、风、确定性，细网格的绑定与重建；以及真人物的裙摆不被腿穿过。
// 运行：node --test Tools/GameMode/web/test/
import { test } from 'node:test';
import assert from 'node:assert/strict';
import * as THREE from '../../../../Assets/Polis/vendor/three.bundle.mjs';
import { createCloth, resetCloth, stepCloth, bindFine, applyFine, clothPoint, signedDistance, bodySupport, CLOTH } from '../../../../Assets/Polis/src/art/cloth.mjs';
import { buildCharacter, CHARACTER_IDS } from '../../../../Assets/Polis/src/art/characters.mjs';
import { setClothWind } from '../../../../Assets/Polis/src/art/rig.mjs';

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

/** 一条裙摆形状的代理：下沿半径 0.28、上沿 0.16，高 1 米，上沿在 y = 0。 */
const skirt = (rings = 10, segs = 24, pinnedRings = 2) => createCloth({
  rings, segs, pinnedRings,
  rest: (j, k) => { const t = j / (rings - 1), th = (k / segs) * Math.PI * 2, r = 0.28 - 0.12 * t; return [r * Math.sin(th), -1 + t, r * Math.cos(th)]; },
});
const frameAt = (x, z, yaw = 0) => new THREE.Matrix4().compose(new THREE.Vector3(x, 1.1, z), new THREE.Quaternion().setFromAxisAngle(new THREE.Vector3(0, 1, 0), yaw), new THREE.Vector3(1, 1, 1));   // 下沿离地 0.1 米
const maxStretch = (cloth) => {
  let worst = 0;
  for (const c of cloth.constraints) {
    if (c.k < 1) continue;   // 只看结构约束
    const d = Math.hypot(cloth.pos[c.b * 3] - cloth.pos[c.a * 3], cloth.pos[c.b * 3 + 1] - cloth.pos[c.a * 3 + 1], cloth.pos[c.b * 3 + 2] - cloth.pos[c.a * 3 + 2]);
    worst = Math.max(worst, Math.abs(d - c.rest) / c.rest);
  }
  return worst;
};

test('cloth: rests where it is told, keeps its structure within a few percent while the body moves, and the pinned rings follow the frame exactly', () => {
  const c = skirt();
  resetCloth(c, frameAt(0, 0));
  assert.ok(Math.abs(clothPoint(c, 0, 0).y - 0.1) < 1e-6 && Math.abs(clothPoint(c, 9, 0).y - 1.1) < 1e-6, 'hem at y=0.1, waist at y=1.1');
  // 0.3 秒内加速到 3 米 / 秒（一步到位的 3 米 / 秒是无穷大的加速度，钉子一帧跳 5 厘米，紧挨着钉子的那一圈会短暂拉长一成多——
  // 玩家起步时也会这样，布一两帧就追上），之后带走路那样 3 厘米的左右摇和 0.3 弧度 / 秒的慢慢转身
  let x = 0, worstTransient = 0;
  for (let i = 0; i < 180; i++) {
    const v = 3 * Math.min(1, i / 18);
    x += v / 60;
    const f = frameAt(x, Math.sin(i / 9) * 0.03, i / 200);
    stepCloth(c, { dt: 1 / 60, frame: f });
    const waist = new THREE.Vector3(c.restLocal[(9 * 24 + 5) * 3], c.restLocal[(9 * 24 + 5) * 3 + 1], c.restLocal[(9 * 24 + 5) * 3 + 2]).applyMatrix4(f);
    assert.ok(clothPoint(c, 9, 5).distanceTo(waist) < 1e-6, 'a pinned vertex sits exactly where the frame puts it (the per-substep frame interpolation re-composes the matrix: ~1e-8)');
    worstTransient = Math.max(worstTransient, maxStretch(c));
    // 实测：加速到 3 米 / 秒时拉长最多 19 %，一秒内回到 4–8 %，摇和转身偶尔到 11 %（cloth.mjs 6 次迭代）
    if (i >= 60) assert.ok(maxStretch(c) < 0.12, `in steady motion the structure stretches less than 12 % (frame ${i}: ${(maxStretch(c) * 100).toFixed(1)} %)`);
    for (let k = 0; k < 24; k++) {
      const p = clothPoint(c, 0, k), top = clothPoint(c, 9, k);
      assert.ok(p.distanceTo(top) <= c.tether[k] * CLOTH.tetherSlack + 1e-6, 'the tether holds');
    }
  }
  assert.ok(worstTransient < 0.3, `even while accelerating no structural constraint stretches more than 30 % (${(worstTransient * 100).toFixed(1)} %)`);
  const hem = clothPoint(c, 0, 12), waist = clothPoint(c, 9, 12);
  assert.ok(waist.x - hem.x > 0.05, `moving forward, the hem trails behind the waist (${(waist.x - hem.x).toFixed(3)} m)`);
  for (let i = 0; i < 240; i++) stepCloth(c, { dt: 1 / 60, frame: frameAt(x, Math.sin(179 / 9) * 0.03, 179 / 200) });
  const settled = clothPoint(c, 0, 12), w2 = clothPoint(c, 9, 12);
  assert.ok(Math.hypot(settled.x - w2.x, settled.z - w2.z) < 0.2, 'four seconds after stopping the hem hangs under the waist again');
  for (let i = 0; i < c.n * 3; i++) assert.ok(Number.isFinite(c.pos[i]), 'finite');
  // 原地急转（1.5 弧度 / 秒，玩家转身比这还快）：裙摆跟不上、短暂拉长，但有上限，停下一秒内恢复
  let worstSpin = 0;
  for (let i = 0; i < 90; i++) { stepCloth(c, { dt: 1 / 60, frame: frameAt(x, 0, 179 / 200 + i / 40) }); worstSpin = Math.max(worstSpin, maxStretch(c)); }
  assert.ok(worstSpin < 0.4, `spinning in place stretches the structure less than 40 % (${(worstSpin * 100).toFixed(1)} %)`);
  for (let i = 0; i < 60; i++) stepCloth(c, { dt: 1 / 60, frame: frameAt(x, 0, 179 / 200 + 89 / 40) });
  assert.ok(maxStretch(c) < 0.05, `a second after the spin the structure is back within 5 % (${(maxStretch(c) * 100).toFixed(1)} %)`);
});

test('cloth: a capsule swept through the hanging cloth never ends up inside it, and the floor stops the hem', () => {
  const c = skirt();
  resetCloth(c, frameAt(0, 0));
  const leg = { a: new THREE.Vector3(0, 1.1, 0), b: new THREE.Vector3(0, 0.2, 0), r: 0.07 };
  for (let i = 0; i < 160; i++) {
    const swing = Math.sin(i / 12) * 0.4;   // 大腿前后摆 ±0.4 米（比真人夸张）
    leg.b.set(0, 0.2, swing);
    stepCloth(c, { dt: 1 / 60, frame: frameAt(0, 0), colliders: [leg] });
    for (let v = 0; v < c.n; v++) {
      if (c.pinned[v]) continue;
      const p = new THREE.Vector3(c.pos[v * 3], c.pos[v * 3 + 1], c.pos[v * 3 + 2]);
      assert.ok(signedDistance(p, leg) >= c.thickness - 1e-4, `frame ${i}: vertex ${v} is ${signedDistance(p, leg).toFixed(4)} from the leg axis surface`);
      assert.ok(p.y >= CLOTH.floorY - 1e-9, 'never below the floor');
    }
  }
  // 低矮的坐标系：下沿本该在地下，被地面托住
  const low = skirt();
  for (let i = 0; i < 60; i++) stepCloth(low, { dt: 1 / 60, frame: new THREE.Matrix4().makeTranslation(0, 0.5, 0) });
  assert.ok(clothPoint(low, 0, 3).y >= CLOTH.floorY - 1e-9, 'the hem rests on the floor');
});

test('cloth: deterministic, re-seated after a teleport, pushed by the wind, and the same at 30 and 60 fps', () => {
  // 两种帧率都先安放在同一处，再从 t = dt 起走同一条轨迹（第一次调用只能安放，没有上一帧可以插值）
  const run = (dt) => {
    const c = skirt();
    resetCloth(c, frameAt(0, 0));
    for (let i = 1; i * dt <= 2 + 1e-9; i++) stepCloth(c, { dt, frame: frameAt(Math.min(3, i * dt * 2), 0) });
    return clothPoint(c, 0, 12);
  };
  assert.ok(run(1 / 60).distanceTo(run(1 / 60)) === 0, 'deterministic');
  assert.ok(run(1 / 30).distanceTo(run(1 / 60)) < 1e-6, `30 fps integrates exactly like 60 fps（两步、钉子按步插值）: ${run(1 / 30).distanceTo(run(1 / 60)).toExponential(2)}`);
  const c = skirt();
  for (let i = 0; i < 30; i++) stepCloth(c, { dt: 1 / 60, frame: frameAt(0, 0) });
  stepCloth(c, { dt: 1 / 60, frame: frameAt(12, 0) });
  const hem = clothPoint(c, 0, 0), waist = clothPoint(c, 9, 0);
  assert.ok(Math.hypot(hem.x - waist.x, hem.z - waist.z) < 0.2, `after a 12 m jump the cloth is re-seated under the waist (${Math.hypot(hem.x - waist.x, hem.z - waist.z).toFixed(3)})`);
  const w = skirt();
  for (let i = 0; i < 120; i++) stepCloth(w, { dt: 1 / 60, frame: frameAt(0, 0), wind: new THREE.Vector3(0, 0, -1.2) });
  assert.ok(clothPoint(w, 0, 0).z < -0.08, `the wind pushes the hem to −z (${clothPoint(w, 0, 0).z.toFixed(3)})`);
  assert.throws(() => createCloth({ rings: 2, segs: 24, rest: () => [0, 0, 0] }), RangeError);
});

test('cloth: a fine mesh with folds bound to the proxy is reproduced exactly at rest and follows the proxy when it moves', () => {
  const c = skirt();
  resetCloth(c, new THREE.Matrix4());
  // 细网格：30 圈 × 65 段的带褶裙摆（和 drape 一样的参数化）
  const R = 30, S = 64, fine = [];
  for (let j = 0; j <= R; j++) for (let k = 0; k <= S; k++) {
    const t = j / R, th = (k / S) * Math.PI * 2;
    const r = 0.28 - 0.12 * t + 0.02 * Math.sin(th * 13) * (1 - t);
    fine.push(r * Math.sin(th), -1 + t + 0.01 * Math.sin(th * 7) * Math.pow(1 - t, 6), r * Math.cos(th));
  }
  const arr = new Float32Array(fine);
  const binding = bindFine(c, arr, (i) => ({ t: (arr[i * 3 + 1] + 1), theta: Math.atan2(arr[i * 3], arr[i * 3 + 2]) }));
  const out = applyFine(c, binding, new Float32Array(arr.length));
  let worst = 0;
  for (let i = 0; i < arr.length; i++) worst = Math.max(worst, Math.abs(out[i] - arr[i]));
  assert.ok(worst < 1e-5, `at rest the fine mesh comes back exactly (worst ${worst.toExponential(2)})`);
  for (let i = 0; i < 90; i++) stepCloth(c, { dt: 1 / 60, frame: frameAt(i * 0.05, 0) });
  applyFine(c, binding, out);
  const hemIndex = (0 * (S + 1) + 32) * 3;   // 下沿、背面
  const proxyHem = clothPoint(c, 0, 12);
  assert.ok(Math.hypot(out[hemIndex] - proxyHem.x, out[hemIndex + 2] - proxyHem.z) < 0.08, 'the fine hem rides on the proxy hem');
  for (let i = 0; i < out.length; i++) assert.ok(Number.isFinite(out[i]));
});

test('figures: every figure wears a simulated skirt; walking, the legs never come out through it; it settles after a stop', () => {
  const M = stubMaterials();
  setClothWind(new THREE.Vector3(0, 0, 0));
  for (const id of CHARACTER_IDS) {
    const f = buildCharacter(M, id);
    assert.equal(f.garments.length, 1, `${id}: one simulated garment`);
    assert.ok(f.garments[0].mesh.parent === f.root, `${id}: the skirt hangs on the figure root (vertices are written in root space)`);
  }
  for (const id of ['player', 'athena', 'smith']) {
    const f = buildCharacter(M, id);
    const speed = id === 'athena' ? 3.2 : 6, stride = id === 'athena' ? 1.6 : 2.0;
    let clock = 0, dist = 0;
    for (let i = 0; i < 30; i++) { clock += 16.67; f.pose('idle', clock, { clock }); }
    const atRest = f.hemPoint(0, 12, new THREE.Vector3());
    const hipsRest = f.joints.hips.getWorldPosition(new THREE.Vector3());
    assert.ok(Math.hypot(atRest.x - hipsRest.x, atRest.z - hipsRest.z) > 0.15, `${id}: at rest the hem hangs away from the body axis`);
    let worstIn = 0;
    for (let i = 0; i < 120; i++) {
      clock += 16.67;
      f.root.position.z += (speed * 16.67) / 1000;
      dist += (speed * 16.67) / 1000;
      f.pose(id === 'player' && i > 60 ? 'run' : 'walk', (dist / speed) * 1000, { clock, speed, stride });
      // 细网格的每个顶点都在腿的外面（允许褶的幅度：细网格是贴在代理上的浮雕，褶最深 2.5 厘米）
      const g = f.garments[0];
      const legs = f.bodyColliders().filter((c) => c.b && c.baseR < 0.1);   // 大腿与小腿的胶囊
      const p = new THREE.Vector3();
      for (let v = 0; v < g.world.length / 3; v += 3) {
        p.set(g.world[v * 3], g.world[v * 3 + 1], g.world[v * 3 + 2]);
        for (const leg of legs) worstIn = Math.min(worstIn, signedDistance(p, leg));
      }
    }
    assert.ok(worstIn > -0.03, `${id}: no skirt vertex deeper than 3 cm inside a leg while walking and running (worst ${worstIn.toFixed(3)} m)`);
    const hem = f.hemPoint(0, 12, new THREE.Vector3()), hips = f.joints.hips.getWorldPosition(new THREE.Vector3());
    assert.ok(hips.z - hem.z > 0.05, `${id}: moving, the back of the hem trails (${(hips.z - hem.z).toFixed(3)} m)`);
    for (let i = 0; i < 150; i++) { clock += 16.67; f.pose('idle', clock, { clock }); }
    const a = f.hemPoint(0, 12, new THREE.Vector3()), b = f.hemPoint(0, 12, new THREE.Vector3());
    clock += 16.67; f.pose('idle', clock, { clock });
    assert.ok(a.distanceTo(f.hemPoint(0, 12, b)) < 0.01, `${id}: 2.5 s after stopping the hem is still (moved ${a.distanceTo(b).toFixed(4)} m in a frame)`);
    const attr = f.garments[0].mesh.geometry.attributes.position;
    for (let i = 0; i < attr.count; i += 50) assert.ok(Number.isFinite(attr.getX(i) + attr.getY(i) + attr.getZ(i)), 'finite geometry');
  }
});

/**
 * 腿有没有露在裙摆外（M3 第三轮，用户：布料加了碰撞之后一穿模，双腿就整条露在裙摆外）：腿表面朝外的采样点沿身体轴的外向水平方向
 * 发一条射线，打不到裙摆的细网格就算露在外面。脚踝一带（长衣）、膝下（短衣）的下摆本来就在那儿，不算。
 */
function bareLegShare(f) {
  const g = f.garments[0];
  const index = g.mesh.geometry.index.array, W = g.world;
  f.joints.hips.updateMatrixWorld(true);
  const toLocal = f.joints.hips.matrixWorld.clone().invert(), toWorld = f.joints.hips.matrixWorld;
  const minY = ['exomis', 'chlamys'].includes(f.spec.garment) ? 0.68 : 0.25;
  const ray = new THREE.Ray(), a = new THREE.Vector3(), b = new THREE.Vector3(), c = new THREE.Vector3(), hit = new THREE.Vector3();
  const L = new THREE.Vector3(), P = new THREE.Vector3(), axis = new THREE.Vector3(), dir = new THREE.Vector3();
  let total = 0, bare = 0;
  for (const cap of f.bodyColliders().filter((x) => x.b && x.baseR < 0.08)) {   // 大腿、小腿
    for (let h = 0.1; h <= 0.9; h += 0.2) {
      L.lerpVectors(cap.a, cap.b, h);
      if (L.y < minY) continue;
      for (let k = 0; k < 8; k++) {
        const th = (k / 8) * Math.PI * 2;
        P.set(L.x + Math.sin(th) * cap.r, L.y, L.z + Math.cos(th) * cap.r);
        const local = P.clone().applyMatrix4(toLocal);
        axis.set(0, local.y, 0).applyMatrix4(toWorld);
        dir.subVectors(P, axis).setY(0);
        if (dir.lengthSq() < 1e-8) continue;
        dir.normalize();
        if (Math.sin(th) * dir.x + Math.cos(th) * dir.z < 0.3) continue;   // 只看背着身体轴、朝外的那一面
        ray.set(P, dir);
        let covered = false;
        for (let i = 0; i < index.length && !covered; i += 3) {
          a.fromArray(W, index[i] * 3); b.fromArray(W, index[i + 1] * 3); c.fromArray(W, index[i + 2] * 3);
          covered = ray.intersectTriangle(a, b, c, false, hit) != null;
        }
        total++;
        if (!covered) bare++;
      }
    }
  }
  // 一个点都没采到（碰撞体还没算过——比如人物从没摆过）不能算"全遮住"
  assert.ok(total >= 20, `sampled ${total} leg points`);
  return bare / total;
}

test('figures: the skirt keeps the legs inside it — a sprint from a standstill, running while turning, kneeling to deliver, and before the first pose', () => {
  const M = stubMaterials();
  setClothWind(new THREE.Vector3(0, 0, 0));
  // 还没 pose 过的人物：裙摆已经垂在站姿的位置（网格挂在根上，几何按髋生成——第一版没摆过的人物裙摆落在脚边、两腿光着）
  for (const id of ['athena', 'player', 'smith']) {
    const fresh = buildCharacter(M, id);
    assert.equal(bareLegShare(fresh), 0, `${id}: a freshly built figure already wears its skirt over its legs`);
  }
  // 旧的最近点推出：起步就冲刺（10.5 米 / 秒）时腿外侧 26–73 % 露在裙摆外；径向挡 + 记住在哪一边 + 上方的身体都算之后是 0
  const scenarios = [
    { id: 'player', action: 'run', speed: 10.5, stride: 2.2, warmup: 0, turn: 0 },
    { id: 'player', action: 'run', speed: 10.5, stride: 2.2, warmup: 30, turn: 4 },
    { id: 'player', action: 'walk', speed: 6, stride: 2.0, warmup: 0, turn: 0 },
    { id: 'athena', action: 'walk', speed: 3.2, stride: 1.6, warmup: 0, turn: 0 },
    { id: 'herald', action: 'run', speed: 4, stride: 2.0, warmup: 0, turn: 0 },
    { id: 'athena', action: 'deliver', speed: 0, stride: 1.6, warmup: 30, turn: 0 },
  ];
  for (const s of scenarios) {
    const f = buildCharacter(M, s.id);
    let clock = 0, dist = 0, worst = 0;
    for (let i = 0; i < s.warmup; i++) { clock += 16.67; f.pose('idle', clock, { clock }); }
    for (let i = 0; i < 108; i++) {
      clock += 16.67;
      f.root.rotation.y += s.turn * 0.01667;
      const step = (s.speed * 16.67) / 1000;
      f.root.position.x += Math.sin(f.root.rotation.y) * step;
      f.root.position.z += Math.cos(f.root.rotation.y) * step;
      dist += step;
      const t = s.speed > 0 ? (dist / s.speed) * 1000 : i * 16.67;
      f.pose(s.action, t, { clock, speed: s.speed, stride: s.stride, duration: 1800 });
      if (i % 6 === 5) worst = Math.max(worst, bareLegShare(f));
    }
    assert.equal(worst, 0, `${s.id} ${s.action} ${s.speed} m/s${s.warmup ? '' : ' from a standstill'}${s.turn ? ' while turning' : ''}: no leg point shows outside the skirt (worst frame ${(worst * 100).toFixed(1)} %)`);
  }
});

test('cloth: the body is pushed out radially and one-sided — a point swept through the legs comes back out on the side it came from', () => {
  // 一根竖着的"腿"在髋的坐标系里，布点一步就从前面穿到了腿后面：最近点推出会把它推到腿后面，径向挡把它推回前面
  const c = skirt();
  const frame = frameAt(0, 0);
  resetCloth(c, frame);
  const leg = { a: new THREE.Vector3(0, 1.0, 0.05), b: new THREE.Vector3(0, 0.15, 0.05), r: 0.07 };
  // 前面那一列（k = 0）中间的一个点：硬塞到腿轴的后面一点
  const i = 4 * 24 + 0;
  c.pos[i * 3 + 2] = 0.02; c.prev[i * 3 + 2] = 0.02;
  stepCloth(c, { dt: 1 / 60, frame, body: [leg] });
  assert.ok(c.pos[i * 3 + 2] > 0.05 + 0.07, `the front point is back in front of the leg (z = ${c.pos[i * 3 + 2].toFixed(3)})`);
  // 支撑函数：竖着的胶囊在它的高度范围里朝外伸出 r，在它上面一点只有球冠，再往上没有
  const e = { a: new THREE.Vector3(0, 0, 0), b: new THREE.Vector3(0, -1, 0), r: 0.1, capsule: true };
  assert.ok(Math.abs(bodySupport(e, -0.5, 0, 1) - 0.1) < 1e-9, 'inside its height range a vertical capsule reaches out r');
  assert.ok(Math.abs(bodySupport(e, 0.06, 0, 1) - 0.08) < 1e-9, 'just above it only the cap counts (sqrt(r² − 0.06²))');
  assert.equal(bodySupport(e, 0.2, 0, 1), -Infinity, 'well above it nothing counts');
  // 下面的布点也被上方伸出去的部分挡住：一条向前抬起的腿（膝盖在前），膝盖下面的布垂在膝盖外面（像帘子）
  const thigh = { a: new THREE.Vector3(0, 0, 0), b: new THREE.Vector3(0, -0.3, 0.3), r: 0.07, capsule: true };
  assert.ok(bodySupport(thigh, -0.6, 0, 1) >= 0.3 + 0.07 - 1e-9, 'below a raised knee the cloth hangs in front of the knee');
  assert.ok(bodySupport(thigh, -0.6, 0, -1) <= 0.07 + 1e-9, 'behind the body the raised knee does not push the back panel');
});
