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
  chimney, rubble, ivy, groundPlane, gable, palmette, triglyphGeometry, column,
} from './kit.mjs';
import { amphoraBody } from './props.mjs';

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
      kit.add(M.olive, new THREE.IcosahedronGeometry(0.42, 2), m4(-side * (w / 2 - 0.6), y0 + 0.72, d / 2 + 0.5, 0, 0, 0, 1, 0.8, 1));
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
// 子型：直廊 / 两翼（两端各凸出一截短翼）/ 双排柱（体量大的，进深加倍）。
// 辨识度在于"长、矮、柱子多、没有正面的山花"——和金库（门廊双柱 + 山花）分开。
// ------------------------------------------------------------------
export function buildStoaLibrary(M, b, r) {
  const kit = new Kit();
  const sc = b.sizeClass;
  const kind = subtype(r, sc >= 3 ? ['straight', 'wings', 'double'] : ['straight', 'wings'], b.subtype);
  const W = [9.4, 10.4, 11.2, 12][sc - 1], D = kind === 'double' ? 6.4 : 5.0, H = 3.1;
  const n = [6, 7, 8, 9][sc - 1];
  const ruin = b.state === 'ruin';
  const y0 = podium(kit, M.marble, W + 0.45, D + 0.45, { steps: 2, stepH: 0.22, stepT: 0.22 });
  const wallH = ruin ? H * 0.62 : H;
  kit.box(M.wall, W, wallH, 0.45, 0, y0 + wallH / 2, -D / 2 + 0.25);
  for (const sx of [-1, 1]) kit.box(M.wall, 0.45, wallH, D - 0.6, sx * (W / 2 - 0.22), y0 + wallH / 2, -0.25);
  // 后墙内侧的卷轴格架：深色的格子里露出一截截卷轴的端头
  const rows = 3, cols = Math.max(3, n - 2);
  for (let i = 0; i < cols; i++) for (let j = 0; j < rows; j++) {
    const x = -W / 2 + 1.0 + (i + 0.5) * ((W - 2.0) / cols);
    const y = y0 + 0.45 + j * 0.78;
    if (ruin && y > y0 + wallH - 0.4) continue;
    kit.box(M.darkWood, (W - 2.0) / cols - 0.12, 0.62, 0.3, x, y + 0.31, -D / 2 + 0.6);
    for (let s = 0; s < 3; s++) kit.cylinder(M.papyrus, 0.07, 0.07, 0.26, x - 0.2 + s * 0.2, y + 0.18 + (s % 2) * 0.17, -D / 2 + 0.78, 8, Math.PI / 2);
  }
  const front = D / 2 - 0.5;
  colonnade(kit, M.marble, { n, x0: -W / 2 + 0.55, x1: W / 2 - 0.55, y: y0, z: front, height: H, key: 'stoa', skip: ruin ? (i) => i % 2 === 1 : null });
  if (kind === 'double') colonnade(kit, M.marble, { n: n - 2, x0: -W / 2 + 1.4, x1: W / 2 - 1.4, y: y0, z: 0, height: H, key: 'stoa-inner', skip: ruin ? (i) => i % 2 === 0 : null });
  if (ruin) {
    // 断柱：只剩一截，柱身横倒在台阶前
    for (let i = 1; i < n; i += 2) {
      const x = -W / 2 + 0.55 + i * ((W - 1.1) / (n - 1));
      kit.instance('stoa-stump', () => column(H * 0.4, { detail: 'low' }), M.marble, m4(x, y0, front, r() * 6));
      kit.cylinder(M.marble, 0.24, 0.27, H * 0.45, x + 0.3, 0.27, D / 2 + 1.3, 12, 0, Math.PI / 2, r());
    }
  } else {
    kit.box(M.marble, W + 0.2, 0.42, 0.75, 0, y0 + H + 0.21, front);
    shedRoof(kit, M.tile, W, D, 1.0, y0 + H + 0.42, -0.15, 0.35);
    for (const sx of [-1, 1]) {
      const tri = new THREE.Shape();
      tri.moveTo(-D / 2, 0); tri.lineTo(D / 2 - 0.4, 0); tri.lineTo(-D / 2, 1.0); tri.closePath();
      kit.add(M.wall, new THREE.ExtrudeGeometry(tri, { depth: 0.45, bevelEnabled: false }), m4(sx * (W / 2 - 0.22) - 0.225, y0 + H + 0.42, 0, -Math.PI / 2));
    }
  }
  if (kind === 'wings') {
    // 两端各凸出一截短翼（paraskenia），小山花朝前
    for (const sx of [-1, 1]) {
      const x = sx * (W / 2 + 0.2);
      kit.box(M.wall, 1.6, wallH, D * 0.75, x, y0 + wallH / 2, 0.4);
      if (!ruin) frontGabledRoofOn(kit, M, x, 1.6, D * 0.75, y0 + wallH, 0.4);
    }
  }
  decorate(kit, M, b, r, { w: W, d: D, h: wallH, y0 });
  const group = kit.build();
  group.userData.height = y0 + H + 1.7;
  group.userData.subtype = kind;
  return group;
}

function frontGabledRoofOn(kit, M, x, w, d, y, z) {
  const sub = new Kit();
  frontGabledRoof(sub, M.tile, M.wall, w, d, 0.7, 0, 0, 0.18);
  for (const [material, list] of sub.byMaterial) for (const g of list) kit.add(material, g, m4(x, y, z));
}

// ------------------------------------------------------------------
// 民居：白灰墙、瓦顶、一扇门几扇小窗。子型：院落（侧边一圈矮院墙）/ 两层带木廊 / 门前葡萄架 / 并排两户。
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
  for (const [k, ux] of units.entries()) {
    const uw = kind === 'twin' ? W / 2 - 0.1 : W;
    const uh = kind === 'twin' && k === 1 ? H * (ruin ? 1 : 0.86) : H;
    const shade = kind === 'twin' && k === 1 ? M.plasterShades[(Math.floor(r() * 3) + 1) % 3] : plaster;
    kit.box(shade, uw, uh, D, ux, 0.25 + uh / 2, 0);
    const doorX = ux + (r() - 0.5) * uw * 0.4;
    door(kit, M, 1.05, 2.05, doorX, 0.25, D / 2);
    for (let s = 0; s < (uh > 4 ? 2 : 1); s++) {
      for (const wx of [ux - uw * 0.34, ux + uw * 0.34]) {
        if (Math.abs(wx - doorX) < 1.2 && s === 0) continue;
        window_(kit, M, 0.6, 0.55, wx, 0.25 + s * 3 + 1.9, D / 2);
      }
    }
    if (!ruin) gabledRoof(kit, kind === 'twin' && k === 1 ? M.tileShades[(Math.floor(r() * 3) + 2) % 3] : tile, shade, uw, D, 1.3, 0.25 + uh, 0, 0.4);
  }
  if (ruin) {
    // 塌了一半的屋顶：只剩几根檩条
    for (let i = 0; i < 4; i++) kit.box(M.darkWood, W * (0.5 + r() * 0.5), 0.14, 0.14, (r() - 0.5) * 1.5, 0.25 + H + 0.1, -D / 2 + 0.8 + i * (D / 4), r() * 0.2, 0, (r() - 0.5) * 0.4);
  }
  if (kind === 'courtyard') {
    const side = r() > 0.5 ? 1 : -1;
    const yardW = Math.min(2.2, (12.5 - W) / 2 - 0.2);
    if (yardW > 0.8) {
      kit.box(M.plaster, 0.3, 1.5, D * 0.85, side * (W / 2 + yardW), 0.75, -D * 0.07);
      kit.box(M.plaster, yardW, 1.5, 0.3, side * (W / 2 + yardW / 2), 0.75, -D / 2 + 0.2);
    }
  } else if (kind === 'balcony' && !ruin) {
    // 二层挑出一道木廊，下面两根木柱
    kit.box(M.darkWood, W * 0.7, 0.12, 1.1, 0, 3.25, D / 2 + 0.55);
    kit.box(M.wood, W * 0.7, 0.7, 0.06, 0, 3.66, D / 2 + 1.08);
    for (let i = 0; i <= 6; i++) kit.box(M.wood, 0.05, 0.7, 0.05, -W * 0.35 + (i * W * 0.7) / 6, 3.66, D / 2 + 1.06);
    for (const sx of [-1, 1]) kit.cylinder(M.wood, 0.08, 0.09, 3.2, sx * W * 0.33, 1.85, D / 2 + 1.0, 8);
  } else if (kind === 'pergola' && !ruin) {
    // 门前一架葡萄：四根柱子、几根横梁，上面一层叶子
    const px = (r() - 0.5) * 2;
    for (const sx of [-1, 1]) for (const sz of [0, 1]) kit.cylinder(M.wood, 0.07, 0.08, 2.4, px + sx * 1.6, 1.2, D / 2 + 0.6 + sz * 1.8, 8);
    for (let i = 0; i < 5; i++) kit.box(M.wood, 3.6, 0.08, 0.08, px, 2.42, D / 2 + 0.5 + i * 0.5);
    for (let i = 0; i < 7; i++) kit.add(M.ivy, new THREE.IcosahedronGeometry(0.42, 1), m4(px - 1.4 + (i % 4) * 0.95, 2.6, D / 2 + 0.8 + Math.floor(i / 4) * 1.1, r() * 3, 0, 0, 1.1, 0.35, 1));
  }
  decorate(kit, M, b, r, { w: W, d: D, h: H });
  const group = kit.build();
  group.userData.height = 0.25 + H + (ruin ? 0.5 : 1.6);
  group.userData.subtype = kind;
  return group;
}

// ------------------------------------------------------------------
// 作坊：屋前搭木棚，棚下工作台，屋侧是这门手艺的家什。子型：陶工（窑与烟囱、成排的罐）/ 织工（棚下立着织机、羊毛篮）/
// 木工（堆着的木料、锯木架）/ 石匠（石料与一台人字吊架）。代码仓库里大半是作坊，远看靠这些家什的剪影分开。
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
  kit.box(plaster, W, H, D, 0, 0.22 + H / 2, -0.8);
  door(kit, M, 2.2, 2.3, shedX, 0.22, D / 2 - 0.8, { lintel: M.darkWood });
  if (storeys === 2) for (const wx of [-W * 0.3, 0, W * 0.3]) window_(kit, M, 0.6, 0.6, wx, 0.22 + 4.0, D / 2 - 0.8);
  if (!ruin) gabledRoof(kit, tile, plaster, W, D, 1.2, 0.22 + H, -0.8, 0.35);
  // 木棚：四根柱子撑一片斜顶，棚下工作台
  for (const sx of [-1, 1]) for (const sz of [0, 1]) kit.cylinder(M.wood, 0.09, 0.1, 2.6, shedX + sx * (shedW / 2 - 0.3), 1.3, D / 2 - 0.8 + 0.3 + sz * shedD, 8);
  if (!ruin) kit.box(M.darkWood, shedW, 0.12, shedD + 0.6, shedX, 2.75, D / 2 - 0.8 + shedD / 2 + 0.3, 0, -0.12);
  const benchZ = D / 2 + 0.6;
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
    // 竖式织机：两根立柱、上下横梁、一排垂下的经线与坠子
    const lx = shedX + (shedW / 2 - 1.2) * toolSide;
    for (const sx of [-1, 1]) kit.box(M.darkWood, 0.12, 2.3, 0.12, lx + sx * 0.75, 1.15, benchZ - 0.6, 0, -0.12);
    kit.box(M.darkWood, 1.7, 0.1, 0.12, lx, 2.25, benchZ - 0.72);
    kit.box(M.awning, 1.4, 1.3, 0.03, lx, 1.45, benchZ - 0.66, 0, -0.12);
    for (let i = 0; i < 7; i++) kit.add(M.stone, new THREE.ConeGeometry(0.05, 0.1, 6), m4(lx - 0.6 + i * 0.2, 0.7, benchZ - 0.55, 0, Math.PI));
    for (let i = 0; i < 3; i++) {
      const bx = tx - toolSide * i * 0.6;
      kit.add(M.wood, new THREE.CylinderGeometry(0.26, 0.2, 0.3, 12, 1, true), m4(bx, 0.15, 0.4 + (i % 2) * 0.5));
      kit.add(M.awning, new THREE.SphereGeometry(0.2, 10, 8), m4(bx, 0.32, 0.4 + (i % 2) * 0.5, 0, 0, 0, 1, 0.6, 1));
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
    // 石匠：几块方石、一台人字吊架（两根斜木 + 一根绳）
    for (let k = 0; k < 4; k++) kit.box(M.marble, 0.7 + r() * 0.3, 0.55, 0.6, tx - toolSide * (k % 2) * 0.8, 0.28 + Math.floor(k / 2) * 0.55, -0.6 + (k % 2) * 0.3, r() * 0.3);
    for (const sx of [-1, 1]) kit.box(M.darkWood, 0.12, 4.2, 0.12, tx + sx * 0.9, 2.0, 1.0, 0, 0, sx * 0.22);
    kit.box(M.darkWood, 0.5, 0.12, 0.12, tx, 4.0, 1.0);
    kit.cylinder(M.awning, 0.015, 0.015, 2.6, tx, 2.7, 1.0, 4);
  }
  if (ruin) rubble(kit, M, r, 5, W, D);
  decorate(kit, M, b, r, { w: W, d: D, h: H });
  const group = kit.build();
  group.userData.height = 0.22 + H + 1.6;
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
    // 半圆的石凳围着中间的立像
    for (let i = 0; i <= 8; i++) {
      const a = Math.PI + (i / 8) * Math.PI;
      kit.box(M.marble, 1.25, 0.45, 0.55, Math.cos(a) * 3.6, 0.23, -0.5 + Math.sin(a) * 3.0, -a + Math.PI / 2);
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
  kit.box(M.wall, W - 0.4, wallH, L - 2.4, 0, y0 + wallH / 2, -1.0);
  for (const sx of [-1, 1]) kit.box(M.wall, 0.55, wallH, 1.6, sx * (W / 2 - 0.47), y0 + wallH / 2, L / 2 - 1.2);
  door(kit, M, 1.4, 2.8, 0, y0, L / 2 - 2.24, { lintel: M.marble });
  for (const sx of [-1, 1]) {
    if (ruin && sx > 0) continue;
    kit.instance(`treasury-col-${order}-${colH.toFixed(2)}`, () => column(colH, { detail: 'low', order }), M.marble, m4(sx * 1.05 * s, y0, L / 2 - 0.55, sx));
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
    for (const sx of [1, -1]) kit.box(M.tile, slope, 0.14, L + 0.8, sx * half / 2, y0 + colH + 0.78 + ridge / 2 + 0.05, 0, 0, 0, -sx * pitch);
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
// 仓库：长而低的石屋。子型：货栈（大门、门外木箱与陶罐）/ 粮仓（架在石墩上的仓房、木梯）/ 罐棚（一面墙加一片单坡顶，下面成排的双耳瓶）。
// ------------------------------------------------------------------
export function buildWarehouse(M, b, r) {
  const kit = new Kit();
  const kind = subtype(r, ['store', 'granary', 'jarShed'], b.subtype);
  const sc = b.sizeClass;
  const W = 8.5 + sc * 0.6, D = 6, H = 3.3;
  const ruin = b.state === 'ruin';
  let height = H + 1.6;
  if (kind === 'store') {
    kit.box(M.stone, W, ruin ? H * 0.6 : H, D, 0, (ruin ? H * 0.6 : H) / 2, -0.4);
    door(kit, M, 2.6, 2.6, 0, 0, D / 2 - 0.4, { lintel: M.darkWood });
    if (!ruin) gabledRoof(kit, M.tile, M.stone, W, D, 1.1, H, -0.4, 0.3);
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
    kit.box(M.plaster, W - 0.2, bodyH, D - 0.2, 0, lift + 0.2 + bodyH / 2, -0.4);
    door(kit, M, 1.2, 1.7, 0, lift + 0.2, D / 2 - 0.5, { lintel: M.darkWood });
    if (!ruin) gabledRoof(kit, M.tile, M.plaster, W - 0.2, D - 0.2, 1.1, lift + 0.2 + bodyH, -0.4, 0.3);
    for (let k = 0; k < 5; k++) kit.box(M.wood, 1.0, 0.06, 0.12, 0, 0.2 + k * 0.22, D / 2 + 0.2 + k * 0.08);
    for (const sx of [-1, 1]) kit.box(M.wood, 0.08, 1.4, 0.08, sx * 0.48, 0.62, D / 2 + 0.42, 0, 0.32);
    height = lift + H + 1.4;
  } else {
    // 罐棚：后面一堵墙、一片单坡顶，下面三排双耳瓶
    kit.box(M.stone, W, H, 0.5, 0, H / 2, -D / 2 + 0.25);
    for (const sx of [-1, 1]) kit.box(M.stone, 0.5, H * 0.8, D * 0.7, sx * (W / 2 - 0.25), H * 0.4, -D * 0.15);
    for (const sx of [-1, -0.33, 0.33, 1]) kit.cylinder(M.wood, 0.1, 0.12, H - 0.4, sx * (W / 2 - 0.5), (H - 0.4) / 2, D / 2 - 0.6, 8);
    if (!ruin) shedRoof(kit, M.tile, W, D, 0.9, H - 0.3, -0.2, 0.3);
    for (let row = 0; row < 3; row++) for (let i = 0; i < Math.floor(W / 0.55) - 2; i++) {
      if (ruin && (i + row) % 3 === 0) continue;
      kit.add(M.terracotta, amphoraBody(), m4(-W / 2 + 0.9 + i * 0.55, 0, -D / 2 + 1.1 + row * 0.7, r() * 6, 0, 0, 0.82));
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
