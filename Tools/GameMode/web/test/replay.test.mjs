// 回放、旁白、布局的单测（纯逻辑，读提交在仓库里的合成夹具）。
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createReplay, toEngineEvents, siteForTool, cityIndex } from '../src/replay.mjs';
import { createOrchestrator } from '../src/orchestrator.mjs';
import { lineForVisit, displayName, pick, LINES_ZH } from '../src/narration.mjs';
import { createLayout, facingFor, pointAlong, polyLength, BLOCK, SPEED } from '../src/layout.mjs';

const fixture = JSON.parse(readFileSync(new URL('../fixtures/synthetic.json', import.meta.url), 'utf8'));

test('fixture events become ordered engine events: ends before starts at the same instant', () => {
  const events = toEngineEvents(fixture);
  for (let k = 1; k < events.length; k++) assert.ok(events[k - 1].t <= events[k].t, '按时间排序');
  const at6260 = events.filter((e) => e.t === 6260).map((e) => e.type);
  assert.deepEqual(at6260, ['tool-end', 'think'], '工具结束在先，下一段等模型在后');
  const starts = events.filter((e) => e.type === 'tool-start');
  assert.equal(starts.length, fixture.replay.toolCalls);
  assert.equal(events.filter((e) => e.type === 'tool-end').length, starts.length, '每个开始都有结束');
});

test('tools map to places: category first, then the building the path falls in, outside is the gate', () => {
  const city = cityIndex(fixture);
  const sites = Object.fromEntries(toEngineEvents(fixture).filter((e) => e.type === 'tool-start').map((e) => [e.id, e.site]));
  assert.equal(sites.t1, 'b:合同', '读合同去合同那座书库');
  assert.equal(sites.t5, 'forge', '终端去锻炉，哪怕工作目录在别的建筑');
  assert.equal(sites.t6, 'harbor', '网页搜索去港口');
  assert.equal(sites.t9, 'library', '记忆去图书馆');
  assert.equal(sites.t10, 'agora', '侍女在广场唤出');
  assert.equal(sites.t11, 'gate', '工作区外：出城门');
  assert.equal(siteForTool({ category: 'read', place: 'inside', building: '' }, city), 'agora', '根目录下的文件：广场');
  assert.equal(siteForTool({ category: 'read', place: 'inside', building: '摊位' }, { buildingKeys: new Set(), marketKeys: new Set(['摊位']) }), 'market');
});

test('the replay clock feeds by time only — never by what the picture is doing (rule 1)', () => {
  const replay = createReplay(fixture);
  const sink = { got: [], ingest(e) { this.got.push(e); } };
  replay.feed(sink, 10_000);
  assert.ok(sink.got.every((e) => e.t <= 10_000));
  const n = sink.got.length;
  replay.feed(sink, 10_000);
  assert.equal(sink.got.length, n, '同一时刻再喂不会重复');
  replay.feed(sink, replay.duration);
  assert.deepEqual(sink.got.map((e) => e.t), replay.events.map((e) => e.t), '喂出去的时间就是夹具时间，一个不差');
  assert.equal(replay.turnAt(22_000).turn, 1);
  assert.equal(replay.turnAt(22_000).delivered, true);
  assert.equal(replay.turnAt(24_050).delivered, false);
});

test('the whole synthetic replay drives the orchestrator to a delivery per turn', () => {
  const layout = createLayout(fixture);
  const o = createOrchestrator({ travelMs: (a, b) => layout.travelMs(a, b), playerSpot: () => 'pt:0,8' });
  const replay = createReplay(fixture);
  for (let t = 0; t <= replay.duration + 8000; t += 16) {
    replay.feed(o, t);
    o.update(t);
  }
  assert.equal(o.steps.filter((s) => s.kind === 'deliver').length, 3, '三个回合三次交付');
  assert.ok(o.steps.some((s) => s.kind === 'act' && s.at === 'forge'));
  assert.ok(o.steps.some((s) => s.kind === 'act' && s.at === 'harbor'));
  assert.ok(o.steps.some((s) => s.kind === 'act' && s.at === 'gate' && s.failed), '城外那一次失败了');
  const merged = o.steps.find((s) => s.kind === 'act' && s.visit?.toolIds?.includes('t2'));
  assert.deepEqual(merged.visit.toolIds.slice(0, 2), ['t2', 't3'], '一批并发的两件在同一座书库：合并成一段');
});

test('narration: display names, deterministic variants, no paths', () => {
  assert.equal(displayName('合同/附件/附件一.pdf'), '附件一');
  assert.equal(displayName('a/b/very-long-file-name-here.md', 8), 'very-lo…');
  assert.equal(displayName('合同'), '合同');
  assert.equal(pick('a|b|c', 4), 'b');
  assert.equal(pick('a|b|c', -1), 'c');
  const kindOf = (b) => (b === '合同' ? 'stoaLibrary' : b === '网站' ? 'workshop' : null);
  const read = lineForVisit({ kind: 'work' }, [{ category: 'read', place: 'inside', path: '合同/租赁合同.docx', building: '合同' }], kindOf, 0);
  assert.equal(read, '我去书库翻翻《租赁合同》');
  const write = lineForVisit({ kind: 'work' }, [{ category: 'write', place: 'inside', path: '网站/app.js', building: '网站' }], kindOf, 1);
  assert.equal(write, '在作坊落笔写《app》');
  assert.equal(lineForVisit({ kind: 'work' }, [{ category: 'subagents', agents: 3 }], kindOf, 0), '我叫上了3位侍女');
  assert.equal(lineForVisit({ kind: 'deliver' }, [], kindOf, 0), '做好了，你看看');
  assert.equal(lineForVisit({ kind: 'work' }, [{ category: 'read', place: 'outside' }], kindOf, 0), '我出城一趟');
  const dir = lineForVisit({ kind: 'work' }, [{ category: 'read', place: 'inside', path: '合同', building: '合同' }], kindOf, 0);
  assert.equal(dir, '我去书库看看', '目录没有文件名：只说去哪儿');
  for (const [key, value] of Object.entries(LINES_ZH)) {
    assert.ok(!value.includes('/'), `${key}：旁白里不出现路径`);
  }
});

test('layout: buildings face the camera (south) and stand points sit on the street in front', () => {
  for (const [x, z] of [[1, -1], [-2, 0], [1, 1], [0, 0]]) assert.deepEqual(facingFor(x, z), [0, 1], `(${x}, ${z}) 正面朝南`);
  const layout = createLayout(fixture);
  for (const [key, plot] of layout.plots) {
    const p = layout.sitePoint(key === 'agora' ? 'agora' : key);
    if (key === 'agora' || key === 'harbor') continue;
    const d = Math.hypot(p.x - plot.cx, p.z - plot.cz);
    assert.ok(d > 6 && d < 8, `${key} 的站立点在门前（离地块中心 ${d.toFixed(1)} 米）`);
    assert.ok(p.z > plot.cz, `${key} 的站立点在南边的街上`);
  }
  assert.ok(layout.sitePoint('harbor').z > layout.quayZ, '港口的站立点在栈桥上，伸进海里');
  assert.ok(layout.sitePoint('gate').z < -layout.ring * BLOCK, '城门在最北一圈之外');
});

test('layout: routes follow streets, start and end where asked, and travel time follows length', () => {
  const layout = createLayout(fixture);
  const sites = ['agora', 'temple', 'library', 'forge', 'harbor', 'market', 'gate', ...fixture.buildings.map((b) => `b:${b.key}`)];
  for (const a of sites) {
    for (const b of sites) {
      const r = layout.route(a, b);
      const pa = layout.sitePoint(a), pb = layout.sitePoint(b);
      assert.ok(Math.hypot(r[0].x - pa.x, r[0].z - pa.z) < 1e-6, `${a}→${b} 从起点出发`);
      assert.ok(Math.hypot(r.at(-1).x - pb.x, r.at(-1).z - pb.z) < 1e-6, `${a}→${b} 到终点为止`);
      const len = polyLength(r);
      assert.ok(len + 1e-6 >= Math.hypot(pa.x - pb.x, pa.z - pb.z), '路不会比直线还短');
      assert.ok(Math.abs(layout.travelMs(a, b) - (len / SPEED) * 1000) < 1e-6);
      for (const p of r) assert.ok(p.z <= layout.quayZ + 12, `${a}→${b} 不走进海里`);
    }
  }
  const neighbour = layout.travelMs('agora', 'b:网站');
  assert.ok(neighbour < 4000, `广场到隔壁一座建筑 ${neighbour.toFixed(0)} ms：走得到，不必瞬移`);
});

test('layout: a click inside a building plot snaps to its door, elsewhere is a free point', () => {
  const layout = createLayout(fixture);
  const b = fixture.buildings[0];
  assert.equal(layout.snapClick({ x: b.plot.x * BLOCK + 1, z: b.plot.z * BLOCK - 2 }), `b:${b.key}`);
  assert.equal(layout.snapClick({ x: 2, z: 3 }), 'pt:2.00,3.00', '广场上点哪儿去哪儿');
  const mid = pointAlong([{ x: 0, z: 0 }, { x: 10, z: 0 }, { x: 10, z: 10 }], 0.75);
  assert.deepEqual([mid.x, mid.z], [10, 5]);
});
