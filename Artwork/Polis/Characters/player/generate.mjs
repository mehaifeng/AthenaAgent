// 玩家（女性）的角色参考图：正 / 侧 / 背三视图，给图生 3D 用。
// 约束来自 Assets/Polis/src/art/bind.mjs：A-pose、腋下以下手臂和躯干之间留空当、两脚分开、衣摆在小腿中段（露出两条腿）；
// 背后的斗篷尾巴不比躯干宽（否则正面投影里会把腋下的空当填上）。
// 配色取自 materials.mjs：playerCloth 0x4f6690、skin 0xcfa384、hair 0x4a3426、iris 0x4a3322、lips 0xa8645a、leather 0x5a3a26。
// 没有赤陶红——那只属于雅典娜。
// 用法：node generate.mjs  →  front.svg / side.svg / back.svg / sheet.svg（同目录）
import { writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const OUT = dirname(fileURLToPath(import.meta.url));
const W = 1400, H = 1900, CX = 700;   // 1 px = 1 mm；头顶 y=100，脚底 y=1800（身高 1.70 m）
const C = {
  bg: '#ffffff',
  cloak: '#4f6690', cloakDark: '#3b4d70', cloakDeep: '#2f3e5c', cloakLight: '#6a80aa',
  linen: '#e6dcc8', linenDark: '#c9bca3', linenDeep: '#b0a287', linenLight: '#f3ecdf',
  skin: '#cfa384', skinDark: '#b88a6b', skinLight: '#dfb898', blush: '#c98f78',
  hair: '#4a3426', hairDark: '#33241a', hairLight: '#6a4d3a',
  iris: '#4a3322', lips: '#a8645a', lipsDark: '#8a4f47', white: '#ece6de',
  leather: '#5a3a26', leatherLight: '#7a5538', sole: '#3e2a1c', cord: '#6b5a43',
  line: 'rgba(40,30,25,0.55)',
};

// ── 几何小工具 ──────────────────────────────────────────────
const f = (n) => Math.round(n * 10) / 10;
/** Catmull-Rom → 三次贝塞尔，tension 0.5。 */
function smooth(pts, closed = true) {
  const n = pts.length;
  const p = (i) => closed ? pts[(i + n) % n] : pts[Math.max(0, Math.min(n - 1, i))];
  let d = `M${f(pts[0][0])},${f(pts[0][1])}`;
  const last = closed ? n : n - 1;
  for (let i = 0; i < last; i++) {
    const p0 = p(i - 1), p1 = p(i), p2 = p(i + 1), p3 = p(i + 2);
    const c1 = [p1[0] + (p2[0] - p0[0]) / 6, p1[1] + (p2[1] - p0[1]) / 6];
    const c2 = [p2[0] - (p3[0] - p1[0]) / 6, p2[1] - (p3[1] - p1[1]) / 6];
    d += ` C${f(c1[0])},${f(c1[1])} ${f(c2[0])},${f(c2[1])} ${f(p2[0])},${f(p2[1])}`;
  }
  return closed ? d + ' Z' : d;
}
/** 右半边（从顶部中线到底部中线）镜像成整圈。 */
const mirror = (half, cx = CX) => [...half, ...half.slice(1, -1).reverse().map(([x, y]) => [2 * cx - x, y])];
const mx = (pts, cx = CX) => pts.map(([x, y]) => [2 * cx - x, y]);
const path = (d, fill, extra = '') => `<path d="${d}" fill="${fill}" ${extra}/>`;
const stroke = (d, color = C.line, w = 2, extra = '') => `<path d="${d}" fill="none" stroke="${color}" stroke-width="${w}" stroke-linecap="round" stroke-linejoin="round" ${extra}/>`;
const line = (pts, color, w) => stroke(smooth(pts, false), color, w);

// ── 手臂（正视 / 背视共用）：A-pose，离竖直 38° ─────────────────
const ARM_ANGLE = 38 * Math.PI / 180;
const SHOULDER = { x: 172, y: 418 };
const L_UPPER = 290, L_FORE = 255, L_HAND = 185;
const T_ELBOW = L_UPPER, T_WRIST = L_UPPER + L_FORE, T_TIP = T_WRIST + L_HAND;
/** side=+1 画面右、-1 画面左；t 沿臂长，off 向外为正。 */
function armPt(side, t, off) {
  const dx = Math.sin(ARM_ANGLE), dy = Math.cos(ARM_ANGLE);
  const nx = Math.cos(ARM_ANGLE), ny = -Math.sin(ARM_ANGLE);
  return [CX + side * (SHOULDER.x + dx * t + nx * off), SHOULDER.y + dy * t + ny * off];
}
function armShape(side, profile) {
  // profile：[[t, 外侧半宽, 内侧半宽], ...]
  const outer = profile.map(([t, o]) => armPt(side, t, o));
  const inner = profile.slice().reverse().map(([t, , i]) => armPt(side, t, -i));
  return smooth([...outer, ...inner]);
}
function arm(side, back) {
  const out = [];
  // 前臂与手（皮肤）
  out.push(path(armShape(side, [[T_ELBOW - 30, 40, 38], [T_ELBOW + 20, 41, 37], [T_ELBOW + 110, 39, 33], [T_WRIST - 10, 29, 26], [T_WRIST + 4, 28, 25]]), C.skin));
  // 前臂内侧的阴影（光从左上来）
  out.push(path(armShape(side, [[T_ELBOW - 10, side > 0 ? 6 : 38, side > 0 ? 37 : -26], [T_ELBOW + 110, side > 0 ? 4 : 39, side > 0 ? 33 : -28], [T_WRIST, side > 0 ? 2 : 28, side > 0 ? 25 : -20]]), C.skinDark, 'opacity="0.45"'));
  // 手：掌心朝大腿、略向前转，看得见手背的一部分和拇指
  const hand = [
    [T_WRIST - 6, 28, 25], [T_WRIST + 30, 34, 30], [T_WRIST + 70, 36, 33], [T_WRIST + 110, 32, 31],
    [T_WRIST + 150, 26, 25], [T_WRIST + 175, 16, 16], [T_TIP, 2, 2],
  ];
  out.push(path(armShape(side, hand), C.skin));
  // 拇指（正视在手的内侧前方，背视看不到）
  if (!back) {
    out.push(path(smooth([armPt(side, T_WRIST + 22, -22), armPt(side, T_WRIST + 50, -42), armPt(side, T_WRIST + 88, -44), armPt(side, T_WRIST + 104, -36), armPt(side, T_WRIST + 92, -26), armPt(side, T_WRIST + 60, -20)]), C.skin, `stroke="${C.skinDark}" stroke-width="1.6"`));
  }
  // 手指缝
  for (const off of [-12, 0, 12]) out.push(line([armPt(side, T_WRIST + 92, off), armPt(side, T_WRIST + 130, off * 0.95), armPt(side, T_WRIST + 165, off * 0.7)], 'rgba(120,80,60,0.5)', 1.6));
  out.push(line([armPt(side, T_WRIST + 78, -22), armPt(side, T_WRIST + 80, 0), armPt(side, T_WRIST + 76, 22)], 'rgba(120,80,60,0.35)', 1.4));
  // 手和前臂的轮廓线
  out.push(stroke(armShape(side, [[T_ELBOW - 30, 40, 38], [T_ELBOW + 20, 41, 37], [T_ELBOW + 110, 39, 33], [T_WRIST - 10, 29, 26], ...hand.slice(1)]), 'rgba(120,80,60,0.6)', 1.8));
  // 袖子（爱奥尼亚式希顿的扣袖，到肘上）
  const sleeve = [[0, 60, 62], [60, 62, 58], [140, 58, 52], [210, 54, 48], [T_ELBOW - 18, 52, 46]];
  out.push(path(armShape(side, sleeve), C.linen));
  out.push(path(armShape(side, [[10, side > 0 ? -10 : 60, side > 0 ? 62 : -14], [140, side > 0 ? -12 : 58, side > 0 ? 52 : -16], [T_ELBOW - 18, side > 0 ? -12 : 52, side > 0 ? 46 : -14]]), C.linenDark, 'opacity="0.55"'));
  // 袖口的褶边
  const hem = [];
  for (let i = 0; i <= 8; i++) {
    const o = 52 - (i / 8) * 98;
    hem.push(armPt(side, T_ELBOW - 18 + (i % 2 ? 6 : 0), o));
  }
  out.push(line(hem, C.linenDeep, 2.2));
  out.push(stroke(armShape(side, sleeve), C.linenDeep, 1.8));
  // 袖上的扣（只在正视，沿上臂上缘）
  if (!back) for (const t of [40, 110, 180]) {
    const [x, y] = armPt(side, t, 30);
    out.push(`<circle cx="${f(x)}" cy="${f(y)}" r="5" fill="${C.cord}"/><circle cx="${f(x - 1.4)}" cy="${f(y - 1.4)}" r="1.6" fill="#a8916c"/>`);
    const [x2, y2] = armPt(side, t + 35, 26);
    out.push(line([[x + 3 * side, y + 4], [x2, y2]], C.linenDeep, 1.4));
  }
  return out.join('\n');
}

// ── 腿与凉鞋（正视 / 背视）──────────────────────────────────
const FOOT_X = 92;   // 两脚中心离中线
function leg(side, back) {
  const c = CX + side * FOOT_X;
  const out = [];
  const shin = [
    [c - 30 * side, 1400], [c - 38 * side, 1490], [c - 34 * side, 1580], [c - 22 * side, 1680], [c - 20 * side, 1735],
    [c + 22 * side, 1735], [c + 26 * side, 1680], [c + 42 * side, 1580], [c + 46 * side, 1490], [c + 40 * side, 1400],
  ];
  out.push(path(smooth(shin), C.skin, `stroke="rgba(120,80,60,0.6)" stroke-width="1.8"`));
  out.push(line([[c + 30 * side, 1470], [c + 36 * side, 1560], [c + 22 * side, 1690]], 'rgba(150,100,75,0.45)', 6));
  // 脚踝的骨点
  out.push(line([[c - 22 * side, 1716], [c - 26 * side, 1726], [c - 22 * side, 1736]], 'rgba(120,80,60,0.45)', 1.6));
  // 脚：正视脚尖朝前、微微外八；背视只看到脚跟
  if (!back) {
    const foot = smooth([
      [c - 24 * side, 1730], [c - 34 * side, 1765], [c - 40 * side, 1790], [c - 30 * side, 1800], [c + 6 * side, 1802],
      [c + 40 * side, 1800], [c + 50 * side, 1788], [c + 38 * side, 1760], [c + 24 * side, 1730],
    ]);
    out.push(path(foot, C.skin, `stroke="rgba(120,80,60,0.6)" stroke-width="1.8"`));
    // 脚趾
    for (let i = 0; i < 5; i++) {
      const x = c + side * (-30 + i * 17.5 + (i === 4 ? -2 : 0));
      out.push(`<ellipse cx="${f(x)}" cy="1795" rx="${i === 0 ? 9 : 7}" ry="6" fill="${C.skinLight}" stroke="rgba(120,80,60,0.5)" stroke-width="1.2"/>`);
    }
    // 鞋底
    out.push(path(smooth([[c - 44 * side, 1798], [c + 6 * side, 1796], [c + 54 * side, 1798], [c + 54 * side, 1808], [c + 6 * side, 1810], [c - 44 * side, 1808]]), C.sole));
  } else {
    const heel = smooth([[c - 24 * side, 1730], [c - 30 * side, 1770], [c - 26 * side, 1796], [c, 1802], [c + 26 * side, 1796], [c + 30 * side, 1770], [c + 24 * side, 1730]]);
    out.push(path(heel, C.skinDark, `stroke="rgba(120,80,60,0.6)" stroke-width="1.8"`));
    out.push(path(smooth([[c - 34 * side, 1798], [c, 1796], [c + 34 * side, 1798], [c + 34 * side, 1808], [c, 1810], [c - 34 * side, 1808]]), C.sole));
  }
  // 凉鞋皮带：脚背一道、脚踝一圈、中间一根
  out.push(line([[c - 32 * side, 1772], [c + 6 * side, 1762], [c + 42 * side, 1772]], C.leather, 7));
  out.push(line([[c - 26 * side, 1712], [c, 1718], [c + 28 * side, 1712]], C.leather, 6));
  if (!back) out.push(line([[c + 2 * side, 1718], [c + 4 * side, 1740], [c + 6 * side, 1763]], C.leather, 5));
  else out.push(line([[c, 1718], [c, 1760], [c, 1796]], C.leather, 6));
  return out.join('\n');
}

// ── 衣裙（希顿）正 / 背 ────────────────────────────────────
const DRESS_HALF = [
  [CX, 395], [CX + 120, 405], [CX + 150, 440], [CX + 152, 480], [CX + 146, 540], [CX + 128, 600], [CX + 120, 632],
  [CX + 136, 660], [CX + 158, 720], [CX + 172, 790], [CX + 176, 900], [CX + 182, 1050], [CX + 190, 1200], [CX + 200, 1330],
  [CX + 206, 1420], [CX + 188, 1430], [CX + 160, 1418], [CX + 130, 1432], [CX + 98, 1420], [CX + 66, 1434], [CX + 34, 1422], [CX, 1434],
];
function dress(back) {
  const out = [];
  const body = smooth(mirror(DRESS_HALF));
  out.push(`<path d="${body}" fill="url(#linenGrad)"/>`);
  // 竖褶：从腰带往下放射
  const folds = [-150, -112, -74, -38, 0, 36, 72, 110, 148];
  folds.forEach((x0, i) => {
    const xTop = CX + x0 * 0.55, xBot = CX + x0 * 1.18;
    const deep = i % 2 ? C.linenDark : C.linenDeep;
    out.push(line([[xTop, 660], [CX + x0 * 0.85, 900], [CX + x0 * 1.05, 1150], [xBot, 1420]], deep, i % 2 ? 3 : 4.5));
    out.push(line([[xTop + 9, 680], [CX + x0 * 0.85 + 12, 920], [CX + x0 * 1.05 + 13, 1160], [xBot + 14, 1410]], 'rgba(255,250,240,0.55)', 3));
  });
  // 胸前与腰带上方的布（kolpos：衣服从腰带上翻出来一点）
  if (!back) {
    out.push(line([[CX - 110, 600], [CX - 60, 622], [CX, 628], [CX + 60, 622], [CX + 110, 600]], C.linenDark, 3));
    out.push(line([[CX - 60, 470], [CX - 40, 540], [CX - 20, 600]], C.linenDark, 2.4));
    out.push(line([[CX + 60, 470], [CX + 40, 540], [CX + 20, 600]], C.linenDark, 2.4));
    // 胸部的形体（两道柔和的下缘阴影）
    out.push(line([[CX - 120, 545], [CX - 80, 568], [CX - 30, 560]], 'rgba(150,135,110,0.55)', 5));
    out.push(line([[CX + 120, 545], [CX + 80, 568], [CX + 30, 560]], 'rgba(150,135,110,0.55)', 5));
  } else {
    out.push(line([[CX, 640], [CX, 1420]], C.linenDark, 2.4));
  }
  // 腰带：一根细绳，正面打结垂下两头
  out.push(path(smooth([[CX - 122, 630], [CX, 642], [CX + 122, 630], [CX + 124, 644], [CX, 656], [CX - 124, 644]]), C.cord));
  out.push(line([[CX - 120, 637], [CX, 649], [CX + 120, 637]], '#8a7656', 1.4));
  if (!back) {
    out.push(`<ellipse cx="${CX + 18}" cy="650" rx="11" ry="8" fill="${C.cord}"/>`);
    out.push(line([[CX + 16, 656], [CX + 12, 700], [CX + 16, 745]], C.cord, 5));
    out.push(line([[CX + 24, 656], [CX + 30, 696], [CX + 28, 735]], C.cord, 5));
  }
  // 下摆的边与轮廓
  out.push(stroke(body, C.linenDeep, 2));
  const hem = mirror(DRESS_HALF).slice(0).filter(([, y]) => y > 1400);
  out.push(line([[CX - 206, 1420], [CX - 188, 1430], [CX - 160, 1418], [CX - 130, 1432], [CX - 98, 1420], [CX - 66, 1434], [CX - 34, 1422], [CX, 1434], [CX + 34, 1422], [CX + 66, 1434], [CX + 98, 1420], [CX + 130, 1432], [CX + 160, 1418], [CX + 188, 1430], [CX + 206, 1420]].map(([x, y]) => [x, y - 14]), C.linenDark, 2));
  void hem;
  return out.join('\n');
}

// ── 斗篷：兜帽 + 披肩 ──────────────────────────────────────
// 正面：披肩盖住两肩和上臂上端、在胸口收成一道柔和的弧；在腋下那一点止住，不搭到臂和躯干之间。
function cloakFront() {
  const out = [];
  const yoke = [
    [CX, 384], [CX + 40, 378], [CX + 76, 352], [CX + 104, 336], [CX + 124, 352],
    armPt(1, -8, 66), armPt(1, 40, 72), armPt(1, 92, 66),   // 搭在上臂外侧
    armPt(1, 112, 20), armPt(1, 92, -44),                     // 绕过上臂，回到腋下
    [CX + 146, 470], [CX + 110, 520], [CX + 56, 556], [CX, 566],
  ];
  out.push(`<path d="${smooth(mirror(yoke))}" fill="url(#cloakGrad)"/>`);
  out.push(stroke(smooth(mirror(yoke)), C.cloakDeep, 2));
  // 披肩的褶
  for (const s of [1, -1]) {
    out.push(line([[CX + s * 40, 392], [CX + s * 52, 460], [CX + s * 40, 550]], C.cloakDark, 4));
    out.push(line([[CX + s * 90, 360], [CX + s * 112, 440], [CX + s * 112, 505]], C.cloakDark, 4));
    out.push(line([[CX + s * 132, 380], armPt(s, 30, 30), armPt(s, 90, 30)], C.cloakDark, 3.5));
    out.push(line([[CX + s * 66, 376], [CX + s * 78, 440], [CX + s * 74, 520]], 'rgba(160,180,215,0.45)', 3));
  }
  // 披肩的滚边（一道深色的织边）
  out.push(line(mirror(yoke).slice(10, 19), C.cloakDeep, 5));
  // 领口：兜帽在脖子前绕一圈，留一道 V 形的口子
  out.push(line([[CX - 104, 340], [CX - 70, 362], [CX - 34, 384], [CX, 392], [CX + 34, 384], [CX + 70, 362], [CX + 104, 340]], C.cloakDeep, 4));
  // 胸口的扣针：小铜扣（不是红色）
  out.push(`<circle cx="${CX}" cy="560" r="10" fill="#a47a48" stroke="#6e5130" stroke-width="2"/><circle cx="${CX - 3}" cy="557" r="3" fill="#d4b07c"/>`);
  return out.join('\n');
}
function hoodBack(back) {
  // 兜帽外形（在头后面）：比头大一圈，顶上略尖、自然塌下来
  const half = back
    ? [[CX, 64], [CX + 52, 72], [CX + 100, 110], [CX + 124, 180], [CX + 128, 250], [CX + 122, 300], [CX + 128, 340], [CX + 70, 350], [CX, 356]]
    : [[CX, 68], [CX + 60, 78], [CX + 104, 118], [CX + 124, 190], [CX + 126, 260], [CX + 116, 320], [CX + 120, 380], [CX + 50, 404], [CX, 404]];
  return `<path d="${smooth(mirror(half))}" fill="url(#hoodGrad)" stroke="${C.cloakDeep}" stroke-width="2"/>`;
}
function hoodRim() {
  // 兜帽的开口：一道翻过来的宽边，框住脸
  const outer = [[CX, 92], [CX + 58, 100], [CX + 98, 140], [CX + 112, 210], [CX + 108, 275], [CX + 96, 322], [CX + 74, 350]];
  const inner = [[CX + 66, 330], [CX + 86, 300], [CX + 94, 250], [CX + 92, 190], [CX + 76, 140], [CX + 40, 116], [CX, 112]];
  const out = [];
  for (const s of [1, -1]) {
    const o = s > 0 ? outer : mx(outer), i = s > 0 ? inner : mx(inner);
    out.push(path(smooth([...o, ...i]), s > 0 ? C.cloakDark : C.cloak, `stroke="${C.cloakDeep}" stroke-width="2"`));
    out.push(line(s > 0 ? [[CX + 30, 104], [CX + 82, 136], [CX + 101, 210], [CX + 98, 280]] : mx([[CX + 30, 104], [CX + 82, 136], [CX + 101, 210], [CX + 98, 280]]), 'rgba(160,180,215,0.45)', 3));
  }
  return out.join('\n');
}

// ── 头（正视）──────────────────────────────────────────────
function headFront() {
  const out = [];
  // 脖子
  out.push(path(smooth([[CX - 34, 300], [CX - 36, 360], [CX - 50, 392], [CX + 50, 392], [CX + 36, 360], [CX + 34, 300]]), C.skin));
  out.push(path(smooth([[CX - 34, 312], [CX, 336], [CX + 34, 312], [CX + 32, 340], [CX, 352], [CX - 32, 340]]), C.skinDark, 'opacity="0.5"'));
  // 头发在兜帽里的那一片（耳朵后面、脸两侧）
  out.push(path(smooth(mirror([[CX, 112], [CX + 60, 122], [CX + 92, 170], [CX + 96, 250], [CX + 90, 312], [CX + 66, 316], [CX, 300]])), C.hair));
  // 脸
  const face = [[CX, 118], [CX + 50, 128], [CX + 74, 165], [CX + 79, 215], [CX + 74, 262], [CX + 60, 295], [CX + 32, 320], [CX, 328]];
  out.push(`<path d="${smooth(mirror(face))}" fill="url(#faceGrad)" stroke="rgba(130,90,65,0.55)" stroke-width="1.6"/>`);
  // 头发：中分，从额头两边梳下去、盖住太阳穴和耳朵
  for (const s of [1, -1]) {
    const lock = [[CX + 2, 118], [CX + 36, 122], [CX + 70, 150], [CX + 84, 200], [CX + 82, 262], [CX + 74, 292], [CX + 66, 250], [CX + 62, 196], [CX + 46, 158], [CX + 16, 140]];
    out.push(path(smooth(s > 0 ? lock : mx(lock)), C.hair));
    for (const k of [0, 1, 2]) {
      const strand = [[CX + 10 + k * 8, 126 + k * 2], [CX + 46 + k * 6, 148 + k * 4], [CX + 68 + k * 3, 196], [CX + 72 + k * 2, 250]];
      out.push(line(s > 0 ? strand : mx(strand), k === 1 ? C.hairLight : C.hairDark, 2));
    }
  }
  // 眉
  for (const s of [1, -1]) {
    const brow = [[CX + 14, 192], [CX + 32, 185], [CX + 52, 186], [CX + 64, 193]];
    out.push(line(s > 0 ? brow : mx(brow), C.hairDark, 4.2));
  }
  // 眼
  for (const s of [1, -1]) {
    const ex = CX + s * 37, ey = 214;
    const eye = [[ex - 17, ey + 1], [ex - 6, ey - 7], [ex + 8, ey - 6], [ex + 17, ey + 1], [ex + 6, ey + 6], [ex - 7, ey + 6]];
    out.push(path(smooth(s > 0 ? eye : eye.map(([x, y]) => [2 * ex - x, y])), C.white));
    out.push(`<circle cx="${ex}" cy="${ey}" r="7" fill="${C.iris}"/><circle cx="${ex}" cy="${ey}" r="3.2" fill="#1d1510"/><circle cx="${ex - 2.5}" cy="${ey - 2.5}" r="1.8" fill="#fff" opacity="0.85"/>`);
    // 上眼睑：深一些的线，外眼角略长
    const lid = [[ex - 18 * s, ey + 1], [ex - 6 * s, ey - 8], [ex + 8 * s, ey - 7], [ex + 19 * s, ey]];
    out.push(line(lid, '#2e211a', 2.8));
    out.push(line([[ex - 14 * s, ey - 11], [ex, ey - 15], [ex + 14 * s, ey - 12]], 'rgba(140,95,70,0.5)', 1.6));
    out.push(line([[ex - 12 * s, ey + 6], [ex, ey + 8], [ex + 12 * s, ey + 5]], 'rgba(140,95,70,0.45)', 1.3));
  }
  // 鼻：鼻梁一侧的阴影 + 鼻底
  out.push(line([[CX - 10, 214], [CX - 9, 238], [CX - 12, 256]], 'rgba(160,110,85,0.45)', 3));
  out.push(line([[CX - 15, 260], [CX - 9, 266], [CX, 268], [CX + 9, 266], [CX + 15, 260]], 'rgba(130,85,62,0.7)', 2));
  out.push(`<ellipse cx="${CX - 7}" cy="264" rx="4" ry="2.2" fill="rgba(110,70,50,0.6)"/><ellipse cx="${CX + 7}" cy="264" rx="4" ry="2.2" fill="rgba(110,70,50,0.6)"/>`);
  // 嘴
  out.push(path(smooth([[CX - 22, 289], [CX - 10, 282], [CX, 285], [CX + 10, 282], [CX + 22, 289], [CX, 290]]), C.lips));
  out.push(path(smooth([[CX - 21, 290], [CX, 291], [CX + 21, 290], [CX + 10, 300], [CX, 302], [CX - 10, 300]]), C.lips, 'opacity="0.88"'));
  out.push(line([[CX - 22, 289], [CX - 8, 290], [CX, 291], [CX + 8, 290], [CX + 22, 289]], C.lipsDark, 1.8));
  out.push(line([[CX - 8, 296], [CX + 8, 296]], 'rgba(255,230,215,0.4)', 2));
  // 人中、下巴、脸颊
  out.push(line([[CX - 4, 270], [CX - 4, 280]], 'rgba(160,110,85,0.3)', 1.4));
  out.push(line([[CX - 12, 314], [CX, 318], [CX + 12, 314]], 'rgba(160,110,85,0.35)', 2));
  out.push(`<ellipse cx="${CX - 50}" cy="252" rx="17" ry="10" fill="${C.blush}" opacity="0.22"/><ellipse cx="${CX + 50}" cy="252" rx="17" ry="10" fill="${C.blush}" opacity="0.22"/>`);
  return out.join('\n');
}

// ── 正视图 ────────────────────────────────────────────────
function front() {
  return [
    hoodBack(false),
    leg(-1, false), leg(1, false),
    dress(false),
    arm(-1, false), arm(1, false),
    headFront(),
    cloakFront(),
    hoodRim(),
  ].join('\n');
}

// ── 背视图 ────────────────────────────────────────────────
function back() {
  const out = [leg(-1, true), leg(1, true), dress(true), arm(-1, true), arm(1, true)];
  // 斗篷：盖住两肩、背上收窄成一条垂到小腿的长尾——宽不过躯干
  const half = [
    [CX, 300], [CX + 112, 350], armPt(1, -8, 66), armPt(1, 40, 72), armPt(1, 92, 66), armPt(1, 112, 20), armPt(1, 92, -44),
    [CX + 140, 480], [CX + 128, 600], [CX + 120, 760], [CX + 128, 1000], [CX + 136, 1250], [CX + 130, 1450], [CX + 92, 1476], [CX + 44, 1466], [CX, 1480],
  ];
  out.push(`<path d="${smooth(mirror(half))}" fill="url(#cloakGradV)"/>`);
  out.push(stroke(smooth(mirror(half)), C.cloakDeep, 2));
  for (const x of [-96, -56, -18, 22, 60, 98]) {
    out.push(line([[CX + x * 0.7, 420], [CX + x * 0.9, 700], [CX + x, 1000], [CX + x * 1.05, 1450]], C.cloakDark, x % 4 ? 4.5 : 3));
    out.push(line([[CX + x * 0.7 + 10, 440], [CX + x * 0.9 + 12, 720], [CX + x + 13, 1020], [CX + x * 1.05 + 13, 1440]], 'rgba(160,180,215,0.35)', 2.6));
  }
  for (const s of [1, -1]) out.push(line([[CX + s * 132, 380], armPt(s, 30, 30), armPt(s, 90, 30)], C.cloakDark, 3.5));
  out.push(line([[CX - 130, 1450], [CX - 92, 1476], [CX - 44, 1466], [CX, 1480], [CX + 44, 1466], [CX + 92, 1476], [CX + 130, 1450]].map(([x, y]) => [x, y - 12]), C.cloakDeep, 5));
  // 兜帽（背后）：从头顶垂到肩胛，中间一道缝
  out.push(hoodBack(true));
  out.push(line([[CX, 70], [CX + 2, 160], [CX, 260], [CX - 2, 350]], C.cloakDeep, 3));
  out.push(line([[CX + 60, 90], [CX + 90, 180], [CX + 96, 300]], 'rgba(160,180,215,0.45)', 3));
  out.push(line([[CX - 60, 90], [CX - 92, 190], [CX - 100, 300]], C.cloakDark, 4));
  return out.join('\n');
}

// ── 侧视图（面朝画面右；画的是她的右侧）──────────────────────
function side() {
  const out = [];
  const B = 690;   // 脚踝的竖线
  // 斗篷尾巴（在背后）
  const tail = [[B - 50, 330], [B - 110, 420], [B - 122, 600], [B - 118, 800], [B - 120, 1100], [B - 112, 1460], [B - 80, 1472], [B - 60, 1300], [B - 72, 900], [B - 74, 700], [B - 66, 480]];
  out.push(`<path d="${smooth(tail)}" fill="url(#cloakGradV)" stroke="${C.cloakDeep}" stroke-width="2"/>`);
  out.push(line([[B - 92, 460], [B - 100, 800], [B - 96, 1200], [B - 92, 1460]], C.cloakDark, 4));
  // 远侧的腿（只露一点）与近侧的腿
  for (const [dx, tone] of [[14, C.skinDark], [0, C.skin]]) {
    const legPts = [[B - 36 + dx, 1400], [B - 52 + dx, 1500], [B - 42 + dx, 1600], [B - 22 + dx, 1700], [B - 24 + dx, 1740], [B + 22 + dx, 1740], [B + 26 + dx, 1690], [B + 38 + dx, 1600], [B + 40 + dx, 1500], [B + 34 + dx, 1400]];
    out.push(path(smooth(legPts), tone, `stroke="rgba(120,80,60,0.6)" stroke-width="1.8"`));
    const foot = [[B - 26 + dx, 1728], [B - 38 + dx, 1776], [B - 36 + dx, 1800], [B + 20 + dx, 1802], [B + 96 + dx, 1802], [B + 108 + dx, 1794], [B + 98 + dx, 1782], [B + 52 + dx, 1764], [B + 24 + dx, 1736]];
    out.push(path(smooth(foot), tone, `stroke="rgba(120,80,60,0.6)" stroke-width="1.8"`));
    out.push(path(smooth([[B - 42 + dx, 1798], [B + 30 + dx, 1797], [B + 112 + dx, 1798], [B + 112 + dx, 1808], [B + 30 + dx, 1810], [B - 42 + dx, 1808]]), C.sole));
    out.push(line([[B - 28 + dx, 1712], [B, 1720], [B + 26 + dx, 1714]], C.leather, 6));
    out.push(line([[B + 40 + dx, 1776], [B + 60 + dx, 1772], [B + 74 + dx, 1786]], C.leather, 7));
    out.push(line([[B + 4 + dx, 1720], [B + 30 + dx, 1752], [B + 56 + dx, 1772]], C.leather, 5));
  }
  // 衣裙（侧面）
  const dressPts = [
    [B - 20, 392], [B + 46, 410], [B + 74, 470], [B + 88, 515], [B + 70, 565], [B + 58, 625], [B + 70, 668], [B + 76, 760], [B + 82, 900],
    [B + 90, 1100], [B + 100, 1300], [B + 106, 1420], [B + 70, 1432], [B + 30, 1420], [B - 10, 1434], [B - 50, 1420], [B - 78, 1430],
    [B - 74, 1250], [B - 70, 1050], [B - 72, 900], [B - 82, 790], [B - 62, 680], [B - 46, 625], [B - 62, 520], [B - 66, 430],
  ];
  out.push(`<path d="${smooth(dressPts)}" fill="url(#linenGradSide)" stroke="${C.linenDeep}" stroke-width="2"/>`);
  for (const [x0, x1, w] of [[40, 80, 4], [10, 40, 3], [-20, -10, 4], [-50, -60, 3]]) {
    out.push(line([[B + x0 * 0.6, 670], [B + x0 * 0.9, 900], [B + x1, 1410]], w > 3 ? C.linenDeep : C.linenDark, w));
  }
  out.push(path(smooth([[B - 48, 630], [B + 58, 630], [B + 60, 646], [B - 46, 646]]), C.cord));
  out.push(line([[B + 66, 640], [B + 74, 690], [B + 70, 735]], C.cord, 5));
  // 斗篷在肩上（侧面）：盖住肩和上背，前面到胸口
  const yoke = [[B - 40, 320], [B + 20, 336], [B + 60, 380], [B + 84, 450], [B + 92, 520], [B + 70, 540], [B + 40, 500], [B + 10, 488], [B - 30, 498], [B - 66, 480], [B - 100, 440], [B - 96, 380]];
  out.push(`<path d="${smooth(yoke)}" fill="url(#cloakGrad)" stroke="${C.cloakDeep}" stroke-width="2"/>`);
  out.push(line([[B + 20, 360], [B + 50, 440], [B + 70, 520]], C.cloakDark, 4));
  out.push(line([[B - 40, 360], [B - 60, 420], [B - 70, 470]], C.cloakDark, 4));
  // 近侧的手臂（A-pose 向外伸，侧面投影几乎竖直、略靠前）
  const sh = [B + 6, 420];
  const sleeve = [[sh[0] - 50, sh[1] + 30], [sh[0] - 48, 560], [sh[0] - 40, 680], [sh[0] + 44, 690], [sh[0] + 52, 560], [sh[0] + 50, sh[1] + 20]];
  const fore = [[sh[0] - 34, 670], [sh[0] - 30, 760], [sh[0] - 20, 860], [sh[0] + 26, 862], [sh[0] + 36, 760], [sh[0] + 36, 670]];
  out.push(path(smooth(fore), C.skin, `stroke="rgba(120,80,60,0.6)" stroke-width="1.8"`));
  // 手：侧面看见手背，四指朝下并拢，拇指在前
  const hand = [[sh[0] - 22, 852], [sh[0] - 34, 900], [sh[0] - 36, 960], [sh[0] - 24, 1004], [sh[0] - 6, 1012], [sh[0] + 14, 1000], [sh[0] + 26, 950], [sh[0] + 30, 890], [sh[0] + 26, 852]];
  out.push(path(smooth(hand), C.skin, `stroke="rgba(120,80,60,0.6)" stroke-width="1.8"`));
  out.push(path(smooth([[sh[0] + 24, 870], [sh[0] + 46, 900], [sh[0] + 50, 940], [sh[0] + 40, 950], [sh[0] + 28, 920]]), C.skin, `stroke="rgba(120,80,60,0.6)" stroke-width="1.6"`));
  for (const x of [-16, -2, 12]) out.push(line([[sh[0] + x, 930], [sh[0] + x + 1, 970], [sh[0] + x * 0.8, 1000]], 'rgba(120,80,60,0.45)', 1.5));
  out.push(path(smooth(sleeve), C.linen, `stroke="${C.linenDeep}" stroke-width="2"`));
  out.push(line([[sh[0] + 10, 470], [sh[0] + 14, 580], [sh[0] + 8, 680]], C.linenDark, 3));
  out.push(line([[sh[0] - 44, 680], [sh[0] - 20, 690], [sh[0], 684], [sh[0] + 20, 692], [sh[0] + 44, 686]], C.linenDeep, 2.2));
  // 斗篷在上臂外侧的那一段
  out.push(path(smooth([[sh[0] - 54, 430], [sh[0] - 30, 400], [sh[0] + 30, 398], [sh[0] + 58, 430], [sh[0] + 52, 520], [sh[0], 534], [sh[0] - 52, 520]]), C.cloak, `stroke="${C.cloakDeep}" stroke-width="2"`));
  out.push(line([[sh[0] - 20, 420], [sh[0] - 24, 520]], C.cloakDark, 3.5));
  out.push(line([[sh[0] + 22, 420], [sh[0] + 26, 515]], C.cloakDark, 3.5));
  // 脖子与头（侧面）
  out.push(path(smooth([[B - 28, 300], [B + 40, 300], [B + 46, 350], [B + 56, 396], [B - 20, 400], [B - 30, 350]]), C.skin));
  out.push(path(smooth([[B + 10, 300], [B + 46, 330], [B + 50, 380], [B + 24, 372]]), C.skinDark, 'opacity="0.4"'));
  // 兜帽外形（侧面）
  const hood = [[B + 90, 108], [B + 40, 72], [B - 40, 66], [B - 100, 100], [B - 128, 180], [B - 126, 270], [B - 104, 340], [B - 50, 352], [B + 20, 346], [B + 54, 336], [B + 50, 290], [B + 30, 250], [B + 38, 160]];
  out.push(`<path d="${smooth(hood)}" fill="url(#hoodGrad)" stroke="${C.cloakDeep}" stroke-width="2"/>`);
  // 头发（兜帽口里）
  out.push(path(smooth([[B + 88, 116], [B + 70, 108], [B + 44, 120], [B + 30, 180], [B + 36, 260], [B + 50, 296], [B + 66, 230], [B + 72, 170], [B + 92, 134]]), C.hair));
  // 脸的侧影：额、眉骨、鼻、唇、下巴、下颌
  const face = [
    [B + 84, 120], [B + 98, 150], [B + 102, 192], [B + 98, 206], [B + 104, 222], [B + 112, 248], [B + 116, 262], [B + 104, 268],
    [B + 102, 280], [B + 106, 287], [B + 100, 293], [B + 104, 301], [B + 96, 312], [B + 92, 326], [B + 72, 334], [B + 52, 330],
    [B + 42, 296], [B + 56, 240], [B + 64, 170], [B + 72, 128],
  ];
  out.push(`<path d="${smooth(face)}" fill="url(#faceGradSide)" stroke="rgba(130,90,65,0.6)" stroke-width="1.6"/>`);
  // 侧脸上的头发（盖住耳朵）
  out.push(path(smooth([[B + 72, 128], [B + 56, 150], [B + 44, 200], [B + 46, 262], [B + 56, 300], [B + 58, 250], [B + 66, 200], [B + 82, 140]]), C.hair));
  out.push(line([[B + 70, 140], [B + 56, 200], [B + 54, 270]], C.hairLight, 2));
  // 眼、眉、鼻孔、嘴角
  out.push(line([[B + 74, 196], [B + 86, 190], [B + 98, 194]], C.hairDark, 4));
  out.push(path(smooth([[B + 84, 214], [B + 94, 209], [B + 99, 216], [B + 92, 220]]), C.white));
  out.push(`<circle cx="${B + 95}" cy="215" r="4.2" fill="${C.iris}"/>`);
  out.push(line([[B + 82, 213], [B + 92, 207], [B + 100, 212]], '#2e211a', 2.6));
  out.push(`<ellipse cx="${B + 102}" cy="263" rx="5" ry="2.4" fill="rgba(110,70,50,0.6)"/>`);
  out.push(path(smooth([[B + 102, 282], [B + 105, 287], [B + 100, 293], [B + 103, 300], [B + 94, 299], [B + 94, 286]]), C.lips));
  out.push(line([[B + 92, 292], [B + 101, 292]], C.lipsDark, 1.8));
  out.push(`<ellipse cx="${B + 76}" cy="252" rx="12" ry="8" fill="${C.blush}" opacity="0.22"/>`);
  // 兜帽的翻边（侧面看是一道斜带）
  out.push(path(smooth([[B + 92, 104], [B + 74, 96], [B + 50, 120], [B + 34, 200], [B + 36, 290], [B + 52, 340], [B + 66, 334], [B + 52, 290], [B + 50, 200], [B + 62, 128], [B + 84, 114]]), C.cloakDark, `stroke="${C.cloakDeep}" stroke-width="2"`));
  return out.join('\n');
}

// ── 输出 ─────────────────────────────────────────────────
const DEFS = `<defs>
  <linearGradient id="linenGrad" x1="0" x2="1" y1="0" y2="0">
    <stop offset="0" stop-color="${C.linenDark}"/><stop offset="0.22" stop-color="${C.linen}"/><stop offset="0.45" stop-color="${C.linenLight}"/><stop offset="0.75" stop-color="${C.linen}"/><stop offset="1" stop-color="${C.linenDark}"/>
  </linearGradient>
  <linearGradient id="linenGradSide" x1="0" x2="1" y1="0" y2="0">
    <stop offset="0" stop-color="${C.linenDark}"/><stop offset="0.5" stop-color="${C.linen}"/><stop offset="0.8" stop-color="${C.linenLight}"/><stop offset="1" stop-color="${C.linen}"/>
  </linearGradient>
  <linearGradient id="cloakGrad" x1="0" x2="1" y1="0" y2="0">
    <stop offset="0" stop-color="${C.cloakDark}"/><stop offset="0.35" stop-color="${C.cloak}"/><stop offset="0.5" stop-color="${C.cloakLight}"/><stop offset="0.7" stop-color="${C.cloak}"/><stop offset="1" stop-color="${C.cloakDark}"/>
  </linearGradient>
  <linearGradient id="cloakGradV" x1="0" x2="1" y1="0" y2="0">
    <stop offset="0" stop-color="${C.cloakDark}"/><stop offset="0.4" stop-color="${C.cloak}"/><stop offset="0.6" stop-color="${C.cloakLight}"/><stop offset="1" stop-color="${C.cloakDark}"/>
  </linearGradient>
  <radialGradient id="hoodGrad" cx="0.45" cy="0.35" r="0.7">
    <stop offset="0" stop-color="${C.cloakLight}"/><stop offset="0.6" stop-color="${C.cloak}"/><stop offset="1" stop-color="${C.cloakDark}"/>
  </radialGradient>
  <radialGradient id="faceGrad" cx="0.45" cy="0.42" r="0.65">
    <stop offset="0" stop-color="${C.skinLight}"/><stop offset="0.7" stop-color="${C.skin}"/><stop offset="1" stop-color="${C.skinDark}"/>
  </radialGradient>
  <linearGradient id="faceGradSide" x1="1" x2="0" y1="0" y2="0">
    <stop offset="0" stop-color="${C.skinLight}"/><stop offset="0.6" stop-color="${C.skin}"/><stop offset="1" stop-color="${C.skinDark}"/>
  </linearGradient>
</defs>`;

const svg = (body, w = W, h = H, bg = true) =>
  `<svg xmlns="http://www.w3.org/2000/svg" width="${w}" height="${h}" viewBox="0 0 ${w} ${h}">\n${DEFS}\n${bg ? `<rect width="${w}" height="${h}" fill="${C.bg}"/>` : ''}\n${body}\n</svg>\n`;

const views = { front: front(), side: side(), back: back() };
for (const [name, body] of Object.entries(views)) writeFileSync(join(OUT, `${name}.svg`), svg(body));

// 三视图合一张：同一条地平线、同一比例，带标注（不要拿这张喂模型——标注会被当成几何）
const SW = W * 3;
const placed = [['front', 0, '正面 FRONT'], ['side', W, '侧面 SIDE（右侧）'], ['back', 2 * W, '背面 BACK']];
let sheet = '';
for (const [name, x, label] of placed) {
  sheet += `<g transform="translate(${x},0)">${views[name]}</g>\n`;
  sheet += `<text x="${x + CX}" y="1870" text-anchor="middle" font-family="sans-serif" font-size="34" fill="#666">${label}</text>\n`;
}
for (const [y, t] of [[100, '1.70 m'], [1800, '0']]) {
  sheet += `<line x1="0" x2="${SW}" y1="${y}" y2="${y}" stroke="#bbb" stroke-dasharray="10 10" stroke-width="2"/><text x="12" y="${y - 10}" font-family="sans-serif" font-size="26" fill="#999">${t}</text>\n`;
}
writeFileSync(join(OUT, 'sheet.svg'), svg(sheet, SW, H));
console.log('wrote front.svg side.svg back.svg sheet.svg →', OUT);
