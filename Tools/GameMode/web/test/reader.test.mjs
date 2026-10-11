// 卷轴阅读器（M3 第三轮，用户：卷轴内容很长时看不到下方、滚动不到位，表格等 Markdown 格式支持不到位）。
// 解析是纯函数；建 DOM 的那一半用一个只认 createElement / createTextNode / append / textContent / className 的假 document 跑——
// 和页面一样不碰 innerHTML。长卷轴能不能滚到底，在冒烟测试（smoke.mjs，真浏览器）里核对。
// 运行：node --test Tools/GameMode/web/test/
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { parseMarkdown, parseInline, inlineText, renderBlocks, BLOCK_TYPES, INLINE_TYPES } from '../../../../Assets/Polis/src/reader.mjs';

/** 只有页面用到的那几样的假 document：节点是 { tag, className, children, text }。 */
function fakeDocument() {
  const node = (tag) => ({
    tag, className: '', children: [], _text: null, start: 1,
    append(...kids) { this.children.push(...kids); },
    set textContent(v) { this._text = String(v); this.children = []; },
    get textContent() { return this._text ?? this.children.map((c) => c.textContent).join(''); },
  });
  return {
    createElement: (tag) => node(tag),
    createTextNode: (text) => ({ tag: '#text', children: [], get textContent() { return text; } }),
  };
}
const find = (n, tag, out = []) => { if (n.tag === tag) out.push(n); for (const c of n.children ?? []) find(c, tag, out); return out; };

test('reader: GFM tables keep their columns, alignment and inline markup', () => {
  const [t] = parseMarkdown('| 类别 | 份数 | 金额 | 状态 |\n|:---|---:|---:|:---:|\n| 采购 | 9 | 128.50 | **已归档** |\n| 销售 | 7 | 342.00 | `待签` |\n| 缺一格 | 1 |');
  assert.equal(t.type, 'table');
  assert.deepEqual(t.align, ['left', 'right', 'right', 'center']);
  assert.deepEqual(t.head.map(inlineText), ['类别', '份数', '金额', '状态']);
  assert.equal(t.rows.length, 3);
  assert.equal(t.rows[0][3][0].type, 'strong');
  assert.equal(t.rows[1][3][0].type, 'code');
  assert.equal(t.rows[2].length, 4, 'a short row is padded to the header width');
  // 代码里的竖线、转义的竖线不切格
  const [t2] = parseMarkdown('| a | b |\n|---|---|\n| `x|y` | c\\|d |');
  assert.deepEqual(t2.rows[0].map(inlineText), ['x|y', 'c|d']);
  // 建出来的是 table / thead / tbody / th / td，对齐用 class，外面一层横向滚动的包裹
  const doc = fakeDocument(), root = doc.createElement('div');
  renderBlocks(doc, root, [t]);
  assert.equal(root.children[0].className, 'table-wrap');
  const ths = find(root, 'th'), tds = find(root, 'td');
  assert.equal(ths.length, 4);
  assert.equal(tds.length, 12);
  assert.deepEqual(ths.map((th) => th.className), ['align-left', 'align-right', 'align-right', 'align-center']);
});

test('reader: nested and task lists, ordered starts, quotes with lazy lines, setext headings, hard breaks', () => {
  const blocks = parseMarkdown('3. 第三\n   - 子一\n   - 子二\n4. 第四\n   - [x] 完成\n   - [ ] 未完成\n\n> 引用一\n引用的续行\n\n标题\n===\n\n副标题\n---\n\n一行  \n二行\\\n三行');
  const [ol, quote, h1, h2, para] = blocks;
  assert.equal(ol.type, 'list'); assert.equal(ol.ordered, true); assert.equal(ol.start, 3);
  assert.equal(ol.items.length, 2);
  assert.equal(ol.items[0].blocks[1].type, 'list', 'the sub-list belongs to its item');
  assert.deepEqual(ol.items[1].blocks[1].items.map((i) => i.task), [true, false]);
  assert.equal(quote.type, 'quote');
  assert.equal(inlineText(quote.blocks[0].inline), '引用一 引用的续行');
  assert.deepEqual([h1.type, h1.level, h2.type, h2.level], ['heading', 1, 'heading', 2]);
  assert.deepEqual(para.inline.map((n) => n.type), ['text', 'break', 'text', 'break', 'text']);
  const doc = fakeDocument(), root = doc.createElement('div');
  renderBlocks(doc, root, blocks);
  assert.equal(find(root, 'ol')[0].start, 3);
  const tasks = find(root, 'li').filter((li) => li.className.startsWith('task'));
  assert.deepEqual(tasks.map((li) => [li.className, li.children[0].textContent]), [['task done', '☑'], ['task', '☐']]);
});

test('reader: inline markup nests; underscores inside words, escapes and images stay text; bare and angle links are found', () => {
  const inline = parseInline('看 **粗里有 `代码` 和 *斜*** ~~删~~ my_file_name _强调_ \\*不是强调\\* ![示意图](a.png) <https://a.example> 和 https://b.example/x，完');
  const types = inline.map((n) => n.type);
  assert.deepEqual(types, ['text', 'strong', 'text', 'del', 'text', 'em', 'text', 'image', 'text', 'link', 'text', 'link', 'text']);
  assert.deepEqual(inline[1].children.map((n) => n.type), ['text', 'code', 'text', 'em']);
  assert.ok(inline[4].text.includes('my_file_name'), 'snake_case is not emphasis');
  assert.ok(inline[6].text.includes('*不是强调*'), 'escaped stars are literal');
  assert.equal(inline[7].alt, '示意图');
  assert.equal(inline[11].href, 'https://b.example/x', 'a bare address stops before full-width punctuation');
  // 图片不加载：建出来的是一段说明文字和地址，没有 img
  const doc = fakeDocument(), root = doc.createElement('p');
  renderBlocks(doc, root, [{ type: 'paragraph', inline }]);
  assert.equal(find(root, 'img').length, 0);
  assert.ok(root.textContent.includes('［图：示意图］ (a.png)'));
  // 每一种节点都在白名单里
  const all = parseMarkdown('# t\n\n| a |\n|---|\n| ~~b~~ |\n\n- [x] c  \nd\n\n```\ne\n```\n\n---\n\n> f ![g](h)');
  const seen = new Set();
  const walk = (n) => { if (Array.isArray(n)) n.forEach(walk); else if (n && typeof n === 'object') { if (n.type) seen.add(n.type); Object.values(n).forEach(walk); } };
  walk(all);
  for (const t of seen) assert.ok([...BLOCK_TYPES, ...INLINE_TYPES].includes(t), t);
});
