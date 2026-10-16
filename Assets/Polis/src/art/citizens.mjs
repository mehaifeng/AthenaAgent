// 居民的长相（M-B，2026-10-13）：每场会话一位居民，他的长相由 C# 给的种子决定——同一场会话永远是同一个人，一座城里撞不了脸。
// 纯函数（Node 单测 citizens.test.mjs）：citizenLook(seed, { age, trade }) 给出 rig.buildFigure 吃的那一份参数，
// 和 busts.mjs 画半身像用的那一份——同一个人的两种画法。
//
// 规则（每一条都有断言）：
//   1. 男女由种子的最低位定（seed & 1 为 1 是女子），和 C# 的 PolisCitizens.IsFemale 一致——名字和长相对得上。
//   2. 只用"市民的词汇"：衣式与衣长、十二档衣色、外衣 / 腰带 / 头带的第二种颜色、通用发式（rig.HAIR_STYLES 的 plain*）与五种发色、
//      头带 / 橄榄枝花环或不戴、胡子、身高、体格、肚腩、仪态、脸型。
//      十一位居民的招牌一样不用——宽檐帽、毡帽、头冠、面纱、发网、雅典娜的金发带、斗篷（chlamys），以及他们手里的家什：
//      远看认人靠帽子、衣长和手里的长家什（characters.mjs 文件头），市民戴上测绘员的帽子，测绘员就认不出来了。
//   3. 走着、站着时手里只拿一卷素卷轴或空手；干活时拿他那门手艺的家什（art/crafts.mjs，第三轮需求 4）——家什也不是居民的招牌；
//      红色只属于雅典娜（"轮到你了"）。
//   4. 同一个种子永远得到同一个长相；任意连续 32 个种子，(衣式, 衣长, 衣色, 第二色, 头饰, 胡子, 发式, 发色) 至少 30 种不同。
//   5. 穿什么跟着职业走（2026-10-16，用户：扩充服装、发型、身形的多样性）：下地、出海、干粗活的男子多穿露一肩的短衣（exomis）或
//      到膝的短希顿，商贩、乐师、无业的人多披外衣（himation）、穿长希顿；干活的女子有的把希顿穿到小腿。半身像和城里的人物
//      用同一个职业算（world.mjs、game.mjs 都把 trade 传进来），对话面板里的那个人和街上的是同一身衣服。
//   6. 身形：身高是两次抽样的平均（中间多、两头少），男 1.58–1.90、女 1.50–1.76，年轻人矮一点点；体格（宽窄）男 0.88–1.16、
//      女 0.86–1.06；成年人和老人里约三成有肚腩（belly 0.35–1，上衣在腰带前面鼓出来，腰带跟着绕过去）；老人弯腰低头。
import { rng } from './noise.mjs';
import { craftFor } from './crafts.mjs';

/** 市集上买卖用的两样（rig 的小件）：每位居民都带着，姿势点名才显示。 */
export const MARKET_PROPS = Object.freeze(['coin', 'parcel']);

/**
 * 衣料的十二档颜色（RGB，materials.mjs 的 clothShades 按它建）：前五档是原来的（本色羊毛的赭、菘蓝的灰蓝、胡桃的褐、橄榄绿、靛蓝），
 * 后七档 2026-10-16 加的（藏红花的黄、灰绿、石板灰、骨螺的暗紫、栗褐、青绿、本色麻）。都不用红；最亮的两档（0.75）仍比雅典娜的白衣暗。
 */
export const CLOTH_SHADES_RGB = Object.freeze([
  [0.86, 0.74, 0.55], [0.55, 0.66, 0.74], [0.62, 0.5, 0.4], [0.44, 0.5, 0.33], [0.3, 0.36, 0.52],
  [0.8, 0.62, 0.3], [0.6, 0.64, 0.52], [0.44, 0.45, 0.47], [0.44, 0.32, 0.44], [0.48, 0.34, 0.25], [0.3, 0.5, 0.5], [0.78, 0.75, 0.66],
].map((c) => Object.freeze(c)));

/** 市民的词汇（规则 2）。衣式按职业加权（规则 5），见 GARMENT_WEIGHTS。 */
export const CITIZEN_VOCAB = Object.freeze({
  female: Object.freeze({
    garments: ['peplos', 'chiton'],
    hair: ['plainLong', 'plainBun', 'plainCurly', 'plainHighBun', 'plainLowBun', 'plainBraidCrown'],
    headgear: [null, null, null, 'headband', 'headband', 'wreath'],
  }),
  male: Object.freeze({
    garments: ['chiton', 'himation', 'exomis'],
    hair: ['plainShort', 'plainCurly', 'plainCrop', 'plainWavy', 'plainReceding'],
    headgear: [null, null, null, 'headband', 'headband', 'wreath'],
    beards: [null, 'stubble', 'short', 'full'],
  }),
  oldMaleHair: 'plainBald',
  oldBeards: Object.freeze(['short', 'full', 'long']),
  cloth: Object.freeze(CLOTH_SHADES_RGB.map((_, i) => i)),
  /** 发色（materials.mjs 的 hairShades）：乌黑、深褐、褐、栗色、浅褐——深的多、浅的少。 */
  hairTones: Object.freeze([0, 0, 1, 1, 2, 2, 3, 4]),
  /** 衣长（身体单位的下摆离地高度）：短希顿到膝上，干活女子的希顿到小腿。 */
  hems: Object.freeze({ short: 0.5, calf: 0.2 }),
});

/** 粗活的职业（规则 5）：下地、放牧、打鱼、石匠、木匠、磨坊、陶工、鞋匠。 */
export const WORK_TRADES = Object.freeze(['farmer', 'herder', 'fisher', 'mason', 'carpenter', 'miller', 'potter', 'cobbler']);
/** 男子的衣式按职业的权重（规则 5）：[衣式, 衣长, 权重]。衣长 null 是那种衣服本来的长度。 */
const GARMENT_WEIGHTS = Object.freeze({
  work: [['exomis', null, 45], ['chiton', 'short', 35], ['himation', null, 8], ['chiton', null, 12]],
  town: [['chiton', null, 30], ['himation', null, 38], ['chiton', 'short', 22], ['exomis', null, 10]],
});

/** 居民的招牌（规则 2）：市民身上一样都不能有。 */
export const RESIDENT_SIGNATURES = Object.freeze({
  headgear: Object.freeze(['petasos', 'petasosBack', 'pilos', 'pilosTilted', 'diadem', 'veil', 'sakkos', 'fillet']),
  garments: Object.freeze(['chlamys']),
  props: Object.freeze(['rod', 'staff', 'tablet', 'stylus', 'abacus', 'palette', 'brush', 'hammer', 'tongs', 'chisel', 'mallet', 'rope', 'phiale', 'scrollOpen', 'gift', 'sealed']),
});

/** 发式 → 半身像的发式（busts.mjs 认的那几种）。 */
const BUST_HAIR = Object.freeze({
  plainShort: 'short', plainCurly: 'curly', plainBald: 'receding', plainLong: 'long', plainBun: 'bun',
  plainCrop: 'short', plainWavy: 'short', plainReceding: 'receding', plainHighBun: 'bun', plainLowBun: 'bun', plainBraidCrown: 'bun',
});

/** 衣色档位 → 半身像衣服的深浅（busts.mjs 的 wash：越大越深，按每一档的明度排）。 */
const BUST_WASH = Object.freeze([0.12, 0.2, 0.28, 0.36, 0.44, 0.21, 0.22, 0.36, 0.44, 0.43, 0.36, 0.12]);
/** 发色 → 半身像头发的墨色（铅笔画，只有深浅）；老人的花白、灰白另算。 */
const BUST_HAIR_INK = Object.freeze(['#2a2623', '#322d29', '#35312d', '#4a433d', '#6a625a']);

const pick = (r, list) => list[Math.floor(r() * list.length) % list.length];
const between = (r, lo, hi) => lo + (hi - lo) * r();
/** 两次抽样的平均：中间多、两头少（身高、体格）。 */
const bell = (r, lo, hi) => lo + (hi - lo) * (r() + r()) / 2;
function weighted(r, rows) {
  let x = r() * rows.reduce((n, row) => n + row[row.length - 1], 0);
  for (const row of rows) { x -= row[row.length - 1]; if (x < 0) return row; }
  return rows[rows.length - 1];
}
const round = (v, k = 1000) => Math.round(v * k) / k;

/** 种子的最低位：1 是女子（规则 1，与 C# 一致）。 */
export const isFemale = (seed) => ((seed >>> 0) & 1) === 1;

/**
 * 一位居民的长相。
 * @param {number} seed C# 给的种子（城的键、地块与序号的哈希，32 位无符号）
 * @param {{ age?: 'young'|'adult'|'old', trade?: string|null }} [options] 年纪由种子定（C# 推来）；职业是 C# 的 PolisTrades.Keys 之一，
 *   没带就按种子算（art/crafts.mjs 的 tradeOf，只是夹具的退路）
 * @returns {{ spec: object, bust: object, key: string }} spec 给 rig.buildFigure（cloth 是衣色档位，建骨架时换成材质）；
 *   bust 给 busts.drawBust（带 key，缓存用）
 */
export function citizenLook(seed, { age = 'adult', trade = null } = {}) {
  const s = seed >>> 0;
  const female = isFemale(s);
  const r = rng(s ^ 0x5bd1e995);
  const vocab = female ? CITIZEN_VOCAB.female : CITIZEN_VOCAB.male;
  const old = age === 'old', young = age === 'young';
  const craft = craftFor(trade, s);
  const work = WORK_TRADES.includes(craft.trade);
  // 规则 5：衣式与衣长跟着职业
  let garment, hem = null;
  if (female) {
    garment = pick(r, vocab.garments);
    if (garment === 'chiton' && r() < (work ? 0.45 : 0.12)) hem = CITIZEN_VOCAB.hems.calf;
  } else {
    const [g, h] = weighted(r, GARMENT_WEIGHTS[work ? 'work' : 'town']);
    garment = g;
    if (h) hem = CITIZEN_VOCAB.hems[h];
  }
  const cloth = pick(r, CITIZEN_VOCAB.cloth);
  // 第二种颜色：披外衣的人外衣一定换一种颜色（衣服和外衣一个颜色，远看是一整块），别人一半系一条异色的腰带
  let trim = null;
  if (garment === 'himation' || r() < 0.5) {
    trim = pick(r, CITIZEN_VOCAB.cloth);
    if (trim === cloth) trim = (trim + 1 + Math.floor(r() * (CITIZEN_VOCAB.cloth.length - 1))) % CITIZEN_VOCAB.cloth.length;
  }
  const headgear = pick(r, vocab.headgear);
  let hair = pick(r, vocab.hair);
  let beard = female ? null : pick(r, CITIZEN_VOCAB.male.beards);
  const hairTone = pick(r, CITIZEN_VOCAB.hairTones);
  // 老人：男子六成秃顶；胡子人人都有，有的留长；头发灰白，有的还是花白
  if (!female && old && r() < 0.6) hair = CITIZEN_VOCAB.oldMaleHair;
  if (!female && old) beard = beard === 'full' && r() < 0.5 ? 'long' : beard ?? pick(r, CITIZEN_VOCAB.oldBeards);
  if (!female && young && beard === 'full') beard = 'stubble';
  if (young && hair === 'plainReceding') hair = 'plainShort';
  const greyMat = old ? (r() < 0.35 ? 'pepperHair' : 'greyHair') : null;
  // 规则 6：身形
  const height = round((female ? bell(r, 1.5, 1.76) : bell(r, 1.58, 1.9)) - (young ? 0.02 : 0));
  const build = round(female ? bell(r, 0.86, 1.06) : bell(r, 0.88, 1.16));
  const belly = !young && r() < (female ? 0.22 : 0.34) ? round(between(r, 0.35, 1)) : 0;
  const bearing = old
    ? { head: round(between(r, 0.1, 0.16)), spine: round(between(r, 0.04, 0.08)) }
    : { head: round(between(r, 0.02, 0.1)), spine: round(between(r, -0.02, 0.025)) };
  const jaw = round(female ? between(r, 0.25, 0.42) : between(r, 0.45, 0.8));
  const nose = round(between(r, 0.9, 1.12));
  const carriesScroll = r() < 0.25;
  const key = `c${s}`;
  const spec = {
    id: `citizen:${s}`,
    height,
    garment,
    ...(hem != null ? { hem } : {}),
    cloth,
    ...(trim != null ? { trim } : {}),
    hair,
    hairTone,
    ...(headgear ? { headgear } : {}),
    ...(beard ? { beard } : {}),
    ...(greyMat ? { hairMat: greyMat } : {}),
    build,
    ...(belly ? { belly } : {}),
    bearing,
    face: { female, age, jaw, nose },
    // 卷轴（走路时夹着）与他那门手艺的家什（干活时姿势点名才显示，第三轮需求 4）
    // 市集（2026-10-15）：人人身上都有一枚钱和一包货（买卖时姿势点名才显示——递钱、接货、抱着走回家；摊主递过去的也是这一包）
    props: [...(carriesScroll ? ['scroll'] : []), ...craft.props, ...MARKET_PROPS],
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
    ...(old ? (greyMat === 'pepperHair' ? { hairInk: '#77706a' } : {}) : { hairInk: BUST_HAIR_INK[hairTone] }),
  };
  return { spec, bust, key };
}

/** 一个长相的特征元组（规则 4 的"撞脸"按它算）。 */
export const lookSignature = ({ spec }) => [spec.garment, spec.hem ?? '-', spec.cloth, spec.trim ?? '-', spec.headgear ?? '-', spec.beard ?? '-', spec.hair, spec.hairMat ?? spec.hairTone].join('/');
