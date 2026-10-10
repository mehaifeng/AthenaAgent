// 2D 叠加层：旁白气泡、建筑名牌、状态行、屏幕边缘的"轮到你了"指示，以及 M1 的几块面板——
// 传令官的"你离开期间"报告、广场公告板（外部改动）、雾（找不到文件夹）、成果卡片、航海过场。
// 文件名和雅典娜说的话一律画在 canvas 上，不放进 innerHTML（设计稿 12.4）：
// 一个名叫 <img onerror=…> 的文件，放进 innerHTML 就会在一个能调用 C# 的页面里变成脚本。
// 面板上的按钮也画在这里：draw() 返回这一帧的可点区域，页面按坐标命中，不建任何 DOM。
// M2：对话面板（居民、委托板、神谕、场馆）左边画说话人的 2D 半身像（busts.mjs，白底陶瓶的线描）；
// 传令官的报告每一行可点；摆放时顶上一条提示；群岛海图是整屏的一张 2D 图。全部画在 canvas 上。
import { drawBust } from './art/busts.mjs';

const FONT = '"PingFang SC", "Hiragino Sans GB", "Noto Sans CJK SC", "Microsoft YaHei", sans-serif';
const ACCENT = '#b9572f';

export function createOverlay(canvas) {
  const ctx = canvas.getContext('2d');
  let dpr = 1;
  let regions = [];

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

  function label({ text, x, y, kind, flash = 0, unmet = false }) {
    const size = kind === 'public' ? 13 : kind === 'resident' || kind === 'tree' || kind === 'lost' ? 11.5 : 12;
    ctx.font = `${kind === 'public' ? 600 : 500} ${size}px ${FONT}`;
    ctx.textAlign = 'center';
    ctx.textBaseline = 'middle';
    // 还没结识的居民：名字后面一个省略号（他有话要说——邀请，不是"轮到你了"，所以不带点缀色，也不用彩色的表情符号）
    const shown = kind === 'resident' && unmet ? `${text} …` : kind === 'lost' ? `▢ ${text}` : text;
    const w = ctx.measureText(shown).width + 12;
    ctx.globalAlpha = 0.86;
    ctx.fillStyle = kind === 'public' ? 'rgba(38,35,32,0.78)' : kind === 'tree' ? 'rgba(46,52,40,0.8)' : 'rgba(250,248,244,0.82)';
    roundRect(x - w / 2, y - 10, w, 20, 4);
    ctx.fill();
    if (kind === 'lost') {
      ctx.globalAlpha = 0.7;
      ctx.setLineDash([3, 3]);
      ctx.strokeStyle = '#2b2724';
      ctx.lineWidth = 1;
      ctx.stroke();
      ctx.setLineDash([]);
    }
    if (flash > 0) {
      // 外部改动：名牌轻轻闪一下（没有光、没有角色动作——光属于雅典娜）
      ctx.globalAlpha = 0.5 * flash;
      ctx.strokeStyle = '#2b2724';
      ctx.lineWidth = 1.5;
      ctx.stroke();
    }
    ctx.globalAlpha = 1;
    ctx.fillStyle = kind === 'public' || kind === 'tree' ? '#f3efe8' : '#2b2724';
    ctx.fillText(shown, x, y + 0.5);
  }

  function bubble({ text, x, y, alpha }) {
    ctx.font = `500 15px ${FONT}`;
    ctx.textAlign = 'center';
    ctx.textBaseline = 'middle';
    const w = Math.min(ctx.measureText(text).width + 24, 420);
    const h = 30;
    const bx = x - w / 2, by = y - h - 12;
    ctx.globalAlpha = alpha;
    ctx.fillStyle = 'rgba(252,250,246,0.95)';
    ctx.strokeStyle = 'rgba(60,55,50,0.35)';
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
    ctx.fillStyle = '#26221f';
    ctx.fillText(text, x, by + h / 2 + 0.5, w - 16);
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

  /** 面板上的一个按钮：画出来并登记可点区域。primary 的按钮带点缀色（只给"轮到你了"的那一个动作）。 */
  function button(text, x, y, id, { primary = false, minWidth = 84 } = {}) {
    ctx.font = `600 13px ${FONT}`;
    const w = Math.max(minWidth, ctx.measureText(text).width + 26);
    const h = 30;
    ctx.fillStyle = primary ? ACCENT : 'rgba(40,37,34,0.86)';
    roundRect(x, y, w, h, 6);
    ctx.fill();
    if (primary) {
      ctx.strokeStyle = '#fff';
      ctx.lineWidth = 1.5;
      ctx.stroke();
    }
    ctx.fillStyle = '#fbf8f3';
    ctx.textAlign = 'center';
    ctx.textBaseline = 'middle';
    ctx.fillText(text, x + w / 2, y + h / 2 + 0.5);
    regions.push({ id, x, y, w, h });
    return w;
  }

  /**
   * 一块纸色面板：标题 + 若干行正文（+ 底部留给按钮的一行），返回底边。长行截断，不换行（面板的尺寸固定）。
   * footer：按钮行的高度——按钮画在正文之下，不压字。
   */
  function panel(x, y, w, title, lines, { maxLines = 8, footer = 0 } = {}) {
    const shown = lines.slice(0, maxLines);
    const h = 22 + 24 + shown.length * 20 + 12 + footer;
    ctx.fillStyle = 'rgba(250,247,241,0.96)';
    ctx.strokeStyle = 'rgba(60,55,50,0.25)';
    ctx.lineWidth = 1;
    roundRect(x, y, w, h, 10);
    ctx.fill();
    ctx.stroke();
    ctx.fillStyle = '#26221f';
    ctx.textAlign = 'left';
    ctx.textBaseline = 'top';
    ctx.font = `600 15px ${FONT}`;
    ctx.fillText(title, x + 16, y + 14, w - 32);
    ctx.font = `400 13px ${FONT}`;
    ctx.fillStyle = '#4a433d';
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
  function herald(report, ui) {
    const w = Math.min(600, window.innerWidth - 40);
    const x = (window.innerWidth - w) / 2;
    const bustW = 84;
    const shown = report.lines.slice(0, 7);
    const h = 22 + 24 + shown.length * 22 + 12 + 40;
    paper(x, 18, w, Math.max(h, 150));
    drawBust(ctx, 'herald', 'speak', x + 12, 26, 112);
    ctx.fillStyle = '#26221f';
    ctx.textAlign = 'left';
    ctx.textBaseline = 'top';
    ctx.font = `600 15px ${FONT}`;
    ctx.fillText(report.title, x + 16 + bustW, 32, w - bustW - 32);
    ctx.font = `400 13px ${FONT}`;
    shown.forEach((line, i) => {
      const ly = 62 + i * 22;
      const clickable = !!(line.conversationId || line.itemId);
      ctx.fillStyle = clickable ? '#26221f' : '#4a433d';
      ctx.fillText(line.text, x + 16 + bustW, ly, w - bustW - 56);
      if (clickable) {
        ctx.fillText('›', x + w - 30, ly);
        regions.push({ id: `herald-line:${i}`, x: x + 12 + bustW, y: ly - 3, w: w - bustW - 24, h: 20 });
      }
    });
    button(ui.dismiss, x + w - 16 - 84, 18 + Math.max(h, 150) - 44, 'herald-dismiss');
  }

  /** 一块纸色面板的底（没有标题与正文，给自己排版的面板用）。 */
  function paper(x, y, w, h) {
    ctx.fillStyle = 'rgba(250,247,241,0.97)';
    ctx.strokeStyle = 'rgba(60,55,50,0.25)';
    ctx.lineWidth = 1;
    roundRect(x, y, w, h, 10);
    ctx.fill();
    ctx.stroke();
  }

  /** 把一段话按宽度折成几行（面板里的说明文字；名字与模型写的话一样画在 canvas 上）。 */
  function wrap(text, maxWidth, maxLines = 4) {
    const out = [];
    let line = '';
    for (const ch of String(text ?? '')) {
      if (ctx.measureText(line + ch).width > maxWidth && line) {
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
   * 面板固定在画面底部中间，和成果卡片在同一处——同一时间只开一块。
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
    const rows = view.type === 'board' ? view.suggestions.length : view.type === 'venue' ? Math.max(1, view.stored.length) + 1 : 0;
    const h = Math.max(bustH + 28, 48 + body.length * 20 + rows * 34 + 56);
    const y = H - h - 18;
    paper(x, y, w, h);
    drawBust(ctx, view.bust, view.expression ?? 'neutral', x + 12, y + 14, bustH);
    ctx.fillStyle = '#26221f';
    ctx.textAlign = 'left';
    ctx.textBaseline = 'top';
    ctx.font = `600 15px ${FONT}`;
    ctx.fillText(view.title, textX, y + 16, textW);
    ctx.font = `400 13px ${FONT}`;
    ctx.fillStyle = '#4a433d';
    let ly = y + 44;
    for (const l of body) { ctx.fillText(l, textX, ly, textW); ly += 20; }
    if (view.type === 'board') {
      view.suggestions.forEach((s, i) => {
        ctx.fillStyle = '#26221f';
        ctx.fillText(`· ${s.line}`, textX, ly + 8, textW - 120);
        button(ui.takeSuggestion, x + w - 18 - 110, ly, `suggestion:${i}`, { minWidth: 110 });
        ly += 34;
      });
    } else if (view.type === 'venue') {
      ctx.fillStyle = '#26221f';
      ctx.font = `600 13px ${FONT}`;
      ctx.fillText(view.stored.length ? view.storedTitle : view.empty, textX, ly + 6, textW);
      ctx.font = `400 13px ${FONT}`;
      ly += 30;
      for (const item of view.stored) {
        ctx.fillText(item.title, textX, ly + 8, textW - 120);
        button(view.placeHere, x + w - 18 - 100, ly, `venue-place:${item.id}`, { minWidth: 100 });
        ly += 34;
      }
    }
    const by = y + h - 44;
    let bx = textX;
    if (view.type === 'resident') bx += button(view.tryLabel, bx, by, 'resident-try') + 8;
    if (view.type === 'oracle') {
      if (view.exists && view.latest) bx += button(ui.latestScroll, bx, by, 'oracle-latest') + 8;
      if (!view.exists) bx += button(ui.requestOracle, bx, by, 'oracle-request', { minWidth: 120 }) + 8;
    }
    button(view.type === 'resident' ? ui.dismiss : ui.close, x + w - 16 - 84, by, 'panel-close');
  }

  /** 摆放时顶上的提示：点一个空底座；或者直接放进默认的场馆；或者取消。 */
  function placingHint(view, ui) {
    const W = window.innerWidth;
    ctx.font = `500 14px ${FONT}`;
    const tw = ctx.measureText(view.text).width;
    const w = Math.min(W - 40, tw + 40 + 2 * 120);
    const x = (W - w) / 2, y = 14;
    paper(x, y, w, 46);
    ctx.fillStyle = '#26221f';
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
  function chart(view, t) {
    const W = window.innerWidth, H = window.innerHeight;
    ctx.fillStyle = '#d9d4ca';
    ctx.fillRect(0, 0, W, H);
    // 海：几条细细的等深线
    ctx.strokeStyle = 'rgba(80,72,64,0.12)';
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
      ctx.fillStyle = isle.missing ? 'rgba(200,196,188,0.85)' : isle.sanctuary ? '#efe9de' : '#e7e1d5';
      ctx.fill();
      ctx.strokeStyle = '#3a322c';
      ctx.lineWidth = 1.4;
      ctx.stroke();
      // 岛上的城：一座小神庙的剪影
      const s = Math.max(0.7, tr.scale * 0.55) * (isle.sanctuary ? 1.4 : 1);
      ctx.fillStyle = '#3a322c';
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
        ctx.fillStyle = '#2f2b28';
        ctx.beginPath(); ctx.moveTo(bx - 12, by); ctx.lineTo(bx + 12, by); ctx.lineTo(bx + 8, by + 5); ctx.lineTo(bx - 8, by + 5); ctx.closePath(); ctx.fill();
        ctx.fillRect(bx - 0.8, by - 14, 1.6, 14);
      }
      const name = isle.sanctuary ? view.labels.sanctuary : isle.name;
      const sub = isle.waiting ? view.labels.waiting : isle.running ? view.labels.running : isle.missing ? view.labels.missing : isle.current ? view.labels.here : '';
      ctx.font = `600 13px ${FONT}`;
      ctx.textAlign = 'center';
      ctx.textBaseline = 'top';
      ctx.fillStyle = '#26221f';
      const ny = c.y + shape.size * tr.scale * 0.9 + 4;
      ctx.fillText(name, c.x, ny, 180);
      if (sub) { ctx.font = `400 11.5px ${FONT}`; ctx.fillStyle = '#5a524a'; ctx.fillText(sub, c.x, ny + 17, 180); }
      regions.push({ id: `island:${isle.id}`, x: c.x - shape.size * tr.scale, y: c.y - shape.size * tr.scale, w: shape.size * tr.scale * 2, h: shape.size * tr.scale * 2 + 30 });
    }
    paper(16, 14, Math.min(460, W - 32), 62);
    ctx.fillStyle = '#26221f';
    ctx.textAlign = 'left';
    ctx.textBaseline = 'top';
    ctx.font = `600 15px ${FONT}`;
    ctx.fillText(view.title, 32, 24);
    ctx.font = `400 12.5px ${FONT}`;
    ctx.fillStyle = '#4a433d';
    ctx.fillText(view.hint, 32, 48, Math.min(430, W - 64));
    button(view.closeLabel ?? '×', W - 16 - 84, 22, 'chart-close');
  }

  /** 广场公告板：外部改动记在这里（没有角色动作、没有光）。 */
  function notices(list, ui) {
    if (list.length === 0) return;
    const w = 280;
    const x = window.innerWidth - w - 14;
    const y = 14;
    ctx.globalAlpha = 0.92;
    panel(x, y, w, ui.noticeBoard, list.map((n) => n.text), { maxLines: 5 });
    ctx.globalAlpha = 1;
  }

  /** 雾：找不到这座城邦的文件夹（11.4）。城还画在后面，盖一层雾和一张卡片；"重新定位"是唯一的动作。 */
  function fog(info, ui) {
    const W = window.innerWidth, H = window.innerHeight;
    const g = ctx.createRadialGradient(W / 2, H / 2, Math.min(W, H) * 0.1, W / 2, H / 2, Math.max(W, H) * 0.7);
    g.addColorStop(0, 'rgba(232,229,224,0.78)');
    g.addColorStop(1, 'rgba(214,210,203,0.95)');
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
    } else if (item.state === 'accepted' && !item.local) {
      if (!item.lost) bx += button(item.placed ? ui.move : ui.place, bx, by, 'card-place') + 8;
      if (item.placed) bx += button(ui.takeDown, bx, by, 'card-unplace') + 8;
    }
    if (item.lost && !item.local) button(ui.remove, bx, by, 'card-remove');
    regions.push({ id: 'card-close', x: x + w - 34, y: y + 6, w: 28, h: 28 });
    ctx.fillStyle = '#6b625a';
    ctx.font = `600 16px ${FONT}`;
    ctx.textAlign = 'center';
    ctx.textBaseline = 'middle';
    ctx.fillText('×', x + w - 20, y + 20);
  }

  /**
   * 航海过场（9.2）：出港 → 海上 → 靠岸。它同时是加载遮罩——C# 在后台测绘新的城邦。整块画在叠加层上，
   * 是 2D 的：过场期间三维场景还停在旧城（或者还没有城），没有什么需要投影。
   */
  function voyage(v, t) {
    const W = window.innerWidth, H = window.innerHeight;
    const sky = ctx.createLinearGradient(0, 0, 0, H);
    sky.addColorStop(0, '#d9d5ce');
    sky.addColorStop(0.55, '#c9c4bb');
    sky.addColorStop(0.56, '#8e9499');
    sky.addColorStop(1, '#6f767c');
    ctx.globalAlpha = v.alpha;
    ctx.fillStyle = sky;
    ctx.fillRect(0, 0, W, H);
    // 远处的海岸线与浪：几条移动的细线
    ctx.strokeStyle = 'rgba(240,238,233,0.35)';
    ctx.lineWidth = 1;
    for (let i = 0; i < 9; i++) {
      const y = H * 0.6 + i * (H * 0.045);
      const off = ((t / (900 + i * 120)) % 1) * 80;
      ctx.beginPath();
      for (let x = -80 + off; x < W + 80; x += 80) {
        ctx.moveTo(x, y);
        ctx.lineTo(x + 34, y + Math.sin((x + t / 300) / 40) * 1.5);
      }
      ctx.stroke();
    }
    // 船：剪影，从左向右，随浪起伏
    const bx = W * (0.15 + 0.7 * v.progress);
    const by = H * 0.6 + Math.sin(t / 420) * 4;
    ctx.fillStyle = '#2f2b28';
    ctx.beginPath();
    ctx.moveTo(bx - 60, by - 6);
    ctx.lineTo(bx + 66, by - 6);
    ctx.lineTo(bx + 46, by + 14);
    ctx.lineTo(bx - 44, by + 14);
    ctx.closePath();
    ctx.fill();
    ctx.fillRect(bx - 2, by - 78, 4, 72);
    ctx.fillStyle = '#efe9df';
    ctx.beginPath();
    ctx.moveTo(bx + 4, by - 74);
    ctx.quadraticCurveTo(bx + 46, by - 46, bx + 4, by - 14);
    ctx.closePath();
    ctx.fill();
    ctx.fillStyle = '#2b2724';
    ctx.font = `600 17px ${FONT}`;
    ctx.textAlign = 'center';
    ctx.textBaseline = 'middle';
    ctx.fillText(v.caption, W / 2, H * 0.3, W - 40);
    ctx.globalAlpha = 1;
  }

  function draw(frame) {
    regions = [];
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, window.innerWidth, window.innerHeight);
    if (frame.voyage) {
      voyage(frame.voyage, frame.time);
      if (frame.voyage.alpha >= 1) return regions;
    }
    if (frame.chart) {
      chart({ ...frame.chart, closeLabel: frame.ui.close }, frame.time);
      return regions;
    }
    for (const l of frame.labels) label(l);
    if (frame.bubble) bubble(frame.bubble);
    for (const e of frame.edges) edgeMarker(e, frame.time);
    if (frame.hud) hud(frame.hud);
    if (frame.notices && !frame.placing) notices(frame.notices, frame.ui);
    if (frame.fog) fog(frame.fog, frame.ui);
    else if (frame.herald) herald(frame.herald, frame.ui);
    if (frame.placing && !frame.fog) placingHint(frame.placing, frame.ui);
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

  return { resize, draw, hit, get dpr() { return dpr; } };
}
