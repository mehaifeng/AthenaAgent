// 2D 半身像（设计稿第 1、8 节：3D 人物只在游戏视距出现；对话等特写用 2D 半身像，默认白底陶瓶风格的 SVG 线描）。
// 全部由代码生成：每个人物一组参数（发式、胡须、头饰、衣着、手里的东西），每人三种神情（平常 / 说话 / 微笑）。
// 生成结果是纯数据（一组 SVG 路径），不碰 DOM：
//   - 城里的对话面板用 drawBust() 画在 canvas 上（Path2D 吃同一套 SVG 路径语法）——和名牌、旁白一样不建 DOM；
//   - 美术审查用 toSvg() 导出独立的 .svg 文件。
// 风格：白底（象牙色）上用深褐色细线勾轮廓，侧脸朝右（朝着说的话）；头发与衣褶用淡墨晕染，不上彩——
// 点缀色只给"轮到你了"（第 8 节），半身像不用它。画框顶上一条回纹带，是瓶肩的纹饰。

export const BUST_VIEWBOX = Object.freeze({ width: 120, height: 150 });
export const EXPRESSIONS = Object.freeze(['neutral', 'speak', 'smile']);

const INK = '#2f2621';
const WASH = '#5b4b40';
const GROUND = '#f3ede1';

/** 人物 → 参数。居民的名字与 PolisResidentKind 的 camelCase 一致。 */
export const CHARACTERS = Object.freeze({
  athena: { hair: 'bun', headgear: 'fillet', garment: 'peplos', beard: null, attribute: null, age: 'young', wash: 0.12, jaw: 0.25 },
  player: { hair: 'hidden', headgear: 'hood', garment: 'himation', beard: null, attribute: null, age: 'adult', wash: 0.5, jaw: 0.4 },
  surveyor: { hair: 'short', headgear: 'petasos', garment: 'himation', beard: 'short', attribute: 'rod', age: 'adult', wash: 0.32, jaw: 0.55 },
  scribe: { hair: 'curly', headgear: 'headband', garment: 'chiton', beard: null, attribute: 'stylus', age: 'young', wash: 0.2, jaw: 0.4 },
  accountant: { hair: 'receding', headgear: null, garment: 'himation', beard: 'short', attribute: 'abacus', age: 'old', wash: 0.36, jaw: 0.5 },
  smith: { hair: 'short', headgear: 'pilos', garment: 'exomis', beard: 'full', attribute: 'hammer', age: 'adult', wash: 0.42, jaw: 0.8 },
  sailor: { hair: 'curly', headgear: 'pilosTilted', garment: 'exomis', beard: 'stubble', attribute: 'rope', age: 'adult', wash: 0.38, jaw: 0.6 },
  librarian: { hair: 'fringe', headgear: null, garment: 'himation', beard: 'long', attribute: 'scrolls', age: 'old', wash: 0.28, jaw: 0.45 },
  maiden: { hair: 'braids', headgear: 'diadem', garment: 'peplos', beard: null, attribute: null, age: 'young', wash: 0.08, jaw: 0.2, archaic: true },
  herald: { hair: 'short', headgear: 'petasosBack', garment: 'chlamys', beard: 'short', attribute: 'staff', age: 'adult', wash: 0.3, jaw: 0.55 },
  painter: { hair: 'sakkos', headgear: 'sakkos', garment: 'chiton', beard: null, attribute: 'brush', age: 'young', wash: 0.22, jaw: 0.3 },
  artisan: { hair: 'short', headgear: 'headband', garment: 'exomis', beard: 'full', attribute: 'chisel', age: 'adult', wash: 0.4, jaw: 0.7 },
  priestess: { hair: 'veiled', headgear: 'veilWreath', garment: 'peplos', beard: null, attribute: 'phiale', age: 'adult', wash: 0.18, jaw: 0.3 },
});

const f1 = (n) => Math.round(n * 10) / 10;
/** 把 [命令, 坐标…] 的列表拼成 SVG 路径（坐标保留一位小数，结果稳定可比）。 */
function path(...parts) {
  return parts.map((p) => (typeof p === 'number' ? f1(p) : p)).join(' ');
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

/**
 * 一张半身像：返回 { viewBox, character, expression, paths: [{ d, fill?, stroke?, width?, opacity? }] }。
 * 同样的输入永远给出同样的路径（截图与断言可复现）。
 */
export function bust(character, expression = 'neutral') {
  const c = CHARACTERS[character];
  if (!c) throw new Error(`busts: unknown character '${character}'`);
  if (!EXPRESSIONS.includes(expression)) throw new Error(`busts: unknown expression '${expression}'`);
  const paths = [];
  const ink = (d, width = 1.15, extra = {}) => paths.push({ d, stroke: INK, width, ...extra });
  const wash = (d, opacity) => paths.push({ d, fill: WASH, opacity });
  const fill = (d, color, opacity = 1) => paths.push({ d, fill: color, opacity });

  // —— 画框：象牙色的地、一圈边、顶上的回纹带 ——
  fill(path('M', 13, 3, 'L', 107, 3, 'Q', 117, 3, 117, 13, 'L', 117, 137, 'Q', 117, 147, 107, 147, 'L', 13, 147, 'Q', 3, 147, 3, 137, 'L', 3, 13, 'Q', 3, 3, 13, 3, 'Z'), GROUND);
  ink(path('M', 13, 3, 'L', 107, 3, 'Q', 117, 3, 117, 13, 'L', 117, 137, 'Q', 117, 147, 107, 147, 'L', 13, 147, 'Q', 3, 147, 3, 137, 'L', 3, 13, 'Q', 3, 3, 13, 3, 'Z'), 1.4);
  ink(path('M', 10, 22, 'L', 110, 22), 0.6, { opacity: 0.7 });
  ink(meander(12, 108, 11, 8), 0.8, { opacity: 0.8 });

  const old = c.age === 'old';
  const jaw = c.jaw;
  const speak = expression === 'speak';
  const smile = expression === 'smile';
  const browLift = speak ? 1.4 : 0;

  // —— 衣着与肩：先画，头压在上面 ——
  const garment = c.garment;
  const shoulderBack = garment === 'exomis' ? path('M', 44, 112, 'C', 34, 116, 22, 122, 14, 147) : path('M', 44, 110, 'C', 32, 115, 20, 124, 12, 147);
  const shoulderFront = path('M', 78, 118, 'C', 88, 121, 99, 128, 106, 147);
  const garmentOutline = path('M', 12, 147, 'C', 20, 124, 32, 115, 44, 110, 'L', 50, 116, 'C', 58, 121, 70, 122, 78, 118, 'C', 88, 121, 99, 128, 106, 147, 'Z');
  wash(garmentOutline, c.wash);
  ink(shoulderBack, 1.2);
  ink(shoulderFront, 1.2);
  if (garment === 'peplos') {
    // 佩普洛斯的翻折：领口一道弧，胸前几道竖褶
    ink(path('M', 47, 114, 'C', 58, 124, 70, 125, 80, 119), 1.0);
    for (const x of [56, 63, 70, 77]) ink(path('M', x, 124, 'C', x - 1, 132, x + 1, 139, x - 1, 147), 0.7, { opacity: 0.75 });
    ink(path('M', 40, 128, 'C', 52, 133, 74, 134, 96, 129), 0.7, { opacity: 0.6 });
  } else if (garment === 'chiton') {
    ink(path('M', 48, 114, 'C', 58, 121, 70, 122, 79, 118), 1.0);
    for (const x of [52, 58, 64, 70, 76, 82]) ink(path('M', x, 122, 'L', x + 1.5, 147), 0.55, { opacity: 0.7 });
  } else if (garment === 'exomis') {
    // 只遮一边肩：斜过胸前的布边，露出的肩画成皮肤的轮廓
    ink(path('M', 40, 113, 'C', 56, 126, 76, 136, 100, 141), 1.1);
    ink(path('M', 62, 128, 'C', 70, 137, 84, 141, 98, 147), 0.6, { opacity: 0.7 });
    fill(path('M', 44, 110, 'L', 78, 118, 'C', 88, 121, 96, 126, 101, 136, 'C', 80, 131, 58, 122, 44, 110, 'Z'), GROUND);
    ink(path('M', 78, 118, 'C', 88, 121, 96, 126, 101, 136), 1.15);
  } else if (garment === 'himation' || garment === 'chlamys') {
    // 斜披的外衣：从一肩斜下，几道弧形的褶
    ink(path('M', 30, 120, 'C', 48, 120, 70, 128, 90, 147), 1.0);
    for (const k of [0, 1, 2]) ink(path('M', 26 + k * 8, 128 + k * 4, 'C', 44 + k * 8, 128 + k * 4, 62 + k * 6, 136 + k * 3, 74 + k * 6, 147), 0.6, { opacity: 0.7 });
    if (garment === 'chlamys') ink(path('M', 80, 119, 'C', 84, 124, 83, 129, 79, 131), 0.9); // 肩头的扣
  }

  // —— 脖子 ——
  ink(path('M', 75, 100, 'C', 76, 106, 77, 112, 78, 118), 1.1);
  ink(path('M', 46, 88, 'C', 47, 96, 46, 104, 44, 110), 1.1);

  // —— 脸：希腊式的侧脸，额头到鼻尖几乎一条直线 ——
  const chinX = 82 + jaw * 2.5;
  const face = path(
    'M', 72, 42,
    'C', 75, 46, 77, 50, 78.5, 55,
    'L', 87.5, 73.5,
    'C', 88, 75, 86.5, 76.5, 84.5, 76.6,
    'C', 84, 77.4, 84.2, 78.6, 85.2, 79.6,
    'C', 85.6, 80.4, 85, 81.4, 83.6, 81.8,
    'C', 84.6, 83.2, 84.6, 84.8, 83.2, 85.8,
    'C', chinX + 0.6, 89, chinX + 0.6, 93.5, chinX - 3, 96.5,
    'C', chinX - 6, 99, 79, 99.5, 75, 100,
  );
  ink(face, 1.2);
  // 下颌线：方一些（男性、年长）或柔一些
  ink(path('M', chinX - 3, 96.5, 'C', 70 - jaw * 2, 96 + jaw, 64 - jaw * 2, 93, 59, 88), 0.9, { opacity: 0.85 });
  // 嘴：平常一条线；说话微张；微笑嘴角上扬、带一道笑纹
  if (speak) {
    fill(path('M', 79.6, 82.6, 'C', 81.4, 82.2, 82.8, 82.4, 83.4, 82.9, 'C', 82.6, 84.2, 80.8, 84.5, 79.6, 83.6, 'Z'), INK, 0.85);
  } else if (smile) {
    ink(path('M', 79, 81.8, 'C', 80.6, 83.3, 82.2, 83.2, 83.6, 82.4), 0.9);
    ink(path('M', 76.2, 79.5, 'C', 77, 81, 77.6, 82.4, 78.6, 83.2), 0.6, { opacity: 0.7 });
  } else {
    ink(path('M', 79.3, 82.7, 'L', 83.6, 82.6), 0.9);
  }
  // 眼：侧面的杏仁眼（白底陶瓶上古典期的画法），瞳仁一点
  const lid = smile ? 0.8 : 0;
  ink(path('M', 73.2, 62.4, 'C', 75.6, 60.8, 78.4, 60.9, 80.4, 62.6), 1.0);
  ink(path('M', 73.6, 63.2 - lid * 0.3, 'C', 75.6, 64.6 - lid, 78.2, 64.4 - lid, 80.2, 62.9), 0.8);
  fill(path('M', 78.2, 62.5, 'C', 78.2, 61.7, 79.4, 61.7, 79.4, 62.5, 'C', 79.4, 63.3, 78.2, 63.3, 78.2, 62.5, 'Z'), INK);
  if (c.archaic) {
    // 黄金侍女：古风少女像那样的正面杏仁眼与"古风的微笑"
    ink(path('M', 74, 62.6, 'C', 76.5, 59.8, 80, 60.4, 81.5, 63), 0.6, { opacity: 0.6 });
  }
  // 眉
  ink(path('M', 71.6, 58.2 - browLift, 'C', 74.4, 56.6 - browLift, 78.2, 56.7 - browLift, 80.8, 58.4 - browLift * 0.6), 1.0);
  if (old) {
    ink(path('M', 73, 52, 'C', 75, 51.4, 77, 51.4, 78, 52), 0.5, { opacity: 0.6 });   // 额纹
    ink(path('M', 76.4, 66.6, 'C', 77.6, 67.6, 79, 67.8, 80.4, 67), 0.5, { opacity: 0.6 }); // 眼袋
  }
  // 耳
  ink(path('M', 58.6, 64, 'C', 53.6, 63.2, 52.4, 72, 54.6, 77.2, 'C', 55.6, 79.4, 57.6, 80, 58.6, 79), 1.0);
  ink(path('M', 57.4, 67.2, 'C', 55.6, 68.6, 55.8, 73, 57.2, 75.4), 0.6, { opacity: 0.75 });

  // —— 头发 ——
  const crown = path('M', 72, 42, 'C', 66, 30, 50, 27, 42, 36, 'C', 35, 44, 36, 60, 40, 72, 'C', 42, 79, 44, 84, 46, 88, 'L', 54, 85, 'C', 52, 78, 52, 70, 56, 62, 'C', 60, 54, 66, 48, 72, 42, 'Z');
  switch (c.hair) {
    case 'bun':
    case 'braids':
      wash(crown, 0.82);
      for (let k = 0; k < 5; k++) ink(path('M', 70 - k * 4, 41 + k * 1.5, 'C', 62 - k * 4, 36 + k, 50 - k * 2, 38 + k * 2, 44 - k, 50 + k * 4), 0.55, { opacity: 0.85 });
      if (c.hair === 'bun') {
        // 脑后的发髻（krobylos）
        wash(path('M', 36, 52, 'C', 24, 50, 22, 68, 33, 72, 'C', 40, 74, 44, 64, 41, 56, 'Z'), 0.85);
        ink(path('M', 36, 52, 'C', 24, 50, 22, 68, 33, 72, 'C', 40, 74, 44, 64, 41, 56), 0.9);
        ink(path('M', 30, 58, 'C', 28, 63, 30, 67, 34, 68), 0.5, { opacity: 0.8 });
      } else {
        // 古风少女：几缕长辫垂到肩上
        for (let k = 0; k < 3; k++) ink(path('M', 44 + k * 3, 86, 'C', 43 + k * 3, 96, 46 + k * 3, 106, 43 + k * 3, 116), 1.6, { opacity: 0.8 });
      }
      break;
    case 'short':
    case 'receding':
    case 'fringe': {
      const top = c.hair === 'short'
        ? crown
        : c.hair === 'receding'
          ? path('M', 62, 38, 'C', 54, 31, 44, 32, 40, 40, 'C', 36, 50, 37, 64, 41, 74, 'C', 43, 80, 44, 84, 46, 88, 'L', 54, 85, 'C', 52, 76, 52, 66, 55, 58, 'C', 57, 50, 60, 44, 62, 38, 'Z')
          : path('M', 50, 54, 'C', 44, 52, 38, 58, 39, 68, 'C', 40, 78, 43, 84, 46, 88, 'L', 54, 85, 'C', 52, 78, 52, 70, 54, 62, 'Z');
      wash(top, old ? 0.45 : 0.85);
      if (c.hair !== 'short') ink(path('M', 72, 42, 'C', 66, 30, 50, 27, 42, 36, 'C', 37, 42, 37, 50, 39, 58), 1.0); // 秃顶的轮廓
      for (let k = 0; k < 4; k++) ink(path('M', 48 + k * 5, 40 + k * 1.2, 'C', 46 + k * 4, 46, 44 + k * 3, 54, 44 + k * 2, 62 + k * 3), 0.5, { opacity: 0.7 });
      break;
    }
    case 'curly':
      wash(crown, 0.85);
      for (let k = 0; k < 9; k++) {
        const x = 44 + (k % 3) * 9, y = 36 + Math.floor(k / 3) * 12;
        ink(path('M', x, y, 'c', 2.4, -2.2, 5.2, -0.8, 4.6, 1.8, 'c', -0.6, 2.4, -3.6, 2.6, -4.6, 0.6), 0.6, { opacity: 0.85 });
      }
      break;
    case 'sakkos':
      // 包在发网（sakkos）里的头发：一个向后垂的布袋
      wash(path('M', 72, 42, 'C', 64, 28, 44, 26, 36, 40, 'C', 28, 52, 30, 70, 38, 80, 'C', 42, 84, 46, 86, 50, 86, 'L', 54, 84, 'C', 52, 74, 53, 64, 57, 58, 'C', 61, 52, 66, 46, 72, 42, 'Z'), 0.35);
      ink(path('M', 72, 42, 'C', 64, 28, 44, 26, 36, 40, 'C', 28, 52, 30, 70, 38, 80, 'C', 42, 84, 46, 86, 50, 86), 1.0);
      for (let k = 0; k < 4; k++) ink(path('M', 40 + k * 6, 34 + k * 2, 'C', 36 + k * 6, 48, 38 + k * 5, 64, 42 + k * 4, 80), 0.5, { opacity: 0.6 });
      ink(path('M', 72, 42, 'C', 70, 46, 70, 49, 72.6, 50), 0.6); // 额前露出的一缕
      break;
    case 'veiled':
    case 'hidden':
    default:
      break;
  }

  // —— 胡须 ——
  if (c.beard === 'short' || c.beard === 'full' || c.beard === 'long') {
    const len = c.beard === 'long' ? 22 : c.beard === 'full' ? 9 : 4;
    const beard = path('M', 60, 82, 'C', 64, 92, 70, 99 + len * 0.4, chinX - 1, 98 + len, 'C', chinX + 2, 96 + len * 0.6, chinX + 1.4, 90, 84.4, 86.4, 'C', 82, 87.6, 79, 86.6, 77, 84.6, 'C', 72, 86, 66, 85, 60, 82, 'Z');
    wash(beard, old ? 0.4 : 0.78);
    ink(path('M', 60, 82, 'C', 64, 92, 70, 99 + len * 0.4, chinX - 1, 98 + len), 0.8);
    for (let k = 0; k < 4; k++) ink(path('M', 64 + k * 4, 86 + k, 'C', 66 + k * 4, 92 + len * 0.3, 68 + k * 4, 96 + len * 0.6, 70 + k * 3.5, 98 + len * 0.8), 0.5, { opacity: 0.75 });
    ink(path('M', 77.6, 80.6, 'C', 79.6, 79.6, 82, 79.6, 84.2, 80.6), 0.8); // 唇髭
  } else if (c.beard === 'stubble') {
    for (let k = 0; k < 12; k++) {
      const x = 62 + (k % 6) * 3.4, y = 86 + Math.floor(k / 6) * 4 + (k % 2);
      fill(path('M', x, y, 'l', 0.7, 0, 'l', 0, 0.7, 'l', -0.7, 0, 'Z'), INK, 0.6);
    }
  }

  // —— 头饰 ——
  switch (c.headgear) {
    case 'fillet':
      // 雅典娜：一条素发带（不披甲、不戴盔，设计稿 4.2）
      ink(path('M', 69, 39.6, 'C', 60, 35, 48, 40, 41, 52), 2.2, { opacity: 0.9 });
      ink(path('M', 69, 39.6, 'C', 60, 35, 48, 40, 41, 52), 0.8, { stroke: GROUND, opacity: 0.9 });
      break;
    case 'diadem':
      ink(path('M', 70, 40, 'C', 62, 33, 50, 34, 43, 42), 2.0);
      for (let k = 0; k < 5; k++) ink(path('M', 66 - k * 5, 37 - (k === 2 ? 1 : 0), 'l', 0.4, -3.2), 1.2);
      break;
    case 'headband':
      ink(path('M', 71.6, 44, 'C', 64, 38, 50, 38, 40, 48), 1.8);
      break;
    case 'petasos':
    case 'petasosBack': {
      // 宽檐旅人帽；传令官的挂在脑后
      const back = c.headgear === 'petasosBack';
      const dy = back ? 34 : 0, dx = back ? -22 : 0;
      wash(path('M', 36 + dx, 40 + dy, 'C', 44 + dx, 22 + dy, 66 + dx, 22 + dy, 74 + dx, 38 + dy, 'Z'), 0.3);
      ink(path('M', 36 + dx, 40 + dy, 'C', 44 + dx, 22 + dy, 66 + dx, 22 + dy, 74 + dx, 38 + dy), 1.0);
      ink(path('M', 26 + dx, 42 + dy, 'C', 44 + dx, 34 + dy, 70 + dx, 33 + dy, 86 + dx, 38 + dy), 1.4);
      if (back) ink(path('M', 52, 74, 'C', 60, 90, 70, 100, 76, 104), 0.6, { opacity: 0.7 }); // 帽带
      break;
    }
    case 'pilos':
    case 'pilosTilted': {
      // 圆锥形的毡帽（pilos）：铁匠、水手
      const tilt = c.headgear === 'pilosTilted' ? 6 : 0;
      wash(path('M', 38, 46, 'C', 42, 30 - tilt, 54, 18 - tilt, 60 + tilt, 20 - tilt, 'C', 68 + tilt, 24, 74, 34, 74, 44, 'C', 62, 40, 48, 41, 38, 46, 'Z'), 0.55);
      ink(path('M', 38, 46, 'C', 42, 30 - tilt, 54, 18 - tilt, 60 + tilt, 20 - tilt, 'C', 68 + tilt, 24, 74, 34, 74, 44), 1.1);
      ink(path('M', 37, 46.6, 'C', 50, 40.4, 64, 40, 75, 44.4), 1.5);
      break;
    }
    case 'hood':
      // 你（玩家）：兜帽罩住头，脸半在阴影里
      wash(path('M', 82, 48, 'C', 76, 26, 46, 22, 34, 40, 'C', 26, 54, 30, 80, 40, 96, 'C', 44, 104, 46, 110, 48, 116, 'L', 58, 112, 'C', 54, 96, 54, 80, 60, 66, 'C', 64, 58, 72, 52, 82, 48, 'Z'), 0.62);
      ink(path('M', 82, 48, 'C', 76, 26, 46, 22, 34, 40, 'C', 26, 54, 30, 80, 40, 96, 'C', 44, 104, 46, 110, 48, 116), 1.2);
      ink(path('M', 82, 48, 'C', 72, 52, 64, 58, 60, 66), 1.0);
      ink(path('M', 74, 47, 'C', 70, 52, 69, 56, 72, 58), 0.5, { opacity: 0.6 });
      break;
    case 'veilWreath':
      // 女祭司：面纱从头顶披到肩后，额上一圈月桂
      wash(path('M', 76, 44, 'C', 68, 26, 44, 24, 34, 40, 'C', 24, 58, 26, 90, 30, 118, 'L', 50, 116, 'C', 50, 98, 52, 80, 56, 66, 'C', 60, 56, 68, 48, 76, 44, 'Z'), 0.3);
      ink(path('M', 76, 44, 'C', 68, 26, 44, 24, 34, 40, 'C', 24, 58, 26, 90, 30, 118), 1.1);
      for (let k = 0; k < 6; k++) {
        const x = 70 - k * 5.4, y = 40 - Math.sin((k / 5) * Math.PI) * 4;
        ink(path('M', x, y, 'c', -1.8, -2.8, -4.2, -2.4, -4.6, 0.2, 'c', 1.6, 1.4, 3.6, 1.4, 4.6, -0.2), 0.7);
      }
      break;
    case 'sakkos':
    default:
      break;
  }

  // —— 手里的东西（只露出一截，进画框的边） ——
  switch (c.attribute) {
    case 'rod':
      ink(path('M', 100, 40, 'L', 96, 147), 2.2);
      for (let y = 50; y < 140; y += 12) ink(path('M', 99.6 - (y - 40) * 0.04, y, 'l', 3, 0), 0.8);
      break;
    case 'stylus':
      ink(path('M', 52, 64, 'L', 62, 54), 1.4);   // 夹在耳后的笔
      break;
    case 'abacus':
      wash(path('M', 82, 132, 'L', 112, 126, 'L', 114, 140, 'L', 84, 146, 'Z'), 0.25);
      ink(path('M', 82, 132, 'L', 112, 126, 'L', 114, 140, 'L', 84, 146, 'Z'), 1.0);
      for (let k = 0; k < 6; k++) fill(path('M', 88 + k * 4, 136 - k * 0.8, 'm', -1.2, 0, 'a', 1.2, 1.2, 0, 1, 0, 2.4, 0, 'a', 1.2, 1.2, 0, 1, 0, -2.4, 0), INK, 0.8);
      break;
    case 'hammer':
      ink(path('M', 104, 147, 'L', 98, 112), 2.4);
      wash(path('M', 90, 108, 'L', 106, 104, 'L', 108, 112, 'L', 92, 116, 'Z'), 0.6);
      ink(path('M', 90, 108, 'L', 106, 104, 'L', 108, 112, 'L', 92, 116, 'Z'), 1.0);
      break;
    case 'rope':
      for (let k = 0; k < 3; k++) ink(path('M', 24 + k * 3, 118 + k * 2, 'C', 34, 110 + k * 2, 52, 116 + k * 2, 50, 130 + k * 2), 1.4, { opacity: 0.85 });
      break;
    case 'scrolls':
      for (let k = 0; k < 3; k++) {
        ink(path('M', 84 + k * 7, 128 - k * 3, 'l', 9, 14), 4.2, { opacity: 0.9 });
        ink(path('M', 84 + k * 7, 128 - k * 3, 'l', 9, 14), 2.6, { stroke: GROUND });
      }
      break;
    case 'staff':
      // 传令官的权杖（kerykeion）：杖头两条相对的蛇
      ink(path('M', 104, 147, 'L', 100, 50), 2.0);
      ink(path('M', 100, 50, 'c', -6, -3, -6, -11, 0, -12, 'c', 6, 1, 6, 9, 0, 12), 1.2);
      ink(path('M', 100, 50, 'c', -4, 3, -9, 1, -9, -4, 'M', 100, 50, 'c', 4, 3, 9, 1, 9, -4), 0.9);
      break;
    case 'brush':
      ink(path('M', 108, 147, 'L', 96, 116), 1.6);
      fill(path('M', 96, 116, 'C', 94, 112, 95, 109, 97, 108, 'C', 98.6, 110, 98.4, 113, 96, 116, 'Z'), INK, 0.85);
      break;
    case 'chisel':
      ink(path('M', 102, 147, 'L', 94, 118), 2.6);
      ink(path('M', 94, 118, 'L', 92.4, 112), 1.4);
      break;
    case 'phiale':
      // 浅浅的祭碗（phiale），中间一个凸起
      wash(path('M', 80, 134, 'C', 86, 142, 108, 142, 114, 134, 'Z'), 0.3);
      ink(path('M', 80, 134, 'C', 86, 142, 108, 142, 114, 134, 'L', 80, 134), 1.0);
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
  const key = `${character}:${expression}`;
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
