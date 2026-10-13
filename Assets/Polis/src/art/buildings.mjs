// 文件夹的建筑（设计稿 5.1 的六类），由建筑套件（kit.mjs）组装。每一类有几种"子型"，按文件夹名的哈希挑一种：
// M0 的真实夹具里 24 座建筑有 14 座是作坊，一模一样分不出谁是谁——体量等级（数据）决定大小与层数，
// 子型与细节（稳定的哈希）决定样子：作坊分陶工、织工、木工、石匠；民居分院落、两层带廊、葡萄架、并排两户……
// 状态决定门口：常用的有陶罐盆栽，冷清的什么都没有，很久没动的爬常春藤，一年以上是废墟（掀顶、断柱、瓦砾）。
//
// 每个构造函数：(M, b, r, ctx) → Group。b 是建筑记录（kind / sizeClass / state / incomplete / key），r 是按名字定的随机数，
// ctx 带共用的几何（雕像、瓶身）。原点在地块中心，正面朝 +z；userData.height 是名牌的高度，userData.subtype 是子型。
import * as THREE from '../../vendor/three.bundle.mjs';
import {
  Kit, m4, podium, door, window_, gabledRoof, frontGabledRoof, shedRoof, flatRoof, colonnade, entablature, enclosure,
  chimney, rubble, ivy, groundPlane, gable, palmette, triglyphGeometry, column, sideGable, ruinShell, lantern,
} from './kit.mjs';
import { amphoraBody } from './props.mjs';
import { leafClump } from './plants.mjs';

/** 大陶瓮（pithos）：比双耳瓶胖一圈、没有耳、口沿厚，半埋在地里（罐棚前成排地摆）。 */
let pithosCache = null;
function lathePithos() {
  if (pithosCache) return pithosCache;
  const pts = [[0, 0], [0.22, 0.02], [0.36, 0.22], [0.42, 0.5], [0.38, 0.8], [0.24, 0.98], [0.2, 1.02], [0.26, 1.06], [0.26, 1.12], [0.17, 1.12]];
  pithosCache = new THREE.LatheGeometry(pts.map(([x, y]) => new THREE.Vector2(x, y)), 16).translate(0, -0.18, 0);
  return pithosCache;
}

/**
 * 按子型表挑一种（用 r 的第一个数，所以同一个名字永远是同一种）。forced 是美术审查指定的子型：
 * 照样先取一个数，后面的细节与正常摆放时同一个名字算出来的一致。
 */
function subtype(r, list, forced = null) {
  const picked = list[Math.min(list.length - 1, Math.floor(r() * list.length))];
  return forced && list.includes(forced) ? forced : picked;
}

/** 门口的陈设、常春藤、废墟的瓦砾、未完全测绘的测量杆——六类建筑共用。 */
function decorate(kit, M, b, r, { w, d, h, y0 = 0 }) {
  const state = b.state;
  if (state === 'bustling' || state === 'lived') {
    const side = r() > 0.5 ? 1 : -1;
    kit.add(M.terracotta, amphoraBody(), m4(side * (w / 2 - 0.7), y0, d / 2 + 0.55, r() * 6, 0, 0, 0.9));
    if (state === 'bustling') {
      // 常用：门口一盆橄榄苗、一张凳子——有人在这里进进出出
      kit.cylinder(M.terracotta, 0.26, 0.2, 0.42, -side * (w / 2 - 0.6), y0 + 0.21, d / 2 + 0.5, 12);
      for (let i = 0; i < 6; i++) kit.add(M.olive, leafClump(0.14 + r() * 0.08, r, 0.75), m4(-side * (w / 2 - 0.6) + (r() - 0.5) * 0.4, y0 + 0.62 + r() * 0.3, d / 2 + 0.5 + (r() - 0.5) * 0.4));
      kit.box(M.wood, 0.5, 0.05, 0.36, side * (w / 2 - 1.5), y0 + 0.44, d / 2 + 0.6);
      for (const sx of [-1, 1]) for (const sz of [-1, 1]) kit.box(M.darkWood, 0.05, 0.42, 0.05, side * (w / 2 - 1.5) + sx * 0.2, y0 + 0.21, d / 2 + 0.6 + sz * 0.13);
    }
  }
  if (state === 'ivy' || state === 'ruin') ivy(kit, M, r, { w, d, h, y0, strands: state === 'ruin' ? 6 : 4 });
  if (state === 'ruin') rubble(kit, M, r, 5, w, d);
  if (b.incomplete) {
    // 未完全测绘：门边插一根测量杆，挂一块布
    kit.cylinder(M.wood, 0.04, 0.05, 2.6, w / 2 + 0.4, 1.3, d / 2 + 0.9, 6);
    kit.box(M.awning, 0.6, 0.4, 0.02, w / 2 + 0.72, 2.3, d / 2 + 0.9);
  }
}

// ------------------------------------------------------------------
// 柱廊书库：又宽又矮的长廊（stoa），一排密柱、单坡屋顶，后墙上是放卷轴的格架。
// 子型：直廊 / 两翼（长廊两端各立一座带小山花的短翼）/ 两层（体量大的：上面再一层爱奥尼亚柱廊，阿塔罗斯柱廊那样）。
// 辨识度在于"长、矮、柱子多、没有正面的山花"——和金库（门廊双柱 + 山花）分开。
// M2 美术审查：两端的侧墙第一版是一块固定坡度的三角，和屋面对不上（屋面下一道黑缝、屋面上戳出白角）；
// 双排柱从上面看和直廊一模一样（内排柱藏在屋顶下面）；两翼的山花屋顶插在长廊的屋面里。
// ------------------------------------------------------------------
export function buildStoaLibrary(M, b, r) {
  const kit = new Kit();
  const sc = b.sizeClass;
  const kind = subtype(r, sc >= 3 ? ['straight', 'wings', 'double'] : ['straight', 'wings'], b.subtype);
  const fullW = [9.4, 10.4, 11.2, 12][sc - 1];
  const wingW = 1.5, wingGap = 0.55;
  // 两翼：长廊让出两端，翼楼立在长廊的屋檐之外（隔开 wingGap，翼楼屋顶的出檐不压到长廊的屋面），各自一个屋顶
  const W = kind === 'wings' ? fullW - 2 * wingW - 2 * (wingGap - 0.25) : fullW;
  const D = kind === 'double' ? 6.0 : 5.0, H = 3.1;
  const upperH = kind === 'double' ? 2.5 : 0;
  const n = [6, 7, 8, 9][sc - 1] - (kind === 'wings' ? 2 : 0);
  const ruin = b.state === 'ruin';
  const y0 = podium(kit, M.marble, fullW + 0.45, D + 0.45, { steps: 2, stepH: 0.22, stepT: 0.22 });
  const front = D / 2 - 0.5;
  const colX0 = -W / 2 + 0.55, colX1 = W / 2 - 0.55;
  // 檐口高度：一层是柱顶加额枋；两层再加上层的柱子与檐
  const eaveY = y0 + H + 0.42 + (upperH ? upperH + 0.36 : 0);
  const rise = 1.0, overhang = 0.35, roofZ = -0.15, depth = D + overhang * 2;
  /** 屋面底面在 z 处的高度（单坡：前低后高） */
  const roofAt = (z) => eaveY + rise / 2 - (z - roofZ) * (rise / depth) - 0.08;
  const wallH = ruin ? H * 0.62 : null;
  const backZ = -D / 2 + 0.25;
  const backTop = ruin ? y0 + wallH : roofAt(backZ - 0.22) + 0.04;
  kit.box(M.wall, W, backTop - y0, 0.45, 0, (y0 + backTop) / 2, backZ);
  const sideZA = -D / 2 + 0.05, sideZB = D / 2 - 0.55;
  for (const sx of [-1, 1]) {
    const x = sx * (W / 2 - 0.22);
    if (ruin) kit.box(M.wall, 0.45, wallH, sideZB - sideZA, x, y0 + wallH / 2, (sideZA + sideZB) / 2);
    else sideGable(kit, M.wall, { x, zA: sideZA, zB: sideZB, yBottom: y0, topAt: roofAt });
  }
  // 后墙内侧的卷轴格架：深色的格子里露出一截截卷轴的端头
  const rows = 3, cols = Math.max(3, n - 2);
  for (let i = 0; i < cols; i++) for (let j = 0; j < rows; j++) {
    const x = -W / 2 + 1.0 + (i + 0.5) * ((W - 2.0) / cols);
    const y = y0 + 0.45 + j * 0.78;
    if (ruin && y > y0 + wallH - 0.4) continue;
    kit.box(M.darkWood, (W - 2.0) / cols - 0.12, 0.62, 0.3, x, y + 0.31, -D / 2 + 0.6);
    for (let s2 = 0; s2 < 3; s2++) kit.cylinder(M.papyrus, 0.07, 0.07, 0.26, x - 0.2 + s2 * 0.2, y + 0.18 + (s2 % 2) * 0.17, -D / 2 + 0.78, 8, Math.PI / 2);
  }
  // 柱廊里吊两盏青铜灯笼（kit.lantern）：长廊没有门窗，夜里靠它们透出光来。挂在后墙上的第一版被檐口挡住，镜头看不见；
  // 挂在柱列后 0.6 米、x = ±W/4 的第二版正好躲在一根柱子后面，盖和链子被额枋挡住。现在吊在离 ±W/4 最近的两根柱子之间、
  // 正在柱列上、额枋底下（吊在柱列后面，45° 方位角下视差把它推到柱子后面），链子 0.62 米——33° 俯角下灯盖露在额枋底下
  if (!ruin && M.lanternPane) {
    const step = (colX1 - colX0) / (n - 1);
    for (const sx of [-1, 1]) {
      const gap = Math.min(n - 2, Math.max(0, Math.round((sx * W / 4 - colX0) / step - 0.5)));
      lantern(kit, M, colX0 + (gap + 0.5) * step, y0 + H, front, { drop: 0.62, scale: 1.3 });
    }
  }
  colonnade(kit, M.marble, { n, x0: colX0, x1: colX1, y: y0, z: front, height: H, key: 'stoa', skip: ruin ? (i) => i % 2 === 1 : null });
  if (ruin) {
    // 断柱：只剩一截，柱身横倒在台阶前
    for (let i = 1; i < n; i += 2) {
      const x = colX0 + i * ((colX1 - colX0) / (n - 1));
      kit.instance('stoa-stump', () => column(H * 0.4, { detail: 'low' }), M.marble, m4(x, y0, front, r() * 6));
      kit.cylinder(M.marble, 0.24, 0.27, H * 0.45, x + 0.3, 0.27, D / 2 + 1.3, 12, 0, Math.PI / 2, r());
    }
  } else {
    kit.box(M.marble, W + 0.2, 0.42, 0.75, 0, y0 + H + 0.21, front);
    if (upperH) {
      // 上层：同样多的爱奥尼亚柱，柱间一道齐胸的栏墙，上面再一道檐
      const yU = y0 + H + 0.42;
      kit.box(M.marble, W + 0.2, 0.12, D - 0.4, 0, yU - 0.06, -0.1);
      colonnade(kit, M.marble, { n, x0: colX0, x1: colX1, y: yU, z: front, height: upperH, order: 'ionic', key: 'stoa-upper' });
      kit.box(M.marble, W - 0.9, 0.85, 0.14, 0, yU + 0.43, front - 0.02);
      kit.box(M.marble, W + 0.2, 0.36, 0.7, 0, yU + upperH + 0.18, front);
    }
    shedRoof(kit, M.tile, W, D, rise, eaveY, roofZ, overhang);
  }
  if (kind === 'wings') {
    // 两翼（paraskenia）：比长廊略向前凸，顶到长廊的檐口，各自一个山花朝前的小屋顶
    const wingD = D * 0.8, wingZ = 0.35, wingTop = ruin ? y0 + wallH : y0 + H + 0.42;
    for (const sx of [-1, 1]) {
      const x = sx * (W / 2 + wingGap + wingW / 2);
      kit.box(M.wall, wingW, wingTop - y0, wingD, x, (y0 + wingTop) / 2, wingZ);
      if (!ruin) frontGabledRoofOn(kit, M, x, wingW, wingD, wingTop, wingZ);
    }
  }
  decorate(kit, M, b, r, { w: fullW, d: D, h: H, y0 });
  const group = kit.build();
  group.userData.height = (ruin ? y0 + H : eaveY + rise) + 0.7;
  group.userData.subtype = kind;
  return group;
}

function frontGabledRoofOn(kit, M, x, w, d, y, z) {
  const sub = new Kit();
  frontGabledRoof(sub, M.tile, M.wall, w, d, 0.7, 0, 0, 0.18);
  for (const [material, list] of sub.byMaterial) for (const g of list) kit.add(material, g, m4(x, y, z));
}

// ------------------------------------------------------------------
// 民居：白灰墙、瓦顶、一扇门几扇小窗。子型：院落（门前一圈矮院墙）/ 两层带木廊 / 门前葡萄架 / 并排两户。
// M2 美术审查：院墙第一版在屋后、被房子挡住；木廊是一块深色的板，俯视像遮阳棚；葡萄架上的叶子是几块黑色的扁圆；
// 并排两户的屋顶都落在中线上；废墟是一个顶上盖着白板的实心方块。
// ------------------------------------------------------------------
export function buildHouse(M, b, r) {
  const kit = new Kit();
  const sc = b.sizeClass;
  const kind = subtype(r, sc >= 3 ? ['courtyard', 'balcony', 'pergola', 'twin'] : ['courtyard', 'pergola', 'twin'], b.subtype);
  const ruin = b.state === 'ruin';
  const plaster = M.plasterShades[Math.floor(r() * 3)];
  const tile = M.tileShades[Math.floor(r() * 3)];
  const storeys = kind === 'balcony' || (sc >= 3 && kind !== 'twin') ? 2 : 1;
  const W = kind === 'twin' ? 9.2 + sc * 0.4 : 6.6 + sc * 0.7, D = 6 + sc * 0.4;
  const H = storeys * 3 * (ruin ? 0.75 : 1);
  kit.box(M.stone, W + 0.3, 0.25, D + 0.3, 0, 0.125, 0);
  const units = kind === 'twin' ? [-W / 4, W / 4] : [0];
  let mainDoorX = 0;
  for (const [k, ux] of units.entries()) {
    const uw = kind === 'twin' ? W / 2 - 0.1 : W;
    const uh = kind === 'twin' && k === 1 ? H * (ruin ? 1 : 0.86) : H;
    const shade = kind === 'twin' && k === 1 ? M.plasterShades[(Math.floor(r() * 3) + 1) % 3] : plaster;
    const doorX = ux + (r() - 0.5) * uw * 0.4;
    if (k === 0) mainDoorX = doorX;
    if (ruin) {
      // 掀了顶的墙壳：断墙、门洞、里面的泥地
      ruinShell(kit, shade, r, { w: uw, h: uh, d: D, x: ux, y: 0.25, floor: M.earth, gaps: [{ x: doorX, w: 1.2 }] });
      continue;
    }
    kit.box(shade, uw, uh, D, ux, 0.25 + uh / 2, 0);
    door(kit, M, 1.05, 2.05, doorX, 0.25, D / 2, { lantern: doorX >= ux ? -1 : 1 });   // 壁灯挂在朝屋子中间那一侧，避开窗
    for (let s2 = 0; s2 < (uh > 4 ? 2 : 1); s2++) {
      for (const wx of [ux - uw * 0.34, ux + uw * 0.34]) {
        if (Math.abs(wx - doorX) < 1.2 && s2 === 0) continue;
        window_(kit, M, 0.6, 0.55, wx, 0.25 + s2 * 3 + 1.9, D / 2);
      }
    }
    gabledRoof(kit, kind === 'twin' && k === 1 ? M.tileShades[(Math.floor(r() * 3) + 2) % 3] : tile, shade, uw, D, 1.3, 0.25 + uh, 0, 0.4, ux);
  }
  if (ruin) {
    // 塌下来的檩条：一头搭在断墙上、一头落进屋里
    for (let i = 0; i < 4; i++) kit.box(M.darkWood, W * (0.45 + r() * 0.4), 0.14, 0.14, (r() - 0.5) * 1.5, 0.25 + H * (0.25 + r() * 0.3), -D / 2 + 0.8 + i * (D / 4), r() * 0.3, 0, (r() - 0.5) * 0.6);
  }
  if (kind === 'courtyard') {
    // 门前一圈齐腰的院墙（在镜头这一面），正对着门留一个院门
    const yardD = Math.min(2.4, 12.5 / 2 - D / 2 - 0.5);
    if (yardD > 0.9) {
      const wallH = ruin ? 0.6 : 1.15, t = 0.28, zF = D / 2 + yardD;
      for (const sx of [-1, 1]) kit.box(M.plaster, t, wallH, yardD, sx * (W / 2 - t / 2), 0.25 + wallH / 2, D / 2 + yardD / 2);
      const gate = 1.4;
      const left = mainDoorX - gate / 2 - (-W / 2), right = W / 2 - (mainDoorX + gate / 2);
      if (left > 0.2) kit.box(M.plaster, left, wallH, t, -W / 2 + left / 2, 0.25 + wallH / 2, zF - t / 2);
      if (right > 0.2) kit.box(M.plaster, right, wallH, t, W / 2 - right / 2, 0.25 + wallH / 2, zF - t / 2);
      kit.box(M.stone, W + 0.3, 0.25, yardD, 0, 0.125, D / 2 + yardD / 2);
    }
  } else if (kind === 'balcony' && !ruin) {
    // 二层挑出一道木廊：浅色的木板、一排栏杆，下面两根木柱；廊后是二层的门
    const bw = W * 0.7, deckY = 3.25, deckZ = D / 2 + 0.6;
    kit.box(M.wood, bw, 0.12, 1.2, 0, deckY, deckZ);
    kit.box(M.papyrus, bw + 0.06, 0.08, 0.1, 0, deckY + 0.9, deckZ + 0.55);
    for (let i = 0; i <= 14; i++) kit.box(M.wood, 0.05, 0.84, 0.05, -bw / 2 + (i * bw) / 14, deckY + 0.48, deckZ + 0.55);
    for (const sx of [-1, 1]) kit.box(M.papyrus, 0.08, 0.08, 1.15, sx * bw / 2, deckY + 0.9, deckZ);
    for (const sx of [-1, 1]) kit.cylinder(M.wood, 0.08, 0.09, deckY - 0.25, sx * bw * 0.46, 0.25 + (deckY - 0.25) / 2, deckZ + 0.5, 8);
    door(kit, M, 0.95, 1.9, 0, deckY + 0.06, D / 2);
  } else if (kind === 'pergola' && !ruin) {
    // 门前一架葡萄：四根柱子、两层交叉的横木，上面一簇簇叶子（棱面的小团，彼此留缝，透下斑驳的影子）
    const px = (r() - 0.5) * 2, pz = D / 2 + 1.5;
    for (const sx of [-1, 1]) for (const sz of [-1, 1]) kit.cylinder(M.wood, 0.07, 0.08, 2.4, px + sx * 1.6, 1.45, pz + sz * 0.95, 8);
    for (const sz of [-1, 1]) kit.box(M.wood, 3.7, 0.12, 0.12, px, 2.66, pz + sz * 0.95);
    for (let i = 0; i < 7; i++) kit.box(M.wood, 0.07, 0.07, 2.5, px - 1.65 + i * 0.55, 2.76, pz);
    for (let i = 0; i < 16; i++) {
      const s2 = 0.22 + r() * 0.16;
      const g = new THREE.IcosahedronGeometry(1, 0);
      kit.add(M.olive, g, m4(px - 1.6 + r() * 3.2, 2.86 + r() * 0.14, pz - 1.05 + r() * 2.1, r() * 6, r() * 6, 0, s2 * 1.3, s2 * 0.55, s2));
    }
  }
  decorate(kit, M, b, r, { w: W, d: D, h: H });
  const group = kit.build();
  group.userData.height = 0.25 + H + (ruin ? 0.5 : 1.6);
  group.userData.subtype = kind;
  return group;
}

// ------------------------------------------------------------------
// 作坊：屋前搭木棚，棚下工作台，屋侧是这门手艺的家什。子型：陶工（窑与烟囱、成排的罐）/ 织工（屋侧立着织机、羊毛篮）/
// 木工（堆着的木料、锯木架）/ 石匠（石料与一台人字吊架）。代码仓库里大半是作坊，远看靠这些家什的剪影分开。
// M2 美术审查：木棚顶第一版是深木色的一块板，俯视是一个黑洞、四种作坊看起来都一样；织机藏在棚顶下面看不见；
// 吊架的两根斜木没有交到一起，像一根歪着的杆子。
// ------------------------------------------------------------------
export function buildWorkshop(M, b, r) {
  const kit = new Kit();
  const sc = b.sizeClass;
  const craft = subtype(r, ['potter', 'weaver', 'carpenter', 'mason'], b.subtype);
  const W = 6.0 + sc * 0.9, D = 4.8 + sc * 0.3;
  const storeys = sc >= 3 ? 2 : 1;
  const H = storeys === 2 ? 5.6 : 3.2;
  const ruin = b.state === 'ruin';
  const tile = M.tileShades[Math.floor(r() * 3)];
  const plaster = M.plasterShades[Math.floor(r() * 3)];
  const toolSide = r() > 0.5 ? 1 : -1;
  const shedW = W * (0.55 + r() * 0.45);
  const shedX = ((W - shedW) / 2) * -toolSide;
  const shedD = 2.0 + r() * 1.0;
  kit.box(M.stone, W + 0.3, 0.22, D + 0.3, 0, 0.11, -0.8);
  if (ruin) {
    ruinShell(kit, plaster, r, { w: W, h: H * 0.7, d: D, y: 0.22, z: -0.8, floor: M.earth, gaps: [{ x: shedX, w: 2.4 }] });
  } else {
    kit.box(plaster, W, H, D, 0, 0.22 + H / 2, -0.8);
    door(kit, M, 2.2, 2.3, shedX, 0.22, D / 2 - 0.8, { lintel: M.darkWood });
    if (storeys === 2) for (const wx of [-W * 0.3, 0, W * 0.3]) window_(kit, M, 0.6, 0.6, wx, 0.22 + 4.0, D / 2 - 0.8);
    // 平房的作坊在木棚另一侧开一扇窗（第一版没有窗，夜里整座作坊是黑的）
    else if (W - shedW > 1.6) window_(kit, M, 0.6, 0.55, toolSide * (W / 2 - (W - shedW) / 2), 0.22 + 1.9, D / 2 - 0.8);
    gabledRoof(kit, tile, plaster, W, D, 1.2, 0.22 + H, -0.8, 0.35);
  }
  // 木棚：四根柱子撑一片苇顶（浅草色、顺坡的苇秆），棚下工作台
  for (const sx of [-1, 1]) for (const sz of [0, 1]) kit.cylinder(M.wood, 0.09, 0.1, 2.6, shedX + sx * (shedW / 2 - 0.3), 1.3, D / 2 - 0.8 + 0.3 + sz * shedD, 8);
  if (!ruin) {
    kit.box(M.reed, shedW, 0.1, shedD + 0.6, shedX, 2.75, D / 2 - 0.8 + shedD / 2 + 0.3, 0, -0.12);
    for (const sx of [-1, 1]) kit.box(M.darkWood, 0.1, 0.12, shedD + 0.7, shedX + sx * (shedW / 2 - 0.05), 2.68, D / 2 - 0.8 + shedD / 2 + 0.3, 0, -0.12);
  }
  // 工作台离门 2 米（第三轮需求 2：门会开、人要进出）——第一版贴在门前 1.4 米，台子和墙之间只剩 0.95 米，
  // 让出人身的半径之后一点能站的地方都没有，作坊的门永远走不出来
  const benchZ = D / 2 + 1.25;
  kit.box(M.wood, Math.min(2.2, shedW - 0.6), 0.12, 0.9, shedX, 0.95, benchZ);
  for (const lx of [-0.8, 0.8]) for (const lz of [-0.35, 0.35]) kit.box(M.wood, 0.08, 0.9, 0.08, shedX + lx, 0.45, benchZ + lz);
  const tx = toolSide * (W / 2 + 1.0);
  if (craft === 'potter') {
    const tall = 3.6 + r() * 1.8 + (storeys === 2 ? 1.6 : 0);
    kit.add(M.stone, new THREE.SphereGeometry(1.05, 16, 10, 0, Math.PI * 2, 0, Math.PI / 2), m4(tx, 0, -0.6));
    chimney(kit, M.stone, tx, -1.3, tall, 0.34);
    kit.box(M.dark, 0.6, 0.5, 0.1, tx, 0.35, 0.43);
    const jars = 2 + Math.floor(r() * 4);
    for (let i = 0; i < jars; i++) kit.add(M.terracotta, amphoraBody(), m4(-toolSide * (W / 2 - 0.6 - i * 0.45), 0, D / 2 + shedD - 0.2, r() * 6, 0, 0, 0.8));
    kit.cylinder(M.stone, 0.32, 0.36, 0.5, shedX + 0.5, 1.26, benchZ, 14);   // 陶轮
  } else if (craft === 'weaver') {
    // 竖式织机，立在屋侧的空地上、正面朝街：两根立柱、上下横梁、一幅织到一半的布、一排经线坠子
    const lx = tx, lz = 0.9;
    for (const sx of [-1, 1]) kit.box(M.darkWood, 0.13, 2.5, 0.13, lx + sx * 0.85, 1.25, lz, 0, -0.1);
    kit.box(M.darkWood, 1.95, 0.12, 0.14, lx, 2.42, lz - 0.12);
    kit.box(M.darkWood, 1.8, 0.08, 0.1, lx, 0.82, lz + 0.05);
    kit.box(M.awning, 1.55, 0.9, 0.03, lx, 1.92, lz - 0.06, 0, -0.1);
    for (let i = 0; i < 12; i++) kit.box(M.papyrus, 0.012, 1.0, 0.012, lx - 0.7 + i * 0.127, 1.0, lz + 0.03, 0, -0.1);
    for (let i = 0; i < 8; i++) kit.add(M.stone, new THREE.ConeGeometry(0.05, 0.11, 6), m4(lx - 0.66 + i * 0.19, 0.6, lz + 0.08, 0, Math.PI));
    for (let i = 0; i < 3; i++) {
      const bx = tx + toolSide * 0.3 - toolSide * i * 0.6;
      kit.add(M.wood, new THREE.CylinderGeometry(0.26, 0.2, 0.3, 12, 1, true), m4(bx, 0.15, -0.9 + (i % 2) * 0.5));
      kit.add(M.awning, new THREE.SphereGeometry(0.2, 10, 8), m4(bx, 0.32, -0.9 + (i % 2) * 0.5, 0, 0, 0, 1, 0.6, 1));
    }
  } else if (craft === 'carpenter') {
    // 一堆横放的木料、一副锯木架
    for (let k = 0; k < 6; k++) kit.box(M.wood, 0.28, 0.28, 3.2, tx - toolSide * (k % 3) * 0.32, 0.14 + Math.floor(k / 3) * 0.28, -0.4, 0.02 * k);
    for (const sx of [-1, 1]) {
      kit.box(M.darkWood, 0.08, 1.0, 0.08, shedX + sx * 0.4, 0.5, benchZ + 1.3, 0, 0, sx * 0.35);
      kit.box(M.darkWood, 0.08, 1.0, 0.08, shedX + sx * 0.4, 0.5, benchZ + 1.7, 0, 0, sx * 0.35);
    }
    kit.box(M.wood, 1.8, 0.16, 0.2, shedX, 0.86, benchZ + 1.5);
  } else {
    // 石匠：几块方石、一台人字吊架——两根斜木在顶上交成一个 A、一根后撑，顶上垂下一根绳，吊着一块方石
    for (let k = 0; k < 4; k++) kit.box(M.marble, 0.7 + r() * 0.3, 0.55, 0.6, tx - toolSide * (k % 2) * 0.8, 0.28 + Math.floor(k / 2) * 0.55, -1.0 + (k % 2) * 0.3, r() * 0.3);
    const apex = new THREE.Vector3(tx, 4.1, 1.1);
    const leg = (foot) => {
      const dir = new THREE.Vector3().subVectors(apex, foot);
      const g = new THREE.CylinderGeometry(0.07, 0.09, dir.length(), 6);
      g.applyQuaternion(new THREE.Quaternion().setFromUnitVectors(new THREE.Vector3(0, 1, 0), dir.clone().normalize()));
      g.translate((foot.x + apex.x) / 2, (foot.y + apex.y) / 2, (foot.z + apex.z) / 2);
      kit.add(M.darkWood, g);
    };
    leg(new THREE.Vector3(tx - 1.05, 0, 1.3)); leg(new THREE.Vector3(tx + 1.05, 0, 1.3)); leg(new THREE.Vector3(tx, 0, -0.9));
    kit.cylinder(M.awning, 0.02, 0.02, 2.4, tx, 4.1 - 1.2, 1.1, 4);
    kit.box(M.marble, 0.62, 0.45, 0.5, tx, 4.1 - 2.6, 1.1, 0.3);
  }
  if (ruin) rubble(kit, M, r, 5, W, D);
  decorate(kit, M, b, r, { w: W, d: D, h: H });
  const group = kit.build();
  group.userData.height = 0.22 + (ruin ? H * 0.7 : H) + 1.6;
  group.userData.subtype = craft;
  return group;
}

// ------------------------------------------------------------------
// 雕塑园：一圈矮墙围着几尊立像，后角种柏树。子型：弧形排开 / 两排夹一道中轴（尽头一尊方柱头像）/
// 半圆形的石凳（exedra）围着立像。立像的数量随体量。
// ------------------------------------------------------------------
export function buildSculptureGarden(M, b, r, ctx) {
  const kit = new Kit();
  const kind = subtype(r, ['arc', 'avenue', 'exedra'], b.subtype);
  const W = 11, D = 10;
  const ruin = b.state === 'ruin';
  kit.add(M.pave, groundPlane(W, D, 3), m4(0, 0.03, 0));
  enclosure(kit, M.marble, W, D, 1.1, { gateWidth: 2.8, thickness: 0.4 });
  const n = 2 + b.sizeClass;
  const statue = ctx.statue;
  const place = (x, z, face, i) => {
    kit.box(M.marble, 0.9, 0.9, 0.9, x, 0.45, z);
    if (ruin && i % 2 === 0) kit.add(M.marble, statue, m4(x + 0.6, 0.3, z + 1.1, r() * 6, Math.PI / 2, 0, 0.9));
    else kit.add(M.marble, statue, m4(x, 0.9, z, face + (r() - 0.5) * 0.4, 0, 0, 1.05));
  };
  if (kind === 'arc') {
    for (let i = 0; i < n; i++) {
      const a = (i / n) * Math.PI * 1.2 - Math.PI * 0.1;
      const x = Math.cos(a + Math.PI) * 3.3, z = -1 + Math.sin(a + Math.PI) * 2.8;
      place(x, z, Math.atan2(-x, 4 - z), i);
    }
  } else if (kind === 'avenue') {
    for (let i = 0; i < n; i++) {
      const side = i % 2 === 0 ? -1 : 1;
      const z = 2.6 - Math.floor(i / 2) * 2.2;
      place(side * 2.4, z, side < 0 ? Math.PI / 2 : -Math.PI / 2, i);
    }
    kit.box(M.marble, 0.42, 1.5, 0.42, 0, 0.75, -3.4);
    kit.add(M.marble, new THREE.SphereGeometry(0.24, 14, 10), m4(0, 1.75, -3.4, 0, 0, 0, 0.9, 1.15, 0.95));
    kit.add(M.pave, groundPlane(1.6, 7, 2), m4(0, 0.05, 0));
  } else {
    // 半圆的石凳围着中间的立像：一段段沿着弧线的切线摆、首尾相接（第一版的段按圆的角度摆在椭圆上，接缝处断开、末段歪着）
    const seg = 14, ax = 3.6, az = 3.0;
    const at = (a) => [Math.cos(a) * ax, -0.5 + Math.sin(a) * az];
    for (let i = 0; i < seg; i++) {
      const a0 = Math.PI + (i / seg) * Math.PI, a1 = Math.PI + ((i + 1) / seg) * Math.PI;
      const [x0, z0] = at(a0), [x1, z1] = at(a1);
      kit.box(M.marble, Math.hypot(x1 - x0, z1 - z0) + 0.06, 0.45, 0.55, (x0 + x1) / 2, 0.23, (z0 + z1) / 2, Math.atan2(-(z1 - z0), x1 - x0));
    }
    for (let i = 0; i < n; i++) {
      const x = (i - (n - 1) / 2) * 1.5;
      place(x, -0.6 - (i % 2) * 0.9, 0, i);
    }
  }
  decorate(kit, M, b, r, { w: W, d: D, h: 1.1 });
  const group = kit.build();
  group.userData.height = 3.4;
  group.userData.trees = [[-W / 2 + 1.2, -D / 2 + 1.2], [W / 2 - 1.2, -D / 2 + 1.2]];
  group.userData.subtype = kind;
  return group;
}

// ------------------------------------------------------------------
// 金库：德尔斐那种"门廊双柱"的小神殿（distyle in antis），三级台基、山花。
// 子型：多立克（三陇板）/ 爱奥尼亚（卷涡柱头、连续的饰带）/ 带顶饰（山花三角上三只棕叶饰）。
// ------------------------------------------------------------------
export function buildTreasury(M, b, r) {
  const kit = new Kit();
  const kind = subtype(r, ['doric', 'ionic', 'acroteria'], b.subtype);
  const order = kind === 'ionic' ? 'ionic' : 'doric';
  const s = 0.9 + (b.sizeClass - 1) * 0.06;
  const W = 6.2 * s, L = 8.6 * s, colH = 4.2 * s;
  const ruin = b.state === 'ruin';
  const y0 = podium(kit, M.marble, W, L, { steps: 3, stepH: 0.25, stepT: 0.3 });
  const wallH = ruin ? colH * 0.6 : colH;
  if (ruin) {
    // 掀了顶的内殿：断墙围着一块泥地，门洞朝前（第一版是一个顶上盖着白板的实心方块）
    ruinShell(kit, M.wall, r, { w: W - 0.4, h: wallH, d: L - 2.4, y: y0, z: -1.0, t: 0.4, floor: M.earth, gaps: [{ x: 0, w: 1.4 }] });
  } else {
    kit.box(M.wall, W - 0.4, wallH, L - 2.4, 0, y0 + wallH / 2, -1.0);
    door(kit, M, 1.4, 2.8, 0, y0, L / 2 - 2.24, { lintel: M.marble, lantern: 'both' });
  }
  for (const sx of [-1, 1]) {
    const antaH = ruin ? wallH * (0.5 + r() * 0.4) : wallH;
    kit.box(M.wall, 0.55, antaH, 1.6, sx * (W / 2 - 0.47), y0 + antaH / 2, L / 2 - 1.2);
  }
  for (const sx of [-1, 1]) {
    if (ruin && sx > 0) continue;
    // 多立克柱转一个角度让凹槽错开；爱奥尼亚的卷涡要朝前
    kit.instance(`treasury-col-${order}-${colH.toFixed(2)}`, () => column(colH, { detail: 'low', order }), M.marble, m4(sx * 1.05 * s, y0, L / 2 - 0.55, order === 'ionic' ? 0 : sx));
  }
  if (!ruin) {
    kit.box(M.marble, W + 0.1, 0.55, L + 0.1, 0, y0 + colH + 0.28, 0);
    if (order === 'doric') {
      const tri = () => triglyphGeometry(0.4, 0.55);
      for (let i = 0; i < 5; i++) kit.instance('treasury-triglyph', tri, M.marble, m4(-W / 2 + 0.45 + i * ((W - 0.9) / 4), y0 + colH + 0.28, L / 2 + 0.06));
    } else {
      kit.box(M.wall, W + 0.12, 0.3, 0.08, 0, y0 + colH + 0.32, L / 2 + 0.07);   // 爱奥尼亚的连续饰带
    }
    kit.box(M.marble, W + 0.6, 0.22, L + 0.6, 0, y0 + colH + 0.67, 0);
    const ridge = 1.3 * s;
    kit.add(M.marble, gable(W + 0.2, ridge, 0.3), m4(0, y0 + colH + 0.78, L / 2 + 0.1));
    kit.add(M.marble, gable(W + 0.2, ridge, 0.3), m4(0, y0 + colH + 0.78, -L / 2 - 0.1));
    const half = (W + 0.8) / 2, pitch = Math.atan2(ridge, half), slope = Math.hypot(half, ridge);
    for (const sx of [1, -1]) kit.box(M.tile, L + 0.8, 0.14, slope, sx * half / 2, y0 + colH + 0.78 + ridge / 2 + 0.05, 0, Math.PI / 2, sx * pitch);
    kit.add(M.marble, palmette(0.8, 0.6), m4(0, y0 + colH + 0.78 + ridge, L / 2 + 0.2));
    if (kind === 'acroteria') for (const sx of [-1, 1]) kit.add(M.marble, palmette(0.55, 0.42), m4(sx * (W / 2 + 0.1), y0 + colH + 0.78, L / 2 + 0.2));
  }
  decorate(kit, M, b, r, { w: W, d: L, h: wallH, y0 });
  const group = kit.build();
  group.userData.height = y0 + colH + 2.6 * s;
  group.userData.subtype = kind;
  return group;
}

// ------------------------------------------------------------------
// 仓库：长而低的石屋。子型：货栈（大门、门外木箱与陶罐）/ 粮仓（架在石墩上的仓房、木梯）/ 罐棚（一面墙加一片单坡顶，棚前成排的大陶瓮）。
// M2 美术审查：罐棚的瓶子第一版全在棚顶底下，俯视一只也看不见；废墟是顶上盖着白板的实心方块。
// ------------------------------------------------------------------
export function buildWarehouse(M, b, r) {
  const kit = new Kit();
  const kind = subtype(r, ['store', 'granary', 'jarShed'], b.subtype);
  const sc = b.sizeClass;
  const W = 8.5 + sc * 0.6, D = 6, H = 3.3;
  const ruin = b.state === 'ruin';
  let height = H + 1.6;
  if (kind === 'store') {
    if (ruin) {
      ruinShell(kit, M.stone, r, { w: W, h: H * 0.7, d: D, z: -0.4, t: 0.4, floor: M.earth, gaps: [{ x: 0, w: 2.6 }] });
    } else {
      kit.box(M.stone, W, H, D, 0, H / 2, -0.4);
      door(kit, M, 2.6, 2.6, 0, 0, D / 2 - 0.4, { lintel: M.darkWood, lantern: 1 });
      gabledRoof(kit, M.tile, M.stone, W, D, 1.1, H, -0.4, 0.3);
    }
    for (let i = 0; i < 5; i++) {
      const s = 0.6 + r() * 0.3;
      kit.box(M.wood, s, s, s, -W / 2 + 0.7 + (i % 3) * 0.8, s / 2 + (i >= 3 ? 0.75 : 0), D / 2 + 0.7, r() * 0.4);
    }
    for (let i = 0; i < 3; i++) kit.add(M.terracotta, amphoraBody(), m4(W / 2 - 0.6 - i * 0.5, 0, D / 2 + 0.6, r() * 6, 0, 0, 0.85));
  } else if (kind === 'granary') {
    // 架在石墩上的仓房：离地防潮，一架木梯搭在门口
    const lift = 0.9;
    for (const sx of [-1, 0, 1]) for (const sz of [-1, 1]) kit.cylinder(M.stone, 0.22, 0.28, lift, sx * (W / 2 - 0.6), lift / 2, -0.4 + sz * (D / 2 - 0.5), 10);
    const bodyH = ruin ? H * 0.6 : H - 0.4;
    kit.box(M.wood, W, 0.2, D, 0, lift + 0.1, -0.4);
    if (ruin) {
      ruinShell(kit, M.plaster, r, { w: W - 0.2, h: bodyH, d: D - 0.2, y: lift + 0.2, z: -0.4, t: 0.25, gaps: [{ x: 0, w: 1.2 }] });
    } else {
      kit.box(M.plaster, W - 0.2, bodyH, D - 0.2, 0, lift + 0.2 + bodyH / 2, -0.4);
      door(kit, M, 1.2, 1.7, 0, lift + 0.2, D / 2 - 0.5, { lintel: M.darkWood });
      gabledRoof(kit, M.tile, M.plaster, W - 0.2, D - 0.2, 1.1, lift + 0.2 + bodyH, -0.4, 0.3);
    }
    for (let k = 0; k < 5; k++) kit.box(M.wood, 1.0, 0.06, 0.12, 0, 0.2 + k * 0.22, D / 2 + 0.2 + k * 0.08);
    for (const sx of [-1, 1]) kit.box(M.wood, 0.08, 1.4, 0.08, sx * 0.48, 0.62, D / 2 + 0.42, 0, 0.32);
    height = lift + H + 1.4;
  } else {
    // 罐棚：后面一堵墙、一片单坡顶，下面三排双耳瓶
    kit.box(M.stone, W, H, 0.5, 0, H / 2, -D / 2 + 0.25);
    for (const sx of [-1, 1]) kit.box(M.stone, 0.5, H * 0.8, D * 0.7, sx * (W / 2 - 0.25), H * 0.4, -D * 0.15);
    for (const sx of [-1, -0.33, 0.33, 1]) kit.cylinder(M.wood, 0.1, 0.12, H - 0.4, sx * (W / 2 - 0.5), (H - 0.4) / 2, D / 2 - 0.6, 8);
    if (!ruin) shedRoof(kit, M.tile, W, D, 0.9, H - 0.3, -0.2, 0.3);
    // 棚下一排，棚前的空地上两排大陶瓮（半埋在地里的 pithos）：俯视看得见的是棚前那两排
    for (let i = 0; i < Math.floor(W / 0.55) - 2; i++) kit.add(M.terracotta, amphoraBody(), m4(-W / 2 + 0.9 + i * 0.55, 0, -D / 2 + 1.1, r() * 6, 0, 0, 0.82));
    const pithos = lathePithos();
    for (let row = 0; row < 2; row++) for (let i = 0; i < Math.floor((W - 1.2) / 0.95); i++) {
      if (ruin && (i + row) % 3 === 0) continue;
      kit.add(M.terracotta, pithos, m4(-W / 2 + 1.0 + i * 0.95 + row * 0.47, 0, D / 2 + 0.55 + row * 0.95, r() * 6));
    }
  }
  if (ruin) rubble(kit, M, r, 6, W, D);
  decorate(kit, M, b, r, { w: W, d: D, h: H });
  const group = kit.build();
  group.userData.height = height;
  group.userData.subtype = kind;
  return group;
}

/** 类型 → 构造函数（world.mjs 与资产表用）。 */
export const BUILDINGS = Object.freeze({
  stoaLibrary: buildStoaLibrary,
  house: buildHouse,
  workshop: buildWorkshop,
  sculptureGarden: buildSculptureGarden,
  treasury: buildTreasury,
  warehouse: buildWarehouse,
});

/** 每类的子型（美术审查逐个渲染）。 */
export const SUBTYPES = Object.freeze({
  stoaLibrary: ['straight', 'wings', 'double'],
  house: ['courtyard', 'balcony', 'pergola', 'twin'],
  workshop: ['potter', 'weaver', 'carpenter', 'mason'],
  sculptureGarden: ['arc', 'avenue', 'exedra'],
  treasury: ['doric', 'ionic', 'acroteria'],
  warehouse: ['store', 'granary', 'jarShed'],
});

export { entablature, colonnade };
