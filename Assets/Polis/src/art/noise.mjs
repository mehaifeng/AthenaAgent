// 确定性、可平铺的噪声与程序化贴图（取自 Artwork/Polis/sample/polis-sample.js）。同一个种子每次生成同样的纹理。
import * as THREE from '../../vendor/three.bundle.mjs';

export const lerp = THREE.MathUtils.lerp;
export const deg = THREE.MathUtils.degToRad;
export const smooth = (a, b, x) => {
  const t = Math.min(1, Math.max(0, (x - a) / (b - a)));
  return t * t * (3 - 2 * t);
};

export function rng(seed) {
  return () => {
    seed |= 0; seed = (seed + 0x6D2B79F5) | 0;
    let t = Math.imul(seed ^ (seed >>> 15), 1 | seed);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

/** 字符串 → 32 位种子（FNV-1a），建筑名决定它的细节，换一次加载也不会变。 */
export function hashSeed(text) {
  let h = 0x811c9dc5;
  for (const ch of String(text)) {
    h ^= ch.codePointAt(0);
    h = Math.imul(h, 0x01000193);
  }
  return h >>> 0;
}

export function valueNoise(seed) {
  const r = rng(seed);
  const lattice = new Float32Array(256 * 256);
  for (let i = 0; i < lattice.length; i++) lattice[i] = r();
  const at = (x, y, p) => lattice[(((x % p) + p) % p) * 256 + (((y % p) + p) % p)];
  return (x, y, p = 256) => {
    // 周期超过格点表就会越界读出 NaN，贴图上整排变黑——打样第一版地面的黑色斜带就是这么来的
    if (p > 256) throw new Error(`valueNoise: period ${p} exceeds the 256 lattice`);
    const xi = Math.floor(x), yi = Math.floor(y);
    const xf = x - xi, yf = y - yi;
    const u = xf * xf * (3 - 2 * xf), v = yf * yf * (3 - 2 * yf);
    const a = at(xi, yi, p), b = at(xi + 1, yi, p), c = at(xi, yi + 1, p), d = at(xi + 1, yi + 1, p);
    return a + (b - a) * u + (c - a) * v + (a - b - c + d) * u * v;
  };
}

export function fbm(noise, x, y, period, octaves = 5) {
  let sum = 0, amp = 0.5, freq = 1, norm = 0;
  for (let o = 0; o < octaves; o++) {
    sum += amp * noise(x * freq, y * freq, period * freq);
    norm += amp; amp *= 0.5; freq *= 2;
  }
  return sum / norm;
}

export function field(size, fn) {
  const f = new Float32Array(size * size);
  for (let y = 0; y < size; y++) for (let x = 0; x < size; x++) f[y * size + x] = fn(x / size, y / size);
  return f;
}

export function textureFrom(size, values, toRGB, { repeat = 1, srgb = true } = {}) {
  const canvas = document.createElement('canvas');
  canvas.width = canvas.height = size;
  const ctx = canvas.getContext('2d');
  const img = ctx.createImageData(size, size);
  for (let i = 0; i < size * size; i++) {
    const [r, g, b] = toRGB(values[i]);
    img.data[i * 4] = r; img.data[i * 4 + 1] = g; img.data[i * 4 + 2] = b; img.data[i * 4 + 3] = 255;
  }
  ctx.putImageData(img, 0, 0);
  const t = new THREE.CanvasTexture(canvas);
  t.wrapS = t.wrapT = THREE.RepeatWrapping;
  t.repeat.set(repeat, repeat);
  t.colorSpace = srgb ? THREE.SRGBColorSpace : THREE.NoColorSpace;
  t.anisotropy = 8;
  return t;
}

export const tone = (v, tint = [1, 1, 1]) => [v * 255 * tint[0], v * 255 * tint[1], v * 255 * tint[2]];
