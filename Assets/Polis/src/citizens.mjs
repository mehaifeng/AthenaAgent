// 居民在城里怎么过日子（M-B，2026-10-13）：日程、避让、分档、闲聊。纯逻辑（不碰 DOM、不碰 three.js），Node 单测 citizens.test.mjs。
// 一位居民是一场会话（C# 的 PolisCitizens）；长相在 art/citizens.mjs，这里只管他在哪儿、在干什么、跟谁说话。
//
// 规则（每一条都有断言）：
//   1. 日程按墙钟、按种子错开：每 periodMs 一个周期——先在家门口待着，走去一处（广场一圈、市集、港口、神庙、图书馆、橄榄林，
//      或者邻居家门口），在那儿待一会儿，再走回来。一程最多走 maxWalkMs（远过它的去处不去，宁可待在家），一来一回最多占
//      周期的 40 %，所以任何时候走着的人不到一半——满街的人都在走，城就成了车站。同一刻永远是同一个位置（截图与断言可复现）。
//   2. 避让：两人靠得比 separation 近，互相让一让（每帧最多挪 pushPerFrame，累计的偏移有上限、没人挤就慢慢回到原路），
//      让开的位置必须能站——绝不把人推进房子里。你不被挡：你是一块推不动的石头，挡在你面前的人侧身让开。
//   3. 分档：离你最近的 tierA 位全套（布料、弹簧骨），画面里其余的只摆姿势、布料停着，画面外的不摆也不画。
//      进出 A 档有 1 米的滞回，不在边界上来回跳。
//   4. 闲聊：两位都闲着、相距 chatRadius 以内、都在画面里、离你 chatPlayerRadius 以内、这一对 chatPairCooldownMs 里没说过——
//      全城同时只有一组在说，一人一句。2026-10-13 起（用户：NPC 之间的对话只用模型，不预置台词）这里只挑人：说什么由 C# 的模型定
//      （ambient-chat），先开口的那位头顶一串循环的点、然后流式出字，另一位接一句；他们说着话时站定（日程钟停下，规则 5），
//      一对最多 chatMaxMs，说完就散。
//   5. 站定不吃日程（2026-10-13，用户：走着的人被叫住，Esc 之后他会飞快地"滑"到本该走到的地方）：被叫住、当陪审团的那段时间
//      从他自己的日程钟里扣掉（scheduleClock / holdSchedule / resumeSchedule），松开后从站定的那一刻接着走。
//   6. 家有门的（第三轮需求 2：民居、作坊、货栈地面上的门，doors.mjs）：一个周期里头尾各 TOWN.insideMs 在屋里（不画、不摆）；
//      开门出来、在门前待着（门阶旁，不挡进出门的路），走去一处再回来，进门。门前到街上走 doors.planApproach 算好的那条路。
//      没有门的家照旧：家门口就是街上那一点附近。
//   7. 手艺（第三轮需求 4，art/crafts.mjs）：在门前干的活（拉坯、纺线、揉面、研磨、缝鞋、琢石、锯木）——家在一座建筑里的，
//      在家门前的那几段时间就在干：沿着墙侧身站、背对着门（工位在他面前 STATION_GAPS 米，按他的身高缩放），工位的四角都要能站、不碰门阶和门前
//      那条路，站不下就不在门前干；一扇门住着几口人时工位不互相压着、不压在别人站的地方（settleStations，按 id 的次序收，
//      压着的那件不摆、那位在门前闲着——只看整份名单，和谁先排日程无关）；在公共地方干的（渔夫在栈桥西沿、葡萄农与养蜂人在橄榄林、乐师在广场、香料商在市集）——去处先试那里
//      （走得到的话），在那儿待着的 25 秒就是干活；渔夫要一个世界给的站位（layout.workSpot：水边、朝着水），没有就不钓。
//      那处走不到的（一程超过 maxWalkMs），拿在手里就能干的活（portable）就在家门前干（乐师在门前弹琴、香料商在门前研磨）；
//      钓鱼要水、锄地要土（门前是街面的铺石），走不到就不干。拿在手里就能干的活，住在公共地方的人也在住处干（广场上纺线的、
//      市集上缝鞋的）——要工位的只在有门的家门前（工位靠着墙；
//      没有门的家门前那一点在街上，工位摆在街当中，过路的人会从它中间穿过去）。
//      干活时 action 是 'work'、work 是那门手艺的样子（poses 的 variant）；闲聊、打招呼照样会挑干活的人（他停下手里的活）。

import { DOOR, transitAt } from './doors.mjs';
import { STATION_GAPS, STATION_EXTENT } from './art/crafts.mjs';

export const TOWN = Object.freeze({
  streetCap: 32,         // 街上同时最多几位（用户定的 32；C# 的 PolisCitizens.StreetCap 先截，页面再守一道——帧率门槛按 32 位量）
  periodMs: 150_000,     // 一个周期
  homeMs: 45_000,        // 每个周期开头在家门口待着
  stayMs: 25_000,        // 在去处待着
  maxWalkMs: 30_000,     // 一程最多走这么久（1.3 米 / 秒，约 39 米：两个街区）；一来一回最多占周期的 40 %
  speed: 1.3,            // 米 / 秒（雅典娜 3.2，你 6）
  homeRing: [0.8, 2.4],  // 家门口站在离门前那一点多远的一圈上（几个人住一处，不叠在一个点上）
  destRing: [0.9, 2.6],  // 去处同理
  separation: 0.6,
  playerRadius: 0.7,     // 你周围这么大一圈，居民让开
  pushPerFrame: 0.03,
  maxNudge: 0.8,
  nudgeDecay: 0.94,
  tierA: 4,
  tierARadius: 6,        // 全套只给离你这么近以内的（再远，人在画面上只有几十像素高，衣摆怎么飘看不出来）
  hysteresis: 1,
  chatRadius: 2.5,
  chatPlayerRadius: 15,
  chatPairCooldownMs: 180_000,
  chatLineMs: 2600,      // 打招呼那一句留多久
  chatNeedMs: 8000,      // 挑的时候两人都还要再闲着这么久（说着话时日程钟停着，这只是别挑正要动身的人）
  chatMaxMs: 60_000,     // 一对最多聊这么久：C# 那边卡住了也散（两句各 25 秒超时）
  greetRadius: 2.5,
  greetCooldownMs: 120_000,
  labelRadius: 6,
  insideMs: 15_000,      // 家有门的：周期开头和末尾各在屋里这么久（第三轮需求 2）
  doorSpot: [0.9, 1.4],  // 门前站的地方离门阶多远（沿着墙往旁边让开，不挡进出门的路）
});

/** 去处（站点名，与 layout.sitePoint 一致）。 */
export const DESTINATIONS = Object.freeze(['agora:0', 'agora:1', 'agora:2', 'agora:3', 'agora:4', 'agora:5', 'market', 'harbor', 'temple', 'library', 'grove:0']);

/** 种子 → [0, 1) 的一串数（mulberry32），同一个种子永远同一串。 */
export function seeded(seed) {
  let s = seed >>> 0;
  return () => {
    s = (s + 0x6D2B79F5) | 0;
    let t = Math.imul(s ^ (s >>> 15), 1 | s);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

const polyLength = (pts) => {
  let d = 0;
  for (let k = 1; k < pts.length; k++) d += Math.hypot(pts[k].x - pts[k - 1].x, pts[k].z - pts[k - 1].z);
  return d;
};

function pointAlong(pts, f) {
  if (pts.length === 1) return { x: pts[0].x, z: pts[0].z, dir: 0 };
  let want = Math.min(1, Math.max(0, f)) * polyLength(pts);
  for (let k = 1; k < pts.length; k++) {
    const a = pts[k - 1], b = pts[k];
    const seg = Math.hypot(b.x - a.x, b.z - a.z);
    if (want <= seg || k === pts.length - 1) {
      const u = seg > 0 ? Math.min(1, want / seg) : 1;
      return { x: a.x + (b.x - a.x) * u, z: a.z + (b.z - a.z) * u, dir: Math.atan2(b.x - a.x, b.z - a.z) };
    }
    want -= seg;
  }
  const last = pts[pts.length - 1];
  return { x: last.x, z: last.z, dir: 0 };
}

/** 一个能站的点：从 center 起，在半径 [lo, hi] 的一圈上按种子试几个方向，一路上每 0.2 米都要能站。都不行就站在 center。 */
export function standNear(center, r, [lo, hi], walkable, tries = 10) {
  for (let k = 0; k < tries; k++) {
    const a = r() * Math.PI * 2, d = lo + (hi - lo) * r();
    const p = { x: center.x + Math.sin(a) * d, z: center.z + Math.cos(a) * d };
    let clear = walkable(p);
    for (let s = 0.2; s < d && clear; s += 0.2) clear = walkable({ x: center.x + Math.sin(a) * s, z: center.z + Math.cos(a) * s });
    if (clear) return p;
  }
  return { x: center.x, z: center.z };
}

/** 整条路每 step 米查一次都能站（街道图的第一段从广场的站位直线连到路口，可能横穿讲台或祭坛）。 */
export function routeClear(pts, walkable, step = 0.25) {
  for (let k = 1; k < pts.length; k++) {
    const a = pts[k - 1], b = pts[k];
    const len = Math.hypot(b.x - a.x, b.z - a.z);
    const n = Math.max(1, Math.ceil(len / step));
    for (let i = 0; i <= n; i++) {
      const u = i / n;
      if (!walkable({ x: a.x + (b.x - a.x) * u, z: a.z + (b.z - a.z) * u })) return false;
    }
  }
  return true;
}

/**
 * 一位居民的日程（换城、城变了时重算）。
 * @param {{ id: string, seed: number, home: string, craft?: { variant: string, site: string, station: string|null, needsSpot?: boolean, portable?: boolean, scale?: number }|null }} citizen
 *   craft（规则 7）：art/crafts.mjs 的 craftFor，site 已经由 craftSite 换成站点名（'home' 是自家门前）；scale 是他的身高 / 1.75（工位跟着缩放）
 * @param {{ sitePoint: (site: string) => {x:number,z:number}, route: (a: string, b: string) => Array<{x:number,z:number}>, walkable: (p) => boolean, hasSite?: (site: string) => boolean, neighbours?: string[], workSpot?: (site: string, seed: number) => {x:number,z:number,yaw:number}|null }} layout
 *   neighbours：城里各座建筑的站点（b:&lt;文件夹&gt;）——居民也去邻居家门口坐坐；workSpot：干活的那处要专门的站位时（渔夫）世界给的那一点
 * @returns {{ id, home: {site, point, yaw}, dest: {site, point, yaw}|null, out: Array<{x,z}>|null, walkMs: number, offset: number, door, craft }}
 */
export function planCitizen(citizen, layout) {
  const r = seeded(citizen.seed ^ 0x9e3779b9);
  const has = layout.hasSite ?? (() => true);
  const homeSite = has(citizen.home) ? citizen.home : 'agora:0';
  const homeStand = layout.sitePoint(homeSite);
  // 规则 6：家有门的，门前站的地方在门阶旁；没有门的照旧在街上那一点附近
  const door = layout.doorFor?.(homeSite, citizen.seed) ?? null;
  let homePoint = door ? doorSpot(door, r, layout.walkable) : standNear(homeStand, r, TOWN.homeRing, layout.walkable);
  const homeYaw = r() * Math.PI * 2;
  const offset = Math.floor(r() * TOWN.periodMs);
  // 规则 7：门前干活的，站在哪、朝哪、工位摆在哪（要工位的往外让，直到工位摆得下）
  const craft = citizen.craft ?? null;
  const home = homeWork(craft, { homeSite, door, homePoint, planYaw: homeYaw, walkable: layout.walkable });
  if (home) homePoint = home.point;
  // 去处按种子排个顺序，取第一个走得到（一程不超过 maxWalkMs）的
  let order = [...DESTINATIONS, ...(layout.neighbours ?? [])].filter((d) => d !== homeSite && has(d))
    .map((d) => ({ d, k: r() }))
    .sort((a, b) => a.k - b.k)
    .map((x) => x.d);
  // 规则 7：在公共地方干活的，去处先试那里
  const workSite = craft && craft.site !== 'home' && craft.site !== homeSite && has(craft.site) ? craft.site : null;
  if (workSite) order = [workSite, ...order.filter((d) => d !== workSite)];
  const place = { home, centre: siteOr(layout, 'agora') };
  // 规则 7：去处那处走不到时，拿在手里就能干的活在家门前干（门前的站位不挪，所以放在找去处之后算）
  const fallback = (dest) => (!home && craft && craft.site !== 'home' && craft.portable && dest?.site !== craft.site
    ? homeWork({ ...craft, site: 'home' }, { homeSite, door, homePoint, planYaw: homeYaw, walkable: layout.walkable })
    : home);
  for (const site of order) {
    let path;
    try { path = layout.route(homeSite, site); } catch { continue; }
    if (!path || path.length === 0) continue;
    // 规则 7：干活的那处有它自己的站位（渔夫：栈桥的西沿上，朝着海）就站那儿
    const spot = site === workSite ? layout.workSpot?.(site, citizen.seed) ?? null : null;
    const destPoint = spot ? { x: spot.x, z: spot.z } : standNear(layout.sitePoint(site), r, TOWN.destRing, layout.walkable);
    const via = spot?.via ?? [];   // 站位前要先走到的点（栈桥中线：斜着走会擦过系船柱）
    // 有门的家：门前 → 门阶 → 门阶到街上的那条路（最后一点就是街上的站点）→ 去处
    const out = door ? [homePoint, door.step, ...door.approach.slice(1), ...path.slice(1), ...via, destPoint] : [homePoint, ...path, ...via, destPoint];
    const walkMs = (polyLength(out) / TOWN.speed) * 1000;
    if (walkMs > TOWN.maxWalkMs) continue;
    if (!routeClear(out, layout.walkable)) continue;
    const dest = { site, point: destPoint, yaw: r() * Math.PI * 2 };
    return { id: citizen.id, home: { site: homeSite, point: homePoint, yaw: homeYaw }, dest, out, walkMs, offset, door: doorOf(door), craft: craftOf(craft, { ...place, home: fallback(dest), dest, spot }) };
  }
  return { id: citizen.id, home: { site: homeSite, point: homePoint, yaw: homeYaw }, dest: null, out: null, walkMs: 0, offset, door: doorOf(door), craft: craftOf(craft, { ...place, home: fallback(null), dest: null, spot: null }) };
}

const siteOr = (layout, site) => { try { return layout.sitePoint(site); } catch { return null; } };

/**
 * 规则 7：工位摆在 at、朝 yaw（人站在它的 −z 那一边）、缩放 scale 时，四个角（各往里收 0.15 米）和正中都要能站——能站的点离挡路的
 * 东西至少一个人的半径（0.25 米），所以收进去 0.15 米的角能站，工位真正的角就不会插进墙里、水里。
 */
export function stationClear(kind, at, yaw, walkable, scale = 1) {
  const ext = STATION_EXTENT[kind];
  if (!ext) return false;
  const fx = Math.sin(yaw), fz = Math.cos(yaw);   // 工位的 +z（人面朝的方向）
  const sx = fz, sz = -fx;                          // 工位的 +x
  const [ex, ez] = [Math.max(0, ext[0] * scale - 0.15), Math.max(0, ext[1] * scale - 0.15)];
  const pts = [[0, 0], [ex, ez], [-ex, ez], [ex, -ez], [-ex, -ez]];
  return pts.every(([a, b]) => walkable({ x: at.x + sx * a + fx * b, z: at.z + sz * a + fz * b }));
}

/**
 * 规则 7：门前干活的样子——站在哪（point）、朝哪（yaw）、工位（station）。null 是不在门前干。
 * 要工位的家要有一扇门（工位靠着墙摆；没有门的家门前那一点在街上，工位会摆在街当中，过路的人从它中间穿过去）；
 * 不要工位的住在公共地方、住在没有门的建筑里也干（朝向就是日程挑的那个）。
 * 门前站不下、只能站在门阶上的（doorSpot 退回门阶）不干：他会把门堵住。沿着墙侧身站、背对着门
 * （门阶沿着墙往哪一边让开的，就面朝那一边）。要工位的，工位四角都要能站、不碰门阶和门前的路——站的地方挨着墙，
 * 工位横着伸进墙里时，人和工位一起沿着门的法线往外让（每次 0.1 米，最多 0.8 米，让出去的地方从门阶走得过去），都不行就不干。
 */
function homeWork(craft, { homeSite, door, homePoint, planYaw, walkable }) {
  if (!craft || craft.site !== 'home' || (craft.station && !door)) return null;
  if (door && Math.hypot(homePoint.x - door.step.x, homePoint.z - door.step.z) < 0.05) return null;
  let yaw = planYaw;
  const nx = door?.nx ?? 0, nz = door?.nz ?? 1;
  if (door) {
    const s = (homePoint.x - door.step.x) * nz - (homePoint.z - door.step.z) * nx >= 0 ? 1 : -1;
    yaw = Math.atan2(s * nz, -s * nx);
  }
  if (!craft.station) return { point: homePoint, yaw, station: null };
  const scale = craft.scale ?? 1;
  const gap = (STATION_GAPS[craft.station] ?? 0.45) * scale;
  for (const k of door ? [0, 0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8] : [0]) {
    const p = { x: homePoint.x + nx * k, z: homePoint.z + nz * k };
    if (k > 0 && (!walkable(p) || !routeClear([door.step, p], walkable, 0.1))) continue;
    const at = { x: p.x + Math.sin(yaw) * gap, z: p.z + Math.cos(yaw) * gap };
    const station = { kind: craft.station, x: at.x, z: at.z, yaw, scale };
    if (stationClear(craft.station, at, yaw, walkable, scale) && !blocksDoorway(station, door)) return { point: p, yaw, station };
  }
  return null;
}

/**
 * 规则 7：计划里的手艺——在门前干（atHome）还是在去处干（atDest）、各朝哪边、门前的工位在哪。返回 null 是不干活。
 * 门前的那一份由 homeWork 先算好（它可能把门前站的地方往外挪）。去处：世界给了站位的朝站位的方向（渔夫朝着海）；
 * 广场上朝着广场中间（乐师弹给大家听）；其余照日程挑的朝向。渔夫这种要站位的，没有站位就不在那儿干。
 */
function craftOf(craft, { home, centre, dest, spot }) {
  if (!craft) return null;
  const atHome = !!home;
  const atDest = !!dest && dest.site === craft.site && (!craft.needsSpot || !!spot);
  let destYaw = null;
  if (atDest) {
    if (spot && Number.isFinite(spot.yaw)) destYaw = spot.yaw;
    else if (dest.site.startsWith('agora') && centre) destYaw = Math.atan2(centre.x - dest.point.x, centre.z - dest.point.z);
    else destYaw = dest.yaw;
  }
  if (!atHome && !atDest) return null;
  // proposed：规则 7 的工位（四角能站、不碰门前的路）；station 与 atHome 由 settleStations 按整份名单定（同一扇门住着几口人）
  const station = home?.station ?? null;
  return { variant: craft.variant, atHome, atDest, homeYaw: home?.yaw ?? null, destYaw, station, proposed: station };
}

/** 一点落在工位里（各边再放宽 margin 米；工位按 st.scale 缩放）。 */
export function inStation(st, p, margin = 0) {
  const ext = STATION_EXTENT[st.kind], k = st.scale ?? 1;
  const fx = Math.sin(st.yaw), fz = Math.cos(st.yaw);
  const dx = p.x - st.x, dz = p.z - st.z;
  return Math.abs(dx * fz - dz * fx) <= ext[0] * k + margin && Math.abs(dx * fx + dz * fz) <= ext[1] * k + margin;
}

/** 工位碰到门阶、或者门前到街上的那条路（每 0.1 米查一次，人的半径 0.25 米）。没有门的家不查。 */
function blocksDoorway(st, door) {
  if (!door?.step) return false;
  if (inStation(st, door.step, 0.35)) return true;
  const pts = [door.step, ...(door.approach ?? []).slice(1)];
  for (let k = 1; k < pts.length; k++) {
    const a = pts[k - 1], b = pts[k];
    const n = Math.max(1, Math.ceil(Math.hypot(b.x - a.x, b.z - a.z) / 0.1));
    for (let i = 0; i <= n; i++) if (inStation(st, { x: a.x + ((b.x - a.x) * i) / n, z: a.z + ((b.z - a.z) * i) / n }, 0.25)) return true;
  }
  return false;
}

/**
 * 规则 7：门前的工位一件一件收（按 id 的次序）：不压在别人站的地方（他家门前那一点，放宽 0.3 米），不和已经收下的工位挨着
 * （两件的外接圆之间至少 0.15 米）。收下的 station 有值、atHome 为真；没收下的 station 为 null、atHome 为假（他在门前闲着）。
 * 只看整份名单（plans），和谁先排日程无关；名单变了再跑一遍。返回收下的件数。
 */
export function settleStations(plans) {
  const all = [...plans].filter(Boolean);
  const radius = (st) => Math.hypot(...STATION_EXTENT[st.kind]) * (st.scale ?? 1);
  const kept = [];
  for (const p of all.filter((q) => q.craft?.proposed).sort((a, b) => (a.id < b.id ? -1 : a.id > b.id ? 1 : 0))) {
    const st = p.craft.proposed;
    const free = all.every((o) => o === p || !inStation(st, o.home.point, 0.3))
      && kept.every((k) => Math.hypot(k.x - st.x, k.z - st.z) >= radius(k) + radius(st) + 0.15);
    p.craft.station = free ? st : null;
    p.craft.atHome = free;
    if (free) kept.push(st);
  }
  return kept.length;
}

/** 规则 7：在家门前、在去处待着的那一刻，干不干活。 */
function craftState(plan, where, state) {
  const c = plan.craft;
  if (!c || (where === 'home' && !c.atHome) || (where === 'dest' && !c.atDest)) return state;
  return { ...state, action: 'work', work: c.variant, yaw: where === 'home' ? c.homeYaw : c.destYaw };
}

/** 计划里记下的那扇门（只留进出门要的几样）。 */
const doorOf = (door) => (door ? { id: door.id, step: door.step, inside: door.inside, sill: door.sill ?? 0, x: door.x, z: door.z, nx: door.nx ?? 0, nz: door.nz ?? 1 } : null);

/**
 * 规则 6：门前站的地方——门阶沿着墙往左或往右让开 TOWN.doorSpot（按种子挑一边，另一边做后备），稍微往外一点；
 * 门阶到那里一路都能站。都不行就站在门阶上。
 */
export function doorSpot(door, r, walkable) {
  const nx = door.nx ?? 0, nz = door.nz ?? 1;
  const side = { x: nz, z: -nx };
  const first = r() < 0.5 ? 1 : -1;
  const [lo, hi] = TOWN.doorSpot;
  const d = lo + (hi - lo) * r();
  const out = 0.1 + 0.3 * r();
  for (const s of [first, -first]) {
    const p = { x: door.step.x + side.x * s * d + nx * out, z: door.step.z + side.z * s * d + nz * out };
    if (walkable(p) && routeClear([door.step, p], walkable, 0.1)) return p;
  }
  return { x: door.step.x, z: door.step.z };
}

const ease = (f) => f * f * (3 - 2 * f);

/** 一位居民自己的日程钟：{ shift: 累计站定了多久, holdAt: 这一次从哪一刻站定的（没站定为 null） }。 */
export const newScheduleClock = () => ({ shift: 0, holdAt: null });

/** 他的日程此刻走到了墙钟的哪一刻：站定时停在站定的那一刻，松开后从那里接着走（规则 5）。 */
export function scheduleClock(clock, wall) {
  return (clock.holdAt ?? wall) - clock.shift;
}

/** 叫住他：日程钟停下（已经停着就不动）。 */
export function holdSchedule(clock, wall) {
  if (clock.holdAt == null) clock.holdAt = wall;
}

/** 放开他：站定的这段时间记进 shift，日程钟接着走。 */
export function resumeSchedule(clock, wall) {
  if (clock.holdAt == null) return;
  clock.shift += Math.max(0, wall - clock.holdAt);
  clock.holdAt = null;
}

/**
 * 他在墙钟 wall 这一刻在哪、干什么。返回 { x, z, yaw, action: 'idle'|'walk'|'work'（干活时还有 work：那门手艺的样子，规则 7）, walkT（步子的相位，毫秒）, idleLeft（这段待着还剩多久，走着时为 0）,
 * at: 'home'|'dest'|'road'|'door'|'inside' }。家有门的（规则 6）：at 'inside' 时 hidden 为真（不画、不摆），at 'door' 时带着
 * door: { id, open（门开几分）, kind: 'exit'|'enter', phase: 'pass'|'leg' }，进门的最后一下 hidden 也为真。
 */
export function citizenAt(plan, wall) {
  const p = ((wall + plan.offset) % TOWN.periodMs + TOWN.periodMs) % TOWN.periodMs;
  const atHome = (left) => craftState(plan, 'home', { x: plan.home.point.x, z: plan.home.point.z, yaw: plan.home.yaw, action: 'idle', walkT: 0, idleLeft: left, at: 'home' });
  if (plan.door) return doorDay(plan, p, atHome);
  if (!plan.dest) return atHome(Number.POSITIVE_INFINITY);
  const t1 = TOWN.homeMs, t2 = t1 + plan.walkMs, t3 = t2 + TOWN.stayMs, t4 = t3 + plan.walkMs;
  const length = polyLength(plan.out);
  if (p < t1) return atHome(t1 - p);
  if (p < t2) {
    const f = ease((p - t1) / plan.walkMs);
    const q = pointAlong(plan.out, f);
    return { x: q.x, z: q.z, yaw: q.dir, action: 'walk', walkT: ((length * f) / TOWN.speed) * 1000, idleLeft: 0, at: 'road' };
  }
  if (p < t3) return craftState(plan, 'dest', { x: plan.dest.point.x, z: plan.dest.point.z, yaw: plan.dest.yaw, action: 'idle', walkT: 0, idleLeft: t3 - p, at: 'dest' });
  if (p < t4) {
    const f = ease((p - t3) / plan.walkMs);
    const q = pointAlong(plan.out, 1 - f);
    return { x: q.x, z: q.z, yaw: q.dir + Math.PI, action: 'walk', walkT: ((length * f) / TOWN.speed) * 1000, idleLeft: 0, at: 'road' };
  }
  return atHome(TOWN.periodMs - p + TOWN.homeMs);
}

/** 规则 6：有门的家的一天（p：周期里的第几毫秒）。 */
function doorDay(plan, p, atHome) {
  const IN = TOWN.insideMs, T = DOOR.transitMs, P = TOWN.periodMs;
  const enterAt = P - IN - T;
  const inside = (left) => ({ x: plan.door.inside.x, z: plan.door.inside.z, yaw: Math.atan2(plan.door.nx, plan.door.nz), action: 'idle', walkT: 0, idleLeft: left, at: 'inside', hidden: true, door: { id: plan.door.id, open: 0, kind: null, phase: null } });
  if (p < IN) return inside(IN - p);
  if (p < IN + T) return doorTransit(plan, 'exit', p - IN);
  if (p >= enterAt + T) return inside(P - p + IN);
  if (p >= enterAt) return doorTransit(plan, 'enter', p - enterAt);
  if (!plan.dest) return atHome(enterAt - p);
  const t1 = TOWN.homeMs, t2 = t1 + plan.walkMs, t3 = t2 + TOWN.stayMs, t4 = t3 + plan.walkMs;
  const length = polyLength(plan.out);
  if (p < t1) return atHome(t1 - p);
  if (p < t2) {
    const f = ease((p - t1) / plan.walkMs);
    const q = pointAlong(plan.out, f);
    return { x: q.x, z: q.z, yaw: q.dir, action: 'walk', walkT: ((length * f) / TOWN.speed) * 1000, idleLeft: 0, at: 'road' };
  }
  if (p < t3) return craftState(plan, 'dest', { x: plan.dest.point.x, z: plan.dest.point.z, yaw: plan.dest.yaw, action: 'idle', walkT: 0, idleLeft: t3 - p, at: 'dest' });
  if (p < t4) {
    const f = ease((p - t3) / plan.walkMs);
    const q = pointAlong(plan.out, 1 - f);
    return { x: q.x, z: q.z, yaw: q.dir + Math.PI, action: 'walk', walkT: ((length * f) / TOWN.speed) * 1000, idleLeft: 0, at: 'road' };
  }
  return atHome(enterAt - p);
}

/**
 * 规则 6：一次进出门（doors.transitAt 给每一刻在哪一段、走了几成、门开几分）。出门：门里 → 门阶 → 门前站的地方；进门反过来。
 * 走着的那几刻 action 是 walk（步子的相位按走过的距离），门开着、人在门里等的那一下是 idle。
 */
export function doorTransit(plan, kind, t, spot = plan.home.point) {
  const tr = transitAt(kind, t);
  const d = plan.door;
  const [a, b] = tr.phase === 'pass'
    ? (kind === 'exit' ? [d.inside, d.step] : [d.step, d.inside])
    : (kind === 'exit' ? [d.step, spot] : [spot, d.step]);
  const x = a.x + (b.x - a.x) * tr.f, z = a.z + (b.z - a.z) * tr.f;
  const len = Math.hypot(b.x - a.x, b.z - a.z);
  const moving = tr.f > 0 && tr.f < 1 && len > 0.05;
  const yaw = len > 0.05 ? Math.atan2(b.x - a.x, b.z - a.z) : Math.atan2(d.nx, d.nz);
  return {
    x, z, yaw, action: moving ? 'walk' : 'idle', walkT: ((len * tr.f) / TOWN.speed) * 1000, idleLeft: 0, at: 'door',
    hidden: !!tr.hidden, door: { id: d.id, open: tr.open, kind, phase: tr.phase },
  };
}

/**
 * 避让（规则 2），每帧一次。people：[{ id, x, z, nudge: {x, z}, movable }]——x / z 是日程给的位置（不含偏移）；
 * player：{ x, z }（推不动的那块石头）。返回每个可动的人新的偏移（已经保证 x + nudge 能站）。
 */
export function separate(people, player, walkable) {
  const cell = 1;
  const grid = new Map();
  const keyOf = (x, z) => `${Math.floor(x / cell)},${Math.floor(z / cell)}`;
  const pos = people.map((p) => ({ ...p, px: p.x + (p.nudge?.x ?? 0), pz: p.z + (p.nudge?.z ?? 0) }));
  pos.forEach((p, i) => {
    const k = keyOf(p.px, p.pz);
    if (!grid.has(k)) grid.set(k, []);
    grid.get(k).push(i);
  });
  const out = new Map();
  for (let i = 0; i < pos.length; i++) {
    const p = pos[i];
    if (!p.movable) continue;
    let fx = 0, fz = 0;
    const cx = Math.floor(p.px / cell), cz = Math.floor(p.pz / cell);
    for (let gx = cx - 1; gx <= cx + 1; gx++) for (let gz = cz - 1; gz <= cz + 1; gz++) {
      for (const j of grid.get(`${gx},${gz}`) ?? []) {
        if (j === i) continue;
        const q = pos[j];
        const dx = p.px - q.px, dz = p.pz - q.pz, d = Math.hypot(dx, dz);
        if (d >= TOWN.separation) continue;
        // 重叠在同一点上：按 id 定一个方向（编号小的让路）
        const ux = d > 1e-6 ? dx / d : (p.id < q.id ? 1 : -1), uz = d > 1e-6 ? dz / d : 0;
        const share = q.movable ? 0.5 : 1;
        fx += ux * (TOWN.separation - d) * share;
        fz += uz * (TOWN.separation - d) * share;
      }
    }
    if (player) {
      const dx = p.px - player.x, dz = p.pz - player.z, d = Math.hypot(dx, dz);
      if (d < TOWN.playerRadius) {
        const ux = d > 1e-6 ? dx / d : 1, uz = d > 1e-6 ? dz / d : 0;
        fx += ux * (TOWN.playerRadius - d);
        fz += uz * (TOWN.playerRadius - d);
      }
    }
    const f = Math.hypot(fx, fz);
    let nx = p.nudge?.x ?? 0, nz = p.nudge?.z ?? 0;
    if (f > 1e-6) {
      const k = Math.min(TOWN.pushPerFrame, f) / f;
      nx += fx * k; nz += fz * k;
    } else {
      nx *= TOWN.nudgeDecay; nz *= TOWN.nudgeDecay;
    }
    const n = Math.hypot(nx, nz);
    if (n > TOWN.maxNudge) { nx *= TOWN.maxNudge / n; nz *= TOWN.maxNudge / n; }
    // 让开的位置必须能站：不能就只缩回一半，再不行就不挪
    let chosen = { x: nx, z: nz };
    if (!walkable({ x: p.x + nx, z: p.z + nz })) {
      const half = { x: nx * 0.5, z: nz * 0.5 };
      chosen = walkable({ x: p.x + half.x, z: p.z + half.z }) ? half : { x: 0, z: 0 };
    }
    out.set(p.id, chosen);
  }
  return out;
}

/**
 * 分档（规则 3）。list：[{ id, distance, inView }]；current：上一帧的档（Map id → 'A'|'B'|'C'）。返回新的 Map。
 */
export function assignTiers(list, current = new Map(), { tierA = TOWN.tierA, radius = TOWN.tierARadius, hysteresis = TOWN.hysteresis } = {}) {
  const tiers = new Map();
  const visible = list.filter((c) => c.inView);
  for (const c of list) if (!c.inView) tiers.set(c.id, 'C');
  visible
    .map((c) => ({ c, d: c.distance - (current.get(c.id) === 'A' ? hysteresis : 0) }))
    .sort((a, b) => a.d - b.d || (a.c.id < b.c.id ? -1 : 1))
    .forEach(({ c, d }, i) => tiers.set(c.id, i < tierA && d <= radius ? 'A' : 'B'));
  return tiers;
}

/**
 * 挑一对来聊天（规则 4）。candidates：[{ id, x, z, idleLeft, inView }]（只放此刻闲着的）；player：{x, z}；
 * cooldown：Map pairKey → 上次说话的时刻；wall：此刻。返回 { a, b, key, start }（a 先开口）或 null。说什么由 C# 的模型定。
 * 第三轮（人物关系）：allowed(a, b) 为假的一对不凑（互相厌恶的两位）；score(a, b, d) 给了就按它挑最小的（同样近时关系好的先），
 * 没给就挑最近的一对。
 */
export function pickChat(candidates, player, cooldown, wall, { allowed = null, score = null } = {}) {
  const ok = candidates.filter((c) => c.inView && c.idleLeft >= TOWN.chatNeedMs && Math.hypot(c.x - player.x, c.z - player.z) <= TOWN.chatPlayerRadius);
  let best = null;
  for (let i = 0; i < ok.length; i++) for (let j = i + 1; j < ok.length; j++) {
    const a = ok[i], b = ok[j];
    const d = Math.hypot(a.x - b.x, a.z - b.z);
    if (d > TOWN.chatRadius) continue;
    const key = a.id < b.id ? `${a.id}|${b.id}` : `${b.id}|${a.id}`;
    const last = cooldown.get(key);
    if (last != null && wall - last < TOWN.chatPairCooldownMs) continue;
    if (allowed && !allowed(a.id, b.id)) continue;
    const s = score ? score(a.id, b.id, d) : d;
    if (!best || s < best.s) best = { a, b, key, d, s };
  }
  if (!best) return null;
  const [first, second] = best.a.id < best.b.id ? [best.a, best.b] : [best.b, best.a];
  return { a: first.id, b: second.id, key: best.key, start: wall };
}

/**
 * C# 说闲聊结束了（ambient-end）之后，多久再挑下一对：没配模型——这座城里不再挑（没有本地台词可退）；超限——五分钟；
 * 正忙、失败——半分钟、一分钟；说完了（ok）——照常（半秒后再看，同一对有三分钟的冷却）。返回下一次挑的时刻，Infinity 是不再挑。
 */
export function nextChatAfter(status, wall) {
  switch (status) {
    case 'nomodel': return Number.POSITIVE_INFINITY;
    case 'limited': return wall + 5 * 60_000;
    case 'busy': return wall + 30_000;
    case 'failed': return wall + 60_000;
    default: return wall + 500;
  }
}

/** 跟你打招呼的那一句（按种子挑一种说法）。 */
export function greetLine(seed, greet, wall) {
  const list = String(greet ?? '').split('|').map((s) => s.trim()).filter(Boolean);
  if (list.length === 0) return '';
  return list[(seed + Math.floor(wall / TOWN.greetCooldownMs)) % list.length];
}
