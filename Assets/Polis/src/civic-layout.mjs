// 港前公共区与海图的布局（纯逻辑，不碰 three.js，Node 单测覆盖）。
//
// M2 的公共建筑在海边的第 z = 2 行（C# 的 PolisLayoutLedger.PublicSites）：账本的生长次序只给 z < 2 的地块，
// 这一行永远不会被文件夹占用——所以它们的位置和城一样稳定，城再怎么长也不用挪。每块地是一座伸进海里的石台：
// 北半边是建筑，南半边临水的步道上是两排底座（藏品的位次）；两侧各一条步道连到码头大街。
// 橄榄林从 (−2, 2) 那块台地起，沿海岸一块一块向西长：每块 16 棵（4 × 4），第 n 棵树永远在同一个位置。

export const PLOT_SIZE = 12.5;                 // 与 layout.mjs 的 PLOT 一致（BLOCK − STREET）
export const SLOTS_PER_VENUE = 12;             // 与 C# 的 PolisCollection.SlotsPerVenue 一致
export const TREES_PER_TERRACE = 16;

/** 三处场馆：页面协议里的名字 → 公共地块的名字（publicSites 里的键）。 */
export const VENUES = Object.freeze({
  paintedStoa: 'paintedStoa',
  archive: 'archive',
  vault: 'vault',
});

/**
 * 石台上的步道与底座（相对地块中心，正面朝南 +z）。建筑占北半边（z ≤ 0.6）；两排底座之间是一条过道，
 * 雅典娜和你站在过道正中（STAND）；东侧的步道连到码头。
 */
export const TERRACE = Object.freeze({
  buildingFront: 0.6,
  rowBack: 2.0,
  aisle: 3.4,
  rowFront: 4.8,
  sideWalk: 5.6,
});

/** 第 slot 个底座的位置（相对地块中心）：后排 0–5 从西到东，前排 6–11 从西到东。 */
export function venueSlotLocal(slot) {
  if (!Number.isInteger(slot) || slot < 0 || slot >= SLOTS_PER_VENUE) throw new RangeError(`civic-layout: slot ${slot} out of range`);
  const row = slot < 6 ? 0 : 1;
  const col = slot % 6;
  return { x: -4.6 + col * 1.84, z: row === 0 ? TERRACE.rowBack : TERRACE.rowFront };
}

/** 场馆的站立点（过道正中）。 */
export function venueStandLocal() {
  return { x: 0, z: TERRACE.aisle };
}

/** 从码头大街走上石台的路：东侧步道的北端 → 南端 → 过道正中（相对地块中心）。 */
export function terraceApproachLocal(quayLocalZ) {
  return [
    { x: TERRACE.sideWalk, z: quayLocalZ },
    { x: TERRACE.sideWalk, z: TERRACE.aisle },
    { x: 0, z: TERRACE.aisle },
  ];
}

/** 确定性的小抖动（FNV-1a → [−1, 1)）：同一棵树、同一个位置每次都一样。 */
function jitter(seed) {
  let h = 0x811c9dc5;
  for (const ch of String(seed)) { h ^= ch.codePointAt(0); h = Math.imul(h, 0x01000193); }
  return ((h >>> 0) / 4294967296) * 2 - 1;
}

/**
 * 林子里第 index 棵树在哪：第几块台地（从 origin 起向西），以及它在台地里的位置（相对台地中心）。
 * 位次只由 index 决定——树的样子由委托决定，位置由种下的先后决定。
 * @param {number} index 0 起
 * @param {{ x: number, z: number }} origin 第一块台地的格子坐标（publicSites.grove）
 */
export function groveSpot(index, origin) {
  if (!Number.isInteger(index) || index < 0) throw new RangeError(`civic-layout: tree index ${index} out of range`);
  const terrace = Math.floor(index / TREES_PER_TERRACE);
  const i = index % TREES_PER_TERRACE;
  const col = i % 4, row = Math.floor(i / 4);
  return {
    terrace,
    plot: { x: origin.x - terrace, z: origin.z },
    local: { x: -4.4 + col * 2.95 + jitter(`gx${index}`) * 0.45, z: -4.2 + row * 2.85 + jitter(`gz${index}`) * 0.4 },
  };
}

/** 有 n 棵树时林子占几块台地（至少一块：空林子也画一块台地，等第一棵树）。 */
export function groveTerraces(n) {
  return Math.max(1, Math.ceil(n / TREES_PER_TERRACE));
}

// ———————————————————————— 群岛海图 ————————————————————————

const GOLDEN = Math.PI * (3 - Math.sqrt(5));

/**
 * 海图上每座岛的位置与轮廓（海图坐标：0..1000 的方格，中心是神殿）。位次 n 的岛放在费马螺线上
 * （半径 ∝ √n，角度每次转一个黄金角）：先建的工作区离中心近，新的往外排，彼此不重叠，已有的永远不挪。
 * 岛的大小随委托数取对数；轮廓按岛的 Id 生成，每次都一样。
 */
export function islandLayout(islands) {
  return islands.map((island) => {
    const n = island.order ?? 0;
    const radius = n === 0 ? 0 : 118 * Math.sqrt(n) + 30;
    const angle = n * GOLDEN - Math.PI / 2;
    const size = (island.sanctuary ? 58 : 34) + 7 * Math.log2(1 + Math.max(0, island.commissions ?? 0));
    const points = [];
    const k = 20;
    for (let i = 0; i < k; i++) {
      const a = (i / k) * Math.PI * 2;
      const wobble = 0.78 + 0.22 * (0.5 + 0.5 * jitter(`${island.id}:${i}`)) + 0.08 * Math.sin(a * 3 + jitter(island.id) * 3);
      points.push({ x: Math.cos(a) * size * wobble, y: Math.sin(a) * size * wobble * 0.82 });
    }
    return { id: island.id, x: 500 + Math.cos(angle) * radius, y: 500 + Math.sin(angle) * radius, size, points };
  });
}

/** 海图坐标 → 屏幕：整张海图缩放到能放下所有的岛，留出边距。返回 { scale, ox, oy, toScreen }。 */
export function chartTransform(layout, width, height, margin = 70) {
  let minX = 400, maxX = 600, minY = 400, maxY = 600;
  for (const l of layout) {
    minX = Math.min(minX, l.x - l.size * 1.3); maxX = Math.max(maxX, l.x + l.size * 1.3);
    minY = Math.min(minY, l.y - l.size * 1.3); maxY = Math.max(maxY, l.y + l.size * 1.6);
  }
  const scale = Math.min((width - margin * 2) / (maxX - minX), (height - margin * 2) / (maxY - minY), 2.2);
  const ox = width / 2 - ((minX + maxX) / 2) * scale;
  const oy = height / 2 - ((minY + maxY) / 2) * scale;
  return { scale, ox, oy, toScreen: (x, y) => ({ x: ox + x * scale, y: oy + y * scale }) };
}

/** 点中了哪座岛（屏幕坐标）：落在岛的外接圆里、离中心最近的那一座；都没点中返回 null。 */
export function hitIsland(layout, transform, sx, sy) {
  let best = null;
  for (const l of layout) {
    const c = transform.toScreen(l.x, l.y);
    const d = Math.hypot(sx - c.x, sy - c.y);
    if (d <= l.size * transform.scale * 1.05 && (!best || d < best.d)) best = { id: l.id, d };
  }
  return best?.id ?? null;
}
