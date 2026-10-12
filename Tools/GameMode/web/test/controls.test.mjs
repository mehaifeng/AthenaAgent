// 操作（2026-10-11 实测之后）：键盘走路相对镜头、撞墙沿墙滑，三种视角与拖动转镜头，对话栏能不能托付。纯逻辑。
// 运行：node --test Tools/GameMode/web/test/
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import {
  VIEW, VIEW_MODES, MOVE_KEYS, nextView, moveDirection, slide, orbitAngles, deadZoneShift, turnToward, isTypingTarget,
} from '../../../../Assets/Polis/src/controls.mjs';
import { talkView, MAX_SAY_CHARS } from '../../../../Assets/Polis/src/talk.mjs';
import { createLayout, BLOCK } from '../../../../Assets/Polis/src/layout.mjs';
import { UI_ZH } from '../../../../Assets/Polis/src/game.mjs';

const fixture = JSON.parse(readFileSync(new URL('../fixtures/synthetic.json', import.meta.url), 'utf8'));
const near = (a, b, msg) => assert.ok(Math.abs(a - b) < 1e-9, `${msg}：${a} ≠ ${b}`);

test('keys: WASD and the arrows, by physical position', () => {
  assert.deepEqual(new Set(Object.values(MOVE_KEYS)), new Set(['up', 'down', 'left', 'right']));
  for (const [code, dir] of [['KeyW', 'up'], ['ArrowUp', 'up'], ['KeyA', 'left'], ['ArrowLeft', 'left'], ['KeyS', 'down'], ['ArrowDown', 'down'], ['KeyD', 'right'], ['ArrowRight', 'right']]) {
    assert.equal(MOVE_KEYS[code], dir, code);
  }
});

test('walking is relative to the camera: W goes up the screen, D goes right', () => {
  // 镜头在正南（方位角 0）朝北看：W 往北（-z），D 往东（+x）
  let d = moveDirection(new Set(['up']), 0);
  near(d.x, 0, 'W 的 x'); near(d.z, -1, 'W 的 z');
  d = moveDirection(new Set(['right']), 0);
  near(d.x, 1, 'D 的 x'); near(d.z, 0, 'D 的 z');
  // 缺省镜头在东南（45°）：W 朝西北、D 朝东北——画面上方与画面右边
  d = moveDirection(new Set(['up']), Math.PI / 4);
  near(d.x, -Math.SQRT1_2, '45° 时 W 的 x'); near(d.z, -Math.SQRT1_2, '45° 时 W 的 z');
  d = moveDirection(new Set(['right']), Math.PI / 4);
  near(d.x, Math.SQRT1_2, '45° 时 D 的 x'); near(d.z, -Math.SQRT1_2, '45° 时 D 的 z');
  // 斜着走不比直着走快
  d = moveDirection(new Set(['up', 'right']), 0.7);
  near(Math.hypot(d.x, d.z), 1, '斜向是单位长度');
  assert.equal(moveDirection(new Set(), 0), null, '没按就不走');
  assert.equal(moveDirection(new Set(['up', 'down']), 0), null, '相反的两个键互相抵消');
});

test('sliding: blocked steps slide along the wall; standing somewhere unwalkable lets you walk out', () => {
  const wall = (p) => p.x < 10;   // x ≥ 10 是墙
  assert.deepEqual(slide({ x: 9.5, z: 0 }, { x: 1, z: 1 }, wall), { x: 9.5, z: 1 }, '斜着撞墙：沿墙滑');
  const approach = slide({ x: 9.5, z: 0 }, { x: 1, z: 0 }, wall);
  assert.ok(approach.x < 10 && approach.x >= 9.5 && approach.z === 0, `正面撞墙：只会贴近，不横移（${approach.x}, ${approach.z}）`);
  assert.deepEqual(slide({ x: 9.99, z: 0 }, { x: 1, z: 0 }, wall), { x: 9.99, z: 0 }, '贴着墙正面按：不动');
  assert.deepEqual(slide({ x: 5, z: 0 }, { x: 1, z: 0 }, wall), { x: 6, z: 0 }, '没撞上：整步走');
  assert.deepEqual(slide({ x: 12, z: 0 }, { x: 1, z: 0 }, wall), { x: 13, z: 0 }, '站在墙里（城变了）：放行');
  // M3：斜着撞上一根圆柱（半径 0.6），只走 x、只走 z 都进柱子，就偏着滑过去；正对着柱心走则不动
  const post = (p) => Math.hypot(p.x, p.z) > 0.6;
  const start = { x: -0.5, z: -0.5 };
  const slid = slide(start, { x: 0.3, z: 0.3 }, post);
  assert.ok(post(slid) && Math.hypot(slid.x - start.x, slid.z - start.z) > 0.1, `偏着滑过去了（${slid.x.toFixed(2)}, ${slid.z.toFixed(2)}）`);
  assert.ok(Math.hypot(slid.x - start.x, slid.z - start.z) <= Math.hypot(0.3, 0.3) + 1e-9, '偏着走的一步不比原来的长');
  let p = { x: -1.5, z: 0 };
  for (let i = 0; i < 40; i++) p = slide(p, { x: 0.1, z: 0 }, post);
  assert.ok(Math.abs(p.z) < 0.15 && Math.hypot(p.x, p.z) <= 0.62 && p.x < 0, `正对着柱心按住：贴到柱子上，不绕着它蹭出去（${p.x.toFixed(2)}, ${p.z.toFixed(2)}）`);
});

test('sliding on a real city: holding a key against a house never walks you into it', () => {
  const layout = createLayout(fixture);
  const b = fixture.buildings[0];
  // 从房子南边的街上一直往北走（朝房子），走 200 步
  let pos = { x: b.plot.x * BLOCK + 2, z: b.plot.z * BLOCK + 9 };
  assert.ok(layout.walkable(pos), '起点在街上');
  for (let i = 0; i < 200; i++) pos = slide(pos, { x: 0, z: -0.1 }, layout.walkable);
  assert.ok(layout.walkable(pos), `停在墙外（${pos.x.toFixed(2)}, ${pos.z.toFixed(2)}）`);
  assert.ok(pos.z > b.plot.z * BLOCK + 5, '没穿进房子');
  // 斜着往西北走：贴着墙滑到街口去，不会卡死
  const before = pos.x;
  for (let i = 0; i < 200; i++) pos = slide(pos, { x: -0.07, z: -0.07 }, layout.walkable);
  assert.ok(pos.x < before - 5, '沿墙滑出去了');
});

test('views: three of them, free first; dragging turns and tilts within limits', () => {
  assert.deepEqual([...VIEW_MODES], ['free', 'player', 'athena'], '自由视角排第一（缺省）');
  assert.equal(nextView('free'), 'player');
  assert.equal(nextView('player'), 'athena');
  assert.equal(nextView('athena'), 'free');
  const right = orbitAngles(45, 33, 100, 0);
  near(right.azimuth, 45 - 100 * VIEW.degPerPx, '向右拖：方位角减小（景物跟着手转）');
  near(right.elevation, 33, '横拖不改仰角');
  assert.equal(orbitAngles(45, 33, 0, 10_000).elevation, VIEW.maxElevation, '往下拖到头：俯视有上限');
  assert.equal(orbitAngles(45, 33, 0, -10_000).elevation, VIEW.minElevation, '往上拖到头：不会贴地平视');
  assert.ok(VIEW.dragPx >= 4, '点击时手抖几像素不算拖动');
});

test('free view keeps you in the frame: nudges only when you walk out of the middle', () => {
  assert.equal(deadZoneShift({ x: 0.2, y: -0.4 }), null, '在中间的方框里：不动');
  const s = deadZoneShift({ x: 0.9, y: -0.7 });
  near(s.x, 0.9 - VIEW.deadZone, '右边出框多少挪多少');
  near(s.y, -0.7 + VIEW.deadZone, '下边出框多少挪多少');
});

test('turning takes the short way round', () => {
  const t = turnToward(Math.PI - 0.1, -Math.PI + 0.1, 1);
  near(Math.atan2(Math.sin(t), Math.cos(t)), -Math.PI + 0.1, '跨过 ±π 也转到位');
  near(turnToward(0, 1, 0.5), 0.5, '按比例转');
});

test('keys typed into the talk bar are letters, not footsteps', () => {
  assert.ok(isTypingTarget({ tagName: 'TEXTAREA' }));
  assert.ok(isTypingTarget({ tagName: 'input' }));
  assert.ok(isTypingTarget({ tagName: 'DIV', isContentEditable: true }));
  assert.ok(!isTypingTarget({ tagName: 'CANVAS' }));
  assert.ok(!isTypingTarget({ tagName: 'BODY' }));
  assert.ok(!isTypingTarget(null));
});

test('talk bar: you can hand Athena a request only when she is free and you wrote something', () => {
  const ui = UI_ZH;
  const idle = { conversationId: 'c-1', canSend: true, busy: false };
  assert.equal(talkView(idle, '整理一下下载区', ui).canSend, true, '空闲、写了字：能托付');
  assert.equal(talkView(idle, '   \n ', ui).canSend, false, '只有空白：不能');
  assert.equal(talkView({ ...idle, conversationId: null }, '整理', ui).canSend, false, '还不知道是哪一份委托：不能');
  const busy = talkView({ ...idle, busy: true, canSend: false }, '下一件事', ui);
  assert.equal(busy.canSend, false, '她在办事：先写着，不能托付');
  assert.equal(busy.showStop, true, '她在办事：可以请她停下');
  assert.equal(busy.status, ui.talkBusy);
  assert.equal(talkView({ ...idle, waiting: true, busy: true }, '', ui).status, ui.talkWaiting, '等审批时提示去看审批窗口');
  // 2026-10-13：审批只在对话栏上显示（她不再停在门槛前）——红封印，收起时占位字就是"她在等你点头"
  const waiting = talkView({ ...idle, waiting: true, busy: true }, '', ui, false);
  assert.equal(waiting.seal, true, '等审批：栏上一枚红封印');
  assert.equal(waiting.placeholder, ui.talkWaiting, '收起时看不见状态行，等审批写在占位字里（比"她在办事"优先）');
  assert.equal(talkView({ ...idle, waiting: true, busy: true }, '', ui, true).placeholder, ui.talkPlaceholder, '展开时状态行已经说了，占位字只是邀请');
  assert.equal(talkView(idle, '', ui).seal, false, '不等审批：没有封印');
  assert.equal(talkView({ ...idle, compressing: true, canSend: false }, 'x', ui).canSend, false, '整理记忆（压缩上下文）时不能托付');
  assert.equal(talkView(idle, '', ui).status, ui.talkIdle);
  assert.ok(MAX_SAY_CHARS >= 8000, '一次能写一大段话');
});

test('the page labels the new controls in its own table (C# overrides them per language)', () => {
  for (const key of ['viewLabel', 'viewFree', 'viewPlayer', 'viewAthena', 'controlsHint', 'focusHint', 'athenaHere',
    'talkName', 'talkToAthena', 'talkPlaceholder', 'talkSend', 'talkStop', 'talkHint', 'talkIdle', 'talkBusy', 'talkQueued',
    'talkWaiting', 'talkCompressing', 'talkAttachments',
    // M3 第三轮：走近按 E 的提示
    'interactKey', 'interactTalk', 'interactResident', 'interactItem', 'interactPlace', 'interactTree', 'interactBoard', 'interactOracle', 'interactVenue']) {
    assert.ok(typeof UI_ZH[key] === 'string' && UI_ZH[key].length > 0, `UI_ZH.${key}`);
  }
  assert.ok(!('followAthena' in UI_ZH), '旧的"镜头："按钮词条不用了');
});
