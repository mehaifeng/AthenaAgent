// 航海过场（设计稿 9.2；M3 第三轮，用户：改进更精细的过场动画——切换工作区）。它同时是加载遮罩：C# 在后台测绘新的城邦。
// 整块画在 2D 叠加层上，纯 canvas、不建 DOM，给定时刻 t 与进度 progress 永远画出同一帧（截图可复现）。
//
// 一帧从后往前：天空（白天一轮淡日，夜里一弯月、几颗会眨的星）→ 几道慢慢飘的云 → 左边渐远的出发之岛、右边渐近的目的之岛
// → 有透视的海（近处的浪大、远处的细、日光 / 月光在海面上的一道碎光）→ 一条希腊船（弯起的船尾、船首的撞角与一只眼睛、
// 一面鼓起的横纹帆、一排随节拍起落的桨、船尾拖出的白沫，随浪起伏、前后点头；甲板上一点暖光——光属于雅典娜，她在船上）
// → 两三只海鸥 → 近处更深的浪 → 四角压暗 → 标题牌（"驶向"小字、目的地大字、回纹细线、一条从出发之岛到目的之岛的航线，
// 船的位置跟着进度走）。黑白灰；点缀色只给"轮到你了"，这里没有。
//
// 时长与靠岸的规则不在这里（live.transitionArrival）；这里只按 { alpha, progress, caption } 画。

const FONT = '"PingFang SC", "Hiragino Sans GB", "Noto Sans CJK SC", "Microsoft YaHei", sans-serif';

/** 白天与夜里的颜色（跟着应用的深 / 浅色主题，见 game.setNight）。 */
export const VOYAGE_PALETTES = Object.freeze({
  day: Object.freeze({
    skyTop: '#c9c5bd', skyMid: '#ddd9d1', skyHorizon: '#ece8e0', sun: 'rgba(255,252,242,0.95)', sunHalo: 'rgba(255,250,236,0.55)',
    cloud: 'rgba(250,248,243,0.55)', cloudShade: 'rgba(160,154,146,0.18)', farIsland: '#b4afa7', nearIsland: '#9a948b', templeInk: '#7d776f',
    seaHorizon: '#a9aeb1', seaNear: '#5f666b', wave: 'rgba(242,240,234,0.42)', waveDark: 'rgba(40,42,44,0.22)', glitter: 'rgba(255,253,245,0.75)',
    hull: '#2a2624', sail: '#efe9de', sailShade: 'rgba(60,55,50,0.22)', sailStripe: 'rgba(60,55,50,0.35)', rigging: 'rgba(42,38,36,0.7)',
    foam: 'rgba(250,250,247,0.85)', bird: 'rgba(48,46,44,0.75)', vignette: 'rgba(60,56,52,0.32)', ink: '#2b2724', inkSoft: '#5f574f', rule: 'rgba(43,39,36,0.55)',
    card: 'rgba(250,247,241,0.0)', lamp: 'rgba(255,236,190,0.9)', lampHalo: 'rgba(255,236,190,0.35)', stars: 0,
  }),
  night: Object.freeze({
    skyTop: '#0f1216', skyMid: '#1a1f25', skyHorizon: '#2a3038', sun: 'rgba(236,236,226,0.95)', sunHalo: 'rgba(200,210,220,0.22)',
    cloud: 'rgba(120,128,138,0.22)', cloudShade: 'rgba(0,0,0,0.25)', farIsland: '#22272d', nearIsland: '#181c21', templeInk: '#0d0f12',
    seaHorizon: '#2b3137', seaNear: '#0d1013', wave: 'rgba(170,182,196,0.30)', waveDark: 'rgba(0,0,0,0.35)', glitter: 'rgba(220,228,236,0.7)',
    hull: '#060708', sail: '#9aa0a6', sailShade: 'rgba(0,0,0,0.35)', sailStripe: 'rgba(0,0,0,0.4)', rigging: 'rgba(10,12,14,0.85)',
    foam: 'rgba(200,210,220,0.6)', bird: 'rgba(200,206,212,0.55)', vignette: 'rgba(0,0,0,0.5)', ink: '#ece5d6', inkSoft: '#b8b0a2', rule: 'rgba(236,229,214,0.45)',
    card: 'rgba(0,0,0,0)', lamp: 'rgba(255,226,170,0.95)', lampHalo: 'rgba(255,214,150,0.4)', stars: 1,
  }),
});

const TAU = Math.PI * 2;
const ease = (x) => { const t = Math.min(1, Math.max(0, x)); return t * t * (3 - 2 * t); };
/** 一个确定的伪随机（第 i 颗星、第 i 片云永远在同一处）。 */
const hash = (i) => { const s = Math.sin(i * 127.1 + 311.7) * 43758.5453; return s - Math.floor(s); };

/**
 * 画一帧过场。v：{ alpha, progress, caption }（voyageFrame 的结果）；t：毫秒；pal：VOYAGE_PALETTES 之一。
 * 返回船此刻的屏幕位置（测试与截图用）。
 */
export function drawVoyage(ctx, W, H, v, t, pal = VOYAGE_PALETTES.day) {
  const progress = Math.min(1, Math.max(0, v.progress ?? 0));
  const horizon = Math.round(H * 0.58);
  ctx.save();
  ctx.globalAlpha = v.alpha ?? 1;

  // —— 天空 ——
  const sky = ctx.createLinearGradient(0, 0, 0, horizon);
  sky.addColorStop(0, pal.skyTop);
  sky.addColorStop(0.62, pal.skyMid);
  sky.addColorStop(1, pal.skyHorizon);
  ctx.fillStyle = sky;
  ctx.fillRect(0, 0, W, horizon + 1);
  if (pal.stars) {
    for (let i = 0; i < 90; i++) {
      const x = hash(i) * W, y = hash(i + 500) * horizon * 0.85;
      const tw = 0.35 + 0.65 * Math.abs(Math.sin(t / (900 + hash(i + 900) * 1400) + i));
      ctx.fillStyle = `rgba(236,238,242,${(0.25 + 0.55 * hash(i + 77)) * tw})`;
      const r = hash(i + 33) > 0.92 ? 1.4 : 0.8;
      ctx.beginPath(); ctx.arc(x, y, r, 0, TAU); ctx.fill();
    }
  }
  // 太阳 / 月亮：在目的之岛那一侧的天上，随航程慢慢升起一点
  const sx = W * 0.7, sy = horizon * (0.42 - 0.06 * progress);
  const halo = ctx.createRadialGradient(sx, sy, 2, sx, sy, Math.min(W, H) * 0.22);
  halo.addColorStop(0, pal.sunHalo);
  halo.addColorStop(1, 'rgba(255,255,255,0)');
  ctx.fillStyle = halo;
  ctx.fillRect(0, 0, W, horizon);
  const R = Math.max(14, H * 0.03);
  ctx.fillStyle = pal.sun;
  if (pal.stars) {
    // 一弯月：月轮减去一个错开的圆（两圆的交点之间，外弧走月轮、内弧走错开的那个圆）。
    // 不能拿天色的圆盖上去——月晕把周围提亮了，盖上去的那一块是一个比周围暗的圆盘
    const cx = sx + R * 0.45, cy = sy - R * 0.2, r = R * 0.92;
    const d = Math.hypot(cx - sx, cy - sy);
    const a = (R * R - r * r + d * d) / (2 * d), h = Math.sqrt(Math.max(0, R * R - a * a));
    const mx = sx + (a * (cx - sx)) / d, my = sy + (a * (cy - sy)) / d;
    const p1 = { x: mx + (h * (cy - sy)) / d, y: my - (h * (cx - sx)) / d }, p2 = { x: mx - (h * (cy - sy)) / d, y: my + (h * (cx - sx)) / d };
    const aMoon1 = Math.atan2(p1.y - sy, p1.x - sx), aMoon2 = Math.atan2(p2.y - sy, p2.x - sx);
    const aCov1 = Math.atan2(p1.y - cy, p1.x - cx), aCov2 = Math.atan2(p2.y - cy, p2.x - cx);
    ctx.beginPath();
    ctx.arc(sx, sy, R, aMoon1, aMoon2, false);
    ctx.arc(cx, cy, r, aCov2, aCov1, true);
    ctx.closePath();
    ctx.fill();
  } else {
    ctx.beginPath(); ctx.arc(sx, sy, R, 0, TAU); ctx.fill();
  }

  // —— 云：几道长条，慢慢往左飘（远的慢） ——
  for (let i = 0; i < 6; i++) {
    const depth = 0.4 + hash(i + 40) * 0.6;
    const span = W * (0.35 + hash(i + 41) * 0.4);
    const x = ((hash(i + 42) * (W + span) - (t * 0.004 * depth)) % (W + span) + (W + span)) % (W + span) - span * 0.6;
    const y = horizon * (0.12 + hash(i + 43) * 0.5);
    for (let k = 0; k < 7; k++) {
      const cx = x + (k / 6) * span, cy = y + Math.sin(k * 1.7 + i) * 6;
      const rx = span * 0.12 * (0.7 + hash(i * 7 + k) * 0.6), ry = 9 + 10 * depth * hash(i * 3 + k);
      ctx.fillStyle = k % 2 ? pal.cloud : pal.cloudShade;
      ctx.beginPath(); ctx.ellipse(cx, cy, rx, ry, 0, 0, TAU); ctx.fill();
    }
  }

  // —— 两座岛：出发之岛在左边渐远渐淡，目的之岛在右边渐近渐大 ——
  const island = (cx, base, scale, color, ink, alpha) => {
    if (alpha <= 0.01 || scale <= 0.01) return;
    ctx.save();
    ctx.globalAlpha *= alpha;
    ctx.fillStyle = color;
    ctx.beginPath();
    ctx.moveTo(cx - 190 * scale, base);
    ctx.bezierCurveTo(cx - 140 * scale, base - 34 * scale, cx - 90 * scale, base - 58 * scale, cx - 30 * scale, base - 66 * scale);
    ctx.bezierCurveTo(cx + 10 * scale, base - 92 * scale, cx + 60 * scale, base - 70 * scale, cx + 95 * scale, base - 44 * scale);
    ctx.bezierCurveTo(cx + 140 * scale, base - 30 * scale, cx + 170 * scale, base - 10 * scale, cx + 200 * scale, base);
    ctx.closePath();
    ctx.fill();
    // 山顶的一座小神庙：台基、六根柱、三角山花
    const tx = cx - 2 * scale, ty = base - 78 * scale, s = scale;
    ctx.fillStyle = ink;
    ctx.fillRect(tx - 26 * s, ty - 3 * s, 52 * s, 4 * s);
    for (let k = 0; k < 6; k++) ctx.fillRect(tx - 22 * s + k * 8.4 * s, ty - 17 * s, 2.4 * s, 14 * s);
    ctx.beginPath(); ctx.moveTo(tx - 27 * s, ty - 17 * s); ctx.lineTo(tx, ty - 27 * s); ctx.lineTo(tx + 27 * s, ty - 17 * s); ctx.closePath(); ctx.fill();
    ctx.restore();
  };
  island(W * (0.12 - 0.08 * progress), horizon + 1, Math.max(0.2, 0.95 - 0.6 * progress), pal.farIsland, pal.templeInk, 1 - 0.85 * ease(progress * 1.2));
  island(W * (0.96 - 0.1 * ease(progress)), horizon + 1, 0.25 + 0.95 * ease(progress), pal.nearIsland, pal.templeInk, 0.25 + 0.75 * ease(progress * 1.4));

  // —— 海：从地平线往下越来越深；一行一行的浪，近处的大、远处的细 ——
  const sea = ctx.createLinearGradient(0, horizon, 0, H);
  sea.addColorStop(0, pal.seaHorizon);
  sea.addColorStop(1, pal.seaNear);
  ctx.fillStyle = sea;
  ctx.fillRect(0, horizon, W, H - horizon);
  // 地平线上一道亮线
  ctx.fillStyle = pal.wave;
  ctx.fillRect(0, horizon, W, 1.5);
  // 日光 / 月光在海面上的碎光：太阳正下方一条竖着的碎片带
  for (let i = 0; i < 70; i++) {
    const d = hash(i + 200);
    const y = horizon + 4 + Math.pow(d, 1.6) * (H - horizon) * 0.9;
    const spread = 6 + (y - horizon) * 0.32;
    const x = sx + (hash(i + 260) - 0.5) * spread * 2 + Math.sin(t / 500 + i) * 3;
    const flick = 0.3 + 0.7 * Math.abs(Math.sin(t / (260 + hash(i + 300) * 400) + i * 1.3));
    ctx.fillStyle = pal.glitter;
    ctx.globalAlpha = (v.alpha ?? 1) * flick * (1 - d * 0.7);
    ctx.fillRect(x, y, 3 + (y - horizon) * 0.05, 1.2);
  }
  ctx.globalAlpha = v.alpha ?? 1;
  const waveRows = 14;
  for (let r = 0; r < waveRows; r++) {
    const f = r / (waveRows - 1);                       // 0 远 … 1 近
    const y = horizon + 6 + Math.pow(f, 1.7) * (H - horizon - 10);
    const amp = 0.8 + f * 5.5, len = 26 + f * 110, speed = 0.012 + f * 0.05;
    ctx.strokeStyle = f > 0.75 ? pal.waveDark : pal.wave;
    ctx.lineWidth = 0.6 + f * 1.6;
    ctx.beginPath();
    for (let x = -20; x <= W + 20; x += 8) {
      const yy = y + Math.sin(x / len + t * speed * 0.01 + r * 1.9) * amp + Math.sin(x / (len * 0.43) - t * 0.0011 + r) * amp * 0.35;
      if (x === -20) ctx.moveTo(x, yy); else ctx.lineTo(x, yy);
    }
    ctx.globalAlpha = (v.alpha ?? 1) * (0.35 + 0.65 * f);
    ctx.stroke();
  }
  ctx.globalAlpha = v.alpha ?? 1;

  // —— 船：从左往右，跟着进度；随浪起伏、前后点头 ——
  const shipScale = Math.max(0.55, Math.min(1.25, W / 1280)) * 1.05;
  const bx = W * (0.14 + 0.62 * progress) + Math.sin(t / 1700) * 6;
  const by = horizon + (H - horizon) * 0.36 + Math.sin(t / 620) * 5 * shipScale;
  const pitch = Math.sin(t / 620 + 0.9) * 0.035;
  // 尾迹：船尾往后张开的两道白沫（V 字形，越远越淡越散），中间一溜碎沫；船首切开的一圈浪花
  ctx.strokeStyle = pal.foam;
  ctx.lineCap = 'round';
  for (const side of [-1, 1]) {
    for (let k = 0; k < 14; k++) {
      const u = k / 13;
      const wx = bx - (58 + u * 360) * shipScale;
      const wy = by + (12 + side * u * 26 + Math.sin(t / 260 + k * 1.7) * 1.4) * shipScale;
      ctx.globalAlpha = (v.alpha ?? 1) * (0.8 - 0.75 * u);
      ctx.lineWidth = (2.4 - 1.8 * u) * shipScale;
      ctx.beginPath();
      ctx.moveTo(wx, wy);
      ctx.lineTo(wx - (14 + 6 * u) * shipScale, wy + side * 2.2 * shipScale);
      ctx.stroke();
    }
  }
  for (let k = 0; k < 18; k++) {
    const u = hash(k + 700);
    const wx = bx - (60 + u * 300) * shipScale + Math.sin(t / 340 + k) * 3;
    const wy = by + (12 + (hash(k + 720) - 0.5) * u * 30) * shipScale;
    ctx.globalAlpha = (v.alpha ?? 1) * (0.6 - 0.55 * u);
    ctx.lineWidth = 1.2 * shipScale;
    ctx.beginPath(); ctx.moveTo(wx, wy); ctx.lineTo(wx - 5 * shipScale, wy); ctx.stroke();
  }
  ctx.globalAlpha = (v.alpha ?? 1) * 0.75;
  ctx.lineWidth = 1.6 * shipScale;
  ctx.beginPath();
  ctx.moveTo(bx + 86 * shipScale, by + 8 * shipScale);
  ctx.quadraticCurveTo(bx + 96 * shipScale, by + 13 * shipScale + Math.sin(t / 200) * 1.5, bx + 70 * shipScale, by + 15 * shipScale);
  ctx.stroke();
  ctx.lineCap = 'butt';
  ctx.globalAlpha = v.alpha ?? 1;
  drawShip(ctx, bx, by, shipScale, pitch, t, pal);

  // —— 海鸥 ——
  for (let i = 0; i < 3; i++) {
    const gx = ((W * (0.2 + 0.25 * i) + t * (0.02 + 0.006 * i)) % (W + 80)) - 40;
    const gy = horizon * (0.38 + 0.12 * i) + Math.sin(t / (700 + i * 90) + i) * 8;
    const flap = Math.sin(t / (160 + i * 25) + i * 2) * 4;
    ctx.strokeStyle = pal.bird;
    ctx.lineWidth = 1.6;
    ctx.beginPath();
    ctx.moveTo(gx - 10, gy - flap);
    ctx.quadraticCurveTo(gx - 4, gy - 3 - flap * 0.3, gx, gy);
    ctx.quadraticCurveTo(gx + 4, gy - 3 - flap * 0.3, gx + 10, gy - flap);
    ctx.stroke();
  }

  // —— 近处更深的一行浪（压在船下面一点，船像是在浪里） ——
  ctx.fillStyle = pal.waveDark;
  ctx.beginPath();
  ctx.moveTo(0, H);
  for (let x = 0; x <= W + 10; x += 10) ctx.lineTo(x, H - 26 - Math.sin(x / 70 + t / 520) * 6 - Math.sin(x / 31 - t / 380) * 2.5);
  ctx.lineTo(W, H);
  ctx.closePath();
  ctx.fill();

  // —— 四角压暗 ——
  const vg = ctx.createRadialGradient(W / 2, H * 0.48, Math.min(W, H) * 0.35, W / 2, H * 0.5, Math.max(W, H) * 0.78);
  vg.addColorStop(0, 'rgba(0,0,0,0)');
  vg.addColorStop(1, pal.vignette);
  ctx.fillStyle = vg;
  ctx.fillRect(0, 0, W, H);

  // —— 标题牌 ——
  titleCard(ctx, W, H, v.caption ?? '', progress, pal);
  ctx.restore();
  return { x: bx, y: by };
}

/** 一条希腊船：船身（弯起的船尾、撞角、船首的眼睛）、一排桨、桅杆、鼓起的横纹帆、几根缆，甲板上一点暖光。 */
function drawShip(ctx, x, y, s, pitch, t, pal) {
  ctx.save();
  ctx.translate(x, y);
  ctx.rotate(pitch);
  ctx.scale(s, s);
  // 桨：一排斜插进水里的细线，按节拍前后划
  const stroke = Math.sin(t / 420);
  ctx.strokeStyle = pal.hull;
  ctx.lineWidth = 1.2;
  for (let k = 0; k < 9; k++) {
    const ox = -44 + k * 10;
    ctx.beginPath();
    ctx.moveTo(ox, 4);
    ctx.lineTo(ox - 8 - stroke * 6, 22 + Math.cos(t / 420) * 2);
    ctx.stroke();
  }
  // 船身
  ctx.fillStyle = pal.hull;
  ctx.beginPath();
  ctx.moveTo(-70, -6);                                   // 船尾
  ctx.quadraticCurveTo(-84, -22, -78, -40);              // 弯起的船尾（aphlaston）
  ctx.quadraticCurveTo(-74, -30, -64, -12);
  ctx.lineTo(58, -10);                                   // 舷
  ctx.quadraticCurveTo(66, -12, 70, -18);                // 船首
  ctx.lineTo(74, -4);
  ctx.lineTo(90, 6);                                     // 撞角
  ctx.lineTo(70, 8);
  ctx.quadraticCurveTo(10, 16, -62, 8);
  ctx.closePath();
  ctx.fill();
  // 船首的一只眼睛
  ctx.fillStyle = pal.sail;
  ctx.beginPath(); ctx.ellipse(60, -3, 4.2, 2.6, 0, 0, TAU); ctx.fill();
  ctx.fillStyle = pal.hull;
  ctx.beginPath(); ctx.arc(61, -3, 1.4, 0, TAU); ctx.fill();
  // 舷边一道浅线
  ctx.strokeStyle = pal.sailShade;
  ctx.lineWidth = 1.2;
  ctx.beginPath(); ctx.moveTo(-60, -4); ctx.quadraticCurveTo(0, -2, 62, -6); ctx.stroke();
  // 桅杆与帆桁
  ctx.fillStyle = pal.hull;
  ctx.fillRect(-3, -96, 3.4, 88);
  const billow = 7 + Math.sin(t / 900) * 3;
  ctx.strokeStyle = pal.hull;
  ctx.lineWidth = 2.2;
  ctx.beginPath(); ctx.moveTo(-40, -90); ctx.lineTo(38, -92); ctx.stroke();
  // 鼓起的方帆，几道横纹（收帆的绳）
  ctx.fillStyle = pal.sail;
  ctx.beginPath();
  ctx.moveTo(-38, -88);
  ctx.quadraticCurveTo(0 + billow, -90, 36, -90);
  ctx.quadraticCurveTo(36 + billow * 1.3, -56, 32, -22);
  ctx.quadraticCurveTo(-2 + billow, -20, -34, -22);
  ctx.quadraticCurveTo(-34 + billow * 1.1, -56, -38, -88);
  ctx.closePath();
  ctx.fill();
  ctx.fillStyle = pal.sailShade;
  ctx.beginPath();
  ctx.moveTo(-34, -22); ctx.quadraticCurveTo(-34 + billow * 1.1, -56, -38, -88); ctx.lineTo(-24, -88); ctx.quadraticCurveTo(-22 + billow, -56, -22, -22); ctx.closePath();
  ctx.fill();
  ctx.strokeStyle = pal.sailStripe;
  ctx.lineWidth = 1;
  for (let k = 1; k < 5; k++) {
    const yy = -88 + k * 13.5;
    ctx.beginPath(); ctx.moveTo(-36 + billow * 0.2, yy); ctx.quadraticCurveTo(0 + billow, yy - 2, 34 + billow * 0.5, yy); ctx.stroke();
  }
  // 缆：桅顶到船首、船尾
  ctx.strokeStyle = pal.rigging;
  ctx.lineWidth = 0.9;
  ctx.beginPath(); ctx.moveTo(-1, -96); ctx.lineTo(66, -14); ctx.moveTo(-1, -96); ctx.lineTo(-66, -12); ctx.stroke();
  // 甲板上一点暖光：雅典娜在船上
  const lamp = ctx.createRadialGradient(-30, -16, 0.5, -30, -16, 16);
  lamp.addColorStop(0, pal.lamp);
  lamp.addColorStop(1, 'rgba(255,236,190,0)');
  ctx.fillStyle = lamp;
  ctx.beginPath(); ctx.arc(-30, -16, 16, 0, TAU); ctx.fill();
  ctx.restore();
}

/** 标题牌："驶向"一行小字、目的地一行大字，一道回纹细线，下面一条航线（左边出发之岛、右边目的之岛，船跟着进度走）。 */
function titleCard(ctx, W, H, caption, progress, pal) {
  const cx = W / 2, top = Math.round(H * 0.17);
  // 标题后面一片柔和的天色：云从这里飘过时淡下去，字不和云抢
  const clear = ctx.createRadialGradient(cx, top + 34, 10, cx, top + 34, Math.min(W * 0.32, 300));
  clear.addColorStop(0, pal.skyMid);
  clear.addColorStop(0.55, pal.skyMid.length === 7 ? `${pal.skyMid}cc` : pal.skyMid);
  clear.addColorStop(1, pal.skyMid.length === 7 ? `${pal.skyMid}00` : 'rgba(0,0,0,0)');
  ctx.fillStyle = clear;
  ctx.fillRect(cx - W * 0.4, top - 40, W * 0.8, 150);
  // 说明分两行：前缀（"驶向" / "Sailing to"）小字，其余大字；说明里没有空格就整句大字
  const text = String(caption ?? '');
  const cut = text.search(/\s/);
  const lead = cut > 0 ? text.slice(0, cut) : '';
  const name = cut > 0 ? text.slice(cut + 1) : text;
  ctx.textAlign = 'center';
  ctx.textBaseline = 'middle';
  if (lead) {
    ctx.font = `500 14px ${FONT}`;
    ctx.fillStyle = pal.inkSoft;
    ctx.fillText(lead, cx, top, W - 40);
  }
  ctx.font = `600 ${Math.round(Math.min(30, Math.max(20, W / 42)))}px ${FONT}`;
  ctx.fillStyle = pal.ink;
  ctx.fillText(name, cx, top + 30, W - 60);
  // 回纹细线：两段直线夹着一小段回纹
  const ruleY = top + 58, half = Math.min(170, W * 0.22);
  ctx.strokeStyle = pal.rule;
  ctx.lineWidth = 1;
  ctx.beginPath();
  ctx.moveTo(cx - half, ruleY); ctx.lineTo(cx - 26, ruleY);
  ctx.moveTo(cx + 26, ruleY); ctx.lineTo(cx + half, ruleY);
  let x = cx - 22;
  ctx.moveTo(x, ruleY);
  for (let k = 0; k < 4; k++) {
    ctx.lineTo(x, ruleY - 5); ctx.lineTo(x + 8, ruleY - 5); ctx.lineTo(x + 8, ruleY + 3); ctx.lineTo(x + 4, ruleY + 3); ctx.lineTo(x + 4, ruleY - 1);
    x += 11;
    ctx.moveTo(x, ruleY);
  }
  ctx.stroke();
  // 航线：两端各一个小岛的圆点，中间虚线，船的位置是一枚实心的小船
  const ly = ruleY + 26, lx0 = cx - half * 0.8, lx1 = cx + half * 0.8;
  ctx.setLineDash([3, 5]);
  ctx.beginPath(); ctx.moveTo(lx0, ly); ctx.lineTo(lx1, ly); ctx.stroke();
  ctx.setLineDash([]);
  ctx.fillStyle = pal.inkSoft;
  for (const px of [lx0, lx1]) { ctx.beginPath(); ctx.arc(px, ly, 3.5, 0, TAU); ctx.fill(); }
  const shipX = lx0 + (lx1 - lx0) * progress;
  ctx.fillStyle = pal.ink;
  ctx.beginPath();
  ctx.moveTo(shipX - 8, ly - 1); ctx.lineTo(shipX + 9, ly - 1); ctx.lineTo(shipX + 5, ly + 4); ctx.lineTo(shipX - 5, ly + 4); ctx.closePath();
  ctx.fill();
  ctx.fillRect(shipX - 0.6, ly - 11, 1.4, 10);
  ctx.beginPath(); ctx.moveTo(shipX + 1, ly - 10); ctx.quadraticCurveTo(shipX + 7, ly - 6, shipX + 1, ly - 2); ctx.closePath(); ctx.fill();
}
