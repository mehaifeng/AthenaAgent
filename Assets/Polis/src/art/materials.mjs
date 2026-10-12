// 材质。世界是彩色的（2026-10-12 起，用户：世界上色；之前整个世界在后期里被调成黑白，只给点缀色留色）：
// 正午的地中海——暖白的大理石与粉墙、赭黄的干土、赤陶的瓦、银绿的橄榄、深绿的柏树、青绿到深蓝的海。
// 明暗关系仍按黑白时代调好的那一套（屋顶比铺地暗一档、雅典娜的白衣最亮），只是把色相找回来；
// 饱和度都收着一点，点缀色（"轮到你了"的赤陶红）在一座彩色的城里也还认得出。大理石、土、铺地、布料取自打样。
import * as THREE from '../../vendor/three.bundle.mjs';
import { valueNoise, fbm, field, textureFrom, tone, smooth, rng, normalFrom } from './noise.mjs';
import { makeWaterMaterial } from './water.mjs';
import { FACE_LOOKS, faceMaterialKey, faceTexture } from './face.mjs';

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
  // 干土是赭黄的，暗下去的地方（草簇）偏橄榄绿：夏天的阿提卡，地上一片片晒干的草
  const earthMap = textureFrom(512, earthField, (f) => {
    const v = 0.52 + (f - 0.45) * 0.26, grass = smooth(0.5, 0.3, f);
    return tone(v, [1.08 - 0.3 * grass, 0.94 - 0.04 * grass, 0.7 - 0.12 * grass]);
  }, { repeat: 40 });

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
  const paveMap = textureFrom(256, paveField, (f) => (f < 0 ? tone(0.5, [1, 0.93, 0.82]) : tone(0.64 + (f - 0.5) * 0.16, [1, 0.95, 0.86])));
  const paveBump = textureFrom(256, paveField, (f) => tone(f < 0 ? 0.2 : 0.55 + f * 0.45), { srgb: false });

  const clothField = field(128, (u, v) => fbm(n2, u * 16, v * 32, 16, 3));
  const clothMap = textureFrom(128, clothField, (f) => tone(0.90 + (f - 0.5) * 0.06, [1, 0.99, 0.965]), { repeat: 3 });
  // M3（决定 3、5）：写实靠光影和材质。布的织纹——经纬两向的细密起伏，近景里衣料读得出是织物，不是塑料壳
  const weaveField = field(128, (u, v) => 0.5 + 0.25 * Math.sin(u * Math.PI * 2 * 32) * Math.sin(v * Math.PI * 2 * 32) + 0.25 * (fbm(n4, u * 8, v * 8, 8, 3) - 0.5));
  const clothNormal = normalFrom(128, weaveField, 0.08, { repeat: 3 });
  // 皮肤：毛孔那一级的细噪声 + 稍大一级的起伏，粗糙度随之变化（T 区亮一点、脸颊哑一点）
  const skinField = field(256, (u, v) => 0.5 + 0.3 * (fbm(n5, u * 48, v * 48, 48, 2) - 0.5) + 0.2 * (fbm(n3, u * 6, v * 6, 6, 3) - 0.5));
  const skinNormal = normalFrom(256, skinField, 0.035, { repeat: 4 });
  const skinRough = textureFrom(256, skinField, (f) => tone(0.5 + f * 0.3), { srgb: false, repeat: 4 });
  const woven = (m) => { m.normalMap = clothNormal; m.normalScale = new THREE.Vector2(0.35, 0.35); return m; };

  // 粉墙：民居的白灰墙，有一点刷痕和污渍
  const plasterField = field(256, (u, v) => fbm(n3, u * 16, v * 16, 16, 4) * 0.7 + fbm(n5, u * 64, v * 8, 64, 2) * 0.3);
  const plasterMap = textureFrom(256, plasterField, (f) => tone(0.84 + (f - 0.5) * 0.12, [1, 0.975, 0.94]));

  // 海的细波纹：一张周期噪声的法线贴图，water.mjs 拿它按两个尺度、两个方向流动着采样
  const waveField = field(256, (u, v) => fbm(n2, u * 16, v * 16, 16, 4));
  const waveNormal = textureFrom(256, waveField, (f) => [128 + (f - 0.5) * 90, 128 + (f - 0.5) * 60, 255], { srgb: false, repeat: 30 });

  // 瓦：一垄一垄的筒瓦顺着坡往下，每隔一段一道搭接的台阶（M2 美术审查：素平的屋顶在俯视里占画面最大，像一块塑料板）。
  // 贴图的 u 是屋面的宽、v 是顺坡的方向（kit.box 的 UV 每 2 米一个周期）：一个周期里 8 垄、5 道搭接
  const tileField = field(256, (u, v) => {
    const ridge = 0.5 + 0.5 * Math.cos(u * Math.PI * 2 * 8);
    const course = smooth(0.0, 0.18, (v * 5) % 1);
    const grain = fbm(n3, u * 16, v * 16, 16, 3);
    return ridge * 0.7 + course * 0.2 + grain * 0.1;
  });
  const tileMap = textureFrom(256, tileField, (f) => tone(0.78 + f * 0.22));
  const tileBump = textureFrom(256, tileField, (f) => tone(f), { srgb: false });
  const tiled = (color) => new THREE.MeshStandardMaterial({ color, map: tileMap, bumpMap: tileBump, bumpScale: 1.6, roughness: 0.78 });

  const marble = new THREE.MeshStandardMaterial({ map: marbleMap, bumpMap: marbleBump, bumpScale: 0.35, roughness: 0.6 });
  const wall = marble.clone(); wall.color = new THREE.Color(0.9, 0.9, 0.9);
  const pave = new THREE.MeshStandardMaterial({ map: paveMap, bumpMap: paveBump, bumpScale: 0.6, roughness: 0.92 });
  // 街道与广场同一种石板，略灰一点；贴图的平铺靠几何体的 UV（每 4 米一块），不靠贴图的 repeat
  const street = pave.clone();
  street.color = new THREE.Color(0.93, 0.92, 0.9);
  // 同一类建筑之间的深浅变化（屋瓦、墙面各三档），按建筑名的哈希挑一档：稳定，不随加载变化
  // 瓦是赤陶的橙褐，比点缀色的赤陶红偏黄、偏暗；三档里一档旧一点（发灰）、一档新一点（更红）
  const tileShades = [0xa0664a, 0xb06f4c, 0xbd7a52].map((c) => tiled(new THREE.Color(c)));
  // 粉墙：白灰、一档带一点赭（旧墙）、一档偏冷的白（新刷的）
  const plasterShades = [[0.97, 0.93, 0.84], [1, 0.98, 0.95], [0.95, 0.97, 1.0]].map(([r, g, b]) => {
    const m = new THREE.MeshStandardMaterial({ map: plasterMap, roughness: 0.9 });
    m.color.setRGB(r, g, b);
    return m;
  });
  return {
    tileShades,
    plasterShades,
    marble,
    wall,
    plaster: new THREE.MeshStandardMaterial({ map: plasterMap, roughness: 0.9 }),
    // 俯视时屋顶占画面最大，得比铺地明显暗一档，城里的建筑才分得开
    roof: new THREE.MeshStandardMaterial({ color: 0x8e5e45, roughness: 0.8 }),
    tile: tiled(0xb06f4c),
    // 作坊木棚的苇顶：浅草色、顺坡的一道道苇秆（第一版用深木色的一块板，俯视是一个黑洞）
    reed: tiled(0xc1a66c),
    dark: new THREE.MeshStandardMaterial({ color: 0x1c1a18, roughness: 0.9 }),
    // 窗洞：白天和 dark 一样是黑的；夜里（深色主题）透出油灯的暖光（setNightWindows，2026-10-12 用户：夜里窗户要有灯火）
    windowLit: new THREE.MeshStandardMaterial({ color: 0x1c1a18, roughness: 0.9, emissive: 0xff8f33, emissiveIntensity: 0 }),
    shadowGap: new THREE.MeshStandardMaterial({ color: 0x2c2723, roughness: 1 }),
    wood: new THREE.MeshStandardMaterial({ color: 0x7d5a3a, roughness: 0.85 }),
    darkWood: new THREE.MeshStandardMaterial({ color: 0x51392a, roughness: 0.9 }),
    // 铜、金：城里的环境光只有 0.18，金属度高的材质几乎反射不到东西、看上去是黑的（M2 美术审查：三足鼎、火盆、
    // 金库的铜门都成了黑块，鼎的两只环耳像一张脸上的两只眼睛）。金属度压低、底色提亮，读得出是"一件金属器"
    bronze: new THREE.MeshStandardMaterial({ color: 0xa47a48, roughness: 0.48, metalness: 0.35 }),
    stone: new THREE.MeshStandardMaterial({ color: 0xa69c88, roughness: 0.95 }),
    earth: new THREE.MeshStandardMaterial({ map: earthMap, roughness: 1 }),
    // 地图边缘的切面（world.mjs）：陆地下面是一层深赭的土，海下面是深海的蓝——整张图读起来是一块切出来的沙盘
    plinthEarth: new THREE.MeshStandardMaterial({ color: 0x6f5a43, roughness: 1 }),
    plinthSea: new THREE.MeshStandardMaterial({ color: 0x24506a, roughness: 1 }),
    pave,
    street,
    // 海：会动的水（water.mjs）——涌浪、流动的细波纹、离岸由青绿到深蓝、岸线与栈桥和船边的白沫、斜看映天
    water: makeWaterMaterial(waveNormal),
    cloth: woven(new THREE.MeshStandardMaterial({
      map: clothMap, roughness: 0.86, side: THREE.DoubleSide,
      emissive: new THREE.Color(1, 0.98, 0.94), emissiveIntensity: 0.025,
    })),
    awning: new THREE.MeshStandardMaterial({ map: clothMap, color: 0xd9c8a2, roughness: 0.9, side: THREE.DoubleSide }),
    // 你的斗篷：靛蓝（黑白时代是偏深的灰——仍比雅典娜的白衣暗得多）
    playerCloth: woven(new THREE.MeshStandardMaterial({ map: clothMap, color: 0x4f6690, roughness: 0.9, side: THREE.DoubleSide })),
    // 皮肤（决定 5：写实）：晒过的暖色，毛孔的法线和粗糙度的变化让近景里的脸和手是皮肤，不是蜡。
    // 毛孔从 0.45 降到 0.2（2026-10-12）：画上细致的五官以后，0.45 的毛孔在近景里是一层橘皮，脸反而显得粗
    skin: new THREE.MeshStandardMaterial({ color: 0xcfa384, roughness: 0.58, normalMap: skinNormal, normalScale: new THREE.Vector2(0.2, 0.2), roughnessMap: skinRough }),
    // 五官（2026-10-12）：画在透明画布上的眼、眉、唇（face.mjs），像蒙版一样贴在雕好的脸上，一种长相一份。
    // 不写深度、多边形偏移往前——贴图那一片离皮肤只有 0.3 毫米
    ...Object.fromEntries(Object.keys(FACE_LOOKS).map((look) => [faceMaterialKey(look), new THREE.MeshStandardMaterial({
      map: faceTexture(look), transparent: true, premultipliedAlpha: true, depthWrite: false, roughness: 0.5,
      polygonOffset: true, polygonOffsetFactor: -2, polygonOffsetUnits: -2,
    })])),
    // 头发：M2 美术审查时近乎纯黑、还带高光，近看像一顶漆皮帽；提一档、去掉光泽
    hair: new THREE.MeshStandardMaterial({ color: 0x4a3426, roughness: 0.86 }),
    gold: new THREE.MeshStandardMaterial({ color: 0xd8c08a, roughness: 0.34, metalness: 0.4 }),
    maiden: new THREE.MeshStandardMaterial({ color: 0xe0c27a, roughness: 0.28, metalness: 0.85, emissive: new THREE.Color(0.25, 0.2, 0.08), emissiveIntensity: 0.4 }),
    cypress: new THREE.MeshStandardMaterial({ color: 0x34502f, roughness: 0.95 }),
    olive: new THREE.MeshStandardMaterial({ color: 0x7f8f62, roughness: 0.95 }),
    // 常春藤：第一版近黑，墙上的叶团读成一个个窟窿；提一档
    ivy: new THREE.MeshStandardMaterial({ color: 0x45663a, roughness: 0.95, side: THREE.DoubleSide }),
    bark: new THREE.MeshStandardMaterial({ color: 0x5f4b3a, roughness: 0.95 }),
    papyrus: new THREE.MeshStandardMaterial({ color: 0xe9e1cf, roughness: 0.8, side: THREE.DoubleSide }),
    // 点缀色：红绘陶器的赤陶红（设计稿 8 节）。只给"轮到你了"的东西用；userData.accent 标记它（黑白调色 ?grade=1 下只有它留色）。
    accent: new THREE.MeshStandardMaterial({ color: 0xb9572f, roughness: 0.5 }),
    // M2：器物的陶土与黑釉。陶土是偏黄的橙、黑釉近黑，瓶身上的纹饰带读得出来；
    // 陶土与点缀色不是同一个颜色（更黄、更浅），也从不挂 userData.accent。
    terracotta: new THREE.MeshStandardMaterial({ color: 0xbf8150, roughness: 0.7 }),
    glaze: new THREE.MeshStandardMaterial({ color: 0x1f1b18, roughness: 0.32, metalness: 0.05 }),
    laurel: new THREE.MeshStandardMaterial({ color: 0x4d6a3c, roughness: 0.92 }),
    // 城邦金库的灰石：比大理石暗两档，比土亮一档——方屋在一片白里一眼认得出
    vaultStone: new THREE.MeshStandardMaterial({ color: 0x8a8781, roughness: 0.95 }),
    // 居民衣料的五档：植物染的颜色（本色羊毛的赭、菘蓝的灰蓝、胡桃的褐、橄榄绿、靛蓝），明度仍是黑白时代那五档
    // （0.76 → 0.37）：雅典娜最亮，人和人去了色也分得开。都不用红——红只给"轮到你了"。
    // M2 美术审查：第一版最浅的两档（0.95、0.8）在正午的高调光下和雅典娜的白衣分不开——光属于她，最浅一档也得比她暗一截
    clothShades: [[0.86, 0.74, 0.55], [0.55, 0.66, 0.74], [0.62, 0.5, 0.4], [0.44, 0.5, 0.33], [0.3, 0.36, 0.52]].map(([r, g, b]) => {
      const m = woven(new THREE.MeshStandardMaterial({ map: clothMap, roughness: 0.88, side: THREE.DoubleSide }));
      m.color.setRGB(r, g, b);
      return m;
    }),
    leather: new THREE.MeshStandardMaterial({ color: 0x5a3a26, roughness: 0.8 }),
    // 围裙的鞣皮：比靴子和帽子的皮浅两档。第一版用同一种深皮，挂在胸前是一块纯黑的板
    hide: new THREE.MeshStandardMaterial({ color: 0x9a7354, roughness: 0.84, side: THREE.DoubleSide }),
    greyHair: new THREE.MeshStandardMaterial({ color: 0xb8b2aa, roughness: 0.7 }),
  };
}

/** 夜里窗洞的灯火有多亮（emissiveIntensity）：k = 1 是深夜。色调映射之后读得出是一盏暖黄的油灯，又不会亮过雅典娜的光。 */
export const WINDOW_GLOW = 1.5;

/** 夜色 k（0 白天 → 1 夜里）换成窗洞的灯火：所有窗共用一份材质，一次设好。 */
export function setNightWindows(M, k) {
  M.windowLit.emissiveIntensity = WINDOW_GLOW * Math.min(1, Math.max(0, k));
}

