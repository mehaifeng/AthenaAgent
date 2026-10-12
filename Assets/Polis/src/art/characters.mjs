// 人物表：雅典娜、你（玩家）、十一位居民（与 C# 的 PolisResidentKind 一一对应，名字是 camelCase）。
// 黑白的画面里人和人靠剪影与明暗分开，所以每位居民都有一样远看认得出的东西：帽子（尖毡帽、宽檐帽、面纱、头巾）、
// 衣长（长衣 / 只遮一边肩的短衣）、手里的长家什（权杖、测杆、锤子）。雅典娜最亮、周身有光（第 8 节"光属于雅典娜"）；
// 你是一位长发的年轻女子，穿深蓝的希顿，没有光。半身像（busts.mjs）用同一套设定。
import * as THREE from '../../vendor/three.bundle.mjs';
import { buildFigure } from './rig.mjs';

/** 居民的名字（camelCase），与 C# 一致。 */
export const RESIDENTS = Object.freeze([
  'surveyor', 'scribe', 'accountant', 'smith', 'sailor', 'librarian', 'maiden', 'herald', 'painter', 'artisan', 'priestess',
]);

/**
 * 每位居民做事的样子（poses.mjs 的 work 变体）与他待的地方（公共地块的名字 + 相对那块地的落脚点）。
 * station：有工位的居民站在工位上、面朝活儿（world.mjs 的 stationFor）——手上的活儿（锤落在砧面上、凿刃抵着石面、笔尖落在画面上）
 * 是按工位的站法解出来的，所以他们不出门闲逛。
 */
export const RESIDENT_ROLES = Object.freeze({
  surveyor: { work: 'survey', site: 'agora', spot: [3.2, 3.6], facing: -0.6 },
  scribe: { work: 'write', site: 'archive', spot: [-3.2, 3.4], facing: 0.3 },
  accountant: { work: 'count', site: 'vault', spot: [3.0, 3.4], facing: -0.4 },
  smith: { work: 'forge', site: 'forge', spot: [1.6, 1.6], facing: -1.2, station: { in: 'forge', key: 'forge' } },   // 隔着铁砧面朝南（M3 第三轮：第一版背对铁砧朝空气抡锤）
  sailor: { work: 'haul', site: 'harbor', spot: [0.98, 19], facing: Math.PI / 2, station: { in: 'harbor', key: 'haul' } },   // 栈桥东沿、面朝泊着的船收缆（第一版站在港前广场上，缆绳拴在空气里）
  librarian: { work: 'shelve', site: 'library', spot: [2.2, 6.1], facing: 0.2 },   // 圆形图书馆最下一级台阶（半径 6.0、高 0.25）外面半米：第一版离圆心 6.02，脚陷在台阶边上
  maiden: { work: 'read', site: 'temple', spot: [-3.4, 6.2], facing: 0.5 },
  herald: { work: 'announce', site: 'agora', spot: [-4.4, -2.05], facing: 0.6, fixed: true },   // 站在讲台上（你站不上去，M3）
  painter: { work: 'paint', site: 'paintedStoa', spot: [-3.2, 0.3], facing: 0.4 + Math.PI, station: { in: 'paintedStoa', key: 'paint' } },   // 西边那架画架前
  artisan: { work: 'carve', site: 'forge', spot: [-2.4, 2.4], facing: 0.8, station: { in: 'forge', key: 'carve' } },   // 锻炉里那块石料前
  priestess: { work: 'tend', site: 'temple', spot: [2.0, 6.6], facing: -0.3 },
});

/**
 * 人物参数（rig.buildFigure 吃的那一份）。cloth 是材质的名字或衣料深浅的档位，建骨架时换成材质。
 * carry：走和停的时候拿在手里的家什（poses.mjs 的 CARRY_GRIPS）——远看认得出是谁，不只在做事的那几秒。
 * face（M3 近景）：age / jaw / nose / female，和半身像（busts.mjs 的 CHARACTERS）是同一个人的同一张脸。
 */
export const CHARACTER_SPECS = Object.freeze({
  // 雅典娜做事的样子按工具类别（workVariantFor）：读、写、锻、召唤、收缆（取网页）——每样手里都有对应的东西，收缆也有一根缆。
  // M3：她走路（3.2 米 / 秒、步幅 1.6），不再滑行。bearing：抬着下巴、挺着胸（rig.mjs）
  athena: { height: 1.9, bearing: { head: -0.03, spine: -0.035 }, garment: 'peplos', cloth: 'cloth', hair: 'athena', headgear: 'fillet', glow: true, sandals: 'tyrrhenian', stride: 1.6, face: { female: true, age: 'young', jaw: 0.3, nose: 0.95 }, props: ['scrollOpen', 'tablet', 'stylus', 'hammer', 'gift', 'sealed', 'scroll', 'rope'] },
  // 你：一位年轻女子，中分的长发披到背上（弹簧骨），女式的希顿（2026-10-12：不再戴兜帽、穿斜披的外衣）
  player: { height: 1.68, garment: 'chiton', cloth: 'playerCloth', hair: 'player', build: 0.94, face: { female: true, age: 'young', jaw: 0.28, nose: 0.92 }, props: [] },
  surveyor: { height: 1.76, garment: 'himation', cloth: 2, hair: 'surveyor', beard: 'short', headgear: 'petasos', face: { age: 'adult', jaw: 0.55, nose: 1.08 }, props: ['rod'], carry: { side: 'R', grip: 'staff', prop: 'rod' } },
  scribe: { height: 1.7, garment: 'chiton', cloth: 1, hair: 'scribe', headgear: 'headband', face: { age: 'young', jaw: 0.4, nose: 0.95 }, props: ['tablet', 'stylus', 'gift'], carry: { side: 'L', grip: 'hug', prop: 'tablet' } },
  accountant: { height: 1.68, garment: 'himation', cloth: 2, hair: 'accountant', beard: 'short', hairMat: 'greyHair', face: { age: 'old', jaw: 0.5, nose: 1.05 }, props: ['abacus'], carry: { side: 'L', grip: 'hug', prop: 'abacus' } },
  smith: { height: 1.74, garment: 'exomis', cloth: 4, hair: 'smith', beard: 'full', headgear: 'pilos', build: 1.12, apron: true, face: { age: 'adult', jaw: 0.8, nose: 1.12 }, props: ['hammer', 'tongs'] },
  sailor: { height: 1.72, garment: 'exomis', cloth: 3, hair: 'sailor', beard: 'stubble', headgear: 'pilosTilted', face: { age: 'adult', jaw: 0.6, nose: 1.0 }, props: ['rope'] },
  librarian: { height: 1.66, garment: 'himation', cloth: 0, hair: 'librarian', beard: 'long', hairMat: 'greyHair', face: { age: 'old', jaw: 0.45, nose: 1.1 }, props: ['scroll'], carry: { side: 'R', grip: 'hug', prop: 'scroll' } },
  maiden: { height: 1.55, garment: 'peplos', cloth: 'maiden', hair: 'maiden', headgear: 'diadem', gold: true, faceless: true, props: ['scrollOpen'] },
  herald: { height: 1.78, garment: 'chlamys', cloth: 2, hair: 'herald', beard: 'short', headgear: 'petasosBack', face: { age: 'adult', jaw: 0.55, nose: 1.0 }, props: ['staff'], carry: { side: 'R', grip: 'staff', prop: 'staff' } },
  painter: { height: 1.65, garment: 'chiton', cloth: 1, hair: 'painter', headgear: 'sakkos', face: { female: true, age: 'young', jaw: 0.3, nose: 0.9 }, props: ['brush', 'palette'], carry: { side: 'L', grip: 'hug', prop: 'palette' } },
  artisan: { height: 1.73, garment: 'exomis', cloth: 3, hair: 'artisan', beard: 'full', headgear: 'headband', build: 1.08, apron: true, face: { age: 'adult', jaw: 0.7, nose: 1.05 }, props: ['chisel', 'mallet'] },
  priestess: { height: 1.72, garment: 'peplos', cloth: 0, hair: 'priestess', headgear: 'veil', face: { female: true, age: 'adult', jaw: 0.3, nose: 0.95 }, props: ['phiale'] },
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

/**
 * 建一位居民（M-B：一场会话一位，长相由种子定，见 art/citizens.mjs 的 citizenLook）。和 buildCharacter 一样把衣色档位、
 * 发色的名字换成材质；不走资产清单（居民没有 Id，不能换成外部模型）。
 */
export function buildCitizen(M, lookSpec) {
  const spec = { ...lookSpec };
  spec.cloth = typeof spec.cloth === 'number' ? M.clothShades[spec.cloth] : M[spec.cloth];
  if (typeof spec.hairMat === 'string') spec.hairMat = M[spec.hairMat];
  if (spec.beard && !spec.beardMat && spec.hairMat) spec.beardMat = spec.hairMat;
  return buildFigure(M, spec);
}
