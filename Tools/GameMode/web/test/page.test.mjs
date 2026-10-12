// 页面的纯逻辑：帧率策略与航海过场（frame-policy.mjs）、意图的封闭集合（intents.mjs）、卷轴阅读器的"不解释 HTML"、
// 以及整个页面源码里不出现任何把字符串当标记写进去的地方。原来是 live.test.mjs（M1）；直播拿掉之后（2026-10-13）
// 实时事件流和编排器的"等审批"连同它们的用例一起删了。
// 运行：node --test Tools/GameMode/web/test/
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, readdirSync, statSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import { framePolicy, transitionArrival, TRANSITION, FRAME } from '../../../../Assets/Polis/src/frame-policy.mjs';
import { intent, INTENT_TYPES } from '../../../../Assets/Polis/src/intents.mjs';
import { parseMarkdown, parseInline, BLOCK_TYPES, INLINE_TYPES } from '../../../../Assets/Polis/src/reader.mjs';

const SRC = fileURLToPath(new URL('../../../../Assets/Polis/src/', import.meta.url));

test('frame policy: hidden stops, Athena walking or teleporting is 60, everything else idle is 30', () => {
  assert.equal(framePolicy({ visible: false, step: 'walk' }), 0, '不可见时停（对话模式、窗口最小化）');
  assert.equal(framePolicy({ visible: true, step: 'walk' }), FRAME.busyFps);
  assert.equal(framePolicy({ visible: true, step: 'teleport' }), FRAME.busyFps);
  assert.equal(framePolicy({ visible: true, step: 'idle', now: 10_000 }), FRAME.idleFps, '她待着是空闲');
  assert.equal(framePolicy({ visible: true, step: 'idle', transitioning: true }), FRAME.busyFps, '过场在动');
  assert.equal(framePolicy({ visible: true, step: 'idle', now: 1000, lastInputAt: 500 }), FRAME.busyFps, '刚拖过镜头');
  assert.equal(framePolicy({ visible: true, step: 'idle', playerMoving: true, now: 10_000 }), FRAME.busyFps, '你在走');
  for (const old of ['act', 'meditate', 'await', 'deliver']) {
    assert.equal(framePolicy({ visible: true, step: old, now: 10_000 }), FRAME.idleFps, `旧编排器的 ${old} 已经没有了，不算在动`);
  }
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
  for (const t of types) assert.ok([...BLOCK_TYPES, ...INLINE_TYPES].includes(t), `只有这几种节点，没有 html：${t}`);
  assert.ok(!types.has('html'));
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

function pageSources() {
  const files = [];
  const walk = (dir) => {
    for (const name of readdirSync(dir)) {
      const full = path.join(dir, name);
      if (statSync(full).isDirectory()) walk(full);
      else if (name.endsWith('.mjs')) files.push(full);
    }
  };
  walk(SRC);
  return files;
}

test('page source never assigns markup: no innerHTML, outerHTML, insertAdjacentHTML or document.write', () => {
  const files = pageSources();
  assert.ok(files.length >= 10, '读到了页面的全部源文件');
  for (const file of files) {
    const text = readFileSync(file, 'utf8').replace(/\/\/.*$/gm, '').replace(/\/\*[\s\S]*?\*\//g, '');
    for (const sink of ['innerHTML', 'outerHTML', 'insertAdjacentHTML', 'document.write', 'eval(', 'new Function']) {
      assert.ok(!text.includes(sink), `${path.basename(file)} 不能用 ${sink}：文件名与模型写的话一律画在 canvas 上或作为文本节点（设计稿 12.4）`);
    }
  }
});

test('the live mirror is gone: no page module imports the orchestrator, the replay, the narration or the live feed', () => {
  const names = new Set(pageSources().map((f) => path.basename(f)));
  for (const gone of ['orchestrator.mjs', 'replay.mjs', 'narration.mjs', 'live.mjs']) {
    assert.ok(!names.has(gone), `${gone} 已随直播拿掉（2026-10-13）`);
  }
  for (const file of pageSources()) {
    const text = readFileSync(file, 'utf8');
    assert.ok(!/from '\.\/(orchestrator|replay|narration|live)\.mjs'/.test(text), `${path.basename(file)} 不该再引用直播的模块`);
    assert.ok(!/['"](tool-start|tool-end|think)['"]/.test(text), `${path.basename(file)} 不该再认工具 / 推理事件`);
  }
});
