// 2D 叠加层：旁白气泡、建筑名牌、状态行、屏幕边缘的"轮到你了"指示。
// 文件名和雅典娜说的话一律画在 canvas 上，不放进 innerHTML（设计稿 12.4）：
// 一个名叫 <img onerror=…> 的文件，放进 innerHTML 就会在一个将来能调用 C# 的页面里变成脚本。
const FONT = '"PingFang SC", "Hiragino Sans GB", "Noto Sans CJK SC", "Microsoft YaHei", sans-serif';
const ACCENT = '#b9572f';

export function createOverlay(canvas) {
  const ctx = canvas.getContext('2d');
  let dpr = 1;

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

  function label({ text, x, y, kind }) {
    const size = kind === 'public' ? 13 : 12;
    ctx.font = `${kind === 'public' ? 600 : 500} ${size}px ${FONT}`;
    ctx.textAlign = 'center';
    ctx.textBaseline = 'middle';
    const w = ctx.measureText(text).width + 12;
    ctx.globalAlpha = 0.86;
    ctx.fillStyle = kind === 'public' ? 'rgba(38,35,32,0.78)' : 'rgba(250,248,244,0.82)';
    roundRect(x - w / 2, y - 10, w, 20, 4);
    ctx.fill();
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

  function draw(frame) {
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    ctx.clearRect(0, 0, window.innerWidth, window.innerHeight);
    for (const l of frame.labels) label(l);
    if (frame.bubble) bubble(frame.bubble);
    for (const e of frame.edges) edgeMarker(e, frame.time);
    if (frame.hud) hud(frame.hud);
  }

  return { resize, draw, get dpr() { return dpr; } };
}
