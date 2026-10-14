// 2D 叠加层：旁白气泡、建筑名牌、状态行、屏幕边缘的"轮到你了"指示，以及 M1 的几块面板——
// 传令官的"你离开期间"报告、广场公告板（外部改动）、雾（找不到文件夹）、成果卡片、航海过场。
// 文件名和雅典娜说的话一律画在 canvas 上，不放进 innerHTML（设计稿 12.4）：
// 一个名叫 <img onerror=…> 的文件，放进 innerHTML 就会在一个能调用 C# 的页面里变成脚本。
// 面板上的按钮也画在这里：draw() 返回这一帧的可点区域，页面按坐标命中，不建任何 DOM。
// M2：对话面板（居民、委托板、神谕、场馆）左边画说话人的 2D 半身像（busts.mjs，白底陶瓶的线描）；
// 传令官的报告每一行可点；摆放时顶上一条提示；群岛海图是整屏的一张 2D 图。全部画在 canvas 上。
import { drawBust } from './art/busts.mjs';
import { drawVoyage, VOYAGE_PALETTES } from './voyage.mjs';

/** 头顶的话最多几行（再多最后一行带省略号）。 */
export const BUBBLE_LINES = 5;

const FONT = '"PingFang SC", "Hiragino Sans GB", "Noto Sans CJK SC", "Microsoft YaHei", sans-serif';
const ACCENT = '#b9572f';

/** 叠加层的颜色：白天一套、夜里一套（setNight；跟着应用的深 / 浅色主题）。纸色的面板夜里换成暗色的羊皮纸，字反过来。 */
export const OVERLAY_PALETTES = Object.freeze({
  day: Object.freeze({
    accent: ACCENT, button: 'rgba(40,37,34,0.86)', buttonText: '#fbf8f3', ink: '#26221f', inkSoft: '#4a433d', inkFaint: '#6b625a',
    paper: 'rgba(250,247,241,0.97)', paperEdge: 'rgba(60,55,50,0.25)', label: 'rgba(250,248,244,0.82)', labelInk: '#2b2724',
    labelDark: 'rgba(38,35,32,0.78)', labelDarkInk: '#f3efe8', tree: 'rgba(46,52,40,0.8)', bubble: 'rgba(252,250,246,0.95)', bubbleEdge: 'rgba(60,55,50,0.35)', bubbleInk: '#26221f',
    chartSea: '#d9d4ca', chartContour: 'rgba(80,72,64,0.12)', chartIsle: '#e7e1d5', chartSanctuary: '#efe9de', chartMissing: 'rgba(200,196,188,0.85)', chartInk: '#3a322c', chartBoat: '#2f2b28',
    fogInner: 'rgba(232,229,224,0.78)', fogOuter: 'rgba(214,210,203,0.95)',
  }),
  night: Object.freeze({
    accent: '#c8653a', button: 'rgba(214,204,186,0.9)', buttonText: '#1d1b19', ink: '#ece5d6', inkSoft: '#c9c0b0', inkFaint: '#a39a8c',
    paper: 'rgba(36,34,32,0.95)', paperEdge: 'rgba(236,229,214,0.18)', label: 'rgba(40,38,35,0.84)', labelInk: '#ece5d6',
    labelDark: 'rgba(222,214,198,0.86)', labelDarkInk: '#1d1b19', tree: 'rgba(196,204,180,0.84)', bubble: 'rgba(42,40,37,0.95)', bubbleEdge: 'rgba(236,229,214,0.25)', bubbleInk: '#ece5d6',
    chartSea: '#14171b', chartContour: 'rgba(170,182,196,0.1)', chartIsle: '#2b2a27', chartSanctuary: '#35322d', chartMissing: 'rgba(60,58,54,0.85)', chartInk: '#c9c0b0', chartBoat: '#ece5d6',
    fogInner: 'rgba(24,27,31,0.8)', fogOuter: 'rgba(14,16,19,0.95)',
  }),
});

/** 不放在行首的标点（避头）。 */
const NO_LINE_START = '。，、；：！？）」』”’》〉．,.;:!?)]';

export function createOverlay(canvas) {
  const ctx = canvas.getContext('2d');
  let dpr = 1;
  let regions = [];
  let palette = OVERLAY_PALETTES.day;
  let night = false;

  function resize() {
    dpr = Math.min(window.devicePixelRatio || 1, 2);
    canvas.width = Math.round(window.innerWidth * dpr);
    canvas.height = Math.round(window.innerHeight * dpr);
    canvas.style.width = `${window.innerWidth}px`;
    canvas.style.height = `${window.innerHeight}px`;
  }
  resize();

  function roundRect(x, y, w, h, r) {
    ctx.beginPath();
    ctx.moveTo(x + r, y);
    ctx.arcTo(x + w, y, x + w, y + h, r);
    ctx.arcTo(x + w, y + h, x, y + h, r);
    ctx.arcTo(x, y + h, x, y, r);
    ctx.arcTo(x, y, x + w, y, r);
    ctx.closePath();
  }

  function label({ text, x, y, kind, flash = 0, unmet = false, keyCap = 'E' }) {
    if (kind === 'prompt') { prompt(text, x, y, keyCap); return; }
    const size = kind === 'public' ? 13 : kind === 'resident' || kind === 'tree' || kind === 'lost' ? 11.5 : 12;
    ctx.font = `${kind === 'public' || kind === 'athena' ? 600 : 500} ${size}px ${FONT}`;
    ctx.textAlign = 'center';
    ctx.textBaseline = 'middle';
    // 还没结识的居民：名字后面一个省略号（他有话要说——邀请，不是"轮到你了"，所以不带点缀色，也不用彩色的表情符号）
    const shown = kind === 'resident' && unmet ? `${text} …` : kind === 'lost' ? `▢ ${text}` : text;
    const w = ctx.measureText(shown).width + 12;
    ctx.globalAlpha = 0.86;
    ctx.fillStyle = kind === 'public' ? palette.labelDark : kind === 'tree' ? palette.tree : palette.label;
    roundRect(x - w / 2, y - 10, w, 20, 4);
    ctx.fill();
    if (kind === 'lost') {
      ctx.globalAlpha = 0.7;
      ctx.setLineDash([3, 3]);
      ctx.strokeStyle = palette.labelInk;
      ctx.lineWidth = 1;
      ctx.stroke();
      ctx.setLineDash([]);
    }
    if (flash > 0) {
      // 外部改动：名牌轻轻闪一下（没有光、没有角色动作——光属于雅典娜）
      ctx.globalAlpha = 0.5 * flash;
      ctx.strokeStyle = palette.labelInk;
      ctx.lineWidth = 1.5;
      ctx.stroke();
    }
    ctx.globalAlpha = 1;
    ctx.fillStyle = kind === 'public' ? palette.labelDarkInk : kind === 'tree' ? (palette === OVERLAY_PALETTES.day ? '#f3efe8' : '#1d1b19') : palette.labelInk;
    ctx.fillText(shown, x, y + 0.5);
  }

  /**
   * 走近按 E 的提示（M3 第三轮）：画在那一样东西脚下——一枚键帽、一行说明。不用点缀色（点缀色只给"轮到你了"）。
   * (x, y) 是脚下那一点按当帧矩阵投影到屏幕上的位置；整块往下挪 14 像素，不压在人的脚上。
   */
  function prompt(text, x, y, keyCap) {
    ctx.save();
    ctx.font = `600 12.5px ${FONT}`;
    const tw = ctx.measureText(text).width;
    const cap = 20, gap = 7, padX = 6;
    const w = padX + cap + gap + tw + 10, h = 26;
    const bx = Math.round(x - w / 2), by = Math.round(y + 14);
    ctx.globalAlpha = 0.93;
    ctx.fillStyle = palette.paper;
    ctx.strokeStyle = palette.paperEdge;
    ctx.lineWidth = 1;
    roundRect(bx, by, w, h, 13);
    ctx.fill();
    ctx.stroke();
    ctx.globalAlpha = 1;
    // 键帽：深底浅字，一眼认得出是"按这个键"
    ctx.fillStyle = palette.button;
    roundRect(bx + padX, by + 3, cap, cap, 5);
    ctx.fill();
    ctx.fillStyle = palette.buttonText;
    ctx.textAlign = 'center';
    ctx.textBaseline = 'middle';
    ctx.font = `700 12px ${FONT}`;
    ctx.fillText(keyCap, bx + padX + cap / 2, by + h / 2 + 0.5);
    ctx.fillStyle = palette.ink;
    ctx.textAlign = 'left';
    ctx.font = `600 12.5px ${FONT}`;
    ctx.fillText(text, bx + padX + cap + gap, by + h / 2 + 0.5);
    ctx.restore();
  }

  /**
   * 头顶的话：一行放得下就是一行；放不下折成几行（最多 BUBBLE_LINES 行，再多的最后一行带省略号——提示词要一句话不超过 80 字，
   * 很少用到）。还在想（typing）时字是一串循环的点，气泡宽度按三个点定，点数变了气泡不跳。锚点 (x, y) 是尾巴尖，运动自检逐帧核对它。
   */
  function bubble({ text, x, y, alpha, typing = false }) {
    ctx.font = `500 15px ${FONT}`;
    ctx.textAlign = 'center';
    ctx.textBaseline = 'middle';
    const maxText = 300;
    let lines = typing ? [String(text ?? '')] : wrap(text, maxText, BUBBLE_LINES + 1);
    if (lines.length > BUBBLE_LINES) {
      lines = lines.slice(0, BUBBLE_LINES);
      lines[BUBBLE_LINES - 1] = `${lines[BUBBLE_LINES - 1].slice(0, -1)}…`;
    }
    const widest = typing ? ctx.measureText('···').width : Math.max(...lines.map((l) => ctx.measureText(l).width));
    const w = Math.min(widest + 24, maxText + 24);
    const lineH = 21;
    const h = 30 + (lines.length - 1) * lineH;
    const bx = x - w / 2, by = y - h - 12;
    ctx.globalAlpha = alpha;
    ctx.fillStyle = palette.bubble;
    ctx.strokeStyle = palette.bubbleEdge;
    ctx.lineWidth = 1;
    roundRect(bx, by, w, h, 8);
    ctx.fill();
    ctx.stroke();
    ctx.beginPath();
    ctx.moveTo(x - 6, by + h - 0.5);
    ctx.lineTo(x, y - 3);
    ctx.lineTo(x + 6, by + h - 0.5);
    ctx.closePath();
    ctx.fill();
    ctx.fillStyle = palette.bubbleInk;
    lines.forEach((line, k) => ctx.fillText(line, x, by + 15 + k * lineH + 0.5, w - 16));
    ctx.globalAlpha = 1;
  }

  // "轮到你了"：屏幕外有一卷等你收下的成果时，在它所在方向的屏幕边缘画一个红色三角。
  // 色弱用户看红色和深灰差别不大，所以同时带亮度对比（白边）、形状（三角里一个感叹号）和脉动（设计稿 8 节）。
  function edgeMarker({ x, y }, t) {
    const W = window.innerWidth, H = window.innerHeight, m = 26;
    const cx = W / 2, cy = H / 2;
    const dx = x - cx, dy = y - cy;
    const k = Math.min((W / 2 - m) / Math.max(1e-6, Math.abs(dx)), (H / 2 - m) / Math.max(1e-6, Math.abs(dy)));
    const px = cx + dx * k, py = cy + dy * k;
    const angle = Math.atan2(dy, dx);
    const pulse = 1 + 0.18 * Math.sin(t / 220);
    ctx.save();
    ctx.translate(px, py);
    ctx.rotate(angle);
    ctx.scale(pulse, pulse);
    ctx.beginPath();
    ctx.moveTo(16, 0); ctx.lineTo(-10, -13); ctx.lineTo(-10, 13); ctx.closePath();
    ctx.fillStyle = ACCENT;
    ctx.strokeStyle = '#fff';
    ctx.lineWidth = 2.5;
    ctx.fill();
    ctx.stroke();
    ctx.rotate(-angle);
    ctx.fillStyle = '#fff';
    ctx.font = `700 13px ${FONT}`;
    ctx.textAlign = 'center';
    ctx.textBaseline = 'middle';
    ctx.fillText('!', 0, 1);
    ctx.restore();
  }

  /**
   * 雅典娜在画面外（自由视角下的常态）：屏幕边缘一枚白色的圆标，尖角指着她——光属于雅典娜，所以是白的、不用点缀色；
   * 她在干活时一明一暗。点它镜头就跟过去（follow-athena）。
   */
  function athenaMarker({ x, y, busy, label: text }, t) {
    const W = window.innerWidth, H = window.innerHeight, m = 30;
    const cx = W / 2, cy = H / 2;
    const dx = x - cx, dy = y - cy;
    const k = Math.min((W / 2 - m) / Math.max(1e-6, Math.abs(dx)), (H / 2 - m) / Math.max(1e-6, Math.abs(dy)));
    const px = cx + dx * k, py = cy + dy * k;
    const angle = Math.atan2(dy, dx);
    const glow = busy ? 0.55 + 0.35 * Math.sin(t / 380) : 0.45;
    ctx.save();
    const g = ctx.createRadialGradient(px, py, 2, px, py, 26);
    g.addColorStop(0, `rgba(255,252,240,${glow})`);
    g.addColorStop(1, 'rgba(255,252,240,0)');
    ctx.fillStyle = g;
    ctx.beginPath(); ctx.arc(px, py, 26, 0, Math.PI * 2); ctx.fill();
    ctx.translate(px, py);
    ctx.rotate(angle);
    ctx.beginPath();
    ctx.moveTo(19, 0); ctx.lineTo(8, -7); ctx.lineTo(8, 7); ctx.closePath();
    ctx.fillStyle = '#2b2724';
    ctx.fill();
    ctx.rotate(-angle);
    ctx.beginPath(); ctx.arc(0, 0, 10, 0, Math.PI * 2);
    ctx.fillStyle = '#fbf8f1';
    ctx.strokeStyle = '#2b2724';
    ctx.lineWidth = 1.5;
    ctx.fill();
    ctx.stroke();
    // 圆里一个小小的人形：她在那边
    ctx.fillStyle = '#2b2724';
    ctx.beginPath(); ctx.arc(0, -3.6, 2.2, 0, Math.PI * 2); ctx.fill();
    ctx.beginPath(); ctx.moveTo(-3.8, 6); ctx.lineTo(0, -1); ctx.lineTo(3.8, 6); ctx.closePath(); ctx.fill();
    ctx.restore();
    if (text) {
      ctx.font = `500 11.5px ${FONT}`;
      ctx.textAlign = 'center';
      ctx.textBaseline = 'middle';
      const tw = ctx.measureText(text).width + 12;
      const lx = Math.min(W - tw / 2 - 4, Math.max(tw / 2 + 4, px - Math.cos(angle) * 34));
      const ly = Math.min(H - 14, Math.max(14, py - Math.sin(angle) * 26));
      ctx.globalAlpha = 0.86;
      ctx.fillStyle = palette.label;
      roundRect(lx - tw / 2, ly - 9, tw, 18, 4);
      ctx.fill();
      ctx.globalAlpha = 1;
      ctx.fillStyle = palette.labelInk;
      ctx.fillText(text, lx, ly + 0.5);
    }
    regions.push({ id: 'follow-athena', x: px - 20, y: py - 20, w: 40, h: 40 });
  }

  function hud(lines) {
    ctx.font = `500 12.5px ${FONT}`;
    ctx.textAlign = 'left';
    ctx.textBaseline = 'top';
    let y = 12;
    const w = Math.max(...lines.map((l) => ctx.measureText(l).width)) + 20;
    ctx.fillStyle = 'rgba(30,28,26,0.55)';
    roundRect(10, 8, w, lines.length * 19 + 10, 6);
    ctx.fill();
    ctx.fillStyle = '#f4f1ec';
    for (const l of lines) { ctx.fillText(l, 20, y + 3); y += 19; }
  }

  /**
   * 面板上的一个按钮：画出来并登记可点区域。primary 的按钮带点缀色（只给"轮到你了"的那一个动作）。
   * 画完把 canvas 的状态还原（save / restore）：第一版把对齐方式留成了居中，委托板上第二条起的建议全都以正文的左边线为中心画，
   * 一半伸到半身像上、伸出面板（用户截图：告示排版不齐，文字脱离告示的显示区域）。
   */
  function button(text, x, y, id, { primary = false, minWidth = 84 } = {}) {
    ctx.save();
    ctx.font = `600 13px ${FONT}`;
    const w = Math.max(minWidth, ctx.measureText(text).width + 26);
    const h = 30;
    ctx.fillStyle = primary ? palette.accent : palette.button;
    roundRect(x, y, w, h, 6);
    ctx.fill();
    if (primary) {
      ctx.strokeStyle = '#fff';
      ctx.lineWidth = 1.5;
      ctx.stroke();
    }
    ctx.fillStyle = palette.buttonText;
    ctx.textAlign = 'center';
    ctx.textBaseline = 'middle';
    ctx.fillText(text, x + w / 2, y + h / 2 + 0.5);
    ctx.restore();
    regions.push({ id, x, y, w, h });
    return w;
  }

  /**
   * 一块纸色面板：标题 + 若干行正文（+ 底部留给按钮的一行），返回底边。长行折成两行（再长的截断），不把字压扁
   * （第一版靠 fillText 的最大宽度把长行压窄，公告板上一行长的外部改动挤成一条）。footer：按钮行的高度——按钮画在正文之下，不压字。
   */
  function panel(x, y, w, title, lines, { maxLines = 8, footer = 0, wrapEach = 2 } = {}) {
    ctx.font = `400 13px ${FONT}`;
    const shown = lines.flatMap((l) => wrap(l, w - 32, wrapEach)).slice(0, maxLines);
    const h = 22 + 24 + shown.length * 20 + 12 + footer;
    ctx.fillStyle = palette.paper;
    ctx.strokeStyle = palette.paperEdge;
    ctx.lineWidth = 1;
    roundRect(x, y, w, h, 10);
    ctx.fill();
    ctx.stroke();
    ctx.fillStyle = palette.ink;
    ctx.textAlign = 'left';
    ctx.textBaseline = 'top';
    ctx.font = `600 15px ${FONT}`;
    ctx.fillText(title, x + 16, y + 14, w - 32);
    ctx.font = `400 13px ${FONT}`;
    ctx.fillStyle = palette.inkSoft;
    let ly = y + 44;
    for (const line of shown) {
      ctx.fillText(line, x + 16, ly, w - 32);
      ly += 20;
    }
    return { bottom: y + h, height: h };
  }

  /**
   * 传令官："你离开期间"——定时任务的结果、被打断的委托、建筑的增删改名、藏品的找回与遗失（设计稿 7.3、11.3）。
   * 左边是他的半身像；带会话的那几行可点（选中那次会话），行尾一个小箭头。点"知道了"收起。
   */
  function herald(report, ui, top = 0) {
    const w = Math.min(600, window.innerWidth - 40);
    const x = (window.innerWidth - w) / 2;
    const bustW = 84;
    const textX = x + 16 + bustW, textW = w - bustW - 56;
    ctx.font = `400 13px ${FONT}`;
    // 每一行最多折成两行；整块最多 9 行字（第一版一行一条、长的压扁）
    const rows = [];
    for (const [i, line] of report.lines.entries()) {
      const parts = wrap(line.text, textW, 2);
      if (rows.length + parts.length > 9) break;
      rows.push({ i, parts, clickable: !!(line.conversationId || line.itemId) });
    }
    const textH = rows.reduce((n, r) => n + r.parts.length * 19 + 4, 0);
    const h = Math.max(150, 22 + 24 + textH + 12 + 40);
    const y0 = 18 + top;
    paper(x, y0, w, h);
    drawBust(ctx, 'herald', 'speak', x + 12, y0 + 8, 112);
    ctx.fillStyle = palette.ink;
    ctx.textAlign = 'left';
    ctx.textBaseline = 'top';
    ctx.font = `600 15px ${FONT}`;
    ctx.fillText(report.title, textX, y0 + 14, w - bustW - 32);
    ctx.font = `400 13px ${FONT}`;
    let ly = y0 + 44;
    for (const r of rows) {
      ctx.fillStyle = r.clickable ? palette.ink : palette.inkSoft;
      r.parts.forEach((part, k) => ctx.fillText(part, textX, ly + k * 19, textW));
      if (r.clickable) {
        ctx.fillText('›', x + w - 30, ly);
        regions.push({ id: `herald-line:${r.i}`, x: x + 12 + bustW, y: ly - 3, w: w - bustW - 24, h: r.parts.length * 19 + 2 });
      }
      ly += r.parts.length * 19 + 4;
    }
    button(ui.dismiss, x + w - 16 - 84, y0 + h - 44, 'herald-dismiss');
  }

  /** 一块纸色面板的底（没有标题与正文，给自己排版的面板用）。 */
  function paper(x, y, w, h) {
    ctx.fillStyle = palette.paper;
    ctx.strokeStyle = palette.paperEdge;
    ctx.lineWidth = 1;
    roundRect(x, y, w, h, 10);
    ctx.fill();
    ctx.stroke();
  }

  /**
   * 把一段话按宽度折成几行（面板里的说明文字；名字与模型写的话一样画在 canvas 上）。
   * 句末与收尾的标点不放到行首（避头）：多出半个字宽也挤在上一行末尾——辩论牌上一行只剩一个"。"的截图审查。
   */
  function wrap(text, maxWidth, maxLines = 4) {
    const out = [];
    let line = '';
    for (const ch of String(text ?? '')) {
      if (ctx.measureText(line + ch).width > maxWidth && line && !NO_LINE_START.includes(ch)) {
        out.push(line);
        line = '';
        if (out.length === maxLines) break;
      }
      line += ch;
    }
    if (line && out.length < maxLines) out.push(line);
    return out;
  }

  /**
   * 对话面板（居民、委托板、神谕、场馆）：左边说话人的半身像，右边标题与正文，下面一排按钮。
   * 面板固定在画面底部中间，和成果卡片在同一处——同一时间只开一块。委托板的建议、场馆里收着的藏品一条一行（太长折成两行，
   * 不再把字压扁），按钮在那一行的右边、竖直居中。所有的字都左对齐、顶对齐，画在正文那一栏里。
   */
  function dialog(view, ui) {
    const W = window.innerWidth, H = window.innerHeight;
    const w = Math.min(620, W - 40);
    const x = (W - w) / 2;
    const bustH = 132, bustW = bustH * 0.8;
    ctx.font = `400 13px ${FONT}`;
    const textX = x + 22 + bustW, textW = w - bustW - 40;
    const body = [];
    for (const l of view.lines ?? []) body.push(...wrap(l, textW, 4));
    if (view.hint) body.push(...wrap(view.hint, textW, 2));
    // 每一行条目：左边一段折好的字，右边一个按钮
    const rowButtonW = view.type === 'board' ? 110 : 100;
    const rowTextW = textW - rowButtonW - 14;
    const rows = view.type === 'board'
      ? view.suggestions.map((s, i) => ({ lines: wrap(`· ${s.line}`, rowTextW, 2), label: ui.takeSuggestion, id: `suggestion:${i}` }))
      : view.type === 'venue'
        ? view.stored.map((item) => ({ lines: wrap(item.title, rowTextW, 2), label: view.placeHere, id: `venue-place:${item.id}` }))
        // 敲门之后（第三轮需求 3）：屋里的人一位一行（名字 · 他对你的态度），右边"找这位"
        : view.type === 'door' && view.people
          ? view.people.map((p) => ({ lines: wrap(p.line, rowTextW, 2), label: ui.doorPick, id: `door-pick:${p.id}` }))
          : [];
    const rowH = (r) => Math.max(36, r.lines.length * 18 + 16);
    const rowsH = rows.reduce((sum, r) => sum + rowH(r), 0) + (view.type === 'venue' ? 30 : 0);
    const h = Math.max(bustH + 28, 48 + body.length * 20 + rowsH + 56);
    const y = H - h - 18;
    paper(x, y, w, h);
    drawBust(ctx, view.bust, view.expression ?? 'neutral', x + 12, y + 14, bustH);
    const text = (str, tx, ty, maxW, font, color) => {
      ctx.font = font;
      ctx.fillStyle = color;
      ctx.textAlign = 'left';
      ctx.textBaseline = 'top';
      ctx.fillText(str, tx, ty, maxW);
    };
    text(view.title, textX, y + 16, textW, `600 15px ${FONT}`, palette.ink);
    let ly = y + 44;
    for (const l of body) { text(l, textX, ly, textW, `400 13px ${FONT}`, palette.inkSoft); ly += 20; }
    if (view.type === 'venue') {
      text(view.stored.length ? view.storedTitle : view.empty, textX, ly + 6, textW, `600 13px ${FONT}`, palette.ink);
      ly += 30;
    }
    for (const r of rows) {
      const rh = rowH(r);
      const textTop = ly + (rh - r.lines.length * 18) / 2;
      r.lines.forEach((line, k) => text(line, textX, textTop + k * 18, rowTextW, `400 13px ${FONT}`, palette.ink));
      button(r.label, x + w - 18 - rowButtonW, ly + (rh - 30) / 2, r.id, { minWidth: rowButtonW });
      ly += rh;
    }
    const by = y + h - 44;
    let bx = textX;
    // 职业居民（2026-10-13）：聊几句（工具能力撤下了，没有"请雅典娜试试"）
    if (view.type === 'resident' && view.askable) bx += button(ui.citizenChat, bx, by, 'resident-chat', { minWidth: 90 }) + 8;
    // 居民即会话（M-B）：聊几句（对话栏借给他，他的话在他头顶）、看看那场会话。
    // "去看那份委托"拿掉了（2026-10-13 第三轮，用户）：卡片是和这个人打交道的地方，选会话走会话树
    if (view.type === 'citizen') {
      if (view.askable) bx += button(ui.citizenChat, bx, by, 'citizen-chat', { minWidth: 90 }) + 8;
    }
    // 讲台（M-E）：提一个议题（对话栏借给辩论）
    if (view.type === 'bema') bx += button(ui.bemaPropose, bx, by, 'bema-propose', { minWidth: 110 }) + 8;
    // 门里的那一位应了（第三轮需求 3）：请他出来、隔着门说话；"算了"在右边
    if (view.type === 'door' && view.picked) {
      bx += button(ui.doorCall, bx, by, 'door-call', { minWidth: 100 }) + 8;
      if (view.canTalk) bx += button(ui.doorTalk, bx, by, 'door-talk', { minWidth: 110 }) + 8;
    }
    if (view.type === 'oracle') {
      if (view.exists && view.latest) bx += button(ui.latestScroll, bx, by, 'oracle-latest') + 8;
      if (!view.exists) bx += button(ui.requestOracle, bx, by, 'oracle-request', { minWidth: 120 }) + 8;
    }
    button(view.type === 'door' ? ui.doorLeave : view.type === 'resident' || view.type === 'citizen' || view.type === 'bema' ? ui.dismiss : ui.close, x + w - 16 - 84, by, 'panel-close');
  }

  /**
   * 顶上的辩论牌（M-E）：议题、第几轮（或在想 / 在商议）、最近的一来一回（谁说的 + 折成最多三行）、裁决（谁赢、票数、一句理由）、
   * 战绩，右上角"结束"。不挡对话栏：它在顶上，栏在底下，同时都在。
   */
  function debateBoard(view, ui, top = 0) {
    const W = window.innerWidth;
    const w = Math.min(620, W - 40);
    const x = (W - w) / 2;
    const textW = w - 32;
    ctx.font = `400 13px ${FONT}`;
    const rows = [];
    for (const l of view.lines ?? []) rows.push({ who: l.who, parts: wrap(l.text, textW - 56, 3) });
    const reason = view.verdict?.reason ? wrap(view.verdict.reason, textW, 2) : [];
    const h = 22 + 22 + (view.state ? 20 : 0) + rows.reduce((n, r) => n + r.parts.length * 18 + 6, 0)
      + (view.verdict ? 26 + 20 + reason.length * 18 : 0) + (view.record ? 22 : 0) + 12;
    const y0 = 18 + top;
    paper(x, y0, w, h);
    const text = (str, tx, ty, maxW, font, color) => {
      ctx.font = font;
      ctx.fillStyle = color;
      ctx.textAlign = 'left';
      ctx.textBaseline = 'top';
      ctx.fillText(str, tx, ty, maxW);
    };
    text(view.title, x + 16, y0 + 14, w - 32 - 90, `600 15px ${FONT}`, palette.ink);
    let ly = y0 + 40;
    if (view.state) { text(view.state, x + 16, ly, textW, `400 13px ${FONT}`, palette.inkSoft); ly += 20; }
    for (const r of rows) {
      text(r.who, x + 16, ly, 50, `600 13px ${FONT}`, palette.ink);
      r.parts.forEach((part, k) => text(part, x + 16 + 56, ly + k * 18, textW - 56, `400 13px ${FONT}`, palette.ink));
      ly += r.parts.length * 18 + 6;
    }
    if (view.verdict) {
      // 胜负都用墨色、靠字说：点缀红只属于"轮到你了"
      text(view.verdict.headline, x + 16, ly + 4, textW, `600 15px ${FONT}`, palette.ink);
      ly += 26;
      text(view.verdict.votes, x + 16, ly, textW, `400 13px ${FONT}`, palette.ink);
      ly += 20;
      for (const part of reason) { text(part, x + 16, ly, textW, `400 13px ${FONT}`, palette.inkSoft); ly += 18; }
    }
    if (view.record) text(view.record, x + 16, ly + 2, textW, `400 12px ${FONT}`, palette.inkSoft);
    let bx = x + w - 16;
    for (const b of [...(view.buttons ?? [])].reverse()) {
      ctx.font = `600 13px ${FONT}`;
      const bw = Math.max(64, ctx.measureText(b.label).width + 26);   // 与 button() 的量法一致
      bx -= bw;
      button(b.label, bx, y0 + 10, b.id, { minWidth: 64 });
      bx -= 8;
    }
  }

  /** 摆放时顶上的提示：点一个空底座；或者直接放进默认的场馆；或者取消。 */
  function placingHint(view, ui, top = 0) {
    const W = window.innerWidth;
    ctx.font = `500 14px ${FONT}`;
    const tw = ctx.measureText(view.text).width;
    const w = Math.min(W - 40, tw + 40 + 2 * 120);
    const x = (W - w) / 2, y = 14 + top;
    paper(x, y, w, 46);
    ctx.fillStyle = palette.ink;
    ctx.textAlign = 'left';
    ctx.textBaseline = 'middle';
    ctx.fillText(view.text, x + 16, y + 23, w - 2 * 120 - 32);
    const bw = button(view.defaultLabel, x + w - 16 - 96 - 8 - 120, y + 8, 'placing-default', { minWidth: 120 });
    void bw;
    button(ui.cancel, x + w - 16 - 84, y + 8, 'placing-cancel');
  }

  /**
   * 群岛海图（设计稿 9.1、9.3）：一座岛一座城，按工作区创建时间排在一条螺线上，神殿居中。有委托在进行的岛点着灯
   * （白色的光——光属于雅典娜）；有一步在等你点头的岛带点缀色的圈和感叹号（"轮到你了"，同时有形状）；你所在的岛旁边一条船。
   */
  function chart(view, t, top = 0) {
    const W = window.innerWidth, H = window.innerHeight;
    ctx.fillStyle = palette.chartSea;
    ctx.fillRect(0, 0, W, H);
    // 海：几条细细的等深线
    ctx.strokeStyle = palette.chartContour;
    ctx.lineWidth = 1;
    for (let k = 0; k < 16; k++) {
      ctx.beginPath();
      for (let x = 0; x <= W; x += 24) {
        const y = (H / 16) * k + Math.sin(x / 90 + k) * 6;
        if (x === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y);
      }
      ctx.stroke();
    }
    const tr = view.transform;
    for (const isle of view.islands) {
      const shape = view.layout.find((l) => l.id === isle.id);
      if (!shape) continue;
      const c = tr.toScreen(shape.x, shape.y);
      ctx.beginPath();
      shape.points.forEach((p, i) => {
        const q = { x: c.x + p.x * tr.scale, y: c.y + p.y * tr.scale };
        if (i === 0) ctx.moveTo(q.x, q.y); else ctx.lineTo(q.x, q.y);
      });
      ctx.closePath();
      ctx.fillStyle = isle.missing ? palette.chartMissing : isle.sanctuary ? palette.chartSanctuary : palette.chartIsle;
      ctx.fill();
      ctx.strokeStyle = palette.chartInk;
      ctx.lineWidth = 1.4;
      ctx.stroke();
      // 岛上的城：一座小神庙的剪影
      const s = Math.max(0.7, tr.scale * 0.55) * (isle.sanctuary ? 1.4 : 1);
      ctx.fillStyle = palette.chartInk;
      ctx.fillRect(c.x - 9 * s, c.y - 2 * s, 18 * s, 2.2 * s);
      for (let k = 0; k < 4; k++) ctx.fillRect(c.x - 8 * s + k * 5 * s, c.y - 9 * s, 1.6 * s, 7 * s);
      ctx.beginPath(); ctx.moveTo(c.x - 10 * s, c.y - 9 * s); ctx.lineTo(c.x, c.y - 14 * s); ctx.lineTo(c.x + 10 * s, c.y - 9 * s); ctx.closePath(); ctx.fill();
      if (isle.running) {
        // 亮灯：白光一明一暗
        const glow = 0.55 + 0.35 * Math.sin(t / 420 + shape.x);
        const g = ctx.createRadialGradient(c.x, c.y - 6 * s, 1, c.x, c.y - 6 * s, 30 * s);
        g.addColorStop(0, `rgba(255,252,240,${glow})`);
        g.addColorStop(1, 'rgba(255,252,240,0)');
        ctx.fillStyle = g;
        ctx.beginPath(); ctx.arc(c.x, c.y - 6 * s, 30 * s, 0, Math.PI * 2); ctx.fill();
      }
      if (isle.waiting) {
        const pulse = 1 + 0.15 * Math.sin(t / 220);
        ctx.strokeStyle = ACCENT;
        ctx.lineWidth = 3;
        ctx.beginPath(); ctx.arc(c.x, c.y - 4 * s, (shape.size * tr.scale) * 0.55 * pulse, 0, Math.PI * 2); ctx.stroke();
        ctx.fillStyle = ACCENT;
        ctx.beginPath(); ctx.arc(c.x + shape.size * tr.scale * 0.5, c.y - shape.size * tr.scale * 0.45, 9, 0, Math.PI * 2); ctx.fill();
        ctx.fillStyle = '#fff'; ctx.font = `700 12px ${FONT}`; ctx.textAlign = 'center'; ctx.textBaseline = 'middle';
        ctx.fillText('!', c.x + shape.size * tr.scale * 0.5, c.y - shape.size * tr.scale * 0.45 + 0.5);
      }
      if (isle.current) {
        // 你在这里：岛边一条小船
        const bx = c.x - shape.size * tr.scale * 0.95, by = c.y + shape.size * tr.scale * 0.55;
        ctx.fillStyle = palette.chartBoat;
        ctx.beginPath(); ctx.moveTo(bx - 12, by); ctx.lineTo(bx + 12, by); ctx.lineTo(bx + 8, by + 5); ctx.lineTo(bx - 8, by + 5); ctx.closePath(); ctx.fill();
        ctx.fillRect(bx - 0.8, by - 14, 1.6, 14);
      }
      const name = isle.sanctuary ? view.labels.sanctuary : isle.name;
      const sub = isle.waiting ? view.labels.waiting : isle.running ? view.labels.running : isle.missing ? view.labels.missing : isle.current ? view.labels.here : '';
      ctx.font = `600 13px ${FONT}`;
      ctx.textAlign = 'center';
      ctx.textBaseline = 'top';
      ctx.fillStyle = palette.ink;
      const ny = c.y + shape.size * tr.scale * 0.9 + 4;
      ctx.fillText(name, c.x, ny, 180);
      if (sub) { ctx.font = `400 11.5px ${FONT}`; ctx.fillStyle = palette.inkSoft; ctx.fillText(sub, c.x, ny + 17, 180); }
      regions.push({ id: `island:${isle.id}`, x: c.x - shape.size * tr.scale, y: c.y - shape.size * tr.scale, w: shape.size * tr.scale * 2, h: shape.size * tr.scale * 2 + 30 });
    }
    paper(16, 14 + top, Math.min(460, W - 32), 62);
    ctx.fillStyle = palette.ink;
    ctx.textAlign = 'left';
    ctx.textBaseline = 'top';
    ctx.font = `600 15px ${FONT}`;
    ctx.fillText(view.title, 32, 24 + top);
    ctx.font = `400 12.5px ${FONT}`;
    ctx.fillStyle = palette.inkSoft;
    ctx.fillText(view.hint, 32, 48 + top, Math.min(430, W - 64));
    button(view.closeLabel ?? '×', W - 16 - 84, 22 + top, 'chart-close');
  }

  /** 广场公告板：外部改动记在这里（没有角色动作、没有光）。 */
  function notices(list, ui, top = 0) {
    if (list.length === 0) return;
    const w = 280;
    const x = window.innerWidth - w - 14;
    const y = 14 + top;
    ctx.globalAlpha = 0.92;
    panel(x, y, w, ui.noticeBoard, list.map((n) => n.text), { maxLines: 7, wrapEach: 2 });
    ctx.globalAlpha = 1;
  }

  /** 雾：找不到这座城邦的文件夹（11.4）。城还画在后面，盖一层雾和一张卡片；"重新定位"是唯一的动作。 */
  function fog(info, ui) {
    const W = window.innerWidth, H = window.innerHeight;
    const g = ctx.createRadialGradient(W / 2, H / 2, Math.min(W, H) * 0.1, W / 2, H / 2, Math.max(W, H) * 0.7);
    g.addColorStop(0, palette.fogInner);
    g.addColorStop(1, palette.fogOuter);
    ctx.fillStyle = g;
    ctx.fillRect(0, 0, W, H);
    const w = Math.min(460, W - 40);
    const x = (W - w) / 2;
    const p = panel(x, H / 2 - 90, w, ui.fogTitle, [ui.fogBody, info.path ?? ''].filter(Boolean), { maxLines: 3, footer: 40 });
    button(ui.relocate, x + 16, p.bottom - 44, 'relocate', { primary: true, minWidth: 120 });
  }

  /**
   * 成果卡片：点了一件交到你手上的东西。待处理的：读 / 打开 / 收下 / 退回；收下的：读 / 打开 / 摆放（或换个地方）/ 撤下；
   * 遗失的：只剩"移除"——由你决定，绝不自动删除（11.3）。
   */
  function card(item, ui) {
    const W = window.innerWidth, H = window.innerHeight;
    const w = Math.min(500, W - 40);
    const x = (W - w) / 2;
    const y = H - 230;
    const lines = [item.kindLabel, item.stateLabel, item.where].filter(Boolean);
    const p = panel(x, y, w, item.title, lines, { maxLines: 3, footer: 40 });
    let bx = x + 16;
    const by = p.bottom - 44;
    if (!item.lost) bx += button(ui.read, bx, by, 'card-read') + 8;
    if (item.path && !item.lost) bx += button(ui.open, bx, by, 'card-open') + 8;
    if (item.state === 'pending' && !item.lost) {
      bx += button(ui.accept, bx, by, 'card-accept', { primary: true }) + 8;
      button(ui.return, bx, by, 'card-return');
    } else if (item.state === 'accepted') {
      if (!item.lost) bx += button(item.placed ? ui.move : ui.place, bx, by, 'card-place') + 8;
      if (item.placed) bx += button(ui.takeDown, bx, by, 'card-unplace') + 8;
    }
    if (item.lost) button(ui.remove, bx, by, 'card-remove');
    regions.push({ id: 'card-close', x: x + w - 34, y: y + 6, w: 28, h: 28 });
    ctx.fillStyle = palette.inkFaint;
    ctx.font = `600 16px ${FONT}`;
    ctx.textAlign = 'center';
    ctx.textBaseline = 'middle';
    ctx.fillText('×', x + w - 20, y + 20);
  }

  /**
   * 航海过场（9.2）：出港 → 海上 → 靠岸。它同时是加载遮罩——C# 在后台测绘新的城邦。整块画在叠加层上（voyage.mjs），
   * 是 2D 的：过场期间三维场景还停在旧城（或者还没有城），没有什么需要投影。M3 第三轮：天空、云、两座岛、透视的海、
   * 一条会起伏的希腊船、海鸥、标题牌与航线（第一版是灰底上一条剪影小船、一行字）。
   */
  function voyage(v, t) {
    drawVoyage(ctx, window.innerWidth, window.innerHeight, v, t, night ? VOYAGE_PALETTES.night : VOYAGE_PALETTES.day);
  }

  function draw(frame) {
    regions = [];
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, window.innerWidth, window.innerHeight);
    if (frame.voyage) {
      voyage(frame.voyage, frame.time);
      if (frame.voyage.alpha >= 1) return regions;
    }
    // 实时模式左上角是视角与海图的工具条（DOM）：顶上的几块面板往下让出它的高度
    const top = frame.topInset ?? 0;
    if (frame.chart) {
      chart({ ...frame.chart, closeLabel: frame.ui.close }, frame.time, top);
      return regions;
    }
    for (const l of frame.labels) label(l);
    // 居民的话（M-B：两个人闲聊、跟你打招呼）在雅典娜的那一句底下——她的话最后画，永远在最上面
    for (const b of frame.bubbles ?? []) bubble(b);
    if (frame.bubble) bubble(frame.bubble);
    if (frame.athenaEdge) athenaMarker(frame.athenaEdge, frame.time);
    for (const e of frame.edges) edgeMarker(e, frame.time);
    if (frame.hud) hud(frame.hud);
    if (frame.notices && !frame.placing) notices(frame.notices, frame.ui, top);
    if (frame.fog) fog(frame.fog, frame.ui);
    else if (frame.herald) herald(frame.herald, frame.ui, top);
    else if (frame.debate) debateBoard(frame.debate, frame.ui, top);
    if (frame.placing && !frame.fog) placingHint(frame.placing, frame.ui, top);
    if (frame.card && !frame.fog) card(frame.card, frame.ui);
    else if (frame.panel && !frame.fog) dialog(frame.panel, frame.ui);
    return regions;
  }

  /** 命中测试：返回被点中的按钮 id（或 null）。 */
  function hit(x, y) {
    for (let i = regions.length - 1; i >= 0; i--) {
      const r = regions[i];
      if (x >= r.x && x <= r.x + r.w && y >= r.y && y <= r.y + r.h) return r.id;
    }
    return null;
  }

  /** 夜里换一套颜色（跟着应用的深色主题）：纸色面板变成暗色的羊皮纸、名牌反过来、过场是夜航。 */
  function setNight(on) {
    night = !!on;
    palette = night ? OVERLAY_PALETTES.night : OVERLAY_PALETTES.day;
  }

  return { resize, draw, hit, setNight, get night() { return night; }, get dpr() { return dpr; } };
}
