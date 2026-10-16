// 门（第三轮需求 2）：doors.mjs 的规则逐条断言，再在 Node 里把合成夹具的城建出来，核对每扇会开的门都走得到街上、
// 居民的一天里进出门的那几刻、门开几分，以及你照旧进不去。
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import * as THREE from '../../../../Assets/Polis/vendor/three.bundle.mjs';
import {
  DOOR, usableSill, insidePoint, findDoorstep, planApproach, stringPull, transitAt, sillHeight, routeClear,
} from '../../../../Assets/Polis/src/doors.mjs';
import { planCitizen, citizenAt, TOWN } from '../../../../Assets/Polis/src/citizens.mjs';
import { createLayout } from '../../../../Assets/Polis/src/layout.mjs';
import { createAssetLibrary } from '../../../../Assets/Polis/src/art/assets.mjs';
import { buildWorld } from '../../../../Assets/Polis/src/world.mjs';

const fixture = JSON.parse(readFileSync(new URL('../fixtures/synthetic.json', import.meta.url), 'utf8'));

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

function buildCity(city = fixture) {
  const M = stubMaterials();
  const layout = createLayout(city);
  const world = buildWorld(new THREE.Scene(), city, layout, M, { assets: createAssetLibrary(M) });
  return { world, layout };
}

test('only ground-level doors open (rule 1), and the inside point is behind the wall', () => {
  assert.equal(usableSill({ y: 0.25 }), true, '民居');
  assert.equal(usableSill({ y: 0.22 }), true, '作坊');
  assert.equal(usableSill({ y: 0 }), true, '货栈');
  assert.equal(usableSill({ y: 0.75 }), false, '金库在台基上');
  assert.equal(usableSill({ y: 1.1 }), false, '粮仓离地');
  const p = insidePoint({ x: 2, z: 3, nx: 0, nz: 1 });
  assert.deepEqual(p, { x: 2, z: 3 - DOOR.inside });
});

test('the doorstep is the first standable point out from the door, and the way to the street goes round what is in front (rules 2, 3)', () => {
  // 一堵墙在 z ≤ 0（门洞在 z = 0），门前 1.2–1.8 米横着一条长凳（x ∈ [−1, 1]）
  const walkable = (p) => p.z > 0.3 && !(p.z > 1.2 && p.z < 1.8 && p.x > -1 && p.x < 1) && Math.abs(p.x) < 8 && p.z < 8;
  const door = { x: 0, z: 0, y: 0.25, w: 1, h: 2, nx: 0, nz: 1 };
  const step = findDoorstep(door, walkable);
  assert.ok(step && Math.abs(step.x) < 0.31 && step.z > 0.3 && step.z < 0.5, `门阶贴着门：${JSON.stringify(step)}`);
  const path = planApproach(step, { x: 0, z: 6 }, walkable, { minX: -8, maxX: 8, minZ: 0, maxZ: 8 });
  assert.ok(path && path.length >= 3, '绕过长凳');
  assert.ok(routeClear(path, walkable, 0.05), '一路都能站');
  assert.deepEqual(path[0], step);
  assert.deepEqual(path[path.length - 1], { x: 0, z: 6 });
  assert.equal(planApproach(step, { x: 0, z: 6 }, (p) => walkable(p) && p.z < 1.0, { minX: -8, maxX: 8, minZ: 0, maxZ: 8 }), null, '走不到就是 null');
  assert.equal(findDoorstep(door, () => false), null, '门前没有能站的地方：没有门阶');
  const straight = stringPull([{ x: 0, z: 0.5 }, { x: 0.2, z: 0.5 }, { x: 0.4, z: 0.5 }], () => true);
  assert.equal(straight.length, 2, '拉直');
});

test('one pass through a door: it opens before he crosses and closes after; the last moment of going in hides him (rule 4)', () => {
  const { openMs, passMs, legMs, transitMs } = DOOR;
  assert.equal(transitMs, openMs + passMs + legMs);
  assert.equal(transitAt('exit', 0).open, 0);
  assert.equal(transitAt('exit', openMs).open, 1, '开好了才跨出来');
  assert.equal(transitAt('exit', openMs).f, 0);
  assert.equal(transitAt('exit', openMs + passMs - 1).open, 1, '跨的时候门开着');
  assert.equal(transitAt('exit', transitMs).open, 0, '走开之后门关上');
  assert.equal(transitAt('exit', transitMs).phase, 'leg');
  assert.equal(transitAt('exit', transitMs).f, 1);
  assert.equal(transitAt('enter', 0).open, 0, '还没走到门阶，门关着');
  assert.equal(transitAt('enter', legMs).open, 1, '到门阶时门已经开了');
  assert.equal(transitAt('enter', legMs + passMs - 1).hidden, false);
  assert.equal(transitAt('enter', legMs + passMs + 1).hidden, true, '进去之后藏起来');
  assert.equal(transitAt('enter', transitMs).open, 0, '进去之后门关上');
  let last = -1;
  for (let t = 0; t <= transitMs; t += 50) {
    const s = transitAt('exit', t);
    const progress = s.phase === 'pass' ? s.f : 1 + s.f;
    assert.ok(progress >= last - 1e-9, '出门一路往外走，不倒回去');
    last = progress;
  }
  const door = { x: 0, z: 0, y: 0.25, nx: 0, nz: 1 };
  assert.equal(sillHeight(door, 0, 0.1), 0.25, '贴着墙踩在门槛（台基）上');
  assert.equal(sillHeight(door, 0, 0.5), 0, '门阶在街面上');
  assert.ok(sillHeight(door, 0, 0.25) > 0 && sillHeight(door, 0, 0.25) < 0.25);
});

test('the synthetic city: every opening door reaches the street, and only ground-level doors open', () => {
  const { world, layout } = buildCity();
  const kinds = new Map(fixture.buildings.map((b) => [`b:${b.key}`, b]));
  assert.ok(world.doors.size >= 2, `合成的城里有会开的门：${[...world.doors.keys()].join(', ')}`);
  for (const [id, d] of world.doors) {
    const b = kinds.get(d.building);
    assert.ok(['house', 'workshop', 'warehouse'].includes(b.kind), `${id}：只有民居、作坊、货栈有门（${b.kind}）`);
    assert.ok(b.state !== 'ruin', `${id}：废墟没有门`);
    const plan = world.doorPlan(id);
    assert.ok(plan, `${id}：走得到街上`);
    assert.ok(routeClear(plan.approach, layout.walkable), `${id}：门阶到街上一路都能站`);
    assert.ok(!layout.walkable({ x: d.x, z: d.z + 0.05 }) && !layout.walkable(plan.inside), `${id}：门洞和门里你进不去`);
    assert.ok(d.leaves.length >= 1 && d.leaves.every((l) => l.pivot.parent === world.root), `${id}：门扇挂在城里（不在建筑的组里，不进足迹）`);
  }
  for (const b of fixture.buildings.filter((x) => x.kind === 'treasury' || x.kind === 'stoaLibrary' || x.kind === 'sculptureGarden')) {
    assert.equal(world.doorFor(`b:${b.key}`, 1), null, `${b.key}（${b.kind}）没有会开的门`);
  }
  world.setDoorOpen([...world.doors.keys()][0], 1);
  const leaf = world.doors.get([...world.doors.keys()][0]).leaves[0];
  assert.ok(Math.abs(Math.abs(leaf.pivot.rotation.y) - DOOR.openAngle) < 1e-9, '开到 92°');
});

test('a day with a door: inside at both ends, out through the door, home by the door, and back in (citizens rule 6)', () => {
  const { world, layout } = buildCity();
  const site = [...world.doors.values()][0].building;
  const plan = planCitizen({ id: 'c1', seed: 12345, home: site }, {
    sitePoint: layout.sitePoint, route: layout.route, walkable: layout.walkable, hasSite: (s) => s.startsWith('agora') || layout.plots.has(s) || s === 'harbor',
    neighbours: [], doorFor: (s, seed) => world.doorFor(s, seed),
  });
  assert.ok(plan.door, '家有门');
  assert.ok(routeClear([plan.door.step, plan.home.point], layout.walkable, 0.1), '门前站的地方从门阶走得过去');
  if (plan.out) assert.ok(routeClear(plan.out, layout.walkable), '出门到去处一路都能站');
  const at = (p) => citizenAt(plan, ((p - plan.offset) % TOWN.periodMs + TOWN.periodMs) % TOWN.periodMs);
  const start = at(1000);
  assert.equal(start.at, 'inside');
  assert.equal(start.hidden, true, '周期开头在屋里，不画');
  const exiting = at(TOWN.insideMs + DOOR.openMs + DOOR.passMs / 2);
  assert.equal(exiting.at, 'door');
  assert.equal(exiting.door.kind, 'exit');
  assert.equal(exiting.door.open, 1, '跨出门时门开着');
  assert.equal(exiting.hidden, false);
  const home = at(TOWN.insideMs + DOOR.transitMs + 500);
  assert.equal(home.at, 'home');
  assert.deepEqual({ x: home.x, z: home.z }, { x: plan.home.point.x, z: plan.home.point.z }, '出门之后站在门前');
  const enterAt = TOWN.periodMs - TOWN.insideMs - DOOR.transitMs;
  const going = at(enterAt + DOOR.legMs + DOOR.passMs / 2);
  assert.equal(going.door.kind, 'enter');
  assert.equal(going.door.open, 1);
  const end = at(TOWN.periodMs - 100);
  assert.equal(end.at, 'inside');
  assert.equal(end.hidden, true, '周期末尾在屋里');
  // 出门那一刻接上门里，进门那一刻接上门前：没有一帧跳
  const near = (a, b, tol) => Math.hypot(a.x - b.x, a.z - b.z) <= tol;
  assert.ok(near(at(TOWN.insideMs + 1), plan.door.inside, 0.05), '出门从门里开始');
  assert.ok(near(at(TOWN.insideMs + DOOR.transitMs - 1), plan.home.point, 0.05), '出门走到门前站的地方');
  assert.ok(near(at(enterAt + 1), plan.home.point, 0.05), '进门从门前开始');
});
