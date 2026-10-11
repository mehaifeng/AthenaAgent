// 卷轴阅读器（设计稿 7.1"收"）：长回答在游戏里读，不把人赶回对话模式。
//
// 正文是模型写的，所以这里**不解释任何 HTML**（12.4）：Markdown 先解析成一棵只有几种节点的树（纯函数，Node 单测），
// 再用 createElement + textContent 一个节点一个节点地建出来——没有 innerHTML，没有 insertAdjacentHTML，
// 原文里的 <img onerror=…> 只会作为一串字显示出来。链接只显示文字和地址，不可点（打开文件走 open-file 意图）；
// 图片也不加载，只显示它的说明和地址。页面另有 CSP（只允许本站脚本），这是第二道防线。
//
// M3 第三轮（用户：卷轴内容很长时看不到下面、滚动不到位，表格等 Markdown 格式支持不到位）：补上 GFM 的表格（含对齐）、
// 嵌套列表、任务列表、删除线、行内的嵌套（粗体里的代码、链接里的粗体）、图片（只显示说明）、自动链接、硬换行、
// setext 标题、转义字符。对话模式用的 LiveMarkdown 支持的写法，这里都读得出来。

/** 块级节点的种类（测试按它核对：节点树里只有这些，没有 html）。 */
export const BLOCK_TYPES = Object.freeze(['heading', 'paragraph', 'code', 'quote', 'list', 'table', 'rule']);
/** 行内节点的种类。 */
export const INLINE_TYPES = Object.freeze(['text', 'strong', 'em', 'del', 'code', 'link', 'image', 'break']);

/**
 * Markdown → 块级节点：heading{level, inline} / paragraph{inline} / code{lang, text} / quote{blocks} /
 * list{ordered, start, items:[{ blocks, task: null | true | false }]} / table{align, head:[inline], rows:[[inline]]} / rule。
 * 行内节点：text{text} / strong|em|del{children} / code{text} / link{children, href} / image{alt, src} / break。
 * 不认识的写法按原文当段落文字。
 */
export function parseMarkdown(source) {
  const lines = String(source ?? '').replace(/\r\n?/g, '\n').replace(/\t/g, '    ').split('\n');
  return parseBlocks(lines);
}

const BLANK = /^\s*$/;
const FENCE = /^(\s{0,3})(`{3,}|~{3,})\s*([^`\s]*)[^`]*$/;
const ATX = /^\s{0,3}(#{1,6})(?:\s+(.*?))?\s*#*\s*$/;
const RULE = /^\s{0,3}([-*_])(\s*\1){2,}\s*$/;
const QUOTE = /^\s{0,3}>/;
const ITEM = /^(\s*)([-*+]|\d{1,9}[.)])(\s+|$)(.*)$/;
const TABLE_DELIM = /^\s*\|?\s*:?-+:?\s*(\|\s*:?-+:?\s*)*\|?\s*$/;
const indentOf = (line) => line.match(/^ */)[0].length;
const startsBlock = (line) => FENCE.test(line) || ATX.test(line) || QUOTE.test(line) || RULE.test(line) || ITEM.test(line);

function parseBlocks(lines) {
  const blocks = [];
  let i = 0;
  while (i < lines.length) {
    const line = lines[i];
    if (BLANK.test(line)) { i++; continue; }

    const fence = line.match(FENCE);
    if (fence) {
      const indent = fence[1].length, mark = fence[2];
      const body = [];
      i++;
      while (i < lines.length && !(lines[i].trim().startsWith(mark[0].repeat(mark.length)) && lines[i].trim().replace(new RegExp(`^\\${mark[0]}+`), '').trim() === '')) {
        body.push(lines[i].slice(Math.min(indent, indentOf(lines[i]))));
        i++;
      }
      i++;   // 跳过收尾的 ```（没有收尾就读到文末）
      blocks.push({ type: 'code', lang: fence[3] || '', text: body.join('\n') });
      continue;
    }

    const heading = line.match(ATX);
    if (heading) {
      blocks.push({ type: 'heading', level: heading[1].length, inline: parseInline(heading[2] ?? '') });
      i++;
      continue;
    }

    if (RULE.test(line)) {
      blocks.push({ type: 'rule' });
      i++;
      continue;
    }

    if (QUOTE.test(line)) {
      // 引用：连续的 > 行；没有 > 的段落行算"懒惰的续行"
      const body = [];
      while (i < lines.length && !BLANK.test(lines[i]) && (QUOTE.test(lines[i]) || (body.length && !startsBlock(lines[i])))) {
        body.push(lines[i].replace(/^\s{0,3}>\s?/, ''));
        i++;
      }
      blocks.push({ type: 'quote', blocks: parseBlocks(body) });
      continue;
    }

    const item = line.match(ITEM);
    if (item && (item[4] !== '' || item[3] !== '')) {
      const parsed = parseList(lines, i);
      blocks.push(parsed.block);
      i = parsed.next;
      continue;
    }

    // 表格：表头一行 + 分隔行（--- / :--- / ---: / :---:）
    if (line.includes('|') && i + 1 < lines.length && TABLE_DELIM.test(lines[i + 1]) && lines[i + 1].includes('-')) {
      const head = splitRow(line);
      const align = splitRow(lines[i + 1]).map((c) => {
        const t = c.trim();
        return t.startsWith(':') && t.endsWith(':') ? 'center' : t.endsWith(':') ? 'right' : t.startsWith(':') ? 'left' : null;
      });
      if (head.length >= 1 && align.length >= 1) {
        i += 2;
        const rows = [];
        while (i < lines.length && !BLANK.test(lines[i]) && lines[i].includes('|') && !startsBlock(lines[i])) {
          const cells = splitRow(lines[i]);
          rows.push(Array.from({ length: head.length }, (_, k) => parseInline((cells[k] ?? '').trim())));
          i++;
        }
        blocks.push({ type: 'table', align: Array.from({ length: head.length }, (_, k) => align[k] ?? null), head: head.map((c) => parseInline(c.trim())), rows });
        continue;
      }
    }

    // 段落：到空行、或者到另一种块为止；末尾两个空格或反斜杠是硬换行；下一行是 === / --- 时是 setext 标题
    const para = [];
    while (i < lines.length && !BLANK.test(lines[i]) && !(para.length && startsBlock(lines[i]) && !/^\s{0,3}(=+|-+)\s*$/.test(lines[i]))) {
      if (para.length && /^\s{0,3}(=+|-+)\s*$/.test(lines[i])) break;
      if (para.length && lines[i].includes('|') && i + 1 < lines.length && TABLE_DELIM.test(lines[i + 1]) && lines[i + 1].includes('-')) break;
      para.push(lines[i]);
      i++;
    }
    if (para.length === 0) { para.push(lines[i]); i++; }
    if (i < lines.length && /^\s{0,3}(=+|-+)\s*$/.test(lines[i]) && !RULE.test(lines[i]) || (i < lines.length && /^\s{0,3}=+\s*$/.test(lines[i]))) {
      blocks.push({ type: 'heading', level: lines[i].trim().startsWith('=') ? 1 : 2, inline: parseInline(para.map((l) => l.trim()).join(' ')) });
      i++;
      continue;
    }
    if (i < lines.length && /^\s{0,3}-{2,}\s*$/.test(lines[i]) && para.length) {
      // "文字\n---"：GFM 把它读作二级标题，不是一条分隔线
      blocks.push({ type: 'heading', level: 2, inline: parseInline(para.map((l) => l.trim()).join(' ')) });
      i++;
      continue;
    }
    const text = para.map((l, k) => {
      const hard = k < para.length - 1 && (/ {2,}$/.test(l) || /\\$/.test(l));
      return l.trim().replace(/\\$/, '') + (hard ? '\u0000' : k < para.length - 1 ? ' ' : '');
    }).join('');
    blocks.push({ type: 'paragraph', inline: parseInline(text) });
  }
  return blocks;
}

/** 一张列表：同一层的条目，条目里缩进到内容那一列的行归它（可以再嵌一层列表、代码、引用）。 */
function parseList(lines, start) {
  const first = lines[start].match(ITEM);
  const base = first[1].length;
  const ordered = /\d/.test(first[2]);
  const items = [];
  let i = start;
  while (i < lines.length) {
    const m = lines[i].match(ITEM);
    if (!m || /\d/.test(m[2]) !== ordered || m[1].length < base || m[1].length > base + 3) break;
    const contentIndent = m[1].length + m[2].length + Math.max(1, Math.min(4, m[3].length || 1));
    const body = [m[4]];
    i++;
    while (i < lines.length) {
      const l = lines[i];
      if (BLANK.test(l)) {
        // 空行之后还缩进到内容那一列的，仍属于这一项（松散列表）；否则这一项到此为止
        let j = i + 1;
        while (j < lines.length && BLANK.test(lines[j])) j++;
        if (j < lines.length && indentOf(lines[j]) >= contentIndent) { for (; i < j; i++) body.push(''); continue; }
        break;
      }
      if (indentOf(l) >= contentIndent) { body.push(l.slice(contentIndent)); i++; continue; }
      if (ITEM.test(l) || startsBlock(l)) break;
      body.push(l.trim());   // 懒惰的续行
      i++;
    }
    let task = null;
    const box = body[0].match(/^\[([ xX])\]\s+(.*)$/);
    if (box) { task = box[1] !== ' '; body[0] = box[2]; }
    items.push({ blocks: parseBlocks(body), task });
    // 同一张列表的下一项之前可以有空行
    let j = i;
    while (j < lines.length && BLANK.test(lines[j])) j++;
    const next = j < lines.length ? lines[j].match(ITEM) : null;
    if (next && /\d/.test(next[2]) === ordered && next[1].length >= base && next[1].length <= base + 3) i = j;
    else break;
  }
  return { block: { type: 'list', ordered, start: ordered ? Math.max(0, parseInt(first[2], 10)) : 1, items }, next: i };
}

/** 表格的一行按 | 切开：去掉首尾的 |，代码段里的 | 和 \| 不切。 */
function splitRow(line) {
  let s = line.trim();
  if (s.startsWith('|')) s = s.slice(1);
  if (s.endsWith('|') && !s.endsWith('\\|')) s = s.slice(0, -1);
  const cells = [];
  let cur = '', code = 0;
  for (let k = 0; k < s.length; k++) {
    const ch = s[k];
    if (ch === '\\' && s[k + 1] === '|') { cur += '|'; k++; continue; }
    if (ch === '`') code = code ? 0 : 1;
    if (ch === '|' && !code) { cells.push(cur); cur = ''; continue; }
    cur += ch;
  }
  cells.push(cur);
  return cells;
}

const INLINE = /\\([!-/:-@[-`{-~])|(`+)([\s\S]*?[^`])\2(?!`)|!\[([^\]]*)\]\(([^)\s]+)(?:\s+"[^"]*")?\)|\[((?:[^[\]]|\[[^\]]*\])+)\]\(([^)\s]+)(?:\s+"[^"]*")?\)|<((?:https?|file|mailto):[^>\s]+)>|\*\*(?=\S)([\s\S]+?)\*\*|__(?=\S)([\s\S]+?)__|~~(?=\S)([\s\S]+?)~~|\*(?=[^\s*])([\s\S]*?[^\s\\])\*|_(?=[^\s_])([\s\S]*?[^\s\\])_(?![\p{L}\p{N}])|\u0000|((?:https?):\/\/[^\s<>()（）「」，。]+[^\s<>()（）「」，。.,;:!?'"）])/u;

/**
 * 行内：转义、`代码`、**粗体** / __粗体__、*强调* / _强调_、~~删除线~~、[文字](地址)、![说明](地址)、<自动链接>、裸地址、硬换行。
 * 粗体、强调、删除线、链接的文字里还可以嵌别的行内写法。不认识的一律是文字。
 */
export function parseInline(text) {
  const out = [];
  let rest = String(text ?? '');
  const push = (node) => {
    const last = out[out.length - 1];
    if (node.type === 'text' && last?.type === 'text') last.text += node.text;
    else if (node.type !== 'text' || node.text) out.push(node);
  };
  let guard = 0;
  while (rest.length > 0 && guard++ < 10000) {
    const m = rest.match(INLINE);
    if (!m) { push({ type: 'text', text: rest }); break; }
    // 词中间的下划线不是强调（my_file_name）：GFM 的规则。不用后行断言写在正则里——旧的 WebKitGTK 不认，整页会加载失败
    if (m[13] != null && /[\p{L}\p{N}]/u.test(m.index > 0 ? rest[m.index - 1] : (out[out.length - 1]?.text ?? ' ').slice(-1))) {
      push({ type: 'text', text: rest.slice(0, m.index + 1) });
      rest = rest.slice(m.index + 1);
      continue;
    }
    push({ type: 'text', text: rest.slice(0, m.index) });
    if (m[1] != null) push({ type: 'text', text: m[1] });
    else if (m[2] != null) push({ type: 'code', text: m[3].replace(/^ (.*) $/s, '$1') });
    else if (m[4] != null) push({ type: 'image', alt: m[4], src: m[5] });
    else if (m[6] != null) push({ type: 'link', children: parseInline(m[6]), href: m[7] });
    else if (m[8] != null) push({ type: 'link', children: [{ type: 'text', text: m[8] }], href: m[8] });
    else if (m[9] != null || m[10] != null) {
      // ***斜体加粗***：粗体先在前两个星号处收住，里面留下一个没收尾的 *——后面紧跟着的那一个星号是它的
      let content = m[9] ?? m[10], used = m[0].length;
      const singles = (content.replace(/\*\*/g, '').match(/\*/g) ?? []).length;
      if (m[9] != null && singles % 2 === 1 && rest[m.index + used] === '*') { content += '*'; used += 1; }
      push({ type: 'strong', children: parseInline(content) });
      rest = rest.slice(m.index + used);
      continue;
    }
    else if (m[11] != null) push({ type: 'del', children: parseInline(m[11]) });
    else if (m[12] != null || m[13] != null) push({ type: 'em', children: parseInline(m[12] ?? m[13]) });
    else if (m[14] != null) push({ type: 'link', children: [{ type: 'text', text: m[14] }], href: m[14] });
    else push({ type: 'break' });
    rest = rest.slice(m.index + m[0].length);
  }
  return out;
}

/** 行内节点里的纯文字（测试、无障碍标签用）。 */
export function inlineText(nodes) {
  return (nodes ?? []).map((n) => (n.type === 'text' || n.type === 'code' ? n.text : n.type === 'image' ? n.alt : n.type === 'break' ? '\n' : inlineText(n.children))).join('');
}

/** 把节点树建成 DOM：只用 createElement / createTextNode / textContent / append / className。 */
export function renderBlocks(doc, container, blocks) {
  for (const block of blocks) container.append(renderBlock(doc, block));
}

function renderBlock(doc, block) {
  switch (block.type) {
    case 'heading': {
      const el = doc.createElement(`h${Math.min(6, Math.max(1, block.level))}`);
      renderInline(doc, el, block.inline);
      return el;
    }
    case 'code': {
      const pre = doc.createElement('pre');
      const code = doc.createElement('code');
      code.textContent = block.text;
      pre.append(code);
      return pre;
    }
    case 'quote': {
      const el = doc.createElement('blockquote');
      renderBlocks(doc, el, block.blocks);
      return el;
    }
    case 'list': {
      const el = doc.createElement(block.ordered ? 'ol' : 'ul');
      if (block.ordered && block.start !== 1) el.start = block.start;
      for (const item of block.items) {
        const li = doc.createElement('li');
        if (item.task != null) {
          // 任务列表：画一个方框（字符，不是可以点的 input）
          li.className = item.task ? 'task done' : 'task';
          const box = doc.createElement('span');
          box.className = 'task-box';
          box.textContent = item.task ? '☑' : '☐';
          li.append(box);
        }
        renderBlocks(doc, li, item.blocks);
        el.append(li);
      }
      return el;
    }
    case 'table': {
      // 宽表格在自己那一块里横着滚动，不把整个卷轴撑宽
      const wrap = doc.createElement('div');
      wrap.className = 'table-wrap';
      const table = doc.createElement('table');
      const thead = doc.createElement('thead');
      const hr = doc.createElement('tr');
      block.head.forEach((cell, k) => {
        const th = doc.createElement('th');
        if (block.align[k]) th.className = `align-${block.align[k]}`;
        renderInline(doc, th, cell);
        hr.append(th);
      });
      thead.append(hr);
      const tbody = doc.createElement('tbody');
      for (const row of block.rows) {
        const tr = doc.createElement('tr');
        row.forEach((cell, k) => {
          const td = doc.createElement('td');
          if (block.align[k]) td.className = `align-${block.align[k]}`;
          renderInline(doc, td, cell);
          tr.append(td);
        });
        tbody.append(tr);
      }
      table.append(thead, tbody);
      wrap.append(table);
      return wrap;
    }
    case 'rule':
      return doc.createElement('hr');
    default: {
      const p = doc.createElement('p');
      renderInline(doc, p, block.inline ?? []);
      return p;
    }
  }
}

function renderInline(doc, parent, nodes) {
  for (const node of nodes) {
    switch (node.type) {
      case 'code': {
        const el = doc.createElement('code');
        el.textContent = node.text;
        parent.append(el);
        break;
      }
      case 'strong':
      case 'em':
      case 'del': {
        const el = doc.createElement(node.type);
        renderInline(doc, el, node.children ?? []);
        parent.append(el);
        break;
      }
      case 'link': {
        // 链接不可点：显示文字和地址。打开文件要走 open-file 意图（C# 校验路径），不让页面自己导航。
        const el = doc.createElement('span');
        el.className = 'link';
        renderInline(doc, el, node.children ?? []);
        parent.append(el);
        const text = inlineText(node.children);
        if (text !== node.href) {
          const href = doc.createElement('span');
          href.className = 'href';
          href.textContent = ` (${node.href})`;
          parent.append(href);
        }
        break;
      }
      case 'image': {
        // 图片不加载（不碰网络、不读文件）：显示它的说明与地址
        const el = doc.createElement('span');
        el.className = 'image';
        el.textContent = node.alt ? `［图：${node.alt}］` : '［图］';
        const href = doc.createElement('span');
        href.className = 'href';
        href.textContent = ` (${node.src})`;
        parent.append(el, href);
        break;
      }
      case 'break':
        parent.append(doc.createElement('br'));
        break;
      default:
        parent.append(doc.createTextNode(node.text));
    }
  }
}

/**
 * 打开阅读器：标题 + 正文。<paramref name="root"/> 是页面里那块（静态）阅读器容器；每次打开都把旧内容清空重建。
 * 关闭按钮的文字来自页面自己的静态词条，不来自模型。正文那一块拿键盘焦点：PageDown / 空格 / 方向键直接滚动。
 */
export function openReader(doc, root, { title, markdown, closeLabel = '收起' }, onClose) {
  root.replaceChildren();
  const card = doc.createElement('article');
  card.className = 'scroll';
  const head = doc.createElement('header');
  const h = doc.createElement('h1');
  h.textContent = title ?? '';
  h.title = title ?? '';
  const close = doc.createElement('button');
  close.type = 'button';
  close.textContent = closeLabel;
  close.addEventListener('click', () => { root.hidden = true; root.replaceChildren(); onClose?.(); });
  head.append(h, close);
  const body = doc.createElement('div');
  body.className = 'body';
  body.tabIndex = 0;
  renderBlocks(doc, body, parseMarkdown(markdown));
  card.append(head, body);
  root.append(card);
  root.hidden = false;
  body.focus?.({ preventScroll: true });
}
