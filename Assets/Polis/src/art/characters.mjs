// 人物表：雅典娜、你（玩家）、十一位居民（与 C# 的 PolisResidentKind 一一对应，名字是 camelCase）。
// 黑白的画面里人和人靠剪影与明暗分开，所以每位居民都有一样远看认得出的东西：帽子（尖毡帽、宽檐帽、面纱、头巾）、
// 衣长（长衣 / 只遮一边肩的短衣）、手里的长家什（权杖、测杆、锤子）。雅典娜最亮、周身有光（第 8 节"光属于雅典娜"）；
// 你穿深色兜帽斗篷，没有光。半身像（busts.mjs）用同一套设定。
import * as THREE from '../../vendor/three.bundle.mjs';
import { buildFigure } from './rig.mjs';

/** 居民的名字（camelCase），与 C# 一致。 */
export const RESIDENTS = Object.freeze([
  'surveyor', 'scribe', 'accountant', 'smith', 'sailor', 'librarian', 'maiden', 'herald', 'painter', 'artisan', 'priestess',
]);

/** 每位居民做事的样子（poses.mjs 的 work 变体）与他待的地方（公共地块的名字 + 相对那块地的落脚点）。 */
export const RESIDENT_ROLES = Object.freeze({
  surveyor: { work: 'survey', site: 'agora', spot: [3.2, 3.6], facing: -0.6 },
  scribe: { work: 'write', site: 'archive', spot: [-3.2, 3.4], facing: 0.3 },
  accountant: { work: 'count', site: 'vault', spot: [3.0, 3.4], facing: -0.4 },
  smith: { work: 'hammer', site: 'forge', spot: [1.6, 1.6], facing: -1.2 },
  sailor: { work: 'haul', site: 'harbor', spot: [-2.4, 2.0], facing: 0.4 },
  librarian: { work: 'shelve', site: 'library', spot: [2.2, 5.6], facing: 0.2 },
  maiden: { work: 'read', site: 'temple', spot: [-3.4, 6.2], facing: 0.5 },
  herald: { work: 'announce', site: 'agora', spot: [-4.4, -2.05], facing: 0.6 },
  painter: { work: 'paint', site: 'paintedStoa', spot: [-3.6, 3.3], facing: 0.9 },
  artisan: { work: 'carve', site: 'forge', spot: [-2.4, 2.4], facing: 0.8 },
  priestess: { work: 'tend', site: 'temple', spot: [2.0, 6.6], facing: -0.3 },
});

/**
 * 人物参数（rig.buildFigure 吃的那一份）。cloth 是材质的名字或衣料深浅的档位，建骨架时换成材质。
 * carry：走和停的时候拿在手里的家什（poses.mjs 的 CARRY_GRIPS）——远看认得出是谁，不只在做事的那几秒。
 */
export const CHARACTER_SPECS = Object.freeze({
  // 雅典娜做事的样子按工具类别（workVariantFor）：读、写、锻、召唤、收缆（取网页）——每样手里都有对应的东西，收缆也有一根缆
  athena: { height: 1.9, garment: 'peplos', cloth: 'cloth', hair: 'bun', headgear: 'fillet', glow: true, glides: true, props: ['scrollOpen', 'tablet', 'stylus', 'hammer', 'gift', 'sealed', 'scroll', 'rope'] },
  player: { height: 1.75, garment: 'himation', cloth: 'playerCloth', hair: 'hidden', headgear: 'hood', props: [] },
  surveyor: { height: 1.76, garment: 'himation', cloth: 2, hair: 'short', beard: 'short', headgear: 'petasos', props: ['rod'], carry: { side: 'R', grip: 'staff', prop: 'rod' } },
  scribe: { height: 1.7, garment: 'chiton', cloth: 1, hair: 'short', headgear: 'headband', props: ['tablet', 'stylus', 'gift'], carry: { side: 'L', grip: 'hug', prop: 'tablet' } },
  accountant: { height: 1.68, garment: 'himation', cloth: 2, hair: 'receding', beard: 'short', hairMat: 'greyHair', props: ['abacus'], carry: { side: 'L', grip: 'hug', prop: 'abacus' } },
  smith: { height: 1.74, garment: 'exomis', cloth: 4, hair: 'short', beard: 'full', headgear: 'pilos', build: 1.12, apron: true, props: ['hammer'] },
  sailor: { height: 1.72, garment: 'exomis', cloth: 3, hair: 'short', beard: 'stubble', headgear: 'pilosTilted', props: ['rope'] },
  librarian: { height: 1.66, garment: 'himation', cloth: 0, hair: 'receding', beard: 'long', hairMat: 'greyHair', props: ['scroll'], carry: { side: 'R', grip: 'hug', prop: 'scroll' } },
  maiden: { height: 1.55, garment: 'peplos', cloth: 'maiden', hair: 'braids', headgear: 'diadem', gold: true, faceless: true, props: ['scrollOpen'] },
  herald: { height: 1.78, garment: 'chlamys', cloth: 2, hair: 'short', beard: 'short', headgear: 'petasosBack', props: ['staff'], carry: { side: 'R', grip: 'staff', prop: 'staff' } },
  painter: { height: 1.65, garment: 'chiton', cloth: 1, hair: 'bun', headgear: 'sakkos', props: ['brush', 'palette'], carry: { side: 'L', grip: 'hug', prop: 'palette' } },
  artisan: { height: 1.73, garment: 'exomis', cloth: 3, hair: 'short', beard: 'full', headgear: 'headband', build: 1.08, apron: true, props: ['chisel', 'mallet'] },
  priestess: { height: 1.72, garment: 'peplos', cloth: 0, hair: 'bun', headgear: 'veil', props: ['phiale'] },
});

export const CHARACTER_IDS = Object.freeze(Object.keys(CHARACTER_SPECS));

/** 做事时的样子：居民按行当；雅典娜按工具类别（game.mjs 传进来）。 */
export function workVariantFor(id, category = null) {
  if (id === 'athena') {
    return { read: 'read', memory: 'read', write: 'write', terminal: 'hammer', workshop: 'hammer', subagents: 'summon', web: 'haul' }[category] ?? 'read';
  }
  if (id === 'player') return 'read';
  return RESIDENT_ROLES[id]?.work ?? 'read';
}

/** 建一个人物（材质名换成材质）。 */
export function buildCharacter(M, id, overrides = {}) {
  const base = CHARACTER_SPECS[id];
  if (!base) throw new Error(`characters: unknown character '${id}'`);
  const spec = { id, ...base, ...overrides };
  spec.cloth = typeof spec.cloth === 'number' ? M.clothShades[spec.cloth] : M[spec.cloth];
  if (typeof spec.hairMat === 'string') spec.hairMat = M[spec.hairMat];
  if (spec.beard && !spec.beardMat && spec.hairMat) spec.beardMat = spec.hairMat;
  const figure = buildFigure(M, spec);
  if (spec.glow) {
    // 她的光照在脚下的一圈地面上；身边的光晕在调色里加，只加在身体轮廓之外（打样）
    const light = new THREE.PointLight(0xfff6e8, 1.1, 4.5, 2);
    light.position.set(0, 0.45, 0.3);
    figure.root.add(light);
    figure.root.userData.light = light;
  }
  return figure;
}
