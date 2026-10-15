// 人物关系在页面上的那一半（第三轮需求 1）：relations.mjs 的规则逐条断言。
import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  bandOf, readRelations, attitudeOf, bondOf, greetingStyle, canChat, chatPreference, relationsView, NEUTRAL, RELATION, BANDS,
  reactionOf, pickResponders, CHORUS, FACING_REACTIONS, regardOf,
} from '../../../../Assets/Polis/src/relations.mjs';
import { pickChat, TOWN } from '../../../../Assets/Polis/src/citizens.mjs';
import { UI_ZH, CITIZEN_ZH } from '../../../../Assets/Polis/src/game.mjs';

test('five bands with the same edges as C# (PolisRelations.BandOf)', () => {
  assert.equal(bandOf(60), 'close');
  assert.equal(bandOf(59), 'friendly');
  assert.equal(bandOf(25), 'friendly');
  assert.equal(bandOf(24), 'neutral');
  assert.equal(bandOf(-24), 'neutral');
  assert.equal(bandOf(-25), 'cool');
  assert.equal(bandOf(-59), 'cool');
  assert.equal(bandOf(-60), 'hostile');
  assert.deepEqual(RELATION, { close: 60, friendly: 25, coolBelow: -25, hostile: -60 });
  assert.deepEqual(BANDS, ['hostile', 'cool', 'neutral', 'friendly', 'close']);
});

test('the payload is read defensively: both directions of a pair, unknown people are neutral, junk dropped', () => {
  const rel = readRelations({
    you: { a: { score: 70, band: 'close', patience: 5, opens: true, comesOut: true, why: ['done', 'chats', 'extra'], talk: 0.9 },
      b: { score: -400, band: 'nonsense', patience: 99 }, c: 'not an object' },
    pairs: [['a', 'b', 40, -70], ['bad'], [1, 2, 3, 4]],
    ties: { a: { friends: [['b', 40], 'junk'], rivals: [] } },
  });
  assert.equal(attitudeOf(rel, 'a').band, 'close');
  assert.deepEqual(attitudeOf(rel, 'a').why, ['done', 'chats'], '最多两条原因');
  assert.equal(attitudeOf(rel, 'b').score, -100, '分数夹在 −100…100');
  assert.equal(attitudeOf(rel, 'b').band, 'hostile', '不认识的档按分数算');
  assert.equal(attitudeOf(rel, 'b').patience, 5, '耐心夹在 1…5');
  assert.equal(attitudeOf(rel, 'c'), NEUTRAL, '不是对象的丢掉');
  assert.equal(attitudeOf(rel, 'nobody'), NEUTRAL, '不认识的人按寻常');
  assert.equal(bondOf(rel, 'a', 'b'), 40);
  assert.equal(bondOf(rel, 'b', 'a'), -70, '有方向');
  assert.equal(bondOf(rel, 'a', 'zzz'), 0);
  assert.deepEqual(rel.ties.get('a').friends, [{ id: 'b', score: 40 }]);
  assert.equal(readRelations(null).you.size, 0, '没有就是空的');
});

test('greetings follow how they see you: close and friendly wave, neutral nods, cool glance, hostile turn away', () => {
  assert.deepEqual(greetingStyle('close'), { greets: true, wave: true, line: 'warm', turnAway: false, glance: false });
  assert.deepEqual(greetingStyle('friendly'), { greets: true, wave: true, line: 'plain', turnAway: false, glance: false });
  assert.deepEqual(greetingStyle('neutral'), { greets: true, wave: false, line: 'plain', turnAway: false, glance: false });
  assert.deepEqual(greetingStyle('cool'), { greets: false, wave: false, line: null, turnAway: false, glance: true });
  assert.deepEqual(greetingStyle('hostile'), { greets: false, wave: false, line: null, turnAway: true, glance: false });
  assert.ok(CITIZEN_ZH.greetWarm.split('|').length >= 3, '热络的话有几种说法');
});

test('small talk: a mutually hostile pair never meets, and a friendlier pair beats a slightly closer one', () => {
  const rel = readRelations({ pairs: [['a', 'b', -80, -70], ['c', 'd', 60, 55], ['e', 'f', 0, 0]] });
  assert.equal(canChat(rel, 'a', 'b'), false, '互相厌恶');
  assert.equal(canChat(rel, 'c', 'd'), true);
  const oneSided = readRelations({ pairs: [['a', 'b', -80, 10]] });
  assert.equal(canChat(oneSided, 'a', 'b'), true, '只有一头厌恶：还能碰上（话里带刺）');
  assert.ok(chatPreference(rel, 'c', 'd', 2.0) < chatPreference(rel, 'e', 'f', 1.6), '合得来的那一对先（哪怕远 0.4 米）');
  const idle = TOWN.chatNeedMs + 1000;
  const candidates = [
    { id: 'a', x: 0, z: 0, idleLeft: idle, inView: true }, { id: 'b', x: 0.5, z: 0, idleLeft: idle, inView: true },
    { id: 'c', x: 10, z: 0, idleLeft: idle, inView: true }, { id: 'd', x: 11.8, z: 0, idleLeft: idle, inView: true },
    { id: 'e', x: 20, z: 0, idleLeft: idle, inView: true }, { id: 'f', x: 21.5, z: 0, idleLeft: idle, inView: true },
  ];
  const player = { x: 10, z: 3 };
  const plain = pickChat(candidates, player, new Map(), 0);
  assert.deepEqual([plain.a, plain.b], ['a', 'b'], '不看关系时挑最近的一对');
  const picked = pickChat(candidates, player, new Map(), 0, { allowed: (x, y) => canChat(rel, x, y), score: (x, y, d) => chatPreference(rel, x, y, d) });
  assert.deepEqual([picked.a, picked.b], ['c', 'd'], '互相厌恶的不凑，合得来的先');
});

test('the people overview: citizens by score, residents apart, reasons in words, ties only to people the city knows', () => {
  const rel = readRelations({
    you: { a: { score: 70, why: ['busy', 'chats'] }, b: { score: -30, why: ['annoyed'] }, c: { score: 5 }, 'resident:smith': { score: 12, why: ['nature'] } },
    ties: { a: { friends: [['c', 50], ['ghost', 90]], rivals: [['b', -40]] } },
    // 第四轮需求 8：每人怎么看雅典娜
    athena: { a: { score: 72, band: 'close', why: ['pious', 'age'] }, b: { score: -70, band: 'hostile', why: ['defiant'] } },
  });
  const people = [
    { id: 'a', name: '阿里斯通', kind: 'citizen', bust: null }, { id: 'b', name: '达芙妮', kind: 'citizen', indoors: true, bust: null },
    { id: 'c', name: '克里同', kind: 'citizen', bust: null }, { id: 'resident:smith', name: '铁匠赫菲斯提昂', kind: 'resident', bust: 'smith' },
  ];
  const view = relationsView(rel, people, UI_ZH);
  assert.deepEqual(view.citizens.map((r) => r.id), ['a', 'c', 'b'], '按分数从高到低');
  assert.deepEqual(view.residents.map((r) => r.id), ['resident:smith'], '职业居民另成一组');
  assert.equal(view.citizens[0].bandLabel, UI_ZH.bandClose);
  assert.equal(view.citizens[0].why, `因为：${UI_ZH.whyBusy}，${UI_ZH.whyChats}`);
  assert.equal(view.citizens[0].athena.label, `怎么看雅典娜：${UI_ZH.regardClose}`);
  assert.equal(view.citizens[0].athena.why, `因为：${UI_ZH.whyPious}，${UI_ZH.whyAge}`);
  assert.equal(view.citizens[2].athena.band, 'hostile', '不是人人都敬重她');
  assert.equal(view.citizens[1].athena.band, 'neutral', '没推来的按寻常');
  assert.equal(regardOf(rel, 'b').score, -70);
  assert.equal(view.citizens[2].why, `因为：${UI_ZH.whyAnnoyed}`);
  assert.equal(view.citizens[1].why, '', '没有原因就不写');
  assert.equal(view.citizens[2].indoors, true);
  assert.deepEqual(view.citizens[0].friends.map((f) => f.name), ['克里同'], '城里不认识的人（ghost）不列');
  assert.deepEqual(view.citizens[0].rivals.map((f) => f.name), ['达芙妮']);
  for (const key of ['relationsButton', 'relationsTitle', 'relationsToYou', 'relationsPick', 'relationsEmpty', 'relationsFriends', 'relationsRivals',
    'relationsNone', 'relationsWhy', 'relationsWhyTwo', 'relationsResidents', 'relationsIndoors', 'bandClose', 'bandFriendly', 'bandNeutral', 'bandCool', 'bandHostile',
    'whyWarm', 'whyPrickly', 'whyNature', 'whyChats', 'whyAnnoyed', 'whyBusy', 'whyNeglected', 'whyIdle', 'whyPious', 'whyDoubting', 'whyReverent', 'whyDefiant',
    'whyTrade', 'whyAge', 'relationsAthena', 'regardClose', 'regardFriendly', 'regardNeutral', 'regardCool', 'regardHostile']) {
    assert.ok(typeof UI_ZH[key] === 'string' && UI_ZH[key].length > 0, `UI_ZH.${key}`);
  }
});

test('several people react when you speak to nobody in particular (third round, need 6)', () => {
  assert.equal(reactionOf('close'), 'wave');
  assert.equal(reactionOf('friendly'), 'nod');
  assert.equal(reactionOf('neutral'), 'listen');
  assert.equal(reactionOf('cool'), 'glance', '冷淡的只瞥一眼');
  assert.equal(reactionOf('hostile'), 'turnAway', '厌恶的背过身去');
  assert.equal(reactionOf('cool', true), 'listen', '要开口的人转过来');
  assert.deepEqual(FACING_REACTIONS, ['wave', 'nod', 'listen']);
  assert.equal(CHORUS.speakers, 3, '最多三位开口（C# 的 PolisTalk.MaxChorusSpeakers）');
  assert.ok(CHORUS.reactors >= CHORUS.speakers);
  const rel = readRelations({ you: {
    near: { score: 0, talk: 0.5 }, fond: { score: 70, talk: 0.5 }, hater: { score: -80, talk: 1 }, chatty: { score: 10, talk: 1 }, quiet: { score: 10, talk: 0 },
  } });
  const hearers = [{ id: 'near', d: 1.0 }, { id: 'fond', d: 4.0 }, { id: 'hater', d: 0.5 }, { id: 'chatty', d: 3.0 }, { id: 'quiet', d: 3.0 }];
  const order = pickResponders(hearers, rel);
  assert.equal(order.length, 3, '最多三位');
  assert.ok(!order.includes('hater'), '厌恶你的不开口，哪怕离得最近');
  assert.equal(order[0], 'fond', '喜欢你的先开口（远一点也是）');
  assert.ok(order.indexOf('chatty') < (order.includes('quiet') ? order.indexOf('quiet') : 99), '同样远近、同样关系，健谈的先');
  assert.deepEqual(pickResponders([{ id: 'hater', d: 1 }], rel), [], '只有厌恶你的人：没人开口');
  assert.deepEqual(pickResponders([], rel), []);
  assert.deepEqual(pickResponders(hearers, rel, { max: 1 }), ['fond']);
});
