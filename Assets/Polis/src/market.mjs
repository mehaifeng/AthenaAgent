// 市集（2026-10-15，用户：市集要用起来——扩大规模、有职业特点的摊位，对应职业的人在特定时段到摊前叫卖，别的人在市集里闲逛、
// 在摊前驻足、成交，并且有符合情景的回应）。纯逻辑（不碰 DOM、不碰 three.js），Node 单测 market.test.mjs。
// 摊位的样子在 art/civic.mjs 的 buildMarket（它读这里的 STALLS / STALL），谁在摊前、什么时候、说什么由 game.mjs 按这里的规则摆。
//
// 规则（每一条都有断言）：
//   1. 市集占满它那块地（账本里保留的 (-1, 1)，地块永不挪位，所以"扩大"只能在这块地里：原来 12 米见方只有两排 6 个摊）：
//      十个摊——北边一排四个朝南、东西各两个朝里、南边两个朝街（镜头从南边看过来，叫卖的人正对着镜头）；中间是空场，
//      南边两摊之间是入口。每个摊：四根柱子撑着只遮后半个摊的布篷（柜台和货露在外面，镜头看得见），前面一张柜台（只占摊位左边那一截），
//      右边留一道口子，摊主从口子走进柜台后面，买主站在柜台前。柜台、柱子挡路，布篷从头顶过去（footprint 的身高带）。
//   2. 每个摊卖一样东西（STALL_KINDS）：面包、陶器、布、凉鞋、面粉、木器归做这门手艺的人；鱼、果蔬、油酒、杂货归商贩——
//      文件夹合并成的摊位（家在市集的那几位）先占商贩的摊，其余按 id 的次序给街上做这门手艺的人；没人的摊盖着布。
//      种地、打鱼、放羊的人不来摆摊（他们的活在田里、港口、羊圈，C# 刚给他们排好了工位）。
//   3. 市集有开市的时段（墙钟上每 cycleMs 开 openMs）：摊主的周期就是这个钟（vendorTiming），开市时陆续到（各自错开不超过
//      arriveSpreadMs），收市时走；没开市、天黑了、摊主不在，货都盖着布。其余时间摊主在自家门前干他自己的活（陶工拉坯）。
//   4. 逛市集的人（三成的人按种子是去买东西的：isShopper——这是一趟差事，多远都去；其余的人闲逛挑到市集的也算）挑一两个有摊主的摊，每个摊前待 stopMs：先看货、问价、摊主报价，然后按种子
//      成交 / 讲价后成交 / 讲价不成走开 / 看看就走（shopScript）；成交是一次手递手——他递一枚钱、摊主递过一包货——之后他抱着那包货，
//      一直到走回家。一个摊同一时刻只招呼一位（先到的），后到的在旁边看货、不说话。
//   5. 说的话是本地台词（按摊的种类、按种子挑；两位居民之间由模型说的闲聊照样会挑中摊主和买主，那时候让给模型，
//      提示词里写着他在摆摊 / 在逛市集，C# 的 PolisTalk.DoingKeys 'sell' / 'shop'）。

/** 市集的钟和招呼的节奏（毫秒、米）。 */
export const MARKET = Object.freeze({
  cycleMs: 600_000,        // 每十分钟一市
  openMs: 420_000,         // 开七分钟，收三分钟
  arriveSpreadMs: 24_000,  // 摊主错开着到（开市后 2 秒到 24 秒之间）
  packMs: 8_000,           // 收市前这么久收摊走人
  shopperShare: 0.3,       // 三成的人闲逛时是去市集买东西（按种子，不管远近——这是一趟差事，不是随便走走）
  stopMs: 20_000,          // 在一个摊前待多久（看货、问价、成交、道谢都在里面）
  tradeMs: 3600,           // 一次手递手
  passMs: 1600,            // 摆摆手走开
  lineMs: 2400,            // 一句话在头顶留多久（之后淡出）
  talkRadius: 16,          // 离你这么近以内的才说出声（远处的照样在做，只是不冒字）
  hawkRadius: 14,
  hawkEveryMs: 15_000,     // 没人招呼时，摊主隔这么久吆喝一声（再按种子错开 0–6 秒）
  hawkMax: 2,              // 全城同时最多几位在吆喝（满街冒字就成了噪音）
});

/** 规则 2：摊的种类，按招人的先后排：做这门手艺的人先挑（trades），商贩的摊再挑（merchant），文件夹的摊主先占商贩的摊。 */
export const STALL_KINDS = Object.freeze([
  Object.freeze({ kind: 'bread', trades: Object.freeze(['baker']) }),
  Object.freeze({ kind: 'pottery', trades: Object.freeze(['potter']) }),
  Object.freeze({ kind: 'cloth', trades: Object.freeze(['weaver', 'spinner']) }),
  Object.freeze({ kind: 'sandals', trades: Object.freeze(['cobbler']) }),
  Object.freeze({ kind: 'grain', trades: Object.freeze(['miller']) }),
  Object.freeze({ kind: 'wood', trades: Object.freeze(['carpenter']) }),
  Object.freeze({ kind: 'goods', trades: Object.freeze(['merchant']), folder: true }),
  Object.freeze({ kind: 'oil', trades: Object.freeze(['merchant']), folder: true }),
  Object.freeze({ kind: 'produce', trades: Object.freeze(['merchant']), folder: true }),
  Object.freeze({ kind: 'fish', trades: Object.freeze(['merchant']), folder: true }),
]);

/** 不来摆摊的职业：活在城外或水边（规则 2）。 */
export const NEVER_VENDORS = Object.freeze(['farmer', 'herder', 'fisher', 'idle']);

/**
 * 一个摊在它自己的坐标里（+z 朝买主）：柱子、柜台、布篷、摊主站的地方、柜台右边那道口子、买主站的两处。米。
 * 柜台只占左边那一截（x0..x1），右边是摊主进出的口子。
 */
export const STALL = Object.freeze({
  halfW: 1.25, halfD: 1.0,
  // 柱子：后面两根高、中间两根矮，布篷只遮后半个摊（摊主头顶）——从 33° 的镜头看下来，柜台和货不能被篷子盖住
  // （第一版前后四根柱、篷子盖满整个摊，近看只看见一块块米色的顶）
  posts: Object.freeze([[-1.2, -0.95], [1.2, -0.95], [-1.2, 0.05], [1.2, 0.05]]),
  postH: 2.25,
  awning: Object.freeze({ z0: -1.1, z1: 0.3, back: 2.5, front: 2.15 }),
  counter: Object.freeze({ x0: -1.2, x1: 0.15, z0: 0.3, z1: 0.7, h: 0.92 }),
  vendor: Object.freeze([-0.5, -0.12]),
  gapX: 0.66,
  front: 1.32,
  // 买主站的两处：离前柱至少 0.5 米（柱子按人的半径膨胀、再按 0.1 米的栅格取整，0.47 米的那一处站不上去）。
  // 摊主和买主都贴着柜台站（隔着 1.37 米）：再远，两人伸直了胳膊也递不到对方手里（market.test 量手递手的那一下）
  customers: Object.freeze([Object.freeze([-0.75, 1.25]), Object.freeze([-0.2, 1.25])]),
});

/**
 * 规则 1：十个摊在市集里（市集的坐标：原点在地块中心，+z 朝南、朝街；yaw 是摊的 +z 转到哪）。
 * side：'in' 朝着中间的空场，'street' 朝着南边的街。
 */
export const STALLS = Object.freeze([
  // 南边两个朝街：面包、果蔬（叫卖声对着街上过路的人）
  { kind: 'bread', x: -3.6, z: 4.9, yaw: 0, side: 'street' },
  { kind: 'produce', x: 3.6, z: 4.9, yaw: 0, side: 'street' },
  // 北边一排朝南
  { kind: 'pottery', x: -4.35, z: -4.9, yaw: 0, side: 'in' },
  { kind: 'cloth', x: -1.45, z: -4.9, yaw: 0, side: 'in' },
  { kind: 'oil', x: 1.45, z: -4.9, yaw: 0, side: 'in' },
  { kind: 'goods', x: 4.35, z: -4.9, yaw: 0, side: 'in' },
  // 西边两个朝东、东边两个朝西
  { kind: 'fish', x: -5.0, z: -1.2, yaw: Math.PI / 2, side: 'in' },
  { kind: 'grain', x: -5.0, z: 1.7, yaw: Math.PI / 2, side: 'in' },
  { kind: 'sandals', x: 5.0, z: -1.2, yaw: -Math.PI / 2, side: 'in' },
  { kind: 'wood', x: 5.0, z: 1.7, yaw: -Math.PI / 2, side: 'in' },
].map((s, index) => Object.freeze({ ...s, index })));

/**
 * 市集里走的几个点（市集坐标）：街上的站点（layout 的 market 站点，地块南沿外 0.8 米）、入口、空场中间；inner 是空场
 * （四面摊前那一圈往里收一点的矩形）——空场里没有东西挡路，两点都在里面就直走；摊前的点先走到它在空场里最近的那一点
 * （aisle），再横过去（从空场中间斜着直奔角上的摊，会擦过侧面那个摊的前柱和柜台）。
 */
export const MARKET_WAY = Object.freeze({
  street: Object.freeze([0, 7.05]), entrance: Object.freeze([0, 5.0]), hub: Object.freeze([0, 2.2]),
  inner: Object.freeze({ minX: -3.3, maxX: 3.3, minZ: -3.1, maxZ: 3.1 }),
});

/** 摊前的一点在空场里最近的那一点（市集坐标）。 */
export function aisleOf([x, z]) {
  const r = MARKET_WAY.inner;
  return [Math.min(r.maxX, Math.max(r.minX, x)), Math.min(r.maxZ, Math.max(r.minZ, z))];
}

/** 摊的坐标 → 市集坐标（three.js 绕 y 轴转 yaw：x' = x cos + z sin，z' = −x sin + z cos）。 */
export function stallToMarket(stall, sx, sz) {
  const c = Math.cos(stall.yaw), s = Math.sin(stall.yaw);
  return [stall.x + sx * c + sz * s, stall.z - sx * s + sz * c];
}

/**
 * 市集里各摊的点，换到世界坐标（市集的地块中心 cx, cz、朝向 yaw——建筑一律朝南，yaw 是 0，这里照样按它转）。
 * 每个摊：vendor { x, z, yaw }（面朝买主）、customers [{ x, z, yaw }]（面朝摊）、via（从街上的站点走到摊主站的地方要经过的点，
 * 不含站点本身和终点）、shopVia（走到买主站的地方要经过的点）。
 */
export function marketGeometry(cx, cz, yaw = 0) {
  const c = Math.cos(yaw), s = Math.sin(yaw);
  const W = ([x, z]) => ({ x: cx + x * c + z * s, z: cz - x * s + z * c });
  const inner = [MARKET_WAY.entrance, MARKET_WAY.hub];
  return STALLS.map((st) => {
    const front = stallToMarket(st, STALL.gapX, STALL.front);
    const behind = stallToMarket(st, STALL.gapX, STALL.vendor[1]);
    const v = stallToMarket(st, ...STALL.vendor);
    const toward = st.yaw + yaw;   // 摊的 +z 在世界里朝哪（市集整体转了 yaw，摊在市集里又转了 st.yaw）
    const wayIn = (p) => (st.side === 'in' ? [...inner, aisleOf(p)] : []);
    return {
      index: st.index, kind: st.kind, side: st.side,
      vendor: { ...W(v), yaw: toward },
      customers: STALL.customers.map((p) => {
        const at = stallToMarket(st, ...p);
        return { ...W(at), yaw: toward + Math.PI, via: wayIn(at).map(W), aisle: st.side === 'in' ? W(aisleOf(at)) : null };
      }),
      via: [...wayIn(front), front, behind].map(W),
    };
  });
}

/**
 * 两个买主站的地方之间怎么走（世界坐标的折线，含两头）。from / to：{ stall, spot }（摊号、第几处）。都朝着空场的：先回到各自在空场里的
 * 那一点、空场里直走；都朝街的：沿着街直走；一里一外：经过空场中间、入口和街上的站点。
 */
export function shopLeg(geo, from, to, cx, cz, yaw = 0) {
  const c = Math.cos(yaw), s = Math.sin(yaw);
  const W = ([x, z]) => ({ x: cx + x * c + z * s, z: cz - x * s + z * c });
  const A = geo[from.stall], B = geo[to.stall];
  const a = A.customers[from.spot], b = B.customers[to.spot];
  const pt = (p) => ({ x: p.x, z: p.z });
  if (A.side === 'street' && B.side === 'street') return [pt(a), pt(b)];
  if (A.side === 'in' && B.side === 'in') return [pt(a), a.aisle, b.aisle, pt(b)];
  const way = [MARKET_WAY.hub, MARKET_WAY.entrance, MARKET_WAY.street].map(W);
  return A.side === 'in' ? [pt(a), a.aisle, ...way, pt(b)] : [pt(a), ...way.reverse(), b.aisle, pt(b)];
}

/** 规则 3：墙钟这一刻开不开市（不管天色——天黑时 game 另外让它收着）。 */
export function marketOpen(wall) {
  const p = ((wall % MARKET.cycleMs) + MARKET.cycleMs) % MARKET.cycleMs;
  return p < MARKET.openMs;
}

/**
 * 规则 2：谁看哪个摊。people：[{ id, trade, stallFolder（文件夹摊位的序号，不是摊主为 null）, nightOwl }]（街上的人）。
 * 返回 { byStall: Map 摊号 → id, byId: Map id → 摊号 }。只看整份名单，按 id 的次序，同一份名单永远同一个结果。
 * 夜游的人不摆摊（摊在天黑时收着）。
 */
export function assignVendors(people) {
  const byStall = new Map(), byId = new Map();
  const list = [...people].filter((p) => p && !p.nightOwl && !NEVER_VENDORS.includes(p.trade))
    .sort((a, b) => (a.id < b.id ? -1 : a.id > b.id ? 1 : 0));
  const take = (stall, person) => { byStall.set(stall.index, person.id); byId.set(person.id, stall.index); };
  const order = STALL_KINDS.map((k) => STALLS.find((s) => s.kind === k.kind));
  // 文件夹合并成的摊位的摊主先占商贩的摊（按文件夹的次序）
  const folderStalls = order.filter((s) => STALL_KINDS.find((k) => k.kind === s.kind).folder);
  const keepers = list.filter((p) => p.stallFolder != null).sort((a, b) => a.stallFolder - b.stallFolder);
  keepers.forEach((p, k) => { if (k < folderStalls.length) take(folderStalls[k], p); });
  for (const stall of order) {
    if (byStall.has(stall.index)) continue;
    const kind = STALL_KINDS.find((k) => k.kind === stall.kind);
    for (const trade of kind.trades) {
      const who = list.find((p) => p.trade === trade && !byId.has(p.id) && p.stallFolder == null);
      if (who) { take(stall, who); break; }
    }
  }
  return { byStall, byId };
}

/**
 * 规则 3：摊主的日程——周期就是市集的钟，开市时（错开 jitter）到摊前，收市时走（路远的晚到一点，一来一回之外至少留
 * homeMs + tailMs 在家）。返回 { periodMs, stayMs, offset }，与 citizens.planTiming 的字段一样；offset 让他到摊前的那一刻落在
 * 开市后 jitter 毫秒（墙钟上每一市都一样）。
 */
export function vendorTiming(walkMs, seed, { homeMs, tailMs }) {
  const P = MARKET.cycleMs;
  const jitter = 2_000 + ((seed >>> 3) % 1000) / 1000 * (MARKET.arriveSpreadMs - 2_000);
  const room = P - homeMs - tailMs - 2 * walkMs;   // 一个周期里能在摊前待多久
  // 收市前 packMs 收摊走人（不是钟一响就凭空没了货）
  const stayMs = Math.max(5_000, Math.min(MARKET.openMs - MARKET.packMs - jitter, room));
  const arrive = homeMs + walkMs;
  const offset = (((arrive - jitter) % P) + P) % P;
  return { periodMs: P, stayMs, offset: Math.floor(offset) };
}

/** 规则 4：这个人闲逛时是不是去市集买东西（按种子；只看种子，不动日程里别的随机数）。 */
export function isShopper(seed) {
  return mix(seed, 0x6d61726b) % 1000 < MARKET.shopperShare * 1000;
}

/** 种子 → 一个整数哈希（同一个人在同一个摊前的那一次永远同一个结局）。 */
function mix(a, b) {
  let h = (Math.imul(a >>> 0, 0x9e3779b1) ^ Math.imul((b >>> 0) + 0x7f4a7c15, 0x85ebca6b)) >>> 0;
  h ^= h >>> 15; h = Math.imul(h, 0x2c1b3c6d) >>> 0; h ^= h >>> 12;
  return h >>> 0;
}

/** 规则 4：这一次在摊前的结局：成交 45 %、讲价后成交 25 %、讲价不成 10 %、看看就走 20 %。 */
export function shopOutcome(seed, visit) {
  const u = mix(seed, visit) % 100;
  return u < 45 ? 'buy' : u < 70 ? 'haggle' : u < 80 ? 'refused' : 'pass';
}

/**
 * 规则 4：在一个摊前过了 t 毫秒，买主和摊主各在做什么、说什么。返回
 * { step, customer: { action, variant?, t, speaker?, line? }, vendor: { action, variant?, t, speaker?, line? } | null（不招呼他）,
 *   bought（钱货已经两清）, engaged（摊主在招呼他） }。line 是台词表的键（game 按种子挑一句）。
 * 时间线：0–5 秒看货（摊主在头 0.8–3.2 秒招呼一句）；5–7.5 问价；7.5–10 报价；之后按结局——
 *   buy：10–13.6 手递手，13.6 起摊主道谢、14.2 起他回一句；
 *   haggle：10–12.5 讲价，12.5–15 摊主让一步，15–18.6 手递手，18.6 起道谢；
 *   refused：10–12.5 讲价，12.5–15 摊主不让，15 起他摆摆手走开；
 *   pass：10 起摆摆手（"再转转"）。
 */
export function shopScript(t, outcome) {
  const T = MARKET.tradeMs;
  const browse = { action: 'work', variant: 'browse', t };
  const talk = (speaker, from, line = null) => ({ action: 'talk', speaker, t: t - from, line });
  const S = (step, customer, vendor, extra = {}) => ({ step, customer, vendor, bought: false, engaged: !!vendor, ...extra });
  if (t < 0) return S('none', { action: 'idle', t: 0 }, null);
  if (t < 5000) {
    return S('browse', browse, { action: 'work', variant: 'offer', t, line: t >= 800 && t < 3200 ? 'marketPitch' : null, lineAt: 800 });
  }
  if (t < 7500) return S('ask', { ...talk(true, 5000, 'marketAsk'), lineAt: 5000 }, talk(false, 5000));
  if (t < 10000) return S('price', talk(false, 7500), { ...talk(true, 7500, 'marketPrice'), lineAt: 7500 });
  const trade = (from) => {
    const k = t - from;
    if (k < T) return S('trade', { action: 'work', variant: 'buy', t: k }, { action: 'work', variant: 'sell', t: k });
    return null;
  };
  const after = (from, bought) => {
    // 道谢：摊主先说，他接一句；之后他站着再看一会儿（买了的抱着那包货）
    const k = t - from;
    const vendor = k < MARKET.lineMs ? { ...talk(true, from, 'marketThanks'), lineAt: from } : null;
    const customer = k >= 600 && k < 600 + MARKET.lineMs ? { ...talk(true, from + 600, 'marketBought'), lineAt: from + 600 } : { action: 'idle', t };
    return S('after', customer, vendor, { bought });
  };
  const leave = (from) => {
    const k = t - from;
    const customer = k < MARKET.passMs ? { action: 'work', variant: 'pass', t: k, line: k < MARKET.lineMs ? 'marketPass' : null, lineAt: from } : { action: 'work', variant: 'browse', t };
    return S(k < MARKET.passMs ? 'pass' : 'after', customer, null);
  };
  switch (outcome) {
    case 'buy': return trade(10000) ?? after(10000 + T, true);
    case 'haggle':
      if (t < 12500) return S('haggle', { ...talk(true, 10000, 'marketHaggle'), lineAt: 10000 }, talk(false, 10000));
      if (t < 15000) return S('concede', talk(false, 12500), { ...talk(true, 12500, 'marketConcede'), lineAt: 12500 });
      return trade(15000) ?? after(15000 + T, true);
    case 'refused':
      if (t < 12500) return S('haggle', { ...talk(true, 10000, 'marketHaggle'), lineAt: 10000 }, talk(false, 10000));
      if (t < 15000) return S('refuse', talk(false, 12500), { ...talk(true, 12500, 'marketRefuse'), lineAt: 12500 });
      return leave(15000);
    default: return leave(10000);
  }
}

/** 摊主隔一阵吆喝一声：此刻在不在吆喝、这一声是第几声（挑哪一句）。没人招呼时才喊。 */
export function hawkCall(wall, seed) {
  const every = MARKET.hawkEveryMs + ((seed >>> 5) % 7) * 1000;
  const k = Math.floor((wall + (seed % every)) / every);
  const local = wall + (seed % every) - k * every;
  return { calling: local < MARKET.lineMs, n: k, since: local };
}

/** 台词表里挑一句（"|" 分隔）：按种子和第几次挑，同一个人同一次永远同一句。 */
export function marketLine(words, key, seed, n = 0) {
  const list = String(words?.[key] ?? '').split('|').map((s) => s.trim()).filter(Boolean);
  if (list.length === 0) return '';
  return list[mix(seed, n) % list.length];
}

/** 摊的种类 → 吆喝的那张台词表的键。 */
export const hawkKey = (kind) => `marketHawk${kind[0].toUpperCase()}${kind.slice(1)}`;
