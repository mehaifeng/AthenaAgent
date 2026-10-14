// 讲台前的辩论（M-E）：舞台站位、陪审团围过来、几个人为你挥手、雅典娜那一句的气泡摘要，以及对话栏借给辩论时的规矩。
// 状态机、提示词、裁判的判读在 C#（Athena.Archive.Tests 的 polis debate）。
// 运行：node --test Tools/GameMode/web/test/
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import * as THREE from '../../../../Assets/Polis/vendor/three.bundle.mjs';
import { debateStage, assignAudience, supportersCount, bubbleExcerpt, nearestStandable, DEBATE } from '../../../../Assets/Polis/src/debate.mjs';
import { lentView, debateLend, chatLend, MAX_MOTION_CHARS, MAX_ARGUMENT_CHARS } from '../../../../Assets/Polis/src/talk.mjs';
import { UI_ZH } from '../../../../Assets/Polis/src/game.mjs';
import { createLayout } from '../../../../Assets/Polis/src/layout.mjs';
import { createAssetLibrary } from '../../../../Assets/Polis/src/art/assets.mjs';
import { buildWorld } from '../../../../Assets/Polis/src/world.mjs';
import { demoAccumulation } from '../../../../Assets/Polis/src/demo.mjs';

const fixture = JSON.parse(readFileSync(new URL('../fixtures/synthetic.json', import.meta.url), 'utf8'));

function stubMaterials() {
  const cache = new Map();
  return new Proxy({}, {
    get: (_, key) => {
      if (typeof key !== 'string') return undefined;
      if (!cache.has(key)) cache.set(key, key.endsWith('Shades') ? [0, 1, 2, 3, 4].map(() => new THREE.MeshStandardMaterial()) : new THREE.MeshStandardMaterial());
      return cache.get(key);
    },
  });
}

function buildCity() {
  const M = stubMaterials();
  const assets = createAssetLibrary(M);
  const acc = demoAccumulation(fixture);
  const layout = createLayout({ ...fixture, groveTrees: acc.trees.length });
  const world = buildWorld(new THREE.Scene(), fixture, layout, M, { assets });
  world.setGrove(acc.trees);
  world.setResidents(acc.residents, (k) => k);
  const b = world.agora.userData.bema;
  const o = world.agora.position;
  return { world, layout, bema: { x: o.x + b.x, z: o.z + b.z }, center: { x: o.x, z: o.z } };
}

test('stage: on the real agora you, Athena and the platform\'s step are all standable, face each other, and the jury makes a half-ring of standable places facing the stage', () => {
  const { layout, bema, center } = buildCity();
  const st = debateStage({ bema, center, walkable: layout.walkable });
  for (const [name, p] of [['讲台前按 E 的那一点', st.interact], ['你的位置', st.you], ['她的位置', st.athena]]) {
    assert.ok(layout.walkable(p), `${name}能站：(${p.x.toFixed(2)}, ${p.z.toFixed(2)})`);
  }
  assert.ok(Math.hypot(st.you.x - st.athena.x, st.you.z - st.athena.z) > 1.5, '你和她隔开站');
  const facing = (from, yaw, to) => Math.cos(yaw - Math.atan2(to.x - from.x, to.z - from.z)) > 0.9;
  assert.ok(facing(st.you, st.you.yaw, st.athena) && facing(st.athena, st.athena.yaw, st.you), '面对面');
  assert.ok(st.audience.length >= 8, `陪审团至少八个站位：${st.audience.length}`);
  assert.ok(st.audience.length <= DEBATE.maxAudience);
  for (const a of st.audience) {
    assert.ok(layout.walkable(a), `站位能站：(${a.x.toFixed(2)}, ${a.z.toFixed(2)})`);
    assert.ok(facing(a, a.yaw, st.stage), '站位面朝舞台');
    // 在舞台中心之外、背对讲台的那一侧（不站到讲台后面去）
    const out = (a.x - st.stage.x) * st.front.x + (a.z - st.stage.z) * st.front.z;
    assert.ok(out > -1.5, `不站到讲台那一边：${out.toFixed(2)}`);
    for (const p of [st.you, st.athena]) assert.ok(Math.hypot(a.x - p.x, a.z - p.z) >= 0.7, '不挤在你和她身上');
  }
  // 第四轮需求 6（2026-10-15，用户：围观的人靠近一些）：两圈收到 2.5 / 3.3 米，挪到能站的地方也不超过外圈半米
  assert.deepEqual([...DEBATE.rings], [2.5, 3.3]);
  for (const a of st.audience) {
    const r = Math.hypot(a.x - st.stage.x, a.z - st.stage.z);
    assert.ok(r <= DEBATE.rings[1] + 0.5, `观众离舞台不远：${r.toFixed(2)} 米`);
  }
  for (let i = 0; i < st.audience.length; i++) {
    for (let j = i + 1; j < st.audience.length; j++) {
      assert.ok(Math.hypot(st.audience[i].x - st.audience[j].x, st.audience[i].z - st.audience[j].z) >= 0.7, '站位之间隔开 0.7 米');
    }
  }
});

test('jury: the nearest come first, each to the nearest free place by a straight walkable line within 9 s; nobody shares a place; too far or walled off stays home', () => {
  const walkable = (p) => Math.abs(p.x) < 20 && Math.abs(p.z) < 20 && !(p.x > 5 && p.x < 6 && p.z > -3 && p.z < 3);
  const spots = [{ x: 0, z: 2, yaw: Math.PI }, { x: 1, z: 2, yaw: Math.PI }, { x: -1, z: 2, yaw: Math.PI }];
  const candidates = [
    { id: 'near', x: 0, z: 5 },
    { id: 'mid', x: 2, z: 6 },
    { id: 'behind-wall', x: 8, z: 1 },   // 直线穿过 x ∈ (5, 6) 的墙
    { id: 'far', x: 0, z: 19 },          // 17 米 / 1.6 米每秒 > 9 秒
    { id: 'late', x: -2, z: 7 },
  ];
  const got = assignAudience(candidates, spots, walkable, { stage: { x: 0, z: 0 } });
  assert.ok(got.has('near') && got.get('near').index === 0, '最近的那位先挑、挑离他最近的空位');
  assert.ok(!got.has('behind-wall'), '走不过去（隔着墙）的不叫');
  assert.ok(!got.has('far'), '九秒走不到的不叫');
  assert.equal(new Set([...got.values()].map((a) => a.index)).size, got.size, '一个站位只站一个人');
  assert.equal(got.size, 3, '三个站位都站满');
  for (const a of got.values()) {
    assert.ok(a.ms > 0 && a.ms <= DEBATE.maxGatherMs);
    for (let k = 0; k <= 20; k++) assert.ok(walkable({ x: a.from.x + (a.to.x - a.from.x) * (k / 20), z: a.from.z + (a.to.z - a.from.z) * (k / 20) }), '一路都能站');
  }
  assert.equal(nearestStandable({ x: 5.5, z: 0 }, walkable)?.x <= 5 || nearestStandable({ x: 5.5, z: 0 }, walkable)?.x >= 6, true, '站在墙里的点往外挪');
});

test('votes: as many wave for you as the jury\'s share of votes, rounded; nobody when there is no audience', () => {
  assert.equal(supportersCount(10, 6), 6);
  assert.equal(supportersCount(7, 6), 4, '七人里 0.6 → 4.2 → 4');
  assert.equal(supportersCount(7, 5), 4, '平局按四舍五入：3.5 → 4');
  assert.equal(supportersCount(0, 8), 0);
  assert.equal(supportersCount(5, 0), 0);
  assert.equal(supportersCount(5, 10), 5);
  assert.equal(supportersCount(5, 42), 5, '票数越界按上限');
});

test('her bubble: the first sentence of her rebuttal, clipped; the whole of it is on the board', () => {
  assert.equal(bubbleExcerpt('周末不碰工作，工作会追上门来。而且……'), '周末不碰工作，工作会追上门来。…');
  assert.equal(bubbleExcerpt('短的。'), '短的。');
  const long = '这是一句非常非常长的话，长到气泡里放不下，所以只取前面的一截，后面用省略号代替，免得盖住半个广场';
  const ex = bubbleExcerpt(long);
  assert.ok(ex.length <= 37 && ex.endsWith('…'), ex);
  assert.equal(bubbleExcerpt('Rest matters. Work can wait.'), 'Rest matters.…');
  assert.equal(bubbleExcerpt('v1.2 is fine'), 'v1.2 is fine', '不在小数点上断句');
});

test('the bar lent to the debate: a motion of 1–200 characters sends debate-start, an argument of 1–600 sends debate-say, nothing while Athena thinks', () => {
  const ui = UI_ZH;
  const motion = debateLend({ stage: 'motion', id: 'motion' }, ui);
  assert.equal(lentView('lent', motion, '周末应该完全不碰工作').canSend, true);
  assert.equal(lentView('lent', motion, '  ').canSend, false, '空议题不能提');
  assert.equal(lentView('lent', motion, '题'.repeat(MAX_MOTION_CHARS)).canSend, true, '200 字正好');
  assert.equal(lentView('lent', motion, '题'.repeat(MAX_MOTION_CHARS + 1)).canSend, false, '201 字不能提（C# 也会整条拒绝）');
  assert.deepEqual(motion.intent('周末不碰工作'), ['debate-start', { motion: '周末不碰工作' }], '议题发 debate-start');
  assert.equal(motion.bust, 'athena', '栏上是她——她是你的对手');
  const argue = debateLend({ stage: 'argue', round: 2, rounds: 3, motion: '周末不碰工作', id: 'd1' }, ui);
  assert.ok(lentView('lent', argue, '').placeholder.includes('2') && lentView('lent', argue, '').placeholder.includes('3'), '占位字写着第几轮');
  assert.ok(lentView('lent', argue, '').status.includes('周末不碰工作'), '状态行写着议题');
  assert.equal(lentView('lent', argue, '论'.repeat(MAX_ARGUMENT_CHARS)).canSend, true, '600 字正好');
  assert.equal(lentView('lent', argue, '论'.repeat(MAX_ARGUMENT_CHARS + 1)).canSend, false);
  assert.deepEqual(argue.intent('休息让人更有效率'), ['debate-say', { text: '休息让人更有效率' }], '论点发 debate-say，不是 say');
  const waiting = lentView('waiting', argue, '');
  assert.equal(waiting.disabled, true, '她在想：输入框停用');
  assert.equal(waiting.canSend, false);
  assert.equal(waiting.status, ui.debateAthenaThinking);
  assert.notEqual(chatLend({ conversationId: 'c', name: 'x' }, ui).kind, argue.kind, '聊天与辩论是两种借法');
  for (const key of ['interactBema', 'bemaTitle', 'bemaBody', 'bemaRecord', 'bemaPropose', 'debateName', 'debateMotionStatus', 'debateMotionPlaceholder',
    'debateArguePlaceholder', 'debateArgueStatus', 'debateSend', 'debateHint', 'debateAthenaThinking', 'debateJudging', 'debateRound', 'debateYou',
    'debateAthena', 'debateVotes', 'debateYouWin', 'debateAthenaWins', 'debateTie', 'debateClose']) {
    assert.ok(typeof ui[key] === 'string' && ui[key].length > 0, `UI_ZH.${key}`);
  }
});

test('the dais (2026-10-13): with a dais the stage is its centre and you and Athena stand at its two ends facing each other, you at the end nearer the agora centre', async () => {
  const { debateStage, DEBATE } = await import('../../../../Assets/Polis/src/debate.mjs');
  const { AGORA_DAIS } = await import('../../../../Assets/Polis/src/art/civic.mjs');
  const st = debateStage({ bema: { x: -4.4, z: -2.05 }, center: { x: 0, z: 0 }, walkable: () => true, dais: { x: AGORA_DAIS.x, z: AGORA_DAIS.z, len: AGORA_DAIS.len } });
  assert.deepEqual(st.stage, { x: AGORA_DAIS.x, z: AGORA_DAIS.z });
  assert.equal(st.you.x, AGORA_DAIS.x + AGORA_DAIS.len / 2 - DEBATE.daisInset, '你在离广场中心近的那一端');
  assert.equal(st.athena.x, AGORA_DAIS.x - AGORA_DAIS.len / 2 + DEBATE.daisInset, '她在讲台那一端');
  assert.ok(Math.abs(st.you.yaw - Math.atan2(st.athena.x - st.you.x, 0)) < 1e-9 && Math.abs(st.athena.yaw - Math.atan2(st.you.x - st.athena.x, 0)) < 1e-9, '面对面');
  assert.ok(st.audience.every((a) => Math.hypot(a.x - st.stage.x, a.z - st.stage.z) > AGORA_DAIS.len / 2), '围观的人站在辩台外面');
  assert.ok(AGORA_DAIS.h < 0.35, '辩台低于挡路的身体那一段（是地面，不挡广场上的路）');
});
