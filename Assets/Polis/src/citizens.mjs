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
 * @param {{ id: string, seed: number, home: string }} citizen
 * @param {{ sitePoint: (site: string) => {x:number,z:number}, route: (a: string, b: string) => Array<{x:number,z:number}>, walkable: (p) => boolean, hasSite?: (site: string) => boolean, neighbours?: string[] }} layout
 *   neighbours：城里各座建筑的站点（b:&lt;文件夹&gt;）——居民也去邻居家门口坐坐
 * @returns {{ id, home: {site, point, yaw}, dest: {site, point, yaw}|null, out: Array<{x,z}>|null, walkMs: number, offset: number }}
 */
export function planCitizen(citizen, layout) {
  const r = seeded(citizen.seed ^ 0x9e3779b9);
  const has = layout.hasSite ?? (() => true);
  const homeSite = has(citizen.home) ? citizen.home : 'agora:0';
  const homeStand = layout.sitePoint(homeSite);
  const homePoint = standNear(homeStand, r, TOWN.homeRing, layout.walkable);
  const homeYaw = r() * Math.PI * 2;
  const offset = Math.floor(r() * TOWN.periodMs);
  // 去处按种子排个顺序，取第一个走得到（一程不超过 maxWalkMs）的
  const order = [...DESTINATIONS, ...(layout.neighbours ?? [])].filter((d) => d !== homeSite && has(d))
    .map((d) => ({ d, k: r() }))
    .sort((a, b) => a.k - b.k)
    .map((x) => x.d);
  for (const site of order) {
    let path;
    try { path = layout.route(homeSite, site); } catch { continue; }
    if (!path || path.length === 0) continue;
    const destPoint = standNear(layout.sitePoint(site), r, TOWN.destRing, layout.walkable);
    const out = [homePoint, ...path, destPoint];
    const walkMs = (polyLength(out) / TOWN.speed) * 1000;
    if (walkMs > TOWN.maxWalkMs) continue;
    if (!routeClear(out, layout.walkable)) continue;
    return { id: citizen.id, home: { site: homeSite, point: homePoint, yaw: homeYaw }, dest: { site, point: destPoint, yaw: r() * Math.PI * 2 }, out, walkMs, offset };
  }
  return { id: citizen.id, home: { site: homeSite, point: homePoint, yaw: homeYaw }, dest: null, out: null, walkMs: 0, offset };
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
 * 他在墙钟 wall 这一刻在哪、干什么。返回 { x, z, yaw, action: 'idle'|'walk', walkT（步子的相位，毫秒）, idleLeft（这段待着还剩多久，走着时为 0）, at: 'home'|'dest'|'road' }。
 */
export function citizenAt(plan, wall) {
  const p = ((wall + plan.offset) % TOWN.periodMs + TOWN.periodMs) % TOWN.periodMs;
  const atHome = (left) => ({ x: plan.home.point.x, z: plan.home.point.z, yaw: plan.home.yaw, action: 'idle', walkT: 0, idleLeft: left, at: 'home' });
  if (!plan.dest) return atHome(Number.POSITIVE_INFINITY);
  const t1 = TOWN.homeMs, t2 = t1 + plan.walkMs, t3 = t2 + TOWN.stayMs, t4 = t3 + plan.walkMs;
  const length = polyLength(plan.out);
  if (p < t1) return atHome(t1 - p);
  if (p < t2) {
    const f = ease((p - t1) / plan.walkMs);
    const q = pointAlong(plan.out, f);
    return { x: q.x, z: q.z, yaw: q.dir, action: 'walk', walkT: ((length * f) / TOWN.speed) * 1000, idleLeft: 0, at: 'road' };
  }
  if (p < t3) return { x: plan.dest.point.x, z: plan.dest.point.z, yaw: plan.dest.yaw, action: 'idle', walkT: 0, idleLeft: t3 - p, at: 'dest' };
  if (p < t4) {
    const f = ease((p - t3) / plan.walkMs);
    const q = pointAlong(plan.out, 1 - f);
    return { x: q.x, z: q.z, yaw: q.dir + Math.PI, action: 'walk', walkT: ((length * f) / TOWN.speed) * 1000, idleLeft: 0, at: 'road' };
  }
  return atHome(TOWN.periodMs - p + TOWN.homeMs);
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
 */
export function pickChat(candidates, player, cooldown, wall) {
  const ok = candidates.filter((c) => c.inView && c.idleLeft >= TOWN.chatNeedMs && Math.hypot(c.x - player.x, c.z - player.z) <= TOWN.chatPlayerRadius);
  let best = null;
  for (let i = 0; i < ok.length; i++) for (let j = i + 1; j < ok.length; j++) {
    const a = ok[i], b = ok[j];
    const d = Math.hypot(a.x - b.x, a.z - b.z);
    if (d > TOWN.chatRadius) continue;
    const key = a.id < b.id ? `${a.id}|${b.id}` : `${b.id}|${a.id}`;
    const last = cooldown.get(key);
    if (last != null && wall - last < TOWN.chatPairCooldownMs) continue;
    if (!best || d < best.d) best = { a, b, key, d };
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
