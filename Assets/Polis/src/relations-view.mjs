// 人际总览（第三轮，2026-10-13，用户：关系系统在游戏界面放一个入口，点击预览所有的关系状态）。DOM，与卷轴阅读器同一套规矩：
// 文字一律经 textContent 进 DOM（名字来自本地名表、原因来自本地词条，也不拼 HTML）；开着时方向键不走路（game 的 modalOpen），
// Esc 收起。左边是每个人怎么看你（半身像、名字、档、一根 −100…100 的条），点一个人，右边是原因和他合得来、合不来的人。
// 要画什么由 relations.relationsView 算（纯函数、有断言），这里只排版。
import { drawBust } from './art/busts.mjs';

const BAND_CLASS = Object.freeze({ close: 'b-close', friendly: 'b-friendly', neutral: 'b-neutral', cool: 'b-cool', hostile: 'b-hostile' });

/**
 * @param {HTMLElement} root #relations（藏着）
 * @param {{ onClose?: () => void }} [options]
 */
export function createRelationsView(root, { onClose = null } = {}) {
  const doc = root.ownerDocument;
  const el = (tag, cls, text) => {
    const e = doc.createElement(tag);
    if (cls) e.className = cls;
    if (text != null) e.textContent = String(text);
    return e;
  };
  let view = null;
  let ui = {};
  let selected = null;

  function bustCanvas(bust, h) {
    const c = el('canvas', 'rel-bust');
    const dpr = Math.min(doc.defaultView?.devicePixelRatio || 1, 2);
    const w = Math.round(h * 0.8);
    c.width = Math.round(w * dpr);
    c.height = Math.round(h * dpr);
    c.style.width = `${w}px`;
    c.style.height = `${h}px`;
    const ctx = c.getContext('2d');
    if (ctx && bust) {
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
      try { drawBust(ctx, bust, 'neutral', 0, 0, h); } catch { /* 画不出半身像不影响列表：留一块空的底 */ }
    }
    return c;
  }

  function bar(score) {
    const track = el('span', 'rel-bar');
    const fill = el('span', 'rel-fill');
    // −100…100：中线在一半处，正的往右、负的往左
    const pct = Math.max(-100, Math.min(100, score)) / 2;
    fill.style.left = pct >= 0 ? '50%' : `${50 + pct}%`;
    fill.style.width = `${Math.abs(pct)}%`;
    fill.classList.add(score >= 0 ? 'pos' : 'neg');
    track.append(fill);
    return track;
  }

  function row(r) {
    const b = el('button', `rel-row ${BAND_CLASS[r.band] ?? ''}`);
    b.type = 'button';
    b.dataset.id = r.id;
    if (r.id === selected) b.classList.add('selected');
    b.append(bustCanvas(r.bust, 40));
    const name = el('span', 'rel-name', r.name);
    if (r.indoors) name.append(el('span', 'rel-tag', ui.relationsIndoors ?? ''));
    b.append(name, el('span', 'rel-band', r.bandLabel), bar(r.score));
    b.addEventListener('click', () => { selected = r.id; render(); });
    return b;
  }

  function detail(r) {
    const box = el('div', 'rel-detail');
    if (!r) {
      box.append(el('p', 'rel-hint', ui.relationsPick ?? ''));
      return box;
    }
    const head = el('div', 'rel-head');
    head.append(bustCanvas(r.bust, 96));
    const who = el('div', 'rel-who');
    who.append(el('h2', null, r.name), el('div', `rel-band ${BAND_CLASS[r.band] ?? ''}`, `${r.bandLabel} · ${r.score > 0 ? '+' : ''}${r.score}`));
    if (r.why) who.append(el('p', 'rel-why', r.why));
    // 第四轮需求 8：他怎么看雅典娜（不是人人都敬重她）
    if (r.athena?.label) who.append(el('p', `rel-athena ${BAND_CLASS[r.athena.band] ?? ''}`, r.athena.why ? `${r.athena.label}（${r.athena.why}）` : r.athena.label));
    head.append(who);
    box.append(head);
    const list = (title, items) => {
      const sec = el('section', 'rel-ties');
      sec.append(el('h3', null, title));
      if (items.length === 0) sec.append(el('p', 'rel-none', ui.relationsNone ?? ''));
      for (const t of items) {
        const link = el('button', 'rel-link', `${t.name}（${t.score > 0 ? '+' : ''}${t.score}）`);
        link.type = 'button';
        link.addEventListener('click', () => { selected = t.id; render(); });
        sec.append(link);
      }
      return sec;
    };
    box.append(list(ui.relationsFriends ?? '', r.friends), list(ui.relationsRivals ?? '', r.rivals));
    return box;
  }

  function render() {
    root.replaceChildren();
    if (!view) return;
    const panel = el('div', 'rel-panel');
    const header = el('header');
    header.append(el('h1', null, ui.relationsTitle ?? ''));
    const close = el('button', 'rel-close', ui.close ?? '');
    close.type = 'button';
    close.addEventListener('click', () => api.close());
    header.append(close);
    const left = el('div', 'rel-list');
    left.append(el('h3', null, ui.relationsToYou ?? ''));
    const all = [...view.citizens, ...view.residents];
    if (all.length === 0) left.append(el('p', 'rel-hint', ui.relationsEmpty ?? ''));
    for (const r of view.citizens) left.append(row(r));
    if (view.residents.length) {
      left.append(el('h3', null, ui.relationsResidents ?? ''));
      for (const r of view.residents) left.append(row(r));
    }
    const body = el('div', 'rel-body');
    body.append(left, detail(all.find((r) => r.id === selected) ?? null));
    panel.append(header, body);
    root.append(panel);
  }

  const api = {
    /** 打开（或换一份内容）：{ citizens: [...], residents: [...] }（relations.relationsView）。 */
    open(next, table) {
      view = next;
      ui = table ?? {};
      if (selected && ![...view.citizens, ...view.residents].some((r) => r.id === selected)) selected = null;
      root.hidden = false;
      render();
    },
    /** 内容变了（关系推来新的一份）：开着就重画，选中的人留着。 */
    update(next, table) { if (!root.hidden) api.open(next, table); },
    close() {
      if (root.hidden) return;
      root.hidden = true;
      root.replaceChildren();
      onClose?.();
    },
    get isOpen() { return !root.hidden; },
    get selected() { return selected; },
    select(id) { selected = id; if (!root.hidden) render(); },
  };
  return api;
}
