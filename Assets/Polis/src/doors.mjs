// 门（第三轮需求 2，2026-10-13，用户：建筑要做可以被 NPC 使用的门，玩家无法进去；NPC 开门关门、走进走出）。纯逻辑，Node 单测 doors.test.mjs。
// 门扇、门洞的样子在 art（kit.door 登记、world 建门扇）；这里只算：门阶在哪、从门阶怎么走到街上、一次进出门里每一刻人在哪、门开着几分。
//
// 规则（每一条都有断言）：
//   1. 会开的门只有地面上的门：门槛不高于 DOOR.maxSill（民居 0.25、作坊 0.22、货栈 0）；台基上的金库、离地的粮仓、二层的廊门不算。
//   2. 门阶：从门洞中心沿朝外的法线，在 [stepMin, stepMax] 里找第一个能站的点（也试左右偏一点）；找不到这扇门就当没有。
//   3. 门阶到街上的路：0.2 米的格子上 A*（八方向），再拉直；整条路每 0.25 米都能站（和居民的路同一条规矩，citizens.routeClear）。
//      走不到街上的门也当没有。门洞本身在建筑的足迹里（挡路的），所以你进不去——进出门的那一小段（门里 ↔ 门阶）只有住在里面的人走。
//   4. 一次进出门 DOOR.transitMs：出门——开门（openMs）、从门里走到门阶（passMs）、从门阶走到门前站的地方，门在他跨出来之后关上；
//      进门反过来——走到门阶，门在他到之前打开，走进门里，进去之后关上、人藏起来。门开几分只由此刻谁在门里进出决定（同一刻永远是同一个样子）。

/** 整条折线每 step 米都能站（和 citizens.routeClear 同一条规矩；这里自己写一份，免得两个模块互相导入）。 */
export function routeClear(pts, walkable, step = 0.25) {
  for (let k = 1; k < pts.length; k++) {
    const a = pts[k - 1], b = pts[k];
    const n = Math.max(1, Math.ceil(Math.hypot(b.x - a.x, b.z - a.z) / step));
    for (let i = 0; i <= n; i++) {
      const u = i / n;
      if (!walkable({ x: a.x + (b.x - a.x) * u, z: a.z + (b.z - a.z) * u })) return false;
    }
  }
  return true;
}

export const DOOR = Object.freeze({
  maxSill: 0.3,
  stepMin: 0.35,
  stepMax: 1.6,
  inside: 0.7,          // 走进门里多深（门洞后面，之后藏起来）
  grid: 0.2,
  pad: 2.0,             // A* 的范围：建筑的地块四边再往外这么多（够到街沿上的站点）
  maxNodes: 40_000,
  openMs: 350,
  passMs: 900,
  legMs: 950,           // 门阶 ↔ 门前站的地方（最远 1.2 米）
  closeMs: 350,
  transitMs: 2200,      // open 350 + pass 900 + leg 950（关门和走那一小段同时）
  openAngle: 1.6,       // 门扇开到 92°（向屋里开）
  speed: 1.3,
});

/** 门的朝外法线（门在南墙上：建筑一律朝南，layout.facingFor）。 */
const outward = (door) => ({ x: door.nx ?? 0, z: door.nz ?? 1 });

/** 规则 1：这扇门能不能让人走进去。 */
export const usableSill = (door) => (door.y ?? 0) <= DOOR.maxSill + 1e-6;

/** 门里那一点（门洞后面 DOOR.inside）。 */
export function insidePoint(door) {
  const n = outward(door);
  return { x: door.x - n.x * DOOR.inside, z: door.z - n.z * DOOR.inside };
}

/** 规则 2：门阶。 */
export function findDoorstep(door, walkable) {
  const n = outward(door);
  const side = { x: n.z, z: -n.x };
  for (let d = DOOR.stepMin; d <= DOOR.stepMax + 1e-9; d += 0.05) {
    for (const lateral of [0, 0.15, -0.15, 0.3, -0.3, 0.45, -0.45]) {
      const p = { x: door.x + n.x * d + side.x * lateral, z: door.z + n.z * d + side.z * lateral };
      if (!walkable(p)) continue;
      // 门阶到门洞那一段不查（门洞在足迹里），但门阶本身要和法线上那条线差不多（不绕到墙角另一面去）
      return { x: p.x, z: p.z };
    }
  }
  return null;
}

/** 二叉堆（A* 的开放表）。 */
function heap() {
  const a = [];
  return {
    get size() { return a.length; },
    push(item) {
      a.push(item);
      let i = a.length - 1;
      while (i > 0) {
        const p = (i - 1) >> 1;
        if (a[p].f <= a[i].f) break;
        [a[p], a[i]] = [a[i], a[p]];
        i = p;
      }
    },
    pop() {
      const top = a[0];
      const last = a.pop();
      if (a.length) {
        a[0] = last;
        let i = 0;
        for (;;) {
          const l = 2 * i + 1, r = l + 1;
          let m = i;
          if (l < a.length && a[l].f < a[m].f) m = l;
          if (r < a.length && a[r].f < a[m].f) m = r;
          if (m === i) break;
          [a[m], a[i]] = [a[i], a[m]];
          i = m;
        }
      }
      return top;
    },
  };
}

/** 拉直：从头开始，能直线到达的最远一点就连过去（每 0.1 米都能站）。 */
export function stringPull(points, walkable) {
  if (points.length <= 2) return points.slice();
  const out = [points[0]];
  let i = 0;
  while (i < points.length - 1) {
    let j = points.length - 1;
    while (j > i + 1 && !routeClear([points[i], points[j]], walkable, 0.1)) j--;
    out.push(points[j]);
    i = j;
  }
  return out;
}

/**
 * 规则 3：门阶 → 街上的站点。bounds：{ minX, maxX, minZ, maxZ }（A* 只在这里面找）。返回拉直后的折线（第一点是门阶、最后一点是 goal），走不到为 null。
 */
export function planApproach(step, goal, walkable, bounds) {
  if (!walkable(step) || !walkable(goal)) return null;
  if (routeClear([step, goal], walkable)) return [step, goal];
  const g = DOOR.grid;
  const nx = Math.max(1, Math.ceil((bounds.maxX - bounds.minX) / g)), nz = Math.max(1, Math.ceil((bounds.maxZ - bounds.minZ) / g));
  if (nx * nz > DOOR.maxNodes) return null;
  const cell = (p) => ({ i: Math.min(nx - 1, Math.max(0, Math.round((p.x - bounds.minX) / g))), j: Math.min(nz - 1, Math.max(0, Math.round((p.z - bounds.minZ) / g))) });
  const at = (i, j) => ({ x: bounds.minX + i * g, z: bounds.minZ + j * g });
  const key = (i, j) => j * nx + i;
  const s = cell(step), t = cell(goal);
  const free = new Map();
  const ok = (i, j) => {
    const k = key(i, j);
    if (!free.has(k)) free.set(k, walkable(at(i, j)));
    return free.get(k);
  };
  const gScore = new Map([[key(s.i, s.j), 0]]);
  const from = new Map();
  const open = heap();
  const h = (i, j) => Math.hypot(i - t.i, j - t.j) * g;
  open.push({ i: s.i, j: s.j, f: h(s.i, s.j) });
  const closed = new Set();
  let found = false;
  while (open.size) {
    const cur = open.pop();
    const ck = key(cur.i, cur.j);
    if (closed.has(ck)) continue;
    closed.add(ck);
    if (cur.i === t.i && cur.j === t.j) { found = true; break; }
    for (let di = -1; di <= 1; di++) for (let dj = -1; dj <= 1; dj++) {
      if (!di && !dj) continue;
      const i = cur.i + di, j = cur.j + dj;
      if (i < 0 || j < 0 || i >= nx || j >= nz || !ok(i, j)) continue;
      // 斜着走不切墙角：两边的格子都要能站
      if (di && dj && (!ok(cur.i + di, cur.j) || !ok(cur.i, cur.j + dj))) continue;
      const k = key(i, j);
      if (closed.has(k)) continue;
      const tentative = gScore.get(ck) + Math.hypot(di, dj) * g;
      if (tentative < (gScore.get(k) ?? Infinity)) {
        gScore.set(k, tentative);
        from.set(k, ck);
        open.push({ i, j, f: tentative + h(i, j) });
      }
    }
  }
  if (!found) return null;
  const cells = [];
  for (let k = key(t.i, t.j); k != null; k = from.get(k)) cells.push(k);
  cells.reverse();
  const pts = [step, ...cells.slice(1, -1).map((k) => at(k % nx, Math.floor(k / nx))), goal];
  const pulled = stringPull(pts, walkable);
  return routeClear(pulled, walkable) ? pulled : null;
}

const clamp01 = (x) => Math.min(1, Math.max(0, x));
const ease = (f) => { const t = clamp01(f); return t * t * (3 - 2 * t); };

/**
 * 规则 4：一次进出门在第 t 毫秒（0…transitMs）——{ phase: 'pass'|'leg', f（这一段走了几成，0…1）, open（门开几分）, hidden（人在门里、藏起来）}。
 * 出门：门里 → 门阶（pass）→ 门前站的地方（leg）；进门：门前 → 门阶（leg）→ 门里（pass）。
 */
export function transitAt(kind, t) {
  const { openMs, passMs, legMs, closeMs, transitMs } = DOOR;
  const u = Math.min(transitMs, Math.max(0, t));
  if (kind === 'exit') {
    const open = u < openMs ? ease(u / openMs) : u < openMs + passMs ? 1 : 1 - ease((u - openMs - passMs) / closeMs);
    if (u < openMs) return { phase: 'pass', f: 0, open, hidden: false };
    if (u < openMs + passMs) return { phase: 'pass', f: ease((u - openMs) / passMs), open, hidden: false };
    return { phase: 'leg', f: ease((u - openMs - passMs) / legMs), open: Math.max(0, open), hidden: false };
  }
  // 进门：先走到门阶（门在他到之前 openMs 开始开），再走进去，进去之后关门、人藏起来
  const arrive = legMs;
  const open = u < arrive - openMs ? 0 : u < arrive ? ease((u - (arrive - openMs)) / openMs) : u < arrive + passMs ? 1 : 1 - ease((u - arrive - passMs) / closeMs);
  if (u < arrive) return { phase: 'leg', f: ease(u / legMs), open, hidden: false };
  if (u < arrive + passMs) return { phase: 'pass', f: ease((u - arrive) / passMs), open, hidden: false };
  return { phase: 'pass', f: 1, open: Math.max(0, open), hidden: true };
}

/** 门里 ↔ 门阶这一段上，离墙面多远处脚踩在门槛的高度（民居的石台基伸出墙面 0.15 米）：贴着墙是门槛高，0.35 米外是街面。 */
export function sillHeight(door, x, z) {
  const n = outward(door);
  const out = (x - door.x) * n.x + (z - door.z) * n.z;   // 离墙面多远（朝外为正）
  const y = door.y ?? 0;
  if (out <= 0.15) return y;
  if (out >= 0.35) return 0;
  return y * (1 - (out - 0.15) / 0.2);
}
