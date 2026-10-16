// 市集（2026-10-15，用户：市集要用起来——扩大规模、有职业特点的摊位，对应职业的人在特定时段到摊前叫卖，别的人在市集闲逛、
// 在摊前驻足、成交，有符合情景的回应，加一些人物动作）。market.mjs 的五条规则、civic 的摊位、citizens 规则 9 的日程、
// poses 的五个新动作在真骨架上的手递手、台词与 C# 的 PolisLocale 对得上。
// 运行：node --test Tools/GameMode/web/test/
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import * as THREE from '../../../../Assets/Polis/vendor/three.bundle.mjs';
import {
  MARKET, STALLS, STALL, STALL_KINDS, NEVER_VENDORS, MARKET_WAY, marketGeometry, shopLeg, marketOpen, assignVendors, vendorTiming,
  shopOutcome, shopScript, hawkCall, marketLine, hawkKey, stallToMarket, isShopper,
} from '../../../../Assets/Polis/src/market.mjs';
import { planCitizen, citizenAt, routeClear, seeded, retireTime, duskHome, TOWN } from '../../../../Assets/Polis/src/citizens.mjs';
import { DOOR } from '../../../../Assets/Polis/src/doors.mjs';
import { createLayout, PLOT } from '../../../../Assets/Polis/src/layout.mjs';
import { createAssetLibrary } from '../../../../Assets/Polis/src/art/assets.mjs';
import { buildWorld } from '../../../../Assets/Polis/src/world.mjs';
import { footprintOf } from '../../../../Assets/Polis/src/footprint.mjs';
import { buildMarket } from '../../../../Assets/Polis/src/art/civic.mjs';
import { buildCitizen } from '../../../../Assets/Polis/src/art/characters.mjs';
import { citizenLook } from '../../../../Assets/Polis/src/art/citizens.mjs';
import { poseAt, WORK_VARIANTS, TRADE_MS, PASS_MS, LIMITS, JOINT_NAMES } from '../../../../Assets/Polis/src/art/poses.mjs';
import { TRADE_KEYS } from '../../../../Assets/Polis/src/art/crafts.mjs';
import { MARKET_ZH, CITIZEN_ZH } from '../../../../Assets/Polis/src/game.mjs';

const fixture = JSON.parse(readFileSync(new URL('../fixtures/synthetic.json', import.meta.url), 'utf8'));

function stubMaterials() {
  const cache = new Map();
  return new Proxy({}, {
    get: (_, key) => {
      if (typeof key !== 'string') return undefined;
      if (!cache.has(key)) {
        cache.set(key, key.endsWith('Shades')
          ? Array.from({ length: 12 }, (_, i) => new THREE.MeshStandardMaterial({ name: `${key}${i}` }))
          : new THREE.MeshStandardMaterial({ name: key }));
      }
      return cache.get(key);
    },
  });
}
const M = stubMaterials();

let cityCache = null;
function buildCity() {
  if (cityCache) return cityCache;
  const layout = createLayout(fixture);
  const world = buildWorld(new THREE.Scene(), fixture, layout, M, { assets: createAssetLibrary(M) });
  const plot = layout.plots.get('market');
  cityCache = { layout, world, plot, geo: marketGeometry(plot.cx, plot.cz, plot.yaw ?? 0) };
  return cityCache;
}
const hasSiteOf = (layout) => (q) => q === 'agora' || q.startsWith('agora:') || q === 'harbor' || q === 'gate' || (q === 'fields' && layout.plots.has('fields:0')) || layout.plots.has(q);

test('market rule 1: ten stalls fill the reserved plot — north row, two on each side facing in, two facing the street — with an open square and an entrance between the south two', () => {
  assert.equal(STALLS.length, 10, '十个摊（原来两排 6 个，只占了地块的一半）');
  assert.deepEqual(STALLS.map((s) => s.kind).sort(), STALL_KINDS.map((k) => k.kind).sort(), '每种摊各一个');
  const rects = STALLS.map((st) => {
    const pts = [[-STALL.halfW, -STALL.halfD], [STALL.halfW, -STALL.halfD], [-STALL.halfW, STALL.halfD], [STALL.halfW, STALL.halfD]].map(([x, z]) => stallToMarket(st, x, z));
    return { st, minX: Math.min(...pts.map((p) => p[0])), maxX: Math.max(...pts.map((p) => p[0])), minZ: Math.min(...pts.map((p) => p[1])), maxZ: Math.max(...pts.map((p) => p[1])) };
  });
  for (const r of rects) {
    assert.ok(r.minX >= -PLOT / 2 && r.maxX <= PLOT / 2 && r.minZ >= -PLOT / 2 && r.maxZ <= PLOT / 2, `${r.st.kind} 在市集那块地里`);
    for (const o of rects) {
      if (o === r) continue;
      const overlap = Math.min(r.maxX, o.maxX) - Math.max(r.minX, o.minX) > 0 && Math.min(r.maxZ, o.maxZ) - Math.max(r.minZ, o.minZ) > 0;
      assert.ok(!overlap, `${r.st.kind} 和 ${o.st.kind} 不叠`);
    }
  }
  assert.deepEqual(STALLS.filter((s) => s.side === 'street').map((s) => s.yaw), [0, 0], '朝街的两个摊正对着南边（镜头那一侧）');
  // 南边两摊之间是入口：入口那一点到两边的摊都至少 1.5 米
  const south = rects.filter((r) => r.st.side === 'street');
  assert.ok(south.every((r) => r.minX >= 1.5 || r.maxX <= -1.5), '入口两边各让出 1.5 米');
  assert.ok(Math.abs(MARKET_WAY.street[1] - (PLOT / 2 + 0.8)) < 1e-9, '街上的那一点就是 layout 的 market 站点');
});

test('market rule 1: in the built city every counter blocks, every vendor walks in through the gap to behind his counter, every customer spot is reachable and every leg between two stalls is clear', () => {
  const { layout, plot, geo } = buildCity();
  const S = layout.sitePoint('market');
  assert.ok(layout.walkable(S), '街上的站点能站');
  for (const g of geo) {
    const st = STALLS[g.index];
    const [cx, cz] = stallToMarket(st, (STALL.counter.x0 + STALL.counter.x1) / 2, (STALL.counter.z0 + STALL.counter.z1) / 2);
    assert.ok(!layout.walkable({ x: plot.cx + cx, z: plot.cz + cz }), `${g.kind}：柜台挡路`);
    assert.ok(routeClear([S, ...g.via, g.vendor], layout.walkable, 0.1), `${g.kind}：摊主从街上走进柜台后面，一路每 0.1 米都能站`);
    for (const [k, c] of g.customers.entries()) assert.ok(routeClear([S, ...c.via, c], layout.walkable, 0.1), `${g.kind}：买主站的第 ${k} 处走得到`);
    // 摊主与买主隔着柜台：面朝彼此，一个在柜台后面、一个在前面
    for (const c of g.customers) {
      const d = Math.hypot(c.x - g.vendor.x, c.z - g.vendor.z);
      assert.ok(d > 1.2 && d < 1.45, `${g.kind}：隔着柜台 ${d.toFixed(2)} 米——够近才递得到`);
      assert.ok(Math.abs(Math.cos(c.yaw - g.vendor.yaw) + 1) < 1e-9, `${g.kind}：两人面对面`);
    }
  }
  let legs = 0;
  for (let i = 0; i < geo.length; i++) for (let j = 0; j < geo.length; j++) for (const a of [0, 1]) for (const b of [0, 1]) {
    if (i === j && a === b) continue;
    const leg = shopLeg(geo, { stall: i, spot: a }, { stall: j, spot: b }, plot.cx, plot.cz, plot.yaw ?? 0);
    assert.ok(routeClear(leg, layout.walkable, 0.1), `${geo[i].kind}#${a} → ${geo[j].kind}#${b} 一路能站`);
    legs++;
  }
  assert.equal(legs, 380);
});

test('market rule 3: wares and the cover switch per stall, and switching them never changes where anyone can walk', () => {
  const market = buildMarket(M, 0);
  const stalls = market.userData.stalls;
  assert.equal(stalls.length, 10);
  const blocked = () => {
    const fp = footprintOf(market);
    let n = 0;
    for (let k = 0; k < fp.bits.length; k++) if (fp.bits[k]) n++;
    return `${fp.x0},${fp.z0},${fp.w},${fp.h},${n}`;
  };
  for (const st of stalls) { st.wares.visible = true; st.cover.visible = false; }
  const open = blocked();
  for (const st of stalls) { st.wares.visible = false; st.cover.visible = true; }
  const closed = blocked();
  assert.equal(open, closed, '摆货和盖布占的是同一块地（都在柜台的足迹里或者头顶上、地上矮于 0.35 米）');
  const { world } = buildCity();
  world.setStallOpen(3, true);
  assert.ok(world.market.userData.stalls[3].wares.visible && !world.market.userData.stalls[3].cover.visible);
  world.setStallOpen(3, false);
  assert.ok(!world.market.userData.stalls[3].wares.visible && world.market.userData.stalls[3].cover.visible);
  assert.ok(!world.stallsOpen.has(3));
  // 文件夹合并成的摊位：名牌挂在商贩的摊上
  assert.equal(market.userData.stallSpots.length, STALL_KINDS.filter((k) => k.folder).length);
});

test('market rule 2: a stall goes to someone of its own trade, folder stall keepers take the merchants\' stalls first, field, sea and pasture trades and night owls never keep a stall, and the result depends only on the list', () => {
  const people = [];
  let n = 0;
  for (const trade of TRADE_KEYS) for (let k = 0; k < 2; k++) people.push({ id: `n:${String(n++).padStart(3, '0')}`, trade, stallFolder: null, nightOwl: false });
  people.push({ id: 'n:900', trade: 'merchant', stallFolder: 1, nightOwl: false });
  people.push({ id: 'n:901', trade: 'merchant', stallFolder: 0, nightOwl: false });
  people.push({ id: 'n:000a', trade: 'baker', stallFolder: null, nightOwl: true });
  const { byStall, byId } = assignVendors(people);
  const kindOf = (id) => STALLS[byId.get(id)]?.kind;
  for (const [stall, id] of byStall) {
    const p = people.find((q) => q.id === id);
    const kind = STALL_KINDS.find((k) => k.kind === STALLS[stall].kind);
    assert.ok(kind.trades.includes(p.trade), `${STALLS[stall].kind} 归 ${p.trade}`);
  }
  assert.equal(kindOf('n:901'), 'goods', '文件夹摊位的摊主按文件夹的次序先占商贩的摊');
  assert.equal(kindOf('n:900'), 'oil');
  for (const p of people) {
    if (NEVER_VENDORS.includes(p.trade)) assert.ok(!byId.has(p.id), `${p.trade} 不来摆摊`);
  }
  assert.ok(!byId.has('n:000a'), '夜游的人不摆摊');
  assert.equal(byStall.size, 10, '这份名单里每个摊都有人');
  assert.equal(new Set(byStall.values()).size, byStall.size, '一人一个摊');
  const shuffled = [...people].sort(() => 0).reverse();
  assert.deepEqual([...assignVendors(shuffled).byStall].sort(), [...byStall].sort(), '只看名单，不看次序');
  // 没有对口的人：那个摊空着（盖着布），不抓别的职业的人凑数
  assert.equal(assignVendors([{ id: 'x', trade: 'potter' }]).byStall.size, 1);
  assert.equal(assignVendors([{ id: 'x', trade: 'farmer' }, { id: 'y', trade: 'idle' }]).byStall.size, 0);
});

test('market rule 3: the market opens openMs of every cycleMs; a vendor\'s day is the market\'s clock — he reaches his counter as it opens (staggered), hawks there with sell set, leaves as it closes, and never jumps', () => {
  const { layout, world, geo } = buildCity();
  assert.equal(marketOpen(0), true);
  assert.equal(marketOpen(MARKET.openMs - 1), true);
  assert.equal(marketOpen(MARKET.openMs), false);
  assert.equal(marketOpen(MARKET.cycleMs + 10), true);
  const hasSite = hasSiteOf(layout);
  const homes = [...layout.plots.keys()].filter((k) => k.startsWith('b:'));
  let checked = 0;
  for (const home of homes) {
    for (const [i, seed] of [7, 1234567, 99991].entries()) {
      const g = geo[(homes.indexOf(home) + i) % geo.length];
      const vendor = { stall: g.index, x: g.vendor.x, z: g.vendor.z, yaw: g.vendor.yaw, via: g.via };
      const p = planCitizen({ id: `v-${home}-${seed}`, seed, home, vendor, craft: null }, {
        sitePoint: layout.sitePoint, route: layout.route, walkable: layout.walkable, hasSite, neighbours: homes, doorFor: (q, sd) => world.doorFor(q, sd),
      });
      assert.equal(p.dest?.site, 'market', `${home}：摊主去市集`);
      assert.deepEqual(p.sell, { stall: g.index });
      assert.equal(p.periodMs, MARKET.cycleMs, '周期就是市集的钟');
      assert.ok(routeClear(p.out, layout.walkable, 0.1), `${home}：一路能站`);
      // 每一市：到柜台后面的那一刻在开市后 arriveSpreadMs 之内、在收市之前走
      for (const cycle of [0, 3]) {
        const t0 = cycle * MARKET.cycleMs;
        let arrive = null, leave = null;
        for (let t = t0; t < t0 + MARKET.cycleMs; t += 250) {
          const at = citizenAt(p, t);
          if (at.at === 'dest' && arrive == null) arrive = t;
          if (at.at === 'dest') leave = t;
          if (at.at === 'dest') {
            assert.equal(at.action, 'work'); assert.equal(at.work, 'hawk'); assert.deepEqual(at.sell, { stall: g.index });
            assert.ok(Math.hypot(at.x - g.vendor.x, at.z - g.vendor.z) < 1e-9, '站在柜台后面');
          }
        }
        assert.ok(arrive != null && arrive - t0 <= MARKET.arriveSpreadMs + 250, `${home}：开市后 ${arrive - t0} 毫秒到`);
        assert.ok(leave - t0 < MARKET.openMs, `${home}：收市前走`);
        assert.ok(leave - arrive > MARKET.openMs / 2, `${home}：守摊大半个开市时段（${Math.round((leave - arrive) / 1000)} 秒）`);
      }
      let prev = null;
      for (let t = -5000; t < MARKET.cycleMs + 5000; t += 250) {
        const at = citizenAt(p, t);
        if (!at.hidden && prev && !prev.hidden) assert.ok(Math.hypot(at.x - prev.x, at.z - prev.z) <= 1, `${home}：${t} 毫秒没有跳`);
        prev = at;
      }
      checked++;
    }
  }
  assert.equal(checked, homes.length * 3);
  assert.ok(checked >= 12);
  // vendorTiming：一来一回之外家里至少还有 homeMs + tailMs
  for (const w of [5_000, 40_000, 90_000]) {
    const v = vendorTiming(w, 12345, { homeMs: TOWN.homeMs, tailMs: TOWN.tailMs });
    assert.ok(TOWN.homeMs + 2 * w + v.stayMs + TOWN.tailMs <= v.periodMs + 1e-6, `一程 ${w}：一圈装得进市集的钟`);
  }
});

test('citizens rule 9: a shopper\'s stay is a tour — one or two staffed stalls, stopMs at each, walking between them, ending where he came in — the round trip fits his period and nothing jumps', () => {
  const { layout, world, plot, geo } = buildCity();
  const hasSite = hasSiteOf(layout);
  const homes = [...layout.plots.keys()].filter((k) => k.startsWith('b:'));
  const staffed = [0, 2, 3, 6, 9];
  const marketVisit = (citizen) => {
    const r = seeded(citizen.seed ^ 0x5bd1e995);
    const first = staffed[Math.floor(r() * staffed.length)];
    const stops = [{ stall: first, spot: Math.floor(r() * 2) }];
    const wanted = r() < 0.65 ? 2 : 1;
    for (const k of staffed.filter((q) => q !== first).slice(0, 3)) stops.push({ stall: k, spot: Math.floor(r() * 2) });
    const legs = stops.slice(1).map((st, k) => shopLeg(geo, stops[k], st, plot.cx, plot.cz, plot.yaw ?? 0));
    return { stops: stops.map((st) => ({ ...st, point: geo[st.stall].customers[st.spot] })), legs, via: geo[first].customers[stops[0].spot].via, wanted };
  };
  let tours = 0, twoStops = 0, moreStops = 0, toMarket = 0, total = 0;
  for (const home of homes) {
    for (let k = 0; k < 6; k++) {
      const seed = 1000 + k * 7919 + homes.indexOf(home) * 31;
      const p = planCitizen({ id: `s-${seed}`, seed, home, craft: null }, {
        sitePoint: layout.sitePoint, route: layout.route, walkable: layout.walkable, hasSite, neighbours: homes, doorFor: (q, sd) => world.doorFor(q, sd), marketVisit,
      });
      total++;
      if (p.dest?.site !== 'market') continue;
      toMarket++;
      const tour = p.dest.tour;
      assert.ok(tour && tour.stops.length >= 1, '逛市集的有一圈');
      tours++;
      if (tour.stops.length >= 2) twoStops++;
      if (tour.stops.length > 2) moreStops++;
      // 路远的多逛几个摊：多逛的那几个都装得进他本来就要在去处待的那段时间
      if (tour.stops.length > 2 && tour.stops.length < 4) assert.ok(p.stayMs - tour.ms < MARKET.stopMs + 30_000, '再多逛一个就装不下了，才停在这几个——不是干站着');
      assert.ok(p.stayMs >= tour.ms, '这一圈走完才回');
      assert.ok(2 * p.walkMs <= TOWN.walkShare * p.periodMs + 1e-6, '一来一回不超过周期的 walkShare');
      assert.ok(routeClear(p.out, layout.walkable, 0.1));
      for (const leg of tour.legs) assert.ok(routeClear(leg.pts, layout.walkable, 0.1), '摊之间一路能站');
      const end = p.out[p.out.length - 1];
      assert.ok(Math.hypot(end.x - tour.stops[0].x, end.z - tour.stops[0].z) < 1e-9, '终点就是头一个摊前（往回走从这里出发）');
      // 在每个摊前：在看货、带着摊号；一整个周期每 250 毫秒取一下，不跳
      const arrive = TOWN.homeMs + p.walkMs - p.offset;
      for (const [i, st] of tour.stops.entries()) {
        const at = citizenAt(p, arrive + st.at + 1000);
        assert.equal(at.at, 'dest'); assert.equal(at.work, 'browse');
        assert.deepEqual({ stall: at.shop.stall, spot: at.shop.spot, k: at.shop.k }, { stall: st.stall, spot: st.spot, k: i });
        assert.ok(Math.abs(at.shop.t - 1000) < 1e-6);
      }
      let prev = null;
      for (let t = -p.offset - 2000; t < p.periodMs - p.offset + 2000; t += 250) {
        const at = citizenAt(p, t);
        if (!at.hidden && prev && !prev.hidden) assert.ok(Math.hypot(at.x - prev.x, at.z - prev.z) <= 1, `${home}：${t} 毫秒没有跳`);
        prev = at;
      }
    }
  }
  assert.ok(toMarket >= total / 10, `去市集的人不少：${toMarket} / ${total}`);
  // 三成的人按种子是去买东西的（只看种子）
  let share = 0;
  for (let k = 0; k < 4000; k++) if (isShopper(k * 2654435761)) share++;
  assert.ok(Math.abs(share / 4000 - MARKET.shopperShare) < 0.03, `去买东西的占 ${(share / 40).toFixed(1)} %`);
  assert.ok(tours > 0 && twoStops > 0, '有人逛两个摊');
  assert.ok(moreStops > 0, '路远的人逛三四个摊');
});

test('market rule 4: the script — browse, ask, price, then buy / haggle and buy / haggle refused / pass — fits one stop, the goods change hands once, the vendor is engaged only when he talks or trades, and every line has words', () => {
  for (const outcome of ['buy', 'haggle', 'refused', 'pass']) {
    let bought = false, trade = 0, prevStep = null;
    const steps = [];
    for (let t = 0; t < MARKET.stopMs; t += 50) {
      const s = shopScript(t, outcome);
      if (s.step !== prevStep) steps.push(s.step);
      prevStep = s.step;
      if (s.step === 'trade') {
        trade++;
        assert.equal(s.customer.variant, 'buy'); assert.equal(s.vendor.variant, 'sell');
        assert.ok(Math.abs(s.customer.t - s.vendor.t) < 1e-9, '两人的手递手同一个钟');
      }
      if (bought) assert.ok(s.bought, '买到了就一直买到了');
      bought = s.bought;
      for (const step of [s.customer, s.vendor]) if (step?.line) assert.ok(MARKET_ZH[step.line], `${step.line} 有台词`);
      if (s.vendor) assert.ok(s.engaged);
    }
    const expect = {
      buy: ['browse', 'ask', 'price', 'trade', 'after'],
      haggle: ['browse', 'ask', 'price', 'haggle', 'concede', 'trade', 'after'],
      refused: ['browse', 'ask', 'price', 'haggle', 'refuse', 'pass', 'after'],
      pass: ['browse', 'ask', 'price', 'pass', 'after'],
    }[outcome];
    assert.deepEqual(steps, expect, outcome);
    assert.equal(bought, outcome === 'buy' || outcome === 'haggle', `${outcome}：买到了没有`);
    if (bought) assert.ok(trade * 50 >= MARKET.tradeMs - 50, '手递手做完整');
  }
  // 结局的比例：成交 45、讲价成交 25、讲价不成 10、看看就走 20（2000 次 ±4 %）
  const count = { buy: 0, haggle: 0, refused: 0, pass: 0 };
  for (let k = 0; k < 2000; k++) count[shopOutcome(k * 2654435761, k % 7)]++;
  for (const [o, share] of Object.entries({ buy: 0.45, haggle: 0.25, refused: 0.1, pass: 0.2 })) assert.ok(Math.abs(count[o] / 2000 - share) < 0.04, `${o}：${count[o]}`);
  assert.equal(shopOutcome(42, 3), shopOutcome(42, 3), '同一个人同一回永远同一个结局');
  // 吆喝：隔一阵喊一声，一声留 lineMs
  let calls = 0, prev = false;
  for (let t = 0; t < 120_000; t += 100) { const c = hawkCall(t, 777).calling; if (c && !prev) calls++; prev = c; }
  assert.ok(calls >= 5 && calls <= 9, `两分钟喊 ${calls} 声`);
  for (const k of STALL_KINDS) assert.ok(MARKET_ZH[hawkKey(k.kind)]?.split('|').length >= 3, `${k.kind} 有三种吆喝`);
  assert.ok(MARKET_ZH.marketHawkFish.split('|').includes(marketLine(MARKET_ZH, 'marketHawkFish', 5, 2)));
  assert.equal(marketLine(MARKET_ZH, 'marketHawkFish', 5, 2), marketLine(MARKET_ZH, 'marketHawkFish', 5, 2));
  assert.equal(marketLine({}, 'nope', 1), '');
  assert.equal(TRADE_MS, MARKET.tradeMs);
  assert.equal(PASS_MS, MARKET.passMs);
  for (const key of Object.keys(MARKET_ZH)) assert.equal(CITIZEN_ZH[key], MARKET_ZH[key], `${key} 在页面的缺省台词表里`);
});

test('market lines: the page\'s Chinese defaults are C#\'s fallbacks, and both locale files carry every key', () => {
  const cs = readFileSync(new URL('../../../../ViewModels/GameMode/PolisLocale.cs', import.meta.url), 'utf8');
  const entries = [...cs.matchAll(/\("(market\w+)", "(Polis\.Citizen\.Market\.\w+)", "([^"]*)"\)/g)].map((m) => ({ js: m[1], key: m[2], zh: m[3] }));
  assert.deepEqual(entries.map((e) => e.js).sort(), Object.keys(MARKET_ZH).sort(), 'C# 的 PolisLocale.Citizen 有同样的键');
  for (const e of entries) assert.equal(e.zh, MARKET_ZH[e.js], `${e.js}：C# 的中文就是页面缺省的那一份`);
  for (const lang of ['zh-CN', 'en-US']) {
    const xaml = readFileSync(new URL(`../../../../Assets/Locales/Locale.${lang}.axaml`, import.meta.url), 'utf8');
    for (const e of entries) {
      const m = new RegExp(`x:Key="${e.key.replace(/\./g, '\\.')}">([^<]*)<`).exec(xaml);
      assert.ok(m && m[1].split('|').length === e.zh.split('|').length, `${lang}：${e.key} 在，几种说法一一对应`);
    }
  }
});

test('market actions: browse, offer, buy, sell and pass stay within the joint limits, start and end where they should, and show the coin and the parcel only when they are in hand', () => {
  for (const v of ['browse', 'offer', 'buy', 'sell', 'pass']) assert.ok(WORK_VARIANTS.includes(v));
  for (const v of ['browse', 'offer', 'buy', 'sell', 'pass']) {
    for (let t = 0; t <= 12_000; t += 40) {
      const p = poseAt('work', t, { variant: v, seed: 3 });
      for (const j of JOINT_NAMES) for (const [k, a] of Object.entries(p[j])) if (k.startsWith('r')) assert.ok(Math.abs(a) <= LIMITS.angle, `${v} ${t} ${j}.${k}`);
    }
  }
  // 手递手和摆手：一帧（16 毫秒）里没有哪个关节转过 0.12 弧度（和挥手、接过一样）
  for (const [v, ms] of [['buy', TRADE_MS], ['sell', TRADE_MS], ['pass', PASS_MS]]) {
    let prev = poseAt('work', 0, { variant: v });
    for (let t = 16; t <= ms; t += 16) {
      const p = poseAt('work', t, { variant: v });
      for (const j of JOINT_NAMES) for (const k of Object.keys(p[j])) if (k.startsWith('r')) assert.ok(Math.abs(p[j][k] - prev[j][k]) <= 0.12, `${v} ${t} ${j}.${k} 一帧转了 ${Math.abs(p[j][k] - prev[j][k]).toFixed(3)}`);
      prev = p;
    }
  }
  const props = (v, f) => poseAt('work', f * TRADE_MS, { variant: v }).propR;
  assert.equal(props('buy', 0.05), null); assert.equal(props('buy', 0.25), 'coin'); assert.equal(props('buy', 0.5), null); assert.equal(props('buy', 0.9), 'parcel');
  assert.equal(props('sell', 0.3), null); assert.equal(props('sell', 0.6), 'parcel'); assert.equal(props('sell', 0.8), null);
  // 抱着走：两只手捧着那包货，走路时不摆
  const walking = [0, 300, 600].map((t) => poseAt('walk', t, { carry: { side: 'R', grip: 'both', prop: 'parcel' } }));
  assert.ok(walking.every((p) => p.propR === 'parcel'));
  assert.ok(walking.every((p) => Math.abs(p.shoulderL.rx - walking[0].shoulderL.rx) < 1e-9 && Math.abs(p.shoulderR.rx - walking[0].shoulderR.rx) < 1e-9), '捧着货的胳膊不随步子摆');
});

test('market actions on the real rig: across the counter the coin reaches the vendor\'s open palm and the parcel passes from his hands to the buyer\'s without a jump', () => {
  const mk = (seed) => buildCitizen(M, { ...citizenLook(seed, { trade: 'potter' }).spec, height: 1.75, build: 1 });
  const vendor = mk(12346), buyer = mk(12348);
  const gap = STALL.customers[0][1] - STALL.vendor[1];
  buyer.root.position.set(0, 0, gap);
  buyer.root.rotation.y = Math.PI;
  const scene = new THREE.Group();
  scene.add(vendor.root, buyer.root);
  let clock = 0;
  const at = (f) => {
    for (let k = 25; k >= 0; k--) {
      clock += 16;
      const t = Math.max(0, f * TRADE_MS - k * 16);
      vendor.pose('work', t, { variant: 'sell', clock });
      buyer.pose('work', t, { variant: 'buy', clock });
    }
    scene.updateMatrixWorld(true);
  };
  const where = (fig, name) => { const p = fig.props.get(name); return p.visible ? p.getWorldPosition(new THREE.Vector3()) : null; };
  // 钱：递到最远的那一刻，钱离摊主左手的手腕不到 0.2 米（掌心比手腕再往前几厘米），高度相同
  at(0.3);
  const coin = where(buyer, 'coin');
  const palm = vendor.joints.handL.getWorldPosition(new THREE.Vector3());
  assert.ok(coin && coin.distanceTo(palm) < 0.2, `钱到了摊主的掌心（${coin?.distanceTo(palm).toFixed(3)} 米）`);
  assert.ok(Math.abs(coin.y - palm.y) < 0.08);
  // 货：摊主手里的最后一刻与买主手里的第一刻，那包货挪了不到 0.12 米；交接的地方在柜台上方
  at(0.65);
  const from = where(vendor, 'parcel');
  at(0.67);
  const to = where(buyer, 'parcel');
  assert.ok(from && to, '交接前在摊主手里、之后在买主手里');
  assert.ok(from.distanceTo(to) < 0.12, `交接时没有跳（${from.distanceTo(to).toFixed(3)} 米）`);
  assert.ok(from.y > STALL.counter.h + 0.15 && from.z > STALL.counter.z0 - STALL.vendor[1] && from.z < STALL.counter.z1 - STALL.vendor[1] + 0.1, '在柜台上方递过去');
  // 两人都不在交接时把手伸进柜台里
  for (const fig of [vendor, buyer]) for (const s of ['L', 'R']) {
    const h = fig.joints[`hand${s}`].getWorldPosition(new THREE.Vector3());
    assert.ok(h.y > STALL.counter.h + 0.05, '手在柜台面之上');
  }
});

test('citizens rule 10: the market closes at nightfall — whoever is going to it, in it or coming back walks straight home from where he stands, goes in, and nobody jumps or vanishes on the spot', () => {
  const { layout, world, plot, geo } = buildCity();
  const hasSite = hasSiteOf(layout);
  const homes = [...layout.plots.keys()].filter((k) => k.startsWith('b:'));
  const staffed = [0, 2, 4, 7, 9];
  const marketVisit = (citizen) => {
    const stops = staffed.slice(0, 3).map((stall, k) => ({ stall, spot: (citizen.seed + k) % 2 }));
    const legs = stops.slice(1).map((st, k) => shopLeg(geo, stops[k], st, plot.cx, plot.cz, plot.yaw ?? 0));
    return { stops: stops.map((st) => ({ ...st, point: geo[st.stall].customers[st.spot] })), legs, via: geo[stops[0].stall].customers[stops[0].spot].via, wanted: 3 };
  };
  const plans = [];
  for (const home of homes.slice(0, 6)) {
    const g = geo[homes.indexOf(home) % geo.length];
    plans.push(planCitizen({ id: `v-${home}`, seed: 31, home, vendor: { stall: g.index, x: g.vendor.x, z: g.vendor.z, yaw: g.vendor.yaw, via: g.via } }, {
      sitePoint: layout.sitePoint, route: layout.route, walkable: layout.walkable, hasSite, neighbours: homes, doorFor: (q, sd) => world.doorFor(q, sd),
    }));
    const shoppers = [];
    for (let seed = 11 + homes.indexOf(home) * 1000; shoppers.length < 2; seed++) if (isShopper(seed)) shoppers.push(seed);
    for (const seed of shoppers) {
      const p = planCitizen({ id: `s-${home}-${seed}`, seed, home }, {
        sitePoint: layout.sitePoint, route: layout.route, walkable: layout.walkable, hasSite, neighbours: homes, doorFor: (q, sd) => world.doorFor(q, sd), marketVisit,
      });
      if (p.dest?.site === 'market') plans.push(p);
    }
  }
  assert.ok(plans.filter((p) => p.sell).length >= 6 && plans.filter((p) => p.dest?.tour?.stops.length === 3).length >= 2, '有摊主、有逛三个摊的人');
  let checked = 0, walkedHome = 0;
  for (const p of plans) {
    for (let k = 0; k < 40; k++) {
      const since = Math.round((k / 40) * p.periodMs) - p.offset;
      const sky = { night: true, since, owl: false };
      const before = citizenAt(p, since - 1);
      const retire = retireTime(p, since);
      if (!before.hidden && retire > since) {
        // 那一刻不跳：入夜后的第一帧就在入夜前的那一点
        const first = citizenAt(p, since, sky);
        assert.ok(!first.hidden && Math.hypot(first.x - before.x, first.z - before.z) < 0.05, `${p.id} @${k}：入夜那一刻没有跳（${Math.hypot(first.x - before.x, first.z - before.z).toFixed(2)} 米）`);
      }
      // 一路走回家：每 250 毫秒不挪超过 1 米、每一点都能站；到家进门（或回屋）之后就不画了
      let prev = null;
      const dusk = duskHome(p, since);
      if (dusk) {
        assert.ok(retire - since <= p.walkMs + (p.dest.tour?.ms ?? 0) + DOOR.transitMs + 1, `${p.id} @${k}：一程（加上市集里倒回去的那一段）之内到家`);
        if (dusk.ms > 1000) walkedHome++;
        assert.ok(routeClear(dusk.pts, layout.walkable, 0.1), `${p.id} @${k}：回家的路每 0.1 米都能站`);
      }
      for (let t = since; t < retire + 2000 && Number.isFinite(retire); t += 250) {
        const at = citizenAt(p, t, sky);
        if (prev && !prev.hidden && !at.hidden) assert.ok(Math.hypot(at.x - prev.x, at.z - prev.z) <= 1, `${p.id} @${k}：${t - since} 毫秒没有跳`);
        prev = at;
      }
      if (Number.isFinite(retire)) assert.ok(citizenAt(p, retire + 10, sky).hidden, `${p.id} @${k}：到家之后在屋里`);
      checked++;
    }
  }
  assert.ok(checked > 400 && walkedHome > 50, `${checked} 个时刻，其中 ${walkedHome} 个是从市集或路上走回家的`);
});
