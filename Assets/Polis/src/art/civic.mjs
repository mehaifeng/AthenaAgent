// 公共建筑（设计稿 5.3：能力做成公共建筑，在城里逛一圈就能发现）：神庙（每日神谕）、圆形图书馆（记忆）、锻炉（终端）、
// 市集、城门、广场（委托板、日晷与传令官）、港口与船；M2 加了港前的三处场馆——彩绘柱廊、书库、金库——
// 和橄榄林的台地。M2 的几座立在伸进海里的石台上（海边第 z = 2 行，账本永远不会把它分给文件夹）。
// 神庙、柱头、三陇板、棕叶饰取自打样。原点在地块中心，正面朝 +z。
import * as THREE from '../../vendor/three.bundle.mjs';
import {
  Kit, m4, deg, doricColumn, column, triglyphGeometry, palmette, gable, gabledRoof, frontGabledRoof, flatRoof, podium, door,
  window_, colonnade, groundPlane, litOpening,
} from './kit.mjs';
import { amphoraBody, statuePlinth, buildSack, buildNetRack, buildLamp } from './props.mjs';
import { STALLS, STALL, STALL_KINDS, stallToMarket } from '../market.mjs';
import { venueSlotLocal, TERRACE, PLOT_SIZE } from '../civic-layout.mjs';


// ------------------------------------------------------------------
// 多立克神庙（打样，参数化）：nx × nz 列，台基三级。scale 之前约为赫菲斯托斯神庙一类的比例。
// ------------------------------------------------------------------
export function buildTemple(M, { nx = 6, nz = 7 } = {}) {
  const kit = new Kit();
  const R1 = 0.39, H_SHAFT = 5.2, H_CAP = 0.408, H_COL = H_SHAFT + H_CAP;
  const AX = 2.55;
  const spanX = (nx - 1) * AX, spanZ = (nz - 1) * AX;
  const STEP_H = 0.33, STEP_T = 0.42, NSTEPS = 3;
  const styW = spanX + 1.6, styL = spanZ + 1.6;
  const yS = NSTEPS * STEP_H;

  for (let i = 0; i < NSTEPS; i++) {
    const grow = (NSTEPS - 1 - i) * STEP_T * 2;
    kit.box(M.marble, styW + grow, STEP_H, styL + grow, 0, i * STEP_H + STEP_H / 2, 0);
  }

  const colGeom = () => doricColumn(H_COL);
  let k = 0;
  for (let i = 0; i < nx; i++) for (let j = 0; j < nz; j++) {
    if (i !== 0 && i !== nx - 1 && j !== 0 && j !== nz - 1) continue;
    kit.instance('temple-column', colGeom, M.marble, m4(-spanX / 2 + i * AX, yS, -spanZ / 2 + j * AX, (k++) * 0.37));
  }

  const cellaW = spanX - 2 * 1.05 * AX, cellaL = spanZ - 2 * 1.45 * AX;
  kit.box(M.wall, cellaW, H_COL, cellaL, 0, yS + H_COL / 2, -0.2);
  litOpening(kit, M, 2.1, 4.1, 0.12, 0, yS + 2.05, -0.2 + cellaL / 2 + 0.02, true, 1.4);   // 神庙的大门：里面供着灯
  for (const sx of [-1, 1]) kit.box(M.wall, 0.6, H_COL, 0.5, sx * (cellaW / 2 - 0.3), yS + H_COL / 2, -0.2 + cellaL / 2 + 0.2);

  const yA = yS + H_COL;
  const entW = spanX + 1.28, entL = spanZ + 1.28;
  const ARCH_H = 0.74, TAENIA = 0.07, FRIEZE_H = 0.74, CROWN = 0.07, CORN_H = 0.34, OVER = 0.42;
  kit.box(M.marble, entW - 0.06, ARCH_H, entL - 0.06, 0, yA + ARCH_H / 2, 0);
  kit.box(M.marble, entW + 0.02, TAENIA, entL + 0.02, 0, yA + ARCH_H + TAENIA / 2, 0);
  const yF = yA + ARCH_H + TAENIA;
  kit.box(M.marble, entW - 0.18, FRIEZE_H, entL - 0.18, 0, yF + FRIEZE_H / 2, 0);

  const along = (n, len) => {
    const a = [];
    for (let i = 0; i <= 2 * (n - 1); i++) a.push(-((n - 1) * AX) / 2 + (i * AX) / 2);
    a[0] = -len / 2 + 0.27; a[a.length - 1] = len / 2 - 0.27;   // 角部三陇板顶到转角（古典多立克的做法）
    return a;
  };
  const tri = () => triglyphGeometry(0.5, FRIEZE_H);
  for (const x of along(nx, entW)) {
    kit.instance('temple-triglyph', tri, M.marble, m4(x, yF + FRIEZE_H / 2, entL / 2 - 0.07, 0));
    kit.instance('temple-triglyph', tri, M.marble, m4(x, yF + FRIEZE_H / 2, -(entL / 2 - 0.07), Math.PI));
  }
  for (const z of along(nz, entL)) {
    kit.instance('temple-triglyph', tri, M.marble, m4(entW / 2 - 0.07, yF + FRIEZE_H / 2, z, Math.PI / 2));
    kit.instance('temple-triglyph', tri, M.marble, m4(-(entW / 2 - 0.07), yF + FRIEZE_H / 2, z, -Math.PI / 2));
  }

  kit.box(M.marble, entW + 0.06, CROWN, entL + 0.06, 0, yF + FRIEZE_H + CROWN / 2, 0);
  const yC = yF + FRIEZE_H + CROWN;
  kit.box(M.marble, entW + 2 * OVER, CORN_H, entL + 2 * OVER, 0, yC + CORN_H / 2, 0);

  // 山墙与屋顶：13° 坡，前后山花三角退进檐口（屋脊沿 z，正面朝 +z）
  const yR = yC + CORN_H;
  const RW = entW + 2 * OVER, RL = entL + 2 * OVER;
  const pitch = deg(13);
  const ridge = (RW / 2) * Math.tan(pitch);
  const tym = new THREE.Shape();
  tym.moveTo(-RW / 2 + 0.35, 0); tym.lineTo(RW / 2 - 0.35, 0); tym.lineTo(0, ridge - 0.12); tym.closePath();
  const prism = new THREE.ExtrudeGeometry(tym, { depth: RL - 0.8, bevelEnabled: false });
  prism.translate(0, 0, -(RL - 0.8) / 2);
  kit.add(M.marble, prism, m4(0, yR, 0));
  const slopeLen = Math.hypot(RW / 2, ridge);
  for (const sz of [1, -1]) for (const sx of [1, -1]) {
    kit.box(M.marble, slopeLen + 0.25, 0.32, 0.72, sx * RW / 4, yR + ridge / 2 + 0.06, sz * (RL / 2 - 0.36), 0, 0, -sx * pitch);
  }
  for (const sx of [1, -1]) kit.box(M.roof, slopeLen + 0.35, 0.12, RL + 0.08, sx * RW / 4, yR + ridge / 2 + 0.25, 0, 0, 0, -sx * pitch);
  const tile = () => {
    const g = new THREE.CylinderGeometry(0.075, 0.075, slopeLen + 0.3, 6, 1, false, 0, Math.PI);
    g.rotateZ(Math.PI / 2);
    return g;
  };
  for (const sx of [1, -1]) for (let z = -RL / 2 + 0.3; z <= RL / 2 - 0.3; z += 0.62) {
    kit.instance('temple-tile', tile, M.roof, m4(sx * RW / 4, yR + ridge / 2 + 0.33, z, 0, 0, -sx * pitch));
  }
  kit.cylinder(M.roof, 0.13, 0.13, RL + 0.1, 0, yR + ridge + 0.26, 0, 10, Math.PI / 2);
  for (const sz of [1, -1]) {
    kit.add(M.marble, palmette(1.15, 0.85), m4(0, yR + ridge + 0.22, sz * (RL / 2 - 0.36)));
    for (const sx of [1, -1]) kit.add(M.marble, palmette(0.6, 0.45), m4(sx * (RW / 2 - 0.25), yR + 0.2, sz * (RL / 2 - 0.36)));
  }
  const group = kit.build();
  group.userData.height = yR + ridge + 1.3;
  group.userData.front = styL / 2 + (NSTEPS - 1) * STEP_T;
  return group;
}

// ------------------------------------------------------------------

// ------------------------------------------------------------------
// 公共建筑
// ------------------------------------------------------------------

/** 图书馆（记忆）：圆形的 tholos，一圈柱子、圆筒内殿、锥形瓦顶，一眼就和柱廊书库分得开。 */
export function buildTholos(M) {
  const kit = new Kit();
  const R = 5.1, colH = 4.0, n = 12;
  for (let i = 0; i < 3; i++) kit.cylinder(M.marble, R + 0.9 - i * 0.35, R + 0.9 - i * 0.35, 0.25, 0, 0.125 + i * 0.25, 0, 48);
  const y0 = 0.75;
  for (let i = 0; i < n; i++) {
    const a = (i / n) * Math.PI * 2;
    kit.instance('tholos-col', () => doricColumn(colH, { detail: 'low' }), M.marble, m4(Math.sin(a) * (R - 0.35), y0, Math.cos(a) * (R - 0.35), a));
  }
  kit.cylinder(M.wall, R - 1.6, R - 1.6, colH, 0, y0 + colH / 2, 0, 40);
  litOpening(kit, M, 1.3, 2.6, 0.2, 0, y0 + 1.3, R - 1.56, true, 1.2);
  kit.cylinder(M.marble, R + 0.15, R + 0.15, 0.55, 0, y0 + colH + 0.27, 0, 48);
  kit.add(M.tile, new THREE.ConeGeometry(R + 0.35, 2.9, 48, 1, true), m4(0, y0 + colH + 0.55 + 1.45, 0));
  kit.add(M.marble, palmette(0.9, 0.6), m4(0, y0 + colH + 3.3, 0));
  const group = kit.build();
  group.userData.height = y0 + colH + 4.4;
  return group;
}

/** 锻炉：敞开的炉棚，铁砧、炉膛、风箱。炉火不发光——光只属于雅典娜，她来干活这里才亮。 */
export function buildForge(M) {
  const kit = new Kit();
  const W = 8.4, D = 6.4, H = 3.4;
  kit.box(M.stone, W + 0.4, 0.2, D + 0.4, 0, 0.1, -0.6);
  kit.box(M.stone, W, H, 0.5, 0, 0.2 + H / 2, -D / 2 - 0.35);
  for (const sx of [-1, 1]) kit.box(M.stone, 0.5, H, D - 1.4, sx * (W / 2 - 0.25), 0.2 + H / 2, -1.25);
  gabledRoof(kit, M.tile, M.stone, W, D, 1.3, 0.2 + H, -0.6, 0.4);
  for (const sx of [-1, 1]) kit.cylinder(M.wood, 0.14, 0.16, H, sx * (W / 2 - 0.3), 0.2 + H / 2, D / 2 - 0.9, 10);
  // 炉膛（后墙下）、烟囱
  kit.box(M.stone, 2.4, 1.1, 1.4, -1.2, 0.75, -D / 2 + 0.5);
  kit.box(M.dark, 1.6, 0.25, 0.9, -1.2, 1.35, -D / 2 + 0.55);
  kit.cylinder(M.stone, 0.45, 0.6, 3.5, -1.2, 0.2 + H + 1.2, -D / 2 - 0.2, 12);
  // 铁砧和木墩。M3 第三轮：砧面从地坪往上 0.775 米（齐手指关节，抡锤落下的高度）——第一版高到 0.92，锤得砸在胸口；
  // 铁砧和石料挪到炉棚的前沿（z ≈ 1.9）：在里面 3 米深的地方，33° 的镜头被檐口挡住人的上半身
  const FLOOR = 0.2, ANVIL_TOP = FLOOR + 0.775, AZ = 1.85;
  kit.cylinder(M.darkWood, 0.42, 0.48, 0.47, 0.95, FLOOR + 0.235, AZ, 14);
  kit.box(M.bronze, 0.9, 0.305, 0.36, 0.95, ANVIL_TOP - 0.1525, AZ);
  kit.box(M.bronze, 0.35, 0.16, 0.26, 1.55, ANVIL_TOP - 0.1, AZ);
  kit.box(M.dark, 0.16, 0.03, 0.05, 0.9, ANVIL_TOP + 0.015, AZ - 0.04);   // 砧面上那根待锻的铁条（不发光——光属于雅典娜）
  kit.box(M.wood, 1.6, 0.9, 0.7, 2.6, 0.45, -1.8);   // 风箱架
  // 工匠的石料：木架上一块凿了一半的大理石（一面已经有了人形的轮廓）。凿的是石料西面靠南的那一角——
  // 镜头从东南看过来，石料不挡住他的手
  const BX = -1.45, BZ = 1.8, TIP = { x: -1.66, y: FLOOR + 0.012 + 1.02, z: 1.95 };
  kit.box(M.wood, 0.6, 0.72, 0.5, BX + 0.03, FLOOR + 0.36, BZ);
  kit.box(M.marble, 0.42, 0.62, 0.42, BX, FLOOR + 0.72 + 0.31, BZ);
  kit.add(M.marble, new THREE.SphereGeometry(0.1, 12, 8), m4(BX - 0.23, FLOOR + 1.36, BZ - 0.06, 0, 0, 0, 0.5, 1.2, 1));
  for (let i = 0; i < 3; i++) kit.add(M.tile, amphoraBody(), m4(-W / 2 + 0.9 + i * 0.5, 0.2, D / 2 - 0.3, i, 0, 0, 0.8));
  const group = kit.build();
  group.userData.height = 0.2 + H + 3;
  // 锻炉里的两个工位（锻炉的局部坐标；world.mjs 按锻炉的摆放矩阵换到世界里）：at 是活儿落下的那一点（砧面、石面），
  // stand 是人站的地方（地坪的高度）、yaw 是他面朝的方向。数值和 poses.ARMS 解接触点时用的那一组对得上——解的时候用的就是
  // 铁匠、工匠自己的骨架（按各自的身高缩放过），所以下面的米数直接用：锤面落在身前 0.6 米、偏右 0.08 米、比根高 0.79 米；
  // 凿刃抵在身前 0.5 米、偏左 0.03 米、比根高 1.02 米（根比脚下的地面高 0.012 米）
  group.userData.anvil = new THREE.Vector3(0.9, ANVIL_TOP, AZ - 0.04);
  group.userData.stations = {
    // 铁匠面朝南（朝着镜头那一侧）隔着铁砧抡锤；工匠面朝东凿石头——镜头从东南看过来，两个人的手上活儿都看得见
    forge: { at: new THREE.Vector3(0.9, ANVIL_TOP, AZ - 0.04), stand: new THREE.Vector3(0.9 + 0.08, FLOOR, AZ - 0.04 - 0.6), yaw: 0 },
    carve: { at: new THREE.Vector3(TIP.x, TIP.y, TIP.z), stand: new THREE.Vector3(TIP.x - 0.5, FLOOR, TIP.z + 0.03), yaw: Math.PI / 2 },
  };
  // 锻炉的地坪比街面高 0.2 米：站在上面的人（铁匠、工匠、你、来干活的雅典娜）脚落在地坪上，不陷进去
  group.userData.floors = [{ minX: -W / 2 - 0.2, maxX: W / 2 + 0.2, minZ: -0.6 - D / 2 - 0.2, maxZ: -0.6 + D / 2 + 0.2, y: FLOOR }];
  return group;
}

/**
 * 市集（2026-10-15，用户：扩大市集规模、设计有职业特点的摊位）：占满它那块地——十个布篷摊（market.mjs 的 STALLS：北边四个、
 * 东西各两个朝着中间的空场，南边两个朝街），中间空着让人逛，南边两摊之间是入口。每个摊卖一样东西（面包、陶器、布、凉鞋、面粉、
 * 木器、鱼、果蔬、油酒、杂货），货摆在柜台上、挂在篷下；没开市、摊主不在时货收起来、柜台盖一块布（userData.stalls[k] 的
 * wares / cover，world.setStallOpen 切换——两样都在柜台的足迹里，切换不改变哪里能走）。
 * stallCount 是合并进市集的文件夹数：它们的名牌挂在商贩的那几个摊上（stallSpots，多了轮着挂）。
 */
export function buildMarket(M, stallCount) {
  void stallCount;
  const kit = new Kit();
  kit.add(M.pave, groundPlane(12.4, 12.4, 4), m4(0, 0.02, 0));
  const stalls = [];
  const shades = M.clothShades;
  for (const st of STALLS) {
    const group = new THREE.Group();
    group.position.set(st.x, 0, st.z);
    group.rotation.y = st.yaw;
    const frame = new Kit();
    stallFrame(frame, M, shades[st.index % shades.length]);
    frame.build(group);
    const wares = new Kit();
    stallWares(wares, M, st.kind, st.index);
    const waresGroup = wares.build();
    waresGroup.name = `wares:${st.kind}`;
    group.add(waresGroup);
    const cover = new Kit();
    const c = STALL.counter;
    // 收市：柜台上盖一块布，前后垂下来一点——不出柜台的边（出一点，足迹就多出几格，开市收市哪里能走就变了；market.test 量过）
    cover.box(M.hide, c.x1 - c.x0, 0.05, c.z1 - c.z0, (c.x0 + c.x1) / 2, c.h + 0.07, (c.z0 + c.z1) / 2);
    for (const sz of [-1, 1]) cover.box(M.hide, c.x1 - c.x0, 0.16, 0.012, (c.x0 + c.x1) / 2, c.h + 0.0, (c.z0 + c.z1) / 2 + sz * ((c.z1 - c.z0) / 2 - 0.006));
    cover.box(M.hide, 0.5, 0.09, 0.3, c.x0 + 0.4, c.h + 0.14, (c.z0 + c.z1) / 2, 0.2);   // 布底下鼓起来的一摞货
    const coverGroup = cover.build();
    coverGroup.name = `cover:${st.kind}`;
    coverGroup.visible = false;
    group.add(coverGroup);
    kit.object(group);
    stalls.push({ index: st.index, kind: st.kind, wares: waresGroup, cover: coverGroup });
  }
  const group = kit.build();
  group.userData.height = 3.0;
  group.userData.stalls = stalls;
  // 文件夹合并成的摊位：名牌挂在商贩的摊上（market.assignVendors 让这些摊主先占这几个摊，名牌和人对得上）
  group.userData.stallSpots = STALL_KINDS.filter((k) => k.folder).map((k) => {
    const st = STALLS.find((s) => s.kind === k.kind);
    return stallToMarket(st, -0.5, 0.2);
  });
  return group;
}

/** 一个摊的架子（摊的坐标，+z 朝买主）：四根柱、前低后高的布篷（篷沿一道颜色）、柜台（左边一截），右边是摊主进出的口子。 */
function stallFrame(kit, M, shade) {
  const S = STALL, c = S.counter, A = S.awning;
  const heightAt = (z) => A.back + ((A.front - A.back) * (z - A.z0)) / (A.z1 - A.z0);
  for (const [x, z] of S.posts) { const h = heightAt(z) - 0.02; kit.cylinder(M.wood, 0.045, 0.055, h, x, h / 2, z, 6); }
  // 布篷：后高前低、只遮后半个摊，有颜色的布、前沿一道本色的布边（2.0 米以上，在身高带之上）；两侧斜撑在头顶上
  const depth = A.z1 - A.z0, tilt = Math.atan2(A.back - A.front, depth);
  const mid = (A.z0 + A.z1) / 2;
  kit.box(shade, 2.7, 0.04, Math.hypot(depth, A.back - A.front), 0, (A.back + A.front) / 2 + 0.04, mid, 0, tilt);
  for (let k = -2; k <= 2; k++) kit.box(M.awning, 0.16, 0.045, Math.hypot(depth, A.back - A.front) + 0.005, k * 0.54, (A.back + A.front) / 2 + 0.042, mid, 0, tilt);   // 本色的条纹
  kit.box(M.awning, 2.72, 0.16, 0.03, 0, A.front - 0.02, A.z1 + 0.01);
  // 篷下一根横杆（后面），挂货用
  kit.cylinder(M.darkWood, 0.022, 0.022, 2.4, 0, 1.98, -0.95, 6, 0, Math.PI / 2);
  // 柜台：木板台面、前脸一块挡板、两头两根腿
  kit.box(M.wood, c.x1 - c.x0, 0.06, c.z1 - c.z0, (c.x0 + c.x1) / 2, c.h - 0.03, (c.z0 + c.z1) / 2);
  kit.box(M.darkWood, c.x1 - c.x0, c.h - 0.12, 0.04, (c.x0 + c.x1) / 2, (c.h - 0.06) / 2, c.z1 - 0.03);
  for (const x of [c.x0 + 0.05, c.x1 - 0.05]) kit.box(M.darkWood, 0.06, c.h - 0.06, c.z1 - c.z0 - 0.06, x, (c.h - 0.06) / 2, (c.z0 + c.z1) / 2);
  kit.box(shade, c.x1 - c.x0 - 0.1, 0.22, 0.012, (c.x0 + c.x1) / 2, c.h - 0.22, c.z1 + 0.002);   // 前脸上一块有颜色的布帘
}

/** 一个摊卖的东西（摊的坐标；柜台面在 STALL.counter.h）：都在柜台的足迹里，或者挂在 1.65 米以上、或者矮于 0.35 米放在地上。 */
function stallWares(kit, M, kind, index) {
  const c = STALL.counter, top = c.h;
  const cx = (c.x0 + c.x1) / 2, cz = (c.z0 + c.z1) / 2;
  const along = (k, n) => c.x0 + 0.14 + ((c.x1 - c.x0 - 0.28) * k) / Math.max(1, n - 1);
  const r = (k) => ((Math.sin(index * 12.9898 + k * 78.233) * 43758.5453) % 1 + 1) % 1;
  const bowl = (mat, rad, x, z, y = top) => kit.add(mat, new THREE.LatheGeometry([[0.02, 0], [rad * 0.8, 0.01], [rad, rad * 0.55], [rad * 0.94, rad * 0.58], [rad * 0.72, rad * 0.12]].map(([a, b]) => new THREE.Vector2(a, b)), 14), m4(x, y, z));
  const hang = (mat, w, h, x, z) => { kit.box(mat, w, h, 0.012, x, 1.98 - h / 2 - 0.03, z); kit.cylinder(M.dark, 0.004, 0.004, 0.06, x, 1.95, z, 4); };
  switch (kind) {
    case 'bread': {
      // 圆面包一排、两只环形的芝麻饼、一篮长面包
      for (let k = 0; k < 5; k++) kit.add(M.crust, new THREE.SphereGeometry(0.085, 12, 8), m4(along(k, 5), top + 0.035, cz - 0.08 + (k % 2) * 0.12, 0, 0, 0, 1, 0.55, 1));
      for (const [x, z] of [[cx - 0.25, cz + 0.12], [cx + 0.2, cz + 0.12]]) kit.add(M.crust, new THREE.TorusGeometry(0.07, 0.028, 8, 16), m4(x, top + 0.03, z, 0, Math.PI / 2));
      kit.add(M.wood, new THREE.CylinderGeometry(0.2, 0.16, 0.12, 14, 1, true), m4(c.x0 + 0.3, 0.06, -0.55));
      for (let k = 0; k < 4; k++) kit.add(M.crust, new THREE.CapsuleGeometry(0.035, 0.22, 4, 8), m4(c.x0 + 0.25 + k * 0.04, 0.16, -0.55, 0.3 * k, 0, Math.PI / 2 - 0.4));
      break;
    }
    case 'pottery': {
      // 小双耳瓶、碗、酒杯；地上一只大水罐（矮于 0.35 米）
      for (let k = 0; k < 3; k++) kit.add(M.terracotta, amphoraBody(), m4(along(k, 4), top, cz - 0.05, k, 0, 0, 0.32));
      bowl(M.glaze, 0.1, along(3, 4), cz + 0.08);
      bowl(M.terracotta, 0.08, along(1, 4) + 0.18, cz + 0.12);
      kit.add(M.terracotta, amphoraBody(), m4(-0.9, 0, -0.55, 0.4, 0, 0, 0.4));
      break;
    }
    case 'cloth': {
      // 叠好的几匹布、篷下挂着三幅
      for (let k = 0; k < 3; k++) for (let j = 0; j < 2 + (k % 2); j++) kit.box(M.clothShades[(k + j + index) % 5], 0.32, 0.06, 0.26, along(k, 3), top + 0.03 + j * 0.062, cz, 0.05 * (j - 1));
      for (let k = 0; k < 3; k++) hang(M.clothShades[(k + 2 + index) % 5], 0.5, 0.3, -0.8 + k * 0.62, -0.95);
      break;
    }
    case 'sandals': {
      // 几双凉鞋摆在台上，几只挂在右边那根中柱上（1.7 米以上）
      for (let k = 0; k < 4; k++) {
        for (const sx of [-1, 1]) {
          const x = along(k, 4) + sx * 0.05, z = cz;
          kit.box(M.leather, 0.08, 0.018, 0.24, x, top + 0.01, z, 0.08 * sx);
          kit.box(M.hide, 0.085, 0.012, 0.025, x, top + 0.026, z + 0.05);
          kit.box(M.hide, 0.085, 0.012, 0.025, x, top + 0.026, z - 0.04);
        }
      }
      for (let k = 0; k < 3; k++) kit.box(M.leather, 0.09, 0.22, 0.02, 1.13, 1.85, STALL.posts[3][1] - 0.08 + k * 0.08);
      break;
    }
    case 'grain': {
      // 三只小麻袋、一只敞口的袋子露着麦粒，一把木勺；量斗
      for (let k = 0; k < 2; k++) {
        const sack = buildSack(M);
        sack.scale.setScalar(0.42);
        sack.position.set(along(k * 2, 4), top, cz - 0.04);
        kit.object(sack);
      }
      kit.add(M.awning, new THREE.CylinderGeometry(0.12, 0.13, 0.16, 12, 1, true), m4(along(1, 4) + 0.1, top + 0.08, cz));
      kit.add(M.wheat, new THREE.SphereGeometry(0.12, 12, 6, 0, Math.PI * 2, 0, Math.PI / 2), m4(along(1, 4) + 0.1, top + 0.14, cz, 0, 0, 0, 1, 0.45, 1));
      kit.add(M.wood, new THREE.CylinderGeometry(0.075, 0.07, 0.1, 10), m4(along(3, 4), top + 0.05, cz + 0.08));
      for (let k = 0; k < 3; k++) {
        const sack = buildSack(M);
        sack.scale.setScalar(0.46);
        sack.position.set(-1.0 + k * 0.42, 0, -0.62);
        kit.object(sack);
      }
      break;
    }
    case 'wood': {
      // 木碗、木勺、一只小凳（放在柜台后面的地上，矮）、挂着两把木勺
      for (let k = 0; k < 3; k++) bowl(M.wood, 0.09 + 0.02 * (k % 2), along(k, 4), cz + (k % 2 ? 0.08 : -0.06));
      for (let k = 0; k < 3; k++) kit.box(M.darkWood, 0.03, 0.012, 0.22, along(3, 4) - 0.05 + k * 0.05, top + 0.006, cz, 0.15 * (k - 1));
      kit.box(M.wood, 0.36, 0.04, 0.28, -0.85, 0.3, -0.55);
      for (const sx of [-1, 1]) for (const sz of [-1, 1]) kit.box(M.darkWood, 0.035, 0.28, 0.035, -0.85 + sx * 0.14, 0.14, -0.55 + sz * 0.1);
      for (let k = 0; k < 2; k++) hang(M.wood, 0.05, 0.28, 0.3 + k * 0.25, -0.95);
      break;
    }
    case 'produce': {
      // 一筐卷心菜、一筐葡萄、一堆石榴；篷下挂两串葡萄
      const basket = (x, z, fill, n, rad) => {
        kit.add(M.wood, new THREE.CylinderGeometry(0.17, 0.14, 0.1, 14, 1, true), m4(x, top + 0.05, z));
        kit.cylinder(M.wood, 0.14, 0.14, 0.01, x, top + 0.005, z, 12);
        for (let k = 0; k < n; k++) {
          const a = (k / n) * Math.PI * 2 + r(k);
          const d = k === 0 ? 0 : 0.085;
          kit.add(fill, new THREE.SphereGeometry(rad, 9, 7), m4(x + Math.cos(a) * d, top + 0.1 + (k === 0 ? 0.04 : 0), z + Math.sin(a) * d));
        }
      };
      basket(along(0, 3), cz, M.greens, 5, 0.065);
      basket(along(1, 3), cz, M.grape, 9, 0.032);
      for (let k = 0; k < 5; k++) kit.add(M.terracotta, new THREE.SphereGeometry(0.045, 9, 7), m4(along(2, 3) + (k % 3 - 1) * 0.08, top + 0.045 + (k >= 3 ? 0.06 : 0), cz + (k >= 3 ? 0 : (k % 2) * 0.06 - 0.03)));
      for (let k = 0; k < 2; k++) {
        for (let j = 0; j < 7; j++) kit.add(M.grape, new THREE.SphereGeometry(0.025, 7, 5), m4(-0.6 + k * 0.7 + (j % 2) * 0.03, 1.9 - j * 0.03, -0.95 + (j % 3) * 0.02));
      }
      break;
    }
    case 'oil': {
      // 一排小油瓶、两只酒壶，柜台后面地上一只大瓶（矮于 0.35）
      for (let k = 0; k < 4; k++) kit.add(k % 2 ? M.terracotta : M.marble, new THREE.LatheGeometry([[0, 0], [0.04, 0], [0.05, 0.08], [0.045, 0.16], [0.018, 0.2], [0.02, 0.24], [0, 0.24]].map(([a, b]) => new THREE.Vector2(a, b)), 12), m4(along(k, 5), top, cz - 0.05));
      kit.add(M.glaze, new THREE.LatheGeometry([[0, 0], [0.06, 0], [0.09, 0.08], [0.08, 0.17], [0.04, 0.22], [0.045, 0.26], [0, 0.26]].map(([a, b]) => new THREE.Vector2(a, b)), 14), m4(along(4, 5), top, cz + 0.05));
      bowl(M.glaze, 0.085, along(2, 5) + 0.1, cz + 0.13);
      kit.add(M.terracotta, amphoraBody(), m4(-0.95, 0, -0.6, 0.6, 0, 0, 0.4));
      break;
    }
    case 'fish': {
      // 一块铺着叶子的木板上摆着鱼；一篮小鱼；篷下挂着一串鱼干
      kit.box(M.darkWood, c.x1 - c.x0 - 0.2, 0.03, c.z1 - c.z0 - 0.08, cx, top + 0.015, cz);
      kit.box(M.greens, c.x1 - c.x0 - 0.3, 0.008, c.z1 - c.z0 - 0.14, cx, top + 0.034, cz);
      for (let k = 0; k < 5; k++) {
        const x = along(k, 5), z = cz + (k % 2 ? 0.06 : -0.06);
        kit.add(M.fish, new THREE.SphereGeometry(0.1, 10, 6), m4(x, top + 0.06, z, 0.25 * (k % 2 ? 1 : -1), 0, 0, 0.32, 0.26, 1));
        kit.add(M.fish, new THREE.ConeGeometry(0.035, 0.07, 4), m4(x + 0.025 * (k % 2 ? 1 : -1), top + 0.06, z - 0.12, 0, Math.PI / 2, 0, 1, 1, 0.3));
      }
      kit.add(M.wood, new THREE.CylinderGeometry(0.2, 0.16, 0.14, 14, 1, true), m4(-0.85, 0.07, -0.55));
      for (let k = 0; k < 6; k++) kit.add(M.fish, new THREE.SphereGeometry(0.05, 8, 5), m4(-0.85 + Math.cos(k) * 0.08, 0.13, -0.55 + Math.sin(k) * 0.08, k, 0, 0, 0.4, 0.4, 1));
      for (let j = 0; j < 4; j++) kit.add(M.hide, new THREE.SphereGeometry(0.06, 8, 5), m4(0.35, 1.88 - j * 0.07, -0.95, 0, 0, 0, 0.35, 1, 0.2));
      break;
    }
    default: {
      // 杂货：油灯、小篮子、一卷绳子、几只小木盒
      for (let k = 0; k < 3; k++) {
        const lamp = buildLamp(M);
        lamp.position.set(along(k, 5), top + 0.02, cz - 0.06);
        lamp.rotation.y = k * 0.7;
        kit.object(lamp);
      }
      kit.add(M.hide, new THREE.TorusGeometry(0.07, 0.03, 8, 16), m4(along(3, 5), top + 0.03, cz + 0.05, 0, Math.PI / 2));
      kit.box(M.darkWood, 0.14, 0.09, 0.1, along(4, 5), top + 0.045, cz - 0.04, 0.3);
      kit.box(M.wood, 0.12, 0.07, 0.09, along(4, 5) - 0.04, top + 0.035, cz + 0.11, -0.2);
      kit.add(M.wood, new THREE.CylinderGeometry(0.12, 0.1, 0.1, 12, 1, true), m4(along(2, 5) + 0.12, top + 0.05, cz + 0.1));
      for (let k = 0; k < 2; k++) hang(M.clothShades[(k + index) % 5], 0.3, 0.25, -0.6 + k * 0.5, -0.95);
      break;
    }
  }
}

/** 城门：一座小门楼（propylon）横跨街口：两根方柱、过梁、三角楣。 */
export function buildGate(M) {
  const kit = new Kit();
  for (const sx of [-1, 1]) {
    kit.box(M.marble, 1.2, 4.6, 1.4, sx * 2.6, 2.3, 0);
    kit.box(M.stone, 7, 2.2, 0.8, sx * 6.6, 1.1, 0);   // 两侧的一段城墙
  }
  kit.box(M.marble, 6.6, 0.7, 1.6, 0, 4.95, 0);
  kit.add(M.marble, gable(6.8, 1.2, 1.6), m4(0, 5.3, 0));
  const group = kit.build();
  group.userData.height = 7.2;
  return group;
}

/** 港口：港前广场（铺地、货堆、一尊海神像）、伸进海里的栈桥、系船柱。船是单独的动画件，见 buildBoat。 */
export function buildHarbor(M, quayLocalZ, pierLength, plotCenterZ, statueGeometry) {
  const kit = new Kit();
  kit.add(M.pave, groundPlane(12.4, 12.4, 4), m4(0, 0.016, plotCenterZ));
  statuePlinth(kit, M, { z: plotCenterZ - 2.5, width: 1.2, height: 1.1 });   // 第四轮：海神像立在带刻字带的台座上
  kit.add(M.marble, statueGeometry, m4(0, 1.1, plotCenterZ - 2.5, 0, 0, 0, 1.25));
  // 第四轮（需求 9）：货堆旁几只扎口的麻袋，港前一副晾着渔网的架子
  const placed = (obj, x, z, ry = 0) => { obj.position.set(x, 0, z); obj.rotation.y = ry; return kit.object(obj); };
  for (let i = 0; i < 4; i++) placed(buildSack(M), -4.6 + i * 0.5, plotCenterZ + 3.4 + (i % 2) * 0.35, i * 0.7);
  placed(buildNetRack(M), 4.6, plotCenterZ - 3.6);
  for (let i = 0; i < 6; i++) kit.box(M.wood, 0.75, 0.75, 0.75, -4 + (i % 3) * 0.85, 0.375 + (i >= 3 ? 0.75 : 0), plotCenterZ + 2.5, i * 0.2);
  for (let i = 0; i < 6; i++) kit.add(M.tile, amphoraBody(), m4(3 + (i % 3) * 0.5, 0, plotCenterZ + 2 + Math.floor(i / 3) * 0.55, i, 0, 0, 0.85));
  kit.box(M.stone, 3.2, 0.9, pierLength, 0, -0.35, quayLocalZ + pierLength / 2);
  for (let z = quayLocalZ + 2; z < quayLocalZ + pierLength; z += 3.2) {
    for (const sx of [-1, 1]) kit.cylinder(M.stone, 0.16, 0.2, 0.55, sx * 1.35, 0.35, z, 8);
  }
  for (let i = 0; i < 4; i++) kit.box(M.wood, 0.7, 0.7, 0.7, -3 + (i % 2) * 0.8, 0.35 + (i >= 2 ? 0.7 : 0), quayLocalZ - 2.5, i * 0.3);
  for (let i = 0; i < 5; i++) kit.add(M.terracotta, amphoraBody(), m4(2.2 + (i % 3) * 0.5, 0, quayLocalZ - 2.2 - Math.floor(i / 3) * 0.5, i, 0, 0, 0.85));
  const group = kit.build();
  group.userData.height = 2;
  // 栈桥的桥面比街面高 0.1 米（石台顶在 -0.35 + 0.45）：走上去的人脚落在桥面上，不陷进石头里（world.groundAt）
  group.userData.floors = [{ minX: -1.6, maxX: 1.6, minZ: quayLocalZ, maxZ: quayLocalZ + pierLength, y: 0.1 }];
  // 水手的工位：站在栈桥东沿、面朝泊着的船收缆（2026-10-12，用户：水手在错误的位置牵拉绳子——第一版他站在港前广场上，
  // 离船 17 米，缆绳一头"在他身前 2.6 米"，落在铺地上，什么也没拴着）。at 是缆绳越过桥沿的那一点（桥外就是水，挡路）；
  // 缆绳的另一头拴在船的西舷上（BOAT_CLEAT），每帧跟着船
  group.userData.stations = {
    haul: { stand: new THREE.Vector3(0.98, 0.1, quayLocalZ + 10), at: new THREE.Vector3(1.62, 0.1, quayLocalZ + 10), yaw: Math.PI / 2, rope: 'boat' },
  };
  return group;
}

/** 船泊在哪（x、y，z 从码头大街量起）：栈桥东侧，船的中线离栈桥中线 3.8 米，西舷离桥沿 1 米多。水手的工位按它摆。 */
export const BOAT_MOORING = Object.freeze([3.8, -0.05, 9.5]);

/** 船上拴缆的那一点（船的坐标系）：西舷的舷栏、船身中段稍偏船头——水手收的缆从这里拉到栈桥上。 */
export const BOAT_CLEAT = Object.freeze([-1.0, 0.42, 0.5]);

/** 一条小商船：船身是车削体压扁，一根桅杆、收着的帆。 */
export function buildBoat(M) {
  const kit = new Kit();
  const pts = [];
  for (let i = 0; i <= 12; i++) {
    const t = i / 12;
    pts.push(new THREE.Vector2(Math.max(0.02, Math.sin(Math.PI * t) * 1.15), (t - 0.5) * 9));
  }
  // 半个车削体：phi ∈ [-90°, 90°] 绕 x 转 90° 之后落在水线以下，开口朝上
  const hull = new THREE.LatheGeometry(pts, 20, -Math.PI / 2, Math.PI);
  hull.rotateX(Math.PI / 2);
  hull.scale(1, 0.85, 1);
  kit.add(M.darkWood, hull, m4(0, 0.15, 0));
  // 甲板是船形的（两头尖），沿船舷一道浅色的舷栏：城里的船泡在水里、只露出甲板，
  // M2 美术审查时第一版的甲板是一块长方形的深色木板，俯视是一块搭在水上的木板，不是一条船
  const halfW = (t) => Math.max(0.03, Math.sin(Math.PI * t) * 1.06);
  const outline = Array.from({ length: 25 }, (_, i) => [halfW(i / 24), (i / 24 - 0.5) * 8.8]);
  const deck = new THREE.Shape();
  deck.moveTo(0, -4.4);
  for (const [w, z] of outline) deck.lineTo(w, z);
  for (const [w, z] of outline.slice().reverse()) deck.lineTo(-w, z);
  const deckGeo = new THREE.ExtrudeGeometry(deck, { depth: 0.08, bevelEnabled: false });
  deckGeo.rotateX(Math.PI / 2);
  kit.add(M.wood, deckGeo, m4(0, 0.2, 0));
  const rail = new THREE.CatmullRomCurve3([
    ...outline.map(([w, z]) => new THREE.Vector3(w * 0.96, 0.42, z * 0.98)),
    ...outline.slice(1, -1).reverse().map(([w, z]) => new THREE.Vector3(-w * 0.96, 0.42, z * 0.98)),
  ], true);
  kit.add(M.papyrus, new THREE.TubeGeometry(rail, 96, 0.05, 6, true));
  kit.cylinder(M.darkWood, 0.09, 0.12, 1.6, 0, 0.9, -4.7, 8, -0.75);   // 船尾翘起的尾饰
  kit.cylinder(M.wood, 0.08, 0.1, 6, 0, 3.1, 0.4, 8);
  kit.cylinder(M.wood, 0.05, 0.05, 3.6, 0, 5.2, 0.4, 6, 0, Math.PI / 2);
  kit.add(M.wood, new THREE.CylinderGeometry(0.1, 0.25, 1.2, 8), m4(0, 0.9, -4.6, 0, -0.6));
  // —— 第四轮（需求 9，2026-10-15，用户：给船只多加一点细节）——
  // 舷墙：甲板到舷栏之间一圈木板墙，外面两道叠板的缝
  const side = (sgn, y0, y1, out = 0) => {
    const pos = [];
    const idx = [];
    outline.forEach(([w, z], i) => {
      pos.push(sgn * (w * 0.97 + out), y0, z * 0.985, sgn * (w * 0.99 + out), y1, z * 0.985);
      if (i > 0) { const a = (i - 1) * 2; idx.push(a, a + 1, a + 2, a + 1, a + 3, a + 2); }
    });
    const g = new THREE.BufferGeometry();
    g.setAttribute('position', new THREE.Float32BufferAttribute(pos, 3));
    g.setIndex(sgn > 0 ? idx : idx.map((_, k) => idx[k - (k % 3) + (2 - (k % 3))]));
    g.computeVertexNormals();
    return g;
  };
  for (const sgn of [-1, 1]) {
    kit.add(M.darkWood, side(sgn, 0.16, 0.42));
    for (const y of [0.24, 0.33]) kit.add(M.wood, side(sgn, y, y + 0.018, 0.012));   // 叠板的缝
  }
  // 船头的眼睛（ophthalmos）：两舷各一只，白的眼白、深色的瞳仁——点缀色不用在船上
  for (const sgn of [-1, 1]) {
    const w = halfW(0.86);
    kit.add(M.papyrus, new THREE.CircleGeometry(0.13, 16), m4(sgn * (w + 0.03), 0.3, 3.15, sgn * Math.PI / 2 + sgn * 0.28));
    kit.add(M.dark, new THREE.CircleGeometry(0.06, 12), m4(sgn * (w + 0.04), 0.3, 3.15, sgn * Math.PI / 2 + sgn * 0.28));
  }
  // 撞角：船头水线上一只铜包的尖角
  kit.add(M.bronze, new THREE.ConeGeometry(0.13, 0.75, 8), m4(0, 0.16, 4.75, 0, Math.PI / 2));
  kit.add(M.bronze, new THREE.TorusGeometry(0.13, 0.025, 6, 12), m4(0, 0.16, 4.4));
  // 收起来的桨：靠着两舷顺着船身搁在横坐板上（泊在栈桥边，伸出去的桨会扫进栈桥），桨叶朝船尾；坐板一道道横在舷墙之间
  for (let k = 0; k < 5; k++) {
    const z = -2.6 + k * 1.25;
    kit.box(M.wood, 2 * halfW(z / 8.8 + 0.5) * 0.92, 0.05, 0.16, 0, 0.3, z);
  }
  for (const sgn of [-1, 1]) {
    for (let k = 0; k < 4; k++) {
      const x = sgn * (0.42 + k * 0.1);
      const a = new THREE.Vector3(x, 0.36 + k * 0.012, 2.4 - k * 0.15), b = new THREE.Vector3(x * 0.92, 0.42 + k * 0.012, -2.2 - k * 0.15);
      const dir = b.clone().sub(a);
      const oar = new THREE.CylinderGeometry(0.022, 0.022, dir.length(), 6);
      oar.applyQuaternion(new THREE.Quaternion().setFromUnitVectors(new THREE.Vector3(0, 1, 0), dir.clone().normalize()));
      oar.translate((a.x + b.x) / 2, (a.y + b.y) / 2, (a.z + b.z) / 2);
      kit.add(M.wood, oar);
      kit.box(M.wood, 0.13, 0.02, 0.5, b.x, b.y, b.z - 0.2);
    }
  }
  // 桅杆的支索：桅顶拉到船头、船尾与两舷
  const rope = (from, to) => kit.add(M.hide, new THREE.TubeGeometry(new THREE.LineCurve3(new THREE.Vector3(...from), new THREE.Vector3(...to)), 1, 0.014, 4, false));
  rope([0, 6.05, 0.4], [0, 0.45, 4.3]);
  rope([0, 6.05, 0.4], [0, 0.6, -4.1]);
  for (const sgn of [-1, 1]) rope([0, 5.9, 0.4], [sgn * 0.95, 0.42, 0.4]);
  for (const sgn of [-1, 1]) rope([sgn * 1.75, 5.2, 0.4], [sgn * 0.9, 0.42, -1.2]);   // 横桁两头的转帆索
  // 横桁上卷起来的帆，几道系绳
  kit.add(M.awning, new THREE.CylinderGeometry(0.13, 0.13, 3.3, 10), m4(0, 5.02, 0.4, 0, 0, Math.PI / 2));
  for (let k = -3; k <= 3; k++) kit.add(M.hide, new THREE.TorusGeometry(0.14, 0.012, 4, 10), m4(k * 0.45, 5.02, 0.4, Math.PI / 2));
  // 船尾两舷各一支舵桨
  for (const sgn of [-1, 1]) {
    const a = new THREE.Vector3(sgn * 0.75, 0.75, -3.6), b = new THREE.Vector3(sgn * 1.05, -0.55, -4.75);
    const dir = b.clone().sub(a);
    const helm = new THREE.CylinderGeometry(0.04, 0.04, dir.length(), 6);
    helm.applyQuaternion(new THREE.Quaternion().setFromUnitVectors(new THREE.Vector3(0, 1, 0), dir.clone().normalize()));
    helm.translate((a.x + b.x) / 2, (a.y + b.y) / 2, (a.z + b.z) / 2);
    kit.add(M.darkWood, helm);
    kit.box(M.darkWood, 0.06, 0.55, 0.32, b.x, b.y + 0.15, b.z - 0.1, 0, 0.5);
  }
  const group = kit.build();
  group.userData.height = 6;
  return group;
}

// ------------------------------------------------------------------
// 广场：委托板（一块立着的大理石碑，带小山花，碑面上钉着三张建议）、日晷与传令官的讲台、两条石凳、一尊方柱头像。
// 委托板的三张纸条在城里不写字（文字画在叠加层上）；有几条建议就钉几张。
// ------------------------------------------------------------------
/** 辩台（广场局部坐标）：讲台正前方 2.6 米处，长 3.4 米，沿 x 放，两端各站一位辩手。 */
export const AGORA_DAIS = Object.freeze({ x: -2.0, z: -0.95, len: 3.4, depth: 1.2, h: 0.22 });

export function buildAgora(M) {
  const kit = new Kit();
  kit.box(M.marble, 2.2, 0.25, 1.0, 0, 0.125, -4.2);
  kit.box(M.marble, 1.8, 0.25, 0.8, 0, 0.375, -4.2);
  kit.box(M.marble, 1.3, 2.6, 0.28, 0, 1.8, -4.2);
  kit.add(M.marble, gable(1.5, 0.45, 0.32), m4(0, 3.1, -4.2));
  for (const sx of [-1, 1]) {
    kit.box(M.marble, 2.4, 0.18, 0.6, sx * 4.2, 0.5, 1.5);
    for (const lx of [-0.9, 0.9]) kit.box(M.marble, 0.25, 0.42, 0.5, sx * 4.2 + lx, 0.21, 1.5);
  }
  kit.box(M.marble, 0.42, 1.5, 0.42, 4.6, 0.75, -3.6);
  kit.add(M.marble, new THREE.SphereGeometry(0.26, 16, 12), m4(4.6, 1.75, -3.6, 0, 0, 0, 0.9, 1.15, 0.95));
  // 日晷：一根短柱上一块平放的圆晷盘，北半边刻着一道道放射的时辰线，正中立着一片三角形的铜晷针；
  // 旁边是传令官站的两级讲台（bema）。M2 美术审查：第一版是柱头上一块方石里挖出的半球（skaphe），
  // 从斜上方看是白盘子上一道黑色的月牙，读成一张脸，不像日晷
  const dial = new THREE.Vector3(-4.6, 1.08, -3.4);
  kit.cylinder(M.marble, 0.2, 0.26, 0.98, dial.x, 0.49, dial.z, 14);
  kit.cylinder(M.marble, 0.46, 0.42, 0.1, dial.x, dial.y, dial.z, 28);
  for (let k = 0; k <= 8; k++) {
    const a = Math.PI + (k / 8) * Math.PI;
    kit.box(M.dark, 0.012, 0.012, 0.3, dial.x + Math.cos(a) * 0.24, dial.y + 0.056, dial.z + Math.sin(a) * 0.24, Math.PI / 2 - a);
  }
  const gnomon = new THREE.Shape();
  gnomon.moveTo(0, 0); gnomon.lineTo(0.38, 0); gnomon.lineTo(0.38, 0.24); gnomon.closePath();
  kit.add(M.bronze, new THREE.ExtrudeGeometry(gnomon, { depth: 0.02, bevelEnabled: false }), m4(dial.x - 0.01, dial.y + 0.05, dial.z + 0.19, Math.PI / 2));
  kit.box(M.marble, 1.6, 0.22, 1.2, -4.4, 0.11, -1.9);
  kit.box(M.marble, 1.1, 0.22, 0.8, -4.4, 0.33, -2.05);
  // 辩台（2026-10-13，用户：日晷前的讲台增加一个，辩论议题定下时两方选手站上去）：讲台正前方一座低矮的长条石台，
  // 你和雅典娜各站一端、面对面，传令官仍在他的讲台上主持。0.22 米高——低于挡路的身体那一段（0.35–1.6 米），是地面不是障碍，
  // 广场上的路照样从上面走
  kit.box(M.marble, AGORA_DAIS.len, AGORA_DAIS.h, AGORA_DAIS.depth, AGORA_DAIS.x, AGORA_DAIS.h / 2, AGORA_DAIS.z);
  kit.box(M.marble, AGORA_DAIS.len + 0.08, 0.04, AGORA_DAIS.depth + 0.08, AGORA_DAIS.x, AGORA_DAIS.h - 0.02, AGORA_DAIS.z);
  const notices = [];
  for (let i = 0; i < 3; i++) {
    const sheet = new THREE.Mesh(new THREE.PlaneGeometry(0.34, 0.44), M.papyrus);
    sheet.position.set(-0.38 + i * 0.38, 2.15 - (i % 2) * 0.12, -4.05);
    sheet.rotation.z = (i - 1) * 0.06;
    sheet.castShadow = true;
    sheet.visible = false;
    kit.object(sheet);
    notices.push(sheet);
  }
  const group = kit.build();
  group.userData.height = 3.6;
  group.userData.notices = notices;
  group.userData.board = new THREE.Vector3(0, 1.8, -4.2);
  group.userData.sundial = new THREE.Vector3(-4.6, 1.2, -3.4);
  group.userData.bema = new THREE.Vector3(-4.4, 0.44, -2.05);
  group.userData.dais = { x: AGORA_DAIS.x, z: AGORA_DAIS.z, len: AGORA_DAIS.len, y: AGORA_DAIS.h };
  // 讲台的两级是地面（world.groundAt）：传令官站在上一级的台面上，不是站在街面上、腿陷进石头 0.43 米
  // （M3 第四轮，用户：他出门、回来都从讲台的侧面穿过去）；你踩上下一级的前沿也落在 0.22 米的台面上
  group.userData.floors = [
    { minX: -4.4 - 0.8, maxX: -4.4 + 0.8, minZ: -1.9 - 0.6, maxZ: -1.9 + 0.6, y: 0.22 },
    { minX: -4.4 - 0.55, maxX: -4.4 + 0.55, minZ: -2.05 - 0.4, maxZ: -2.05 + 0.4, y: 0.44 },
    { minX: AGORA_DAIS.x - AGORA_DAIS.len / 2, maxX: AGORA_DAIS.x + AGORA_DAIS.len / 2, minZ: AGORA_DAIS.z - AGORA_DAIS.depth / 2, maxZ: AGORA_DAIS.z + AGORA_DAIS.depth / 2, y: AGORA_DAIS.h },
  ];
  return group;
}

// ------------------------------------------------------------------
// M2：港前的石台与场馆
// ------------------------------------------------------------------

/**
 * 伸进海里的石台：地块那么大，台面与城里的地面一样高，四边是石砌的护岸，南边临水的一侧留几级台阶下到水面，
 * 东西两侧是连到码头大街的步道。quayLocalZ 是码头大街在这块地的局部坐标里的位置（石台的北边接着它）。
 */
export function buildTerrace(M, { surface = 'pave' } = {}) {
  const kit = new Kit();
  const S = PLOT_SIZE;
  kit.box(M.stone, S + 0.4, 1.4, S, 0, -0.7, 0);
  kit.add(surface === 'earth' ? M.earth : M.pave, groundPlane(S, S - 0.2, 4), m4(0, 0.015, 0.1));
  // 临水一侧：一道矮石沿，中间留出台阶。M3：0.42 米的护墙齐膝、挡路，加上人的半径把两侧 0.65 米的步道吃掉一半；
  // 改成 0.3 米的石沿（码头边系缆的那种），迈得过去、不挡路——海本来就不是地（layout.floor）
  for (const sx of [-1, 1]) kit.box(M.stone, S / 2 - 1.6, 0.3, 0.32, sx * (S / 4 + 0.8), 0.15, S / 2 - 0.16);
  for (let k = 0; k < 3; k++) kit.box(M.stone, 2.6, 0.22, 0.42, 0, -0.11 - k * 0.22, S / 2 + 0.2 + k * 0.42);
  for (const sx of [-1, 1]) kit.box(M.stone, 0.32, 0.3, S - 1.2, sx * (S / 2 + 0.04), 0.15, 0.4);
  // 系船的石柱（两根）
  for (const sx of [-1, 1]) kit.cylinder(M.stone, 0.14, 0.18, 0.5, sx * 1.8, 0.25, S / 2 - 0.2, 8);
  return kit.build();
}

/** 一排排底座（12 个），摆藏品用；底座本身是器物（props.buildPedestal），这里只给每个位次的局部坐标。 */
export function venueSlots() {
  return Array.from({ length: 12 }, (_, i) => venueSlotLocal(i));
}

/**
 * 彩绘柱廊（Stoa Poikile）：雅典广场北边真有一座专门挂画的柱廊（设计稿 7.2）。比文件夹的柱廊书库更高、更长，
 * 双坡瓦顶（两端是山墙，不是单坡），后墙上挂着一排带框的画板；正面一排多立克柱。画放在前面台地的底座上。
 */
export function buildPaintedStoa(M) {
  const kit = new Kit();
  // M3：11.2 米宽时台基的第二级（0.44 米高，齐膝）伸到 ±5.85，加上人的半径把石台两侧的步道整个占掉——雅典娜沿步道走上石台的路
  // 正好穿过它。收到 10 米：仍是八根柱、山花顶，比书库和金库都宽
  const W = 10.0, D = 4.4, H = 3.6, zc = -PLOT_SIZE / 2 + D / 2 + 0.5;
  const y0 = podium(kit, M.marble, W, D, { steps: 2, stepH: 0.22, stepT: 0.25, z: zc });
  kit.box(M.wall, W, H, 0.45, 0, y0 + H / 2, zc - D / 2 + 0.22);
  for (const sx of [-1, 1]) kit.box(M.wall, 0.45, H, D - 0.4, sx * (W / 2 - 0.22), y0 + H / 2, zc - 0.2);
  // 后墙上的画板：深色木框里一块浅色的板，板上几笔深色的剪影（远看是一排挂着的画）
  for (let i = 0; i < 6; i++) {
    const x = -W / 2 + 1.25 + i * ((W - 2.5) / 5);
    kit.box(M.darkWood, 1.3, 1.0, 0.08, x, y0 + 1.9, zc - D / 2 + 0.5);
    kit.box(M.papyrus, 1.12, 0.82, 0.02, x, y0 + 1.9, zc - D / 2 + 0.55);
    kit.box(M.glaze, 0.16, 0.42, 0.02, x - 0.18 + (i % 3) * 0.12, y0 + 1.84, zc - D / 2 + 0.57);
    kit.add(M.glaze, new THREE.SphereGeometry(0.08, 8, 6), m4(x - 0.18 + (i % 3) * 0.12, y0 + 2.12, zc - D / 2 + 0.57, 0, 0, 0, 1, 1, 0.2));
  }
  colonnade(kit, M.marble, { n: 8, x0: -W / 2 + 0.5, x1: W / 2 - 0.5, y: y0, z: zc + D / 2 - 0.45, height: H, order: 'doric', key: 'poikile' });
  const top = y0 + H;
  kit.box(M.marble, W + 0.3, 0.5, D + 0.3, 0, top + 0.25, zc);
  const tri = () => triglyphGeometry(0.36, 0.5);
  for (let i = 0; i < 11; i++) kit.instance('poikile-tri', tri, M.marble, m4(-W / 2 + 0.4 + i * ((W - 0.8) / 10), top + 0.25, zc + D / 2 + 0.2));
  gabledRoof(kit, M.tile, M.marble, W + 0.3, D + 0.3, 1.1, top + 0.5, zc, 0.35);
  for (const sx of [-1, 1]) kit.add(M.marble, palmette(0.7, 0.5), m4(sx * (W / 2 + 0.4), top + 1.6, zc, Math.PI / 2));
  const group = kit.build();
  group.userData.height = top + 2.2;
  return group;
}

/**
 * 书库：一座方正的厅，正面四根爱奥尼亚柱撑着门廊，上面一层开着小方窗的阁楼，顶是四坡的攒尖瓦顶——
 * 和文件夹的金库（两根柱、陡的三角山花）从剪影上分开。门廊两侧各立一只卷轴筒。
 */
export function buildArchive(M) {
  const kit = new Kit();
  const W = 8.2, D = 4.6, H = 3.4, zc = -PLOT_SIZE / 2 + D / 2 + 0.6;
  const y0 = podium(kit, M.marble, W, D, { steps: 3, stepH: 0.2, stepT: 0.22, z: zc });
  kit.box(M.wall, W - 0.6, H, D - 1.6, 0, y0 + H / 2, zc - 0.8);
  door(kit, M, 1.5, 2.6, 0, y0, zc + D / 2 - 1.6 - 0.02, { lintel: M.marble, lantern: 'both' });
  for (const sx of [-1, 1]) {
    window_(kit, M, 0.7, 0.9, sx * 2.4, y0 + 1.9, zc + D / 2 - 1.62);
  }
  colonnade(kit, M.marble, { n: 4, x0: -W / 2 + 0.7, x1: W / 2 - 0.7, y: y0, z: zc + D / 2 - 0.45, height: H, order: 'ionic', key: 'archive' });
  const top = y0 + H;
  kit.box(M.marble, W + 0.2, 0.45, D + 0.2, 0, top + 0.22, zc);
  // 阁楼：缩进一圈，一排小方窗
  kit.box(M.wall, W - 1.2, 1.2, D - 1.0, 0, top + 1.05, zc - 0.2);
  for (let i = 0; i < 5; i++) window_(kit, M, 0.42, 0.42, -2.2 + i * 1.1, top + 1.1, zc - 0.2 + (D - 1.0) / 2);
  // 四坡攒尖顶：底面压成长方形的四棱锥
  kit.add(M.tile, new THREE.ConeGeometry(1, 1, 4, 1).rotateY(Math.PI / 4), m4(0, top + 1.65 + 0.7, zc - 0.2, 0, 0, 0, (W - 0.6) * 0.72, 1.4, (D - 0.4) * 0.72));
  kit.add(M.marble, palmette(0.6, 0.45), m4(0, top + 3.0, zc - 0.2));
  for (const sx of [-1, 1]) {
    kit.add(M.darkWood, new THREE.CylinderGeometry(0.2, 0.2, 0.42, 14), m4(sx * 1.4, y0 + 0.21, zc + D / 2 - 0.9));
    for (let k = 0; k < 4; k++) kit.cylinder(M.papyrus, 0.045, 0.045, 0.5, sx * 1.4 + Math.cos(k * 1.6) * 0.09, y0 + 0.36, zc + D / 2 - 0.9 + Math.sin(k * 1.6) * 0.09, 8);
  }
  const group = kit.build();
  group.userData.height = top + 3.6;
  return group;
}

/**
 * 金库（城邦的）：一座厚重的石砌方屋，墙是一层层错缝的条石，平顶一圈女儿墙，正面一对包铜的大门、门上一个小山花。
 * 文件夹的金库是大理石的小神殿；这一座是灰石的、方的、没有柱子——收账册的地方。
 */
export function buildVault(M) {
  const kit = new Kit();
  const W = 7.6, D = 5.0, H = 3.8, zc = -PLOT_SIZE / 2 + D / 2 + 0.6;
  const y0 = podium(kit, M.stone, W, D, { steps: 2, stepH: 0.24, stepT: 0.25, z: zc });
  // 错缝的条石：每一层比上一层错半块，层与层之间一道细缝
  const courses = 7;
  for (let c = 0; c < courses; c++) {
    const y = y0 + (c + 0.5) * (H / courses);
    const inset = c % 2 === 0 ? 0 : 0.04;
    kit.box(M.vaultStone, W - inset, H / courses - 0.05, D - inset, 0, y, zc);
  }
  door(kit, M, 2.0, 2.7, 0, y0, zc + D / 2, { lintel: M.stone, frame: M.stone, lantern: 'both' });
  for (const sx of [-1, 1]) {
    kit.box(M.bronze, 0.92, 2.6, 0.06, sx * 0.48, y0 + 1.3, zc + D / 2 + 0.06);
    for (let k = 0; k < 4; k++) for (let j = 0; j < 3; j++) kit.add(M.bronze, new THREE.SphereGeometry(0.04, 6, 4), m4(sx * (0.2 + j * 0.28), y0 + 0.5 + k * 0.6, zc + D / 2 + 0.1));
  }
  kit.add(M.stone, gable(2.8, 0.7, 0.3), m4(0, y0 + 2.86, zc + D / 2 + 0.1));
  flatRoof(kit, M.stone, M.vaultStone, W, D, y0 + H, zc);
  const group = kit.build();
  group.userData.height = y0 + H + 1.6;
  return group;
}

/** 橄榄林的台地标：第一块台地上一根方柱头像（名牌挂在它上面）。树由 world.mjs 按委托实例化。 */
export function buildGroveMarker(M) {
  const kit = new Kit();
  kit.box(M.marble, 0.4, 1.3, 0.4, 0, 0.65, 0);
  kit.add(M.marble, new THREE.SphereGeometry(0.22, 14, 10), m4(0, 1.5, 0, 0, 0, 0, 0.9, 1.1, 0.95));
  const group = kit.build();
  group.userData.height = 2.2;
  return group;
}

export { deg, doricColumn, column, frontGabledRoof, TERRACE };
