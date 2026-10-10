// 材质。世界最终会被调成黑白，这里仍按真实颜色给，是为了让去色后的明暗关系像胶片拍到的那样自然
// （暖白的大理石、偏暗的土、更暗一档的屋顶）。大理石、土、铺地、布料取自打样。
import * as THREE from '../../vendor/three.bundle.mjs';
import { valueNoise, fbm, field, textureFrom, tone, smooth, rng } from './noise.mjs';

export function makeMaterials() {
  const n1 = valueNoise(11), n2 = valueNoise(23), n3 = valueNoise(37), n4 = valueNoise(41), n5 = valueNoise(53);

  const marbleField = field(256, (u, v) => {
    const cloud = fbm(n1, u * 8, v * 8, 8, 5);
    const warp = fbm(n2, u * 4, v * 4, 4, 4);
    const vein = Math.abs(Math.sin((u * 2 + v * 1 + warp * 2.6) * Math.PI * 2));
    return cloud - (1 - smooth(0.0, 0.04, vein)) * 0.3;
  });
  const marbleMap = textureFrom(256, marbleField, (f) => tone(0.80 + (f - 0.5) * 0.10, [1, 0.985, 0.955]));
  const marbleBump = textureFrom(256, marbleField, (f) => tone(f), { srgb: false });

  const earthField = field(512, (u, v) => {
    const broad = fbm(n3, u * 8, v * 8, 8, 6);
    const grit = fbm(n4, u * 64, v * 64, 64, 2);
    const tuft = smooth(0.6, 0.7, fbm(n5, u * 32, v * 32, 32, 3));
    return broad * 0.6 + grit * 0.4 - tuft * 0.16;
  });
  const earthMap = textureFrom(512, earthField, (f) => tone(0.50 + (f - 0.45) * 0.24, [1, 0.96, 0.9]), { repeat: 40 });

  // 铺地石板：行高、块宽都不等的错缝条石
  const paveField = (() => {
    const size = 256, f = new Float32Array(size * size), r = rng(5);
    const rows = [];
    for (let y = 0; y < size;) { const h = Math.min(size - y, 24 + Math.floor(r() * 20)); rows.push({ y0: y, y1: y + h }); y += h; }
    for (const row of rows) {
      row.breaks = [];
      let x = r() * 60;
      while (x < size + 100) { row.breaks.push(x); x += 30 + r() * 70; }
      row.tones = row.breaks.map(() => r());
    }
    for (const row of rows) {
      for (let y = row.y0; y < row.y1; y++) {
        const dy = Math.min(y - row.y0, row.y1 - y);
        for (let x = 0; x < size; x++) {
          let k = 0;
          while (k < row.breaks.length - 1 && row.breaks[k + 1] <= x) k++;
          const left = row.breaks[k] <= x ? row.breaks[k] : row.breaks[k] - size;
          const right = row.breaks[k + 1] ?? left + 60;
          const dx = Math.min(Math.abs(x - left), Math.abs(right - x));
          const wear = fbm(n4, (x / size) * 16, (y / size) * 16, 16, 4);
          const grout = Math.min(dx, dy) < 0.9 + wear * 0.7;
          const grain = fbm(n1, (x / size) * 32, (y / size) * 32, 32, 3);
          f[y * size + x] = grout ? -1 : row.tones[k] * 0.45 + grain * 0.3 + wear * 0.25;
        }
      }
    }
    return f;
  })();
  const paveMap = textureFrom(256, paveField, (f) => (f < 0 ? tone(0.5, [1, 0.97, 0.93]) : tone(0.62 + (f - 0.5) * 0.16, [1, 0.98, 0.94])));
  const paveBump = textureFrom(256, paveField, (f) => tone(f < 0 ? 0.2 : 0.55 + f * 0.45), { srgb: false });

  const clothField = field(128, (u, v) => fbm(n2, u * 16, v * 32, 16, 3));
  const clothMap = textureFrom(128, clothField, (f) => tone(0.90 + (f - 0.5) * 0.06, [1, 0.99, 0.965]), { repeat: 3 });

  // 粉墙：民居的白灰墙，有一点刷痕和污渍
  const plasterField = field(256, (u, v) => fbm(n3, u * 16, v * 16, 16, 4) * 0.7 + fbm(n5, u * 64, v * 8, 64, 2) * 0.3);
  const plasterMap = textureFrom(256, plasterField, (f) => tone(0.84 + (f - 0.5) * 0.12, [1, 0.975, 0.94]));

  // 海：静止的法线起伏 + 低粗糙度，去色后是一片发亮的深灰
  const waveField = field(256, (u, v) => fbm(n2, u * 16, v * 16, 16, 4));
  const waveNormal = textureFrom(256, waveField, (f) => [128 + (f - 0.5) * 90, 128 + (f - 0.5) * 60, 255], { srgb: false, repeat: 30 });

  const marble = new THREE.MeshStandardMaterial({ map: marbleMap, bumpMap: marbleBump, bumpScale: 0.35, roughness: 0.6 });
  const wall = marble.clone(); wall.color = new THREE.Color(0.9, 0.9, 0.9);
  const pave = new THREE.MeshStandardMaterial({ map: paveMap, bumpMap: paveBump, bumpScale: 0.6, roughness: 0.92 });
  // 街道与广场同一种石板，略灰一点；贴图的平铺靠几何体的 UV（每 4 米一块），不靠贴图的 repeat
  const street = pave.clone();
  street.color = new THREE.Color(0.93, 0.92, 0.9);
  return {
    marble,
    wall,
    plaster: new THREE.MeshStandardMaterial({ map: plasterMap, roughness: 0.9 }),
    // 俯视时屋顶占画面最大，得比铺地明显暗一档，城里的建筑才分得开
    roof: new THREE.MeshStandardMaterial({ color: 0x75685e, roughness: 0.8 }),
    tile: new THREE.MeshStandardMaterial({ color: 0x8a6656, roughness: 0.75 }),
    dark: new THREE.MeshStandardMaterial({ color: 0x1c1a18, roughness: 0.9 }),
    shadowGap: new THREE.MeshStandardMaterial({ color: 0x2c2723, roughness: 1 }),
    wood: new THREE.MeshStandardMaterial({ color: 0x6b5a48, roughness: 0.85 }),
    darkWood: new THREE.MeshStandardMaterial({ color: 0x4a3d31, roughness: 0.9 }),
    bronze: new THREE.MeshStandardMaterial({ color: 0x6f5b3f, roughness: 0.45, metalness: 0.75 }),
    stone: new THREE.MeshStandardMaterial({ color: 0x9c9488, roughness: 0.95 }),
    earth: new THREE.MeshStandardMaterial({ map: earthMap, roughness: 1 }),
    pave,
    street,
    // 海：比第一版亮两档。正午的海去色后应当是发亮的中灰，一大片近黑会压住整个画面
    water: new THREE.MeshStandardMaterial({ color: 0x7d8a93, roughness: 0.14, metalness: 0.05, normalMap: waveNormal, normalScale: new THREE.Vector2(0.8, 0.8) }),
    cloth: new THREE.MeshStandardMaterial({
      map: clothMap, roughness: 0.86, side: THREE.DoubleSide,
      emissive: new THREE.Color(1, 0.98, 0.94), emissiveIntensity: 0.025,
    }),
    awning: new THREE.MeshStandardMaterial({ map: clothMap, color: 0xcfc6b6, roughness: 0.9, side: THREE.DoubleSide }),
    playerCloth: new THREE.MeshStandardMaterial({ map: clothMap, color: 0x8d8780, roughness: 0.9, side: THREE.DoubleSide }),
    skin: new THREE.MeshStandardMaterial({ color: 0xc8ae98, roughness: 0.6 }),
    hair: new THREE.MeshStandardMaterial({ color: 0x3a332e, roughness: 0.65 }),
    gold: new THREE.MeshStandardMaterial({ color: 0xd8c08a, roughness: 0.3, metalness: 0.9 }),
    maiden: new THREE.MeshStandardMaterial({ color: 0xe0c27a, roughness: 0.28, metalness: 0.85, emissive: new THREE.Color(0.25, 0.2, 0.08), emissiveIntensity: 0.4 }),
    cypress: new THREE.MeshStandardMaterial({ color: 0x48503f, roughness: 0.95 }),
    olive: new THREE.MeshStandardMaterial({ color: 0x7c8574, roughness: 0.95 }),
    ivy: new THREE.MeshStandardMaterial({ color: 0x3c4634, roughness: 0.95, side: THREE.DoubleSide }),
    bark: new THREE.MeshStandardMaterial({ color: 0x5c534a, roughness: 0.95 }),
    papyrus: new THREE.MeshStandardMaterial({ color: 0xe9e1cf, roughness: 0.8, side: THREE.DoubleSide }),
    // 点缀色：红绘陶器的赤陶红（设计稿 8 节）。只给"轮到你了"的东西用，并且要配合 userData.accent 才会在调色里留色。
    accent: new THREE.MeshStandardMaterial({ color: 0xb9572f, roughness: 0.5 }),
  };
}
