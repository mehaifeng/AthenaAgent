// 画出来的五官（2026-10-12，用户：所有人物的五官是几根线条勾勒的，显得呆笨，没有活人的细腻感——用 SVG 画出细致的五官，
// 像蒙版一样贴在有起伏的脸上）。
//
// 做法：五官是一组 SVG 路径（只用 M / L / C / Q / Z，和 busts.mjs 一样走 Path2D，没有 DOM），按"脸"的毫米坐标写——
// 原点在头的中心，x 朝人物的左（+x），y 朝上；眼睛中心在 (±29.4, 15.3)，鼻尖 y ≈ −6，嘴缝 y ≈ −40.5，
// 这些位置是从雕好的头（rig.sculptHead）上量出来的，所以画上去的眼睛落在眼窝里、嘴唇落在唇的隆起上。
// 画在一张透明的画布上（五官以外 alpha 为 0），沿 z 正投影到头网格正面那一片上（faceDecalGeometry），
// 那一片沿法线往外抬 0.3 毫米——光照、阴影、起伏全是头本身的，贴图只管颜色。
//
// 五种长相（FACE_LOOKS）按人物参数挑（lookFor）：雅典娜（灰绿的"明眸"——glaukopis，上挑的眼尾）、少女、妇人、青年、
// 成年男子、老人。材质在 materials.mjs 里建（只有那里建受光材质），这里只画画布和建网格。
import * as THREE from '../../vendor/three.bundle.mjs';
import { rng, lerp } from './noise.mjs';

/** 贴图覆盖的那一块脸（毫米，头的坐标系）：x −66…66、y −88…44。 */
export const FACE_RECT = Object.freeze({ x0: -66, y0: -88, size: 132 });
/** 贴图那一片网格沿法线抬起的距离（米）：够躲开深度冲突，又看不出是浮着的一层。 */
export const DECAL_LIFT = 0.0003;
export const FACE_TEXTURE_SIZE = 512;

/** 眼睛中心（毫米）：雕好的头上眼窝的中心。 */
export const EYE = Object.freeze({ x: 29.4, y: 15.3 });

/**
 * 每种长相的参数。颜色只在画布上用，不是受光材质（受光材质都在 materials.mjs）。
 * eye：半宽、上眼睑最高处、眼尾抬高（正数上挑）、下眼睑深度；brow：眉头、眉峰、眉尾（相对眼睛中心）与粗细；
 * lips：嘴的半宽、上唇厚、下唇厚、唇色；lashes：上睫毛；age：皱纹的深浅。
 */
export const FACE_LOOKS = Object.freeze({
  athena: {
    eye: { half: 15, lid: 6.9, tilt: 2.6, low: 4.1 }, iris: ['#7f9a96', '#47605f', '#26302f'], lashes: 1, liner: 1.15,
    brow: { inner: [-20, 17.5], peak: [-3, 24.5], tail: [17, 19.6], width: 3.6, color: '#34251b' },
    lips: { half: 18.5, upper: 4.3, lower: 5.4, color: '#b8695e', shade: '#8f4a42' }, blush: 0.1, lid: '#8a6a50', age: 0,
  },
  maiden: {
    eye: { half: 14.6, lid: 6.9, tilt: 1.4, low: 4.2 }, iris: ['#6b4a32', '#4a2f1d', '#1e140e'], lashes: 0.9, liner: 0.95,
    brow: { inner: [-18.5, 16.5], peak: [-2, 22], tail: [14, 18.4], width: 2.6, color: '#3a291e' },
    lips: { half: 17.5, upper: 4, lower: 5.1, color: '#c27a6e', shade: '#99524a' }, blush: 0.14, lid: null, age: 0,
  },
  woman: {
    eye: { half: 14.4, lid: 6.2, tilt: 1, low: 4 }, iris: ['#5e4a36', '#3e2e20', '#1b130d'], lashes: 0.75, liner: 0.9,
    brow: { inner: [-18.5, 16], peak: [-2, 21.4], tail: [14.5, 17.6], width: 2.6, color: '#2f241c' },
    lips: { half: 17.5, upper: 3.7, lower: 4.8, color: '#ab665b', shade: '#874940' }, blush: 0.08, lid: null, age: 0.15,
  },
  youth: {
    eye: { half: 13.4, lid: 5.4, tilt: 0.4, low: 3.5 }, iris: ['#5a412c', '#3b2819', '#1a120c'], lashes: 0, liner: 0.8,
    brow: { inner: [-19, 15.8], peak: [-1, 20.4], tail: [15, 17.6], width: 3.3, color: '#2e2219' },
    lips: { half: 17, upper: 2.6, lower: 3.8, color: '#a2665a', shade: '#7d4a40' }, blush: 0, lid: null, age: 0,
  },
  man: {
    eye: { half: 13.4, lid: 5, tilt: 0, low: 3.5 }, iris: ['#584330', '#38291b', '#19120c'], lashes: 0, liner: 0.8,
    brow: { inner: [-19.5, 15], peak: [-1, 19.2], tail: [15.5, 16.6], width: 3.8, color: '#2a1f17' },
    lips: { half: 18, upper: 2.3, lower: 3.6, color: '#9a6155', shade: '#76463d' }, blush: 0, lid: null, age: 0.35,
  },
  elder: {
    eye: { half: 13, lid: 4.4, tilt: -1, low: 3.3 }, iris: ['#5f5446', '#3d352b', '#1b1712'], lashes: 0, liner: 0.65,
    brow: { inner: [-19.5, 15.4], peak: [-1, 19.6], tail: [15, 15.4], width: 3.4, color: '#8d8478' },
    lips: { half: 17, upper: 1.9, lower: 3.1, color: '#94625a', shade: '#704840' }, blush: 0, lid: null, age: 1,
  },
});

/** 按人物参数挑长相（characters.mjs 的 spec）。 */
export function lookFor(spec) {
  const f = spec.face ?? {};
  if (spec.id === 'athena') return 'athena';
  if (f.age === 'old') return 'elder';
  if (f.female) return f.age === 'young' ? 'maiden' : 'woman';
  return f.age === 'young' ? 'youth' : 'man';
}

/** materials.mjs 里那份贴图材质的名字：faceAthena、faceMaiden…… */
export const faceMaterialKey = (look) => `face${look[0].toUpperCase()}${look.slice(1)}`;

const n = (v) => +v.toFixed(2);
const P = (...pts) => pts.map(n).join(' ');

/**
 * 一只眼睛的路径（眼睛自己的坐标：原点在眼睛中心，+x 朝外眼角）。返回 SVG 路径字符串，测试也读它。
 * shape：上眼睑从内眼角拱到最高处再落到抬起的外眼角；下眼睑浅一些、最低处偏外。
 */
export function eyePaths(e) {
  const ix = -e.half, iy = -0.6, ox = e.half, oy = e.tilt * 0.75;
  const upper = `M ${P(ix, iy)} C ${P(ix + 3.5, iy + e.lid * 0.95, -3, e.lid * 1.12, 1, e.lid)} C ${P(5.5, e.lid * 0.9, ox - 3, oy + e.lid * 0.55, ox, oy)}`;
  const lower = `C ${P(ox - 3.5, oy - e.low * 0.75, 4, -e.low * 1.05, -1, -e.low)} C ${P(-6, -e.low * 0.92, ix + 3, iy - e.low * 0.45, ix, iy)} Z`;
  const crease = `M ${P(ix + 2.2, iy + 2.6)} C ${P(ix + 6, iy + e.lid + 3.3, 2, e.lid + 4.2, 4, e.lid + 3.4)} C ${P(8, e.lid + 2.8, ox - 1.5, oy + 3.4, ox + 1.2, oy + 1.8)}`;
  const lashLine = `M ${P(ix + 0.6, iy + 0.25)} C ${P(ix + 3.5, iy + e.lid * 0.95, -3, e.lid * 1.12, 1, e.lid)} C ${P(5.5, e.lid * 0.9, ox - 3, oy + e.lid * 0.55, ox, oy)}`;
  const lowerLine = `M ${P(ox - 1.2, oy - 0.4)} C ${P(ox - 3.5, oy - e.low * 0.75, 4, -e.low * 1.05, -1, -e.low)} C ${P(-6, -e.low * 0.92, ix + 3, iy - e.low * 0.45, ix + 2, iy - 0.5)}`;
  return { white: `${upper} ${lower}`, crease, lashLine, lowerLine, inner: [ix, iy], outer: [ox, oy] };
}

/** 眉毛的外形（眉头圆、眉峰最厚、眉尾尖），坐标同 eyePaths。 */
export function browPath(b) {
  const [ix, iy] = b.inner, [px, py] = b.peak, [tx, ty] = b.tail, w = b.width;
  return `M ${P(ix, iy - w * 0.45)} C ${P(ix - 0.8, iy + w * 0.4, ix + 2, iy + w * 0.75, ix + 4, iy + w * 0.7)} `
    + `C ${P(ix + 9, py - 1 + w * 0.55, px - 3, py + w * 0.5, px, py + w * 0.42)} C ${P(px + 6, py + w * 0.2, tx - 3, ty + 1.1, tx, ty)} `
    + `C ${P(tx - 4, ty - 0.2, px + 5, py - w * 0.55, px, py - w * 0.5)} C ${P(px - 4, py - w * 0.45, ix + 7, iy - w * 0.25, ix, iy - w * 0.45)} Z`;
}

/** 嘴唇：上唇带唇峰（丘比特弓）、下唇饱满；嘴缝两角略上扬。坐标是脸的毫米坐标。 */
export function lipPaths(l, y = -40.5) {
  const h = l.half, u = l.upper, d = l.lower;
  const upper = `M ${P(-h, y + 0.3)} C ${P(-h + 4, y + u * 0.55, -6, y + u * 1.05, -2.6, y + u * 1.12)} Q ${P(0, y + u * 0.72, 2.6, y + u * 1.12)} `
    + `C ${P(6, y + u * 1.05, h - 4, y + u * 0.55, h, y + 0.3)} C ${P(h - 5, y - 0.15, 5, y - 0.3, 0, y - 0.15)} C ${P(-5, y - 0.3, -h + 5, y - 0.15, -h, y + 0.3)} Z`;
  const lower = `M ${P(-h + 1, y + 0.1)} C ${P(-h + 5, y - 0.35, -5, y - 0.45, 0, y - 0.3)} C ${P(5, y - 0.45, h - 5, y - 0.35, h - 1, y + 0.1)} `
    + `C ${P(h - 3.5, y - d * 0.8, 5, y - d * 1.08, 0, y - d)} C ${P(-5, y - d * 1.08, -h + 3.5, y - d * 0.8, -h + 1, y + 0.1)} Z`;
  const seam = `M ${P(-h - 0.6, y + 0.7)} C ${P(-h + 5, y - 0.2, -5, y - 0.45, 0, y - 0.25)} C ${P(5, y - 0.45, h - 5, y - 0.2, h + 0.6, y + 0.7)}`;
  return { upper, lower, seam };
}

/** 把一种长相画到 2D 画布上（size×size，透明底）。纯 Path2D，同一种长相永远画成同一张。 */
export function paintFace(ctx, lookName, size = FACE_TEXTURE_SIZE) {
  const look = FACE_LOOKS[lookName];
  if (!look) throw new Error(`face: unknown look '${lookName}'`);
  const k = size / FACE_RECT.size;
  ctx.clearRect(0, 0, size, size);
  // 毫米坐标、y 朝上：画布第 0 行是 y = y0 + size
  ctx.setTransform(k, 0, 0, -k, -FACE_RECT.x0 * k, (FACE_RECT.y0 + FACE_RECT.size) * k);
  ctx.lineCap = 'round';
  ctx.lineJoin = 'round';
  const random = rng(lookName.length * 7919 + 17);
  const soft = (x, y, r, color, alpha, sy = 1) => {
    ctx.save();
    ctx.translate(x, y); ctx.scale(1, sy);
    const g = ctx.createRadialGradient(0, 0, 0, 0, 0, r);
    g.addColorStop(0, rgba(color, alpha)); g.addColorStop(1, rgba(color, 0));
    ctx.fillStyle = g;
    ctx.beginPath(); ctx.arc(0, 0, r, 0, Math.PI * 2); ctx.fill();
    ctx.restore();
  };

  // 腮红、眼影、眼周的暖色——都是很淡的一层，让脸有血色，不是化妆
  if (look.blush) for (const sx of [-1, 1]) soft(sx * 37, -12, 17, '#c8695c', look.blush, 0.7);
  for (const sx of [-1, 1]) soft(sx * (EYE.x + 1), EYE.y + 1, 15, '#7a4a3a', 0.07 + look.age * 0.06, 0.6);
  if (look.lid) for (const sx of [-1, 1]) soft(sx * (EYE.x + 2), EYE.y + 5.5, 12, look.lid, 0.22, 0.42);

  // 鼻翼下的阴影与鼻孔（正面几乎只看得到一点点）
  for (const sx of [-1, 1]) {
    soft(sx * 5.4, -19.5, 3.4, '#5a2f24', 0.42, 0.55);
    soft(sx * 9.5, -16.5, 4.5, '#7a4535', 0.1, 0.8);
  }
  soft(0, -23.5, 5, '#7a4535', 0.08, 0.6);   // 人中

  // 老人和成年男子：法令纹、眼尾的细纹、眼袋
  if (look.age > 0) {
    ctx.strokeStyle = rgba('#5a3428', 0.16 + look.age * 0.2);
    ctx.lineWidth = 0.7 + look.age * 0.5;
    for (const sx of [-1, 1]) {
      ctx.stroke(new Path2D(`M ${P(sx * 11.5, -15)} C ${P(sx * 16, -22, sx * 19.5, -31, sx * (look.lips.half + 3.5), -40)}`));
    }
    if (look.age >= 0.5) {
      ctx.lineWidth = 0.45;
      ctx.strokeStyle = rgba('#5a3428', 0.26 * look.age);
      for (const sx of [-1, 1]) {
        for (let i = 0; i < 3; i++) {
          const y0 = EYE.y + 1.5 - i * 2.4;
          ctx.stroke(new Path2D(`M ${P(sx * (EYE.x + 15), y0)} Q ${P(sx * (EYE.x + 18.5), y0 + 0.3 - i * 0.6, sx * (EYE.x + 21), y0 - 0.8 - i * 0.9)}`));
        }
        ctx.stroke(new Path2D(`M ${P(sx * (EYE.x - 9), EYE.y - 6)} C ${P(sx * (EYE.x - 3), EYE.y - 9.5, sx * (EYE.x + 5), EYE.y - 9.5, sx * (EYE.x + 11), EYE.y - 5.5)}`));
      }
    }
  }

  // 眉毛：先铺一层底色，再按生长方向一根根画（眉头朝上、往后顺着倒向眉尾）
  for (const sx of [-1, 1]) {
    ctx.save();
    ctx.translate(sx * EYE.x, EYE.y); ctx.scale(sx, 1);
    const b = look.brow;
    const shape = new Path2D(browPath(b));
    ctx.fillStyle = rgba(b.color, 0.55);
    ctx.fill(shape);
    ctx.save();
    ctx.clip(shape);
    ctx.strokeStyle = rgba(b.color, 0.9);
    const hairs = 90;
    for (let i = 0; i < hairs; i++) {
      const t = i / (hairs - 1) + (random() - 0.5) * 0.02;
      const x = lerp(b.inner[0], b.tail[0], t);
      const yMid = t < 0.6 ? lerp(b.inner[1], b.peak[1], t / 0.6) : lerp(b.peak[1], b.tail[1], (t - 0.6) / 0.4);
      const y = yMid + (random() - 0.5) * b.width * 0.9;
      const ang = t < 0.12 ? 1.2 - t * 4 : 0.35 - t * 0.55;   // 眉头立着长，往后越来越顺
      const len = 2.2 + random() * 1.8;
      ctx.lineWidth = 0.22 + random() * 0.12;
      ctx.beginPath(); ctx.moveTo(x, y); ctx.lineTo(x + Math.cos(ang) * len, y + Math.sin(ang) * len); ctx.stroke();
    }
    ctx.restore();
    ctx.restore();
  }

  // 眼睛：眼白（内外眼角暗、上眼睑投下阴影）、虹膜（外圈深、放射状纹理）、瞳孔、高光、泪阜、睫毛线、上眼睑褶
  for (const sx of [-1, 1]) {
    ctx.save();
    ctx.translate(sx * EYE.x, EYE.y); ctx.scale(sx, 1);
    const e = look.eye, paths = eyePaths(e);
    const white = new Path2D(paths.white);
    // 上眼睑褶
    ctx.strokeStyle = rgba('#6a3f30', 0.42);
    ctx.lineWidth = 0.55;
    ctx.stroke(new Path2D(paths.crease));
    ctx.fillStyle = rgba('#7a4a3a', 0.12);
    ctx.fill(new Path2D(`${paths.crease} L ${P(paths.outer[0], paths.outer[1])} L ${P(paths.inner[0], paths.inner[1])} Z`));
    // 眼白
    const wg = ctx.createLinearGradient(-e.half, 0, e.half, 0);
    wg.addColorStop(0, '#d8c4ba'); wg.addColorStop(0.25, '#efe8e2'); wg.addColorStop(0.7, '#ece4dd'); wg.addColorStop(1, '#cdb9ae');
    ctx.fillStyle = wg;
    ctx.fill(white);
    ctx.save();
    ctx.clip(white);
    // 虹膜（略偏上：上眼睑盖住虹膜的一小截，像活人）
    const ir = e.half * 0.42, ix = 0.3, iy = 0.9;
    const ig = ctx.createRadialGradient(ix, iy, 0.6, ix, iy, ir);
    ig.addColorStop(0, look.iris[0]); ig.addColorStop(0.55, look.iris[0]); ig.addColorStop(0.85, look.iris[1]); ig.addColorStop(1, look.iris[2]);
    ctx.fillStyle = ig;
    ctx.beginPath(); ctx.arc(ix, iy, ir, 0, Math.PI * 2); ctx.fill();
    ctx.lineWidth = 0.18;
    for (let i = 0; i < 48; i++) {
      const a = (i / 48) * Math.PI * 2 + random() * 0.08, r0 = 2.1 + random() * 0.5, r1 = ir * (0.75 + random() * 0.2);
      ctx.strokeStyle = rgba(random() < 0.5 ? '#ffffff' : look.iris[2], 0.14 + random() * 0.12);
      ctx.beginPath(); ctx.moveTo(ix + Math.cos(a) * r0, iy + Math.sin(a) * r0); ctx.lineTo(ix + Math.cos(a) * r1, iy + Math.sin(a) * r1); ctx.stroke();
    }
    ctx.fillStyle = '#0d0907';
    ctx.beginPath(); ctx.arc(ix, iy, ir * 0.37, 0, Math.PI * 2); ctx.fill();
    // 上眼睑在眼球上投下的影
    const sg = ctx.createLinearGradient(0, e.lid + 0.5, 0, e.lid - 3.2);
    sg.addColorStop(0, 'rgba(60,32,24,0.55)'); sg.addColorStop(1, 'rgba(60,32,24,0)');
    ctx.fillStyle = sg;
    ctx.fillRect(-e.half - 1, e.lid - 4, e.half * 2 + 2, 6);
    // 高光：一大一小，在光来的那一边
    ctx.fillStyle = 'rgba(255,255,255,0.92)';
    ctx.beginPath(); ctx.ellipse(ix + 1.8 * sx, iy + 1.9, 1.15, 0.95, 0, 0, Math.PI * 2); ctx.fill();
    ctx.fillStyle = 'rgba(255,255,255,0.5)';
    ctx.beginPath(); ctx.arc(ix - 1.6 * sx, iy - 1.7, 0.45, 0, Math.PI * 2); ctx.fill();
    ctx.restore();
    // 泪阜
    ctx.fillStyle = rgba('#c98a80', 0.85);
    ctx.beginPath(); ctx.ellipse(paths.inner[0] + 1.4, paths.inner[1] - 0.1, 1.3, 0.9, 0, 0, Math.PI * 2); ctx.fill();
    // 上睫毛线（外眼角粗，女子的往外挑出一笔）
    ctx.strokeStyle = rgba('#1c120d', 0.92);
    ctx.lineWidth = 0.75 * look.liner;
    ctx.stroke(new Path2D(paths.lashLine));
    ctx.lineWidth = 1.25 * look.liner;
    ctx.stroke(new Path2D(`M ${P(2, e.lid * 0.98)} C ${P(6, e.lid * 0.85, e.half - 3, paths.outer[1] + e.lid * 0.5, paths.outer[0] + 0.2, paths.outer[1])}`));
    if (look.lashes > 0) {
      ctx.lineWidth = 0.75;
      ctx.stroke(new Path2D(`M ${P(paths.outer[0] - 1, paths.outer[1] + 0.3)} Q ${P(paths.outer[0] + 2.2, paths.outer[1] + 1.1, paths.outer[0] + 3.4 * look.lashes, paths.outer[1] + 2.4 * look.lashes)}`));
      ctx.lineWidth = 0.32;
      for (let i = 0; i < 16; i++) {
        const t = 0.3 + (i / 15) * 0.68;
        const x = lerp(-e.half, e.half, t);
        const y = upperLidAt(e, x) + 0.2;
        const len = (1.1 + t * 1.7) * look.lashes;
        const ang = Math.PI / 2 - (t - 0.2) * 1.3;
        ctx.beginPath(); ctx.moveTo(x, y);
        ctx.quadraticCurveTo(x + Math.cos(ang) * len * 0.6, y + Math.sin(ang) * len * 0.75, x + Math.cos(ang - 0.35) * len, y + Math.sin(ang - 0.35) * len * 0.9);
        ctx.stroke();
      }
    }
    // 下眼睑：一条很淡的线
    ctx.strokeStyle = rgba('#4a2c22', 0.38);
    ctx.lineWidth = 0.42;
    ctx.stroke(new Path2D(paths.lowerLine));
    ctx.restore();
  }

  // 嘴唇：上唇比下唇暗（受光朝下），下唇中间一块柔和的高光，嘴缝两端加深
  const lips = lipPaths(look.lips);
  const lc = look.lips;
  const ug = ctx.createLinearGradient(0, -40.5 + lc.upper, 0, -40.5);
  ug.addColorStop(0, rgba(lc.color, 0.88)); ug.addColorStop(1, rgba(lc.shade, 0.95));
  ctx.fillStyle = ug;
  ctx.fill(new Path2D(lips.upper));
  const lg = ctx.createLinearGradient(0, -40.5, 0, -40.5 - lc.lower);
  lg.addColorStop(0, rgba(lc.shade, 0.82)); lg.addColorStop(0.45, rgba(lc.color, 0.85)); lg.addColorStop(1, rgba(lc.color, 0.5));
  ctx.fillStyle = lg;
  ctx.fill(new Path2D(lips.lower));
  soft(0, -40.5 - lc.lower * 0.48, lc.half * 0.42, '#ffffff', 0.2, 0.32);
  ctx.strokeStyle = rgba('#3c1d17', 0.78);
  ctx.lineWidth = 0.55;
  ctx.stroke(new Path2D(lips.seam));
  for (const sx of [-1, 1]) soft(sx * (lc.half + 0.4), -40, 1.6, '#3c1d17', 0.35);
  soft(0, -40.5 - lc.lower - 2.2, 6, '#6a3a2c', 0.1, 0.4);   // 下唇下的影

  ctx.setTransform(1, 0, 0, 1, 0, 0);
}

/** 上眼睑在 x 处的高度（睫毛沿它长）：三次贝塞尔按 x 近似求值。 */
function upperLidAt(e, x) {
  const ix = -e.half, iy = -0.6, ox = e.half, oy = e.tilt * 0.75;
  const seg = x < 1
    ? [[ix, iy], [ix + 3.5, iy + e.lid * 0.95], [-3, e.lid * 1.12], [1, e.lid]]
    : [[1, e.lid], [5.5, e.lid * 0.9], [ox - 3, oy + e.lid * 0.55], [ox, oy]];
  let lo = 0, hi = 1;
  const at = (t, i) => (1 - t) ** 3 * seg[0][i] + 3 * (1 - t) ** 2 * t * seg[1][i] + 3 * (1 - t) * t * t * seg[2][i] + t ** 3 * seg[3][i];
  for (let k = 0; k < 24; k++) { const m = (lo + hi) / 2; if (at(m, 0) < x) lo = m; else hi = m; }
  return at((lo + hi) / 2, 1);
}

function rgba(hex, a) {
  const v = parseInt(hex.slice(1), 16);
  return `rgba(${(v >> 16) & 255},${(v >> 8) & 255},${v & 255},${a})`;
}

/** 一种长相的贴图（浏览器里；materials.mjs 调用）。透明处按预乘 alpha 过滤，边缘不发黑。 */
export function faceTexture(lookName, size = FACE_TEXTURE_SIZE) {
  const canvas = document.createElement('canvas');
  canvas.width = canvas.height = size;
  paintFace(canvas.getContext('2d'), lookName, size);
  const t = new THREE.CanvasTexture(canvas);
  t.colorSpace = THREE.SRGBColorSpace;
  t.premultiplyAlpha = true;
  t.anisotropy = 8;
  return t;
}

const decalCache = new WeakMap();
/**
 * 头网格正面那一片（贴图的载体）：同一批顶点沿法线抬 DECAL_LIFT，UV 是 x、y 在 FACE_RECT 里的位置（沿 z 正投影），
 * 只留三个顶点都在脸前面（z > 2 厘米）、都落在贴图范围里的三角形。按头的几何缓存。
 */
export function faceDecalGeometry(head) {
  if (decalCache.has(head)) return decalCache.get(head);
  const pos = head.attributes.position, nrm = head.attributes.normal;
  const count = pos.count;
  const out = new Float32Array(count * 3), uv = new Float32Array(count * 2), normal = new Float32Array(count * 3);
  const inside = new Uint8Array(count);
  for (let i = 0; i < count; i++) {
    const x = pos.getX(i), y = pos.getY(i), z = pos.getZ(i);
    out[i * 3] = x + nrm.getX(i) * DECAL_LIFT; out[i * 3 + 1] = y + nrm.getY(i) * DECAL_LIFT; out[i * 3 + 2] = z + nrm.getZ(i) * DECAL_LIFT;
    normal[i * 3] = nrm.getX(i); normal[i * 3 + 1] = nrm.getY(i); normal[i * 3 + 2] = nrm.getZ(i);
    const u = (x * 1000 - FACE_RECT.x0) / FACE_RECT.size, v = (y * 1000 - FACE_RECT.y0) / FACE_RECT.size;
    uv[i * 2] = u; uv[i * 2 + 1] = v;
    inside[i] = z > 0.02 && u >= 0 && u <= 1 && v >= 0 && v <= 1 ? 1 : 0;
  }
  const src = head.index.array, keep = [];
  for (let i = 0; i < src.length; i += 3) {
    if (inside[src[i]] && inside[src[i + 1]] && inside[src[i + 2]]) keep.push(src[i], src[i + 1], src[i + 2]);
  }
  const g = new THREE.BufferGeometry();
  g.setAttribute('position', new THREE.BufferAttribute(out, 3));
  g.setAttribute('normal', new THREE.BufferAttribute(normal, 3));
  g.setAttribute('uv', new THREE.BufferAttribute(uv, 2));
  g.setIndex(keep);
  decalCache.set(head, g);
  return g;
}
