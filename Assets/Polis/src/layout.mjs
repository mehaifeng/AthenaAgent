// 城邦的空间布局（纯逻辑）：账本里的地块 → 世界坐标、建筑朝向、站立点，以及沿街道的走法。
// 单位是米。格子 (x, z) 的中心在 (x·BLOCK, z·BLOCK)；街道在相邻地块之间（半格处）。z 向南增大，南边是海。
// M2：海边第 z = 2 行的公共地块是伸进海里的石台（civic-layout.mjs）——站立点在台上两排底座之间的过道，
// 从码头大街沿石台东侧的步道走上去；橄榄林的台地从 publicSites.grove 起向西排，站立点在台地北沿。
// 你用键盘走路（controls.mjs）：walkable() 说哪里能站——地面（floor）由这里定：陆地、海里的石台和栈桥；
// 挡路的东西（obstacles）由世界按每件资产的足迹盖出来的占用栅格回答（footprint.mjs，M3 决定 1）。
// 没有世界的时候（纯逻辑的测试）退回按地块算：有房子的地块整块不能进，四边留一条贴墙的路。
import { TERRACE, VENUES, groveTerraces, venueSlotLocal, SLOTS_PER_VENUE } from './civic-layout.mjs';

export const BLOCK = 18;           // 一个街区：约 12.5 米的地块 + 5.5 米的街道
export const STREET = 5.5;
export const PLOT = BLOCK - STREET;
// 雅典娜走路的速度（米 / 秒）。M0–M2 她以 12 米 / 秒滑行（真实步速跟不上模型的节奏），用户实测说像女鬼在地上飘（2026-10-11），
// M3 改成疾步走：3.2 米 / 秒、步幅 1.6 米（一步半秒），超过 athena.ATHENA.maxWalkMs（10 秒，约 32 米）的路化作一道光瞬移——
// 神本来就能瞬移
export const SPEED = 3.2;
export const ATHENA_STRIDE = 1.6;
export const PLAYER_SPEED = 6;

/** 码头石岸的外沿离码头大街中线多远（world.mjs 在这里砌岸；再往南是海）。 */
export const SHORE_OFFSET = STREET / 2 + 0.2;

/**
 * 整张地图是一块有限的矩形（2026-10-12，用户：地图是有限面积的矩形，随工作区规模自适应；矩形外是跟着主题的纯色）。
 * 陆地三面在城的范围（最外一圈街道、码头大街向西接长到橄榄林）之外再留 MAP_MARGIN——城外那一圈散种的橄榄与柏树
 * 落在最外一条街外 0.35 个地块之内，树冠再宽两米；南边带一片海，到码头大街以南 SEA_REACH：船出海最远到码头以南 41.5 米，
 * 船身半长 3.6 米，场馆的石台到码头以南 15 米。
 */
export const MAP_MARGIN = 8;
export const SEA_REACH = 50;

/**
 * 城郊（第四轮需求 5，2026-10-15）：最北一排地块（z = −ring − 1，城外，任何文件夹都不会占：生长次序只给到 ring 圈）是农田与牧场——
 * 西头一块牧场、往东最多 COUNTRY_FIELDS 块农田（城门两侧的城墙挡着的那两块不用）；能走的范围往北、往东、往西各多 COUNTRY 米，
 * 把这一排地块（连同两头的那一列）整个圈进来（原来只圈到地块中线）。
 */
export const COUNTRY = 7.75;
export const COUNTRY_FIELDS = 3;

/** 港口的栈桥：码头正南、宽 3.2 米、伸出 14 米（art/civic.mjs 的 buildHarbor）。 */
export const PIER = Object.freeze({ halfWidth: 1.45, length: 14 });

/** 房子占着的地块，四边各留这么宽能走（贴着墙走，不穿墙）。 */
const WALL_MARGIN = 0.6;

/** 有房子的公共地块（广场、港口是开阔地，石台另算）。 */
const SOLID_PUBLIC = new Set(['temple', 'library', 'forge', 'market']);

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
    const site = place(plot.x, plot.z, facingFor(plot.x, plot.z), null);
    if (plot.z >= seaZ) {
      // 港前的石台：站在两排底座之间的过道正中
      site.terrace = true;
      site.stand = { x: site.cx, z: site.cz + TERRACE.aisle };
    }
    plots.set(name, site);
  }
  // 橄榄林的台地：第 k 块在 grove 那块地以西 k 格；站立点在台地北沿正中（树占满了台面）
  const groveOrigin = publicSites.grove ?? null;
  const groveCount = groveTerraces(fixture.groveTrees ?? 0);
  for (let k = 0; groveOrigin && k < Math.max(groveCount, 1); k++) {
    const site = place(groveOrigin.x - k, groveOrigin.z, [0, 1], null);
    site.terrace = true;
    site.grove = k;
    site.stand = { x: site.cx, z: site.cz - PLOT / 2 + 0.9 };
    plots.set(`grove:${k}`, site);
  }

  // 城郊：最北一排地块里西头一块牧场、往东几块农田（站点：pasture、fields:k，fields 是第一块）。站点在地块南沿往里 1.2 米的正中
  const countryZ = -ring - 1;
  const country = [];
  // 城门（北街上 x = 0.5 格，两侧各一段 10 米的城墙）挡着的那两块不用：田要从南沿走得进去
  const gateX = 0.5 * BLOCK;
  for (let x = -ring - 1; x <= ring + 1 && country.length < 1 + COUNTRY_FIELDS; x++) {
    if (Math.abs(x * BLOCK - gateX) < 12) continue;
    const site = place(x, countryZ, [0, 1], null);
    site.country = country.length === 0 ? 'pasture' : 'field';
    site.stand = { x: site.cx, z: site.cz + PLOT / 2 - 1.2 };   // 离街口的路灯（地块角上往里 0.45 米）一米远
    if (site.country === 'pasture') plots.set('pasture', site);
    else { site.field = country.length - 1; plots.set(`fields:${site.field}`, site); }
    country.push(site);
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

  // 广场上闲逛的六个点：半径 2.6 米的一圈。M3：广场的家具（长凳、委托板、讲台、日晷、方柱头像）都挡路了，
  // 4.6 米的那一圈有三个点正落在长凳、讲台和委托板上——雅典娜站过的每个地方你都要能走到（断言）
  const wanderSpots = [];
  for (let k = 0; k < 6; k++) {
    const a = (k / 6) * Math.PI * 2 + 0.4;
    wanderSpots.push({ x: Math.cos(a) * 2.6, z: Math.sin(a) * 2.6 });
  }

  function sitePoint(site) {
    if (site === 'agora') return { x: 0, z: 0 };
    if (site === 'harbor') return { ...harborPoint };
    if (site === 'gate') return { ...gate };
    if (site.startsWith('agora:')) return { ...wanderSpots[Number(site.slice(6)) % wanderSpots.length] };
    if (site === 'fields') return sitePoint('fields:0');
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
  // 码头大街比城里的街网长：港前的石台（金库在 x = 2、橄榄林一路向西）可能在城的这一圈之外，
  // 只把码头那一行接长（多出来的交叉口只连在码头上），不在空地上铺整片街网。
  const terraceXs = [...plots.values()].filter((p) => p.terrace).map((p) => (p.grove != null ? p.stand.x : p.cx + TERRACE.sideWalk));
  const qMin = Math.min(iMin, ...terraceXs.map((x) => Math.floor(x / BLOCK - 0.5)));
  const qMax = Math.max(iMax, ...terraceXs.map((x) => Math.floor(x / BLOCK - 0.5) + 1));
  const quayKey = (i) => (i >= iMin && i <= iMax ? nodeKey(i, jMax) : `q:${i}`);

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
    for (let i = qMin; i < qMax; i++) {
      if (i >= iMin && i < iMax) continue;
      add(quayKey(i), quayKey(i + 1), { x: X(i), z: Z(jMax) }, { x: X(i + 1), z: Z(jMax) });
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
    const plot = plots.get(site);
    if (plot?.terrace) {
      // 石台：从过道走到东侧步道，再沿步道北上到码头大街（橄榄林的台地直接从北沿上街）。
      // 挂在码头那一行上（包括接长的那几段），不挂到别的街上去
      const sx = plot.grove != null ? plot.stand.x : plot.cx + TERRACE.sideWalk;
      const i0 = Math.min(Math.max(Math.floor(sx / BLOCK - 0.5), qMin), qMax - 1);
      const point = { x: Math.min(Math.max(sx, X(qMin)), X(qMax)), z: quayZ };
      const via = plot.grove != null ? [point] : [{ x: sx, z: plot.stand.z }, point];
      return [quayKey(i0), quayKey(i0 + 1)].map((node) => ({ via, node }));
    }
    const a = attach(p);
    return a.ends.map((node) => ({ via: [a.point], node }));
  }

  function route(from, to) {
    if (from === 'fields') from = 'fields:0';
    if (to === 'fields') to = 'fields:0';
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

  // —— 人能站在哪（键盘走路）：城的范围之内；陆地上，房子占着的地块（文件夹的建筑、神庙、图书馆、锻炉、市集）
  // 不能进，四边留一条贴墙的路；海里只有石台（场馆北半边是建筑、台上的底座也挡路，橄榄林的台地都能走）和港口的栈桥 ——
  const shoreZ = quayZ + SHORE_OFFSET;
  const walkBounds = {
    // 城郊（第四轮）：东西两头的那一列地块也整块圈进来（原来只圈到地块中线）——最北一排两头的牧场、农田才走得进去
    minX: Math.min(X(iMin) - BLOCK / 2 - COUNTRY, X(qMin) - BLOCK / 2),
    maxX: Math.max(X(iMax) + BLOCK / 2 + COUNTRY, X(qMax) + BLOCK / 2),
    minZ: Z(jMin) - BLOCK / 2 - COUNTRY,
  };
  const pedestals = Array.from({ length: SLOTS_PER_VENUE }, (_, i) => venueSlotLocal(i));
  const venueSites = new Set(Object.values(VENUES));
  // 场馆的建筑占石台北半边、东西两侧各留一条步道（最宽的彩绘柱廊外墙在 ±5.4 米）
  const venueHalfWidth = TERRACE.sideWalk - 0.2;

  /** 脚下有没有地：陆地、海里的石台（场馆的与橄榄林的）和港口的栈桥。城的范围之外没有。 */
  function floor(p) {
    if (!p || !Number.isFinite(p.x) || !Number.isFinite(p.z)) return false;
    if (p.x < walkBounds.minX || p.x > walkBounds.maxX || p.z < walkBounds.minZ) return false;
    if (p.z <= shoreZ || (Math.abs(p.x - harborPoint.x) <= PIER.halfWidth && p.z <= quayZ + PIER.length)) return true;
    for (const plot of plots.values()) {
      if (!plot.terrace) continue;
      if (Math.abs(p.x - plot.cx) <= PLOT / 2 && Math.abs(p.z - plot.cz) <= PLOT / 2) return true;
    }
    return false;
  }

  /**
   * 没有占用栅格时的挡路规则（按地块）：有房子的地块整块不能进、四边留一条贴墙的路；场馆的建筑占石台北半边，台上的底座也挡。
   * 世界建好之后换成按足迹的栅格（setObstacles）——这里只是纯逻辑测试与世界建好之前那一瞬的退路。
   */
  function plotObstacles(p) {
    for (const [key, plot] of plots) {
      const lx = p.x - plot.cx, lz = p.z - plot.cz;
      if (Math.abs(lx) > PLOT / 2 || Math.abs(lz) > PLOT / 2) continue;
      if (plot.terrace) {
        if (!venueSites.has(key)) continue;
        if (Math.abs(lx) < venueHalfWidth && lz < TERRACE.buildingFront - 0.4) return true;
        for (const s of pedestals) if (Math.hypot(lx - s.x, lz - s.z) < 0.55) return true;
        continue;
      }
      const solid = key.startsWith('b:') || SOLID_PUBLIC.has(key);
      if (solid && Math.abs(lx) < PLOT / 2 - WALL_MARGIN && Math.abs(lz) < PLOT / 2 - WALL_MARGIN) return true;
    }
    return false;
  }
  let obstacles = plotObstacles;

  function walkable(p) {
    return floor(p) && !obstacles(p);
  }

  /** 换一套挡路规则：世界把按足迹盖出来的占用栅格接进来；传 null 退回按地块算。 */
  function setObstacles(fn) {
    obstacles = typeof fn === 'function' ? fn : plotObstacles;
  }

  /**
   * 给一座新建筑登记地块（运行期新建的文件夹）。只接受落在当前这一圈之内的地块：街道图和城市边界都按圈数建，
   * 地块在更外一圈时返回 false，调用方整城重建。
   */
  function addBuildingPlot(b) {
    if (Math.abs(b.plot.x) > ring || Math.abs(b.plot.z) > ring || b.plot.z >= seaZ) return false;
    plots.set(`b:${b.key}`, place(b.plot.x, b.plot.z, facingFor(b.plot.x, b.plot.z), b));
    return true;
  }

  return {
    plots, ring, gate, harborPoint, quayZ, seaZ, shoreZ, wanderSpots, groveOrigin, country,
    quayRange: { minX: X(qMin), maxX: X(qMax) },
    mapRect: {
      minX: walkBounds.minX - MAP_MARGIN, maxX: walkBounds.maxX + MAP_MARGIN,
      minZ: walkBounds.minZ - MAP_MARGIN, maxZ: quayZ + SEA_REACH,
    },
    bounds: { minX: X(iMin) - BLOCK / 2, maxX: X(iMax) + BLOCK / 2, minZ: Z(jMin) - BLOCK / 2, maxZ: quayZ + 30 },
    sitePoint, route, travelMs, walkable, floor, setObstacles, plotObstacles, addBuildingPlot,
    get obstacles() { return obstacles; },
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
