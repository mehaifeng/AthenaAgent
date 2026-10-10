// 2D 叠加层：旁白气泡、建筑名牌、状态行、屏幕边缘的"轮到你了"指示，以及 M1 的几块面板——
// 传令官的"你离开期间"报告、广场公告板（外部改动）、雾（找不到文件夹）、成果卡片、航海过场。
// 文件名和雅典娜说的话一律画在 canvas 上，不放进 innerHTML（设计稿 12.4）：
// 一个名叫 <img onerror=…> 的文件，放进 innerHTML 就会在一个能调用 C# 的页面里变成脚本。
// 面板上的按钮也画在这里：draw() 返回这一帧的可点区域，页面按坐标命中，不建任何 DOM。
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

  function label({ text, x, y, kind, flash = 0 }) {
    const size = kind === 'public' ? 13 : 12;
    ctx.font = `${kind === 'public' ? 600 : 500} ${size}px ${FONT}`;
    ctx.textAlign = 'center';
    ctx.textBaseline = 'middle';
    const w = ctx.measureText(text).width + 12;
    ctx.globalAlpha = 0.86;
    ctx.fillStyle = kind === 'public' ? 'rgba(38,35,32,0.78)' : 'rgba(250,248,244,0.82)';
    roundRect(x - w / 2, y - 10, w, 20, 4);
    ctx.fill();
    if (flash > 0) {
      // 外部改动：名牌轻轻闪一下（没有光、没有角色动作——光属于雅典娜）
      ctx.globalAlpha = 0.5 * flash;
      ctx.strokeStyle = '#2b2724';
      ctx.lineWidth = 1.5;
      ctx.stroke();
    }
    ctx.globalAlpha = 1;
    ctx.fillStyle = kind === 'public' ? '#f3efe8' : '#2b2724';
    ctx.fillText(text, x, y + 0.5);
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

  /** 传令官："你离开期间"——建筑的增删改名、藏品的找回与遗失（设计稿 11.3）。点"知道了"收起。 */
  function herald(report, ui) {
    const w = Math.min(520, window.innerWidth - 40);
    const x = (window.innerWidth - w) / 2;
    const p = panel(x, 18, w, report.title, report.lines, { maxLines: 7, footer: 40 });
    button(ui.dismiss, x + w - 16 - 84, p.bottom - 44, 'herald-dismiss');
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

  /** 成果卡片：点了一件交到你手上的东西。读 / 打开 / 收下 / 退回。 */
  function card(item, ui) {
    const W = window.innerWidth, H = window.innerHeight;
    const w = Math.min(440, W - 40);
    const x = (W - w) / 2;
    const y = H - 220;
    const lines = [item.kindLabel, item.stateLabel].filter(Boolean);
    const p = panel(x, y, w, item.title, lines, { maxLines: 2, footer: 40 });
    let bx = x + 16;
    const by = p.bottom - 44;
    bx += button(ui.read, bx, by, 'card-read') + 8;
    if (item.path) bx += button(ui.open, bx, by, 'card-open') + 8;
    if (item.state === 'pending') {
      bx += button(ui.accept, bx, by, 'card-accept', { primary: true }) + 8;
      button(ui.return, bx, by, 'card-return');
    }
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
    for (const l of frame.labels) label(l);
    if (frame.bubble) bubble(frame.bubble);
    for (const e of frame.edges) edgeMarker(e, frame.time);
    if (frame.hud) hud(frame.hud);
    if (frame.notices) notices(frame.notices, frame.ui);
    if (frame.fog) fog(frame.fog, frame.ui);
    else if (frame.herald) herald(frame.herald, frame.ui);
    if (frame.card && !frame.fog) card(frame.card, frame.ui);
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
