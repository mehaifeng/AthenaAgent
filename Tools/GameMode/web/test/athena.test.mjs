// 雅典娜住在城里（athena.mjs，2026-10-13）：她不再镜像回合，只在广场上待着、走一走；不瞬移，远了就跑；递卷轴时追着你跑。
// 运行：node --test Tools/GameMode/web/test/
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createAthena, ATHENA, spotIndex, stepProgress } from '../../../../Assets/Polis/src/athena.mjs';
import { createLayout } from '../../../../Assets/Polis/src/layout.mjs';

const fixture = JSON.parse(readFileSync(new URL('../fixtures/synthetic.json', import.meta.url), 'utf8'));

/** 假的路程：广场一圈的点之间 1.2 秒，去港口 20 秒（远过 10 秒：跑），你身边 4 秒。 */
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

test('a new destination waits for the walk in progress (rule 1), and a far one is run, never teleported (rule 4)', () => {
  const a = createAthena({ travelMs, home: 'agora:0', startAt: 0 });
  a.goTo('agora:2', 100);
  const walking = a.update(500).step;
  assert.equal(walking.kind, 'walk');
  assert.equal(walking.to, 'agora:2');
  a.goTo('harbor', 600);
  assert.equal(a.update(900).step.to, 'agora:2', '走到一半不改道');
  const run = a.update(100 + 1_200 + 1).step;
  assert.equal(run.kind, 'run', '港口远过 10 秒的路：跑过去（2026-10-13 起没有瞬移）');
  assert.equal(run.start, 100 + 1_200, '新目标从上一段走完的那一刻开始');
  assert.equal(run.end - run.start, 20_000 * ATHENA.runFactor, '跑的时长是走的 runFactor 倍');
  a.update(run.end + 1);
  assert.equal(a.position, 'harbor');
  assert.equal(a.goTo('harbor', run.end + 2), false, '已经在那儿了：什么也不做');
  assert.ok(!a.steps.some((x) => x.kind === 'teleport'), '没有一步是瞬移');
  assert.ok(!('teleportMs' in ATHENA));
});

test('chase (rule 6, handing you a scroll): she runs to you from where she is, re-routes when you move, and drops what was queued', () => {
  const a = createAthena({ travelMs, home: 'agora:0', startAt: 0 });
  a.goTo('agora:1', 0);
  a.goTo('agora:2', 0);
  assert.equal(a.update(500).step.kind, 'walk');
  assert.equal(a.chase('pt:5.00,5.00', 'pt:0.50,0.00', 600), true);
  const first = a.state().step;
  assert.deepEqual([first.kind, first.from, first.to, first.start, first.reason], ['run', 'pt:0.50,0.00', 'pt:5.00,5.00', 600, 'chase'], '从此刻所在的那一点跑向你（追的这一段永远是跑）');
  assert.equal(first.end - first.start, 4_000 * ATHENA.runFactor);
  assert.equal(a.state().queued, 0, '排着的目的地作废');
  assert.equal(a.chase('pt:5.00,5.00', 'pt:1.00,1.00', 700), false, '已经在追这一点了：不重来');
  // 你走动了：从她此刻所在的那一点改道
  assert.equal(a.chase('pt:8.00,5.00', 'pt:2.00,2.00', 1200), true);
  const second = a.state().step;
  assert.deepEqual([second.from, second.to, second.start], ['pt:2.00,2.00', 'pt:8.00,5.00', 1200]);
  assert.equal(first.end, 1200, '上一段在改道那一刻截住');
  a.update(second.end + 1);
  assert.equal(a.position, 'pt:8.00,5.00', '跑到了你跟前');
  assert.equal(a.state().step.kind, 'idle');
  // 时间只进不退：更早的 t 当成此刻
  const b = createAthena({ travelMs, home: 'agora:0', startAt: 5000 });
  b.chase('pt:1.00,1.00', 'agora:0', 10);
  assert.equal(b.state().step.start, 5000);
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

test('on the real synthetic city: the agora ring is walked, the harbour is far enough that she runs', () => {
  const layout = createLayout(fixture);
  const a = createAthena({ travelMs: (x, y) => layout.travelMs(x, y), home: 'agora:0', startAt: 0 });
  a.goTo('agora:3', 0);
  const walk = a.update(1).step;
  assert.equal(walk.kind, 'walk', '广场对面那个点走得到');
  assert.ok(walk.end - walk.start < 3000, `广场一圈不到 3 秒：${walk.end - walk.start}`);
  a.goTo('harbor', walk.end);
  const run = a.update(walk.end + 1).step;
  assert.equal(run.kind, 'run', `广场到港口远过 ${ATHENA.maxWalkMs / 1000} 秒：${layout.travelMs('agora:3', 'harbor')} 毫秒`);
  assert.equal(stepProgress(run, (run.start + run.end) / 2), 0.5);
  assert.equal(stepProgress(a.steps[0], 10), null, '待着的一步没有进度');
});
