// 城邦的空间布局（纯逻辑）：账本里的地块 → 世界坐标、建筑朝向、站立点，以及沿街道的走法。
// 单位是米。格子 (x, z) 的中心在 (x·BLOCK, z·BLOCK)；街道在相邻地块之间（半格处）。z 向南增大，南边是海。

export const BLOCK = 18;           // 一个街区：约 12.5 米的地块 + 5.5 米的街道
export const STREET = 5.5;
export const PLOT = BLOCK - STREET;
export const SPEED = 10;           // 雅典娜滑行的速度（米 / 秒）：真实步速跟不上模型的节奏，神本来就走得快
export const PLAYER_SPEED = 6;

/**
 * 建筑正面一律朝南（朝镜头）。第一版让建筑朝向广场，结果广场以南的柱廊书库从默认镜头看过去只剩一堵后墙，
 * 认不出是什么（M0 截图审查）。等距视角的游戏都让正面朝向镜头，可读性比"围着广场"重要。返回 [dx, dz]。
 */
export function facingFor() {
  return [0, 1];
}

/** 朝向向量 → 绕 y 轴的旋转角（模型默认正面朝 +z）。 */
export function yawFor([dx, dz]) {
  return Math.atan2(dx, dz);
}

export function createLayout(fixture) {
  const seaZ = fixture.seaStartsAtZ ?? 2;
  const plots = new Map();
  let ring = 1;
  for (const b of fixture.buildings ?? []) {
    plots.set(`b:${b.key}`, place(b.plot.x, b.plot.z, facingFor(b.plot.x, b.plot.z), b));
    ring = Math.max(ring, Math.abs(b.plot.x), Math.abs(b.plot.z));
  }
  for (const v of fixture.vacant ?? []) ring = Math.max(ring, Math.abs(v.plot.x), Math.abs(v.plot.z));
  const publicSites = fixture.publicSites ?? {};
  for (const [name, plot] of Object.entries(publicSites)) {
    plots.set(name, place(plot.x, plot.z, facingFor(plot.x, plot.z), null));
  }

  // 街道网格的范围：比最外一圈再多一条街
  const iMin = -ring - 1, iMax = ring;          // 竖街 x = (i + 0.5)·BLOCK
  const jMin = -ring - 1, jMax = seaZ - 1;      // 横街 z = (j + 0.5)·BLOCK；最南一条是码头
  const quayZ = (jMax + 0.5) * BLOCK;
  const gate = { x: 0.5 * BLOCK, z: (jMin + 0.5) * BLOCK - 4 };
  const harborPoint = { x: 0, z: quayZ + 11 };

  function place(x, z, facing, building) {
    const cx = x * BLOCK, cz = z * BLOCK;
    const reach = PLOT / 2 + 0.8;   // 站在门前、街沿上
    return { x, z, cx, cz, facing, yaw: yawFor(facing), building, stand: { x: cx + facing[0] * reach, z: cz + facing[1] * reach } };
  }

  const wanderSpots = [];
  for (let k = 0; k < 6; k++) {
    const a = (k / 6) * Math.PI * 2 + 0.4;
    wanderSpots.push({ x: Math.cos(a) * 4.6, z: Math.sin(a) * 4.6 });
  }

  function sitePoint(site) {
    if (site === 'agora') return { x: 0, z: 0 };
    if (site === 'harbor') return { ...harborPoint };
    if (site === 'gate') return { ...gate };
    if (site.startsWith('agora:')) return { ...wanderSpots[Number(site.slice(6)) % wanderSpots.length] };
    if (site.startsWith('pt:')) {
      const [x, z] = site.slice(3).split(',').map(Number);
      return { x, z };
    }
    const plot = plots.get(site);
    if (!plot) throw new Error(`layout: unknown site ${site}`);
    return { ...plot.stand };
  }

  // —— 街道图：交叉口 + 端点挂到最近的一段街上 ——
  const nodeKey = (i, j) => `${i},${j}`;
  const X = (i) => (i + 0.5) * BLOCK;
  const Z = (j) => (j + 0.5) * BLOCK;

  function baseGraph() {
    const g = new Map();
    const add = (a, b, pa, pb) => {
      const d = Math.hypot(pa.x - pb.x, pa.z - pb.z);
      if (!g.has(a)) g.set(a, { p: pa, edges: [] });
      if (!g.has(b)) g.set(b, { p: pb, edges: [] });
      g.get(a).edges.push([b, d]);
      g.get(b).edges.push([a, d]);
    };
    for (let i = iMin; i <= iMax; i++) {
      for (let j = jMin; j <= jMax; j++) {
        if (i < iMax) add(nodeKey(i, j), nodeKey(i + 1, j), { x: X(i), z: Z(j) }, { x: X(i + 1), z: Z(j) });
        if (j < jMax) add(nodeKey(i, j), nodeKey(i, j + 1), { x: X(i), z: Z(j) }, { x: X(i), z: Z(j + 1) });
      }
    }
    return g;
  }
  const graph = baseGraph();

  // 把一个点挂到最近的一段街上，返回挂点和它连着的交叉口
  function attach(p) {
    const fi = Math.min(Math.max(p.x / BLOCK - 0.5, iMin), iMax);
    const fj = Math.min(Math.max(p.z / BLOCK - 0.5, jMin), jMax);
    const ci = Math.round(fi), cj = Math.round(fj);
    // 横街候选：最近的横街，x 夹在网格内
    const hx = Math.min(Math.max(p.x, X(iMin)), X(iMax));
    const h = { x: hx, z: Z(cj) };
    const v = { x: X(ci), z: Math.min(Math.max(p.z, Z(jMin)), Z(jMax)) };
    const dh = Math.hypot(p.x - h.x, p.z - h.z), dv = Math.hypot(p.x - v.x, p.z - v.z);
    if (dh <= dv) {
      const i0 = Math.min(Math.floor(hx / BLOCK - 0.5), iMax - 1);
      return { point: h, ends: [nodeKey(i0, cj), nodeKey(i0 + 1, cj)] };
    }
    const j0 = Math.min(Math.floor(v.z / BLOCK - 0.5), jMax - 1);
    return { point: v, ends: [nodeKey(ci, j0), nodeKey(ci, j0 + 1)] };
  }

  // 特殊入口：广场是开阔地，从中心直走到四角；港口沿码头的栈桥走；城门接最北一条横街
  function entries(site, p) {
    if (site === 'agora' || site.startsWith('agora:')) {
      return [[-1, -1], [0, -1], [-1, 0], [0, 0]].map(([i, j]) => ({ via: [], node: nodeKey(i, j) }));
    }
    if (site === 'harbor') {
      const a = attach({ x: 0, z: quayZ });
      return a.ends.map((node) => ({ via: [a.point], node }));
    }
    if (site === 'gate') {
      const top = { x: X(0), z: Z(jMin) };
      return [{ via: [top], node: nodeKey(0, jMin) }];
    }
    const a = attach(p);
    return a.ends.map((node) => ({ via: [a.point], node }));
  }

  function route(from, to) {
    const a = sitePoint(from), b = sitePoint(to);
    if (from === to) return [a];
    const nearAgora = (s) => s === 'agora' || s.startsWith('agora:');
    if (nearAgora(from) && nearAgora(to)) return [a, b];   // 广场上直走
    const starts = entries(from, a);
    const goals = entries(to, b);
    // Dijkstra：起点是"起点 → 挂点 → 交叉口"，终点同理
    const dist = new Map(), prev = new Map(), done = new Set();
    const pq = [];
    for (const s of starts) {
      const pts = [a, ...s.via, graph.get(s.node).p];
      const d = polyLength(pts);
      if (!dist.has(s.node) || d < dist.get(s.node)) {
        dist.set(s.node, d);
        prev.set(s.node, { start: pts });
        pq.push([d, s.node]);
      }
    }
    const goalTail = new Map(goals.map((g) => [g.node, [graph.get(g.node).p, ...g.via.slice().reverse(), b]]));
    let best = null;
    while (pq.length) {
      pq.sort((x, y) => x[0] - y[0]);
      const [d, n] = pq.shift();
      if (done.has(n)) continue;
      done.add(n);
      if (goalTail.has(n)) {
        const total = d + polyLength(goalTail.get(n));
        if (!best || total < best.total) best = { total, node: n };
      }
      for (const [m, w] of graph.get(n).edges) {
        const nd = d + w;
        if (!dist.has(m) || nd < dist.get(m)) {
          dist.set(m, nd);
          prev.set(m, { from: n });
          pq.push([nd, m]);
        }
      }
    }
    if (!best) return [a, b];
    const chain = [];
    let n = best.node;
    for (;;) {
      chain.push(graph.get(n).p);
      const pr = prev.get(n);
      if (pr.start) { chain.push(...pr.start.slice(0, -1).reverse()); break; }
      n = pr.from;
    }
    chain.reverse();
    const tail = goalTail.get(best.node).slice(1);
    return dedupe([...chain, ...tail]);
  }

  function travelMs(from, to, speed = SPEED) {
    return (polyLength(route(from, to)) / speed) * 1000;
  }

  /** 点到哪：落在某座建筑的地块里就去它门前，否则就是那个点。 */
  function snapClick(p) {
    for (const [key, plot] of plots) {
      if (key === 'agora') continue;   // 广场是开阔地，点哪儿就去哪儿
      if (Math.abs(p.x - plot.cx) < PLOT / 2 && Math.abs(p.z - plot.cz) < PLOT / 2) return key;
    }
    return `pt:${p.x.toFixed(2)},${p.z.toFixed(2)}`;
  }

  return {
    plots, ring, gate, harborPoint, quayZ, seaZ, wanderSpots,
    bounds: { minX: X(iMin) - BLOCK / 2, maxX: X(iMax) + BLOCK / 2, minZ: Z(jMin) - BLOCK / 2, maxZ: quayZ + 30 },
    sitePoint, route, travelMs, snapClick,
  };
}

export function polyLength(pts) {
  let d = 0;
  for (let k = 1; k < pts.length; k++) d += Math.hypot(pts[k].x - pts[k - 1].x, pts[k].z - pts[k - 1].z);
  return d;
}

function dedupe(pts) {
  const out = [];
  for (const p of pts) {
    const last = out[out.length - 1];
    if (!last || Math.hypot(last.x - p.x, last.z - p.z) > 1e-6) out.push(p);
  }
  return out;
}

/** 沿折线走到比例 f（0..1）处的位置和朝向。 */
export function pointAlong(pts, f) {
  if (pts.length === 1) return { x: pts[0].x, z: pts[0].z, dir: 0 };
  const total = polyLength(pts);
  let want = Math.min(1, Math.max(0, f)) * total;
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
