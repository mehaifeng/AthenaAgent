// 手艺（第三轮需求 4，2026-10-13，用户：增加一些简单的手工产业动画、使用工具的动画，让街道上的人不再全部无所事事）。纯函数，Node 单测 crafts.test.mjs。
// 居民的职业是 C# 已有的（PolisTalk.PersonaFor 的职业那一列，citizens 推来 trade）——他说话时是陶工，他干活时就拉坯：名字、长相、说话、干活对得上。
//
// 规则（每一条都有断言）：
//   1. 十四种职业归成十种活（TRADE_CRAFT）；每种活有它的家什（props，人物 spec 里的小件，干活时姿势点名才显示）、要不要工位（station，城里的东西）、
//      在哪儿干（site：'home' 自家门前；或者一处公共地方——渔夫在港口、葡萄农与养蜂人在橄榄林、乐师在广场、香料商在市集；
//      那处走不到的，拿在手里就能干的活（portable：纺线、研磨、缝鞋、弹琴）回自家门前干；钓鱼要水、锄地要土，走不到就不干，
//      citizens.mjs 规则 7）。
//   2. 家什都不是十位职业居民的招牌（citizens.RESIDENT_SIGNATURES）：居民远看靠招牌认人，市民拿着锤子就成了铁匠。
//      石匠用两手的石斧琢石（工匠是凿子加木槌），木匠、造船匠用锯（铁匠是锤），乐师抱里拉琴。
//   3. 职业的键与 C# 的 PolisTalk.TradeKeys 同一个次序；citizens 里没带职业时（夹具、演示）按种子算（tradeOf，与 C# 的 TradeIndex 同一个式子）。
//   4. 门前干活时人侧对着街（沿着墙站），工位在他面前 STATION_GAPS[工位] 米——镜头从南边看过去，看得见他的手和工位，不是他的后背。
//      姿势是拿身高 1.75 的参照人解的（poses.CRAFT_ARMS）：工位按这个人的身高缩放（连离他的距离一起乘 身高 / 1.75），接触点就对得上。

export const TRADE_KEYS = Object.freeze([
  'potter', 'weaver', 'fisher', 'baker', 'vineGrower', 'cobbler', 'mason', 'boatwright', 'herbalist', 'shepherd', 'musician', 'spiceTrader',
  'carpenter', 'beekeeper',
]);

/** 规则 1：职业 → 活。 */
export const TRADE_CRAFT = Object.freeze({
  potter: 'wheel', weaver: 'spin', shepherd: 'spin', fisher: 'fish', baker: 'knead', vineGrower: 'hoe', beekeeper: 'hoe',
  herbalist: 'grind', spiceTrader: 'grind', cobbler: 'stitch', mason: 'dress', boatwright: 'saw', carpenter: 'saw', musician: 'lyre',
});

/** 规则 1：每种活。variant 是 poses.work 的样子；props 是手里的家什（rig 的小件）；station 是城里的工位（stations.mjs 的样子）。 */
export const CRAFTS = Object.freeze({
  wheel: Object.freeze({ variant: 'wheel', props: Object.freeze([]), station: 'wheel', site: 'home' }),
  spin: Object.freeze({ variant: 'spin', props: Object.freeze(['distaff', 'spindle']), station: null, site: 'home', portable: true }),
  fish: Object.freeze({ variant: 'fish', props: Object.freeze(['fishingRod']), station: null, site: 'harbor', needsSpot: true }),
  knead: Object.freeze({ variant: 'knead', props: Object.freeze([]), station: 'trough', site: 'home' }),
  hoe: Object.freeze({ variant: 'hoe', props: Object.freeze(['hoe']), station: null, site: 'grove:0' }),
  grind: Object.freeze({ variant: 'grind', props: Object.freeze(['mortar', 'pestle']), station: null, site: 'home', portable: true }),
  stitch: Object.freeze({ variant: 'stitch', props: Object.freeze(['sandal', 'awl']), station: null, site: 'home', portable: true }),
  dress: Object.freeze({ variant: 'dress', props: Object.freeze(['stoneAxe']), station: 'block', site: 'home' }),
  saw: Object.freeze({ variant: 'saw', props: Object.freeze(['saw']), station: 'sawhorse', site: 'home' }),
  lyre: Object.freeze({ variant: 'lyre', props: Object.freeze(['lyre']), station: null, site: 'agora', portable: true }),
});

/** 有的职业在别处干同一种活：香料商在市集研磨（药草师在自家门前）。 */
const TRADE_SITE = Object.freeze({ spiceTrader: 'market' });

/** 工位的正中在人面前多远（米，参照人的；按身高缩放）：揉面台、陶轮、锯木架贴着人站，石料要留出斧子落下去的那一段。 */
export const STATION_GAPS = Object.freeze({ wheel: 0.42, trough: 0.42, block: 0.58, sawhorse: 0.42 });

/** 解姿势用的参照人的身高（rig 的缩放 = 身高 / 1.75）。 */
export const CRAFT_REFERENCE_HEIGHT = 1.75;

/** 工位的半宽（沿它自己的 x，人的左右）与半深（沿 z，人面朝的方向），米——stations.mjs 的实际大小（crafts.test 量过）。 */
export const STATION_EXTENT = Object.freeze({
  wheel: Object.freeze([0.3, 0.3]), trough: Object.freeze([0.47, 0.25]), block: Object.freeze([0.4, 0.38]), sawhorse: Object.freeze([0.76, 0.22]),
});

/** 规则 3：种子 → 职业（与 C# 的 PolisTalk.TradeIndex 同一个式子）。 */
export function tradeOf(seed) {
  const s = seed >>> 0;
  return TRADE_KEYS[(((s >>> 3) ^ (Math.imul(s, 2654435761) >>> 6)) >>> 0) % TRADE_KEYS.length];
}

/** 一位居民干什么活：{ trade, craft, variant, props, station, site, needsSpot, portable }；职业不认识的按种子算。 */
export function craftFor(trade, seed) {
  const t = TRADE_KEYS.includes(trade) ? trade : tradeOf(seed);
  const key = TRADE_CRAFT[t];
  const c = CRAFTS[key];
  return { trade: t, craft: key, variant: c.variant, props: c.props, station: c.station, site: TRADE_SITE[t] ?? c.site, needsSpot: !!c.needsSpot, portable: !!c.portable };
}

/** 一处公共地方的站点名：广场按种子挑一圈里的一个点。 */
export function craftSite(craft, seed) {
  if (craft.site === 'agora') return `agora:${(seed >>> 0) % 6}`;
  return craft.site;
}
