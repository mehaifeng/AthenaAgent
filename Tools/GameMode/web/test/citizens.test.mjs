// 居民即会话（M-B，2026-10-13）：长相（art/citizens.mjs）与城里的日子（citizens.mjs）。
// 运行：node --test Tools/GameMode/web/test/
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import * as THREE from '../../../../Assets/Polis/vendor/three.bundle.mjs';
import { citizenLook, isFemale, lookSignature, CITIZEN_VOCAB, RESIDENT_SIGNATURES } from '../../../../Assets/Polis/src/art/citizens.mjs';
import { buildCitizen, CHARACTER_SPECS } from '../../../../Assets/Polis/src/art/characters.mjs';
import { HAIR_STYLES } from '../../../../Assets/Polis/src/art/rig.mjs';
import { bust, CHARACTERS } from '../../../../Assets/Polis/src/art/busts.mjs';
import {
  TOWN, DESTINATIONS, planCitizen, routeClear, citizenAt, newScheduleClock, scheduleClock, holdSchedule, resumeSchedule, separate, assignTiers, pickChat, nextChatAfter, greetLine,
} from '../../../../Assets/Polis/src/citizens.mjs';
import { createLayout } from '../../../../Assets/Polis/src/layout.mjs';
import { createAssetLibrary } from '../../../../Assets/Polis/src/art/assets.mjs';
import { buildWorld } from '../../../../Assets/Polis/src/world.mjs';
import { demoAccumulation, demoCitizens } from '../../../../Assets/Polis/src/demo.mjs';
import { CITIZEN_ZH, UI_ZH, citizenInfoLines, transcriptMarkdown, speechAlpha, thinkingDots, SPEECH } from '../../../../Assets/Polis/src/game.mjs';
import { parseMarkdown } from '../../../../Assets/Polis/src/reader.mjs';

const fixture = JSON.parse(readFileSync(new URL('../fixtures/synthetic.json', import.meta.url), 'utf8'));
/**
 * 测试用的种子：C# 给的是会话 Id 的 SHA-256 前四个字节，在 32 位上是均匀的；这里用 murmur3 的 fmix32 把 i 打散成同样均匀的数。
 * （第一版用的 imul(i + 1, …) ^ (i * 40503) 最低位恒为 1——四百位居民全是女子，多样性只有一半的词汇可用。）
 */
const seedOf = (i) => { let h = (i + 1) >>> 0; h ^= h >>> 16; h = Math.imul(h, 0x85ebca6b); h ^= h >>> 13; h = Math.imul(h, 0xc2b2ae35); h ^= h >>> 16; return h >>> 0; };

function stubMaterials() {
  const cache = new Map();
  return new Proxy({}, {
    get: (_, key) => {
      if (typeof key !== 'string') return undefined;
      if (!cache.has(key)) cache.set(key, key.endsWith('Shades') ? [0, 1, 2, 3, 4].map((i) => new THREE.MeshStandardMaterial({ name: `${key}${i}` })) : new THREE.MeshStandardMaterial({ name: key }));
      return cache.get(key);
    },
  });
}

test('look: the sex is the seed\'s lowest bit, the same seed is always the same person, and nothing outside the citizens\' vocabulary is used', () => {
  for (let i = 0; i < 200; i++) {
    const seed = seedOf(i) >>> 0;
    const age = ['young', 'adult', 'old'][i % 3];
    const a = citizenLook(seed, { age }), b = citizenLook(seed, { age });
    assert.deepEqual(a, b, '同一个种子永远同一个人');
    const { spec, bust: bs } = a;
    assert.equal(spec.face.female, (seed & 1) === 1, `${seed}: 最低位是 1 的是女子（和 C# 的 PolisCitizens.IsFemale 一致）`);
    assert.equal(isFemale(seed), spec.face.female);
    const vocab = spec.face.female ? CITIZEN_VOCAB.female : CITIZEN_VOCAB.male;
    assert.ok(vocab.garments.includes(spec.garment), `${seed}: 衣式 ${spec.garment}`);
    assert.ok(CITIZEN_VOCAB.cloth.includes(spec.cloth));
    assert.ok([null, undefined, ...vocab.headgear].includes(spec.headgear), `${seed}: 头饰 ${spec.headgear}`);
    assert.ok(spec.hair in HAIR_STYLES && spec.hair.startsWith('plain'), `${seed}: 发式 ${spec.hair} 是居民的通用发式`);
    if (spec.face.female) assert.ok(!spec.beard, '女子没有胡子');
    // 规则 2、3：十一位居民的招牌一样都不用，手里只有一卷素卷轴或空手
    assert.ok(!RESIDENT_SIGNATURES.headgear.includes(spec.headgear), `${seed}: 不戴居民的帽子 / 头冠 / 面纱`);
    assert.ok(!RESIDENT_SIGNATURES.garments.includes(spec.garment), `${seed}: 不穿传令官的斗篷`);
    for (const p of spec.props) assert.ok(p === 'scroll' && !RESIDENT_SIGNATURES.props.includes(p), `${seed}: 手里只有素卷轴：${p}`);
    assert.ok(!spec.glow && !spec.gold && !spec.accentGift, '没有雅典娜的光、侍女的金、红色的成果');
    // 半身像的参数是 busts.mjs 认得的那几种
    assert.ok(['short', 'curly', 'receding', 'long', 'bun'].includes(bs.hair), `${seed}: 半身像发式 ${bs.hair}`);
    assert.equal(bs.female, spec.face.female);
    assert.equal(bs.age, age);
  }
});

test('look: any 32 consecutive seeds give at least 24 different looks — nobody in a city shares a face', () => {
  const sigs = Array.from({ length: 400 }, (_, i) => lookSignature(citizenLook(seedOf(i) >>> 0)));
  let worst = Infinity;
  for (let i = 0; i + 32 <= sigs.length; i++) worst = Math.min(worst, new Set(sigs.slice(i, i + 32)).size);
  assert.ok(worst >= 24, `最差的一段 32 位里有 ${worst} 种长相`);
});

test('look: a citizen builds on the real rig and draws as a bust, and residents keep their own hairstyles', () => {
  const M = stubMaterials();
  for (let i = 0; i < 12; i++) {
    const look = citizenLook(seedOf(i) >>> 0, { age: ['young', 'adult', 'old'][i % 3] });
    const figure = buildCitizen(M, look.spec);
    assert.ok(figure.root.userData.headTop, '人物有头顶的锚点（头顶的话按它投影）');
    figure.pose('walk', 200, { clock: 1000, cloth: false });
    assert.equal(figure.clothFrozen, true, 'cloth: false 时布料停着（远处的居民）');
    const hidden = figure.setDetail(false);
    assert.ok(hidden >= 20, `远处藏起手指和脚趾：${hidden} 个网格`);
    assert.equal(figure.setDetail(true), 0);
    figure.pose('walk', 216, { clock: 1016 });
    assert.equal(figure.clothFrozen, false, '走近了布料接着推进');
    const b = bust(look.bust, 'speak');
    assert.ok(b.paths.length > 20, '半身像画得出来');
  }
  for (const id of Object.keys(CHARACTER_SPECS)) assert.ok(!CHARACTER_SPECS[id].hair.startsWith('plain'), `${id} 用自己的发式`);
  assert.ok(Object.keys(CHARACTERS).every((id) => !id.startsWith('citizen')));
});

test('look: a citizen first posed far from where it was built wears its skirt on its own hips — the cloth is resettled, not dragged in from the origin', () => {
  const M = stubMaterials();
  let worst = 0;
  // 挪一两米最糟：挪得很远时布料自己按"瞬移"重新安放（cloth.mjs），挪一点点却会被一帧拽过来。旧规则下这一排里
  // 四位的裙摆甩到 0.6–1.1 米开外（审查图里看到的），新规则下都在 0.4 米以内
  for (let i = 0; i < 10; i++) {
    const look = citizenLook(((Math.imul(i + 1, 2654435761) & ~1) | (i & 1)) >>> 0, { age: ['young', 'adult', 'old'][i % 3] });
    const figure = buildCitizen(M, look.spec);
    if (figure.garments.length === 0) continue;
    // 和城里一样：建好之后才挪到他在城里的位置、转向，然后头一回摆姿势（全套，布料推进）
    figure.root.position.set((i - 4.5) * 0.95, 0, 0);
    figure.root.rotation.y = 0.35;
    figure.pose('idle', 1200 + i * 777, { clock: 1_000_000 + i * 777 });
    figure.root.updateMatrixWorld(true);
    const v = new THREE.Vector3();
    for (const g of figure.garments) {
      const attr = g.mesh.geometry.attributes.position;
      for (let k = 0; k < attr.count; k++) {
        v.fromBufferAttribute(attr, k).applyMatrix4(g.mesh.matrixWorld);
        worst = Math.max(worst, Math.hypot(v.x - figure.root.position.x, v.z - figure.root.position.z));
      }
    }
  }
  assert.ok(worst < 0.5, `裙摆离身体最远 ${worst.toFixed(2)} 米（甩出去的裙摆到一米开外）`);
});

function buildCity() {
  const M = stubMaterials();
  const assets = createAssetLibrary(M);
  const acc = demoAccumulation(fixture);
  const layout = createLayout({ ...fixture, groveTrees: acc.trees.length });
  const world = buildWorld(new THREE.Scene(), fixture, layout, M, { assets });
  world.setGrove(acc.trees);
  world.setResidents(acc.residents, (k) => k);
  const hasSite = (s) => s === 'agora' || s.startsWith('agora:') || s === 'harbor' || s === 'gate' || layout.plots.has(s);
  return { world, layout, hasSite };
}

test('day: every citizen of the synthetic city stands and walks only where you can stand (the whole route, not just its ends), never walks farther than a 30 s leg, and fewer than half are on the road at once', () => {
  const { layout, hasSite } = buildCity();
  const { street } = demoCitizens(fixture, 32);
  const neighbours = [...layout.plots.keys()].filter((k) => k.startsWith('b:'));
  const plans = street.map((c) => planCitizen(c, { sitePoint: layout.sitePoint, route: layout.route, walkable: layout.walkable, hasSite, neighbours }));
  assert.ok(plans.filter((p) => p.dest).length >= 24, `大多数居民有一处去处：${plans.filter((p) => p.dest).length}/32`);
  for (const p of plans) {
    assert.ok(layout.walkable(p.home.point), `${p.id} 的家门口能站`);
    if (p.dest) {
      assert.ok(DESTINATIONS.includes(p.dest.site) || neighbours.includes(p.dest.site), p.dest.site);
      assert.notEqual(p.dest.site, p.home.site, '不去自己家');
      assert.ok(layout.walkable(p.dest.point), `${p.id} 的去处能站`);
      assert.ok(p.walkMs <= TOWN.maxWalkMs, `${p.id} 一程 ${Math.round(p.walkMs)} 毫秒`);
      assert.ok(routeClear(p.out, layout.walkable, 0.1), `${p.id} 去 ${p.dest.site} 的整条路每 0.1 米都能站`);
    }
  }
  let worstRoad = 0, blocked = 0, samples = 0;
  for (let wall = 0; wall < TOWN.periodMs * 2; wall += 997) {
    let road = 0;
    for (const p of plans) {
      const s = citizenAt(p, wall);
      samples++;
      if (!layout.walkable(s)) blocked++;
      if (s.action === 'walk') road++;
    }
    worstRoad = Math.max(worstRoad, road);
  }
  assert.equal(blocked, 0, `每一刻都站在能站的地方（${samples} 个取样里有 ${blocked} 个不能站）`);
  assert.ok(worstRoad <= Math.ceil(plans.length / 2), `同一刻走着的最多 ${worstRoad} 位（不超过一半）`);
  // 同一刻永远是同一个位置；日程与墙钟对齐、按种子错开
  assert.deepEqual(citizenAt(plans[0], 12345), citizenAt(plans[0], 12345));
  assert.ok(new Set(plans.map((p) => p.offset)).size > 24, '节拍按种子错开');
});

test('held (2026-10-13): a walker stopped for a talk resumes from where he stopped — the time he stood is taken off his own schedule, so he never races to where the wall clock says he should be', () => {
  const { layout, hasSite } = buildCity();
  const { street } = demoCitizens(fixture, 32);
  const neighbours = [...layout.plots.keys()].filter((k) => k.startsWith('b:'));
  const plan = street.map((c) => planCitizen(c, { sitePoint: layout.sitePoint, route: layout.route, walkable: layout.walkable, hasSite, neighbours })).find((p) => p.dest);
  // 找一刻他正走在去处的半路上
  let wall = 0;
  while (citizenAt(plan, wall).action !== 'walk' || citizenAt(plan, wall + 3000).action !== 'walk') wall += 250;
  const clock = newScheduleClock();
  assert.equal(scheduleClock(clock, wall), wall, '没站定过：日程钟就是墙钟');
  const before = citizenAt(plan, scheduleClock(clock, wall));
  holdSchedule(clock, wall);
  holdSchedule(clock, wall + 5000);   // 已经停着：不重新计时
  const heldFor = 20_000;
  assert.deepEqual(citizenAt(plan, scheduleClock(clock, wall + heldFor)), before, '站定期间日程不走');
  resumeSchedule(clock, wall + heldFor);
  const after = citizenAt(plan, scheduleClock(clock, wall + heldFor));
  assert.ok(Math.hypot(after.x - before.x, after.z - before.z) < 1e-9, '松开的那一刻还在站定的地方');
  // 松开之后一帧（16 ms）挪的距离是走路的速度，不是去追墙钟（旧做法：墙钟上的位置在 20 秒后，差出十几米）
  const next = citizenAt(plan, scheduleClock(clock, wall + heldFor + 16));
  assert.ok(Math.hypot(next.x - after.x, next.z - after.z) <= TOWN.speed * 0.016 * 1.6 + 1e-6, '松开后按走路的速度接着走');
  const wallClockSpot = citizenAt(plan, wall + heldFor);
  assert.ok(Math.hypot(wallClockSpot.x - before.x, wallClockSpot.z - before.z) > 2, `这个用例真的测到了差别：墙钟上的位置离站定处 ${Math.hypot(wallClockSpot.x - before.x, wallClockSpot.z - before.z).toFixed(1)} 米`);
  resumeSchedule(clock, wall + heldFor + 1000);   // 没站定：不动
  assert.equal(clock.shift, heldFor);
});

test('households (M-D): a household\'s house carries the household\'s name, not its key, and a citizen whose home is that house is planned from it', () => {
  // 一块没人用的地：在合成城邦的第一、二圈里找（不是文件夹的、不是公共建筑的、不是空地、不在海里）
  const used = new Set([...fixture.buildings.map((b) => `${b.plot.x},${b.plot.z}`), ...Object.values(fixture.publicSites).map((p) => `${p.x},${p.z}`), ...(fixture.vacant ?? []).map((v) => `${v.plot.x},${v.plot.z}`)]);
  let plot = null;
  for (let r = 1; r <= 3 && !plot; r++) {
    for (let x = -r; x <= r && !plot; x++) {
      for (let z = -r; z <= r && !plot; z++) {
        if (Math.max(Math.abs(x), Math.abs(z)) !== r || z >= fixture.seaStartsAtZ || used.has(`${x},${z}`)) continue;
        plot = { x, z };
      }
    }
  }
  assert.ok(plot, '找得到一块空地');
  const house = { key: 'household:abcd1234', label: '整理合同一家', kind: 'house', files: 3, bytes: 0, kinds: {}, lastModified: '2026-10-13T00:00:00Z', state: 'lived', sizeClass: 2, incomplete: false, plot, order: 999 };
  const city = { ...fixture, buildings: [...fixture.buildings, house] };
  const M = stubMaterials();
  const assets = createAssetLibrary(M);
  const acc = demoAccumulation(city);
  const layout = createLayout({ ...city, groveTrees: acc.trees.length });
  const world = buildWorld(new THREE.Scene(), city, layout, M, { assets });
  const label = world.labels.find((l) => l.key === 'b:household:abcd1234');
  assert.ok(label, '民居有名牌');
  assert.equal(label.text, '整理合同一家', '名牌写户名，不写钥匙');
  assert.ok(world.labels.filter((l) => l.kind === 'building').every((l) => !l.text.startsWith('household:')), '没有一块名牌露出钥匙');
  assert.ok(layout.plots.has('b:household:abcd1234'), '民居是一块地，站点 b:household:…');
  const hasSite = (site) => site === 'agora' || site.startsWith('agora:') || site === 'harbor' || site === 'gate' || layout.plots.has(site);
  const plan = planCitizen({ id: 'conv-x', seed: 12345, home: 'b:household:abcd1234' }, { sitePoint: layout.sitePoint, route: layout.route, walkable: layout.walkable, hasSite, neighbours: [] });
  assert.equal(plan.home.site, 'b:household:abcd1234', '住在这一户的人从民居门前出门');
  assert.ok(layout.walkable(plan.home.point), '民居门前能站');
  // 民居还没盖起来（城还没推到）时只能先住广场：日程记下的家与他真正的家不同——城一变，页面据此重排（game.replanCitizens）
  const early = planCitizen({ id: 'conv-x', seed: 12345, home: 'b:household:ffff0000' }, { sitePoint: layout.sitePoint, route: layout.route, walkable: layout.walkable, hasSite, neighbours: [] });
  assert.notEqual(early.home.site, 'b:household:ffff0000', '还没有那座民居：先住广场');
});

test('avoidance: overlapping people step apart, nobody is pushed off walkable ground, you are the stone they step around, and the nudge fades when no one is near', () => {
  const walkable = (p) => p.x > -10 && p.x < 10 && p.z > -10 && p.z < 10 && !(p.x > 2 && p.x < 3);
  let people = [
    { id: 'a', x: 0, z: 0, nudge: { x: 0, z: 0 }, movable: true },
    { id: 'b', x: 0.2, z: 0, nudge: { x: 0, z: 0 }, movable: true },
    { id: 'r', x: 0, z: 0.3, nudge: { x: 0, z: 0 }, movable: false },
  ];
  for (let k = 0; k < 40; k++) {
    const n = separate(people, null, walkable);
    people = people.map((p) => (p.movable ? { ...p, nudge: n.get(p.id) } : p));
  }
  const pos = (p) => ({ x: p.x + p.nudge.x, z: p.z + p.nudge.z });
  const [a, b, r] = people.map(pos);
  assert.ok(Math.hypot(a.x - b.x, a.z - b.z) >= TOWN.separation - 0.05, `两人让开：${Math.hypot(a.x - b.x, a.z - b.z).toFixed(2)} 米`);
  assert.deepEqual(people[2].nudge, { x: 0, z: 0 }, '站着不动的人（雅典娜、十一位居民）不被推');
  assert.ok(Math.hypot(a.x - r.x, a.z - r.z) > 0.3);
  // 墙边：往墙里推的那一步不走
  const atWall = [{ id: 'w', x: 1.95, z: 0, nudge: { x: 0, z: 0 }, movable: true }, { id: 'v', x: 1.7, z: 0, nudge: { x: 0, z: 0 }, movable: true }];
  for (let k = 0; k < 30; k++) {
    const n = separate(atWall, null, walkable);
    for (const p of atWall) p.nudge = n.get(p.id);
    for (const p of atWall) assert.ok(walkable(pos(p)), '让开的地方能站');
  }
  // 你：推不动，挡在你身边的人让开
  const crowd = [{ id: 'c', x: 0.1, z: 0, nudge: { x: 0, z: 0 }, movable: true }];
  for (let k = 0; k < 40; k++) crowd[0].nudge = separate(crowd, { x: 0, z: 0 }, walkable).get('c');
  assert.ok(Math.hypot(pos(crowd[0]).x, pos(crowd[0]).z) >= TOWN.playerRadius - 0.05, '你身边让出一圈');
  // 没人挤了：偏移慢慢回到 0
  const alone = [{ id: 'd', x: 5, z: 5, nudge: { x: 0.5, z: 0 }, movable: true }];
  for (let k = 0; k < 120; k++) alone[0].nudge = separate(alone, null, walkable).get('d');
  assert.ok(Math.hypot(alone[0].nudge.x, alone[0].nudge.z) < 0.01, '没人挤就回到原路');
});

test('tiers: out of view is C, the nearest four within 6 m are A, the rest B, and an A at the edge does not flicker', () => {
  const list = [
    { id: 'p1', distance: 1, inView: true }, { id: 'p2', distance: 2, inView: true }, { id: 'p3', distance: 3, inView: true },
    { id: 'p4', distance: 4, inView: true }, { id: 'p5', distance: 5, inView: true }, { id: 'far', distance: 20, inView: true },
    { id: 'off', distance: 0.5, inView: false },
  ];
  const t = assignTiers(list);
  assert.deepEqual(['p1', 'p2', 'p3', 'p4'].map((id) => t.get(id)), ['A', 'A', 'A', 'A']);
  assert.equal(t.get('p5'), 'B');
  assert.equal(t.get('far'), 'B');
  assert.equal(t.get('off'), 'C', '画面外的不摆，哪怕就在你身边');
  const lone = assignTiers([{ id: 'x', distance: 9, inView: true }]);
  assert.equal(lone.get('x'), 'B', '离你远过 6 米的，就算只有他一个也不是全套');
  // 滞回：p4 稍远一点（4.6）、p5 稍近一点（4.5），p4 仍然留在 A
  const t2 = assignTiers([...list.slice(0, 3), { id: 'p4', distance: 4.6, inView: true }, { id: 'p5', distance: 4.5, inView: true }], t);
  assert.equal(t2.get('p4'), 'A');
  assert.equal(t2.get('p5'), 'B');
});

test('small talk (2026-10-13: model only, no preset lines): one pair at a time, idle people near you and each other, a cooldown per pair, and a back-off when the model is missing or busy', () => {
  const people = [
    { id: 'a', x: 0, z: 0, idleLeft: 30_000, inView: true },
    { id: 'b', x: 1.5, z: 0, idleLeft: 30_000, inView: true },
    { id: 'c', x: 1.6, z: 0.3, idleLeft: 1000, inView: true },           // 马上要动身：不挑
    { id: 'd', x: 40, z: 0, idleLeft: 30_000, inView: true },            // 离你太远
  ];
  const cooldown = new Map();
  const talk = pickChat(people, { x: 0, z: 2 }, cooldown, 10_000);
  assert.deepEqual(talk, { a: 'a', b: 'b', key: 'a|b', start: 10_000 }, '挑中离得最近、都闲着的一对；只挑人，没有台词');
  cooldown.set(talk.key, 10_000);
  assert.equal(pickChat(people, { x: 0, z: 2 }, cooldown, 10_000 + 60_000), null, '这一对三分钟里不再说');
  assert.ok(pickChat(people, { x: 0, z: 2 }, cooldown, 10_000 + TOWN.chatPairCooldownMs + 1), '过了三分钟又能说');
  assert.equal(pickChat(people.map((p) => ({ ...p, idleLeft: TOWN.chatNeedMs - 1 })), { x: 0, z: 2 }, new Map(), 0), null, '正要动身的人不挑');
  // 结束之后多久再挑：没配模型就不再挑（没有本地台词可退）；超限五分钟；正忙半分钟；失败一分钟；说完照常
  assert.equal(nextChatAfter('nomodel', 0), Number.POSITIVE_INFINITY);
  assert.equal(nextChatAfter('limited', 0), 5 * 60_000);
  assert.equal(nextChatAfter('busy', 0), 30_000);
  assert.equal(nextChatAfter('failed', 0), 60_000);
  assert.equal(nextChatAfter('ok', 0), 500);
  assert.ok(TOWN.chatMaxMs >= 2 * 25_000, '一对最多聊这么久，够两句各 25 秒的超时');
  // 打招呼仍是本地的
  assert.ok(CITIZEN_ZH.greet.split('|').includes(greetLine(42, CITIZEN_ZH.greet, 0)), '打招呼的话来自表里');
  assert.deepEqual(Object.keys(CITIZEN_ZH), ['greet'], '没有闲聊的本地台词了');
});

test('card (2026-10-13): the citizen\'s card shows that conversation\'s facts, and the preview is a reader document of only your and Athena\'s words', () => {
  const lines = citizenInfoLines({ title: '整理合同', outcome: 'completed', turns: 3, days: 5, folders: ['合同', '账本'] }, UI_ZH);
  assert.deepEqual(lines, ['委托：整理合同', '办完了 · 完成了 3 回合', '5 天前动过', '常去：合同、账本']);
  assert.deepEqual(citizenInfoLines({ title: '', outcome: 'running', turns: 0, days: 0, folders: [] }, UI_ZH), ['委托：（还没有标题）', '正在办 · 完成了 0 回合', '今天动过']);
  const md = transcriptMarkdown({ entries: [{ role: 'user', text: '第一问' }, { role: 'assistant', text: '**答**了' }], omitted: 4 }, UI_ZH);
  const blocks = parseMarkdown(md);
  assert.deepEqual(blocks.filter((b) => b.type === 'heading').map((b) => b.inline.map((c) => c.text).join('')), ['你', '雅典娜']);
  assert.ok(md.startsWith('*（更早的 4 条没有列出）*'), '告诉你前面还有几条');
  assert.ok(md.includes('第一问') && md.includes('**答**了'), '正文原样交给阅读器（Markdown 由阅读器解析、只经 textContent 进 DOM）');
  assert.equal(transcriptMarkdown({ entries: [] }, UI_ZH), UI_ZH.transcriptEmpty);
});

test('speech over a head (2026-10-13): thinking is a looping row of dots, a streaming or kept line stays, a finished one holds by its length then fades', () => {
  assert.deepEqual([0, SPEECH.dotMs, 2 * SPEECH.dotMs, 3 * SPEECH.dotMs].map(thinkingDots), ['·', '··', '···', '·'], '一、二、三个点，循环');
  assert.equal(speechAlpha({ text: '', typing: true }, 99_999), 1, '还在想：一直在');
  assert.equal(speechAlpha({ text: '半句', typing: false, doneAt: null }, 99_999), 1, '还在说（流式）：一直在');
  const done = { text: '风是不小。', typing: false, doneAt: 1000 };
  const hold = SPEECH.holdMs + SPEECH.perCharMs * 5;
  assert.equal(speechAlpha(done, 1000 + hold - SPEECH.fadeMs), 1, '说完之后按字数留一阵');
  assert.ok(speechAlpha(done, 1000 + hold - SPEECH.fadeMs / 2) < 1, '然后淡出');
  assert.equal(speechAlpha(done, 1000 + hold + 1), 0, '淡完了');
  assert.equal(speechAlpha(done, 1000 + hold + 60_000, true), 1, '你还在和他聊：他最近那一句一直留着');
  assert.ok(SPEECH.holdMs + SPEECH.perCharMs * 80 > SPEECH.holdMs + SPEECH.perCharMs * 5, '长的话留得久');
});
