// 居民的长相（M-B，2026-10-13）：每场会话一位居民，他的长相由 C# 给的种子决定——同一场会话永远是同一个人，一座城里撞不了脸。
// 纯函数（Node 单测 citizens.test.mjs）：citizenLook(seed, { age, trade }) 给出 rig.buildFigure 吃的那一份参数，
// 和 busts.mjs 画半身像用的那一份——同一个人的两种画法。
//
// 规则（每一条都有断言）：
//   1. 男女由种子的最低位定（seed & 1 为 1 是女子），和 C# 的 PolisCitizens.IsFemale 一致——名字和长相对得上。
//   2. 只用"市民的词汇"：衣式、五档衣色、通用发式（rig.HAIR_STYLES 的 plain*）、头带或不戴、胡子、身高、体格、脸型。
//      十一位居民的招牌一样不用——宽檐帽、毡帽、头冠、面纱、发网、雅典娜的金发带、斗篷（chlamys），以及他们手里的家什：
//      远看认人靠帽子、衣长和手里的长家什（characters.mjs 文件头），市民戴上测绘员的帽子，测绘员就认不出来了。
//   3. 走着、站着时手里只拿一卷素卷轴或空手；干活时拿他那门手艺的家什（art/crafts.mjs，第三轮需求 4）——家什也不是居民的招牌；
//      红色只属于雅典娜（"轮到你了"）。
//   4. 同一个种子永远得到同一个长相；任意连续 32 个种子，(衣式, 衣色, 头饰, 胡子, 发式) 至少 24 种不同。
import { rng } from './noise.mjs';
import { craftFor } from './crafts.mjs';

/** 市民的词汇（规则 2）。 */
export const CITIZEN_VOCAB = Object.freeze({
  female: Object.freeze({ garments: ['peplos', 'chiton'], hair: ['plainLong', 'plainBun', 'plainCurly'], headgear: [null, 'headband'] }),
  male: Object.freeze({ garments: ['chiton', 'himation', 'exomis'], hair: ['plainShort', 'plainCurly'], headgear: [null, 'headband'], beards: [null, 'stubble', 'short', 'full'] }),
  oldMaleHair: 'plainBald',
  cloth: Object.freeze([0, 1, 2, 3, 4]),
});

/** 居民的招牌（规则 2）：市民身上一样都不能有。 */
export const RESIDENT_SIGNATURES = Object.freeze({
  headgear: Object.freeze(['petasos', 'petasosBack', 'pilos', 'pilosTilted', 'diadem', 'veil', 'sakkos', 'fillet']),
  garments: Object.freeze(['chlamys']),
  props: Object.freeze(['rod', 'staff', 'tablet', 'stylus', 'abacus', 'palette', 'brush', 'hammer', 'tongs', 'chisel', 'mallet', 'rope', 'phiale', 'scrollOpen', 'gift', 'sealed']),
});

/** 发式 → 半身像的发式（busts.mjs 认的那几种）。 */
const BUST_HAIR = Object.freeze({ plainShort: 'short', plainCurly: 'curly', plainBald: 'receding', plainLong: 'long', plainBun: 'bun' });

/** 衣色档位 → 半身像衣服的深浅（busts.mjs 的 wash：越大越深；居民的五档从浅到深）。 */
const BUST_WASH = Object.freeze([0.12, 0.2, 0.28, 0.36, 0.44]);

const pick = (r, list) => list[Math.floor(r() * list.length) % list.length];
const between = (r, lo, hi) => lo + (hi - lo) * r();
const round = (v, k = 1000) => Math.round(v * k) / k;

/** 种子的最低位：1 是女子（规则 1，与 C# 一致）。 */
export const isFemale = (seed) => ((seed >>> 0) & 1) === 1;

/**
 * 一位居民的长相。
 * @param {number} seed C# 给的种子（会话 Id 的哈希，32 位无符号）
 * @param {{ age?: 'young'|'adult'|'old', trade?: string|null }} [options] 年纪按会话开始了多久（C# 定）；职业是 C# 的 PolisTalk.TradeKeys 之一，
 *   没带就按种子算（art/crafts.mjs 的 tradeOf，与 C# 同一个式子）
 * @returns {{ spec: object, bust: object, key: string }} spec 给 rig.buildFigure（cloth 是衣色档位，建骨架时换成材质）；
 *   bust 给 busts.drawBust（带 key，缓存用）
 */
export function citizenLook(seed, { age = 'adult', trade = null } = {}) {
  const s = seed >>> 0;
  const female = isFemale(s);
  const r = rng(s ^ 0x5bd1e995);
  const vocab = female ? CITIZEN_VOCAB.female : CITIZEN_VOCAB.male;
  const garment = pick(r, vocab.garments);
  const cloth = pick(r, CITIZEN_VOCAB.cloth);
  const headgear = pick(r, vocab.headgear);
  let hair = pick(r, vocab.hair);
  let beard = female ? null : pick(r, CITIZEN_VOCAB.male.beards);
  const old = age === 'old';
  if (!female && old && r() < 0.6) hair = CITIZEN_VOCAB.oldMaleHair;
  if (!female && old && beard == null) beard = 'short';
  if (!female && age === 'young' && beard === 'full') beard = 'stubble';
  const height = round(female ? between(r, 1.56, 1.72) : between(r, 1.66, 1.82));
  const build = round(female ? between(r, 0.9, 1.02) : between(r, 0.92, 1.12));
  const jaw = round(female ? between(r, 0.25, 0.42) : between(r, 0.45, 0.8));
  const nose = round(between(r, 0.9, 1.12));
  const carriesScroll = r() < 0.25;
  const key = `c${s}`;
  const spec = {
    id: `citizen:${s}`,
    height,
    garment,
    cloth,
    hair,
    ...(headgear ? { headgear } : {}),
    ...(beard ? { beard } : {}),
    ...(old ? { hairMat: 'greyHair' } : {}),
    build,
    face: { female, age, jaw, nose },
    // 卷轴（走路时夹着）与他那门手艺的家什（干活时姿势点名才显示，第三轮需求 4）
    props: [...(carriesScroll ? ['scroll'] : []), ...craftFor(trade, s).props],
    // 卷轴夹在右臂里（图书管理员也是这么拿的，那一种握法对过真骨架）
    ...(carriesScroll ? { carry: { side: 'R', grip: 'hug', prop: 'scroll' } } : {}),
    seed: s % 9973,
  };
  const bust = {
    key,
    hair: BUST_HAIR[hair],
    headgear: headgear ?? null,
    garment,
    beard,
    attribute: null,
    age,
    wash: BUST_WASH[cloth],
    jaw,
    female,
  };
  return { spec, bust, key };
}

/** 一个长相的特征元组（规则 4 的"撞脸"按它算）。 */
export const lookSignature = ({ spec }) => [spec.garment, spec.cloth, spec.headgear ?? '-', spec.beard ?? '-', spec.hair].join('/');
