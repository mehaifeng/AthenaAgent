// 编排器单测：设计稿 6.2 的六条节奏规则，每条至少一例。
// 运行：node --test Tools/GameMode/web/test/   （Node ≥ 20；应用自带的 Playwright 驱动里也有一份 node）
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createOrchestrator, DEFAULTS, stepProgress } from '../src/orchestrator.mjs';

const hop = (ms) => (from, to) => (from === to ? 0 : ms);

function tool(t, id, site, ms = 50, extra = {}) {
  return [
    { t, type: 'tool-start', id, site, category: extra.category ?? 'read', ...extra },
    { t: t + ms, type: 'tool-end', id, ok: extra.ok ?? true },
  ];
}

function feed(o, events, until, stepMs = null) {
  for (const e of events) o.ingest(e);
  if (stepMs == null) return o.update(until);
  let s;
  for (let t = 0; t <= until; t += stepMs) s = o.update(t);
  return o.update(until) ?? s;
}

const kinds = (o) => o.steps.map((s) => s.kind);

// —— 规则 1：工具执行永远不等动画 ——
test('rule 1: ingesting never waits on the presentation and never shifts engine time', () => {
  const o = createOrchestrator({ travelMs: hop(10_000), maxWalkMs: 60_000 });   // 故意让画面慢得离谱
  const events = [{ t: 0, type: 'turn' }];
  for (let k = 0; k < 30; k++) events.push(...tool(k * 50, `t${k}`, `s${k}`, 20));
  // 像回放那样边走边喂：画面越积越多，事件照样按自己的时间进来
  const started = performance.now();
  let next = 0;
  for (let t = 0; t <= 1_600; t += 10) {
    while (next < events.length && events[next].t <= t) o.ingest(events[next++]);
    o.update(t);
  }
  assert.ok(performance.now() - started < 500, 'ingest 只记账，立即返回');
  assert.ok(o.state(1_600).queueLength > 20, '画面已经积压了一大串');
  // 引擎时间原样记下，一个都没被呈现拖后
  assert.deepEqual(o.ingested.map((e) => e.t), events.map((e) => e.t));
  for (let k = 0; k < 30; k++) {
    const tl = o.tools.get(`t${k}`);
    assert.equal(tl.start, k * 50, `t${k} 的开始时间就是事件时间`);
    assert.equal(tl.end, k * 50 + 20, `t${k} 在 ${k * 50 + 20} ms 做完，画面还在第一段路上也一样`);
  }
  assert.equal(o.state(1_600).step.kind, 'walk', '这时画面还在走第一段路');
});

// —— 规则 2：动画收尾有上限 ——
test('rule 2: a finished tool is presented by at most one walk and one act, and only a beat when others wait', () => {
  const near = createOrchestrator({ travelMs: hop(1000) });
  feed(near, [{ t: 0, type: 'turn' }, ...tool(0, 'a', 'A', 50)], 10_000);
  const act = near.steps.find((s) => s.kind === 'act');
  assert.equal(act.start, 1000);
  assert.equal(act.end, 1000 + DEFAULTS.minActMs, '演满一段 minActMs');

  // 没人排队：走 3.5 秒到场，动作照样演满，收尾是"一段路 + 一段动作"，有上限
  const far = createOrchestrator({ travelMs: hop(3500) });
  feed(far, [{ t: 0, type: 'turn' }, ...tool(0, 'a', 'A', 50)], 10_000);
  const late = far.steps.find((s) => s.kind === 'act');
  assert.equal(late.start, 3500);
  assert.equal(late.end - late.start, DEFAULTS.minActMs, '没人排队时到场照样演满，不是一闪');
  assert.ok(late.end - 50 <= DEFAULTS.maxWalkMs + DEFAULTS.minActMs, '收尾不超过 一段路 + 一段动作');

  // 有人排队：到的时候已经超过"结束 + 收尾上限"，只演一拍就去下一处
  const busy = createOrchestrator({ travelMs: hop(3500) });
  feed(busy, [{ t: 0, type: 'turn' }, ...tool(0, 'a', 'A', 50), ...tool(1000, 'b', 'B', 50)], 20_000);
  const hurried = busy.steps.find((s) => s.kind === 'act' && s.at === 'A');
  assert.equal(hurried.end - hurried.start, DEFAULTS.minBeatMs, '后面有人等：只演一拍');
  assert.ok(hurried.hurried);
});

test('rule 2: a long tool keeps her working until it ends, then the tail is only a settle beat', () => {
  const o = createOrchestrator({ travelMs: hop(1000) });
  feed(o, [{ t: 0, type: 'turn' }, ...tool(0, 'long', 'A', 30_000)], 40_000, 100);
  const act = o.steps.find((s) => s.kind === 'act');
  assert.equal(act.end, 30_000 + DEFAULTS.settleMs, '工具做完后只多演 settleMs');
  for (let t = 1000; t < 30_000; t += 500) {
    const s = o.steps.find((x) => x.start <= t && (x.end == null || x.end > t));
    assert.equal(s.kind, 'act', `${t} ms：长工具还在跑，她一直在做事，不沉思也不闲着`);
  }
});

test('rule 2: a long tool yields the stage to newer places instead of freezing the picture', () => {
  const o = createOrchestrator({ travelMs: hop(1000) });
  feed(o, [{ t: 0, type: 'turn' }, ...tool(0, 'browser', 'harbor', 120_000), ...tool(0, 'quick', 'B', 50)], 10_000, 50);
  const actA = o.steps.find((s) => s.kind === 'act' && s.at === 'harbor');
  assert.ok(actA.yielded && actA.end === actA.start + DEFAULTS.minActMs, '演满一段就让位');
  assert.ok(o.steps.some((s) => s.kind === 'act' && s.at === 'B'), '排在后面的地点照样演到');
});

test('rule 2: the tail after a burst is bounded no matter how many places the burst touched', () => {
  const tails = [];
  for (const n of [5, 20, 60, 200]) {
    const o = createOrchestrator({ travelMs: hop(1500) });
    const events = [{ t: 0, type: 'turn' }];
    for (let k = 0; k < n; k++) events.push(...tool(k * 100, `t${k}`, `s${k}`, 50));
    const lastEnd = (n - 1) * 100 + 50;
    events.push({ t: lastEnd, type: 'think' });
    feed(o, events, 120_000, 50);
    const settled = o.steps.find((s) => s.kind === 'meditate' && s.start >= lastEnd);
    assert.ok(settled, `${n} 个地点：最后回到沉思`);
    tails.push(settled.start - lastEnd);
  }
  const bound = DEFAULTS.lagThresholdMs + DEFAULTS.maxWalkMs + DEFAULTS.teleportMs + DEFAULTS.tailCapMs;
  for (const tail of tails) assert.ok(tail <= bound, `收尾 ${tail} ms 不超过上限 ${bound} ms`);
  // 不设上限时，200 个地点要排 200 ×（走 1.5 秒 + 演 1.1 秒）≈ 9 分钟
  assert.ok(Math.max(...tails) < 200 * 1500 / 20, `收尾 ${tails.join(' / ')} ms：不随地点数量增长`);
});

// —— 规则 3：同一地点的连续动作合并成一次 ——
test('rule 3: consecutive calls at the same place become one walk and one act', () => {
  const o = createOrchestrator({ travelMs: hop(2000) });
  feed(o, [{ t: 0, type: 'turn' }, ...tool(0, 'r1', 'Docs'), ...tool(100, 'r2', 'Docs'), ...tool(200, 'w1', 'Docs', 50, { category: 'write' })], 10_000);
  assert.equal(o.steps.filter((s) => s.kind === 'walk').length, 1, '只走一次');
  const acts = o.steps.filter((s) => s.kind === 'act');
  assert.equal(acts.length, 1, '只演一段');
  assert.deepEqual(acts[0].visit.toolIds, ['r1', 'r2', 'w1'], '这一段覆盖三次调用');
  assert.equal(acts[0].category, 'write', '动作取最新的类别');
});

test('rule 3: calls that pile up for the same place while she is busy elsewhere wait as one visit', () => {
  const o = createOrchestrator({ travelMs: hop(2000) });
  feed(o, [{ t: 0, type: 'turn' }, ...tool(0, 'a', 'A'), ...tool(300, 'b1', 'B'), ...tool(400, 'b2', 'B'), ...tool(500, 'b3', 'B')], 20_000);
  const toB = o.steps.filter((s) => (s.kind === 'walk' || s.kind === 'teleport') && s.to === 'B');
  assert.equal(toB.length, 1, '去 B 只走一趟');
  const actB = o.steps.filter((s) => s.kind === 'act' && s.at === 'B');
  assert.equal(actB.length, 1);
  assert.deepEqual(actB[0].visit.toolIds, ['b1', 'b2', 'b3'], '排队时就已经合成一次拜访');
});

test('rule 3: a call at the place she is already working at extends that act instead of a new trip', () => {
  const o = createOrchestrator({ travelMs: hop(1000) });
  feed(o, [{ t: 0, type: 'turn' }, ...tool(0, 'a', 'A', 50), ...tool(1500, 'b', 'A', 50)], 10_000, 50);
  assert.equal(o.steps.filter((s) => s.kind === 'walk').length, 1);
  const acts = o.steps.filter((s) => s.kind === 'act');
  assert.equal(acts.length, 1, '正在 A 做事时 A 又来一件：同一段动作');
  assert.deepEqual(acts[0].visit.toolIds, ['a', 'b']);
  assert.ok(acts[0].end >= 1550 + DEFAULTS.settleMs, '动作延长到覆盖第二件');
});

test('rule 3: only consecutive calls merge — A, B, A is three visits', () => {
  const o = createOrchestrator({ travelMs: hop(1000) });
  feed(o, [{ t: 0, type: 'turn' }, ...tool(0, 'a1', 'A'), ...tool(100, 'b1', 'B'), ...tool(200, 'a2', 'A')], 20_000);
  assert.deepEqual(o.steps.filter((s) => s.kind === 'act').map((s) => s.at), ['A', 'B', 'A']);
});

// —— 规则 4：每次移动都要完成 ——
test('rule 4: a walk in progress is never retargeted; the new place waits its turn', () => {
  const o = createOrchestrator({ travelMs: hop(3000) });
  feed(o, [{ t: 0, type: 'turn' }, ...tool(0, 'a', 'A'), ...tool(1000, 'b', 'B')], 20_000, 50);
  const walks = o.steps.filter((s) => s.kind === 'walk');
  assert.equal(walks[0].to, 'A');
  assert.equal(walks[0].end, 3000, '去 A 的路走完了才停');
  const actA = o.steps.find((s) => s.kind === 'act' && s.at === 'A');
  assert.equal(actA.start, 3000, '到了 A 先做 A 的事');
  assert.equal(walks[1].to, 'B');
  assert.ok(walks[1].start >= actA.end, '然后才去 B');
});

test('rule 4: wandering never cuts into travel — a stroll finishes, then she sets off', () => {
  let n = 0;
  const o = createOrchestrator({ travelMs: hop(1500), wanderTarget: () => `agora:${n++}` });
  const wanderStart = DEFAULTS.idleBeforeWanderMs;
  const arrive = wanderStart + 300;   // 闲逛到一半来了活
  feed(o, [...tool(arrive, 'a', 'A')], 20_000, 50);
  const wander = o.steps.find((s) => s.kind === 'wander');
  assert.ok(wander, '待命 4 秒后出去走一小圈');
  assert.equal(wander.start, wanderStart);
  const walk = o.steps.find((s) => s.kind === 'walk');
  assert.equal(walk.start, wander.end, '闲逛走完那一刻才出发，没有半路掉头');
  assert.equal(walk.from, wander.to);
});

test('rule 4: no step ever changes its destination after it starts', () => {
  const o = createOrchestrator({ travelMs: hop(2500) });
  const events = [{ t: 0, type: 'turn' }];
  for (let k = 0; k < 12; k++) events.push(...tool(k * 700, `t${k}`, `s${k % 5}`));
  for (const e of events) o.ingest(e);
  const seen = new Map();
  for (let t = 0; t <= 40_000; t += 37) {
    o.update(t);
    for (const s of o.steps) {
      if (s.to == null) continue;
      if (!seen.has(s)) seen.set(s, s.to);
      assert.equal(s.to, seen.get(s), '移动开始后目的地不变');
    }
  }
});

// —— 规则 5：画面落后超过阈值就瞬移 ——
test('rule 5: once the oldest pending place is more than the threshold behind, she teleports to the newest', () => {
  const o = createOrchestrator({ travelMs: hop(3000) });
  const events = [{ t: 0, type: 'turn' }];
  ['A', 'B', 'C', 'D', 'E'].forEach((s, k) => events.push(...tool(k * 200, s.toLowerCase(), s)));
  feed(o, events, 20_000, 50);
  const tp = o.steps.find((s) => s.kind === 'teleport');
  assert.ok(tp, '落后了就瞬移');
  assert.equal(tp.reason, 'lag');
  assert.ok(tp.lag > DEFAULTS.lagThresholdMs, `落后 ${tp.lag} ms 超过阈值`);
  assert.equal(tp.to, 'E', '直接去最新的那一处');
  assert.equal(tp.skipped, 3, '中间三处不再排队');
  assert.equal(tp.end - tp.start, DEFAULTS.teleportMs);
  assert.equal(o.state(20_000).queueLength, 0);
});

test('rule 5: no teleport while she keeps up', () => {
  const o = createOrchestrator({ travelMs: hop(1500) });
  const events = [{ t: 0, type: 'turn' }];
  for (let k = 0; k < 6; k++) events.push(...tool(k * 6000, `t${k}`, `s${k}`));
  feed(o, events, 60_000, 50);
  assert.equal(o.steps.filter((s) => s.kind === 'teleport').length, 0);
  assert.equal(o.steps.filter((s) => s.kind === 'walk').length, 6);
});

test('rule 5: a delivery is never dropped when she catches up', () => {
  const o = createOrchestrator({ travelMs: hop(3000), playerSpot: () => 'pt:5,5' });
  feed(o, [{ t: 0, type: 'turn' }, ...tool(0, 'a', 'A'), ...tool(100, 'b', 'B'), ...tool(200, 'c', 'C'), { t: 300, type: 'deliver', turn: 1 }], 30_000, 50);
  const deliver = o.steps.find((s) => s.kind === 'deliver');
  assert.ok(deliver, '交付一定会演');
  assert.equal(deliver.at, 'pt:5,5', '交付去玩家此刻所在的地方');
});

test('rule 5: a trip longer than maxWalk is a teleport even when she is on time', () => {
  const o = createOrchestrator({ travelMs: hop(DEFAULTS.maxWalkMs + 1) });
  feed(o, [{ t: 0, type: 'turn' }, ...tool(0, 'a', 'A')], 10_000);
  const tp = o.steps.find((s) => s.kind === 'teleport');
  assert.equal(tp.reason, 'far');
});

// —— 规则 6：等模型时沉思，没有空白 ——
test('rule 6: while the model thinks and nothing is queued she meditates — never a blank', () => {
  const o = createOrchestrator({ travelMs: hop(1500) });
  const events = [
    { t: 0, type: 'turn' }, { t: 0, type: 'think', reasoning: 800 },
    ...tool(8000, 'a', 'A', 50),
    { t: 8050, type: 'think', reasoning: 120 },
    { t: 20_000, type: 'deliver', turn: 1 },
  ];
  feed(o, events, 26_000, 25);
  for (let t = 0; t < 20_000; t += 100) {
    const s = o.steps.find((x) => x.start <= t && (x.end == null || x.end > t));
    assert.ok(s, `${t} ms 有一个步骤`);
    assert.ok(['meditate', 'walk', 'act', 'teleport'].includes(s.kind), `${t} ms 不是空白（${s.kind}）`);
  }
  const before = o.steps.find((s) => s.kind === 'meditate' && s.start === 0);
  assert.equal(before.end, 8000, '提问到第一个工具之间一直沉思');
  assert.equal(before.reasoning, 800, '沉思带着推理长度，光的明暗由它驱动');
  const actA = o.steps.find((s) => s.kind === 'act');
  const after = o.steps.find((s) => s.kind === 'meditate' && s.start === actA.end);
  assert.ok(after, '做完事接着沉思，中间没有一帧闲着');
  assert.equal(after.end, 20_000);
  const deliverWalk = o.steps.find((s) => s.start === 20_000);
  assert.ok(deliverWalk.kind === 'walk' || deliverWalk.kind === 'deliver', '交付时走向玩家');
  const last = o.state(26_000).step;
  assert.equal(last.kind, 'idle', '交付之后这一回合结束，她才闲下来');
});

// —— 其余性质 ——
test('the timeline is deterministic: one big jump and 16 ms frames give the same steps', () => {
  const events = [{ t: 0, type: 'turn' }, { t: 0, type: 'think' }];
  for (let k = 0; k < 15; k++) events.push(...tool(1000 + k * 900, `t${k}`, `s${k % 4}`, k % 3 === 0 ? 2500 : 40));
  events.push({ t: 20_000, type: 'deliver', turn: 1 });
  const strip = (o) => o.steps.map(({ kind, start, end, to, at }) => ({ kind, start, end, to, at }));
  const a = createOrchestrator({ travelMs: hop(1800), playerSpot: () => 'pt:0,0' });
  feed(a, events, 23_000);
  const b = createOrchestrator({ travelMs: hop(1800), playerSpot: () => 'pt:0,0' });
  feed(b, events, 23_000, 16);
  assert.deepEqual(strip(a), strip(b));
});

test('events must carry a finite time', () => {
  const o = createOrchestrator();
  assert.throws(() => o.ingest({ type: 'turn' }), TypeError);
  assert.throws(() => o.ingest({ t: Number.NaN, type: 'turn' }), TypeError);
});

test('stepProgress clamps and reports open steps as null', () => {
  assert.equal(stepProgress({ start: 0, end: 100 }, 50), 0.5);
  assert.equal(stepProgress({ start: 0, end: 100 }, 500), 1);
  assert.equal(stepProgress({ start: 0, end: null }, 50), null);
  assert.equal(stepProgress({ start: 10, end: 10 }, 0), 1);
  assert.deepEqual(kinds(createOrchestrator()), ['idle'], '一开始在广场待命');
});
