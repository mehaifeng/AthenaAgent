// 卷轴阅读器（设计稿 7.1"收"）：长回答在游戏里读，不把人赶回对话模式。
//
// 正文是模型写的，所以这里**不解释任何 HTML**（12.4）：Markdown 先解析成一棵只有几种节点的树（纯函数，Node 单测），
// 再用 createElement + textContent 一个节点一个节点地建出来——没有 innerHTML，没有 insertAdjacentHTML，
// 原文里的 <img onerror=…> 只会作为一串字显示出来。链接只显示文字和地址，不可点（打开文件走 open-file 意图）。
// 页面另有 CSP（只允许本站脚本），这是第二道防线。

/**
 * Markdown → 块级节点：heading{level, inline} / paragraph{inline} / code{lang, text} / quote{blocks} /
 * list{ordered, items:[blocks]} / rule。行内节点：text / strong / em / code / link{text, href}。
 * 只覆盖回答里常见的子集；不认识的写法按原文当段落文字。
 */
export function parseMarkdown(source) {
  const lines = String(source ?? '').replace(/\r\n?/g, '\n').split('\n');
  return parseBlocks(lines);
}

function parseBlocks(lines) {
  const blocks = [];
  let i = 0;
  while (i < lines.length) {
    const line = lines[i];
    if (/^\s*$/.test(line)) { i++; continue; }

    const fence = line.match(/^\s*(`{3,}|~{3,})\s*([\w+-]*)\s*$/);
    if (fence) {
      const close = fence[1];
      const body = [];
      i++;
      while (i < lines.length && !lines[i].trim().startsWith(close)) body.push(lines[i++]);
      i++;   // 跳过收尾的 ```（没有收尾就读到文末）
      blocks.push({ type: 'code', lang: fence[2] || '', text: body.join('\n') });
      continue;
    }

    const heading = line.match(/^\s*(#{1,6})\s+(.*?)\s*#*\s*$/);
    if (heading) {
      blocks.push({ type: 'heading', level: heading[1].length, inline: parseInline(heading[2]) });
      i++;
      continue;
    }

    if (/^\s*([-*_])(\s*\1){2,}\s*$/.test(line)) {
      blocks.push({ type: 'rule' });
      i++;
      continue;
    }

    if (/^\s*>/.test(line)) {
      const body = [];
      while (i < lines.length && /^\s*>/.test(lines[i])) body.push(lines[i++].replace(/^\s*>\s?/, ''));
      blocks.push({ type: 'quote', blocks: parseBlocks(body) });
      continue;
    }

    const bullet = line.match(/^(\s*)([-*+]|\d{1,9}[.)])\s+(.*)$/);
    if (bullet) {
      const ordered = /\d/.test(bullet[2]);
      const items = [];
      while (i < lines.length) {
        const m = lines[i].match(/^(\s*)([-*+]|\d{1,9}[.)])\s+(.*)$/);
        if (!m || /\d/.test(m[2]) !== ordered || m[1].length > bullet[1].length + 1) break;
        const body = [m[3]];
        i++;
        // 续行：缩进的、或者不是新列表项也不是空行的行，归这一项
        while (i < lines.length && lines[i].trim() !== '' && !/^(\s*)([-*+]|\d{1,9}[.)])\s+/.test(lines[i])) body.push(lines[i++].trim());
        items.push(parseBlocks(body));
      }
      blocks.push({ type: 'list', ordered, items });
      continue;
    }

    const para = [];
    while (i < lines.length && lines[i].trim() !== '' && !/^\s*(#{1,6}\s|>|(`{3,}|~{3,}))/.test(lines[i])
      && !/^(\s*)([-*+]|\d{1,9}[.)])\s+/.test(lines[i])) {
      para.push(lines[i++].trim());
    }
    if (para.length === 0) { para.push(lines[i++].trim()); }
    blocks.push({ type: 'paragraph', inline: parseInline(para.join(' ')) });
  }
  return blocks;
}

/** 行内：`code`、**strong**、*em* / _em_、[text](href)。不认识的一律是文字。 */
export function parseInline(text) {
  const out = [];
  let rest = String(text ?? '');
  const push = (node) => {
    const last = out[out.length - 1];
    if (node.type === 'text' && last?.type === 'text') last.text += node.text;
    else if (node.type !== 'text' || node.text) out.push(node);
  };
  while (rest.length > 0) {
    const m = rest.match(/`([^`]+)`|\*\*([^*]+)\*\*|__([^_]+)__|\*([^*\s][^*]*)\*|_([^_\s][^_]*)_|\[([^\]]+)\]\(([^)\s]+)\)/);
    if (!m) { push({ type: 'text', text: rest }); break; }
    push({ type: 'text', text: rest.slice(0, m.index) });
    if (m[1] != null) push({ type: 'code', text: m[1] });
    else if (m[2] != null || m[3] != null) push({ type: 'strong', text: m[2] ?? m[3] });
    else if (m[4] != null || m[5] != null) push({ type: 'em', text: m[4] ?? m[5] });
    else push({ type: 'link', text: m[6], href: m[7] });
    rest = rest.slice(m.index + m[0].length);
  }
  return out;
}

/** 把节点树建成 DOM：只用 createElement / textContent / append。 */
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
      for (const item of block.items) {
        const li = doc.createElement('li');
        renderBlocks(doc, li, item);
        el.append(li);
      }
      return el;
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
      case 'em': {
        const el = doc.createElement(node.type);
        el.textContent = node.text;
        parent.append(el);
        break;
      }
      case 'link': {
        // 链接不可点：显示文字和地址。打开文件要走 open-file 意图（C# 校验路径），不让页面自己导航。
        const el = doc.createElement('span');
        el.className = 'link';
        el.textContent = node.text;
        const href = doc.createElement('span');
        href.className = 'href';
        href.textContent = ` (${node.href})`;
        parent.append(el, href);
        break;
      }
      default:
        parent.append(doc.createTextNode(node.text));
    }
  }
}

/**
 * 打开阅读器：标题 + 正文。<paramref name="root"/> 是页面里那块（静态）阅读器容器；每次打开都把旧内容清空重建。
 * 关闭按钮的文字来自页面自己的静态词条，不来自模型。
 */
export function openReader(doc, root, { title, markdown, closeLabel = '收起' }, onClose) {
  root.replaceChildren();
  const card = doc.createElement('article');
  card.className = 'scroll';
  const head = doc.createElement('header');
  const h = doc.createElement('h1');
  h.textContent = title ?? '';
  const close = doc.createElement('button');
  close.type = 'button';
  close.textContent = closeLabel;
  close.addEventListener('click', () => { root.hidden = true; root.replaceChildren(); onClose?.(); });
  head.append(h, close);
  const body = doc.createElement('div');
  body.className = 'body';
  renderBlocks(doc, body, parseMarkdown(markdown));
  card.append(head, body);
  root.append(card);
  root.hidden = false;
}
