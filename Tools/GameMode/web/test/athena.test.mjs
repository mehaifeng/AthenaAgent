// 雅典娜住在城里（athena.mjs，2026-10-13）：她不再镜像回合，只在广场上待着、走一走、你开口时过来。
// 运行：node --test Tools/GameMode/web/test/
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createAthena, ATHENA, spotIndex, stepProgress } from '../../../../Assets/Polis/src/athena.mjs';
import { createLayout } from '../../../../Assets/Polis/src/layout.mjs';

const fixture = JSON.parse(readFileSync(new URL('../fixtures/synthetic.json', import.meta.url), 'utf8'));

/** 假的路程：广场一圈的点之间 1.2 秒，去港口 20 秒（走不到，瞬移），你身边 4 秒。 */
const travelMs = (from, to) => {
  if (from === to) return 0;
  if (to === 'harbor' || from === 'harbor') return 20_000;
  if (to.startsWith('pt:') || from.startsWith('pt:')) return 4_000;
  return 1_200;
};

test('she wanders only between the six points of the agora ring, one after another, and every walk completes', () => {
  const a = createAthena({ travelMs, home: 'agora:0', startAt: 0 });
  a.update(120_000);
  const moves = a.steps.filter((s) => s.kind !== 'idle');
  assert.ok(moves.length >= 8, `两分钟里走了几趟：${moves.length}`);
  for (const s of moves) {
    assert.equal(s.kind, 'walk', '广场一圈的点之间都是走的');
    assert.ok(spotIndex(s.to) >= 0 && spotIndex(s.to) < ATHENA.wanderSpots, `闲逛只去广场一圈的点：${s.to}`);
    assert.equal(spotIndex(s.to), (spotIndex(s.from) + 1) % ATHENA.wanderSpots, '一个点接一个点');
  }
  // 每一步从上一步结束的那一刻开始：没有被改道、没有半路换目标
  for (let i = 1; i < a.steps.length; i++) assert.equal(a.steps[i].start, a.steps[i - 1].end ?? a.steps[i].start, `第 ${i} 步接着上一步`);
  // 每到一处至少待 idleMs
  for (const s of a.steps.filter((x) => x.kind === 'idle' && x.end == null)) assert.ok(s.until - s.start >= ATHENA.idleMs - 1e-6);
});

test('a new destination waits for the walk in progress (rule 1), and a far one is a teleport (rule 4)', () => {
  const a = createAthena({ travelMs, home: 'agora:0', startAt: 0 });
  a.goTo('agora:2', 100);
  const walking = a.update(500).step;
  assert.equal(walking.kind, 'walk');
  assert.equal(walking.to, 'agora:2');
  a.goTo('harbor', 600);
  assert.equal(a.update(900).step.to, 'agora:2', '走到一半不改道');
  const tele = a.update(100 + 1_200 + 1).step;
  assert.equal(tele.kind, 'teleport', '港口远过 10 秒的路：化作一道光');
  assert.equal(tele.start, 100 + 1_200, '新目标从上一段走完的那一刻开始');
  assert.equal(tele.end - tele.start, ATHENA.teleportMs);
  a.update(tele.end + 1);
  assert.equal(a.position, 'harbor');
  assert.equal(a.goTo('harbor', tele.end + 2), false, '已经在那儿了：什么也不做');
});

test('held (you are near, or the talk bar is open): no new wander starts, and the walk in progress still completes (rule 3)', () => {
  const a = createAthena({ travelMs, home: 'agora:0', startAt: 0 });
  const firstIdle = a.steps[0];
  a.goTo('agora:1', 0);
  a.update(300, { hold: true });
  assert.equal(a.state().step.kind, 'walk', '被留住之前就出发了的那一段照样走完');
  for (let t = 400; t <= 60_000; t += 400) a.update(t, { hold: true });
  assert.equal(a.state().step.kind, 'idle', '一分钟里一直被留住：一步也没再走');
  assert.equal(a.position, 'agora:1');
  const released = 60_400;
  const s = a.update(released).step;
  assert.equal(s.kind, 'walk', '放开她，过了该走的时候就接着走');
  assert.ok(s.start >= 60_000, `从最近一次被留住时起算，不是补走一段很久以前就该走的路：${s.start}`);
  assert.ok(firstIdle.until > 0);
});

test('the same seed gives the same day; another seed staggers it; time never goes back (rule 5)', () => {
  const run = (seed) => {
    const a = createAthena({ travelMs, home: 'agora:0', startAt: 0, seed });
    for (let t = 0; t < 90_000; t += 37) a.update(t);
    return a.steps.map((s) => `${s.kind}:${Math.round(s.start)}:${s.to ?? s.at}`);
  };
  assert.deepEqual(run(7), run(7));
  assert.notDeepEqual(run(7), run(8), '停留时长按种子错开');
  const a = createAthena({ travelMs, home: 'agora:0', startAt: 1000 });
  a.update(5000);
  const before = a.steps.length;
  a.update(10);
  assert.equal(a.steps.length, before, '更早的 now 当成上一次');
});

test('on the real synthetic city: the agora ring is walked, the harbour is too far and she teleports', () => {
  const layout = createLayout(fixture);
  const a = createAthena({ travelMs: (x, y) => layout.travelMs(x, y), home: 'agora:0', startAt: 0 });
  a.goTo('agora:3', 0);
  const walk = a.update(1).step;
  assert.equal(walk.kind, 'walk', '广场对面那个点走得到');
  assert.ok(walk.end - walk.start < 3000, `广场一圈不到 3 秒：${walk.end - walk.start}`);
  a.goTo('harbor', walk.end);
  const tele = a.update(walk.end + 1).step;
  assert.equal(tele.kind, 'teleport', `广场到港口远过 ${ATHENA.maxWalkMs / 1000} 秒：${layout.travelMs('agora:3', 'harbor')} 毫秒`);
  assert.equal(stepProgress(tele, tele.start + ATHENA.teleportMs / 2), 0.5);
  assert.equal(stepProgress(a.steps[0], 10), null, '待着的一步没有进度');
});
