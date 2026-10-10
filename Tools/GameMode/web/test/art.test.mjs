// M2 的纯逻辑：人物姿势、资产清单的缺省回退、半身像、港前场馆与橄榄林的布局、群岛海图、新意图。
// 运行：node --test Tools/GameMode/web/test/
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { ACTIONS, WORK_VARIANTS, JOINT_NAMES, poseAt, blendPoses, restPose, walkPeriod, LIMITS } from '../../../../Assets/Polis/src/art/poses.mjs';
import { planOverrides, resolveSource, clipFor, modelUrl, isAssetId, MODEL_ROUTE } from '../../../../Assets/Polis/src/art/asset-manifest.mjs';
import { bust, toSvg, CHARACTERS, EXPRESSIONS, BUST_VIEWBOX } from '../../../../Assets/Polis/src/art/busts.mjs';
import {
  venueSlotLocal, venueStandLocal, groveSpot, groveTerraces, islandLayout, chartTransform, hitIsland,
  SLOTS_PER_VENUE, TREES_PER_TERRACE, PLOT_SIZE, TERRACE,
} from '../../../../Assets/Polis/src/civic-layout.mjs';
import { intent, INTENT_TYPES } from '../../../../Assets/Polis/src/live.mjs';

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

test('poses: Athena glides — no stepping legs, robe trailing back', () => {
  for (let t = 0; t < 3000; t += 100) {
    const p = poseAt('walk', t, { glide: true });
    assert.equal(p.legL.rx, 0); assert.equal(p.legR.rx, 0);
    assert.ok(p.skirt.rx > 0, 'the hem trails behind her');
    assert.ok(p.spine.rx > 0, 'she leans into the motion');
  }
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
  const strike = [0, 300, 495, 612].map((t) => poseAt('work', t, { variant: 'hammer' }).shoulderR.rx);
  assert.ok(strike[2] < -2.4 && strike[3] > -0.6, 'the hammer goes up over the head, then comes down fast');
});

test('poses: blending is exact at the ends and switches the held tool halfway', () => {
  const a = poseAt('idle', 1234), b = poseAt('work', 400, { variant: 'write' });
  const at0 = blendPoses(a, b, 0), at1 = blendPoses(a, b, 1), mid = blendPoses(a, b, 0.5);
  for (const j of JOINT_NAMES) for (const k of Object.keys(a[j])) {
    assert.ok(near(at0[j][k], a[j][k]) && near(at1[j][k], b[j][k]), `${j}.${k}`);
  }
  assert.equal(at0.propL, null); assert.equal(mid.propL, 'tablet');
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
