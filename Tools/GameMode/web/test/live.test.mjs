// 实时模式的纯逻辑、卷轴阅读器的"不解释 HTML"、编排器的"等审批"（M1）。
// 运行：node --test Tools/GameMode/web/test/
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, readdirSync, statSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import { createLiveFeed, framePolicy, transitionArrival, intent, INTENT_TYPES, TRANSITION, FRAME } from '../../../../Assets/Polis/src/live.mjs';
import { parseMarkdown, parseInline } from '../../../../Assets/Polis/src/reader.mjs';
import { createOrchestrator } from '../../../../Assets/Polis/src/orchestrator.mjs';
import { cityIndex } from '../../../../Assets/Polis/src/replay.mjs';

const SRC = fileURLToPath(new URL('../../../../Assets/Polis/src/', import.meta.url));

test('live feed: host ages become page times that never go backwards', () => {
  const feed = createLiveFeed({ city: cityIndex({ buildings: [{ key: '合同' }], market: [] }) });
  const got = [];
  const sink = { ingest: (e) => got.push(e) };
  feed.push([
    { type: 'turn', turn: 1, ageMs: 900 },
    { type: 'tool-start', id: 't1', tool: 'read_system_file', category: 'read', place: 'inside', path: '合同/a.docx', building: '合同', ageMs: 400 },
    { type: 'tool-end', id: 't1', ok: true, ageMs: 600 },   // 时钟乱序：不能比前一个更早
  ], 10_000);
  feed.drain(sink);
  assert.deepEqual(got.map((e) => e.t), [9100, 9600, 9600], '按"多久以前"换算成页面时间，且只进不退');
  assert.equal(got[1].site, 'b:合同', '读写类按 C# 算好的建筑落到那座建筑');
  feed.push([{ type: 'tool-start', id: 't2', category: 'terminal', ageMs: 0 }, { type: 'tool-start', id: 't3', category: 'read', place: 'outside', ageMs: 0 }], 11_000);
  feed.drain(sink);
  assert.equal(got[3].site, 'forge', '终端去锻炉');
  assert.equal(got[4].site, 'gate', '城外的路径去城门');
});

test('live feed: approval becomes approval-start / approval-end; unknown types are dropped', () => {
  const feed = createLiveFeed();
  const accepted = feed.push([{ type: 'approval', waiting: true }, { type: 'approve', id: 'x' }, { type: 'approval', waiting: false }, { nope: 1 }], 500);
  assert.equal(accepted, 2, '只收认得的类型');
  const got = [];
  feed.drain({ ingest: (e) => got.push(e.type) });
  assert.deepEqual(got, ['approval-start', 'approval-end']);
});

test('approval: she stops at the threshold holding the sealed tablet, and only acts after approval', () => {
  const o = createOrchestrator({ travelMs: (a, b) => (a === b ? 0 : 1000) });
  o.ingest({ t: 0, type: 'turn', turn: 1 });
  o.ingest({ t: 100, type: 'tool-start', id: 'w', site: 'b:合同', category: 'write' });
  o.ingest({ t: 300, type: 'approval-start' });
  assert.equal(o.update(500).step.kind, 'walk', '等审批不打断赶路（规则 4）');
  const waiting = o.update(2000);
  assert.equal(waiting.step.kind, 'await', '走到了就停在门槛前，不开工');
  assert.equal(waiting.step.at, 'b:合同');
  assert.equal(waiting.awaiting, true);
  assert.equal(o.update(8000).step.kind, 'await', '没人点头就一直等：审批没有超时');
  o.ingest({ t: 8200, type: 'approval-end' });
  const acting = o.update(8300);
  assert.equal(acting.step.kind, 'act', '点了头才开始做事');
  assert.equal(acting.step.at, 'b:合同');
  o.ingest({ t: 8400, type: 'tool-end', id: 'w', ok: true });
  o.ingest({ t: 9000, type: 'deliver', turn: 1 });
  assert.ok(o.update(12_000).step.kind !== 'await', '交付之后不再等审批');
});

test('approval after she already started there: she stops and waits, then resumes the same place', () => {
  const o = createOrchestrator({ travelMs: () => 1000, home: 'b:合同' });
  o.ingest({ t: 0, type: 'turn', turn: 1 });
  o.ingest({ t: 100, type: 'tool-start', id: 'w', site: 'b:合同', category: 'write' });
  assert.equal(o.update(105).step.kind, 'act', '人已经在那儿：立即开工');
  o.ingest({ t: 110, type: 'approval-start' });
  const waiting = o.update(500);
  assert.equal(waiting.step.kind, 'await', '审批弹出来了：停手，在门槛前等');
  o.ingest({ t: 3000, type: 'approval-end' });
  const resumed = o.update(3100);
  assert.equal(resumed.step.kind, 'act', '点头之后接着做这一处');
  assert.equal(resumed.step.at, 'b:合同');
});

test('approval with nothing queued: she waits in place, then goes back to meditating', () => {
  const o = createOrchestrator({ travelMs: () => 1000 });
  o.ingest({ t: 0, type: 'turn', turn: 1 });
  o.ingest({ t: 200, type: 'approval-start' });
  assert.equal(o.update(300).step.kind, 'await');
  o.ingest({ t: 900, type: 'approval-end' });
  assert.equal(o.update(1000).step.kind, 'meditate');
});

test('frame policy: hidden stops, busy is 60, idle is 30', () => {
  assert.equal(framePolicy({ visible: false, step: 'walk' }), 0, '不可见时停（对话模式、窗口最小化）');
  assert.equal(framePolicy({ visible: true, step: 'walk' }), FRAME.busyFps);
  assert.equal(framePolicy({ visible: true, step: 'act' }), FRAME.busyFps);
  assert.equal(framePolicy({ visible: true, step: 'meditate', now: 10_000 }), FRAME.idleFps, '沉思、待命是空闲');
  assert.equal(framePolicy({ visible: true, step: 'idle', transitioning: true }), FRAME.busyFps, '过场在动');
  assert.equal(framePolicy({ visible: true, step: 'idle', now: 1000, lastInputAt: 500 }), FRAME.busyFps, '刚拖过镜头');
  assert.equal(framePolicy({ visible: true, step: 'await', now: 10_000 }), FRAME.idleFps, '等审批时人不动');
});

test('voyage: at least the minimum, arrives once ready, and lands at the cap without waiting forever', () => {
  assert.deepEqual(transitionArrival(1000, 1200, 1300), { arriveAt: 1000 + TRANSITION.minMs, partial: false }, '就绪得再早也要演满最短时长');
  assert.deepEqual(transitionArrival(1000, 5000, 5000), { arriveAt: 5000, partial: false }, '就绪了就靠岸');
  assert.deepEqual(transitionArrival(1000, null, 3000), { arriveAt: null, partial: false }, '没就绪、没到上限：继续航行');
  assert.deepEqual(transitionArrival(1000, null, 1000 + TRANSITION.capMs), { arriveAt: 1000 + TRANSITION.capMs, partial: true }, '到了上限先靠岸，显示已就绪的部分');
});

test('intents: the closed set has no approval, and unknown types throw before they leave the page', () => {
  assert.ok(!INTENT_TYPES.some((t) => /approv|allow|grant|deny/i.test(t)), '网页没有"批准"意图');
  assert.throws(() => intent('approve', {}), /not a page intent/);
  assert.deepEqual(intent('open-file', { path: '合同/a.docx' }), { v: 1, type: 'open-file', path: '合同/a.docx' });
});

test('reader: raw HTML is text, never markup', () => {
  const blocks = parseMarkdown('<img src=x onerror="alert(1)">\n\n# 标题 <b>粗</b>\n\n- 一项 <script>x</script>\n\n```html\n<div>code</div>\n```');
  const json = JSON.stringify(blocks);
  const types = new Set();
  const walk = (node) => {
    if (Array.isArray(node)) { node.forEach(walk); return; }
    if (node && typeof node === 'object') {
      if (node.type) types.add(node.type);
      Object.values(node).forEach(walk);
    }
  };
  walk(blocks);
  for (const t of types) assert.ok(['paragraph', 'heading', 'list', 'code', 'text', 'strong', 'em', 'link', 'quote', 'rule'].includes(t), `只有这几种节点，没有 html：${t}`);
  assert.ok(json.includes('<img src=x onerror=\\"alert(1)\\">'), 'HTML 原样作为文字保留');
  assert.equal(blocks[1].type, 'heading');
  assert.equal(blocks[3].type, 'code');
  assert.equal(blocks[3].text, '<div>code</div>');
});

test('reader: inline markup and links are parsed; links carry their address as text', () => {
  const inline = parseInline('看 **这里** 和 `代码`，*强调*，[报告](file:///x/报告.docx)');
  assert.deepEqual(inline.map((n) => n.type), ['text', 'strong', 'text', 'code', 'text', 'em', 'text', 'link']);
  assert.equal(inline[7].href, 'file:///x/报告.docx');
  const list = parseMarkdown('1. 第一\n2. 第二\n\n- 甲\n- 乙');
  assert.equal(list[0].type, 'list');
  assert.equal(list[0].ordered, true);
  assert.equal(list[0].items.length, 2);
  assert.equal(list[1].ordered, false);
});

test('page source never assigns markup: no innerHTML, outerHTML, insertAdjacentHTML or document.write', () => {
  const files = [];
  const walk = (dir) => {
    for (const name of readdirSync(dir)) {
      const full = path.join(dir, name);
      if (statSync(full).isDirectory()) walk(full);
      else if (name.endsWith('.mjs')) files.push(full);
    }
  };
  walk(SRC);
  assert.ok(files.length >= 10, '读到了页面的全部源文件');
  for (const file of files) {
    const text = readFileSync(file, 'utf8').replace(/\/\/.*$/gm, '').replace(/\/\*[\s\S]*?\*\//g, '');
    for (const sink of ['innerHTML', 'outerHTML', 'insertAdjacentHTML', 'document.write', 'eval(', 'new Function']) {
      assert.ok(!text.includes(sink), `${path.basename(file)} 不能用 ${sink}：文件名与模型写的话一律画在 canvas 上或作为文本节点（设计稿 12.4）`);
    }
  }
});
