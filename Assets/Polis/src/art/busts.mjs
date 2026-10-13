// 2D 半身像（设计稿第 1、8 节：3D 人物只在游戏视距出现；对话等特写用 2D 半身像）。M3（决定 3、4、5）重做：仍是 SVG、仍是侧脸朝右，
// 但从陶瓶式的线描改成**灰度写实的素描**——皮肤有明暗体积（眼窝、鼻侧、颊骨、颌下的阴影，额、颧、下巴的高光），
// 眼睛有眼白、虹膜、瞳孔和一点高光，嘴唇有上下两片，头发与胡须有发丝的走向，衣料有褶的阴影；轮廓线变细、变淡，让体积说话。
// 全部由代码生成：每个人物一组参数（发式、胡须、头饰、衣着、手里的东西），每人三种神情（平常 / 说话 / 微笑）。
// 生成结果是纯数据（一组 SVG 路径，只有 M / L / C / Q / Z 命令；柔和的阴影是几层同心椭圆叠出来的，不用渐变），不碰 DOM：
//   - 城里的对话面板和对话栏用 drawBust() 画在 canvas 上（Path2D 吃同一套 SVG 路径语法）——和名牌、旁白一样不建 DOM；
//   - 美术审查用 toSvg() 导出独立的 .svg 文件。
// 调子：象牙色的纸上只有灰——点缀色只给"轮到你了"（第 8 节），半身像不用它。画框顶上一条回纹带，是瓶肩的纹饰。

export const BUST_VIEWBOX = Object.freeze({ width: 120, height: 150 });
export const EXPRESSIONS = Object.freeze(['neutral', 'speak', 'smile']);

const INK = '#2a2724';
const SHADOW = '#2a2724';
const HIGHLIGHT = '#ffffff';
const GROUND = '#f3ede1';
const SKIN = '#cfc8bc';
const SCLERA = '#ebe6de';
const IRIS = '#4a443d';
const PUPIL = '#15120f';
const HAIR = '#35312d';
const HAIR_LIGHT = '#8a827a';
const GREY_HAIR = '#b2aca4';
const LIP_UPPER = '#7f736a';
const LIP_LOWER = '#a3968b';
const MOUTH = '#2b2622';
const METAL = '#9a948b';
const WOOD = '#7a7066';

/** 人物 → 参数。居民的名字与 PolisResidentKind 的 camelCase 一致。wash：衣料的深浅（0 最浅）。 */
export const CHARACTERS = Object.freeze({
  athena: { hair: 'bun', headgear: 'fillet', garment: 'peplos', beard: null, attribute: null, age: 'young', wash: 0.12, jaw: 0.25, female: true },
  player: { hair: 'long', headgear: null, garment: 'chiton', beard: null, attribute: null, age: 'young', wash: 0.5, jaw: 0.3, female: true },   // 2026-10-12：不戴兜帽，长发披到肩上
  surveyor: { hair: 'short', headgear: 'petasos', garment: 'himation', beard: 'short', attribute: 'rod', age: 'adult', wash: 0.32, jaw: 0.55 },
  scribe: { hair: 'curly', headgear: 'headband', garment: 'chiton', beard: null, attribute: 'stylus', age: 'young', wash: 0.2, jaw: 0.4 },
  accountant: { hair: 'receding', headgear: null, garment: 'himation', beard: 'short', attribute: 'abacus', age: 'old', wash: 0.36, jaw: 0.5 },
  smith: { hair: 'short', headgear: 'pilos', garment: 'exomis', beard: 'full', attribute: 'hammer', age: 'adult', wash: 0.42, jaw: 0.8 },
  sailor: { hair: 'curly', headgear: 'pilosTilted', garment: 'exomis', beard: 'stubble', attribute: 'rope', age: 'adult', wash: 0.38, jaw: 0.6 },
  librarian: { hair: 'fringe', headgear: null, garment: 'himation', beard: 'long', attribute: 'scrolls', age: 'old', wash: 0.28, jaw: 0.45 },
  herald: { hair: 'short', headgear: 'petasosBack', garment: 'chlamys', beard: 'short', attribute: 'staff', age: 'adult', wash: 0.3, jaw: 0.55 },
  painter: { hair: 'sakkos', headgear: 'sakkos', garment: 'chiton', beard: null, attribute: 'brush', age: 'young', wash: 0.22, jaw: 0.3, female: true },
  artisan: { hair: 'short', headgear: 'headband', garment: 'exomis', beard: 'full', attribute: 'chisel', age: 'adult', wash: 0.4, jaw: 0.7 },
  priestess: { hair: 'veiled', headgear: 'veilWreath', garment: 'peplos', beard: null, attribute: 'phiale', age: 'adult', wash: 0.18, jaw: 0.3, female: true },
});

const f1 = (n) => Math.round(n * 10) / 10;

/** 头发的发区（多数发式共用）：从额前发际线翻过头顶、沿后脑垂到颈后，再沿鬓角回到额前。 */
const CROWN = Object.freeze([
  [72, 42],
  ['C', 66, 30, 50, 27, 42, 36], ['C', 35, 44, 36, 60, 40, 72], ['C', 42, 79, 44, 84, 46, 88],
  ['L', 54, 85],
  ['C', 52, 78, 52, 70, 56, 62], ['C', 60, 54, 66, 48, 72, 42],
]);
const crownPath = () => path('M', ...CROWN[0], ...CROWN.slice(1).flat(), 'Z');

/** 发区轮廓采样成的多边形（测试用它核对卷发的小卷都落在头发里，不落在脸上）。 */
export function crownPolygon(steps = 24) {
  const pts = [CROWN[0]];
  let cur = CROWN[0];
  for (const [cmd, ...v] of CROWN.slice(1)) {
    if (cmd === 'L') { cur = [v[0], v[1]]; pts.push(cur); continue; }
    const [x1, y1, x2, y2, x3, y3] = v;
    for (let k = 1; k <= steps; k++) {
      const t = k / steps, u = 1 - t;
      pts.push([
        u * u * u * cur[0] + 3 * u * u * t * x1 + 3 * u * t * t * x2 + t * t * t * x3,
        u * u * u * cur[1] + 3 * u * u * t * y1 + 3 * u * t * t * y2 + t * t * t * y3,
      ]);
    }
    cur = [x3, y3];
  }
  return pts;
}

/** 卷发的一个小卷相对起点占的范围（下面 'curly' 里那条路径的外接框）。 */
export const CURL_BOX = Object.freeze({ dx0: 0, dx1: 5.2, dy0: -1.6, dy1: 2.7 });
/** 卷发的小卷（起点）：每一个都整个落在发区之内，测试逐个核对。 */
export const CURLS = Object.freeze([
  [47, 36], [55, 34.5], [62, 38],
  [41, 45], [50, 44], [58, 45],
  [39, 55], [47, 54], [53, 52],
  [41, 64], [49, 63],
  [44, 73], [47, 81],
]);

/** 把 [命令, 坐标…] 的列表拼成 SVG 路径（坐标保留一位小数，结果稳定可比）。 */
function path(...parts) {
  return parts.map((p) => (typeof p === 'number' ? f1(p) : p)).join(' ');
}

/** 椭圆（四段三次贝塞尔；rot 弧度）：柔和的阴影和高光都是几层同心椭圆叠出来的。 */
const KAPPA = 0.5523;
function ell(cx, cy, rx, ry, rot = 0) {
  const c = Math.cos(rot), s = Math.sin(rot);
  const P = (x, y) => [cx + x * c - y * s, cy + x * s + y * c];
  const k = KAPPA;
  const pts = [
    P(rx, 0), P(rx, ry * k), P(rx * k, ry), P(0, ry),
    P(-rx * k, ry), P(-rx, ry * k), P(-rx, 0),
    P(-rx, -ry * k), P(-rx * k, -ry), P(0, -ry),
    P(rx * k, -ry), P(rx, -ry * k), P(rx, 0),
  ];
  return path('M', ...pts[0], 'C', ...pts[1], ...pts[2], ...pts[3], 'C', ...pts[4], ...pts[5], ...pts[6], 'C', ...pts[7], ...pts[8], ...pts[9], 'C', ...pts[10], ...pts[11], ...pts[12], 'Z');
}

function meander(x0, x1, y, h) {
  // 回纹：每个单元是一个向内卷的方折线
  const u = h;
  const out = [];
  for (let x = x0; x + u <= x1 + 0.01; x += u) {
    out.push(path('M', x, y + h, 'L', x, y, 'L', x + u * 0.8, y, 'L', x + u * 0.8, y + h * 0.65, 'L', x + u * 0.35, y + h * 0.65, 'L', x + u * 0.35, y + h * 0.35));
  }
  return out.join(' ');
}

/** 衣料的底色：按 wash（0 最浅）在浅灰与中灰之间取。 */
function garmentTone(wash) {
  const v = Math.round(228 - wash * 150);
  const h = v.toString(16).padStart(2, '0');
  return `#${h}${h}${h}`;
}

/**
 * 一张半身像：返回 { viewBox, character, expression, paths: [{ d, fill?, stroke?, width?, opacity? }] }。
 * 同样的输入永远给出同样的路径（截图与断言可复现）。
 */
export function bust(character, expression = 'neutral') {
  // 居民（M-B）没有登记在 CHARACTERS 里：他们的半身像参数由种子生成（art/citizens.mjs 的 citizenLook().bust），直接传对象
  const c = character && typeof character === 'object' ? character : CHARACTERS[character];
  if (!c) throw new Error(`busts: unknown character '${character}'`);
  if (!EXPRESSIONS.includes(expression)) throw new Error(`busts: unknown expression '${expression}'`);
  const paths = [];
  const ink = (d, width = 0.9, extra = {}) => paths.push({ d, stroke: INK, width, opacity: 0.8, ...extra });
  const fill = (d, color, opacity = 1) => paths.push({ d, fill: color, opacity });
  /** 几层同心椭圆叠出的柔和阴影（light 为真是高光）：k 是中心叠起来的总不透明度。 */
  const shade = (cx, cy, rx, ry, { rot = 0, k = 0.15, light = false, steps = 5 } = {}) => {
    // 五层同心椭圆，半径从 1 收到 0.3，每层只占总量的五分之一：边缘几乎看不出台阶（三层时是一圈圈的环）
    for (let i = 0; i < steps; i++) {
      const s = 1 - (i / steps) * 0.7;
      fill(ell(cx, cy, rx * s, ry * s, rot), light ? HIGHLIGHT : SHADOW, k / steps);
    }
  };

  // —— 画框：象牙色的地、一圈边、顶上的回纹带（前四条路径，测试按这个数跳过它们） ——
  fill(path('M', 13, 3, 'L', 107, 3, 'Q', 117, 3, 117, 13, 'L', 117, 137, 'Q', 117, 147, 107, 147, 'L', 13, 147, 'Q', 3, 147, 3, 137, 'L', 3, 13, 'Q', 3, 3, 13, 3, 'Z'), GROUND);
  ink(path('M', 13, 3, 'L', 107, 3, 'Q', 117, 3, 117, 13, 'L', 117, 137, 'Q', 117, 147, 107, 147, 'L', 13, 147, 'Q', 3, 147, 3, 137, 'L', 3, 13, 'Q', 3, 3, 13, 3, 'Z'), 1.4, { opacity: 0.9 });
  ink(path('M', 10, 22, 'L', 110, 22), 0.6, { opacity: 0.7 });
  ink(meander(12, 108, 11, 8), 0.8, { opacity: 0.8 });

  const old = c.age === 'old';
  const fem = !!c.female;
  const jaw = c.jaw;
  const speak = expression === 'speak';
  const smile = expression === 'smile';
  const browLift = speak ? 1.4 : 0;
  const hairColor = c.hair === 'sakkos' ? null : old ? GREY_HAIR : HAIR;
  const chinX = 82 + jaw * 2.5;

  // —— 纸上的一团淡影：头后面暗一点，人从纸里站出来 ——
  shade(60, 78, 36, 44, { k: 0.07, steps: 6 });

  // —— 衣着与肩：先画，脖子和头压在上面 ——
  const garment = c.garment;
  const tone = garmentTone(c.wash);
  const shoulderBack = garment === 'exomis' ? path('M', 44, 112, 'C', 34, 116, 22, 122, 14, 147) : path('M', 44, 110, 'C', 32, 115, 20, 124, 12, 147);
  const shoulderFront = path('M', 78, 118, 'C', 88, 121, 99, 128, 106, 147);
  const garmentOutline = path('M', 12, 147, 'C', 20, 124, 32, 115, 44, 110, 'L', 50, 116, 'C', 58, 121, 70, 122, 78, 118, 'C', 88, 121, 99, 128, 106, 147, 'Z');
  fill(garmentOutline, tone);
  // 布的体积：背肩暗、前肩亮，胸前一道大的明暗（顺着肩的斜度拉长，不是一团圆）
  shade(26, 140, 18, 9, { rot: -0.8, k: 0.18, steps: 6 });
  shade(98, 142, 12, 7, { rot: 0.9, k: 0.12, steps: 6 });
  shade(86, 125, 9, 4, { rot: 0.5, light: true, k: 0.28, steps: 4 });
  if (garment === 'peplos') {
    // 佩普洛斯的翻折：领口一道弧，翻折下沿一道阴影，胸前几道竖褶（每道褶一边暗一边亮）
    ink(path('M', 47, 114, 'C', 58, 124, 70, 125, 80, 119), 0.9);
    fill(path('M', 40, 128, 'C', 52, 133, 74, 134, 96, 129, 'L', 96, 132, 'C', 74, 137, 52, 136, 40, 131, 'Z'), SHADOW, 0.14);
    for (const x of [56, 63, 70, 77]) {
      fill(path('M', x - 1.2, 124, 'C', x - 2.2, 132, x - 0.6, 139, x - 1.6, 147, 'L', x + 0.6, 147, 'C', x + 1.4, 139, x - 0.2, 132, x + 0.6, 124, 'Z'), SHADOW, 0.13);
      ink(path('M', x + 1.4, 125, 'C', x + 0.8, 133, x + 2, 140, x + 1.2, 147), 0.7, { stroke: HIGHLIGHT, opacity: 0.45 });
    }
  } else if (garment === 'chiton') {
    ink(path('M', 48, 114, 'C', 58, 121, 70, 122, 79, 118), 0.9);
    for (const x of [52, 58, 64, 70, 76, 82]) {
      fill(path('M', x - 0.9, 122, 'L', x + 0.3, 147, 'L', x + 1.6, 147, 'L', x + 0.6, 122, 'Z'), SHADOW, 0.12);
      ink(path('M', x + 1.9, 123, 'L', x + 3.1, 147), 0.55, { stroke: HIGHLIGHT, opacity: 0.4 });
    }
    fill(path('M', 46, 118, 'C', 58, 125, 72, 126, 82, 121, 'L', 83, 124, 'C', 72, 129, 58, 128, 46, 121, 'Z'), SHADOW, 0.1);
  } else if (garment === 'exomis') {
    // 只遮一边肩：露出的肩与上臂是皮肤（有三角肌的体积），布边斜过胸前
    fill(path('M', 44, 110, 'L', 78, 118, 'C', 88, 121, 96, 126, 101, 136, 'C', 80, 131, 58, 122, 44, 110, 'Z'), SKIN);
    shade(88, 128, 7, 4.5, { rot: 0.4, light: true, k: 0.35 });
    shade(70, 124, 9, 4, { rot: 0.3, k: 0.16 });
    ink(path('M', 78, 118, 'C', 88, 121, 96, 126, 101, 136), 1.0);
    ink(path('M', 40, 113, 'C', 56, 126, 76, 136, 100, 141), 1.0);
    fill(path('M', 40, 113, 'C', 56, 126, 76, 136, 100, 141, 'L', 100, 144, 'C', 76, 139, 56, 129, 40, 116, 'Z'), SHADOW, 0.18);
    ink(path('M', 62, 128, 'C', 70, 137, 84, 141, 98, 147), 0.6, { opacity: 0.6 });
  } else if (garment === 'himation' || garment === 'chlamys') {
    // 斜披的外衣：从一肩斜下的几道厚褶，每道褶的下沿一条阴影
    ink(path('M', 30, 120, 'C', 48, 120, 70, 128, 90, 147), 0.9);
    for (const k of [0, 1, 2]) {
      const y0 = 128 + k * 4;
      fill(path('M', 26 + k * 8, y0, 'C', 44 + k * 8, y0, 62 + k * 6, y0 + 8, 74 + k * 6, 147, 'L', 70 + k * 6, 147, 'C', 58 + k * 6, y0 + 11, 42 + k * 8, y0 + 3, 26 + k * 8, y0 + 3, 'Z'), SHADOW, 0.13);
      ink(path('M', 26 + k * 8, y0, 'C', 44 + k * 8, y0, 62 + k * 6, y0 + 8, 74 + k * 6, 147), 0.5, { opacity: 0.5 });
    }
    shade(62, 134, 10, 6, { rot: 0.5, light: true, k: 0.25 });
    if (garment === 'chlamys') {
      fill(ell(80.5, 125.5, 2.2, 2.2), METAL);   // 肩头的扣
      ink(ell(80.5, 125.5, 2.2, 2.2), 0.6);
      fill(ell(80.0, 125.0, 0.8, 0.8), HIGHLIGHT, 0.7);
    }
  }
  ink(shoulderBack, 1.0);
  ink(shoulderFront, 1.0);

  // —— 脖子：有体积的皮肤，颌下一道阴影，胸锁乳突肌一条淡线 ——
  fill(path('M', 46, 86, 'L', 75, 99, 'L', 78, 118, 'C', 66, 117, 54, 115, 44, 110, 'Z'), SKIN);
  shade(63, 101, 13, 3.6, { rot: 0.32, k: 0.3, steps: 6 });   // 颌下：贴着下颌线的一道扁影
  shade(69, 110, 2.4, 6, { rot: 0.15, light: true, k: 0.22 });
  ink(path('M', 75, 100, 'C', 76, 106, 77, 112, 78, 118), 1.0);
  ink(path('M', 46, 88, 'C', 47, 96, 46, 104, 44, 110), 1.0);
  ink(path('M', 70, 101, 'C', 72, 107, 73.5, 112, 75, 117), 0.5, { opacity: 0.35 });

  // —— 脸：希腊式的侧脸——额头到鼻尖几乎一条直线，鼻梁略带一点弧 ——
  const face = path(
    'M', 72, 42,
    'C', 75, 46, 77, 50, 78.5, 55,
    'C', 81.2, 60, 85.4, 67.5, 87.5, 73.5,
    'C', 88, 75, 86.5, 76.5, 84.5, 76.6,
    'C', 84, 77.4, 84.2, 78.6, 85.2, 79.6,
    'C', 85.6, 80.4, 85, 81.4, 83.6, 81.8,
    'C', 84.6, 83.2, 84.6, 84.8, 83.2, 85.8,
    'C', chinX + 0.6, 89, chinX + 0.6, 93.5, chinX - 3, 96.5,
    'C', chinX - 6, 99, 79, 99.5, 75, 100,
  );
  // 头的皮肤：脸的轮廓加上后脑（头发盖住大半；秃顶的人露出头皮）
  fill(face + path(' L', 70, 101, 'C', 60, 98, 48, 92, 46, 86, 'C', 40, 76, 38, 60, 44, 48, 'C', 50, 38, 62, 36, 72, 42, 'Z'), SKIN);
  // 体积：眼窝、眉下、鼻侧、鼻下、唇下、颌下、颊窝、太阳穴、后颌；高光：额、颧、鼻梁、下巴
  shade(75, 61.2, 6.6, 4.4, { k: 0.22 });
  shade(76.5, 57.6, 5, 1.6, { k: 0.1 });
  shade(81.6, 66, 2.3, 7.6, { rot: 0.36, k: 0.18 });
  shade(82.8, 77.9, 2.6, 1.3, { k: 0.26 });
  shade(81.2, 86.5, 3, 1.5, { k: 0.2 });
  shade(78, 98.4, 6, 2.6, { rot: 0.25, k: 0.24 });
  shade(66, 80, 7, 8.5, { rot: 0.2, k: old ? 0.13 : 0.07, steps: 6 });
  shade(63, 54, 6, 7, { k: 0.08, steps: 6 });
  shade(53, 82, 7, 11, { rot: -0.3, k: 0.1, steps: 6 });   // 后颌、耳后
  shade(72, 50, 4.5, 7, { light: true, k: 0.38 });
  shade(76, 71, 4, 3.2, { light: true, k: smile ? 0.42 : 0.3 });
  ink(path('M', 79.6, 57.2, 'C', 82.4, 62, 85.2, 67.6, 86.8, 72.4), 0.9, { stroke: HIGHLIGHT, opacity: 0.45 });
  shade(80, 92.5, 2.6, 2.6, { light: true, k: 0.3 });
  ink(face, 1.0);
  // 下颌线：方一些（男性、年长）或柔一些；颌下的阴影已经在上面
  ink(path('M', chinX - 3, 96.5, 'C', 70 - jaw * 2, 96 + jaw, 64 - jaw * 2, 93, 59, 88), 0.7, { opacity: 0.6 });
  if (old) {
    ink(path('M', 73, 52, 'C', 75, 51.4, 77, 51.4, 78, 52), 0.45, { opacity: 0.55 });   // 额纹
    ink(path('M', 72.6, 49.6, 'C', 74.6, 49, 76.4, 49, 77.4, 49.6), 0.4, { opacity: 0.4 });
    ink(path('M', 80.6, 76.2, 'C', 79.2, 80, 78.4, 84, 78.8, 88), 0.5, { opacity: 0.45 });   // 法令纹
    shade(76.4, 66.8, 3.2, 1.4, { k: 0.18 });   // 眼袋
    ink(path('M', 72.4, 63.6, 'C', 71.2, 64.2, 70.4, 65, 70, 65.8), 0.4, { opacity: 0.4 });   // 鱼尾纹
    ink(path('M', 72.6, 62.6, 'C', 71.2, 62.6, 70.2, 62.8, 69.4, 63.2), 0.4, { opacity: 0.35 });
  }
  if (smile) ink(path('M', 81.4, 75.8, 'C', 79.6, 79, 78.2, 82.6, 78.4, 86.4), 0.5, { opacity: 0.45 });   // 笑起来的法令纹

  // —— 鼻孔与鼻翼 ——
  ink(path('M', 82.6, 74.2, 'C', 84.8, 74.4, 85.6, 76.2, 84.6, 77.3), 0.6, { opacity: 0.7 });
  fill(ell(84.0, 76.4, 1.05, 0.62, -0.3), SHADOW, 0.55);

  // —— 嘴：上唇暗、下唇亮，中间一条嘴缝；说话微张（露出口腔的暗）；微笑嘴角上扬 ——
  const lipShift = speak ? 1.4 : 0;
  const cornerUp = smile ? -0.9 : 0;
  fill(path('M', 79.4, 82.4 + cornerUp, 'C', 80.6, 81.1, 82.4, 81.0, 83.9, 82.3, 'C', 82.6, 82.9, 80.9, 83.0, 79.4, 82.4 + cornerUp, 'Z'), LIP_UPPER, 0.8);
  if (speak) {
    fill(path('M', 79.6, 82.9, 'C', 81.0, 83.0, 82.6, 83.0, 83.6, 82.7, 'C', 83.2, 85.6, 80.8, 86.3, 79.6, 84.6, 'Z'), MOUTH, 0.85);
    fill(path('M', 80.2, 83.2, 'C', 81.2, 83.2, 82.4, 83.2, 83.2, 83.0, 'L', 83.0, 83.9, 'C', 82.2, 84.1, 81.0, 84.1, 80.2, 83.9, 'Z'), SCLERA, 0.5);   // 牙
  }
  fill(path('M', 79.6, 82.9 + lipShift, 'C', 80.8, 83.0 + lipShift, 82.6, 83.1 + lipShift, 83.6, 82.6 + lipShift, 'C', 83.4, 84.7 + lipShift, 81.2, 85.2 + lipShift, 79.6, 83.6 + lipShift, 'Z'), LIP_LOWER, 0.75);
  shade(82.0, 83.7 + lipShift, 0.9, 0.42, { light: true, k: 0.5, steps: 2 });
  if (smile) ink(path('M', 79, 81.9, 'C', 80.6, 83.3, 82.3, 83.2, 83.7, 82.3), 0.75);
  else if (speak) ink(path('M', 79.5, 82.8, 'C', 81, 82.9, 82.6, 82.9, 83.7, 82.6), 0.7);
  else ink(path('M', 79.4, 82.7, 'C', 80.8, 82.9, 82.4, 82.9, 83.7, 82.6), 0.75);

  // —— 眼：侧面的杏仁眼——眼白、虹膜、瞳孔、一点高光；上睑一条重线、睫毛几根、下睑一条淡线、上面一道眼褶 ——
  const squint = smile ? 0.7 : 0;
  fill(path('M', 73.2, 62.4, 'C', 75.6, 60.8, 78.4, 60.9, 80.4, 62.6, 'C', 78.4, 64.6 - squint, 75.6, 64.7 - squint, 73.6, 63.2, 'Z'), SCLERA);
  fill(ell(78.0, 62.65, 1.65, 1.5), IRIS);
  fill(ell(78.3, 62.7, 0.78, 0.74), PUPIL);
  fill(ell(78.7, 62.1, 0.36, 0.3), HIGHLIGHT, 0.95);
  shade(76.6, 62.6, 3.6, 1.1, { k: 0.2, steps: 2 });   // 上睑投在眼白上的影
  ink(path('M', 73.2, 62.4, 'C', 75.6, 60.8, 78.4, 60.9, 80.4, 62.6), 1.05, { opacity: 0.95 });
  ink(path('M', 73.6, 63.2, 'C', 75.6, 64.6 - squint, 78.2, 64.5 - squint, 80.2, 62.9), 0.6, { opacity: 0.65 });
  ink(path('M', 73.6, 61.0, 'C', 75.8, 59.4, 78.6, 59.5, 80.8, 61.2), 0.45, { opacity: 0.4 });   // 眼褶
  for (const [x, y, dx, dy] of [[80.2, 62.4, 1.2, -0.5], [79.4, 61.8, 1.0, -0.7], [78.4, 61.4, 0.7, -0.8]]) ink(path('M', x, y, 'l', dx, dy), 0.5, { opacity: 0.7 });
  if (c.archaic) {
    // 黄金侍女：古风少女像那样的正面杏仁眼与"古风的微笑"
    ink(path('M', 74, 62.6, 'C', 76.5, 59.8, 80, 60.4, 81.5, 63), 0.55, { opacity: 0.5 });
  }
  // 眉：一道有宽度的形（眉头粗、眉尾细），几根眉毛
  const browMat = hairColor ?? HAIR;
  fill(path('M', 71.4, 58.6 - browLift, 'C', 74.4, 56.4 - browLift, 78.2, 56.5 - browLift, 81.0, 58.3 - browLift * 0.6, 'C', 78.6, 57.8 - browLift * 0.6, 75.0, 58.2 - browLift, 71.4, 59.6 - browLift, 'Z'), browMat, fem ? 0.75 : 0.9);
  for (const [x, y] of [[73.6, 57.6], [76.2, 57.0], [78.8, 57.2]]) ink(path('M', x, y - browLift, 'l', 1.3, -0.5), 0.4, { stroke: browMat, opacity: 0.7 });

  // —— 耳：轮廓、耳轮、耳窝的影 ——
  ink(path('M', 58.6, 64, 'C', 53.6, 63.2, 52.4, 72, 54.6, 77.2, 'C', 55.6, 79.4, 57.6, 80, 58.6, 79), 0.95);
  ink(path('M', 57.4, 67.2, 'C', 55.6, 68.6, 55.8, 73, 57.2, 75.4), 0.55, { opacity: 0.7 });
  shade(56.4, 72.2, 1.6, 4.2, { k: 0.3, steps: 2 });

  // —— 头发：一团有发丝走向的体积，顶上一道光，发际线下一道影 ——
  const crown = crownPath();
  const strands = (lines, color, width, opacity) => { for (const d of lines) ink(d, width, { stroke: color, opacity }); };
  const hairMass = (d, color) => {
    fill(d, color, 0.96);
    shade(70, 44.6, 4.2, 2.4, { rot: 0.5, k: 0.25, steps: 2 });   // 发际线投在额上的影
  };
  switch (c.hair) {
    case 'bun':
    case 'braids':
      hairMass(crown, hairColor);
      shade(56, 41, 10, 3.4, { rot: -0.3, light: true, k: 0.28 });
      strands([0, 1, 2, 3, 4].map((k) => path('M', 70 - k * 4, 41 + k * 1.5, 'C', 62 - k * 4, 36 + k, 50 - k * 2, 38 + k * 2, 44 - k, 50 + k * 4)), HAIR_LIGHT, 0.55, 0.5);
      strands([0, 1, 2].map((k) => path('M', 66 - k * 5, 44 + k * 2, 'C', 58 - k * 4, 42 + k, 48 - k * 2, 48 + k * 2, 43 - k, 58 + k * 4)), SHADOW, 0.5, 0.45);
      if (c.hair === 'bun') {
        // 脑后的发髻（krobylos）
        fill(path('M', 36, 52, 'C', 24, 50, 22, 68, 33, 72, 'C', 40, 74, 44, 64, 41, 56, 'Z'), hairColor, 0.96);
        shade(31, 59, 4, 3.5, { light: true, k: 0.3, steps: 2 });
        ink(path('M', 36, 52, 'C', 24, 50, 22, 68, 33, 72, 'C', 40, 74, 44, 64, 41, 56), 0.8);
        strands([path('M', 30, 58, 'C', 28, 63, 30, 67, 34, 68), path('M', 34, 55, 'C', 30, 60, 31, 66, 36, 69)], HAIR_LIGHT, 0.5, 0.6);
      } else {
        // 古风少女：几缕长辫垂到肩上，每缕一道暗一道亮
        for (let k = 0; k < 3; k++) {
          ink(path('M', 44 + k * 3, 86, 'C', 43 + k * 3, 96, 46 + k * 3, 106, 43 + k * 3, 116), 1.7, { stroke: hairColor, opacity: 0.95 });
          ink(path('M', 44.5 + k * 3, 87, 'C', 43.5 + k * 3, 96, 46.5 + k * 3, 105, 43.5 + k * 3, 115), 0.5, { stroke: HAIR_LIGHT, opacity: 0.6 });
        }
      }
      break;
    case 'long': {
      // 你：中分的长发，从头顶梳下来盖住耳朵，后面一大缕垂过后颈、搭在肩上（在脖子后面，不挡脸和喉咙）
      const fall = path('M', 46, 48, 'C', 38, 58, 36, 78, 33, 96, 'C', 31, 106, 30, 114, 34, 121, 'C', 41, 121, 48, 116, 51, 106, 'C', 53, 98, 53, 90, 55, 82, 'C', 57, 72, 54, 60, 46, 48, 'Z');
      fill(fall, hairColor, 0.96);
      shade(40, 96, 4, 14, { rot: 0.15, k: 0.22 });
      ink(path('M', 46, 48, 'C', 38, 58, 36, 78, 33, 96, 'C', 31, 106, 30, 114, 34, 121, 'C', 41, 121, 48, 116, 51, 106, 'C', 53, 98, 53, 90, 55, 82), 0.8);
      hairMass(crown, hairColor);
      shade(56, 41, 10, 3.4, { rot: -0.3, light: true, k: 0.28 });
      strands([0, 1, 2, 3, 4].map((k) => path('M', 70 - k * 4, 41 + k * 1.5, 'C', 62 - k * 4, 38 + k, 50 - k * 2, 44 + k * 3, 44 - k, 62 + k * 5)), HAIR_LIGHT, 0.55, 0.5);
      strands([0, 1, 2, 3].map((k) => path('M', 42 + k * 3, 60 + k * 2, 'C', 39 + k * 3, 78, 37 + k * 3, 98, 36 + k * 3, 116 - k * 2)), HAIR_LIGHT, 0.5, 0.45);
      strands([0, 1, 2].map((k) => path('M', 45 + k * 3, 62 + k * 2, 'C', 42 + k * 3, 80, 41 + k * 3, 100, 41 + k * 3, 114 - k * 3)), SHADOW, 0.45, 0.4);
      break;
    }
    case 'short':
    case 'receding':
    case 'fringe': {
      const top = c.hair === 'short'
        ? crown
        : c.hair === 'receding'
          ? path('M', 62, 38, 'C', 54, 31, 44, 32, 40, 40, 'C', 36, 50, 37, 64, 41, 74, 'C', 43, 80, 44, 84, 46, 88, 'L', 54, 85, 'C', 52, 76, 52, 66, 55, 58, 'C', 57, 50, 60, 44, 62, 38, 'Z')
          : path('M', 50, 54, 'C', 44, 52, 38, 58, 39, 68, 'C', 40, 78, 43, 84, 46, 88, 'L', 54, 85, 'C', 52, 78, 52, 70, 54, 62, 'Z');
      if (c.hair === 'short') hairMass(top, hairColor);
      else fill(top, hairColor, 0.96);
      shade(c.hair === 'short' ? 56 : 46, c.hair === 'short' ? 41 : 60, 9, 3.2, { rot: c.hair === 'short' ? -0.3 : 1.2, light: true, k: 0.26 });
      if (c.hair !== 'short') {
        // 秃顶的轮廓与头皮的光
        ink(path('M', 72, 42, 'C', 66, 30, 50, 27, 42, 36, 'C', 37, 42, 37, 50, 39, 58), 0.95);
        shade(56, 40, 8, 5, { light: true, k: 0.4 });
      }
      if (c.hair === 'short') {
        strands([0, 1, 2, 3].map((k) => path('M', 48 + k * 5, 40 + k * 1.2, 'C', 46 + k * 4, 46, 44 + k * 3, 54, 44 + k * 2, 62 + k * 3)), HAIR_LIGHT, 0.5, 0.5);
        strands([0, 1].map((k) => path('M', 60 + k * 5, 43 + k * 2, 'C', 57 + k * 4, 50, 54 + k * 3, 56, 52 + k * 2, 64 + k * 3)), SHADOW, 0.45, 0.4);
      } else {
        // 秃顶：发丝只在后脑和鬓角那一片里
        strands([0, 1, 2].map((k) => path('M', 42 + k * 3, 58 + k * 6, 'C', 40 + k * 3, 66 + k * 5, 42 + k * 2, 76 + k * 3, 46 + k, 84)), SHADOW, 0.45, 0.4);
      }
      break;
    }
    case 'curly': {
      // 一个个小卷排在发区里面（每个卷都落在 crown 的轮廓之内，测试逐个核对）。戴尖毡帽的人，帽子盖住的那几个不画
      const capped = c.headgear === 'pilos' || c.headgear === 'pilosTilted';
      hairMass(crown, hairColor);
      shade(56, 42, 9, 3.6, { rot: -0.3, light: true, k: 0.22 });
      for (const [x, y] of CURLS) {
        if (capped && y < 48) continue;
        fill(path('M', x, y, 'c', 2.4, -2.2, 5.2, -0.8, 4.6, 1.8, 'c', -0.6, 2.4, -3.6, 2.6, -4.6, 0.6, 'Z'), HAIR_LIGHT, 0.18);
        ink(path('M', x, y, 'c', 2.4, -2.2, 5.2, -0.8, 4.6, 1.8, 'c', -0.6, 2.4, -3.6, 2.6, -4.6, 0.6), 0.5, { stroke: HAIR_LIGHT, opacity: 0.55 });
      }
      break;
    }
    case 'sakkos':
      // 包在发网（sakkos）里的头发：一个向后垂的布袋，几道褶
      fill(path('M', 72, 42, 'C', 64, 28, 44, 26, 36, 40, 'C', 28, 52, 30, 70, 38, 80, 'C', 42, 84, 46, 86, 50, 86, 'L', 54, 84, 'C', 52, 74, 53, 64, 57, 58, 'C', 61, 52, 66, 46, 72, 42, 'Z'), garmentTone(0.45), 0.96);
      shade(50, 44, 9, 5, { rot: -0.4, light: true, k: 0.3 });
      shade(40, 72, 6, 8, { k: 0.2 });
      ink(path('M', 72, 42, 'C', 64, 28, 44, 26, 36, 40, 'C', 28, 52, 30, 70, 38, 80, 'C', 42, 84, 46, 86, 50, 86), 0.9);
      strands([0, 1, 2, 3].map((k) => path('M', 40 + k * 6, 34 + k * 2, 'C', 36 + k * 6, 48, 38 + k * 5, 64, 42 + k * 4, 80)), SHADOW, 0.5, 0.35);
      fill(path('M', 72, 42, 'C', 70, 46, 70, 49, 72.6, 50, 'C', 73.6, 48, 73.4, 44.8, 72, 42, 'Z'), HAIR, 0.9);   // 额前露出的一缕
      break;
    case 'veiled':
    case 'hidden':
    default:
      break;
  }

  // —— 胡须：一团有体积的毛发，几缕发丝；唇髭一道 ——
  if (c.beard === 'short' || c.beard === 'full' || c.beard === 'long') {
    const len = c.beard === 'long' ? 22 : c.beard === 'full' ? 9 : 4;
    const beard = path('M', 60, 82, 'C', 64, 92, 70, 99 + len * 0.4, chinX - 1, 98 + len, 'C', chinX + 2, 96 + len * 0.6, chinX + 1.4, 90, 84.4, 86.4, 'C', 82, 87.6, 79, 86.6, 77, 84.6, 'C', 72, 86, 66, 85, 60, 82, 'Z');
    fill(beard, hairColor, 0.94);
    shade(76, 90 + len * 0.3, 4, 3 + len * 0.2, { light: true, k: 0.25 });
    shade(66, 88, 4, 4, { k: 0.2 });
    ink(path('M', 60, 82, 'C', 64, 92, 70, 99 + len * 0.4, chinX - 1, 98 + len), 0.75);
    for (let k = 0; k < 5; k++) ink(path('M', 63 + k * 4, 86 + k, 'C', 65 + k * 4, 92 + len * 0.3, 67 + k * 4, 96 + len * 0.6, 69 + k * 3.5, 98 + len * 0.8), 0.45, { stroke: k % 2 ? HAIR_LIGHT : SHADOW, opacity: 0.55 });
    fill(path('M', 77.6, 80.6, 'C', 79.6, 79.4, 82, 79.4, 84.2, 80.6, 'C', 82, 81.6, 79.6, 81.6, 77.6, 80.6, 'Z'), hairColor, 0.9);   // 唇髭
  } else if (c.beard === 'stubble') {
    shade(70, 90, 9, 7, { rot: 0.3, k: 0.14 });
    for (let k = 0; k < 14; k++) {
      const x = 62 + (k % 7) * 3.1, y = 86 + Math.floor(k / 7) * 4 + (k % 2);
      fill(path('M', x, y, 'l', 0.7, 0, 'l', 0, 0.7, 'l', -0.7, 0, 'Z'), INK, 0.55);
    }
  }

  // —— 头饰 ——
  switch (c.headgear) {
    case 'fillet':
      // 雅典娜：一条素发带（不披甲、不戴盔，设计稿 4.2），带子的亮面与它压在头发上的影
      ink(path('M', 69, 39.6, 'C', 60, 35, 48, 40, 41, 52), 2.4, { stroke: SHADOW, opacity: 0.35 });
      ink(path('M', 69, 39.2, 'C', 60, 34.6, 48, 39.6, 41, 51.6), 1.5, { stroke: '#ece7dc', opacity: 0.95 });
      break;
    case 'diadem':
      ink(path('M', 70, 40, 'C', 62, 33, 50, 34, 43, 42), 2.0, { stroke: METAL, opacity: 0.95 });
      ink(path('M', 70, 39.4, 'C', 62, 32.6, 50, 33.6, 43, 41.4), 0.6, { stroke: HIGHLIGHT, opacity: 0.6 });
      for (let k = 0; k < 5; k++) ink(path('M', 66 - k * 5, 37 - (k === 2 ? 1 : 0), 'l', 0.4, -3.2), 1.2, { stroke: METAL, opacity: 0.95 });
      break;
    case 'headband':
      ink(path('M', 71.6, 44, 'C', 64, 38, 50, 38, 40, 48), 1.8, { stroke: garmentTone(0.3), opacity: 0.95 });
      ink(path('M', 71.6, 43.4, 'C', 64, 37.4, 50, 37.4, 40, 47.4), 0.5, { stroke: HIGHLIGHT, opacity: 0.5 });
      break;
    case 'petasos':
      // 宽檐旅人帽：帽冠有体积、帽檐在额上投一道影
      fill(path('M', 36, 40, 'C', 44, 22, 66, 22, 74, 38, 'Z'), '#8a847b', 0.96);
      shade(55, 30, 7, 3, { rot: -0.2, light: true, k: 0.3 });
      ink(path('M', 36, 40, 'C', 44, 22, 66, 22, 74, 38), 0.9);
      ink(path('M', 26, 42, 'C', 44, 34, 70, 33, 86, 38), 1.6, { stroke: '#6f6962', opacity: 0.98 });
      shade(70, 47, 10, 4, { rot: 0.2, k: 0.28 });
      break;
    case 'petasosBack':
      // 传令官：帽子用带子挂在背后，侧面看是颈后一只竖着的帽檐、帽顶朝后，帽带绕过喉前
      fill(path('M', 30, 88, 'C', 18, 90, 17, 110, 30, 113, 'Z'), '#8a847b', 0.9);
      ink(path('M', 30, 88, 'C', 18, 90, 17, 110, 30, 113), 0.9);
      fill(path('M', 33, 80, 'C', 41, 81, 41, 120, 33, 121, 'C', 26, 120, 26, 81, 33, 80, 'Z'), '#7a746c', 0.95);
      shade(33, 100, 2.2, 14, { light: true, k: 0.3 });
      ink(path('M', 33, 80, 'C', 41, 81, 41, 120, 33, 121, 'C', 26, 120, 26, 81, 33, 80, 'Z'), 1.1);
      ink(path('M', 38, 86, 'C', 52, 99, 64, 104, 77, 105), 0.7, { opacity: 0.75 });   // 帽带
      break;
    case 'pilos':
    case 'pilosTilted': {
      // 圆锥形的毡帽（pilos）：铁匠、水手；水手的帽尖歪向前。帽尖不高过 y=25（回纹在 11–19，下面一道线在 22）
      const tilt = c.headgear === 'pilosTilted' ? 6 : 0;
      const tipX = 58 + tilt * 1.4, tipY = 25.5;
      const cone = path('M', 38, 46, 'C', 40, 37, 50, 27, tipX, tipY, 'C', 66 + tilt, 27, 73, 35, 74, 44);
      fill(cone + path(' C', 62, 40, 48, 41, 38, 46, 'Z'), '#6f6962', 0.96);
      fill(path('M', 38, 46, 'C', 40, 37, 50, 27, tipX, tipY, 'C', tipX - 2, 32, 50, 40, 44, 46, 'Z'), SHADOW, 0.22);   // 背光的一面
      ink(path('M', tipX + 4 + tilt * 0.4, 30, 'C', 67 + tilt * 0.6, 34, 70, 38, 71, 42), 0.8, { stroke: HIGHLIGHT, opacity: 0.35 });
      ink(cone, 1.0);
      ink(path('M', 37, 46.6, 'C', 50, 40.4, 64, 40, 75, 44.4), 1.5, { stroke: '#4e4943', opacity: 0.98 });
      break;
    }
    case 'veilWreath':
      // 女祭司：面纱从头顶披到肩后，布有褶；额上一圈月桂
      fill(path('M', 76, 44, 'C', 68, 26, 44, 24, 34, 40, 'C', 24, 58, 26, 90, 30, 118, 'L', 50, 116, 'C', 50, 98, 52, 80, 56, 66, 'C', 60, 56, 68, 48, 76, 44, 'Z'), '#e4ded2', 0.96);
      shade(42, 90, 5, 22, { k: 0.16 });
      shade(50, 46, 10, 6, { rot: -0.4, light: true, k: 0.3 });
      ink(path('M', 76, 44, 'C', 68, 26, 44, 24, 34, 40, 'C', 24, 58, 26, 90, 30, 118), 1.0);
      strands([path('M', 40, 50, 'C', 34, 70, 34, 96, 38, 116), path('M', 48, 56, 'C', 44, 74, 44, 96, 46, 114)], SHADOW, 0.5, 0.35);
      for (let k = 0; k < 6; k++) {
        const x = 70 - k * 5.4, y = 40 - Math.sin((k / 5) * Math.PI) * 4;
        fill(path('M', x, y, 'c', -1.8, -2.8, -4.2, -2.4, -4.6, 0.2, 'c', 1.6, 1.4, 3.6, 1.4, 4.6, -0.2, 'Z'), '#5b5d53', 0.95);
        ink(path('M', x, y, 'c', -1.8, -2.8, -4.2, -2.4, -4.6, 0.2, 'c', 1.6, 1.4, 3.6, 1.4, 4.6, -0.2), 0.5, { opacity: 0.7 });
      }
      break;
    case 'sakkos':
    default:
      break;
  }

  // —— 手里的东西（只露出一截，进画框的边）：有体积、有一道亮 ——
  switch (c.attribute) {
    case 'rod':
      ink(path('M', 100, 40, 'L', 96, 147), 2.4, { stroke: WOOD, opacity: 0.98 });
      ink(path('M', 99.4, 40, 'L', 95.4, 147), 0.6, { stroke: HIGHLIGHT, opacity: 0.4 });
      for (let y = 50; y < 140; y += 12) ink(path('M', 99.6 - (y - 40) * 0.04, y, 'l', 3, 0), 0.8);
      break;
    case 'stylus':
      ink(path('M', 52, 64, 'L', 62, 54), 1.5, { stroke: METAL, opacity: 0.98 });   // 夹在耳后的笔
      ink(path('M', 52.4, 63.4, 'L', 61.4, 54.4), 0.4, { stroke: HIGHLIGHT, opacity: 0.6 });
      break;
    case 'abacus':
      fill(path('M', 82, 132, 'L', 112, 126, 'L', 114, 140, 'L', 84, 146, 'Z'), '#8c8378', 0.95);
      shade(96, 134, 8, 3, { rot: -0.2, light: true, k: 0.2 });
      ink(path('M', 82, 132, 'L', 112, 126, 'L', 114, 140, 'L', 84, 146, 'Z'), 0.9);
      for (let k = 0; k < 6; k++) { fill(ell(88 + k * 4, 136 - k * 0.8, 1.2, 1.2), '#d9d3c8', 0.95); fill(ell(87.6 + k * 4, 135.6 - k * 0.8, 0.4, 0.4), HIGHLIGHT, 0.8); }
      break;
    case 'hammer':
      ink(path('M', 104, 147, 'L', 98, 112), 2.8, { stroke: WOOD, opacity: 0.98 });
      fill(path('M', 86, 107, 'L', 108, 101, 'L', 111, 112, 'L', 89, 118, 'Z'), '#6b665f', 0.98);
      shade(97, 108, 7, 2, { rot: -0.26, light: true, k: 0.3 });
      ink(path('M', 86, 107, 'L', 108, 101, 'L', 111, 112, 'L', 89, 118, 'Z'), 1.0);
      break;
    case 'rope':
      for (let k = 0; k < 3; k++) {
        ink(path('M', 24 + k * 3, 118 + k * 2, 'C', 34, 110 + k * 2, 52, 116 + k * 2, 50, 130 + k * 2), 1.6, { stroke: '#8c8378', opacity: 0.95 });
        ink(path('M', 24 + k * 3, 118 + k * 2, 'C', 34, 110 + k * 2, 52, 116 + k * 2, 50, 130 + k * 2), 0.5, { stroke: INK, opacity: 0.5 });
      }
      break;
    case 'scrolls':
      for (let k = 0; k < 3; k++) {
        ink(path('M', 84 + k * 7, 128 - k * 3, 'l', 9, 14), 4.4, { stroke: '#6f6962', opacity: 0.95 });
        ink(path('M', 84 + k * 7, 128 - k * 3, 'l', 9, 14), 2.8, { stroke: '#e9e3d6', opacity: 0.98 });
        ink(path('M', 83.4 + k * 7, 128.4 - k * 3, 'l', 9, 14), 0.6, { stroke: HIGHLIGHT, opacity: 0.5 });
      }
      break;
    case 'staff':
      // 传令官的权杖（kerykeion）：杖头两条相对的蛇
      ink(path('M', 104, 147, 'L', 100, 50), 2.2, { stroke: WOOD, opacity: 0.98 });
      ink(path('M', 103.4, 147, 'L', 99.4, 50), 0.5, { stroke: HIGHLIGHT, opacity: 0.4 });
      ink(path('M', 100, 50, 'c', -6, -3, -6, -11, 0, -12, 'c', 6, 1, 6, 9, 0, 12), 1.3, { stroke: METAL, opacity: 0.98 });
      ink(path('M', 100, 50, 'c', -4, 3, -9, 1, -9, -4, 'M', 100, 50, 'c', 4, 3, 9, 1, 9, -4), 1.0, { stroke: METAL, opacity: 0.98 });
      break;
    case 'brush':
      ink(path('M', 108, 147, 'L', 96, 116), 1.7, { stroke: WOOD, opacity: 0.98 });
      fill(path('M', 96, 116, 'C', 94, 112, 95, 109, 97, 108, 'C', 98.6, 110, 98.4, 113, 96, 116, 'Z'), INK, 0.85);
      break;
    case 'chisel':
      ink(path('M', 102, 147, 'L', 94, 118), 2.8, { stroke: WOOD, opacity: 0.98 });
      ink(path('M', 94, 118, 'L', 92.4, 112), 1.5, { stroke: METAL, opacity: 0.98 });
      break;
    case 'phiale':
      // 浅浅的祭碗（phiale），中间一个凸起
      fill(path('M', 80, 134, 'C', 86, 142, 108, 142, 114, 134, 'Z'), METAL, 0.95);
      shade(96, 136, 7, 1.6, { light: true, k: 0.35, steps: 2 });
      ink(path('M', 80, 134, 'C', 86, 142, 108, 142, 114, 134, 'L', 80, 134), 0.9);
      ink(path('M', 95, 134, 'c', 0, -2.4, 4, -2.4, 4, 0), 0.8);
      break;
    default:
      break;
  }

  return { viewBox: BUST_VIEWBOX, character, expression, paths };
}

/** 独立的 SVG 文档（美术审查导出用）。属性值都由上面的代码生成，没有外来文字。 */
export function toSvg(b, size = 240) {
  const { width, height } = b.viewBox;
  const body = b.paths.map((p) => {
    const attrs = [`d="${p.d}"`];
    attrs.push(p.fill ? `fill="${p.fill}"` : 'fill="none"');
    if (p.stroke) attrs.push(`stroke="${p.stroke}"`, `stroke-width="${p.width ?? 1}"`, 'stroke-linecap="round"', 'stroke-linejoin="round"');
    if (p.opacity != null) attrs.push(`opacity="${p.opacity}"`);
    return `<path ${attrs.join(' ')}/>`;
  }).join('');
  return `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ${width} ${height}" width="${Math.round((size * width) / height)}" height="${size}">${body}</svg>`;
}

/**
 * 画在 canvas 上（对话面板）：左上角 (x, y)，高 h 像素。Path2D 吃 SVG 路径语法；路径按 bust() 的结果缓存。
 * 面板的文字仍然画在 canvas 上——城里不建任何 DOM（设计稿 12.4）。
 */
const cache = new Map();
export function drawBust(ctx, character, expression, x, y, h) {
  const key = character && typeof character === 'object' ? `citizen:${character.key ?? JSON.stringify(character)}:${expression}` : `${character}:${expression}`;
  let entry = cache.get(key);
  if (!entry) {
    const b = bust(character, expression);
    entry = { b, paths: b.paths.map((p) => ({ ...p, path: new Path2D(p.d) })) };
    cache.set(key, entry);
  }
  const k = h / entry.b.viewBox.height;
  ctx.save();
  ctx.translate(x, y);
  ctx.scale(k, k);
  ctx.lineCap = 'round';
  ctx.lineJoin = 'round';
  for (const p of entry.paths) {
    ctx.globalAlpha = p.opacity ?? 1;
    if (p.fill) { ctx.fillStyle = p.fill; ctx.fill(p.path); }
    if (p.stroke) { ctx.strokeStyle = p.stroke; ctx.lineWidth = p.width ?? 1; ctx.stroke(p.path); }
  }
  ctx.restore();
  ctx.globalAlpha = 1;
}
